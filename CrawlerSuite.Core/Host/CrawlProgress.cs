namespace CrawlerSuite.Core.Host
{
	using System;

	/// <summary>Which download phase a progress report belongs to.</summary>
	public enum DownloadPhase
	{
		/// <summary>Link discovery from the start URL. The total is not known in advance.</summary>
		MainCrawl,
		/// <summary>The extra download of URLs from log 05. The total is known.</summary>
		ExtraDownload,
	}

	/// <summary>
	/// A snapshot of the crawl's live counters. Immutable; the engine produces a new one
	/// whenever it reports. Counts are cumulative for the current download phase except
	/// <see cref="DownloadedBytes"/>, which is cumulative for the whole crawl (it is what
	/// the total download limit is measured against).
	/// </summary>
	public sealed record CrawlProgress
	{
		public DownloadPhase Phase { get; init; }

		/// <summary>Pages saved (rows with status "saved").</summary>
		public int PagesSaved { get; init; }

		/// <summary>Assets saved (rows with status "savedAsset").</summary>
		public int AssetsSaved { get; init; }

		/// <summary>URLs accepted for download but not started yet.</summary>
		public int Queued { get; init; }

		/// <summary>Downloads currently transferring.</summary>
		public int Active { get; init; }

		/// <summary>Downloads aborted because no data arrived within the stall timeout.</summary>
		public int Stalled { get; init; }

		/// <summary>Failed fetches (HTTP errors, connection failures).</summary>
		public int Errors { get; init; }

		/// <summary>Of <see cref="Errors"/>: not found (404, 410) — the site's broken links.</summary>
		public int NotFound { get; init; }

		/// <summary>Of <see cref="Errors"/>: other refusals (4xx: 401, 403 …).</summary>
		public int ClientErrors { get; init; }

		/// <summary>Of <see cref="Errors"/>: server errors (5xx), after the retry round.</summary>
		public int ServerErrors { get; init; }

		/// <summary>Of <see cref="Errors"/>: no answer at all (stalled, refused, TLS, timed out), after the retry round.</summary>
		public int ConnectionErrors { get; init; }

		/// <summary>Redirect responses seen.</summary>
		public int Redirects { get; init; }

		/// <summary>Files over the single-file limit (not stored; sample kept).</summary>
		public int Oversized { get; init; }

		/// <summary>Bytes written to the download folder during this crawl.</summary>
		public long DownloadedBytes { get; init; }

		/// <summary>Recent transfer rate in bytes per second.</summary>
		public double BytesPerSecond { get; init; }

		/// <summary>Time since the crawl started.</summary>
		public TimeSpan Elapsed { get; init; }

		/// <summary>The configured total download limit in bytes (0 = no limit).</summary>
		public long LimitBytes { get; init; }

		/// <summary>Extra download only: URLs in the list.</summary>
		public int ListTotal { get; init; }

		/// <summary>Extra download only: list URLs finished (saved, failed or skipped).</summary>
		public int ListDone { get; init; }

		/// <summary>Main crawl only: the previous completed crawl of the same site, if any.</summary>
		public PreviousCrawl? Previous { get; init; }

		/// <summary>Downloads that failed for a passing reason (stall, no answer, connection, 429, 5xx) and wait for the retry round.</summary>
		public int ToRetry { get; init; }

		/// <summary>Downloads the retry round saved after all (this phase).</summary>
		public int Recovered { get; init; }

		/// <summary>The retry round of the phase is running.</summary>
		public bool RetryRound { get; init; }

		/// <summary>New requests to a server are held because it stopped answering properly: for how much longer; null when none is held.</summary>
		public TimeSpan? PausedFor { get; init; }

		/// <summary>How often requests were paused so far (the whole crawl).</summary>
		public int Pauses { get; init; }

		/// <summary>Pages plus assets saved — the figure compared with the previous crawl.</summary>
		public int FilesSaved => PagesSaved + AssetsSaved;
	}

	/// <summary>
	/// Facts about the last completed crawl of the same site, read from its 01-crawler.log.
	/// Used as the expected size of the current crawl.
	/// </summary>
	public sealed record PreviousCrawl(string SnapshotName, int FileCount, TimeSpan? Duration);
}
