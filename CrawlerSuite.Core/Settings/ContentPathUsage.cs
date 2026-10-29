namespace CrawlerSuite.Core.Settings
{
	/// <summary>
	/// How much each content-list path to leave out (<see cref="CmsContentListConfig.RowsToExclude"/>)
	/// was needed: the rows of the export it matched, counted where the export is read
	/// (<see cref="Content.Listing"/>) — by a crawl and by a re-run alike, which reads the export
	/// again without a request to the site. The numbers describe the export as that run read it.
	/// </summary>
	public static class ContentPathUsage
	{
		/// <summary>Per entry (trimmed): [rows matched, rows matched].</summary>
		public const string Setting = "CmsContentList.RowsToExclude";

		/// <summary>The rows read, under <see cref="RowsKey"/>: [rows, rows] — what the share is taken of.</summary>
		public const string ReadSetting = "CmsContentList.RowsToExclude.Read";

		public const string RowsKey = "rows";

		/// <summary>Writes a listing's counts into the snapshot's usage file, keeping every other setting's.</summary>
		public static void Write(string snapshotDirectory, Content.ListingResult listing) =>
			SettingsUsage.Merge(snapshotDirectory, new Dictionary<string, IReadOnlyDictionary<string, UsageCount>>
			{
				[Setting] = listing.PathsLeftOut.ToDictionary(p => p.Key, p => new UsageCount(p.Value, p.Value), StringComparer.Ordinal),
				[ReadSetting] = new Dictionary<string, UsageCount> { [RowsKey] = new(listing.RowsRead, listing.RowsRead) },
			});

		/// <summary>The rows the last run read, or null when it did not count them.</summary>
		public static int? RowsRead(UsageReport report) => report.Of(ReadSetting, RowsKey)?.Pages;
	}
}
