namespace CrawlerSuite.Core.Findings
{
	/// <summary>Where a site's snapshots are: &lt;BaseDirectory&gt;\&lt;site folder&gt;\&lt;timestamp&gt;.</summary>
	public static class SnapshotLocator
	{
		/// <summary>The folder name a crawl uses for a site URL ("https://a.test" → "https___a_test").</summary>
		public static string FolderName(string url) => url.Replace(":", "_").Replace("/", "_").Replace(".", "_");

		/// <summary>The configured download folder, resolved like a crawl resolves it.</summary>
		public static string BaseFolder(Config config) => AppPaths.Resolve(config.BaseDirectory);

		/// <summary>The folder holding a site's snapshots (it may not exist yet).</summary>
		public static string SiteFolder(Config config, string siteUrl) => Path.Combine(BaseFolder(config), FolderName(siteUrl));

		/// <summary>The site's newest snapshot folder, or null when it has none.</summary>
		public static string? Latest(string siteFolder) => SnapshotFlows.MostRecentSnapshot(siteFolder)?.FullName;
	}
}
