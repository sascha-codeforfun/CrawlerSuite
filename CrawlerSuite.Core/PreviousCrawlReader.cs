namespace CrawlerSuite.Core
{
	using System;
	using System.IO;
	using System.Linq;
	using System.Text;
	using CrawlerSuite.Core.Host;
	using CrawlerSuite.Core.Snapshots;

	/// <summary>
	/// Reads the last completed crawl of a site, to serve as the expected size of the crawl
	/// that is starting. Only the main crawl is compared: files saved by link discovery and
	/// the time from the start URL's first fetch to the first "completed" marker.
	/// Read-only; changes nothing on disk.
	/// </summary>
	internal static class PreviousCrawlReader
	{
		/// <summary>
		/// The newest completed snapshot next to <paramref name="currentSaveDirectory"/>
		/// (excluding it), or null when there is none.
		/// </summary>
		internal static PreviousCrawl? Read(string currentSaveDirectory, string baseUrl)
		{
			try
			{
				var parent = Path.GetDirectoryName(currentSaveDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
				if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
				{
					return null;
				}

				var currentName = Path.GetFileName(currentSaveDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
				var candidates = Directory.EnumerateDirectories(parent)
					.Select(d => new DirectoryInfo(d))
					.Where(d => SnapshotFolder.Matches(d.Name)
						&& !d.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase))
					.OrderByDescending(d => d.CreationTimeUtc);

				foreach (var snapshot in candidates)
				{
					var log = Path.Combine(snapshot.FullName, LogFileNames.Crawler);
					var (isComplete, _, _) = CrawlLog.Analyse(log, baseUrl);
					if (!isComplete)
					{
						continue;
					}

					return FromLog(snapshot.Name, log, baseUrl);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Logger.LogWarning($"Previous crawl: could not read the site folder — {ex.Message}");
			}

			return null;
		}

		/// <summary>Counts and times the main crawl recorded in one 01-crawler.log.</summary>
		internal static PreviousCrawl FromLog(string snapshotName, string crawlerLogPath, string baseUrl)
		{
			int files = 0;
			DateTime? start = null;
			DateTime? end = null;

			foreach (var line in File.ReadLines(crawlerLogPath, Encoding.UTF8))
			{
				var parts = line.Split('|', StringSplitOptions.TrimEntries);
				if (parts.Length < 3)
				{
					continue;
				}

				var status = parts[2];
				if (start is null
					&& status.Equals("crawled", StringComparison.OrdinalIgnoreCase)
					&& parts[1].StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
				{
					start = CrawlLog.ParseTimestamp(line);
				}

				if (end is null
					&& status.Equals("completed", StringComparison.OrdinalIgnoreCase)
					&& parts.Length >= 4
					&& parts[3].Equals("info", StringComparison.OrdinalIgnoreCase))
				{
					end = CrawlLog.ParseTimestamp(line);
				}

				bool saved = status.Equals("saved", StringComparison.OrdinalIgnoreCase)
					|| status.Equals("savedAsset", StringComparison.OrdinalIgnoreCase);
				if (saved)
				{
					var source = parts.Length >= 5 ? parts[4] : "discovery";
					if (!source.Equals("list", StringComparison.OrdinalIgnoreCase))
					{
						files++;
					}
				}
			}

			TimeSpan? duration = start.HasValue && end.HasValue && end >= start ? end - start : null;
			return new PreviousCrawl(snapshotName, files, duration);
		}
	}
}
