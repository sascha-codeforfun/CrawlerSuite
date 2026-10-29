namespace CrawlerSuite.Core
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Text;
	using CrawlerSuite.Core.Host;

	/// <summary>
	/// Builds what the operator sees when a crawl stops at the total download limit: the
	/// largest files and the URL folders that take the most space. Downloads are stored flat
	/// under hashed names, so the URL of each file comes from the raw crawl log (00).
	/// </summary>
	internal static class CrawlLimitReportBuilder
	{
		internal const int TopCount = 10;

		internal static CrawlLimitReport Build(string saveDirectory, string rawLogPath, long limitBytes, long downloadedBytes)
		{
			var urlByFile = ReadSavedFiles(rawLogPath);
			var downloadDir = Path.Combine(saveDirectory, "download");

			var files = new List<(string Name, string Url, long Bytes)>();
			if (Directory.Exists(downloadDir))
			{
				foreach (var path in CrawlerSuite.Core.Host.FolderFiles.Sorted(downloadDir, "*"))
				{
					var name = Path.GetFileName(path);
					if (name.EndsWith(HeaderSidecar.HeaderSidecarExtension, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					long length;
					try
					{
						length = new FileInfo(path).Length;
					}
					catch (IOException)
					{
						continue;
					}

					urlByFile.TryGetValue(name, out var url);
					files.Add((name, url ?? "(no URL recorded)", length));
				}
			}

			var largestFiles = files
				.OrderByDescending(f => f.Bytes)
				.Take(TopCount)
				.ToList();

			var largestFolders = files
				.GroupBy(f => FolderOf(f.Url), StringComparer.OrdinalIgnoreCase)
				.Select(g => (Folder: g.Key, Bytes: g.Sum(f => f.Bytes), Files: g.Count()))
				.OrderByDescending(g => g.Bytes)
				.Take(TopCount)
				.ToList();

			return new CrawlLimitReport(saveDirectory, limitBytes, downloadedBytes, files.Count, largestFiles, largestFolders);
		}

		/// <summary>The first two path segments of a URL ("/en/products/"), the grouping unit.</summary>
		internal static string FolderOf(string url)
		{
			if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			{
				return "(unknown)";
			}

			var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
			// The last segment is the file itself; group by up to two folders above it.
			var folders = segments.Take(Math.Max(0, Math.Min(2, segments.Length - 1))).ToArray();
			return folders.Length == 0 ? "/" : "/" + string.Join('/', folders) + "/";
		}

		private static Dictionary<string, string> ReadSavedFiles(string rawLogPath)
		{
			var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (!File.Exists(rawLogPath))
			{
				return map;
			}

			try
			{
				foreach (var line in File.ReadLines(rawLogPath, Encoding.UTF8))
				{
					var parts = line.Split('|', StringSplitOptions.TrimEntries);
					if (parts.Length >= 4
						&& (parts[2].Equals("saved", StringComparison.OrdinalIgnoreCase)
							|| parts[2].Equals("savedAsset", StringComparison.OrdinalIgnoreCase)))
					{
						map[parts[3]] = parts[1];
					}
				}
			}
			catch (IOException)
			{
			}

			return map;
		}
	}
}
