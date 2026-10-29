namespace CrawlerSuite.Core
{
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.IO;
	using System.Linq;
	using System.Net;
	using System.Net.Http;
	using System.Net.Sockets;
	using System.Security.Authentication;
	using System.Threading;
	using System.Threading.Tasks;
	using CrawlerSuite.Core.Host;
	using CrawlerSuite.Core.Logging;
	using CrawlerSuite.Core.Security;
	using CrawlerSuite.Core.Urls;
	using HtmlAgilityPack;

	/// <summary>
	/// One crawl: the HTTP clients, what has been visited, the concurrency limits, the host
	/// allowlist, the size limits and the live counters. Created per run and disposed at the
	/// end, so nothing carries over to the next run.
	///
	/// Every body is streamed straight to disk. A download that receives no data for the
	/// stall timeout is aborted; a file over the single-file limit is not stored (its first
	/// bytes and headers are kept in <c>oversized/</c>); once the whole crawl passes the
	/// total limit the session stops every download at once.
	///
	/// A server that pushes back is not hammered: each server has a <see cref="ServerGate"/>
	/// that pauses new requests while failures cluster. A download that fails for a passing
	/// reason (stalled, no answer, connection or TLS failure, 429, 5xx) is not logged as failed
	/// at once: it is tried again in the retry round (<see cref="RetryRoundAsync"/>) at the end
	/// of the download phase, and logged as failed only if it fails there too.
	/// </summary>
	internal sealed class CrawlSession : IDisposable
	{
		private const int MaxRetries = 5;
		private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(200);
		private const int CopyBufferSize = 81920;

		private readonly CrawlSessionOptions options;
		private readonly SocketsHttpHandler handler;
		private readonly HttpClient client;
		private readonly SemaphoreSlim pageSemaphore;
		private readonly SemaphoreSlim assetSemaphore;
		private readonly CancellationTokenSource stop = new();
		private readonly ConcurrentDictionary<string, byte> visitedPages = new(StringComparer.Ordinal);
		private readonly ConcurrentDictionary<string, byte> visitedAssets = new(StringComparer.Ordinal);
		private readonly object refusalLock = new();
		private readonly List<(string Url, string Reason)> containmentRefusals = [];
		private readonly Stopwatch clock = Stopwatch.StartNew();

		private int pagesSaved;
		private int assetsSaved;
		private int queued;
		private int active;
		private int stalled;
		private int errors;
		private int notFound;
		private int clientErrors;
		private int serverErrors;
		private int connectionErrors;
		private int redirects;
		private int oversized;
		private long downloadedBytes;
		private int limitTriggered;

		private readonly ConcurrentDictionary<string, ServerGate> gates = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, Func<Task>> deferred = new(StringComparer.Ordinal);
		private readonly ConcurrentDictionary<string, byte> retrying = new(StringComparer.Ordinal);
		private volatile bool retryRound;
		private int toRetry;
		private int recovered;
		private int stillFailed;

		private DownloadPhase phase = DownloadPhase.MainCrawl;
		private PreviousCrawl? previous;
		private int listTotal;
		private int listDone;
		private CancellationTokenSource? progressLoop;

		internal CrawlSession(CrawlSessionOptions options)
		{
			this.options = options;
			pageSemaphore = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentPageDownloads));
			assetSemaphore = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentAssetDownloads));

			var proxy = ProxyConfig.Build(options.ProxyUrl, options.ProxyUser, options.ProxyPassword, "Crawler");

			// One handler for pages and assets: the connection cap holds per server for both together.
			// [KEEP] No User-Agent is sent, on purpose: a site answers a named crawler with a
			// variant made for it (a bot page, another layout, a consent wall), and the crawl is
			// to see what a visitor's browser gets. See DECISIONS.md.
			handler = new SocketsHttpHandler
			{
				AllowAutoRedirect = false,
				PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
				PooledConnectionLifetime = TimeSpan.FromMinutes(10),
				MaxConnectionsPerServer = Math.Max(1, options.MaxConnectionsPerServer),
				UseProxy = proxy != null,
				Proxy = proxy,
			};

			// Without a proxy, record where each connection went: the same connect step as the
			// handler's own, with the address noted. Through a proxy every connection goes to the proxy.
			if (proxy == null && options.Dns is { } dns)
			{
				handler.ConnectCallback = dns.ConnectAsync;
			}

			// The stall timeout governs each download; the client-wide timeout would
			// otherwise cut a large but steadily arriving file.
			client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
		}

		/// <summary>True once the crawl passed the total download limit and was stopped.</summary>
		internal bool LimitExceeded => Volatile.Read(ref limitTriggered) == 1;

		internal long DownloadedBytes => Interlocked.Read(ref downloadedBytes);

		/// <summary>Downloads aborted by the stall timeout in the current phase.</summary>
		internal int Stalled => Volatile.Read(ref stalled);

		internal int FilesSaved => Volatile.Read(ref pagesSaved) + Volatile.Read(ref assetsSaved);

		/// <summary>Downloads that failed for a passing reason and wait for the retry round.</summary>
		internal int ToRetry => Volatile.Read(ref toRetry);

		/// <summary>Downloads the retry rounds of this phase saved after all.</summary>
		internal int Recovered => Volatile.Read(ref recovered);

		/// <summary>Downloads that failed in the retry rounds of this phase too.</summary>
		internal int StillFailed => Volatile.Read(ref stillFailed);

		/// <summary>How often a server's requests were paused, over the whole crawl.</summary>
		internal int Pauses => gates.Values.Sum(g => g.Pauses);

		internal TimeSpan Elapsed => clock.Elapsed;

		internal CrawlSessionOptions Options => options;

		internal IReadOnlyList<(string Url, string Reason)> ContainmentRefusals
		{
			get
			{
				lock (refusalLock)
				{
					return [.. containmentRefusals];
				}
			}
		}

		internal void AddContainmentRefusal(string url, string reason)
		{
			lock (refusalLock)
			{
				containmentRefusals.Add((url, reason));
			}
		}

		// ── Progress ─────────────────────────────────────────────────────────

		/// <summary>Starts reporting progress for a download phase (every half second).</summary>
		internal void BeginPhase(DownloadPhase newPhase, PreviousCrawl? previousCrawl, int total)
		{
			EndPhase();
			phase = newPhase;
			previous = previousCrawl;
			listTotal = total;
			listDone = 0;
			Interlocked.Exchange(ref pagesSaved, 0);
			Interlocked.Exchange(ref assetsSaved, 0);
			Interlocked.Exchange(ref stalled, 0);
			Interlocked.Exchange(ref errors, 0);
			Interlocked.Exchange(ref notFound, 0);
			Interlocked.Exchange(ref clientErrors, 0);
			Interlocked.Exchange(ref serverErrors, 0);
			Interlocked.Exchange(ref connectionErrors, 0);
			Interlocked.Exchange(ref redirects, 0);
			Interlocked.Exchange(ref oversized, 0);
			Interlocked.Exchange(ref recovered, 0);
			Interlocked.Exchange(ref stillFailed, 0);
			if (newPhase == DownloadPhase.MainCrawl)
			{
				clock.Restart();
			}

			var loop = new CancellationTokenSource();
			progressLoop = loop;
			var token = loop.Token;
			_ = Task.Run(async () =>
			{
				var samples = new Queue<(TimeSpan Time, long Bytes)>();
				while (!token.IsCancellationRequested)
				{
					try
					{
						await Task.Delay(500, token).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						break;
					}

					RunUi.Progress(Snapshot(samples));
				}
			}, token);
		}

		/// <summary>Stops progress reporting and sends one final report.</summary>
		internal void EndPhase()
		{
			var loop = Interlocked.Exchange(ref progressLoop, null);
			if (loop is null)
			{
				return;
			}

			loop.Cancel();
			loop.Dispose();
			RunUi.Progress(Snapshot(null) with { BytesPerSecond = AverageRate() });
		}

		/// <summary>Bytes per second over the whole phase so far: the speed shown once a phase is done.</summary>
		private double AverageRate()
		{
			var seconds = clock.Elapsed.TotalSeconds;
			return seconds > 0 ? Interlocked.Read(ref downloadedBytes) / seconds : 0;
		}

		/// <summary>A list URL of the extra download has finished (whatever its outcome).</summary>
		internal void MarkListItemDone() => Interlocked.Increment(ref listDone);

		private CrawlProgress Snapshot(Queue<(TimeSpan Time, long Bytes)>? samples)
		{
			var now = clock.Elapsed;
			var bytes = Interlocked.Read(ref downloadedBytes);
			double rate = 0;
			if (samples is not null)
			{
				samples.Enqueue((now, bytes));
				while (samples.Count > 6)
				{
					samples.Dequeue();
				}

				if (samples.Count >= 2)
				{
					var (t0, b0) = samples.Peek();
					var seconds = (now - t0).TotalSeconds;
					if (seconds > 0)
					{
						rate = (bytes - b0) / seconds;
					}
				}
			}

			return new CrawlProgress
			{
				Phase = phase,
				PagesSaved = Volatile.Read(ref pagesSaved),
				AssetsSaved = Volatile.Read(ref assetsSaved),
				Queued = Math.Max(0, Volatile.Read(ref queued)),
				Active = Math.Max(0, Volatile.Read(ref active)),
				Stalled = Volatile.Read(ref stalled),
				Errors = Volatile.Read(ref errors),
				NotFound = Volatile.Read(ref notFound),
				ClientErrors = Volatile.Read(ref clientErrors),
				ServerErrors = Volatile.Read(ref serverErrors),
				ConnectionErrors = Volatile.Read(ref connectionErrors),
				Redirects = Volatile.Read(ref redirects),
				Oversized = Volatile.Read(ref oversized),
				DownloadedBytes = bytes,
				BytesPerSecond = rate,
				Elapsed = now,
				LimitBytes = options.MaxCrawlBytes,
				ListTotal = listTotal,
				ListDone = Volatile.Read(ref listDone),
				Previous = previous,
				ToRetry = Volatile.Read(ref toRetry),
				Recovered = Volatile.Read(ref recovered),
				RetryRound = retryRound,
				PausedFor = PausedFor(),
				Pauses = Pauses,
			};
		}

		/// <summary>How much longer new requests are held, for the server held longest; null when none is.</summary>
		private TimeSpan? PausedFor()
		{
			var now = DateTime.UtcNow;
			DateTime? latest = null;
			foreach (var gate in gates.Values)
			{
				if (gate.PausedUntil(now) is { } until && (latest is null || until > latest))
				{
					latest = until;
				}
			}

			return latest - now;
		}

		// ── Pages ────────────────────────────────────────────────────────────

		internal async Task DownloadPageAsync(
			string url,
			string saveDirectory,
			string log,
			string websiteUrl,
			IReadOnlyList<CrawlLinkExclusion> downloadExclusions,
			List<ModalQueryParam> modalQueryParameters,
			string source,
			List<string>? jsonPathPrefixes,
			IReadOnlyList<string>? allowedSubdomains)
		{
			if (stop.IsCancellationRequested)
			{
				return;
			}

			var policy = options.Policy;
			var scopeBase = new Uri(websiteUrl).GetLeftPart(UriPartial.Authority);
			var requested = url;

			// Canonicalize (modal unwrap / query strip) inside a guard: the value comes from
			// page content, a malformed one must fail this URL only. The canonical URL is then
			// admitted through the gate again — the unwrapped target is a new URL the
			// discovery-time check never saw.
			string target;
			try
			{
				target = Crawl.CanonicalizeUrlForFetch(url, websiteUrl, modalQueryParameters);
			}
			catch (Exception ex) when (ex is UriFormatException or ArgumentException)
			{
				Logger.LogDetailToFile($"Crawl: could not canonicalize '{url}' ({ex.Message}). Not fetched.");
				return;
			}

			// Not counted for the exclusions' usage: each address was counted where it was found
			// (a link, a redirect, an asset, list 05), and this check would count it a second time.
			var targetAdmission = CrawlGate.TryAdmit(target, new Uri(websiteUrl), policy);
			if (!targetAdmission.Admitted
				|| !Validity.IsInDownloadScope(targetAdmission.AbsoluteUrl!, scopeBase, downloadExclusions, allowedSubdomains))
			{
				Logger.LogDetailToFile(
					$"Crawl gate: refused '{target}' (from '{url}', reason: {(targetAdmission.Admitted ? "excluded" : targetAdmission.Reason)}). Not fetched.");
				return;
			}

			url = target;

			if (HasPlaceholder(url))
			{
				Logger.LogDetailToFile($"Crawl gate: refused '{url}' (an unfilled template placeholder). Not fetched.");
				return;
			}

			if (!visitedPages.TryAdd(url, 0))
			{
				return;
			}

			// Tried again in the retry round: from the start, as if first found.
			var page = url;
			Task Again()
			{
				visitedPages.TryRemove(page, out _);
				return DownloadPageAsync(requested, saveDirectory, log, websiteUrl, downloadExclusions,
					modalQueryParameters, source, jsonPathPrefixes, allowedSubdomains);
			}

			if (!retrying.ContainsKey(url))
			{
				CrawlLogWriter.Write(url, "crawled", "info", log);
			}

			List<string> urlsToDownload = [];
			List<string> assetsToDownload = [];

			var pageBase = new Uri(url);

			Interlocked.Increment(ref queued);
			try
			{
				await pageSemaphore.WaitAsync(stop.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				Interlocked.Decrement(ref queued);
				return;
			}

			var slot = new Slot(this);
			using var transfer = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
			string? fileName = null;
			string? filePath = null;
			ServerGate? gate = null;
			ServerGate.Ticket ticket = default;
			try
			{
				// Waiting for the answer has its own timeout; the stall timer starts once the body flows.
				using var request = NewRequest(url);
				var sent = await SendWithRetriesAsync(client, request, slot).ConfigureAwait(false);
				(gate, ticket) = (sent.Gate, sent.Ticket);
				using var response = sent.Response;

				var location = response.Headers.Location;
				if (location != null)
				{
					var locationUrl = location.ToString();
					Interlocked.Increment(ref redirects);
					CrawlLogWriter.Write(url, response.StatusCode.ToString(), locationUrl, log);
					var redirectAdmission = CrawlGate.TryAdmit(locationUrl, pageBase, policy);
					if (redirectAdmission.Admitted
						&& Validity.IsInDownloadScope(redirectAdmission.AbsoluteUrl!, scopeBase, downloadExclusions, allowedSubdomains, options.ExclusionUsage, phase))
					{
						urlsToDownload.Add(StripFragment(redirectAdmission.AbsoluteUrl!));
					}
				}

				response.EnsureSuccessStatusCode();

				var contentType = response.Content.Headers.ContentType?.MediaType;

				// Pages are saved under a provisional ".unverified" name; the settle phase
				// decides the final extension.
				var baseName = Naming.GenerateFileName(url);
				fileName = Path.ChangeExtension(baseName, FileTypeClassifier.UnverifiedExtension.TrimStart('.'));
				var downloadDir = Path.Combine(saveDirectory, "download");
				Directory.CreateDirectory(downloadDir);

				var containment = PathContainmentCheck.Resolve(downloadDir, fileName);
				if (!containment.Safe)
				{
					Crawl.ReportContainmentRefusal(containment, url, downloadDir, log);
					return;
				}

				filePath = containment.FullPath;

				var outcome = await StoreBodyAsync(response, url, filePath, baseName, saveDirectory, log, transfer)
					.ConfigureAwait(false);
				if (outcome != StoreOutcome.Stored)
				{
					filePath = null;
					return;
				}

				Crawl.WriteHeaderSidecar(response, url, downloadDir, fileName);
				CrawlLogWriter.WriteSaved(url, fileName, log, source, contentType);
				Interlocked.Increment(ref pagesSaved);
				MarkRecovered(url);

				if (!FileTypeClassifier.IsHtmlContentType(contentType))
				{
					return;
				}

				var rawBytes = await File.ReadAllBytesAsync(filePath, stop.Token).ConfigureAwait(false);
				var encoding = DetectEncoding.FromBytes(rawBytes);
				var content = encoding.GetString(rawBytes);

				var document = CrawlerSuite.Core.Html.HtmlDocuments.Create();
				document.LoadHtml(content);

				var links = document.DocumentNode.SelectNodes("//a[@href]");
				if (links != null)
				{
					foreach (var link in links)
					{
						var hrefAttr = link.Attributes["href"];
						var href = hrefAttr?.DeEntitizeValue ?? string.Empty;
						var linkAdmission = CrawlGate.TryAdmit(href, pageBase, policy);
						if (linkAdmission.Admitted
							&& Validity.IsInDownloadScope(linkAdmission.AbsoluteUrl!, scopeBase, downloadExclusions, allowedSubdomains, options.ExclusionUsage, phase))
						{
							urlsToDownload.Add(StripFragment(linkAdmission.AbsoluteUrl!));
						}
					}
				}

				var assets = document.DocumentNode.SelectNodes("//img[@src]");
				if (assets != null)
				{
					foreach (var asset in assets)
					{
						var src = asset.GetAttributeValue("src", asset.GetAttributeValue("href", string.Empty));
						var assetAdmission = CrawlGate.TryAdmit(src, pageBase, policy);
						if (assetAdmission.Admitted
							&& Validity.IsInDownloadScope(assetAdmission.AbsoluteUrl!, scopeBase, downloadExclusions, allowedSubdomains, options.ExclusionUsage, phase))
						{
							assetsToDownload.Add(assetAdmission.AbsoluteUrl!);
						}
					}
				}

				var canonicalNodes = document.DocumentNode.SelectNodes(CrawlerSuite.Core.Html.XPaths.CanonicalLinks);
				if (canonicalNodes != null)
				{
					foreach (var node in canonicalNodes)
					{
						var canonicalHref = HtmlEntity.DeEntitize(node.GetAttributeValue("href", string.Empty));
						var canonicalAdmission = CrawlGate.TryAdmit(canonicalHref, pageBase, policy);
						if (canonicalAdmission.Admitted
							&& Validity.IsInDownloadScope(canonicalAdmission.AbsoluteUrl!, scopeBase, downloadExclusions, allowedSubdomains, options.ExclusionUsage, phase))
						{
							urlsToDownload.Add(StripFragment(canonicalAdmission.AbsoluteUrl!));
						}
					}
				}

				var extended = Extractor.FromHtml(content, url, jsonPathPrefixes);
				foreach (var extracted in extended)
				{
					var extAdmission = CrawlGate.TryAdmit(extracted.Url, pageBase, policy);
					if (!extAdmission.Admitted
						|| !Validity.IsInDownloadScope(extAdmission.AbsoluteUrl!, scopeBase, downloadExclusions, allowedSubdomains, options.ExclusionUsage, phase))
					{
						continue;
					}

					assetsToDownload.Add(StripFragment(extAdmission.AbsoluteUrl!));
				}
			}
			catch (OperationCanceledException) when (stop.IsCancellationRequested)
			{
				// The session stopped (total limit). The snapshot is discarded; no row.
				DeletePartial(filePath);
			}
			catch (OperationCanceledException)
			{
				DeletePartial(filePath);
				Pushback(gate, ticket, url);
				if (!Defer(url, Again))
				{
					Interlocked.Increment(ref stalled);
					CountError(null);
					MarkFailed(url);
					CrawlLogWriter.Write(url, "N/A", CrawlLogMessages.Stalled(options.StallTimeout), log);
				}
			}
			catch (AnswerTimeoutException)
			{
				DeletePartial(filePath);
				if (!Defer(url, Again))
				{
					CountError(null);
					MarkFailed(url);
					CrawlLogWriter.Write(url, "N/A", CrawlLogMessages.NoAnswer(options.AnswerTimeout), log);
				}
			}
			catch (HttpRequestException httpEx)
			{
				DeletePartial(filePath);
				if (!(IsPassing(httpEx) && Defer(url, Again)))
				{
					if (!IsRedirect(httpEx.StatusCode))
					{
						CountError(httpEx.StatusCode);
					}

					MarkFailed(url);
					CrawlLogWriter.Write(url, httpEx.StatusCode?.ToString() ?? "HttpRequestException", httpEx.Message, log);
				}
			}
			catch (Exception ex)
			{
				DeletePartial(filePath);
				if (IsTransient(ex))
				{
					Pushback(gate, ticket, url);
				}

				if (!(IsTransient(ex) && Defer(url, Again)))
				{
					CountError(null);
					MarkFailed(url);
					CrawlLogWriter.Write(url, "N/A", ex.Message, log);
				}
			}
			finally
			{
				slot.End();
				pageSemaphore.Release();
			}

			if (stop.IsCancellationRequested)
			{
				return;
			}

			var downloadTasks = urlsToDownload
				.Select(u => DownloadPageAsync(u, saveDirectory, log, websiteUrl, downloadExclusions,
					modalQueryParameters, source, jsonPathPrefixes, allowedSubdomains))
				.ToList();
			await Task.WhenAll(downloadTasks).ConfigureAwait(false);

			var assetTasks = assetsToDownload
				.Select(a => DownloadAssetAsync(a, saveDirectory, log, source))
				.ToList();
			await Task.WhenAll(assetTasks).ConfigureAwait(false);
		}

		// ── Assets ───────────────────────────────────────────────────────────

		/// <summary>
		/// Downloads one asset (image, script, stylesheet, …). Self-gating: the URL is
		/// re-admitted here, so no caller can reach an off-scheme, IP-literal or off-host
		/// target. CSS files are scanned one level for url() references.
		/// </summary>
		internal async Task DownloadAssetAsync(string assetUrl, string saveDirectory, string logFilePath, string source)
		{
			if (stop.IsCancellationRequested)
			{
				return;
			}

			var policy = options.Policy;
			if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var assetUri))
			{
				Logger.LogDetailToFile($"Asset gate: refused '{assetUrl}' (unparseable). Not fetched.");
				return;
			}

			var admission = CrawlGate.TryAdmit(assetUrl, assetUri, policy);
			if (!admission.Admitted)
			{
				Logger.LogDetailToFile($"Asset gate: refused '{assetUrl}' (reason: {admission.Reason}). Not fetched.");
				return;
			}

			assetUrl = admission.AbsoluteUrl!;

			if (HasPlaceholder(assetUrl))
			{
				Logger.LogDetailToFile($"Asset gate: refused '{assetUrl}' (an unfilled template placeholder). Not fetched.");
				return;
			}

			if (!visitedAssets.TryAdd(assetUrl, 0))
			{
				return;
			}

			var asset = assetUrl;
			Task Again()
			{
				visitedAssets.TryRemove(asset, out _);
				return DownloadAssetAsync(asset, saveDirectory, logFilePath, source);
			}

			if (!retrying.ContainsKey(assetUrl))
			{
				CrawlLogWriter.Write(assetUrl, "crawled", "info", logFilePath);
			}

			Interlocked.Increment(ref queued);
			try
			{
				await assetSemaphore.WaitAsync(stop.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				Interlocked.Decrement(ref queued);
				return;
			}

			var slot = new Slot(this);
			using var transfer = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
			string? filePath = null;
			bool scanCss = false;
			ServerGate? gate = null;
			ServerGate.Ticket ticket = default;
			try
			{
				// Assets are asked again like pages: a passing failure must not lose them.
				using var request = NewRequest(assetUrl);
				var sent = await SendWithRetriesAsync(client, request, slot).ConfigureAwait(false);
				(gate, ticket) = (sent.Gate, sent.Ticket);
				using var response = sent.Response;
				response.EnsureSuccessStatusCode();

				// An ending assets do not have is the site's choice of how Windows treats the file: saved
				// as ".unverified", as a linked file is.
				var assetFileName = Naming.GenerateAssetFileName(assetUrl);
				var downloadDir = Path.Combine(saveDirectory, "download");
				Directory.CreateDirectory(downloadDir);

				var containment = PathContainmentCheck.Resolve(downloadDir, assetFileName);
				if (!containment.Safe)
				{
					Crawl.ReportContainmentRefusal(containment, assetUrl, downloadDir, logFilePath);
					return;
				}

				filePath = containment.FullPath;
				var outcome = await StoreBodyAsync(response, assetUrl, filePath, assetFileName, saveDirectory, logFilePath, transfer)
					.ConfigureAwait(false);
				if (outcome != StoreOutcome.Stored)
				{
					filePath = null;
					return;
				}

				Crawl.WriteHeaderSidecar(response, assetUrl, downloadDir, assetFileName);
				CrawlLogWriter.Write(assetUrl, "savedAsset", assetFileName, logFilePath, source);
				Interlocked.Increment(ref assetsSaved);
				MarkRecovered(assetUrl);
				scanCss = assetUrl.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
			}
			catch (OperationCanceledException) when (stop.IsCancellationRequested)
			{
				DeletePartial(filePath);
			}
			catch (OperationCanceledException)
			{
				DeletePartial(filePath);
				Pushback(gate, ticket, assetUrl);
				if (!Defer(assetUrl, Again))
				{
					Interlocked.Increment(ref stalled);
					CountError(null);
					MarkFailed(assetUrl);
					CrawlLogWriter.Write(assetUrl, "N/A", CrawlLogMessages.Stalled(options.StallTimeout), logFilePath);
				}
			}
			catch (AnswerTimeoutException)
			{
				DeletePartial(filePath);
				if (!Defer(assetUrl, Again))
				{
					CountError(null);
					MarkFailed(assetUrl);
					CrawlLogWriter.Write(assetUrl, "N/A", CrawlLogMessages.NoAnswer(options.AnswerTimeout), logFilePath);
				}
			}
			catch (HttpRequestException httpEx)
			{
				DeletePartial(filePath);
				if (!(IsPassing(httpEx) && Defer(assetUrl, Again)))
				{
					if (!IsRedirect(httpEx.StatusCode))
					{
						CountError(httpEx.StatusCode);
					}

					MarkFailed(assetUrl);
					CrawlLogWriter.Write(assetUrl, httpEx.StatusCode?.ToString() ?? "N/A", httpEx.Message, logFilePath);
				}
			}
			catch (Exception ex)
			{
				DeletePartial(filePath);
				if (IsTransient(ex))
				{
					Pushback(gate, ticket, assetUrl);
				}

				if (!(IsTransient(ex) && Defer(assetUrl, Again)))
				{
					CountError(null);
					MarkFailed(assetUrl);
					CrawlLogWriter.Write(assetUrl, "N/A", ex.Message, logFilePath);
				}
			}
			finally
			{
				slot.End();
				assetSemaphore.Release();
			}

			// CSS files are scanned for url() references (outside the download slot, so a
			// stylesheet's own references never wait on the slot it holds).
			if (scanCss && filePath is not null && !stop.IsCancellationRequested)
			{
				string cssText;
				try
				{
					cssText = await File.ReadAllTextAsync(filePath, System.Text.Encoding.UTF8).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Logger.LogWarning($"CSS scan: could not read {Path.GetFileName(filePath)} — {ex.Message}");
					return;
				}

				var cssUrls = Extractor.FromCss(cssText, assetUrl);
				var cssTasks = cssUrls
					.Select(u => DownloadAssetAsync(u.Url, saveDirectory, logFilePath, source))
					.ToList();
				await Task.WhenAll(cssTasks).ConfigureAwait(false);
			}
		}

		// ── Body storage with limits ─────────────────────────────────────────

		private enum StoreOutcome { Stored, Oversized }

		/// <summary>
		/// Streams the response body to <paramref name="filePath"/>, resetting the stall timer
		/// on every chunk and counting bytes against both limits. A body over the single-file
		/// limit is not stored: its first <see cref="OversizedFiles.SampleBytes"/> bytes and the
		/// header sidecar go to <c>oversized/</c>, and an <c>Oversized</c> row is logged.
		/// </summary>
		private async Task<StoreOutcome> StoreBodyAsync(
			HttpResponseMessage response,
			string url,
			string filePath,
			string baseName,
			string saveDirectory,
			string log,
			CancellationTokenSource transfer)
		{
			long maxFile = options.MaxSingleFileBytes;
			var announced = response.Content.Headers.ContentLength;
			var sampleName = OversizedFiles.SampleName(baseName);

			await using var body = await response.Content.ReadAsStreamAsync(transfer.Token).ConfigureAwait(false);

			// Announced too large: take the sample only, never touch the download folder.
			if (maxFile > 0 && announced.HasValue && announced.Value > maxFile)
			{
				var sample = await ReadSampleAsync(body, transfer).ConfigureAwait(false);
				RecordOversized(response, url, saveDirectory, sampleName, sample, log);
				return StoreOutcome.Oversized;
			}

			var buffer = new byte[CopyBufferSize];
			long written = 0;
			bool over = false;
			await using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read,
				CopyBufferSize, useAsync: true))
			{
				while (true)
				{
					transfer.CancelAfter(options.StallTimeout);
					int read = await body.ReadAsync(buffer, transfer.Token).ConfigureAwait(false);
					if (read == 0)
					{
						break;
					}

					await fs.WriteAsync(buffer.AsMemory(0, read), transfer.Token).ConfigureAwait(false);
					written += read;
					CountBytes(read);

					if (maxFile > 0 && written > maxFile)
					{
						over = true;
						break;
					}
				}
			}

			if (!over)
			{
				return StoreOutcome.Stored;
			}

			// Grew past the limit without announcing it: keep the head as the sample.
			byte[] head;
			await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				head = new byte[(int)Math.Min(OversizedFiles.SampleBytes, fs.Length)];
				int total = 0;
				while (total < head.Length)
				{
					int r = await fs.ReadAsync(head.AsMemory(total), CancellationToken.None).ConfigureAwait(false);
					if (r == 0)
					{
						break;
					}

					total += r;
				}
			}

			DeletePartial(filePath);
			RecordOversized(response, url, saveDirectory, sampleName, head, log);
			return StoreOutcome.Oversized;
		}

		private async Task<byte[]> ReadSampleAsync(Stream body, CancellationTokenSource transfer)
		{
			var sample = new byte[OversizedFiles.SampleBytes];
			int total = 0;
			while (total < sample.Length)
			{
				transfer.CancelAfter(options.StallTimeout);
				int read = await body.ReadAsync(sample.AsMemory(total), transfer.Token).ConfigureAwait(false);
				if (read == 0)
				{
					break;
				}

				total += read;
				CountBytes(read);
			}

			Array.Resize(ref sample, total);
			return sample;
		}

		private void RecordOversized(
			HttpResponseMessage response, string url, string saveDirectory, string sampleName, byte[] sample, string log)
		{
			Interlocked.Increment(ref oversized);
			var dir = Path.Combine(saveDirectory, OversizedFiles.FolderName);
			try
			{
				Directory.CreateDirectory(dir);
				var containment = PathContainmentCheck.Resolve(dir, sampleName);
				if (containment.Safe)
				{
					File.WriteAllBytes(containment.FullPath, sample);
					Crawl.WriteHeaderSidecar(response, url, dir, sampleName);
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Oversized sample: could not write {sampleName} — {ex.Message}");
			}

			CrawlLogWriter.Write(url, CrawlLogMessages.OversizedStatus,
				CrawlLogMessages.Oversized(options.MaxSingleFileBytes, sampleName), log);
			Logger.LogWarning($"Oversized: {url} exceeds the single-file limit; not stored, sample kept as {sampleName}.");
		}

		private void CountBytes(int read)
		{
			var total = Interlocked.Add(ref downloadedBytes, read);
			if (options.MaxCrawlBytes > 0 && total > options.MaxCrawlBytes
				&& Interlocked.Exchange(ref limitTriggered, 1) == 0)
			{
				Logger.LogWarning(
					$"Total download limit reached ({total:N0} of {options.MaxCrawlBytes:N0} bytes) — stopping the crawl.");
				stop.Cancel();
			}
		}

		// ── Helpers ──────────────────────────────────────────────────────────

		private static string StripFragment(string url)
		{
			var hashIdx = url.IndexOf('#');
			return hashIdx >= 0 ? url[..hashIdx] : url;
		}

		private static bool IsRedirect(HttpStatusCode? code) =>
			code is { } c && (int)c >= 300 && (int)c < 400;

		private static void DeletePartial(string? path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not remove partial download {Path.GetFileName(path)} — {ex.Message}");
			}
		}

		private static bool IsTransient(Exception ex) =>
			ex is HttpRequestException or IOException or SocketException or AuthenticationException;

		/// <summary>
		/// Sends a GET and waits for the answer (status and headers) up to the answer timeout,
		/// through the server's <see cref="ServerGate"/>. A server that does not answer in time,
		/// a connection or TLS failure, and a 429 or 5xx answer are asked again (pages and assets
		/// alike), each attempt with a fresh request (a message can only be sent once). Every
		/// outcome is reported to the gate. Throws <see cref="AnswerTimeoutException"/> when the
		/// last attempt got no answer; a last 429 or 5xx is returned for the caller to log.
		/// </summary>
		private async Task<(HttpResponseMessage Response, ServerGate Gate, ServerGate.Ticket Ticket)> SendWithRetriesAsync(
			HttpClient client, HttpRequestMessage originalRequest, Slot slot)
		{
			var uri = originalRequest.RequestUri!;
			var gate = GateOf(uri);
			for (int attempt = 0; ; attempt++)
			{
				bool last = attempt >= MaxRetries - 1;
				var ticket = await PassGateAsync(gate, uri.AbsoluteUri).ConfigureAwait(false);
				slot.Start();
				using var current = Clone(originalRequest);
				using var answer = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
				answer.CancelAfter(options.AnswerTimeout);
				HttpResponseMessage response;
				try
				{
					response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, answer.Token)
						.ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (!stop.IsCancellationRequested && answer.IsCancellationRequested)
				{
					Pushback(gate, ticket, uri.AbsoluteUri);
					if (last)
					{
						throw new AnswerTimeoutException();
					}

					await Task.Delay(BackoffDelay(attempt), stop.Token).ConfigureAwait(false);
					continue;
				}
				catch (Exception ex) when (IsTransient(ex))
				{
					Pushback(gate, ticket, uri.AbsoluteUri);
					if (last)
					{
						throw;
					}

					await Task.Delay(BackoffDelay(attempt), stop.Token).ConfigureAwait(false);
					continue;
				}

				// Answered: the body is guarded by the stall timer from here on.
				answer.CancelAfter(Timeout.InfiniteTimeSpan);

				Seen(uri, response);
				if (IsPushback(response.StatusCode))
				{
					Pushback(gate, ticket, uri.AbsoluteUri);
				}
				else
				{
					gate.Answered(ticket, DateTime.UtcNow);
				}

				if (IsRetryable(response.StatusCode) && !last)
				{
					response.Dispose();
					await Task.Delay(BackoffDelay(attempt), stop.Token).ConfigureAwait(false);
					continue;
				}

				return (response, gate, ticket);
			}
		}

		/// <summary>
		/// A download's place in the counters: "waiting" until its request goes out — through the
		/// download slot and the server's gate — then "downloading" until it ends.
		/// </summary>
		private sealed class Slot(CrawlSession session)
		{
			private int started;

			internal void Start()
			{
				if (Interlocked.Exchange(ref started, 1) == 0)
				{
					Interlocked.Decrement(ref session.queued);
					Interlocked.Increment(ref session.active);
				}
			}

			internal void End()
			{
				if (Volatile.Read(ref started) == 1)
				{
					Interlocked.Decrement(ref session.active);
				}
				else
				{
					Interlocked.Decrement(ref session.queued);
				}
			}
		}

		/// <summary>Counts a failure, and of which kind: 404/410, other 4xx, 5xx, or the connection (no status).</summary>
		private void CountError(HttpStatusCode? code)
		{
			Interlocked.Increment(ref errors);
			switch (code is { } c ? (int)c : 0)
			{
				case 404 or 410:
					Interlocked.Increment(ref notFound);
					break;
				case >= 400 and < 500:
					Interlocked.Increment(ref clientErrors);
					break;
				case >= 500:
					Interlocked.Increment(ref serverErrors);
					break;
				default:
					Interlocked.Increment(ref connectionErrors);
					break;
			}
		}

		/// <summary>
		/// 429 (too many requests), 502, 503 or 504: the server, or what stands before it, asks to be
		/// left alone for a while. A 500 is not among them: it is that address's own answer, and an
		/// address that always fails must not hold back the crawl of a healthy server.
		/// </summary>
		private static bool IsPushback(HttpStatusCode code) => (int)code is 429 or 502 or 503 or 504;

		/// <summary>Asked again at once: 429 and every 5xx (a 500 too may pass).</summary>
		private static bool IsRetryable(HttpStatusCode code) => (int)code == 429 || (int)code >= 500;

		/// <summary>An HTTP failure that may pass, and so waits for the retry round: no status (the connection failed), 429 or 5xx.</summary>
		private static bool IsPassing(HttpRequestException ex) => ex.StatusCode is not { } code || IsRetryable(code);

		// ── Protocols ────────────────────────────────────────────────────────

		private readonly ConcurrentDictionary<string, Version> protocols = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, byte> offersHttp3 = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Notes the protocol a server answered in, and whether it offers HTTP/3 (Alt-Svc).</summary>
		private void Seen(Uri uri, HttpResponseMessage response)
		{
			protocols.AddOrUpdate(uri.Authority, response.Version, (_, known) => response.Version > known ? response.Version : known);
			if (response.Headers.TryGetValues("Alt-Svc", out var alternatives)
				&& alternatives.Any(a => a.Contains("h3", StringComparison.OrdinalIgnoreCase)))
			{
				offersHttp3.TryAdd(uri.Authority, 0);
			}
		}

		/// <summary>
		/// The protocols the servers answered in ("HTTP/1.1"; "HTTP/1.1, HTTP/2" when they differ),
		/// and whether a server offers HTTP/3 the crawl did not use; empty before any answer.
		/// Names no server.
		/// </summary>
		internal string ProtocolNote()
		{
			if (protocols.IsEmpty)
			{
				return string.Empty;
			}

			var used = protocols.Values.Distinct().OrderBy(v => v).Select(Name).ToList();
			var note = string.Join(", ", used);
			if (!offersHttp3.IsEmpty && protocols.Values.All(v => v.Major < 3))
			{
				note += offersHttp3.Count == protocols.Count ? " (the server offers HTTP/3)" : " (a server offers HTTP/3)";
			}

			return note;

			static string Name(Version v) => v.Major >= 2 ? $"HTTP/{v.Major}" : $"HTTP/{v.Major}.{v.Minor}";
		}

		// ── robots.txt ───────────────────────────────────────────────────────

		/// <summary>
		/// The Crawl-delay the site's robots.txt asks of every crawler ("User-agent: *"), in
		/// seconds; null when there is none or robots.txt cannot be read. Reported, never applied:
		/// the crawl is to get what a visitor's browser gets, and a browser does not read robots.txt.
		/// </summary>
		internal async Task<double?> RobotsCrawlDelayAsync(string websiteUrl)
		{
			try
			{
				var robots = new Uri(new Uri(websiteUrl), "/robots.txt");
				using var request = NewRequest(robots.AbsoluteUri);
				using var answer = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
				answer.CancelAfter(TimeSpan.FromSeconds(15));
				using var response = await client.SendAsync(request, answer.Token).ConfigureAwait(false);
				return response.IsSuccessStatusCode
					? CrawlDelayOf(await response.Content.ReadAsStringAsync(answer.Token).ConfigureAwait(false))
					: null;
			}
			catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UriFormatException)
			{
				return null;
			}
		}

		/// <summary>The Crawl-delay of the group for every crawler ("User-agent: *") in a robots.txt; null when there is none.</summary>
		internal static double? CrawlDelayOf(string robotsText)
		{
			bool inStar = false;
			bool lastWasAgent = false;
			foreach (var raw in robotsText.Split('\n'))
			{
				var line = raw.Split('#')[0].Trim();
				int colon = line.IndexOf(':');
				if (colon <= 0)
				{
					continue;
				}

				var key = line[..colon].Trim();
				var value = line[(colon + 1)..].Trim();
				if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
				{
					// Consecutive User-agent lines open one group together.
					inStar = (lastWasAgent && inStar) || value == "*";
					lastWasAgent = true;
					continue;
				}

				lastWasAgent = false;
				if (inStar && key.Equals("Crawl-delay", StringComparison.OrdinalIgnoreCase)
					&& double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
					&& seconds >= 0)
				{
					return seconds;
				}
			}

			return null;
		}

		// ── Placeholders ─────────────────────────────────────────────────────

		/// <summary>
		/// An address with an unfilled template placeholder ("{{name}}"), as scripts carry them: a
		/// template, never a real address. Not fetched.
		/// </summary>
		internal static bool HasPlaceholder(string url) =>
			url.Contains("{{", StringComparison.Ordinal) || url.Contains("}}", StringComparison.Ordinal)
			|| url.Contains("%7B%7B", StringComparison.OrdinalIgnoreCase) || url.Contains("%7D%7D", StringComparison.OrdinalIgnoreCase);

		private ServerGate GateOf(Uri uri) => gates.GetOrAdd(uri.Authority, _ => new ServerGate(options.Gate, options.MaxRequestsPerSecond));

		/// <summary>Waits until the server's gate lets a request go.</summary>
		private async Task<ServerGate.Ticket> PassGateAsync(ServerGate gate, string address)
		{
			while (true)
			{
				var answer = gate.Enter(DateTime.UtcNow, address);
				if (answer.Wait > TimeSpan.Zero)
				{
					await Task.Delay(answer.Wait, stop.Token).ConfigureAwait(false);
				}

				if (answer.Ticket is { } ticket)
				{
					return ticket;
				}
			}
		}

		/// <summary>Tells the gate a request failed or was pushed back; logs a pause it starts.</summary>
		private static void Pushback(ServerGate? gate, ServerGate.Ticket ticket, string url)
		{
			var now = DateTime.UtcNow;
			if (gate is null || !gate.Failed(ticket, now, url))
			{
				return;
			}

			var seconds = gate.PauseLength(now).TotalSeconds;
			Logger.LogWarning($"Crawl: a server is not answering properly — new requests to it are paused for {seconds:0} s (pause {gate.Pauses}).");
			Logger.LogDetailToFile($"Crawl: pausing requests to {(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : url)} for {seconds:0} s after failures clustered.");
		}

		// ── Retry round ──────────────────────────────────────────────────────

		/// <summary>Downloads tried at once in the retry round.</summary>
		internal const int RetryParallelism = 4;

		/// <summary>
		/// Holds a download that failed for a passing reason for the retry round; false when it
		/// is to be logged as failed now (in the retry round itself, or once the crawl stopped).
		/// </summary>
		private bool Defer(string url, Func<Task> again)
		{
			if (retryRound || stop.IsCancellationRequested)
			{
				return false;
			}

			if (deferred.TryAdd(url, again))
			{
				Interlocked.Increment(ref toRetry);
			}

			return true;
		}

		private void MarkRecovered(string url)
		{
			if (retrying.TryRemove(url, out _))
			{
				Interlocked.Increment(ref recovered);
			}
		}

		private void MarkFailed(string url)
		{
			if (retrying.TryRemove(url, out _))
			{
				Interlocked.Increment(ref stillFailed);
			}
		}

		/// <summary>
		/// Tries once more, after a pause and a few at a time, every download of the phase that
		/// failed for a passing reason. What fails again is logged as failed; what was found
		/// anew on a page saved now is crawled as usual. Call at the end of a download phase.
		/// </summary>
		internal async Task RetryRoundAsync()
		{
			if (deferred.IsEmpty || stop.IsCancellationRequested)
			{
				return;
			}

			var items = deferred.ToArray();
			deferred.Clear();
			foreach (var (url, _) in items)
			{
				retrying[url] = 0;
			}

			retryRound = true;
			Logger.LogInfo($"Crawl: {items.Length} download(s) failed for a passing reason; trying them again in "
				+ $"{options.RetryRoundPause.TotalSeconds:0} s, {RetryParallelism} at a time.");
			try
			{
				await Task.Delay(options.RetryRoundPause, stop.Token).ConfigureAwait(false);
				using var slots = new SemaphoreSlim(RetryParallelism);
				await Task.WhenAll(items.Select(async item =>
				{
					await slots.WaitAsync(stop.Token).ConfigureAwait(false);
					try
					{
						await item.Value().ConfigureAwait(false);
					}
					finally
					{
						slots.Release();
						Interlocked.Decrement(ref toRetry);
					}
				})).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (stop.IsCancellationRequested)
			{
				// The session stopped (total limit): the snapshot is discarded.
			}
			finally
			{
				retryRound = false;
			}

			// What neither saved nor failed again (a redirect now, say) counts as neither.
			retrying.Clear();
			Logger.LogInfo($"Crawl: retry round done — {Recovered} recovered, {StillFailed} still failed.");
		}

		private static TimeSpan BackoffDelay(int attempt)
		{
			var maxMillis = (int)(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
			var jitter = Random.Shared.Next(0, Math.Min(1000, maxMillis));
			return TimeSpan.FromMilliseconds(maxMillis + jitter);
		}

		/// <summary>
		/// A GET in the highest HTTP version allowed, taking what the server agrees to: by default
		/// HTTP/1.1, where every request has a connection of its own while it runs — a failure, a
		/// stall and every measurement belong to that request alone.
		/// </summary>
		private HttpRequestMessage NewRequest(string url) => new(HttpMethod.Get, url)
		{
			Version = options.HttpVersion,
			VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
		};

		private static HttpRequestMessage Clone(HttpRequestMessage req)
		{
			var clone = new HttpRequestMessage(req.Method, req.RequestUri) { Version = req.Version, VersionPolicy = req.VersionPolicy };
			foreach (var header in req.Headers)
			{
				clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}

			return clone;
		}

		public void Dispose()
		{
			EndPhase();
			stop.Cancel();
			client.Dispose();
			handler.Dispose();
			pageSemaphore.Dispose();
			assetSemaphore.Dispose();
			stop.Dispose();
		}
	}

	/// <summary>The server did not start answering within the answer timeout, after the retries.</summary>
	internal sealed class AnswerTimeoutException : TimeoutException
	{
	}
}
