namespace CrawlerSuite.Core.Formats
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Text;

	/// <summary>What a snapshot holds for one output file.</summary>
	public sealed record OutputFileStatus(
		OutputFormat Format,
		string Path,
		bool Exists,
		long Bytes,
		int? Rows,
		string Explanation);

	/// <summary>
	/// Reads which output files a snapshot contains, their size and row counts. Read-only.
	/// A row is a data line: blank lines, comment lines ('#', '//') and the header row are not
	/// counted. Files whose records span several lines (free-text blocks) report no row count.
	/// </summary>
	public static class SnapshotSummary
	{
		public static IReadOnlyList<OutputFileStatus> Read(string snapshotDirectory)
		{
			var result = new List<OutputFileStatus>();
			foreach (var format in OutputFormats.All)
			{
				result.Add(ReadOne(snapshotDirectory, format));
			}

			return result;
		}

		private static OutputFileStatus ReadOne(string snapshotDirectory, OutputFormat format)
		{
			var name = format.PrimaryFileName.TrimEnd('/');
			var directory = name.Equals("connectivity.log", StringComparison.OrdinalIgnoreCase)
				? System.IO.Path.GetDirectoryName(snapshotDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? snapshotDirectory
				: snapshotDirectory;
			var path = System.IO.Path.Combine(directory, name);

			try
			{
				if (format.IsFolder)
				{
					if (!Directory.Exists(path))
					{
						return new OutputFileStatus(format, path, false, 0, null, NotPresent(format));
					}

					var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();
					long bytes = files.Sum(f => new FileInfo(f).Length);
					return new OutputFileStatus(format, path, true, bytes, files.Count, $"{files.Count:N0} file(s)");
				}

				if (!File.Exists(path))
				{
					return new OutputFileStatus(format, path, false, 0, null, NotPresent(format));
				}

				var length = new FileInfo(path).Length;
				int? rows = IsLineRecords(format) ? CountRows(path, format.HeaderRow) : null;
				var explanation = rows switch
				{
					null => "records span several lines",
					0 => "no rows",
					_ => $"{rows:N0} row(s)",
				};
				return new OutputFileStatus(format, path, true, length, rows, explanation);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return new OutputFileStatus(format, path, false, 0, null, $"could not be read: {ex.Message}");
			}
		}

		private static string NotPresent(OutputFormat format) =>
			"not in this snapshot — written " + LowerFirst(format.WrittenWhen);

		private static string LowerFirst(string s) =>
			string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];

		/// <summary>True when every record is a single line (tables, lists, the crawl logs).</summary>
		internal static bool IsLineRecords(OutputFormat format)
		{
			var d = format.Delimiter.ToLowerInvariant();
			return !(d.Contains("multi-line") || d.StartsWith("free text", StringComparison.Ordinal) || d.StartsWith("xml", StringComparison.Ordinal));
		}

		private static int CountRows(string path, bool headerRow)
		{
			int rows = 0;
			bool first = true;
			foreach (var raw in File.ReadLines(path, Encoding.UTF8))
			{
				var line = raw.Trim();
				if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
				{
					continue;
				}

				if (first && headerRow)
				{
					first = false;
					continue;
				}

				first = false;
				rows++;
			}

			return rows;
		}
	}
}
