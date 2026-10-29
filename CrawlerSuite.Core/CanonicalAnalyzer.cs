using HtmlAgilityPack;
using System.Text;

namespace CrawlerSuite.Core
{
	// ── CanonicalAnalyzer ─────────────────────────────────────────────────────
	//
	// Reads all downloaded HTML files, extracts <link rel="canonical"> tags,
	// and detects the following issues:
	//
	//   CANONICAL_404      — canonical URL points to a page not in the crawler
	//                        index (not downloaded = not found on this site).
	//                        Auto-promoted to IssueTracking as "new".
	//
	//   CANONICAL_CHAIN    — canonical chain A→B→C (B also has a canonical).
	//                        Search engines only follow one hop — the chain is
	//                        ineffective. Auto-promoted to IssueTracking as "new".
	//
	//   CANONICAL_CONFLICT — page has multiple <link rel="canonical"> tags.
	//                        Only the first is honoured by search engines — the
	//                        rest are ignored. Auto-promoted to IssueTracking.
	//
	//   CANONICAL_REDIRECT — canonical URL redirects (log 03): a search engine
	//                        gets sent on instead of finding the page named.
	//                        Auto-promoted to IssueTracking.
	//
	//   CANONICAL_NOINDEX  — canonical URL is a page kept out of search (noindex
	//                        in the page or by header, log 08), while the page
	//                        itself is not: the two signals contradict each other.
	//                        Auto-promoted to IssueTracking.
	//
	//   CANONICAL_EXTERNAL — canonical URL host differs from the crawled site's
	//                        host (may be intentional syndication). Logged only,
	//                        NOT auto-promoted to IssueTracking.
	//
	// Output: 16-canonical-issues_semicolon.csv and _comma.csv (timestamped folder,
	// dual-locale CSV pair, like the other human-facing analysis logs). IssueTracking
	// entries returned for the caller to merge.
	//
	// Fields are RFC 4180-quoted (IssueLogWriter.WriteCsvPair): a delimiter or quote
	// inside a URL or Detail value is preserved verbatim rather than stripped.
	// ─────────────────────────────────────────────────────────────────────────

	public static class CanonicalAnalyzer
	{
		// ── Public entry point ────────────────────────────────────────────────

		/// <summary>
		/// Analyses canonical links across all downloaded HTML files.
		/// Writes the 16-canonical-issues dual-locale CSV pair and returns IssueRecords for
		/// CANONICAL_404, CANONICAL_CHAIN, and CANONICAL_CONFLICT to be
		/// merged into IssueTracking.log by the caller.
		/// CANONICAL_EXTERNAL issues are logged but not returned.
		/// </summary>
		public static List<IssueTracking.IssueRecord> Analyse(
			string downloadDirectory,
			string crawlerIndexPath,
			string canonicalCsvBasePath,
			string siteBaseUrl,
			IReadOnlyList<string> allowedSubdomainUrls,
			string filePattern,
			IReadOnlyDictionary<string, string[]>? canonicalHrefsByFile = null,
			string? seoDataPath = null,
			string? redirectLogPath = null)
		{
			// Build filename→url and url→filename maps from the crawler index.
			// Pipe delimiter — RFC 3986 defines '|' as not valid in URLs.
			var filenameToUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var urlToFilename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			if (File.Exists(crawlerIndexPath))
			{
				foreach (var line in File.ReadAllLines(crawlerIndexPath, Encoding.UTF8))
				{
					if (string.IsNullOrWhiteSpace(line))
					{
						continue;
					}

					var parts = line.Split('|', StringSplitOptions.TrimEntries);
					if (parts.Length < 2)
					{
						continue;
					}
					var url = parts[1];

					filenameToUrl[parts[0]] = url;
					urlToFilename[url] = parts[0];
				}
			}

			// Build the canonical map: pageUrl → list of declared canonical URLs.
			var canonicalMap = BuildCanonicalMap(
				downloadDirectory, filenameToUrl, filePattern, canonicalHrefsByFile);

			// Detect issues.
			var issues = DetectIssues(canonicalMap, urlToFilename, siteBaseUrl, allowedSubdomainUrls,
				RedirectAnalyzer.Targets(redirectLogPath), SearchByUrl(seoDataPath));

			// Write log.
			WriteLog(canonicalCsvBasePath, issues);
			Logger.LogInfo($"Canonical analysis: {issues.Count} issue(s) found. " +
				$"See {Path.GetFileName(canonicalCsvBasePath)}{IssueLogWriter.CsvSemicolonSuffix} / " +
				$"{Path.GetFileName(canonicalCsvBasePath)}{IssueLogWriter.CsvCommaSuffix}.");

			// Return only auto-promotable issues (not CANONICAL_EXTERNAL).
			return issues
				.Where(i => i.IssueType != "CANONICAL_EXTERNAL")
				.Select(i => new IssueTracking.IssueRecord
				{
					Type = "QUALITY",
					Url = i.PageUrl,
					Word = i.IssueType,
					SourceLabel = i.CanonicalUrl,
					Excerpt = i.Detail,
				})
				.ToList();
		}

		// ── Issue model ───────────────────────────────────────────────────────

		public record CanonicalIssue(
			string PageUrl,
			string IssueType,
			string CanonicalUrl,
			string Detail);

		// ── Canonical map builder ─────────────────────────────────────────────

		/// <summary>
		/// Reads all HTML files in the download directory and extracts
		/// canonical URLs. Returns pageUrl → list of canonical URLs declared.
		/// </summary>
		/// <param name="canonicalHrefsByFile">
		/// The raw canonical hrefs per page file the SEO step already read from its parse of the
		/// same files; a page found there is not parsed again. Null (or a page missing from it,
		/// e.g. when a debug session kept a cached log 08) parses the page itself.
		/// </param>
		internal static Dictionary<string, List<string>> BuildCanonicalMap(
			string downloadDirectory,
			IReadOnlyDictionary<string, string> filenameToUrl,
			string filePattern,
			IReadOnlyDictionary<string, string[]>? canonicalHrefsByFile = null)
		{
			var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

			if (!Directory.Exists(downloadDirectory))
			{
				return result;
			}

			var files = CrawlerSuite.Core.Host.FolderFiles.Sorted(downloadDirectory, filePattern,
				SearchOption.TopDirectoryOnly);

			// Pages are parsed in parallel; the map is filled in file order afterwards, so its order
			// (and with it the issue order of log 16) is the same as a one-by-one pass.
			var perFile = new (string Url, List<string> Canonicals)?[files.Length];
			RunUi.StepTotal(files.Length);
			Parallel.For(0, files.Length, i => perFile[i] = CanonicalsOf(files[i], filenameToUrl, canonicalHrefsByFile));
			foreach (var entry in perFile)
			{
				if (entry is { } e)
				{
					result[e.Url] = e.Canonicals;
				}
			}

			return result;
		}

		/// <summary>The canonical URLs one page declares, or null when it declares none (or cannot be read).</summary>
		private static (string Url, List<string> Canonicals)? CanonicalsOf(
			string file, IReadOnlyDictionary<string, string> filenameToUrl, IReadOnlyDictionary<string, string[]>? canonicalHrefsByFile)
		{
			{
				RunUi.StepAdvance();
				var filename = Path.GetFileName(file);
				if (!filenameToUrl.TryGetValue(filename, out var pageUrl))
				{
					return null;
				}

				IReadOnlyList<string> rawHrefs;
				if (canonicalHrefsByFile is not null && canonicalHrefsByFile.TryGetValue(file, out var harvested))
				{
					rawHrefs = harvested;
				}
				else
				{
					var doc = CrawlerSuite.Core.Html.HtmlDocuments.Create();
					// Decode with the page's own charset, like the rest of the pipeline.
					try
					{
						var raw = CrawlerSuite.Core.Host.WorkFile.ReadAllBytes(file);
						doc.LoadHtml(DetectEncoding.FromBytes(raw).GetString(raw));
					}
					catch (Exception ex)
					{
						Logger.LogWarning($"Canonical analysis: {filename} skipped — {ex.Message}");
						return null;
					}

					var canonicalNodes = doc.DocumentNode
						.SelectNodes(CrawlerSuite.Core.Html.XPaths.CanonicalLinks);
					if (canonicalNodes == null)
					{
						return null;
					}

					rawHrefs = canonicalNodes.Select(n => n.GetAttributeValue("href", string.Empty)).ToList();
				}

				List<string> canonicals = [];
				foreach (var rawHref in rawHrefs)
				{
					// Attribute values are not entity-decoded by the parser: "?a=1&amp;b=2" must
					// compare equal to the crawled "?a=1&b=2".
					var href = HtmlEntity.DeEntitize(rawHref).Trim();
					if (string.IsNullOrEmpty(href))
					{
						continue;
					}

					// Resolve relative URLs against the page URL.
					if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
					{
						try
						{
							href = new Uri(new Uri(pageUrl), href).ToString();
						}
						catch { continue; }
					}

					canonicals.Add(href);
				}

				return canonicals.Count > 0 ? (pageUrl, canonicals) : null;
			}
		}

		// ── Issue detection ───────────────────────────────────────────────────

		/// <summary>
		/// Detects canonical issues from the canonical map.
		/// Internal so it can be unit-tested without file I/O.
		/// </summary>
		/// <param name="redirects">The redirects of log 03 by the address that redirects (none: not checked).</param>
		/// <param name="searchByUrl">Column search of log 08 by page (none: not checked).</param>
		internal static List<CanonicalIssue> DetectIssues(
			IReadOnlyDictionary<string, List<string>> canonicalMap,
			IReadOnlyDictionary<string, string> urlToFilename,
			string siteBaseUrl,
			IReadOnlyList<string>? allowedSubdomainUrls = null,
			IReadOnlyDictionary<string, (string Status, string Target)>? redirects = null,
			IReadOnlyDictionary<string, string>? searchByUrl = null)
		{
			List<CanonicalIssue> issues = [];
			var siteHost = GetHost(siteBaseUrl);

			// In-scope hosts: the primary site plus any operator-declared allowed subdomains
			// (SiteConfig.UrlSubdomainsAllowed). A canonical pointing at any of these is in
			// scope; only a host outside the whole set is CANONICAL_EXTERNAL. Comparing against
			// siteHost alone would flag an allowed subdomain (e.g. a module.* page
			// self-canonicalising) as external.
			var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (!string.IsNullOrEmpty(siteHost))
			{
				allowedHosts.Add(siteHost);
			}
			if (allowedSubdomainUrls != null)
			{
				foreach (var allowed in allowedSubdomainUrls)
				{
					var allowedHost = GetHost(allowed);
					if (!string.IsNullOrEmpty(allowedHost))
					{
						allowedHosts.Add(allowedHost);
					}
				}
			}

			foreach (var (pageUrl, canonicals) in canonicalMap)
			{
				// CANONICAL_CONFLICT — multiple canonical tags on one page.
				if (canonicals.Count > 1)
				{
					issues.Add(new CanonicalIssue(
						pageUrl,
						"CANONICAL_CONFLICT",
						canonicals[0],
						$"{canonicals.Count} canonical tags found — only first honoured: {string.Join(", ", canonicals)}"));
				}

				// Analyse the first (effective) canonical.
				var canonical = canonicals[0];
				var canonicalHost = GetHost(canonical);

				// CANONICAL_EXTERNAL — host outside the in-scope set (primary + allowed subdomains).
				if (allowedHosts.Count > 0
					&& !allowedHosts.Contains(canonicalHost))
				{
					issues.Add(new CanonicalIssue(
						pageUrl,
						"CANONICAL_EXTERNAL",
						canonical,
						$"Canonical points outside allowed crawl scope ({string.Join(", ", allowedHosts)}) to: {canonicalHost}"));
					continue;
				}

				// CANONICAL_REDIRECT — the canonical names an address that redirects. Checked before
				// "not found": the crawler saves the page a redirect leads to, not its address.
				if (redirects is not null && Lookup(redirects, canonical) is var (status, target) && target is not null)
				{
					issues.Add(new CanonicalIssue(
						pageUrl,
						"CANONICAL_REDIRECT",
						canonical,
						$"Canonical target redirects ({status}) to: {target}"));
					continue;
				}

				// CANONICAL_404 — canonical URL not in crawler index.
				if (!urlToFilename.ContainsKey(canonical)
					&& !urlToFilename.ContainsKey(StripTrailingSlash(canonical)))
				{
					issues.Add(new CanonicalIssue(
						pageUrl,
						"CANONICAL_404",
						canonical,
						$"Canonical target not found in crawler index: {canonical}"));
					continue;
				}

				// CANONICAL_NOINDEX — the canonical names a page kept out of search, while the page
				// itself is not: one signal says "show that page", the other "show none".
				if (searchByUrl is not null
					&& SEO.CanonicalElsewhere(pageUrl, [canonical])
					&& IsNoindex(Lookup(searchByUrl, canonical))
					&& !IsNoindex(Lookup(searchByUrl, pageUrl)))
				{
					issues.Add(new CanonicalIssue(
						pageUrl,
						"CANONICAL_NOINDEX",
						canonical,
						$"Canonical target is kept out of search ({Lookup(searchByUrl, canonical)}): {canonical}"));
					continue;
				}

				// CANONICAL_CHAIN — canonical target itself has a canonical.
				if (canonicalMap.TryGetValue(canonical, out var targetCanonicals)
					&& targetCanonicals.Count > 0
					&& !targetCanonicals[0].Equals(canonical, StringComparison.OrdinalIgnoreCase))
				{
					issues.Add(new CanonicalIssue(
						pageUrl,
						"CANONICAL_CHAIN",
						canonical,
						$"Chain: {pageUrl} → {canonical} → {targetCanonicals[0]}"));
				}
			}

			return issues;
		}

		// ── Log writer ────────────────────────────────────────────────────────

		private static void WriteLog(string csvBasePath, List<CanonicalIssue> issues)
		{
			// [KEEP] Routed through IssueLogWriter — Detail can include canonical
			// URL fragments and crawled page metadata; sanitization protects
			// against pipe / CR / LF / control chars in those values.
			var records = new List<string?[]>
			{
				new string?[] { "PageUrl", "IssueType", "CanonicalUrl", "Detail" }
			};
			records.AddRange(issues
				.OrderBy(i => i.IssueType, StringComparer.Ordinal)
				.ThenBy(i => i.PageUrl, StringComparer.Ordinal)
				.Select(i => new string?[] { i.PageUrl, i.IssueType, i.CanonicalUrl, i.Detail }));
			IssueLogWriter.WriteCsvPair(csvBasePath, records);
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private static string GetHost(string url)
		{
			try { return new Uri(url).Host.ToLowerInvariant(); }
			catch { return string.Empty; }
		}

		private static string StripTrailingSlash(string url) =>
			url.TrimEnd('/');

		private static bool IsNoindex(string? search) => search is SEO.Search.NoindexInPage or SEO.Search.NoindexByHeader;

		/// <summary>The value for an address, a trailing '/' aside; default when there is none.</summary>
		private static T? Lookup<T>(IReadOnlyDictionary<string, T> byUrl, string url)
		{
			if (byUrl.TryGetValue(url, out var value) || byUrl.TryGetValue(StripTrailingSlash(url), out value)
				|| byUrl.TryGetValue(StripTrailingSlash(url) + "/", out value))
			{
				return value;
			}

			return default;
		}

		/// <summary>Column search of log 08 by page; empty when the log is missing or older than the column.</summary>
		internal static IReadOnlyDictionary<string, string> SearchByUrl(string? seoDataPath)
		{
			var byUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrEmpty(seoDataPath) || !File.Exists(seoDataPath))
			{
				return byUrl;
			}

			foreach (var row in Findings.FindingFiles.Csv(seoDataPath, 10, header: true))
			{
				var search = row[9].Trim();
				if (search.Length > 0)
				{
					byUrl.TryAdd(row[0].Trim(), search);
				}
			}

			return byUrl;
		}
	}
}
