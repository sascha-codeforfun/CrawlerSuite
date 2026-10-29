namespace CrawlerSuite.Core
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Net.Http;
	using System.Text;
	using System.Threading.Tasks;
	using CrawlerSuite.Core.Logging;
	using CrawlerSuite.Core.Security;
	using CrawlerSuite.Core.Urls;

	/// <summary>
	/// The fetch authority. A crawl runs inside one <see cref="CrawlSession"/> — the
	/// HTTP clients, the visited sets, the concurrency limits, the host allowlist and
	/// the counters all live there and are created fresh for each run, so a second run in
	/// the same process starts clean. This static class is the entry point the
	/// orchestrator and the tests use.
	/// </summary>
	public static class Crawl
	{
		private static CrawlSession? session;

		/// <summary>The session of the running crawl, or null between crawls.</summary>
		internal static CrawlSession? Session => session;

		/// <summary>
		/// Starts a new crawl session (disposing any previous one). Everything a crawl
		/// remembers — visited URLs, counters, refusals — starts empty.
		/// </summary>
		internal static CrawlSession StartSession(CrawlSessionOptions options)
		{
			session?.Dispose();
			session = new CrawlSession(options);
			return session;
		}

		/// <summary>Ends the current session and releases its connections.</summary>
		internal static void EndSession()
		{
			session?.Dispose();
			session = null;
		}

		/// <summary>
		/// Canonicalizes a URL prior to fetch. If a configured modal query parameter appears
		/// in <paramref name="url"/>, the URL is a modal carrier and the inner URL is unwrapped
		/// (resolved against <paramref name="websiteUrl"/>); otherwise the query string is
		/// dropped. Throws <see cref="UriFormatException"/> when the URL (or the unwrapped
		/// value) is not a valid absolute URL — callers fetch inside a try and record that.
		///
		/// The result is NOT trusted: the unwrapped value comes from page content, so the
		/// caller re-admits it through <see cref="CrawlGate"/> before fetching.
		/// </summary>
		internal static string CanonicalizeUrlForFetch(
			string url,
			string websiteUrl,
			List<ModalQueryParam> modalQueryParameters)
		{
			var matchedParam = modalQueryParameters
				.FirstOrDefault(p => url.Contains(p.Param, StringComparison.OrdinalIgnoreCase));

			if (matchedParam != null)
			{
				return Query.ExtractModalUrl(url, websiteUrl, matchedParam);
			}

			return url.Split('?')[0];
		}

		/// <summary>Downloads a page and everything it leads to, within the current session.</summary>
		public static Task DownloadWebsiteAsync
		(
			string url,
			string saveDirectory,
			string log,
			string websiteUrl,
			IReadOnlyList<CrawlLinkExclusion> downloadExclusions,
			List<ModalQueryParam> modalQueryParameters,
			string source = "discovery",
			List<string>? jsonPathPrefixes = null,
			IReadOnlyList<string>? allowedSubdomains = null
		)
		{
			var current = session
				?? throw new InvalidOperationException("Crawl.StartSession() must be called before DownloadWebsiteAsync.");
			return current.DownloadPageAsync(
				url, saveDirectory, log, websiteUrl, downloadExclusions, modalQueryParameters,
				source, jsonPathPrefixes, allowedSubdomains);
		}

		/// <summary>
		/// Records a refused out-of-root write. The write itself is blocked by
		/// <see cref="PathContainmentCheck"/>; this only keeps the trail and the crawl
		/// continues (the refusal list is shown once, after all downloads).
		/// </summary>
		internal static void ReportContainmentRefusal(
			PathContainmentCheck.Verdict verdict, string sourceUrl, string captureRoot, string log)
		{
			Logger.LogDetailToFile(
				$"Path containment: blocked an out-of-root write for '{sourceUrl}' — the download " +
				$"name resolved outside the capture root '{captureRoot}' (reason: {verdict.Reason}). " +
				"No bytes were written.");

			session?.AddContainmentRefusal(sourceUrl, verdict.Reason);
		}

		/// <summary>Presents the collected out-of-root refusals once, after all download phases.</summary>
		internal static void WriteContainmentRefusalSummary()
		{
			var refusals = session?.ContainmentRefusals ?? [];
			if (refusals.Count == 0)
			{
				return;
			}

			RunUi.WriteActionBlock(
				$"Path containment: {refusals.Count} download(s) were blocked from writing outside the capture folder.",
				new[] { "These files were NOT downloaded. Review for a benign config slip vs. the site serving hostile content:" }
					.Concat(refusals.Select(r => $"{r.Url}  (reason: {r.Reason})")));
		}

		/// <summary>
		/// Writes the request/response header sidecar ("&lt;name&gt;.header") next to a body
		/// file in <paramref name="directory"/>. Best-effort: a failure is logged, never thrown.
		/// </summary>
		internal static void WriteHeaderSidecar(
			HttpResponseMessage response, string url, string directory, string bodyFileName)
		{
			try
			{
				var headerName = Path.ChangeExtension(bodyFileName,
					HeaderSidecar.HeaderSidecarExtension.TrimStart('.'));
				var headerPath = Path.Combine(directory, headerName);

				var request = response.RequestMessage;
				var method = request?.Method?.Method ?? "GET";
				var requestLine = $"{method} {url}";

				var requestHeaders = new List<(string, string)>();
				if (request is not null)
				{
					foreach (var h in request.Headers)
					{
						foreach (var v in h.Value)
						{
							requestHeaders.Add((h.Key, v));
						}
					}

					if (request.Content is not null)
					{
						foreach (var h in request.Content.Headers)
						{
							foreach (var v in h.Value)
							{
								requestHeaders.Add((h.Key, v));
							}
						}
					}
				}

				var statusLine =
					$"HTTP/{response.Version} {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();

				var responseHeaders = new List<(string, string)>();
				foreach (var h in response.Headers)
				{
					foreach (var v in h.Value)
					{
						responseHeaders.Add((h.Key, v));
					}
				}

				foreach (var h in response.Content.Headers)
				{
					foreach (var v in h.Value)
					{
						responseHeaders.Add((h.Key, v));
					}
				}

				var text = HeaderSidecar.FormatHeaderSidecar(requestLine, requestHeaders, statusLine, responseHeaders);
				FileFormat.WriteAllText(headerPath, text);
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Header sidecar: could not write for " +
					$"{Path.GetFileName(bodyFileName)} — {ex.Message}");
			}
		}
	}

	/// <summary>Settings that shape one crawl session.</summary>
	internal sealed record CrawlSessionOptions
	{
		public string? ProxyUrl { get; init; }
		public string? ProxyUser { get; init; }
		public string? ProxyPassword { get; init; }
		public int MaxConcurrentPageDownloads { get; init; } = 100;
		public int MaxConcurrentAssetDownloads { get; init; } = 200;
		public required CrawlPolicy Policy { get; init; }

		/// <summary>A single file larger than this is not stored (0 = no limit).</summary>
		public long MaxSingleFileBytes { get; init; }

		/// <summary>The crawl stops once this many bytes were downloaded (0 = no limit).</summary>
		public long MaxCrawlBytes { get; init; }

		/// <summary>Once data flows, a download with no data for this long is aborted.</summary>
		public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

		/// <summary>
		/// How long the server may take to start answering (status and headers). A busy server
		/// queues requests when a hundred pages are fetched at once; that is waiting, not a stall.
		/// 100 s is the .NET default.
		/// </summary>
		public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(100);

		/// <summary>
		/// The pause before the retry round tries again what failed for a passing reason: long
		/// enough for a server that hiccuped to be back.
		/// </summary>
		public TimeSpan RetryRoundPause { get; init; } = TimeSpan.FromSeconds(30);

		/// <summary>How a server's gate reacts to pushback; null = <see cref="ServerGate.Settings.Default"/>.</summary>
		public ServerGate.Settings? Gate { get; init; }

		/// <summary>The most requests per second to one server, pages and assets together; 0 = no ceiling.</summary>
		public double MaxRequestsPerSecond { get; init; }

		/// <summary>The most connections open to one server at a time, pages and assets together.</summary>
		public int MaxConnectionsPerServer { get; init; } = 16;

		/// <summary>Where the crawl records the addresses it connected to (null: not recorded; never through a proxy).</summary>
		public Security.DnsRecord? Dns { get; init; }

		/// <summary>Where the crawl counts what each download exclusion kept out (null: not counted).</summary>
		public Settings.DownloadExclusionUsage? ExclusionUsage { get; init; }

		/// <summary>The highest HTTP version offered; each server is spoken to in the best it agrees to, up to this.</summary>
		public Version HttpVersion { get; init; } = System.Net.HttpVersion.Version11;
	}

	/// <summary>Messages written to the crawl log that the log's substring readers must not misread.</summary>
	internal static class CrawlLogMessages
	{
		/// <summary>Status of a URL whose body exceeded the single-file limit.</summary>
		internal const string OversizedStatus = "Oversized";

		/// <summary>
		/// The message column of an <see cref="OversizedStatus"/> row. Deliberately free of
		/// the words the log readers match anywhere in a row ("saved", "completed",
		/// "crawled", "found"), so the row is ignored by all of them.
		/// </summary>
		internal static string Oversized(long limitBytes, string sampleName) =>
			$"exceeds the {limitBytes / 1_048_576} MB single-file limit, not stored; sample kept as {OversizedFiles.FolderName}/{sampleName}";

		/// <summary>The message of a download aborted by the stall timeout (status "N/A").</summary>
		internal static string Stalled(TimeSpan timeout) =>
			$"No data received for {timeout.TotalSeconds:0} s — download aborted (stall timeout)";

		/// <summary>The message of a request the server never answered, after the retries (status "N/A").</summary>
		internal static string NoAnswer(TimeSpan timeout) =>
			$"No answer within {timeout.TotalSeconds:0} s — request abandoned after retries (answer timeout)";
	}
}
