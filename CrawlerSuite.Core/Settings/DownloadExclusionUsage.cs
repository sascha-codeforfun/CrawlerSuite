namespace CrawlerSuite.Core.Settings
{
	using System.Collections.Concurrent;
	using CrawlerSuite.Core.Host;

	/// <summary>
	/// How much each download exclusion was needed in a crawl: for the main crawl and the extra
	/// download apart, how many different addresses it kept out and how often one came up. Every
	/// entry whose text an address contains is counted, also when an earlier entry already
	/// rejected it (an entry behind a broader one still shows what it would keep out) and also
	/// when it is switched off (counted, not applied — whether it would still hit). Counted where
	/// the crawl decides on an address (<see cref="Urls.Validity.IsInDownloadScope"/>), after the
	/// host check: an address of another host was never the exclusion's to keep out. Written into
	/// <see cref="SettingsUsage.FileName"/> at the end of a crawl; a replay crawls nothing and
	/// leaves the counts of the crawl it replays.
	/// </summary>
	public sealed class DownloadExclusionUsage
	{
		/// <summary>The main crawl's counts in the usage file: per entry text, [addresses, times].</summary>
		public const string MainCrawl = "DownloadExclusions";

		/// <summary>The extra download's counts (the addresses of list 05 and what their pages link to).</summary>
		public const string ExtraDownload = "DownloadExclusions.ExtraDownload";

		private sealed class Tally
		{
			public readonly ConcurrentDictionary<string, byte> Addresses = new(StringComparer.OrdinalIgnoreCase);
			public int Times;
		}

		private readonly ConcurrentDictionary<(string Entry, DownloadPhase Phase), Tally> tallies = new();

		/// <param name="entries">Every entry of the configuration: each is in the file, also one that matched nothing (that is the point).</param>
		public DownloadExclusionUsage(IEnumerable<CrawlLinkExclusion> entries)
		{
			foreach (var entry in entries.Where(e => e is not null && !string.IsNullOrEmpty(e.Value)))
			{
				tallies.TryAdd((KeyOf(entry), DownloadPhase.MainCrawl), new Tally());
				tallies.TryAdd((KeyOf(entry), DownloadPhase.ExtraDownload), new Tally());
			}
		}

		/// <summary>Counts one address against every entry it contains (thread-safe; the crawl decides in parallel).</summary>
		public void Count(string address, IReadOnlyList<CrawlLinkExclusion>? entries, DownloadPhase phase)
		{
			if (entries is null || string.IsNullOrEmpty(address))
			{
				return;
			}

			foreach (var entry in entries)
			{
				if (entry is null || string.IsNullOrEmpty(entry.Value) || !address.Contains(entry.Value, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var tally = tallies.GetOrAdd((KeyOf(entry), phase), _ => new Tally());
				tally.Addresses.TryAdd(address, 0);
				Interlocked.Increment(ref tally.Times);
			}
		}

		/// <summary>An entry's key in the usage file: its text, trimmed (the settings screen looks it up so).</summary>
		public static string KeyOf(CrawlLinkExclusion entry) => entry.Value.Trim();

		/// <summary>The counts of one phase: per entry text, [different addresses, times].</summary>
		public IReadOnlyDictionary<string, UsageCount> Of(DownloadPhase phase) =>
			tallies.Where(t => t.Key.Phase == phase)
				.ToDictionary(t => t.Key.Entry, t => new UsageCount(t.Value.Addresses.Count, t.Value.Times), StringComparer.Ordinal);

		/// <summary>
		/// Writes both phases into the snapshot's crawl usage file (<see cref="SettingsUsage.CrawlFileName"/>),
		/// which a re-run keeps — only a crawl can count these.
		/// </summary>
		public void Write(string snapshotDirectory) =>
			SettingsUsage.Merge(snapshotDirectory, new Dictionary<string, IReadOnlyDictionary<string, UsageCount>>
			{
				[MainCrawl] = Of(DownloadPhase.MainCrawl),
				[ExtraDownload] = Of(DownloadPhase.ExtraDownload),
			}, fileName: SettingsUsage.CrawlFileName);
	}
}
