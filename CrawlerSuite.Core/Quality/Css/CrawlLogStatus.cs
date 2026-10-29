namespace CrawlerSuite.Core.Quality.Css
{
	using System.Text;

	/// <summary>
	/// What the crawl log records for an address: the status column of its last row ("savedAsset",
	/// "404", "N/A" …). Read once, on first question; an address not in the log answers null.
	/// </summary>
	internal static class CrawlLogStatus
	{
		public static Func<string, string?> From(string crawlLogPath)
		{
			var table = new Lazy<Dictionary<string, string>>(() => Read(crawlLogPath));
			return url => table.Value.TryGetValue(url, out var status) ? status : null;
		}

		private static Dictionary<string, string> Read(string path)
		{
			var table = new Dictionary<string, string>(StringComparer.Ordinal);
			try
			{
				if (!File.Exists(path))
				{
					return table;
				}

				foreach (var line in File.ReadLines(path, Encoding.UTF8))
				{
					var parts = line.Split('|');
					if (parts.Length > 2)
					{
						table[parts[1].Replace(":443", string.Empty, StringComparison.Ordinal).Trim()] = parts[2].Trim();
					}
				}
			}
			catch (IOException)
			{
			}

			return table;
		}
	}
}
