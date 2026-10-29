namespace CrawlerSuite.Core.Formats
{
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>One column of an output file.</summary>
	public sealed record OutputColumn(string Name, string Meaning);

	/// <summary>
	/// The description of one output file: what it is for, who writes and reads it, and its
	/// exact shape. This registry is the format contract between the writers and every reader
	/// of the files: the app's reference view shows it, the findings views parse files with it,
	/// and tests check the writers against it. A change here is a format change and is agreed
	/// before it is made.
	/// </summary>
	public sealed record OutputFormat
	{
		/// <summary>Two-digit number, or empty for unnumbered files and folders.</summary>
		public required string Number { get; init; }

		/// <summary>The file name as written (for a CSV pair, both names).</summary>
		public required string FileName { get; init; }

		/// <summary>The file a reader opens (for a CSV pair, the comma version).</summary>
		public required string PrimaryFileName { get; init; }

		/// <summary>Topic the file belongs to.</summary>
		public required string Group { get; init; }

		public required string Title { get; init; }
		public required string Purpose { get; init; }
		public required string WrittenBy { get; init; }
		public required string WrittenWhen { get; init; }
		public required string Delimiter { get; init; }
		public required bool HeaderRow { get; init; }
		public required IReadOnlyList<OutputColumn> Columns { get; init; }
		public required string Encoding { get; init; }
		public required string Quoting { get; init; }
		public required IReadOnlyList<string> ReadBy { get; init; }
		public required string Notes { get; init; }

		/// <summary>The version whose format this entry describes ("1.2.5" is the oldest format recorded).</summary>
		public required string FormatVersion { get; init; }

		/// <summary>True when the entry is a folder rather than a file.</summary>
		public bool IsFolder => FileName.Contains("(folder)", System.StringComparison.Ordinal);

		/// <summary>True for the _semicolon.csv / _comma.csv pairs.</summary>
		public bool IsCsvPair => FileName.Contains("_comma.csv", System.StringComparison.Ordinal);
	}

	/// <summary>All output files of a run, in number order.</summary>
	public static class OutputFormats
	{
		public static IReadOnlyList<OutputFormat> All { get; } = Build();

		/// <summary>The entry for a number ("00".."38"), or null.</summary>
		public static OutputFormat? ByNumber(string number) => All.FirstOrDefault(f => f.Number == number);

		private static List<OutputFormat> Build() =>
		[
			new OutputFormat
			{
				Number = @"00",
				FileName = @"00-crawler.log",
				PrimaryFileName = @"00-crawler.log",
				Group = @"Crawl",
				Title = @"Raw crawl log",
				Purpose = @"Every crawl event as it happened: each URL queued, saved, redirected or failed, plus the end-of-crawl marker. It is the raw record that the settle step turns into 01.",
				WrittenBy = @"CrawlLogWriter.Write / CrawlLogWriter.WriteSaved (CrawlSession during CrawlOrchestrator.Step_PerformMainCrawl and Step_PerformPostCrawlPass)",
				WrittenWhen = @"Appended live during a fresh crawl and during the post-crawl list pass. It is not written on a replay (IsDebugSession). The 'completed' marker is written only when at least one 'saved'/'savedAsset' row exists. A second marker ('completed | post-crawl-pass') is added after the list pass.",
				Delimiter = @" | (pipe with a space on each side)",
				HeaderRow = false,
				Columns =
				[
					new(@"timestamp", @"Local time of the event, yyyy-MM-dd HH:mm:ss"),
					new(@"url", @"URL that was crawled, saved or failed"),
					new(@"status", @"crawled, saved, savedAsset, completed, Oversized, HTTP status name, N/A"),
					new(@"message / filename", @"Saved file name, redirect Location, error text, or 'info'"),
					new(@"source", @"discovery or list; only on saved/savedAsset rows"),
					new(@"contentType", @"Response Content-Type ('n/a' if missing); only on 'saved' page rows")
				],
				Encoding = @"UTF-8 with BOM (Encoding.UTF8 through File.AppendAllText; BOM only when the file is created), CRLF (FileFormat.NewLine)",
				Quoting = @"None and not sanitized: fields are interpolated directly, with no IssueLogWriter",
				ReadBy = [@"CrawlOrchestrator.Step_PerformMainCrawl / IsSavedRow (decides whether to write the completed marker)", @"CrawlOrchestrator.RawCrawlIsComplete (last non-empty line contains 'completed')", @"CrawlOrchestrator.Step_SettleExtensions / ParseRawSavedRow (projects it to 01)", @"CrawlLimitReportBuilder.ReadSavedFiles (report shown when the total download limit is hit)", @"Findings.FindingsCatalog (Results: Crawl › Failed downloads, when 01 is missing)"],
				Notes = @"Rows have different numbers of columns. 'saved' page rows have 6: timestamp | url | saved | <hash>.unverified | discovery|list | contentType. 'savedAsset' rows have 5: timestamp | url | savedAsset | filename | source (no contentType). crawled/redirect/error/Oversized/completed rows have 4: timestamp | url | status | message. Redirect rows hold the .NET status name (e.g. MovedPermanently) and the Location URL. HttpRequestException rows hold the status name (e.g. NotFound) or 'HttpRequestException'/'N/A' and the exception message. Stall rows: 'N/A | No data received for N s — download aborted (stall timeout)'. No-answer rows: 'N/A | No answer within N s — request abandoned after retries (answer timeout)'. Oversized rows: 'Oversized | exceeds the N MB single-file limit, not stored; sample kept as oversized/<name>.sample'. A clean sweep before a replay keeps this file.",
				FormatVersion = @"2.0.0",
			},
			new OutputFormat
			{
				Number = @"01",
				FileName = @"01-crawler.log",
				PrimaryFileName = @"01-crawler.log",
				Group = @"Crawl",
				Title = @"Settled crawl log",
				Purpose = @"The crawl log that analysis reads. Each saved page has its final on-disk file name after HTML/PDF/image classification. Its last line shows whether the crawl finished.",
				WrittenBy = @"CrawlOrchestrator.Step_SettleExtensions (projection of 00); filenames rewritten in place by CrawlOrchestrator.RewriteCrawlerLogFilenames (Step_ReSettleUnverifiedAssets)",
				WrittenWhen = @"Written only after a completed crawl (00 ends in 'completed'). It is not regenerated when it already exists (replay), except on the forced second settle after the post-crawl pass. If the crawl is incomplete, no 01 is produced.",
				Delimiter = @" | (pipe with a space on each side)",
				HeaderRow = false,
				Columns =
				[
					new(@"timestamp", @"Local time of the event, yyyy-MM-dd HH:mm:ss"),
					new(@"url", @"Crawled URL"),
					new(@"status", @"crawled, saved, savedAsset, completed, Oversized, HTTP status name, N/A"),
					new(@"message / filename", @"Final saved file name (saved rows), Location, error text, or 'info'"),
					new(@"source", @"discovery or list; saved/savedAsset rows only")
				],
				Encoding = @"UTF-8 with BOM, CRLF (FileIo.WriteAllLinesWithRetry → FileFormat.WriteAllLines)",
				Quoting = @"None and not sanitized",
				ReadBy = [@"RedirectAnalyzer.AnalyzeRedirects (log 03)", @"CrawlIndex.CreateLookupFile (log 02)", @"CrawlIndex.GetLinesContainingKey via AnalysisPipeline.Step_Process404s (log 06)", @"OversizedFiles.WriteLog (log 34: rows with status 'Oversized')", @"CrawlOrchestrator.Step_SettleExtensions (forced pass: URLs already settled)", @"CrawlOrchestrator.LoadUnverifiedUrlMap / RewriteCrawlerLogFilenames (re-settle)", @"Snapshots/CrawlLog.Analyse / Summarise (snapshot integrity: last line contains 'completed')", @"PreviousCrawlReader.Read / FromLog (previous-crawl file count and duration)", @"CrawlerApp (existence check before analysis)", @"Findings.FindingsCatalog (Results: Crawl › Failed downloads)", @"Links.BrokenLinkOrigins.Find (redirects pointing at a broken address)"],
				Notes = @"'saved' rows are rebuilt as 'timestamp | url | saved | finalName | source', which drops the 6th column (contentType) from 00. finalName is <hash>.html/.pdf/.jpg/... or still <hash>.unverified if quarantined. Every other row (including savedAsset, redirects, errors and the completed marker(s)) is copied from 00 unchanged. A clean sweep before a replay keeps this file.",
				FormatVersion = @"2.0.0",
			},
			new OutputFormat
			{
				Number = @"02",
				FileName = @"02-crawler-index.log",
				PrimaryFileName = @"02-crawler-index.log",
				Group = @"Crawl",
				Title = @"Crawler index (file → URL)",
				Purpose = @"Maps each downloaded, analysable file to the URL it came from and how it was found. Almost every analyzer uses it to turn file names back into URLs.",
				WrittenBy = @"CrawlIndex.CreateLookupFile (CrawlOrchestrator.Step_CreateLookupFileFirstPass; rebuilt in Step_PerformPostCrawlPass)",
				WrittenWhen = @"Every run from 01 (including replays). It is rebuilt a second time after the post-crawl list pass.",
				Delimiter = @"| (bare pipe, no spaces)",
				HeaderRow = false,
				Columns =
				[
					new(@"filename", @"Downloaded file name in download/"),
					new(@"url", @"Source URL of that file"),
					new(@"source", @"discovery (link crawl) or list (post-crawl pass)")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None and not sanitized",
				ReadBy = [@"Urls/Cache.Load (AnalysisPipeline.Step_LoadUrlCache → CrawlIndex.LookUpUrlForFile/LookUpSourceForFile)", @"CanonicalAnalyzer.Analyse", @"Downloader/OrphanFiles.Detect (CrawlOrchestrator.CrawlerIndexIntegrityCheck)", @"Content.CompareCrawlAndContent (log 05)", @"Links.BrokenLinkOrigins.Find (saved file → address)", @"Triage.TriageSource.Read (saved file of a page)"],
				Notes = @"Built only from 01 rows whose status is exactly 'saved' or 'savedAsset'. Files ending .unverified are excluded. Lines are sorted (ordinal) so the output is deterministic. The source column is optional for readers; it is written whenever 01 has a source.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"03",
				FileName = @"03-redirect-analysis.log",
				PrimaryFileName = @"03-redirect-analysis.log",
				Group = @"Links & redirects",
				Title = @"Redirect chains",
				Purpose = @"Each redirect the crawler hit, followed up to three hops, so the operator can see where it ends: a page, a 404, a loop or another redirect.",
				WrittenBy = @"RedirectAnalyzer.AnalyzeRedirects (CrawlOrchestrator.Step_AnalyseRedirects)",
				WrittenWhen = @"Always, every run (including replay). Empty when there were no redirects.",
				Delimiter = @" | (pipe with a space on each side)",
				HeaderRow = false,
				Columns =
				[
					new(@"url", @"URL that answered with a redirect"),
					new(@"status", @".NET redirect status name, e.g. MovedPermanently, Found"),
					new(@"followup", @"'<Location> > <hop1> -> <hop2> ...' chain, max 3 hops")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"Sanitized, not quoted: control characters, CR/LF and bidi are stripped using a sentinel delimiter, so pipes are NOT replaced",
				ReadBy = [@"Findings.FindingsCatalog (Results: Links › Redirect chains)"],
				Notes = @"Each hop is '<status> <url>'. A hop can end in '> (Loop)', '> (NotFound)' (target never crawled) or '> (IgnoredVariableInfo)'. Only 301/302/303/307/308 count (MovedPermanently, Moved, Found, Redirect, SeeOther, RedirectMethod, TemporaryRedirect, RedirectKeepVerb, PermanentRedirect). Rows whose message starts 'Response status code does not indicate success' are skipped. Cleared before a debug replay.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"04",
				FileName = @"04-full-content.log",
				PrimaryFileName = @"04-full-content.log",
				Group = @"Crawl",
				Title = @"CMS content list (expected pages)",
				Purpose = @"The list of page paths taken from the CMS export CSV: what the site should contain, for comparison with what the crawl found.",
				WrittenBy = @"Content.Listing (CrawlOrchestrator.Step_WriteContentListLogs)",
				WrittenWhen = @"Only when config.CmsContentList.Path is set and the CSV exists. It is written on the first crawl pass and is not regenerated by the post-crawl pass.",
				Delimiter = @"none (one value per line)",
				HeaderRow = false,
				Columns =
				[
					new(@"path", @"Value from the configured CSV column (prefix-replaced, suffix added)")
				],
				Encoding = @"UTF-8 with BOM, CRLF (FileIo.WriteList → FileFormat.WriteAllLines)",
				Quoting = @"None",
				ReadBy = [@"Content.CompareCrawlAndContent (log 05)"],
				Notes = @"A CSV row is kept when it contains RowFilter, matches none of the '|'-separated RowNegativeFilter values, and its column value contains none of the active RowsToExclude (an entry written {""Value"": …, ""Enabled"": false} is kept in the file, not applied, and still counted in settings-usage.json). With ValuePrefixReplace on, RowFilter text is removed and ValueSuffix appended. With OnlineColumn and OnlinePattern set, a row is listed only when its value in that column (found by name in the header row after SkipRows) matches the pattern; the others go to 39. Cleared before a debug replay.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"05",
				FileName = @"05-not-directly-crawlable.log",
				PrimaryFileName = @"05-not-directly-crawlable.log",
				Group = @"Crawl",
				Title = @"Pages not reachable by links",
				Purpose = @"CMS content-list entries that the link crawl never reached, such as orphaned pages with no inbound links. It is also the input list for the optional post-crawl download pass.",
				WrittenBy = @"Content.CompareCrawlAndContent (CrawlOrchestrator.Step_WriteContentListLogs)",
				WrittenWhen = @"Only when CmsContentList is configured and its CSV exists (same condition as 04).",
				Delimiter = @"none (one value per line)",
				HeaderRow = false,
				Columns =
				[
					new(@"path", @"Entry from 04 with no matching URL in 02")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [@"CrawlOrchestrator.Step_PerformPostCrawlPass (lines starting with '/' are prefixed with config.Url and downloaded with source 'list')", @"CrawlOrchestrator.ShouldSkipPostCrawlPass (existence)", @"Findings.FindingsCatalog (Results: Crawl › Not directly discoverable)", @"Links.BrokenLinkOrigins.Find (broken addresses on the content list)"],
				Notes = @"Compared against 02's URL column after removing the config.Url prefix. Cleared before a debug replay.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"06",
				FileName = @"06-404.log",
				PrimaryFileName = @"06-404.log",
				Group = @"Links & redirects",
				Title = @"Broken URLs (404)",
				Purpose = @"Every crawled URL that returned 404 Not Found.",
				WrittenBy = @"AnalysisPipeline.Step_Process404s (the 01 rows whose status is NotFound)",
				WrittenWhen = @"Always (empty when there are no 404s).",
				Delimiter = @"none (one URL per line)",
				HeaderRow = false,
				Columns =
				[
					new(@"url", @"URL whose 01 status column is NotFound; ':443' removed")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [@"Findings.FindingsCatalog (Results: Links › 404 answers)"],
				Notes = @"Only the status column counts (an address holding the word is not a 404). Each address once, sorted. Cleared before a debug replay.",
				FormatVersion = @"1.3.0",
			},
			new OutputFormat
			{
				Number = @"07",
				FileName = @"07-404-sources_semicolon.csv / 07-404-sources_comma.csv (base 07-404-sources)",
				PrimaryFileName = @"07-404-sources_comma.csv",
				Group = @"Links & redirects",
				Title = @"Where each 404 comes from",
				Purpose = @"For each broken URL, every place that references it — page, stylesheet, redirect or content list — how, as written, and for a stylesheet reference when a browser requests it; so the fix is made where the reference is.",
				WrittenBy = @"Logging/ErrorSourcesLog.Write via IssueLogWriter.WriteCsvPair (AnalysisPipeline.Step_Process404s)",
				WrittenWhen = @"Every analysis; the header alone when 06 is empty (removed before a debug replay).",
				Delimiter = @"';' in _semicolon.csv, ',' in _comma.csv (CsvSemicolonSuffix='_semicolon.csv' → ';', CsvCommaSuffix='_comma.csv' → ',')",
				HeaderRow = true,
				Columns =
				[
					new(@"BrokenUrl", @"The full broken URL, as in 06"),
					new(@"Kind", @"link · image · canonical · redirect · script · link element · data attribute · form action · inline data · stylesheet url() · content list · no reference found"),
					new(@"Referrer", @"The page, stylesheet or redirecting URL that references it (empty when none was found)"),
					new(@"AsWritten", @"The reference exactly as it stands in the source"),
					new(@"Grade", @"Stylesheet references: fires on load · fires on matching devices · fires in a state · fires when printed · can fire if a script adds the class · dead reference · undecided; else canonical, relative link or empty"),
					new(@"Evidence", @"What the grade rests on: the rule and the page it matched, the script naming a class, the class nobody names, the page folder of a relative link, a stylesheet deployed twice"),
					new(@"Reach", @"Who runs into it: Every page view · When opened (before 2.0.136: When a visitor clicks) · Only if a script acts · Reach undecided · Never requested · Error elsewhere on site · Origin unknown (from Kind and Grade; a link element by its rel)")
				],
				Encoding = @"UTF-8 with BOM, CRLF, no 'sep=' line",
				Quoting = @"RFC-4180: a field is quoted when it contains the delimiter or '""'; formula-trigger values (= + - @ TAB CR at start) get a leading apostrophe; control characters, CR/LF and bidi are stripped",
				ReadBy = [@"Findings.FindingsCatalog (Results: Links › Broken links; Crawl › Failed downloads, linked from)", @"Triage.BrokenLinkSource.Read (Triage › Broken links)"],
				Notes = @"Found after the crawl, without a request: the crawler's discovery runs again over the saved pages and stylesheets, the redirects of 01 and the list 05; references resolve as the crawler resolved them (pages without their query). One row per reference. Scripts are not run. A stylesheet reference is graded (fires on load, fires on matching devices, fires in a state, fires when printed, can fire if a script adds the class, dead reference, undecided), the evidence naming the state or condition; the Reach column (last) says who runs into the address (Every page view, When opened, Only if a script acts, Reach undecided, Never requested, Error elsewhere on site, Origin unknown). A link element with rel=alternate reaches Error elsewhere on site. Readers take the former reach name 'When a visitor clicks' as 'When opened'.",
				FormatVersion = @"2.3.0",
			},
			new OutputFormat
			{
				Number = @"08",
				FileName = @"08-seo-data.csv",
				PrimaryFileName = @"08-seo-data.csv",
				Group = @"SEO",
				Title = @"SEO metadata per page",
				Purpose = @"Title, description, robots and keywords meta tags, with lengths and H1 count, for every downloaded page.",
				WrittenBy = @"SEO.ExtractDataAsync (AnalysisPipeline.Step_ExtractSeoData)",
				WrittenWhen = @"Every run. The 'skip if cached on debug' branch never triggers in practice, because Step_ClearDebugLogs deletes the file before a debug replay. Header only when there are no pages.",
				Delimiter = @"; (semicolon only, single file, not a pair). Note that IssueLogWriter.SeoDelimiter '@@@' is not used by this writer.",
				HeaderRow = true,
				Columns =
				[
					new(@"url", @"Page URL from index ('error' if unindexed)"),
					new(@"robots", @"meta name=robots content"),
					new(@"title", @"<title> text"),
					new(@"title_length", @"Character count of the title"),
					new(@"description", @"meta name=description content"),
					new(@"description_length", @"Character count of the description"),
					new(@"keywords", @"meta name=keywords content"),
					new(@"source", @"discovery or list (crawl source)"),
					new(@"h1_count", @"Number of <h1> elements on the page"),
					new(@"search", @"Whether the page may appear in search: in search · noindex in page · noindex by header (X-Robots-Tag of the .header sidecar) · canonical elsewhere (the first canonical names another address); robots.txt is not read"),
					new(@"canonical", @"The address the first canonical tag names, entity-decoded and resolved against the page; empty without one"),
					new(@"h1", @"The texts of the <h1> elements in document order, as a reader sees them (entities decoded, white space collapsed), separated by ' ¦ ' (the character is taken out of the texts)")
				],
				Encoding = @"UTF-8 with BOM (IssueLogWriter.Utf8WithBom StreamWriter), CRLF, no 'sep=' line",
				Quoting = @"RFC-4180 via ComposeCsvLine(';'), with formula-injection apostrophe",
				ReadBy = [@"AnalysisPipeline.CountDataLines (run summary row count only)", @"Findings.FindingsCatalog (Results: SEO › SEO data)", @"Triage.SeoSource (Triage › Search appearance and Main heading: pages in search only)", @"CanonicalAnalyzer.SearchByUrl (log 16: a canonical naming a page out of search)"],
				Notes = @"Pages are files matching config.FilePattern in download/. Text is decoded with DetectEncoding.FromBytes. Title and description are written as found (entities not decoded; the lengths count them as written). The last three columns are search, canonical and h1.",
				FormatVersion = @"2.0.125",
			},
			new OutputFormat
			{
				Number = @"09",
				FileName = @"09-self-link-analysis_semicolon.csv / 09-self-link-analysis_comma.csv (base 09-self-link-analysis)",
				PrimaryFileName = @"09-self-link-analysis_comma.csv",
				Group = @"Links & redirects",
				Title = @"Self-linking pages",
				Purpose = @"Links in page content (boilerplate removed) that point back to the same page, a typical CMS authoring workaround.",
				WrittenBy = @"SelfLinkScanner.FindSelfLinks via IssueLogWriter.WriteCsvPair (AnalysisPipeline.Step_ScanSelfLinks)",
				WrittenWhen = @"Always after the boilerplate prune. Header only when none are found.",
				Delimiter = @"';' in _semicolon.csv, ',' in _comma.csv",
				HeaderRow = true,
				Columns =
				[
					new(@"File", @"Pruned page file name"),
					new(@"FileUrl", @"URL of that page"),
					new(@"LinkFound", @"Raw href value (entity-decoded) pointing to itself"),
					new(@"ContextSnippet", @"About 240 characters of surrounding HTML")
				],
				Encoding = @"UTF-8 with BOM, CRLF, no 'sep=' line",
				Quoting = @"RFC-4180 quoted (pair writer), with formula-injection apostrophe",
				ReadBy = [@"Findings.FindingsCatalog (Results: Links › Self-links)", @"Triage.SelfLinkSource.Read (triage: one item per page and link)"],
				Notes = @"Scans the pruned/ tree, so self-links in navigation and other chrome are excluded. On a reference page, where the prune keeps the template so it is checked there once, links inside the parts the group's selectors name are left out too (the home page's logo leads to the home page by design). Hrefs with '#' or an ignored query key (QueryStringsToIgnoreForSelfLinkDetermination) are skipped. A link whose query hands the server a full address (a forwarding link) is never a self-link here; it goes to 40. The context radius is 200 characters and the snippet is capped at 240. Rows are sorted ordinally by all fields. Cleared before a debug replay.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"10",
				FileName = @"10-content-quality-issues.log",
				PrimaryFileName = @"10-content-quality-issues.log",
				Group = @"Content quality",
				Title = @"Content quality issues (editor)",
				Purpose = @"Typography, markup and authoring defects that editors can fix: ligatures, quote problems, anchor defects, unwanted patterns, malformed HTML and similar.",
				WrittenBy = @"ContentQuality.WriteLog (ContentQuality.Analyse, AnalysisPipeline.Step_RunContentQualityChecks)",
				WrittenWhen = @"When ContentQuality is enabled or ContentUnwantedPatterns are configured. Header only when nothing is found or everything is suppressed. Not written when both are off.",
				Delimiter = @"| (bare pipe)",
				HeaderRow = true,
				Columns =
				[
					new(@"Filename", @"Downloaded/pruned file name (not URL)"),
					new(@"IssueType", @"e.g. LIGATURE, QUOTE_UNMATCHED, UNWANTED_PATTERN, MALFORMED_HTML"),
					new(@"Detail", @"What was found (character, pattern, code, count)"),
					new(@"Context", @"Excerpt of the surrounding text/markup")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"Sanitized, not quoted: '|' in a field becomes '/', CR/LF/TAB become a space, control/bidi/zero-width characters are stripped",
				ReadBy = [@"Findings.FindingsCatalog (Results: Content quality)", @"Triage.TriageSource.Read (Triage: content quality)"],
				Notes = @"Rows are sorted by Filename, IssueType, Detail, Context, after operator ContentQualityIssueSuppressions are applied. Pass 1 (raw download/) adds UNWANTED_PATTERN, CONTENT_BEFORE_DOCTYPE and MALFORMED_HTML:<code>. Pass 2 (pruned/) adds LIGATURE, LANGUAGE_MISMATCH, CONTROL_CHARS_IN_CONTENT, LINE_BREAK_GLUE_*, DECOMPOSED/MIXED_NORMALIZATION, QUOTE_*, BARE_TEXT_IN_CONTAINER, WORD_COLLISION, SPACE_AFTER_HYPHEN and SPACE_BEFORE_HYPHEN (their Detail names the word next to the two at the hyphen, e.g. “Xyzzy- und Hausverband”; their Context is the page's HTML around the spot, from the block it stands in, on one line; their Detail names the diagnosis — “/” or a joining word missing, stray space, space at the slash — or stays “space after/before the hyphen” when the dictionaries cannot tell), SPLIT_WORD_ANCHOR, MISPLACED_ANCHOR_EMPTY, ADJACENT_ANCHOR, and ARIA_HIDDEN_FOCUSABLE and ARIA_REFERENCE_BROKEN. MISPLACED_ANCHOR_EMPTY follows the W3C ACT rules (links without an accessible name only; anchors without href, hidden and named links are not listed; image links without alt are); its Detail says why, ending with the element, e.g. 'No accessible name — <a href=""/x"">'. The Detail of QUOTE_SYSTEM_MIX ends with the page language it was judged by, e.g. “(page language: de)”, when the page declares exactly one; the screen blames the quotation that does not fit it, and blames none without it. ContentQuality.WriteTranslationIssues could append POTENTIAL_TRANSLATION rows, but nothing calls it. Architect-class defects go to 22 instead.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"11",
				FileName = @"11-spell-errors-unique.log",
				PrimaryFileName = @"11-spell-errors-unique.log",
				Group = @"Spelling",
				Title = @"Unique misspellings",
				Purpose = @"Each distinct flagged word with its language(s), listed once for the whole site: the 'what to fix' list.",
				WrittenBy = @"NewSpellEngineRunner.WriteViews → ParallelStoreWriter.UniqueView (AnalysisPipeline.Step_RunSpellCheck)",
				WrittenWhen = @"Every run where dictionaries load and the spell pass completes. Empty file (BOM only) when there are no findings. Not written if the spell engine throws.",
				Delimiter = @"free text: 'word (langs)', optionally ' · valid lang, lang'",
				HeaderRow = false,
				Columns =
				[
					new(@"word (languages)", @"Flagged word plus comma-joined language codes in parentheses"),
					new(@" · valid languages", @"Optional: the other reference dictionaries (the English flavours and the default language) that know the word. Information only; the word stays flagged")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [@"Lexicon.SpellWordList.Read (Spell findings page: the words)", @"Findings.FindingsCatalog (Results: Spelling)", @"Triage.SpellingSource.Read (triage: valid-in of a spelling item)"],
				Notes = @"De-duplicated by the exact line text, sorted case-insensitively (invariant culture). Cleared before a debug replay. Readers split the suffix off with ParallelStoreWriter.SplitValidIn.",
				FormatVersion = @"1.3.0",
			},
			new OutputFormat
			{
				Number = @"12",
				FileName = @"12-spell-errors-sources.log",
				PrimaryFileName = @"12-spell-errors-sources.log",
				Group = @"Spelling",
				Title = @"Misspellings by page",
				Purpose = @"Which page each flagged word appears on, with meta-tag provenance when the word came from a meta tag.",
				WrittenBy = @"NewSpellEngineRunner.WriteViews → ParallelStoreWriter.SourcesView (Step_RunSpellCheck)",
				WrittenWhen = @"Same as 11.",
				Delimiter = @"| (bare pipe, one per line)",
				HeaderRow = false,
				Columns =
				[
					new(@"url", @"Page URL ('error' if file not in index)"),
					new(@"word (languages)[ (metaPath)]", @"Flagged word, languages, meta source path if from meta")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None and not sanitized",
				ReadBy = [@"Lexicon.SpellWordList.Read (Spell findings page: pages per word)", @"Triage.SpellingSource.Read (triage: one spelling item per word per page)", @"Findings.FindingsCatalog (Results: Spelling › By page)"],
				Notes = @"One line per ticket, that is, per distinct word per page (case-insensitive), in document order. The meta suffix is the SourcePath of the first Meta occurrence, e.g. meta[@name=description]. Cleared before a debug replay.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"13",
				FileName = @"13-spell-errors-source-location.log",
				PrimaryFileName = @"13-spell-errors-source-location.log",
				Group = @"Spelling",
				Title = @"Misspellings with exact location",
				Purpose = @"Every occurrence of every flagged word with its DOM location and an excerpt, so the operator can find it on the page.",
				WrittenBy = @"NewSpellEngineRunner.WriteViews → ParallelStoreWriter.LocatedView (Step_RunSpellCheck)",
				WrittenWhen = @"Same as 11.",
				Delimiter = @"free text, multi-line blocks",
				HeaderRow = false,
				Columns =
				[
					new(@"block header: 'word (langs) @ url'", @"Flagged word, its languages, and the page"),
					new(@"occurrence line: '    sourcePath  excerpt'", @"4-space indent; DOM path (e.g. p[#text]), 2 spaces, excerpt")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [@"Triage.SpellingSource.Read (triage: the excerpts of a spelling item)", @"Findings.FindingsCatalog (Results: Spelling › Exact location)"],
				Notes = @"One block per (word, page). No blank line between blocks. Cleared before a debug replay.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"14",
				FileName = @"14-word-tickets.log",
				PrimaryFileName = @"14-word-tickets.log",
				Group = @"Spelling",
				Title = @"Spell-engine ticket dump",
				Purpose = @"A full diagnostic record of everything the spell engine flagged and where, including each occurrence's source type.",
				WrittenBy = @"NewSpellEngineRunner.WriteViews → WordTicketsDiagnostic.Compose (Step_RunSpellCheck)",
				WrittenWhen = @"Same as 11 (empty when there are no tickets).",
				Delimiter = @"free text, multi-line blocks",
				HeaderRow = false,
				Columns =
				[
					new(@"block header: 'word (langs) @ url'", @"Flagged word, its languages, and the page"),
					new(@"occurrence line: '  [Source] sourcePath  excerpt'", @"Source is TextNode, Attribute, Meta or Script; then path and excerpt")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None (written exactly as produced, not sanitized)",
				ReadBy = [],
				Notes = @"One blank line between ticket blocks. Script-source tickets from the JS-file scan are added to the in-memory tickets later, but are NOT written here. Cleared before a debug replay.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"15",
				FileName = @"15-user-dictionary-orphan-words.log",
				PrimaryFileName = @"15-user-dictionary-orphan-words.log",
				Group = @"Spelling",
				Title = @"Unused / redundant dictionary words",
				Purpose = @"Words in the user and site dictionaries that no crawled page used, or that prefix stripping already covers: candidates to remove.",
				WrittenBy = @"Lexicon.Audit.WriteAnalysisReport → WriteAnalysisLog (AnalysisPipeline.Step_MaintainDictionaries)",
				WrittenWhen = @"Only when DictionaryMaintenance.Mode is Report or Interactive, the spell pass completed, and UpdateUserDictionary or UpdateSiteSpecificDictionary is true. Empty (BOM only) when nothing is flagged.",
				Delimiter = @"none (one word per line, in sections)",
				HeaderRow = false,
				Columns =
				[
					new(@"word", @"Dictionary entry flagged in the section above it")
				],
				Encoding = @"UTF-8 with BOM (StreamWriter with Encoding.UTF8), CRLF",
				Quoting = @"None",
				ReadBy = [@"Lexicon.DictionaryReport.Read (Dictionaries page: not used, redundant)", @"Findings.FindingsCatalog (Results: Spelling › Dictionary orphans)"],
				Notes = @"Contains up to 6 sections, each starting with a '// ' comment line. User dictionary: not found in any page; user dictionary: redundant; site dictionary: not found; site dictionary: redundant; in BOTH: not found; in BOTH: redundant. Empty sections are left out. Sections are separated by a blank line. Words are sorted case-insensitively. This version only reports: dictionaries are not modified. Cleared before a debug replay.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"16",
				FileName = @"16-canonical-issues_semicolon.csv / 16-canonical-issues_comma.csv (base 16-canonical-issues)",
				PrimaryFileName = @"16-canonical-issues_comma.csv",
				Group = @"SEO",
				Title = @"Canonical link issues",
				Purpose = @"Problems with <link rel=canonical>: target not crawled, chains, multiple tags, or a target outside the allowed hosts.",
				WrittenBy = @"CanonicalAnalyzer.WriteLog via IssueLogWriter.WriteCsvPair (AnalysisPipeline.Step_AnalyseCanonicals)",
				WrittenWhen = @"Always. Header only when there are no issues.",
				Delimiter = @"';' in _semicolon.csv, ',' in _comma.csv",
				HeaderRow = true,
				Columns =
				[
					new(@"PageUrl", @"Page declaring the canonical"),
					new(@"IssueType", @"CANONICAL_404, CANONICAL_REDIRECT, CANONICAL_NOINDEX, CANONICAL_CHAIN, CANONICAL_CONFLICT, CANONICAL_EXTERNAL"),
					new(@"CanonicalUrl", @"First (effective) canonical URL"),
					new(@"Detail", @"Explanation, e.g. chain A → B → C or tag count")
				],
				Encoding = @"UTF-8 with BOM, CRLF, no 'sep=' line",
				Quoting = @"RFC-4180 quoted (pair writer), with formula-injection apostrophe",
				ReadBy = [@"Findings.FindingsCatalog (Results: SEO › Canonical issues)", @"Triage.CanonicalSource.Read (triage: chains, several tags, a target that redirects or is out of search; a missing target and another host left out)"],
				Notes = @"Sorted by IssueType, then PageUrl. Only pages listed in 02 are scanned. Relative hrefs are resolved against the page URL. EXTERNAL means the host is outside config.Url plus UrlSubdomainsAllowed; EXTERNAL rows are logged but not returned for tracking. Issue types include REDIRECT (the target is a redirect source in 03; checked before 404) and NOINDEX (the target is noindex in page or by header in 08, the page is not).",
				FormatVersion = @"2.0.125",
			},
			new OutputFormat
			{
				Number = @"17",
				FileName = @"17-pdf-quality_semicolon.csv / 17-pdf-quality_comma.csv (base 17-pdf-quality)",
				PrimaryFileName = @"17-pdf-quality_comma.csv",
				Group = @"PDF",
				Title = @"PDF metadata & accessibility",
				Purpose = @"One row per downloaded PDF showing which metadata and accessibility features (title, language, tags, PDF/A, PDF/UA and others) are present.",
				WrittenBy = @"PdfQualityAnalyzer.WriteLog via IssueLogWriter.WriteCsvPair (AnalysisPipeline.Step_AnalysePdfQuality)",
				WrittenWhen = @"Whenever download/ exists. Header only when there are no *.pdf files. Not written if download/ is missing.",
				Delimiter = @"';' in _semicolon.csv, ',' in _comma.csv",
				HeaderRow = true,
				Columns =
				[
					new(@"PageUrl", @"PDF URL (file name if not indexed)"),
					new(@"Title", @"dc:title or /Title, or 'n/a'"),
					new(@"Description", @"dc:description or /Subject, or 'n/a'"),
					new(@"Keywords", @"dc:subject, pdf:Keywords or /Keywords, or 'n/a'"),
					new(@"Language", @"dc:language or catalog /Lang, or 'n/a'"),
					new(@"Tags", @"1 = /Marked true, -1 = untagged"),
					new(@"PdfA", @"1 = pdfaid part/conformance present, -1 = absent"),
					new(@"StructTree", @"1 = structure tree present, -1 = absent"),
					new(@"RoleMap", @"1 = RoleMap present, -1 = absent (information only: needed for tag names of the PDF's own, so no gap)"),
					new(@"Outlines", @"1 = bookmarks present, -1 = absent"),
					new(@"AltText", @"1 = some /Alt found, -1 = none"),
					new(@"FormFields", @"-1 no AcroForm, 0 unnamed fields, 1 named (/TU)"),
					new(@"PdfUA", @"1 = pdfuaid:part present, -1 = absent"),
					new(@"Protection", @"none; restricted (opens for everyone, accessibility extraction allowed); accessibility blocked (PDF/UA-1 7.16: gap PDF_ACCESSIBILITY_BLOCKED); password to open (gap PDF_PASSWORD_TO_OPEN). Empty in logs written before 2.0.147.")
				],
				Encoding = @"UTF-8 with BOM, CRLF, no 'sep=' line",
				Quoting = @"RFC-4180 quoted. Because of the formula-injection guard, every '-1' flag is written as ""'-1"" (leading apostrophe).",
				ReadBy = [@"Findings.FindingsCatalog (Results: Assets › PDF quality)"],
				Notes = @"Sorted by PageUrl. Scans download/*.pdf at the top level only (settled PDFs). Replaced the former '@@@' log.",
				FormatVersion = @"2.0.147",
			},
			new OutputFormat
			{
				Number = @"18",
				FileName = @"18-pdf-remediation_semicolon.csv / 18-pdf-remediation_comma.csv (base 18-pdf-remediation)",
				PrimaryFileName = @"18-pdf-remediation_comma.csv",
				Group = @"PDF",
				Title = @"PDF remediation plan",
				Purpose = @"A to-do list with one row per gap per PDF, ordered from easy fixes to full PDF/UA, with effort and a suggested action.",
				WrittenBy = @"PdfQualityAnalyzer.WriteRemediationLog via IssueLogWriter.WriteCsvPair (Step_AnalysePdfQuality)",
				WrittenWhen = @"Only when at least one PDF exists in download/. Header only when no PDF has gaps. Not written when there are no PDFs, so a stale file can remain.",
				Delimiter = @"';' in _semicolon.csv, ',' in _comma.csv",
				HeaderRow = true,
				Columns =
				[
					new(@"PageUrl", @"PDF URL (file name if not indexed)"),
					new(@"Priority", @"0 (protection, fix first) to 12 (PDF/UA, last)"),
					new(@"Effort", @"low, medium or high"),
					new(@"Gap", @"PDF_PASSWORD_TO_OPEN, PDF_ACCESSIBILITY_BLOCKED, PDF_NO_LANGUAGE, PDF_NO_TITLE, ..., PDF_NO_PDFUA"),
					new(@"Action", @"Suggested fix in plain words")
				],
				Encoding = @"UTF-8 with BOM, CRLF, no 'sep=' line",
				Quoting = @"RFC-4180 quoted (pair writer), with formula-injection apostrophe",
				ReadBy = [@"Findings.FindingsCatalog (Results: Assets › PDF remediation)"],
				Notes = @"Fixed priority ladder: 0 PASSWORD_TO_OPEN and ACCESSIBILITY_BLOCKED, 1 LANGUAGE, 2 TITLE, 3 DESCRIPTION, 4 KEYWORDS, 5 OUTLINES, 6 TAGS, 7 PDFA, 8 ALTTEXT, 9 STRUCTTREE, 10 ROLEMAP, 11 FORMFIELD_NAMES, 12 PDFUA. PDFs are sorted by URL, and rows within a PDF by priority.",
				FormatVersion = @"2.0.147",
			},
			new OutputFormat
			{
				Number = @"19",
				FileName = @"19-base64assets.log",
				PrimaryFileName = @"19-base64assets.log",
				Group = @"Assets",
				Title = @"Embedded Base64 assets",
				Purpose = @"Every Base64 data-URI found inside JS/CSS (or other configured) files: decoded size, type, and where it was saved for review.",
				WrittenBy = @"Base64AssetExtractor.WriteLog (AnalysisPipeline.Step_ExtractBase64Assets)",
				WrittenWhen = @"Only when Base64AssetFileExtensions is non-empty and download/ exists. Header only when no matching files or no assets are found.",
				Delimiter = @"@@@",
				HeaderRow = true,
				Columns =
				[
					new(@"SourceFileUrl", @"URL of the file containing the data URI"),
					new(@"SavedFile", @"Decoded file name in base64assets/"),
					new(@"MediaType", @"Declared MIME type or 'unknown'"),
					new(@"EncodedBytes", @"Length of the Base64 text"),
					new(@"DecodedBytes", @"Size of the decoded asset in bytes")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None and not sanitized (raw interpolation)",
				ReadBy = [@"ResourceBloatAnalyzer.LoadBase64Log (log 20)", @"Findings.FindingsCatalog (Results: Assets › Base64 assets)"],
				Notes = @"Only base64 data URIs with at least 64 encoded characters are included. Input files are read as Latin-1 (top-level download/ only), sorted by path. Assets of BloatThresholdBase64Kilobytes or more are returned as BASE64_LARGE_ASSET issues.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"20",
				FileName = @"20-resource-bloat.log",
				PrimaryFileName = @"20-resource-bloat.log",
				Group = @"Resource bloat",
				Title = @"Resource bloat per page",
				Purpose = @"For each page with a problem: how much JS/CSS it loads, inlined Base64 assets, CSS injected from JS, and JSON blobs in the HTML. The worst pages come first.",
				WrittenBy = @"ResourceBloatAnalyzer.Analyse (AnalysisPipeline.Step_AnalyseResourceBloat)",
				WrittenWhen = @"Whenever download/ exists (every run). Header only when no page has issues.",
				Delimiter = @"@@@ (the Base64LargeAssets and Issues columns use '|' inside)",
				HeaderRow = true,
				Columns =
				[
					new(@"PageUrl", @"Page URL"),
					new(@"JSCSSFileCount", @"Downloaded JS/CSS files the page references"),
					new(@"JSCSSTotalBytes", @"Total bytes of those files"),
					new(@"JSFileSizeBytes", @"Bytes of referenced JS"),
					new(@"CSSFileSizeBytes", @"Bytes of referenced CSS"),
					new(@"Base64AssetCount", @"Base64 assets inside those files (from log 19)"),
					new(@"Base64TotalDecodedBytes", @"Their total decoded size"),
					new(@"Base64LargeAssets", @"Up to 5 'name(NKB)' joined by '|', '+N more', or 'none'"),
					new(@"InlinedCSSBytes", @"CSS injected through JS .textContent=`...`"),
					new(@"JSONBlobCount", @"data='{...}' JSON attributes in the HTML"),
					new(@"JSONBlobTotalBytes", @"Their total size"),
					new(@"Issues", @"'|'-joined: OVERSIZED_JS/CSS, INLINED_CSS, BASE64_LARGE, JSON_BLOBS")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None and not sanitized",
				ReadBy = [@"ResourceBloatBaselineAnalyzer.LoadLog20 (log 21)", @"Findings.FindingsCatalog (Results: Assets › Resource bloat)"],
				Notes = @"Rows are sorted by JSCSSTotalBytes, descending. The class comment lists 10 columns, but the real header has 12 (it adds JSFileSizeBytes and CSSFileSizeBytes). Pages are *.html, *.htmlx or the configured extension; pages not in the index are skipped. OVERSIZED thresholds come from BloatThresholdJs/CssKilobytes.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"21",
				FileName = @"21-resource-bloat-above-baseline.log",
				PrimaryFileName = @"21-resource-bloat-above-baseline.log",
				Group = @"Resource bloat",
				Title = @"Bloat above site baseline",
				Purpose = @"A summary view: first the JS/CSS payload every page carries (BASELINE), then only the pages that load meaningfully more than that.",
				WrittenBy = @"ResourceBloatBaselineAnalyzer.Analyse (AnalysisPipeline.Step_AnalyseResourceBloatBaseline)",
				WrittenWhen = @"Only when log 20 exists and has at least one data row. Otherwise it is skipped, and a stale file is not removed.",
				Delimiter = @"@@@ (the Base64LargeAssets and Issues columns use '|' inside)",
				HeaderRow = true,
				Columns =
				[
					new(@"PageUrl", @"Page URL, or 'BASELINE' on the first data row"),
					new(@"DeltaJSCSSFileCount", @"Extra JS/CSS files vs baseline (absolute on BASELINE)"),
					new(@"DeltaJSCSSTotalBytes", @"Extra JS+CSS bytes vs baseline"),
					new(@"DeltaJSBytes", @"Extra JS bytes vs baseline"),
					new(@"DeltaCSSBytes", @"Extra CSS bytes vs baseline"),
					new(@"DeltaBase64Count", @"Extra Base64 assets vs baseline"),
					new(@"DeltaBase64TotalBytes", @"Extra Base64 decoded bytes vs baseline"),
					new(@"Base64LargeAssets", @"From log 20; on BASELINE: '|'-list of baseline files"),
					new(@"DeltaInlinedCSSBytes", @"Extra inlined-CSS bytes vs baseline"),
					new(@"JSONBlobCount", @"JSON blobs on the page (0 on BASELINE)"),
					new(@"JSONBlobTotalBytes", @"JSON blob bytes (0 on BASELINE)"),
					new(@"Issues", @"BASE64_LARGE, INLINED_CSS, JSON_BLOBS, EXTRA_JS/CSS, ABOVE_BASELINE, SITEWIDE")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None and not sanitized",
				ReadBy = [@"Findings.FindingsCatalog (Results: Assets › Above baseline)"],
				Notes = @"The baseline row comes from the log-20 row with the smallest JSCSSTotalBytes. Baseline files are those referenced by at least 90% of pages. A page is listed when its delta is at least BloatThresholdAboveBaselineKilobytes, or it has BASE64_LARGE/INLINED_CSS/JSON_BLOBS, and its JS/CSS delta passes the threshold or it carries one of those flags. Rows are sorted by delta, descending. OVERSIZED_* flags are dropped. The class comment lists 10 columns; the header has 12.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"22",
				FileName = @"22-cms-template-authoring-defects_semicolon.csv / 22-cms-template-authoring-defects_comma.csv (base 22-cms-template-authoring-defects)",
				PrimaryFileName = @"22-cms-template-authoring-defects_comma.csv",
				Group = @"Content quality",
				Title = @"CMS template defects (architect)",
				Purpose = @"Defects that come from CMS templates rather than editors, such as embedded BOMs or invisible characters in the body. These go to the template/architect team.",
				WrittenBy = @"ContentQuality.WriteCsvLog via IssueLogWriter.WriteCsvPair (ContentQuality.Analyse, AnalysisPipeline.Step_RunContentQualityChecks)",
				WrittenWhen = @"Whenever content quality runs (same condition as log 10), because the base path is always passed. Header only when there are no findings, or when CheckCmsTemplateAuthoringDefects is off.",
				Delimiter = @"';' in _semicolon.csv, ',' in _comma.csv",
				HeaderRow = true,
				Columns =
				[
					new(@"Filename", @"Downloaded file name"),
					new(@"Url", @"Page URL (blank if not indexed)"),
					new(@"IssueType", @"e.g. EMBEDDED_BOM_IN_BODY, INVISIBLE_CHAR_IN_BODY"),
					new(@"Detail", @"What was detected"),
					new(@"Context", @"Excerpt around the defect. For STYLED_HEADING (from 2.0.58): the class attributes as the page writes them (""as found: class=…"", when they hold more than the heading class) and every heading text in page order, a repeated one once with ""×n"", joined by "" ¦ "".")
				],
				Encoding = @"UTF-8 with BOM, CRLF, no 'sep=' line",
				Quoting = @"RFC-4180 quoted (pair writer), with formula-injection apostrophe",
				ReadBy = [@"Findings.FindingsCatalog (Results: Content quality)", @"Triage.TriageSource.Read (Triage: content quality)"],
				Notes = @"Sorted by Filename, IssueType, Detail, Context. Detected from raw bytes in download/. Not filtered by ContentQualityIssueSuppressions.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"23",
				FileName = @"23-contenttype-extension-mismatch.log",
				PrimaryFileName = @"23-contenttype-extension-mismatch.log",
				Group = @"Crawl",
				Title = @"HTML type-signal mismatches",
				Purpose = @"Downloads where the URL extension, the Content-Type header and the file bytes disagreed about whether the file is HTML, and what the settle step decided.",
				WrittenBy = @"CrawlOrchestrator.Step_SettleExtensions (IssueLogWriter.Write / AppendMany)",
				WrittenWhen = @"Written fresh with a header on the first settle of a completed crawl (header only means checked, no mismatches). The forced post-crawl settle appends rows only when it finds mismatches. Not rewritten on replay.",
				Delimiter = @"| (bare pipe)",
				HeaderRow = true,
				Columns =
				[
					new(@"timestamp", @"Time from the 00 saved row"),
					new(@"url", @"Downloaded URL"),
					new(@"requestedHtml", @"True/False: URL extension implies HTML"),
					new(@"contentType", @"Content-Type recorded in 00 or 'n/a'"),
					new(@"sniffHtml", @"True/False: file bytes look like HTML"),
					new(@"policy", @"UnverifiedHtmlPolicy in effect"),
					new(@"action", @"analysed-as-html or quarantined")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"Sanitized, not quoted ('|' becomes '/', control characters stripped)",
				ReadBy = [@"Findings.FindingsCatalog (Results: Crawl › HTML type)"],
				Notes = @"Only files that were still .unverified are evaluated.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"24",
				FileName = @"24-pdf-contenttype-extension-mismatch.log",
				PrimaryFileName = @"24-pdf-contenttype-extension-mismatch.log",
				Group = @"PDF",
				Title = @"PDF type-signal mismatches",
				Purpose = @"Same as 23 but for PDF identity: where the URL, header and bytes disagreed about a file being a PDF.",
				WrittenBy = @"CrawlOrchestrator.Step_SettleExtensions (IssueLogWriter.Write / AppendMany)",
				WrittenWhen = @"Same discipline as 23: header plus rows on the first settle, appends on the forced settle, not rewritten on replay.",
				Delimiter = @"| (bare pipe)",
				HeaderRow = true,
				Columns =
				[
					new(@"timestamp", @"Time from the 00 saved row"),
					new(@"url", @"Downloaded URL"),
					new(@"requestedPdf", @"True/False: URL implies PDF"),
					new(@"contentType", @"Content-Type recorded in 00 or 'n/a'"),
					new(@"sniffPdf", @"True/False: bytes start with %PDF-"),
					new(@"policy", @"UnverifiedPdfPolicy in effect"),
					new(@"action", @"analysed-as-pdf or quarantined")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"Sanitized, not quoted",
				ReadBy = [@"Findings.FindingsCatalog (Results: Crawl › PDF type)"],
				Notes = @"Evaluated only for files not settled as HTML.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"25",
				FileName = @"25-asset-quality_semicolon.csv / 25-asset-quality_comma.csv (base 25-asset-quality)",
				PrimaryFileName = @"25-asset-quality_comma.csv",
				Group = @"Assets",
				Title = @"Image asset quality",
				Purpose = @"Problems with downloaded images: truncated or oversized files, degenerate or huge dimensions, and embedded metadata such as GPS, camera or author.",
				WrittenBy = @"AssetQuality.Analyse via IssueLogWriter.WriteCsvPair (AnalysisPipeline.Step_AnalyseAssetQuality)",
				WrittenWhen = @"Only when AssetQuality is enabled and download/ exists. Header only when there are no findings.",
				Delimiter = @"';' in _semicolon.csv, ',' in _comma.csv",
				HeaderRow = true,
				Columns =
				[
					new(@"Filename", @"Downloaded image file name"),
					new(@"Url", @"Image URL (blank if not indexed)"),
					new(@"IssueType", @"ASSET_SIZE, ASSET_DIMENSIONS or ASSET_METADATA"),
					new(@"Detail", @"LENGTH_MISMATCH, OVERSIZE, DEGENERATE, or metadata tokens like GPS+Make"),
					new(@"Context", @"Readable explanation or a summary of the metadata present"),
					new(@"Exif", @"Full EXIF inventory, capped (metadata rows only)"),
					new(@"Iptc", @"Full IPTC inventory, capped (metadata rows only)"),
					new(@"Xmp", @"Full XMP inventory, capped (metadata rows only)")
				],
				Encoding = @"UTF-8 with BOM, CRLF, no 'sep=' line",
				Quoting = @"RFC-4180 quoted (pair writer), with formula-injection apostrophe. Metadata values are also passed through SanitizeField('|').",
				ReadBy = [@"Findings.FindingsCatalog (Results: Assets › Images)", @"Triage.ImageSource (Triage › Images; with 38)"],
				Notes = @"Covers all top-level download/ files except .header, ordered ordinally, but only files classified as images (JPEG/PNG/GIF/WebP) under UnverifiedImagePolicy. SizeAndDimensionExclusions skip the size and dimension checks; metadata is always checked. Content-Length comes from the .header sidecar.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"26",
				FileName = @"26-image-contenttype-extension-mismatch.log",
				PrimaryFileName = @"26-image-contenttype-extension-mismatch.log",
				Group = @"Assets",
				Title = @"Image type-signal mismatches",
				Purpose = @"Same as 23 but for image identity: where the URL, header and bytes disagreed about a file being an image.",
				WrittenBy = @"CrawlOrchestrator.Step_SettleExtensions (IssueLogWriter.Write / AppendMany)",
				WrittenWhen = @"Same discipline as 23/24.",
				Delimiter = @"| (bare pipe)",
				HeaderRow = true,
				Columns =
				[
					new(@"timestamp", @"Time from the 00 saved row"),
					new(@"url", @"Downloaded URL"),
					new(@"requestedImage", @"True/False: URL has an image extension"),
					new(@"contentType", @"Content-Type recorded in 00 or 'n/a'"),
					new(@"sniffImage", @"True/False: bytes look like an image"),
					new(@"policy", @"UnverifiedImagePolicy in effect"),
					new(@"action", @"analysed-as-image or quarantined")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"Sanitized, not quoted",
				ReadBy = [@"Findings.FindingsCatalog (Results: Crawl › Image type)"],
				Notes = @"Evaluated only for files not settled as HTML or PDF. The final extension comes from sniffing the bytes (jpg/png/gif/webp).",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"27",
				FileName = @"27-spellcheck-suppression-suggestions.log",
				PrimaryFileName = @"27-spellcheck-suppression-suggestions.log",
				Group = @"Scripts",
				Title = @"Script token filter suggestions",
				Purpose = @"Script string literals flagged by spell-check that look technical, offered as ready-to-paste TokensToFilter entries, with low-confidence ones kept separate for review.",
				WrittenBy = @"NewSpellEngineRunner.Run → SuppressionSuggestionAnalyzer.Compose (AnalysisPipeline.Step_RunSpellCheck, written in Step_WriteSuggestions)",
				WrittenWhen = @"Only when SpellCheckJavaScript.Enabled is true and the spell pass completes. Always contains the sections, even when they are empty.",
				Delimiter = @"free text (fixed-width rows)",
				HeaderRow = false,
				Columns =
				[
					new(@"spread", @"Number of distinct pages with this literal, then '×'"),
					new(@"literal", @"Whole script literal, padded to 30 characters"),
					new(@"context", @"Source context, single line, at most 80 characters")
				],
				Encoding = @"UTF-8 with BOM, CRLF (WriteView splits on \n)",
				Quoting = @"None. The paste blocks are JSON-escaped strings.",
				ReadBy = [@"Findings.FindingsCatalog (Results: Scripts › Filter suggestions)"],
				Notes = @"Starts with 4 '#' comment lines. Then sections: '## SUGGEST — N literal(s)', '## REVIEW (low confidence ...) — N', '## NOT SUGGESTED — N multiword literal(s)' (count only), then two '## Ready to paste' blocks, each a '""TokensToFilter"": [ ... ]' JSON fragment. Empty sections show '   (none)'. Rows within a section are sorted by spread (descending), then literal. Cleared before a debug replay.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"28",
				FileName = @"28-bulk-script-blob.log",
				PrimaryFileName = @"28-bulk-script-blob.log",
				Group = @"Scripts",
				Title = @"Inline script blob",
				Purpose = @"Every inline <script> body from every page, joined into one file with a header comment naming the source page. It is the input for log 29.",
				WrittenBy = @"BulkScriptScanner.HarvestBlob (BulkScriptScanner.Run, AnalysisPipeline.Step_BulkScanPageScript)",
				WrittenWhen = @"Only when SpellCheckJavaScript.BulkScanPageScript is true (off by default).",
				Delimiter = @"free text (JavaScript)",
				HeaderRow = false,
				Columns =
				[
					new(@"provenance header", @"'// ===== {url} · script #N =====' before each block"),
					new(@"script body", @"Raw inline script text, followed by a blank line")
				],
				Encoding = @"UTF-8 with BOM (Utf8WithBom), CRLF for written lines (script bodies keep their own line breaks)",
				Quoting = @"None. Quotes and newlines are removed from the source label only.",
				ReadBy = [@"BulkScriptScanner.ScanBlob (reads it back to produce log 29)"],
				Notes = @"The first line is '// 28 — bulk inline-<script> blob (BulkScanPageScript). Headers are // comments, inert when re-scanned.' followed by a blank line. Only scripts without src are included. Pages are processed in ordinal path order.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"29",
				FileName = @"29-bulk-script-findings.log",
				PrimaryFileName = @"29-bulk-script-findings.log",
				Group = @"Scripts",
				Title = @"Inline script spell findings",
				Purpose = @"Spelling findings from string literals in inline page scripts, scanned without any configured suppression, to show blind spots.",
				WrittenBy = @"BulkScriptScanner.ScanBlob / ScanText (AnalysisPipeline.Step_BulkScanPageScript)",
				WrittenWhen = @"Only when BulkScanPageScript is true.",
				Delimiter = @"tab",
				HeaderRow = false,
				Columns =
				[
					new(@"word", @"Flagged word (line starts with 2 spaces)"),
					new(@"page", @"Page URL from the preceding blob header"),
					new(@"context", @"About 80 characters of surrounding source on one line")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None. Tabs and newlines in the context are replaced with spaces.",
				ReadBy = [@"Findings.FindingsCatalog (Results: Scripts › Inline scripts)"],
				Notes = @"'#' comment lines: '# 29 — bulk page-script scan findings (BulkScanPageScript)', '# Source: ... · dictionaries: ... · gates ON ...', '# columns: word <TAB> page <TAB> context', then a blank line. If ScriptBulkScanDictionaries is unusable, only a '# No usable ...' note follows. Ends with '  (no findings)' when empty. The prose-ratio gate is off here.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"30",
				FileName = @"30-js-files-spell-check.log",
				PrimaryFileName = @"30-js-files-spell-check.log",
				Group = @"Scripts",
				Title = @"JS file spell-check (full)",
				Purpose = @"Full debug output of spell-checking the downloaded .js bundles: every file scanned, every finding kept, and a note for each literal the prose-ratio gate dropped.",
				WrittenBy = @"JsFileScanner.Run / BulkScriptScanner.ScanText (AnalysisPipeline.Step_ScanJsFiles)",
				WrittenWhen = @"Only when SpellCheckJavaScript.ScanScriptFilesInDownload is true (off by default).",
				Delimiter = @"tab (finding lines); '#' lines are free text",
				HeaderRow = false,
				Columns =
				[
					new(@"word", @"Flagged word (line starts with 2 spaces)"),
					new(@"file", @".js path relative to download/"),
					new(@"context", @"Surrounding source text on one line")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [],
				Notes = @"Header comments: '# 30 — ... FULL / debug', dictionaries/gates/TokensToFilter/τ=0.40 line, gate explanation, '# columns: word <TAB> file <TAB> context'. Per file: '# ── rel · size ──' (or UNREADABLE / SKIPPED over 32 MB). Gated literals: '# gated (ratio R, miss/total) · file · literal'. A literal of one word (beside punctuation or digits at most) is dropped before any dictionary and gets no gated line — it could never yield a finding; a header line says so. Then '#    (N undecodable literal(s) skipped)' if any, and a final '# total: ...' summary. base64assets/ is excluded.",
				FormatVersion = @"2.0.107",
			},
			new OutputFormat
			{
				Number = @"31",
				FileName = @"31-js-files-spell-check-trimmed.log",
				PrimaryFileName = @"31-js-files-spell-check-trimmed.log",
				Group = @"Scripts",
				Title = @"JS file spell-check (triage)",
				Purpose = @"The triage view of log 30: only findings from literals that passed the prose-ratio gate.",
				WrittenBy = @"JsFileScanner.Run (trimmed writer) (AnalysisPipeline.Step_ScanJsFiles)",
				WrittenWhen = @"Same as 30.",
				Delimiter = @"tab (finding lines)",
				HeaderRow = false,
				Columns =
				[
					new(@"word", @"Flagged word (line starts with 2 spaces)"),
					new(@"file", @".js path relative to download/"),
					new(@"context", @"Surrounding source text on one line")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [@"Findings.FindingsCatalog (Results: Scripts › JS files)"],
				Notes = @"Has the same per-file '# ── ... ──' list and total summary as 30, with no '# gated' lines. The header says '# 31 — ... TRIMMED / triage' and points to log 30 for the full output.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"32",
				FileName = @"32-js-files-spell-check-unique.log",
				PrimaryFileName = @"32-js-files-spell-check-unique.log",
				Group = @"Scripts",
				Title = @"JS file unique misspellings",
				Purpose = @"A flat, sorted, de-duplicated list of every distinct word kept from the JS-file scan.",
				WrittenBy = @"JsFileScanner.WriteUniqueWords (AnalysisPipeline.Step_ScanJsFiles)",
				WrittenWhen = @"Same as 30, including when the dictionaries are unusable (then the list is empty).",
				Delimiter = @"none (one word per line)",
				HeaderRow = false,
				Columns =
				[
					new(@"word", @"Distinct kept-finding word (case-sensitive de-duplication)")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [@"Findings.FindingsCatalog (Results: Scripts › JS unique words)"],
				Notes = @"Two '#' comment lines ('# 32 — ... UNIQUE tokens', count line), then a blank line. Sorted case-insensitively (invariant culture).",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"33",
				FileName = @"33-js-files-spell-check-routing.log",
				PrimaryFileName = @"33-js-files-spell-check-routing.log",
				Group = @"Scripts",
				Title = @"JS findings routing preview",
				Purpose = @"For each JS bundle with findings: how many pages load it (reach), and whether its findings would be ticketed per page (CLEAR) or once per bundle (BULK).",
				WrittenBy = @"JsFileScanner.WriteRoutingPreview (AnalysisPipeline.Step_ScanJsFiles)",
				WrittenWhen = @"Same as 30.",
				Delimiter = @"free text (two spaces between key=value fields)",
				HeaderRow = false,
				Columns =
				[
					new(@"route", @"CLEAR, BULK or UNRESOLVED"),
					new(@"reach", @"Pages that load the bundle"),
					new(@"findings", @"Kept findings in the bundle"),
					new(@"key / file", @"Hash-stripped bundle key, or file= and url= when unresolved")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [],
				Notes = @"Starts with 4 '#' lines (title, pages indexed/unresolved, reach threshold rule = JsFindingPageReachThreshold, preview disclaimer) and a blank line. CLEAR rows are followed by '        page: <url>' lines. Sorted by reach (descending), then file. Ends with '# (no bundles produced findings)' when empty. This is a preview only; nothing goes to the ticket ledger.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"34",
				FileName = @"34-oversized-files.log",
				PrimaryFileName = @"34-oversized-files.log",
				Group = @"Other",
				Title = @"Oversized files not stored",
				Purpose = @"URLs whose body exceeded the single-file size limit: announced size, real type sniffed from a 64 KB sample, and the pages that link to them.",
				WrittenBy = @"OversizedFiles.WriteLog (AnalysisPipeline.Step_WriteOversizedLog)",
				WrittenWhen = @"Always, every run (rebuilt from 01, samples and sidecars, so it is safe on replay). Header only when nothing is oversized.",
				Delimiter = @"| (bare pipe)",
				HeaderRow = true,
				Columns =
				[
					new(@"timestamp", @"Time from the 01 'Oversized' row"),
					new(@"url", @"Oversized URL"),
					new(@"size", @"'N bytes (announced)' or 'more than LIMIT bytes (no size announced)'"),
					new(@"contentType", @"Response Content-Type from the sidecar or 'n/a'"),
					new(@"detectedType", @"Sniffed from sample: PDF, ZIP, MP4, HTML, unknown..."),
					new(@"contentDisposition", @"Content-Disposition header or 'n/a'"),
					new(@"sample", @"'oversized/<name>.sample' or 'n/a'"),
					new(@"linkedFrom", @"Space-separated URLs of pages containing the path, or 'n/a'")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"Sanitized, not quoted ('|' becomes '/')",
				ReadBy = [@"Findings.FindingsCatalog (Results: Crawl › Oversized)"],
				Notes = @"Size and type come from the RESPONSE section of the sample's .header sidecar.",
				FormatVersion = @"2.0.0",
			},
			new OutputFormat
			{
				Number = @"35",
				FileName = @"35-dictionary-not-needed.log",
				PrimaryFileName = @"35-dictionary-not-needed.log",
				Group = @"Spelling",
				Title = @"Dictionary entries no longer needed",
				Purpose = @"User and site dictionary entries that pages asked for, but whose words the app accepts without them (a second spell-check pass without the two lists): candidates to remove.",
				WrittenBy = @"Lexicon.DictionaryReport.WriteNotNeeded (AnalysisPipeline.Step_FindUnneededEntries)",
				WrittenWhen = @"Only when the run was started from the Dictionaries page with ""Find entries no longer needed"", the spell pass completed, and UpdateUserDictionary or UpdateSiteSpecificDictionary is true.",
				Delimiter = @"none (one word per line, in sections)",
				HeaderRow = false,
				Columns =
				[
					new(@"word", @"Dictionary entry flagged in the section above it")
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [@"Dictionaries page"],
				Notes = @"First line: '# 35 — … found yyyy-MM-dd HH:mm …'. Sections as in log 15 (user, site, in both), each starting with a '// ' line. Pinned entries and entries no page asked for (log 15) are never listed. A word that the second pass still flagged, or that is part of a flagged hyphenated word, keeps its entry. Cleared before a replay.",
				FormatVersion = @"2.0.28",
			},
			new OutputFormat
			{
				Number = @"36",
				FileName = @"36-hyphenated-word-parts.log",
				PrimaryFileName = @"36-hyphenated-word-parts.log",
				Group = @"Spelling",
				Title = @"Hyphenated misspellings part by part",
				Purpose = @"For each hyphenated word of log 11, how the part-by-part check placed each part, per language it was flagged in: the parts marked unknown are the ones that keep the word flagged, and the ones the Spell findings page offers to add to a dictionary.",
				WrittenBy = @"SpellCheck.HyphenParts.WriteLog (AnalysisPipeline, after the page spell-check)",
				WrittenWhen = @"Every run whose page spell-check completed (header lines only when no finding is hyphenated).",
				Delimiter = @"TAB",
				HeaderRow = false,
				Columns =
				[
					new(@"word", @"The finding as in log 11"),
					new(@"language", @"A language the word was flagged in"),
					new(@"part, status (repeated)", @"Each part in order and how it was placed: unknown · known-part (an entry of Known word parts, which may span several parts) · capital (a single capital letter) · acronym (all capitals, not checked; 2.0.45) · system · user · site · foreign (the page language's dictionary tiers) · english · compound-head (a head before a hyphen, with a linking element or as a bare stem)"),
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [@"Spell findings page"],
				Notes = @"Starts with 3 '#' comment lines. One line per word and language, in log 11's order. The parts are placed as the spell-check places them, but every part is placed (the check stops at the first unknown one) and no dictionary usage is recorded, so dictionary maintenance is unaffected. Cleared before a replay.",
				FormatVersion = @"2.0.43",
			},
			new OutputFormat
			{
				Number = @"37",
				FileName = @"37-acronyms-not-checked.log",
				PrimaryFileName = @"37-acronyms-not-checked.log",
				Group = @"Spelling",
				Title = @"Acronyms not spell-checked",
				Purpose = @"The words in all capitals the page spell-check left unchecked (Ignore acronyms), once each, for a person to look over: a transposed or mistyped abbreviation can only be told from a real one in context.",
				WrittenBy = @"SpellCheck.AcronymList.Lines (SpellCheck.NewSpellEngineRunner.Run)",
				WrittenWhen = @"Every run whose page spell-check completed while SpellCheckEngine.IgnoreAcronyms is on (header lines only when there are none).",
				Delimiter = @"TAB",
				HeaderRow = false,
				Columns =
				[
					new(@"acronym", @"As written (with its plural s, when accepted)"),
					new(@"pages", @"On how many pages it stands"),
					new(@"text around it", @"Up to 40 characters either side of its first occurrence on the first page (page order), whitespace collapsed"),
				],
				Encoding = @"UTF-8 with BOM, CRLF",
				Quoting = @"None",
				ReadBy = [],
				Notes = @"Starts with 3 '#' comment lines; the first names the rule (length, plural s) and the count. Sorted by the acronym's letters regardless of their order, then the acronym, so look-alikes ('ABC', 'ACB') sit next to each other. Includes acronyms that are parts of hyphenated words. Information only: nothing here is a finding. Cleared before a replay.",
				FormatVersion = @"2.0.45",
			},
			new OutputFormat
			{
				Number = @"38",
				FileName = @"38-image-references_semicolon.csv / 38-image-references_comma.csv (base 38-image-references)",
				PrimaryFileName = @"38-image-references_comma.csv",
				Group = @"Assets",
				Title = @"Who uses each flagged image",
				Purpose = @"For each image with a finding triage takes (log 25: a truncated or oversized file, oversized or tiny dimensions, location or device data, names of people), every page or stylesheet that references it — how, as written, and for a stylesheet reference when a browser requests it; so triage can place the finding where the fix is made and tell an image shown on a page from one only offered as a link.",
				WrittenBy = @"Logging/ImageReferencesLog.Write via IssueLogWriter.WriteCsvPair (AnalysisPipeline.Step_Process404s, the same pass as 07)",
				WrittenWhen = @"Every analysis; the header alone when no image is flagged or asset quality is off (removed before a debug replay).",
				Delimiter = @"';' in _semicolon.csv, ',' in _comma.csv (CsvSemicolonSuffix='_semicolon.csv' → ';', CsvCommaSuffix='_comma.csv' → ',')",
				HeaderRow = true,
				Columns =
				[
					new(@"Image", @"The image URL, as in 25"),
					new(@"Kind", @"image · srcset · link (offered as a link, not shown) · link element · data attribute · inline data · stylesheet url() · no reference found"),
					new(@"Referrer", @"The page or stylesheet that references it (empty when none was found)"),
					new(@"AsWritten", @"The reference exactly as it stands in the source"),
					new(@"WhenRequested", @"Stylesheet references: as the Grade of 07 (fires on load … dead reference · undecided); else empty"),
					new(@"Evidence", @"What the grade rests on, as in 07")
				],
				Encoding = @"UTF-8 with BOM, CRLF, no 'sep=' line",
				Quoting = @"RFC-4180: a field is quoted when it contains the delimiter or '""'; formula-trigger values (= + - @ TAB CR at start) get a leading apostrophe; control characters, CR/LF and bidi are stripped",
				ReadBy = [@"Triage.ImageSource (Triage › Images: where an item is placed, shown or linked)", @"Findings.FindingsCatalog (Results: Assets › Image references)"],
				Notes = @"Found in the broken-links pass, without a request: pages are read for href and src values and srcset candidates; a page that also names a broken address is parsed, any other page that names a flagged image has its a, img and source tags read from the text (comments, scripts and styles left out — what a parse takes as tags); stylesheets for url(). One row per reference, in the order of 25. Rights, equipment, program and date fields alone do not flag an image. Scripts are not run.",
				FormatVersion = @"2.0.116",
			},
			new OutputFormat
			{
				Number = @"40",
				FileName = @"40-forwarding-links_semicolon.csv / 40-forwarding-links_comma.csv (base 40-forwarding-links)",
				PrimaryFileName = @"40-forwarding-links_comma.csv",
				Group = @"Links & redirects",
				Title = @"Forwarding links",
				Purpose = @"Links to the site's own host whose query holds a full address as a value (…?next=https://…, URL-encoded or not, //host/…): the server is to forward the visitor there. Whether it forwards to any address (an open redirect, CWE-601) is for a person to check; the app never requests these links.",
				WrittenBy = @"SelfLinkScanner.FindSelfLinks via IssueLogWriter.WriteCsvPair (AnalysisPipeline.Step_ScanSelfLinks), the same pass as 09",
				WrittenWhen = @"Always after the boilerplate prune, with 09. Header only when none are found.",
				Delimiter = @"';' in _semicolon.csv, ',' in _comma.csv",
				HeaderRow = true,
				Columns =
				[
					new(@"Page", @"URL of the page holding the link"),
					new(@"Link", @"Raw href value (entity-decoded)"),
					new(@"Parameter", @"The query parameter holding the address (decoded)"),
					new(@"Target", @"The address handed over, decoded (twice when encoded twice)"),
					new(@"TargetHost", @"The target's host"),
					new(@"Scope", @"'external' (another host) or 'same host'")
				],
				Encoding = @"UTF-8 with BOM, CRLF, no 'sep=' line",
				Quoting = @"RFC-4180 quoted (pair writer), with formula-injection apostrophe",
				ReadBy = [@"Findings.FindingsCatalog (Results: Links › Forwarding links)", @"Triage.ForwardingLinkSource.Read (triage: one item per page, parameter and target hosts; external ones security relevant)"],
				Notes = @"Found by the shape of the value only — no parameter names. Reads the pruned/ tree like 09; on a reference page the template's links count (reported there once). One row per link, parameter and target; a link written twice on a page is one row. External first, then by page, link, parameter and target. Cleared before a debug replay.",
				FormatVersion = @"2.0.174",
			},
			new OutputFormat
			{
				Number = @"39",
				FileName = @"39-content-list-not-online.log",
				PrimaryFileName = @"39-content-list-not-online.log",
				Group = @"Crawl",
				Title = @"Content list rows not online",
				Purpose = @"The page paths of the CMS export whose online column does not hold the online value: in the CMS, not on the site (not published yet, or taken offline). They are left out of 04, so neither 05 nor the post-crawl pass requests them; a live page that links to one is still a broken link, and its item says the CMS has the page but it is not online.",
				WrittenBy = @"Content.Listing (CrawlOrchestrator.Step_WriteContentListLogs)",
				WrittenWhen = @"When the content list is read and CmsContentList.OnlineColumn and OnlinePattern are both set and the header row has the column — then always, empty when every row is online. Not written while the filter is off; a file from an earlier crawl is removed.",
				Delimiter = @"none (one value per line)",
				HeaderRow = false,
				Columns =
				[
					new(@"path", @"As in 04: the configured column's value, prefix-replaced and suffix added")
				],
				Encoding = @"UTF-8 with BOM, CRLF (FileIo.WriteList → FileFormat.WriteAllLines)",
				Quoting = @"None",
				ReadBy = [@"Triage.BrokenLinkSource (a broken link to a page the CMS has but not online)", @"Findings.FindingsCatalog (Results: Crawl › Not online in the CMS)"],
				Notes = @"Which column and which value mean online comes from the configuration only. Rows left out by RowFilter, RowNegativeFilter or RowsToExclude are in neither 04 nor 39.",
				FormatVersion = @"2.0.171",
			},
			new OutputFormat
			{
				Number = @"",
				FileName = @"sitemap.xml",
				PrimaryFileName = @"sitemap.xml",
				Group = @"Sitemap",
				Title = @"Generated XML sitemap",
				Purpose = @"A sitemaps.org sitemap of the crawled pages, excluding noindex pages, pages from the post-crawl list pass, and configured exclusions.",
				WrittenBy = @"Sitemap.Generate (AnalysisPipeline.Step_GenerateSitemap); copied to the site root by AnalysisPipeline.Step_CopySitemapToSiteRoot",
				WrittenWhen = @"Every run, into the snapshot folder. Then copied, overwriting, to <BaseDirectory>/<urlDirectory>/sitemap.xml.",
				Delimiter = @"XML (one <url> element per line)",
				HeaderRow = false,
				Columns =
				[
					new(@"<url><loc>", @"XML-escaped page URL")
				],
				Encoding = @"UTF-8 with BOM (FileFormat.WriteAllText), CRLF between <url> lines",
				Quoting = @"XML escaping (SecurityElement.Escape)",
				ReadBy = [@"AnalysisPipeline.CountSitemapEntries (run summary count only)", @"AnalysisPipeline.CopyFilesToDirectory (copy to site root)"],
				Notes = @"Written as one string: the XML declaration, the <urlset> (with sitemap, image and video namespaces) and the first <url> are on one line. URLs are sorted ordinally. Robots noindex is read from the first 16 KB of <head>. A page is excluded if its URL contains a SitemapGeneratorExclusions entry and no SitemapGenerateForcedInclusions entry. Index misses ('error') are never written.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"",
				FileName = @"connectivity.log (site folder: <BaseDirectory>/<urlDirectory>/connectivity.log)",
				PrimaryFileName = @"connectivity.log",
				Group = @"Other",
				Title = @"Connectivity check failures",
				Purpose = @"Diagnostic record of pre-crawl reachability checks (DNS, proxy, HTTP HEAD) for this machine and user.",
				WrittenBy = @"ConnectivityCheck.AppendToLog (ConnectivityCheck.RunAsync, called from CrawlerApp before the crawl)",
				WrittenWhen = @"Appended only when the check fails. It is in the site folder, not the timestamp snapshot.",
				Delimiter = @"free text, 'Label       : value' blocks",
				HeaderRow = false,
				Columns =
				[
					new(@"Timestamp", @"Local time of the check"),
					new(@"Result", @"OK or FAILED"),
					new(@"Machine / Machine IPs / Username", @"Where and as whom the check ran"),
					new(@"Target URL / Target host / Resolved IPs", @"Target and its DNS result"),
					new(@"Proxy URL / Proxy IPs", @"Only when a proxy is configured"),
					new(@"HEAD status", @"HTTP status code and response time in ms"),
					new(@"Failure", @"Failure reason, if any")
				],
				Encoding = @"UTF-8 with BOM (Encoding.UTF8 File.AppendAllText; BOM only on creation). Line endings are Environment.NewLine (StringBuilder.AppendLine), not FileFormat.NewLine, so they are LF on non-Windows systems.",
				Quoting = @"None",
				ReadBy = [],
				Notes = @"Each entry starts with a '──────...' separator line.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"",
				FileName = @"download/ (folder) incl. <name>.header sidecars",
				PrimaryFileName = @"download/",
				Group = @"Crawl",
				Title = @"Downloaded files + header sidecars",
				Purpose = @"The raw downloaded pages and assets, each with a .header file recording the exact HTTP request and response headers.",
				WrittenBy = @"CrawlSession (StoreBodyAsync) + Crawl.WriteHeaderSidecar → HeaderSidecar.FormatHeaderSidecar; renamed by CrawlOrchestrator.Step_SettleExtensions / Step_ReSettleUnverifiedAssets",
				WrittenWhen = @"During a fresh crawl and the post-crawl pass. A clean sweep before a replay keeps it.",
				Delimiter = @".header: 'Name: Value' lines (split on the first ':')",
				HeaderRow = false,
				Columns =
				[
					new(@"=== REQUEST ===", @"Section marker line"),
					new(@"request line", @"'GET <url>'"),
					new(@"request headers", @"'Name: Value', one per line, repeats kept"),
					new(@"=== RESPONSE ===", @"Section marker line"),
					new(@"status line", @"'HTTP/<ver> <code> <reason>'"),
					new(@"response headers", @"'Name: Value' (response and content headers), repeats kept")
				],
				Encoding = @".header: UTF-8 with BOM (FileFormat.WriteAllText), LF ('\n') line endings. Bodies: raw bytes as received.",
				Quoting = @"Header values are sanitized to visible markers: [CR], [LF], [INVISIBLE ... U+XXXX]; TAB is kept",
				ReadBy = [@"AssetQuality.ReadSidecarResponseHeaders (Content-Type, Content-Length)", @"CrawlOrchestrator ReadSidecarContentType (re-settle of .unverified assets)", @"CrawlLimitReportBuilder (skips .header when sizing)"],
				Notes = @"Body name = lowercase SHA-256 hex of the absolute URL + the last path segment if it contains a dot (e.g. '<64hex>styles.min.css'), else '.htm'. Pages are first saved as <hash>….unverified, then settled to the page extension, .pdf, or a sniffed image extension, or left .unverified (quarantined; excluded from 02). The sidecar name uses Path.ChangeExtension, so 'x.html' pairs with 'x.header', not 'x.html.header'. Orphan pages missing from 02 may be renamed to *.html.bak by the integrity check. A sibling pruned/ folder holds pages with boilerplate removed, used by the content-quality, self-link and spell checks.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"",
				FileName = @"oversized/ (folder): <name>.sample + <name>.header",
				PrimaryFileName = @"oversized/",
				Group = @"Other",
				Title = @"Oversized file samples",
				Purpose = @"For files over the single-file limit: the first 64 KB as a sample (with a neutral extension so it never opens as the real type) plus its HTTP header sidecar.",
				WrittenBy = @"CrawlSession.RecordOversized (File.WriteAllBytes + Crawl.WriteHeaderSidecar)",
				WrittenWhen = @"During the crawl, for each body over MaxSingleFileBytes. A clean sweep keeps it.",
				Delimiter = @".header: same two-section format as download/",
				HeaderRow = false,
				Columns =
				[
					new(@"<name>.sample", @"First up to 65,536 bytes of the body"),
					new(@"<name>.header", @"REQUEST/RESPONSE sidecar (see download/)")
				],
				Encoding = @".sample: raw bytes; .header: UTF-8 with BOM, LF",
				Quoting = @"Header values sanitized to visible markers",
				ReadBy = [@"OversizedFiles.WriteLog (log 34: sniffs the sample, reads the RESPONSE headers)"],
				Notes = @"Sample name = Path.ChangeExtension(<hash><lastSegment>, 'sample'). Kept outside download/ so analyzers never treat a fragment as a page.",
				FormatVersion = @"2.0.0",
			},
			new OutputFormat
			{
				Number = @"",
				FileName = @"base64assets/ (folder) incl. sourcesstripped/",
				PrimaryFileName = @"base64assets/",
				Group = @"Assets",
				Title = @"Decoded Base64 assets",
				Purpose = @"The decoded files from every Base64 data URI listed in log 19, plus copies of the source files with each data URI replaced by a short placeholder.",
				WrittenBy = @"Base64AssetExtractor.Extract / ProcessFile (AnalysisPipeline.Step_ExtractBase64Assets)",
				WrittenWhen = @"Only when Base64AssetFileExtensions is non-empty and download/ exists. Files are overwritten on re-run, never deleted.",
				Delimiter = @"n/a (binary/text files)",
				HeaderRow = false,
				Columns =
				[
					new(@"<safeBase>__<n><ext>", @"Decoded asset; n counts per source file, ext from MIME or WASM magic"),
					new(@"sourcesstripped/<filename>", @"Source file with '/* BASE64_STRIPPED:<savedName> */' placeholders")
				],
				Encoding = @"Decoded assets: raw bytes. Stripped sources: Latin-1, no BOM, original line endings.",
				Quoting = @"n/a",
				ReadBy = [@"JsFileScanner.EnumerateJsFiles explicitly excludes this subtree"],
				Notes = @"safeBase = source file name without extension, with invalid file-name characters replaced by '_'. Unknown or missing MIME types become .bin, '+xml' becomes .xml, other text/* becomes .txt. A stripped copy is written only for files that had at least one extracted asset.",
				FormatVersion = @"1.2.5",
			},
			new OutputFormat
			{
				Number = @"",
				FileName = @"settings-usage.json",
				PrimaryFileName = @"settings-usage.json",
				Group = @"Spelling",
				Title = @"Settings usage",
				Purpose = @"How often each technical XPath, attribute not checked and boilerplate part matched in this run, switched-off entries included, and which reference pages were seen; how many findings each active suppression rule hid and each active unwanted-pattern group found. The settings screen shows it for the primary site's latest snapshot.",
				WrittenBy = @"SettingsUsage.Write (after the spell-check pass), ContentQuality.WriteUsage (after the content-quality pass) and ContentPathUsage.Write (when the CMS export is read); each keeps the others' sections. The download exclusions' counts are in crawl-usage.json.",
				WrittenWhen = @"Every run that spell-checks or checks content quality; overwritten on re-run.",
				Delimiter = @"JSON",
				HeaderRow = false,
				Columns =
				[
					new(@"Pages", @"Pages counted"),
					new(@"Entries.<setting>.<entry>", @"[pages with a match, elements matched]; a boilerplate entry is '<path prefix>␟<type>␟<value>', a suppression rule '<type>␟<value>' (findings hidden), a pattern group its name (findings before suppression); for script strings to skip, '…TokensToFilter' counts [pages, strings skipped] in inline scripts and '…TokensToFilter.ScriptFiles' [script files, strings skipped]"),
					new(@"ReferencePagesSeen", @"Reference pages (paths) that were in the run")
				],
				Encoding = @"UTF-8, no BOM",
				Quoting = @"JSON",
				ReadBy = [@"The settings screen (SettingsModel)"],
				Notes = @"Sections: issue suppressions, pattern groups, script strings to skip, and the switchable lists (XPaths, attributes, boilerplate parts), each counting how often an entry matched, switched-off entries included. 'SpellCheckWordPrefixesToStrip' (Known word parts) counts [pages, distinct words it let pass per page, added up]; 'SpellCheckWordPrefixesToStrip.NotNeeded' lists the entries whose every word would pass without them (same numbers); 'SpellCheckWordPrefixesToStrip.Off' [pages, flagged words] for switched-off entries; 'Examples.SpellCheckWordPrefixesToStrip' holds up to three words (with languages) that need each entry. 'CmsContentList.RowsToExclude' counts [rows, rows] of the CMS export per path to leave out (trimmed; switched-off and overlapping entries too) and 'CmsContentList.RowsToExclude.Read' has 'rows': the rows read past the row filters; written whenever the export is read (Step_WriteContentListLogs), a re-run included. The download exclusions' counts are written by the crawl into crawl-usage.json, which a replay keeps.",
				FormatVersion = @"2.0.40",
			},
			new OutputFormat
			{
				Number = @"",
				FileName = @"crawl-usage.json",
				PrimaryFileName = @"crawl-usage.json",
				Group = @"Crawl",
				Title = @"Crawl usage",
				Purpose = @"What only a crawl can count: for each download exclusion, how many different addresses it kept out and how often, in the main crawl and the extra download apart. Same shape as settings-usage.json; the settings screen reads both, this one's settings first.",
				WrittenBy = @"DownloadExclusionUsage.Write via SettingsUsage.Merge (CrawlOrchestrator.Step_WriteExclusionUsage, at the end of a crawl)",
				WrittenWhen = @"Every crawl; never by a replay, which keeps the file (SnapshotFlows.CleanSweepSnapshot keeps it with 00, 01 and download/).",
				Delimiter = @"JSON",
				HeaderRow = false,
				Columns =
				[
					new(@"Entries.DownloadExclusions.<entry>", @"[different addresses, times] in the main crawl; the entry text trimmed"),
					new(@"Entries.DownloadExclusions.ExtraDownload.<entry>", @"The same for the extra download")
				],
				Encoding = @"UTF-8, no BOM",
				Quoting = @"JSON",
				ReadBy = [@"SettingsUsage.Read (the settings screen, with settings-usage.json)"],
				Notes = @"Written by the crawl only, so a replay, which deletes every other derived file, keeps it; the settings screen reads it together with settings-usage.json.",
				FormatVersion = @"2.0.175",
			},
			new OutputFormat
			{
				Number = @"",
				FileName = @"dns.json",
				PrimaryFileName = @"dns.json",
				Group = @"Crawl",
				Title = @"Where the site's names pointed",
				Purpose = @"The addresses the site's host names (the site and its allowed subdomains) resolved to at crawl start, and — without a proxy — the addresses the crawl's connections actually went to. Taken into the site's dns-history.json, against which an address a host never had is reported as information (step row, application log). Nothing is refused.",
				WrittenBy = @"Security.DnsRecord.Write (CrawlOrchestrator.Step_WriteDnsRecord, at the end of a crawl)",
				WrittenWhen = @"Every crawl; never by a replay, which keeps the file (SnapshotFlows.CleanSweepSnapshot).",
				Delimiter = @"JSON",
				HeaderRow = false,
				Columns =
				[
					new(@"ThroughProxy", @"true when a proxy was in between: it resolved the names itself, and the lookup is this machine's view"),
					new(@"Resolved[].Host / Addresses / Error", @"Each host's addresses at crawl start, or the lookup's error"),
					new(@"Connected[].Host / Addresses", @"The addresses connections went to (no proxy only)")
				],
				Encoding = @"UTF-8, no BOM",
				Quoting = @"JSON",
				ReadBy = [@"Security.DnsRecord.Read (a snapshot's own record)"],
				Notes = @"Addresses are written as text, IPv4-mapped IPv6 as IPv4. A lookup that fails or times out (10 s) keeps its error.",
				FormatVersion = @"2.0.179",
			},
			new OutputFormat
			{
				Number = @"",
				FileName = @"dns-history.json",
				PrimaryFileName = @"dns-history.json",
				Group = @"Crawl",
				Title = @"Every address the site's names had",
				Purpose = @"In the site folder, next to the triage ledger and apart from it: every address each host of the site had across its crawls, when first and last seen and how. A crawl compares against it — an address the host never had is reported as information, one of a class it never had prominently — and adds its own. Results › Crawl › DNS shows it.",
				WrittenBy = @"Security.DnsHistory.Write (CrawlOrchestrator.Step_WriteDnsRecord, at the end of a crawl; replaced in one step)",
				WrittenWhen = @"Every crawl; never by a replay. Left as it is when it cannot be read.",
				Delimiter = @"JSON",
				HeaderRow = false,
				Columns =
				[
					new(@"Addresses[].Host / Address", @"A host of the site and one address it had"),
					new(@"Addresses[].FirstSeen / LastSeen", @"The snapshot folder names it was first and last seen in"),
					new(@"Addresses[].SeenAs", @"looked up, looked up (proxy in between), connected")
				],
				Encoding = @"UTF-8, no BOM",
				Quoting = @"JSON",
				ReadBy = [@"Security.DnsHistory.Read (the comparison after a crawl; Results › Crawl › DNS)"],
				Notes = @"The class is worked out when read, not stored.",
				FormatVersion = @"2.0.179",
			},
			new OutputFormat
			{
				Number = @"",
				FileName = @"prune-stamp.json",
				PrimaryFileName = @"prune-stamp.json",
				Group = @"Spelling",
				Title = @"Prune stamp",
				Purpose = @"Which boilerplate groups the snapshot's pruned pages were written with, so the Comparison can tell two snapshots pruned differently.",
				WrittenBy = @"Reprune.WriteStamp — by the analysis' boilerplate prune and by a re-prune from the Comparison page",
				WrittenWhen = @"Every analysis and every re-prune; overwritten each time. Absent in snapshots pruned before 2.0.150.",
				Delimiter = @"JSON",
				HeaderRow = false,
				Columns =
				[
					new(@"Fingerprint", @"16 hex digits over each group's path prefix, reference pages and switched-on parts (order-free)"),
					new(@"Groups", @"Boilerplate groups in effect"),
					new(@"Rules", @"Parts switched on, over all groups"),
					new(@"PrunedAt", @"When the pages were pruned (UTC)")
				],
				Encoding = @"UTF-8, no BOM",
				Quoting = @"JSON",
				ReadBy = [@"The Comparison page (ComparisonModel), through Reprune.ReadStamp"],
				Notes = @"A re-prune writes only pruned/ and this file; it never touches the triage ledger.",
				FormatVersion = @"2.0.150",
			},
		];
	}
}
