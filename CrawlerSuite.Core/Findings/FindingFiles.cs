using System.Text;

namespace CrawlerSuite.Core.Findings
{
	/// <summary>
	/// Reads the shapes the output files come in: pipe or @@@ tables, CSV pairs, tab-separated
	/// lines and one-value-per-line lists. Every reader returns the raw cells; the catalog decides
	/// which become columns. Read-only, and tolerant: a malformed line is kept as far as it goes,
	/// never thrown on.
	/// </summary>
	internal static class FindingFiles
	{
		/// <summary>
		/// Opens for reading while letting others keep the file open — Excel holding a CSV open
		/// must not stop the Results page.
		/// </summary>
		public static StreamReader Open(string path) =>
			new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

		/// <summary>The file's lines, without blank lines; BOM and either line ending handled.</summary>
		public static IEnumerable<string> Lines(string path)
		{
			using var reader = Open(path);
			while (reader.ReadLine() is { } line)
			{
				if (!string.IsNullOrWhiteSpace(line))
				{
					yield return line;
				}
			}
		}

		/// <summary>
		/// A delimited table. The last column keeps any further separators, so a value that may
		/// contain the separator only ever sits last.
		/// </summary>
		public static IEnumerable<string[]> Delimited(string path, string separator, int columns, bool header)
		{
			bool first = true;
			foreach (var line in Lines(path))
			{
				if (first && header)
				{
					first = false;
					continue;
				}

				first = false;
				// Cells written by IssueLogWriter.ComposeLine carry the formula guard: stripped here.
				yield return Pad(line.Split(separator, columns).Select(Unguard).ToArray(), columns);
			}
		}

		/// <summary>Tab-separated finding lines; '#' comment lines and "(no findings)" are skipped.</summary>
		public static IEnumerable<string[]> Tabbed(string path, int columns)
		{
			foreach (var line in Lines(path))
			{
				var trimmed = line.TrimStart();
				if (trimmed.StartsWith('#') || trimmed.StartsWith("(no findings)", StringComparison.Ordinal))
				{
					continue;
				}

				// The context cell carries the formula guard: stripped here.
				yield return Pad(line.Split('\t', columns).Select(Unguard).ToArray(), columns);
			}
		}

		/// <summary>One value per line; '#' comment lines are skipped.</summary>
		public static IEnumerable<string[]> Values(string path)
		{
			foreach (var line in Lines(path))
			{
				if (!line.TrimStart().StartsWith('#'))
				{
					yield return [line.Trim()];
				}
			}
		}

		/// <summary>
		/// The CSV of a pair (the comma file, else the semicolon file), header row skipped.
		/// Returns null when neither was written.
		/// </summary>
		public static string? PairPath(string snapshotDirectory, string commaFileName)
		{
			var comma = Path.Combine(snapshotDirectory, commaFileName);
			if (File.Exists(comma))
			{
				return comma;
			}

			var semicolon = Path.Combine(snapshotDirectory, commaFileName.Replace("_comma.csv", "_semicolon.csv", StringComparison.Ordinal));
			return File.Exists(semicolon) ? semicolon : null;
		}

		/// <summary>RFC 4180 records (quoted fields may hold the delimiter, quotes and line breaks).</summary>
		public static IEnumerable<string[]> Csv(string path, int columns, bool header)
		{
			char delimiter = path.EndsWith("_comma.csv", StringComparison.OrdinalIgnoreCase) ? ','
				: path.EndsWith("_semicolon.csv", StringComparison.OrdinalIgnoreCase) ? ';'
				: DetectDelimiter(path);

			bool first = true;
			using var reader = Open(path);
			foreach (var record in CsvRecords(reader, delimiter))
			{
				if (record.Length == 1 && record[0].Length == 0)
				{
					continue;
				}

				if (first && header)
				{
					first = false;
					continue;
				}

				first = false;
				yield return Pad(record.Select(Unguard).ToArray(), columns);
			}
		}

		/// <summary>
		/// The writers put an apostrophe before a value that a spreadsheet would run as a formula
		/// ("=", "+", "-", "@"); on screen the value is shown as it was.
		/// </summary>
		public static string Unguard(string value) =>
			value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' or '\t' or '\r' ? value[1..] : value;

		private static char DetectDelimiter(string path)
		{
			using var reader = Open(path);
			var head = reader.ReadLine() ?? string.Empty;
			return head.Count(c => c == ';') >= head.Count(c => c == ',') ? ';' : ',';
		}

		/// <summary>Streams the records: a large CSV is never held in memory as one string.</summary>
		private static IEnumerable<string[]> CsvRecords(TextReader reader, char delimiter)
		{
			var fields = new List<string>();
			var field = new StringBuilder();
			bool quoted = false;
			int read;

			while ((read = reader.Read()) >= 0)
			{
				char c = (char)read;
				if (quoted)
				{
					if (c == '"')
					{
						if (reader.Peek() == '"')
						{
							field.Append('"');
							reader.Read();
						}
						else
						{
							quoted = false;
						}
					}
					else
					{
						field.Append(c);
					}

					continue;
				}

				if (c == '"' && field.Length == 0)
				{
					quoted = true;
				}
				else if (c == delimiter)
				{
					fields.Add(field.ToString());
					field.Clear();
				}
				else if (c is '\r' or '\n')
				{
					if (c == '\r' && reader.Peek() == '\n')
					{
						reader.Read();
					}

					fields.Add(field.ToString());
					field.Clear();
					yield return [.. fields];
					fields.Clear();
				}
				else
				{
					field.Append(c);
				}
			}

			if (field.Length > 0 || fields.Count > 0)
			{
				fields.Add(field.ToString());
				yield return [.. fields];
			}
		}

		private static string[] Pad(string[] cells, int columns)
		{
			if (cells.Length >= columns)
			{
				return cells.Length == columns ? Trim(cells) : Trim(cells[..columns]);
			}

			var padded = new string[columns];
			for (int i = 0; i < columns; i++)
			{
				padded[i] = i < cells.Length ? cells[i] : string.Empty;
			}

			return Trim(padded);
		}

		private static string[] Trim(string[] cells)
		{
			for (int i = 0; i < cells.Length; i++)
			{
				cells[i] = cells[i].Trim();
			}

			return cells;
		}
	}
}
