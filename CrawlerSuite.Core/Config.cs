namespace CrawlerSuite.Core
{
	using CrawlerSuite.Core.Urls;
	using System.Diagnostics.CodeAnalysis;
	using System.Text;
	using System.Text.Json;
	using System.Text.RegularExpressions;

	public partial class Config
	{
		/// <summary>
		/// Per-site definitions for multi-site operation. Each entry carries the
		/// site-specific surface — identity (<see cref="SiteConfig.Url"/>,
		/// <see cref="SiteConfig.Name"/>, <see cref="SiteConfig.Tenant"/>), crawl
		/// scope (<see cref="SiteConfig.UrlSubdomainsAllowed"/>), the default-site
		/// marker (<see cref="SiteConfig.IsPrimary"/>), and the post-crawl-pass
		/// toggle (<see cref="SiteConfig.PostCrawlPass"/>). Everything else stays
		/// global and is shared across sites; fields that merely vary by tenant use
		/// the <c>{tenant}</c> token resolved per-site (see <see cref="ResolveForSite"/>).
		///
		/// A single run still processes ONE selected site: silent mode runs the one
		/// <see cref="SiteConfig.IsPrimary"/> entry; interactive mode presents the
		/// list for selection (Enter = the primary). The selected site is projected
		/// onto the global config by <see cref="ResolveForSite"/> before the pipeline
		/// runs, so every downstream consumer sees a fully-resolved single-site config
		/// and is unaware multi-site exists. (The projection is repeatable, so it can
		/// also be applied to each site in turn.)
		///
		/// Required — there is no top-level <c>Url</c> fallback. A config without a
		/// <c>Sites</c> entry halts at load with a named error.
		/// </summary>
		public List<SiteConfig> Sites { get; set; } = [];

		/// <summary>
		/// The site selected and projected for this run, recorded by
		/// <see cref="ResolveForSite"/>. Resolution OUTPUT, not authored in JSON —
		/// <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/> keeps it
		/// out of (de)serialization. Null until resolution. Downstream surfaces that
		/// need to show which site a run is for (e.g. the snapshot-prompt heading)
		/// read identity from here rather than re-threading Name/Tenant through call
		/// chains.
		/// </summary>
		[System.Text.Json.Serialization.JsonIgnore]
		public SiteConfig? ResolvedSite { get; set; }

		/// <summary>
		/// The site-identity label used by every surface that announces the
		/// current site ("Name — Url" when a site is resolved with a non-empty Name,
		/// else the bare Url as a defensive fallback). Single source of truth — used
		/// by the snapshot prompt, the main-crawl banner, and the post-crawl banner so
		/// all three render the same format. Reading this rather than re-composing
		/// inline keeps the format change-in-one-place if it ever evolves.
		/// </summary>
		[System.Text.Json.Serialization.JsonIgnore]
		public string ResolvedSiteLabel =>
			ResolvedSite is { } rs && !string.IsNullOrWhiteSpace(rs.Name)
				? $"{rs.Name} — {Url}"
				: Url;

		/// <summary>
		/// Effective target URL for the run. Populated by <see cref="ResolveForSite"/>
		/// from the selected site; not authored directly in JSON (resolution overwrites
		/// any top-level value). Empty until resolution.
		/// </summary>
		public string Url { get; set; } = string.Empty;
		public string BaseDirectory { get; set; } = string.Empty;
		public string ProxyUrl { get; set; } = string.Empty;
		// No proxy credentials here: the proxy's address is a setting, who signs in is not.
		// They are asked for at every crawl start, live only for that job, and are dropped after it;
		// an empty user means the current Windows identity (UseDefaultCredentials).
		public bool UseProxy { get; set; } = false;

		/// <summary>
		/// Debug mode: every completed run is archived in the "debug" folder next to the program
		/// and compared with the previous run of the same snapshot (and a marked baseline); the
		/// test levers of the dictionary checks apply. Off: no archive, levers at their defaults.
		/// </summary>
		public bool DebugMode { get; set; } = false;
		// Query parameter names that carry a URL-encoded target URL to a modal/overlay page.
		// When a crawled link contains any of these parameters, the encoded target URL is
		// extracted and crawled instead of the carrier page. Supports sites that load modal
		// content via query strings (e.g. ?popup=..., ?modal=..., ?overlay=...).
		// Leave empty if the site does not use this pattern.
		public List<ModalQueryParam> ModalQueryParameters { get; set; } = [];

		/// <summary>
		/// Operator-curated list of substring patterns that exclude matching URLs
		/// from the crawl. Applied by <see cref="Urls.Validity.IsInDownloadScope"/> via a
		/// case-insensitive <c>Contains</c> check against the link string — any
		/// link whose URL contains an enabled entry's <c>Value</c> as a substring
		/// (anywhere in the URL) is rejected. Default empty.
		///
		/// Use this for non-security operational filtering — sections you don't
		/// want crawled because the response shape is uninteresting or
		/// disproportionately large (e.g. forum URLs, CMS stub pages). NOT used
		/// for security: the security boundary (primary domain / configured
		/// subdomains) lives in <c>IsInDownloadScope</c>'s domain gate above this
		/// check and is enforced regardless of <c>DownloadExclusions</c> state.
		///
		/// Validated at config-load time by <see cref="DownloadExclusionsConfigValidator"/>:
		/// an entry with <c>Enabled = true</c> and empty <c>Value</c> halts at
		/// startup, because <c>link.Contains("")</c> is always true and would
		/// reject every link — a silent crawl-killer the operator should hear
		/// about at edit time, not via an empty result set.
		/// </summary>
		public List<CrawlLinkExclusion> DownloadExclusions { get; set; } = [];

		/// <summary>
		/// The site's own patterns the Comparison sets aside in the text of PDFs: what its PDF
		/// generator prints anew on every regeneration (a date in a form number, a dated line).
		/// Regular expressions, run without backtracking. Empty: nothing is set aside. Site-specific
		/// by nature — keep them in crawlersuite.config.json.
		/// </summary>
		public List<ComparisonPattern> ComparisonPdfSetAside { get; set; } = [];

		// ── Resource bloat thresholds ───────────────────────────────────────
		// Base64 asset threshold — decoded assets above this size are flagged
		// as BASE64_LARGE in IssueTracking and log 19. Default: 100KB.
		public int BloatThresholdBase64Kilobytes { get; set; } = 100;

		// JS file threshold — JS files above this size trigger EXTRA_JS in
		// log 21 when above baseline. Default: 500KB.
		public int BloatThresholdJsKilobytes { get; set; } = 500;

		// CSS file threshold — CSS files above this size trigger EXTRA_CSS in
		// log 21 when above baseline. Default: 500KB.
		public int BloatThresholdCssKilobytes { get; set; } = 500;

		// Minimum total delta above baseline (JS+CSS combined) for a page to
		// appear in log 21. Adjust as the site improves to surface next tier.
		// Default: 3000KB (3MB) — targets the most impactful offenders first.
		public int BloatThresholdAboveBaselineKilobytes { get; set; } = 3000;

		// File extensions to scan for embedded Base64 data URIs.
		// The extractor decodes found assets and saves them to the base64assets/
		// folder for manual review. Common values: ".js", ".css", ".html".
		// Leave empty to skip Base64 asset extraction entirely.
		public List<string> Base64AssetFileExtensions { get; set; } = [".js", ".mjs", ".css", ".html"];

		// [KEEP] Security boundary — only explicitly listed subdomain base URLs are
		// followed and downloaded. The crawler never blindly follows any subdomain
		// link it encounters. Each entry must be a full base URL (scheme + host),
		// e.g. "https://help.example.com". Authored PER SITE (SiteConfig.UrlSubdomainsAllowed)
		// and projected here by ResolveForSite before the crawl — which replaces any
		// top-level "UrlSubdomainsAllowed" value wholesale, since crawl scope is part
		// of a site's identity and must not be shared globally.
		// Empty until resolution; no subdomains followed unless the selected site lists them.
		public List<string> UrlSubdomainsAllowed { get; set; } = [];

		// Path prefixes used by Extractor to find URLs embedded in <script> block
		// JSON/config strings. Only paths starting with one of these prefixes are
		// extracted. Configure in crawlersuite.config.json — keep site-specific prefixes
		// out of the shared config to avoid leaking internal path structures.
		// Example: ["/content/", "/assets/", "/en/", "/fr/"]
		// Default empty — JSON path extraction disabled until prefixes are configured.
		public List<string> ExtendedCrawlJsonPathPrefixes { get; set; } = [];
		public string FilePattern { get; set; } = string.Empty;

		/// <summary>
		/// The bare page extension derived from <see cref="FilePattern"/> — the glob
		/// "*.html" yields ".html", "*.aspx" yields ".aspx". Single derivation point
		/// for the many sites that need the extension (not the glob): file-extension
		/// filters, output filename construction, and the requested-URL settle signal.
		/// FilePattern is validated to the shape "*.ext", so this is always ".ext".
		/// </summary>
		public string FileExtension => FilePattern.TrimStart('*');

		// How the settle phase classifies a download when the requested extension,
		// Content-Type header, and byte sniff disagree about it being HTML. One of:
		// "TrustByteSniff" (default), "TrustContentType", "Quarantine", "AnalyseBlindly".
		// Stored as a string and validated against the UnverifiedHtmlPolicy enum at
		// startup. Empty also means TrustByteSniff.
		// See README for each value's risk. Takes effect on a NEW crawl only; replay
		// reuses the original crawl's classification.
		public string UnverifiedHtmlPolicy { get; set; } = "TrustByteSniff";

		/// <summary>
		/// The <see cref="UnverifiedHtmlPolicy"/> string resolved to its enum.
		/// Empty / unset resolves to the default (<see cref="Crawler.UnverifiedHtmlPolicy.TrustByteSniff"/>).
		/// Unknown values are rejected by ValidateConfig at startup, so by the time
		/// this is read the string is either empty or a valid member name.
		/// </summary>
		public UnverifiedHtmlPolicy ResolvedUnverifiedHtmlPolicy =>
			string.IsNullOrWhiteSpace(UnverifiedHtmlPolicy)
				? CrawlerSuite.Core.UnverifiedHtmlPolicy.TrustByteSniff
				: Enum.Parse<UnverifiedHtmlPolicy>(UnverifiedHtmlPolicy, ignoreCase: true);

		// How the settle phase classifies a download when the requested extension,
		// Content-Type header, and byte sniff disagree about it being a PDF. One of:
		// "TrustByteSniff" (default), "TrustContentType", "Quarantine", "AnalyseBlindly".
		// Stored as a string and validated against the UnverifiedPdfPolicy enum at
		// startup. Empty also means TrustByteSniff. Separate from the HTML policy by design. Takes
		// effect on a NEW crawl only; replay reuses the original classification.
		public string UnverifiedPdfPolicy { get; set; } = "TrustByteSniff";

		/// <summary>
		/// The <see cref="UnverifiedPdfPolicy"/> string resolved to its enum.
		/// Empty / unset resolves to the default (<see cref="Crawler.UnverifiedPdfPolicy.TrustByteSniff"/>).
		/// Unknown values are rejected by ValidateConfig at startup, so by the time
		/// this is read the string is either empty or a valid member name.
		/// </summary>
		public UnverifiedPdfPolicy ResolvedUnverifiedPdfPolicy =>
			string.IsNullOrWhiteSpace(UnverifiedPdfPolicy)
				? CrawlerSuite.Core.UnverifiedPdfPolicy.TrustByteSniff
				: Enum.Parse<UnverifiedPdfPolicy>(UnverifiedPdfPolicy, ignoreCase: true);
		public string CustomDictionaryFile { get; set; } = string.Empty;

		/// <summary>
		/// Optional foreign-language dictionary filename (e.g. "user_foreign_languages.dic").
		/// A relaxed-policy allow-list of researched, comment-justified words from any
		/// script that the strict user/site dictionary rejects. Empty (default) = not
		/// loaded, no behaviour change. Consult-only: words are accepted but exempt from
		/// orphan/prune maintenance and cannot be added from triage.
		/// </summary>
		public string CustomForeignDictionaryFile { get; set; } = string.Empty;

		/// <summary>
		/// CMS content list — drives 04/05 logs, spell-ticket metadata, and the
		/// optional post-crawl pass. The list is exported manually from the CMS
		/// (currently as CSV; could be other tabular formats in the future), so
		/// the schema also carries an age threshold and a free-form comment
		/// shown to the operator when the file is stale.
		///
		/// Leave null / omit to disable all CMS-dependent features. Set Path
		/// empty for the same effect.
		///
		/// [KEEP] This is one object holding ALL causally-related fields:
		/// the toggle, the file location, freshness metadata, parsing rules,
		/// row filtering, and value extraction. Grouping them here makes the
		/// audit-via-delta surface this concern as a single coherent block.
		/// </summary>
		public CmsContentListConfig? CmsContentList { get; set; } = null;
		public List<string> QueryStringsToIgnoreForSelfLinkDetermination { get; set; } = [];
		public List<string> SitemapGeneratorExclusions { get; set; } = [];
		public List<string> SitemapGenerateForcedInclusions { get; set; } = [];

		// -- Dictionary maintenance --
		// End-of-run orphan/redundancy review for the user and site dictionaries.
		// Orphans are discovered by the cross-off usage recorder during spell-check
		// (Lexicon.UsageTracker) — an entry never consulted on any page is an orphan —
		// so there is no separate corpus scan. See DictionaryMaintenanceConfig.
		public DictionaryMaintenanceConfig DictionaryMaintenance { get; set; } = new();

		// -- Content quality checks --
		// Checks for editorial quality issues distinct from spelling:
		// ligatures from PDF copy-paste, and typographic quote problems.
		public ContentQualityConfig ContentQuality { get; set; } = new();

		// -- Triage --
		/// <summary>
		/// What triage counts: switch id → On, RideAlong or Off, for the switches set apart from
		/// their built-in default (<see cref="Triage.TriageSwitches"/>). Absent or empty: every switch
		/// at its default. One set for all sites; Triage itself only shows it.
		/// </summary>
		public Dictionary<string, string> TriageModes { get; set; } = new(StringComparer.Ordinal);

		/// <summary>
		/// The language of the ticket documents (PDF and XPS): "English" or "Deutsch". The app's screens
		/// stay English; see <see cref="Text.DocumentText"/>.
		/// </summary>
		public string TicketLanguage { get; set; } = Text.DocumentText.EnglishName;

		// -- SEO: the site's title patterns (see SeoConfig) --
		public SeoConfig Seo { get; set; } = new();

		// Asset-level quality checks on downloaded non-HTML assets (images):
		// metadata leakage (EXIF/GPS/author), suspicious pixel dimensions, and
		// byte-size sanity. See AssetQualityConfig below. Findings flow through
		// IssueTracking like the other analyzers.
		public AssetQualityConfig AssetQuality { get; set; } = new();

		// -- Content unwanted patterns --
		// Named pattern groups scanned against simplified HTML source.
		// Matches reported as UNWANTED_PATTERN in 10-content-quality-issues.log.
		// Configure in crawlersuite.config.json to keep internal patterns private.
		public List<ContentUnwantedPattern> ContentUnwantedPatterns { get; set; } = [];

		// Ticket generation and lifecycle. All fields nested under TicketGeneration —
		// see TicketGenerationConfig below. Set in crawlersuite.config.json.
		//
		// [KEEP] The overdue age is TicketGeneration.OverdueAfterDays, not a
		// top-level field, so all ticket-lifecycle and ticket-text-generation
		// fields live in one place.
		public TicketGenerationConfig TicketGeneration { get; set; } = new();

		// -- German compound word trailing-hyphen handling --
		// German compound word enumeration lists use trailing hyphens on prefixes
		// (e.g. "Lieferungs-, Zahlungs- und Rückgabebedingungen").
		// When a German-language page token ends with "-", the stem is checked against
		// the dictionary after stripping the hyphen, then after stripping each element.
		// Only applied when the page language resolves to "de" — never for English pages.
		// Default ["s"] covers the most common case: Lieferungs- → Lieferung.
		// Add further Fugenelemente (e.g. "en", "er", "e", "es") if real content needs them.
		public List<string> GermanFugenelemente { get; set; } = ["s"];

		/// <summary>
		/// Proper names that no dictionary can judge: an institution's name, possibly several words
		/// with a hyphen and a space ("Xyzzy- und Hausverband"), and optionally its acronym. A word of
		/// a name passes the spell check only where the whole name stands in the same text; the
		/// hyphen-spacing check takes the name as written on purpose. The acronym passes anywhere,
		/// with its exact capitals — also beyond the length the acronym rule leaves unchecked.
		/// </summary>
		public List<ProperName> ProperNames { get; set; } = [];

		// Pages not spell-checked are declared in SpellCheckEngine.PageLanguageOverrides: a path
		// with no language is not spell-checked.

		// URL patterns (case-insensitive substring match) for pages to skip entirely
		// during content quality checks. No entries are written to 10-content-quality-issues.log
		// for excluded pages.
		public List<string> ContentQualityExcludedUrls { get; set; } = [];

		// -- Dictionary configuration --
		// Dictionary bundles — one entry per language. LanguageCode must match the ISO 639-1
		// code used in html lang attributes (e.g. "en", "de", "fr"). DicFile and AffFile are
		// paths relative to the application working directory.
		// Configure in crawlersuite.config.json to keep file paths out of the shared config.
		public List<DictionaryBundleConfig> DictionaryBundles { get; set; } = [];

		// -- Spell checking engine --
		// Boilerplate groups + parallel-store output paths. All other spell settings
		// (dictionaries, prefixes, languages, exclusions) are read from their own keys.
		// Defaults to an empty engine config.
		public CrawlerSuite.Core.SpellCheck.SpellCheckEngineConfig SpellCheckEngine { get; set; } = new();

		// Prefixes listed here are stripped from hyphenated compound words before
		// spell-checking. The remainder after the hyphen is checked instead.
		// Example: "COMPANY-Beratung" with prefix "COMPANY" → checks "Beratung".
		// Matching is case-insensitive. A word made only of entries passes, one entry standing
		// alone included ("COMPANY"); each entry is also a known part of any hyphenated word.
		// Use this instead of adding every COMPANY-X variant to the user dictionary.
		// An entry can be switched off ({"Value": …, "Enabled": false}): kept, not applied, and judged
		// against the run's findings so the settings screen shows what it would do.
		public CrawlerSuite.Core.Settings.SwitchableList SpellCheckWordPrefixesToStrip { get; set; } = [];

		// -- Parallelism --
		// 0 means "use Environment.ProcessorCount" (the default).
		// MaxDegreeOfParallelism controls CPU-bound parallel work: HTML simplification,
		// normalization, attribute aggregation, self-link scanning, and lookup-file building.
		// MaxConcurrentPageDownloads and MaxConcurrentAssetDownloads control how many
		// HTTP requests the crawler issues simultaneously.
		public int MaxDegreeOfParallelism { get; set; } = 0;
		public int MaxConcurrentPageDownloads { get; set; } = 100;
		public int MaxConcurrentAssetDownloads { get; set; } = 200;

		/// <summary>
		/// The most requests per second the crawl sends to one server, pages and assets together
		/// (0 = no ceiling). 50 is about one and a half page views of a modern site every second.
		/// Above <see cref="GentleRequestsPerSecond"/> the settings warn: a crawl that fast can
		/// strain a site whose infrastructure you do not know.
		/// </summary>
		public int MaxRequestsPerSecond { get; set; } = 50;

		/// <summary>The rate above which the settings warn.</summary>
		public const int GentleRequestsPerSecond = 100;

		/// <summary>
		/// The most connections open to one server at a time, pages and assets together. A browser
		/// opens 6 over HTTP/1.1; 16 carries the default 50 requests a second at the answer times
		/// of a typical site.
		/// </summary>
		public int MaxConnectionsPerServer { get; set; } = 16;

		/// <summary>
		/// The highest HTTP version the crawl offers: "1.1", "2" or "3". Each server is spoken to
		/// in the best version it agrees to, up to this. 1.1, the default, gives every request a
		/// connection of its own while it runs, so a failure, a stall and a measurement belong to
		/// that request alone; 2 and 3 share one connection among many requests, as browsers do
		/// where the server offers them.
		/// </summary>
		public string HighestHttpVersion { get; set; } = "1.1";

		/// <summary>The versions <see cref="HighestHttpVersion"/> takes.</summary>
		public static readonly IReadOnlyList<string> HttpVersions = ["1.1", "2", "3"];

		/// <summary><see cref="HighestHttpVersion"/> as a version (1.1 when the value is unknown).</summary>
		[System.Text.Json.Serialization.JsonIgnore]
		public Version ResolvedHttpVersion => HighestHttpVersion switch
		{
			"3" => System.Net.HttpVersion.Version30,
			"2" => System.Net.HttpVersion.Version20,
			_ => System.Net.HttpVersion.Version11,
		};

		/// <summary>
		/// Largest single file the crawler stores, in MB (0 = no limit). A larger file is not
		/// stored: its first 64 KB and its headers are kept in the snapshot's oversized/ folder
		/// and it is listed in 34-oversized-files.log.
		/// </summary>
		public int MaxSingleFileMB { get; set; } = 150;

		/// <summary>
		/// Total download size at which the crawl stops, in GB (0 = no limit). Reaching it
		/// means something on the site is badly wrong; the operator then deletes the snapshot
		/// or deletes it and starts over.
		/// </summary>
		public int MaxCrawlDownloadGB { get; set; } = 10;

		/// <summary>A download that receives no data for this many seconds is aborted.</summary>
		public int StallTimeoutSeconds { get; set; } = 30;

		/// <summary><see cref="MaxSingleFileMB"/> in bytes.</summary>
		[System.Text.Json.Serialization.JsonIgnore]
		public long MaxSingleFileBytes => MaxSingleFileMB * 1024L * 1024L;

		/// <summary><see cref="MaxCrawlDownloadGB"/> in bytes.</summary>
		[System.Text.Json.Serialization.JsonIgnore]
		public long MaxCrawlDownloadBytes => MaxCrawlDownloadGB * 1024L * 1024L * 1024L;

		/// <summary>
		/// Resolved degree of parallelism: returns MaxDegreeOfParallelism when set to a
		/// positive value, otherwise falls back to Environment.ProcessorCount.
		/// </summary>
		public int ResolvedDegreeOfParallelism =>
			MaxDegreeOfParallelism > 0 ? MaxDegreeOfParallelism : Environment.ProcessorCount;

		[RequiresUnreferencedCode("Requires unreferenced code")]
		public static Config LoadFromJson(string filePath)
		{
			if (!File.Exists(filePath))
			{
				throw new FileNotFoundException("Config file not found", filePath);
			}

			return Parse(File.ReadAllText(filePath, Encoding.UTF8));
		}

		/// <summary>
		/// Reads a configuration from its JSON text and validates it, as <see cref="LoadFromJson"/>
		/// does for a file — the settings screen checks a change this way before it is saved.
		/// Throws <see cref="JsonException"/> on broken JSON and <see cref="InvalidOperationException"/>
		/// (or <see cref="ConfigHaltException"/>) when a value is not valid.
		/// </summary>
		[RequiresUnreferencedCode("Requires unreferenced code")]
		public static Config Parse(string json)
		{
			var config = JsonSerializer.Deserialize<Config>(FilterComments(json))
				?? throw new JsonException("Deserialization failed, config is null.");

			ValidateConfig(config);
			return config;
		}

		/// <summary>
		/// Projects one selected site onto this config so that every downstream
		/// consumer sees a fully-resolved single-site configuration and is unaware
		/// multi-site exists. Two operations:
		///   1. PROJECTION — the site's per-site fields are stamped onto their global
		///      targets: <c>Url</c> → <see cref="Url"/>, <c>UrlSubdomainsAllowed</c> →
		///      <see cref="UrlSubdomainsAllowed"/> (replaced wholesale — a security
		///      boundary, no leakage of a prior site's subdomains), <c>PostCrawlPass</c>
		///      → <see cref="CmsContentListConfig.PostCrawlPass"/>.
		///   2. TOKEN RESOLUTION — every <c>{tenant}</c> in the allow-listed path/
		///      filter/URL fields (see <see cref="TenantSubstitutableFields"/>) is
		///      replaced with the site's <see cref="SiteConfig.Tenant"/>. After
		///      resolution, any surviving <c>{tenant}</c> in a walked field is a halt
		///      (config issue — site missing Tenant / token typo — or code issue —
		///      a field that should be in the allow-list is not). Either way: stop, don't proceed.
		///
		/// Written to be REPEATABLE: it mutates this config in place from the site's
		/// own values, not by accumulating, so a multi-site sweep can call it
		/// once per site in a loop. Call BEFORE any consumption of <see cref="Url"/>
		/// (i.e. before the urlDirectory derivation) and BEFORE the
		/// per-site-dependent validation (<see cref="ValidateResolvedSite"/>).
		/// </summary>
		/// <summary>
		/// The tenant value substituted for <c>{tenant}</c> when a site's
		/// <see cref="SiteConfig.Tenant"/> is empty or whitespace. Chosen so that
		/// substitution yields a well-formed value in every field shape (a specific
		/// "default" path segment / a "default_..."-prefixed filename) rather than an
		/// empty segment that could match over-broadly or malform a path. Hardcoded
		/// (not a config knob) until a real need to override it arises.
		/// </summary>
		public const string DefaultTenant = "default";

		public void ResolveForSite(SiteConfig site)
		{
			ArgumentNullException.ThrowIfNull(site);

			// Record the resolved site identity. This is the truthful record of which
			// site was projected onto the config, and the single place downstream
			// surfaces read site identity from (e.g. the snapshot-prompt heading shows
			// "Name — Url"). Survives the projection so any consumer holding `config`
			// can show which site this run is for without re-plumbing.
			ResolvedSite = site;

			// 1. Projection — per-site identity/scope/toggle onto global targets.
			Url = site.Url;
			// Filter empty/whitespace entries: an empty string in the allow-list
			// would neutralise the entire scope boundary, because every consumer
			// tests `link.StartsWith(entry)` and StartsWith("") is always true —
			// turning "[ \"\" ]" into "allow every URL". A sloppy config
			// ("UrlSubdomainsAllowed": [ "" ]) must NOT silently disable
			// containment. See IsInDownloadScope and the redirect-allowed check.
			UrlSubdomainsAllowed = site.UrlSubdomainsAllowed is null
				? []
				: [.. site.UrlSubdomainsAllowed.Where(s => !string.IsNullOrWhiteSpace(s))]; // copy + sanitise, not alias — wholesale replace
			if (!site.UsesContentListResolved)
			{
				// The site does not use the content list: nothing reads it for this run — no
				// comparison, page source, CMS links or post-crawl pass.
				CmsContentList = null;
			}
			else if (CmsContentList is not null)
			{
				CmsContentList.PostCrawlPass = site.PostCrawlPass;
			}

			// 2. Token resolution over the allow-listed fields. An empty/whitespace
			// Tenant resolves {tenant} to DefaultTenant ("default") rather than to an
			// empty string: that keeps EVERY field shape valid — a path-prefix like
			// "/floes/site/{tenant}" becomes ".../default" (a specific prefix that
			// may MISS but never matches over-broadly or malforms), and a filename
			// template like "{tenant}_content.csv" becomes "default_content.csv" (a
			// usable shared-fallback file). Whether that fallback FILE must exist is
			// governed by PostCrawlPass exactly as for a real tenant: PostCrawlPass=false
			// tolerates its absence (graceful skip), PostCrawlPass=true requires it — so
			// a default site is safe with the pass off and simply needs default_content.csv
			// when the pass is on. No special-casing in validation; the CMS cascade sees
			// the resolved path like any other.
			//
			// {productiongroup} follows the same rules: empty/whitespace ProductionGroup
			// resolves to DefaultTenant ("default"); a default-group deeplink is a
			// harmless miss (no CMS route matches) rather than damage. Both tokens are
			// substituted across the SAME allow-list — they are peer tokens, not
			// per-field-typed; any allow-listed field can carry either or both, and
			// no-token fields are unaffected (replace is a no-op).
			var effectiveTenant = string.IsNullOrWhiteSpace(site.Tenant)
				? DefaultTenant
				: site.Tenant;
			var effectiveProductionGroup = string.IsNullOrWhiteSpace(site.ProductionGroup)
				? DefaultTenant
				: site.ProductionGroup;
			foreach (var field in TenantSubstitutableFields(this))
			{
				field.Set(SubstituteTokens(field.Get(), effectiveTenant, effectiveProductionGroup));
			}

			// Survives-halt — defensive only. Scoped to allow-listed fields, which are
			// exactly the fields just substituted, so in normal operation nothing
			// survives. The one real trigger is a pathological per-site value that itself
			// contains a literal token (e.g. Tenant="pre{tenant}post"), which re-introduces
			// it. NOTE: this does NOT catch a dev forgetting to register a new
			// templatable field in TenantSubstitutableFields — such a field is never
			// walked, so its surviving token is invisible here. That dev-time mistake
			// is guarded by the resolution test that asserts every registered field
			// resolves (see SiteResolutionTests); when a new field is added, the test
			// roster is extended alongside the allow-list, and a forgotten registration
			// shows up as a failing test, not a runtime halt (it is dev debt, not config debt).
			var unresolved = TenantSubstitutableFields(this)
				.Where(f => ContainsAnyToken(f.Get()))
				.Select(f => f.Name)
				.ToList();
			if (unresolved.Count > 0)
			{
				throw new InvalidOperationException(
					"Unresolved token(s) remain after resolving site "
					+ $"'{site.Name}' (Tenant='{site.Tenant}', ProductionGroup='{site.ProductionGroup}'): "
					+ $"{string.Join(", ", unresolved)}. "
					+ "Set the site's Tenant / ProductionGroup, fix a token typo, or — if a field "
					+ "legitimately needs a token but is not being resolved — add it to "
					+ "Config.TenantSubstitutableFields.");
			}
		}

		/// <summary>
		/// The settings (dotted paths, lists by name) in which <c>{tenant}</c> and
		/// <c>{productiongroup}</c> are resolved per site — the names of
		/// <see cref="TenantSubstitutableFields"/>; a test keeps the two in step. Elsewhere a
		/// placeholder stays as written.
		/// </summary>
		internal static readonly IReadOnlyList<string> SubstitutableSettings =
		[
			"CustomDictionaryFile",
			"CustomForeignDictionaryFile",
			"ExtendedCrawlJsonPathPrefixes",
			"TicketGeneration.CmsEditorBaseUrl",
			"TicketGeneration.CmsEditorBaseUrlSuffix",
			"CmsContentList.Path",
			"CmsContentList.RowFilter",
			"CmsContentList.RowNegativeFilter",
			"CmsContentList.RowsToExclude",
		];

		/// <summary>The names of the substitutable fields of a config, list positions stripped (tests).</summary>
		internal static IEnumerable<string> SubstitutableFieldNames(Config c) =>
			TenantSubstitutableFields(c).Select(f => f.Name.Split('[')[0]).Distinct();

		/// <summary>A value as it resolves for a site: tokens replaced, an empty tenant or group read as "default".</summary>
		internal static string ResolveTokens(string value, string? tenant, string? productionGroup) =>
			SubstituteTokens(
				value,
				string.IsNullOrWhiteSpace(tenant) ? DefaultTenant : tenant,
				string.IsNullOrWhiteSpace(productionGroup) ? DefaultTenant : productionGroup) ?? string.Empty;

		/// <summary>Whether a value holds a placeholder.</summary>
		internal static bool HasToken(string? value) => ContainsAnyToken(value);

		private static bool ContainsAnyToken(string? value)
			=> value is not null
				&& (value.Contains("{tenant}", StringComparison.Ordinal)
					|| value.Contains("{productiongroup}", StringComparison.Ordinal));

		/// <summary>
		/// Replaces every <c>{tenant}</c> with <paramref name="tenant"/> and every
		/// <c>{productiongroup}</c> with <paramref name="productionGroup"/>. Null-safe
		/// (returns input). Order is irrelevant — neither token's value contains the
		/// other's literal in any normal configuration, and the pathological case is
		/// caught by the survives-halt.
		/// </summary>
		private static string? SubstituteTokens(string? value, string tenant, string productionGroup)
		{
			if (value is null)
			{
				return null;
			}

			var v = value.Replace("{tenant}", tenant, StringComparison.Ordinal);
			v = v.Replace("{productiongroup}", productionGroup, StringComparison.Ordinal);
			return v;
		}

		/// <summary>A single get/set accessor over one substitutable string field, with its name for halts.</summary>
		private readonly struct TenantField(string name, Func<string?> get, Action<string?> set)
		{
			public string Name { get; } = name;
			public string? Get() => get();
			public void Set(string? v) => set(v);
		}

		/// <summary>
		/// The ALLOW-LIST of fields in which <c>{tenant}</c> is resolved per site.
		///
		/// ════════════════════ HOW TO ADD A FIELD ════════════════════
		/// Add a field here IFF it is a path / row-filter / URL that legitimately
		/// VARIES BY TENANT — i.e. the same global value would be WRONG for a
		/// different tenant. Add it as one more <c>yield return</c> with its
		/// dotted name (for halt messages) and a get/set pair.
		///
		/// Do NOT add: parsing mechanics (ColumnDelimiter, ColumnIndex, ValueSuffix,
		/// PathStripPrefix), attribute/tag/regex lists, checksums, enum-name policies,
		/// or any operator-facing prose (CmsContentList.Comment). These never carry a tenant
		/// and walking them would risk corrupting a value or raising a false survives-halt.
		///
		/// List<string> fields are resolved element-wise via an indexer accessor.
		/// CmsContentList is nullable — its fields are only yielded when present.
		/// ═════════════════════════════════════════════════════════════
		/// </summary>
		private static IEnumerable<TenantField> TenantSubstitutableFields(Config c)
		{
			// ── Top-level path / URL fields ───────────────────────────────────

			// CustomDictionaryFile — a {tenant}-templated dictionary filename
			// (e.g. "user_{tenant}_dictionary.dic") resolves to a per-site dictionary
			// path immediately, no other machinery required. (Per-site dictionary
			// WRITE governance — shared-vs-isolated, mutate authority — is not handled
			// here; the path templating is independent of it.)
			yield return new("CustomDictionaryFile",
				() => c.CustomDictionaryFile, v => c.CustomDictionaryFile = v ?? string.Empty);

			yield return new("CustomForeignDictionaryFile",
				() => c.CustomForeignDictionaryFile,
				v => c.CustomForeignDictionaryFile = v ?? string.Empty);

			for (int i = 0; i < c.ExtendedCrawlJsonPathPrefixes.Count; i++)
			{
				int idx = i;   // capture
				yield return new($"ExtendedCrawlJsonPathPrefixes[{idx}]",
					() => c.ExtendedCrawlJsonPathPrefixes[idx],
					v => c.ExtendedCrawlJsonPathPrefixes[idx] = v ?? string.Empty);
			}

			// ── TicketGeneration — CMS deeplink base/suffix for ticket drafts ──
			yield return new("TicketGeneration.CmsEditorBaseUrl",
				() => c.TicketGeneration.CmsEditorBaseUrl,
				v => c.TicketGeneration.CmsEditorBaseUrl = v ?? string.Empty);
			yield return new("TicketGeneration.CmsEditorBaseUrlSuffix",
				() => c.TicketGeneration.CmsEditorBaseUrlSuffix,
				v => c.TicketGeneration.CmsEditorBaseUrlSuffix = v ?? string.Empty);

			// ── CmsContentList path / filter fields (nullable object) ──────────
			if (c.CmsContentList is { } cms)
			{
				yield return new("CmsContentList.Path",
					() => cms.Path, v => cms.Path = v ?? string.Empty);
				yield return new("CmsContentList.RowFilter",
					() => cms.RowFilter, v => cms.RowFilter = v ?? string.Empty);
				yield return new("CmsContentList.RowNegativeFilter",
					() => cms.RowNegativeFilter, v => cms.RowNegativeFilter = v ?? string.Empty);
				for (int i = 0; i < cms.RowsToExclude.Count; i++)
				{
					int idx = i;   // capture
					yield return new($"CmsContentList.RowsToExclude[{idx}]",
						() => cms.RowsToExclude[idx],
						v => cms.RowsToExclude[idx] = v ?? string.Empty);
				}

				// Switched-off entries too: they are counted, so they must name the same paths.
				for (int i = 0; i < cms.RowsToExclude.Disabled.Count; i++)
				{
					int idx = i;   // capture
					yield return new($"CmsContentList.RowsToExclude[off {idx}]",
						() => cms.RowsToExclude.Disabled[idx],
						v => cms.RowsToExclude.Disabled[idx] = v ?? string.Empty);
				}
			}
		}

		private static string FilterComments(string json)
		{
			// Comment lines are blanked, not removed, so a JSON error's line number still
			// points at the right line of the file the operator edits.
			return string.Join("\n", json.Split('\n').Select(line => line.TrimStart().StartsWith("//") ? string.Empty : line));
		}

		private static void ValidateConfig(Config config)
		{
			// Only validate properties that are genuinely always required.
			List<string> errors = [];

			// Sites collection — multi-site is mandatory (there is no top-level Url
			// fallback). The effective Url is validated AFTER resolution
			// (ValidateResolvedSite), since config.Url is empty until a site is
			// projected onto it. Here we check only what is knowable pre-resolution:
			// the collection is present and holds exactly one primary.
			if (config.Sites.Count == 0)
			{
				errors.Add(
					"Sites must contain at least one site. Add one on the Sites tab of Settings.");
			}
			else
			{
				int primaryCount = config.Sites.Count(s => s.IsPrimary);
				if (primaryCount == 0)
				{
					errors.Add(
						"Exactly one site must have IsPrimary:true (found none). The primary "
						+ "is the site the Run page preselects.");
				}
				else if (primaryCount > 1)
				{
					errors.Add(
						$"Exactly one site must have IsPrimary:true (found {primaryCount}: "
						+ $"{string.Join(", ", config.Sites.Where(s => s.IsPrimary).Select(s => $"'{s.Name}'"))}). "
						+ "Two primaries is a contradiction — commonly a copied site block whose "
						+ "IsPrimary flag was not changed.");
				}

				// Each site needs its own Url (the per-site identity). Empty Url on a
				// site is a definite config error regardless of which site is selected.
				for (int i = 0; i < config.Sites.Count; i++)
				{
					if (string.IsNullOrWhiteSpace(config.Sites[i].Url))
					{
						errors.Add($"Sites[{i}] (Name='{config.Sites[i].Name}') has an empty Url.");
					}
				}
			}

			if (string.IsNullOrWhiteSpace(config.BaseDirectory))
			{
				errors.Add("BaseDirectory is required.");
			}

			if (string.IsNullOrWhiteSpace(config.FilePattern))
			{
				errors.Add("FilePattern is required.");
			}
			else if (!IsValidFilePattern(config.FilePattern))
			{
				errors.Add(
					$"FilePattern must be a glob of the form \"*.ext\" where ext is " +
					$"1-8 letters or digits (e.g. \"*.html\"); got \"{config.FilePattern}\".");
			}

			// SEO title templates — when set, each entry must contain exactly one
			// {title} placeholder. The literals around it are the expected brand
			// framing; zero placeholders means nothing to extract/measure, more than
			// one is ambiguous. A title conforms if it matches any entry. (Invisible-
			// character validation happens in the halt section below, where any visible
			// script is permitted.)
			for (int i = 0; i < config.Seo.TitleTemplates.Count; i++)
			{
				var entry = config.Seo.TitleTemplates[i];
				if (string.IsNullOrEmpty(entry))
				{
					continue;
				}

				int placeholderCount = entry.Split("{title}").Length - 1;
				if (placeholderCount != 1)
				{
					errors.Add(
						$"Seo.TitleTemplates[{i}] must contain exactly one \"{{title}}\" placeholder; " +
						$"found {placeholderCount} in \"{entry}\".");
				}
			}
			if (string.IsNullOrWhiteSpace(config.CustomDictionaryFile))
			{
				errors.Add("CustomDictionaryFile is required.");
			}

			if (config.MaxDegreeOfParallelism < 0)
			{
				errors.Add("MaxDegreeOfParallelism must be 0 (auto) or a positive integer.");
			}

			if (config.MaxConcurrentPageDownloads < 1)
			{
				errors.Add("MaxConcurrentPageDownloads must be at least 1.");
			}

			if (config.MaxConcurrentAssetDownloads < 1)
			{
				errors.Add("MaxConcurrentAssetDownloads must be at least 1.");
			}

			if (config.MaxRequestsPerSecond < 0)
			{
				errors.Add("MaxRequestsPerSecond must be 0 (no ceiling) or a positive number.");
			}

			if (config.MaxConnectionsPerServer < 1)
			{
				errors.Add("MaxConnectionsPerServer must be at least 1.");
			}

			if (!HttpVersions.Contains(config.HighestHttpVersion))
			{
				errors.Add($"HighestHttpVersion must be one of {string.Join(", ", HttpVersions)}.");
			}

			// Boilerplate XPath selectors are compiled once here, so a typo halts at start-up
			// with its location instead of failing the prune after a full crawl.
			var groups = config.SpellCheckEngine?.BoilerplateGroups ?? [];
			for (int g = 0; g < groups.Count; g++)
			{
				var selectors = groups[g]?.BoilerplateSelectors ?? [];
				for (int k = 0; k < selectors.Count; k++)
				{
					var sel = selectors[k];
					if (sel != null && CrawlerSuite.Core.Boilerplate.BoilerplateAttribute.IsAttributeType(sel.Type)
						&& CrawlerSuite.Core.Boilerplate.BoilerplateAttribute.Check(sel.Value) is { } attributeProblem)
					{
						errors.Add($"SpellCheckEngine.BoilerplateGroups[{g}].BoilerplateSelectors[{k}] ('{sel.Value}'): {attributeProblem}");
						continue;
					}

					if (sel == null || !string.Equals(sel.Type?.Trim(), "xpath", StringComparison.OrdinalIgnoreCase)
						|| string.IsNullOrWhiteSpace(sel.Value))
					{
						continue;
					}

					try
					{
						System.Xml.XPath.XPathExpression.Compile(sel.Value.Trim());
						if (CrawlerSuite.Core.Boilerplate.XpathShape.EndsInAttribute(sel.Value))
						{
							errors.Add($"SpellCheckEngine.BoilerplateGroups[{g}].BoilerplateSelectors[{k}] ('{sel.Value}') "
								+ "selects attributes: as an xpath part it would remove their elements. Use the type \"attribute\" (e.g. script/@data-x).");
						}
					}
					catch (System.Xml.XPath.XPathException ex)
					{
						errors.Add($"SpellCheckEngine.BoilerplateGroups[{g}].BoilerplateSelectors[{k}] ('{sel.Value}') "
							+ $"is not a valid XPath expression: {ex.Message}");
					}
				}
			}

			var setAside = config.ComparisonPdfSetAside ?? [];
			for (int i = 0; i < setAside.Count; i++)
			{
				if (setAside[i] is { Enabled: true } entry && CrawlerSuite.Core.Comparison.PdfSetAside.Problem(entry) is { } setAsideProblem)
				{
					errors.Add($"ComparisonPdfSetAside[{i}] ('{entry.Name}'): {setAsideProblem}");
				}
			}

			if (config.SpellCheckEngine?.AcronymMaxLength is < 1 or > 5)
			{
				errors.Add("SpellCheckEngine.AcronymMaxLength must be from 1 to 5 letters.");
			}

			if (config.MaxSingleFileMB < 0)
			{
				errors.Add("MaxSingleFileMB must be 0 (no limit) or a positive number of megabytes.");
			}

			if (config.MaxCrawlDownloadGB < 0)
			{
				errors.Add("MaxCrawlDownloadGB must be 0 (no limit) or a positive number of gigabytes.");
			}

			if (config.StallTimeoutSeconds < 1)
			{
				errors.Add("StallTimeoutSeconds must be at least 1.");
			}

			// UnverifiedHtmlPolicy — empty (= default TrustByteSniff) or a valid enum
			// member name. Reject anything else with the allowed set, so a typo halts
			// at startup rather than silently falling back. Mirrors how other enum-as-
			// string options are guarded.
			if (!string.IsNullOrWhiteSpace(config.UnverifiedHtmlPolicy)
				&& !Enum.TryParse<UnverifiedHtmlPolicy>(config.UnverifiedHtmlPolicy, ignoreCase: true, out _))
			{
				errors.Add(
					$"UnverifiedHtmlPolicy '{config.UnverifiedHtmlPolicy}' is not valid. " +
					"Use one of: TrustByteSniff (default), TrustContentType, Quarantine, AnalyseBlindly.");
			}

			// UnverifiedPdfPolicy — same rule as the HTML policy, separate enum.
			if (!string.IsNullOrWhiteSpace(config.UnverifiedPdfPolicy)
				&& !Enum.TryParse<UnverifiedPdfPolicy>(config.UnverifiedPdfPolicy, ignoreCase: true, out _))
			{
				errors.Add(
					$"UnverifiedPdfPolicy '{config.UnverifiedPdfPolicy}' is not valid. " +
					"Use one of: TrustByteSniff (default), TrustContentType, Quarantine, AnalyseBlindly.");
			}

			// CheckDecomposition — empty (= default FlagAll) or a valid enum member.
			if (!string.IsNullOrWhiteSpace(config.ContentQuality.CheckDecomposition)
				&& !Enum.TryParse<DecompositionMode>(config.ContentQuality.CheckDecomposition, ignoreCase: true, out _))
			{
				errors.Add(
					$"ContentQuality.CheckDecomposition '{config.ContentQuality.CheckDecomposition}' is not valid. " +
					"Use one of: FlagAll (default), FlagMixed, off.");
			}

			ValidateProperNames(config.ProperNames, errors);

			if (errors.Count > 0)
			{
				throw new InvalidOperationException(
					"Config validation failed:\n" + string.Join("\n", errors.Select(e => $"  - {e}")));
			}

			// SpellCheckWordPrefixesToStrip — HALT on any suspicious character.
			// A contaminated prefix silently disables spell-check coverage for matching
			// compound words, making this a potential data integrity attack vector.
			// ValidateConfig is static and knows no run mode, so false is passed (show the
			// error block) — this is always a startup error that needs operator attention.
			CharacterValidator.ValidateListHalt(
				"SpellCheckWordPrefixesToStrip",
				config.SpellCheckWordPrefixesToStrip,
				silent: false);

			// Seo.TitleTemplates — HALT on any invisible character. Each template is
			// matched strictly against page titles; an invisible char (zero-width,
			// NBSP, BOM, bidi control) would silently fail every match and flood
			// InconsistentTitleFormat findings. Uses the invisible-only validator so
			// a legitimate non-Latin brand name (Cyrillic, CJK, …) is permitted —
			// only what a human cannot see is rejected.
			for (int i = 0; i < config.Seo.TitleTemplates.Count; i++)
			{
				var entry = config.Seo.TitleTemplates[i];
				if (string.IsNullOrEmpty(entry))
				{
					continue;
				}

				CharacterValidator.ValidateInvisibleHalt(
					$"Seo.TitleTemplates[{i}]", entry, silent: false);
			}

			// PathShortenSegments — entries must be 4+ chars to actually
			// shorten the path. /<segment>/ → /.../ saves chars only when the
			// segment is longer than 3. Mode-conditional behavior matching the
			// stale-CSV gate: interactive halts loudly so the
			// operator fixes the typo immediately; silent warns and skips the
			// bad entries so scheduled runs still produce usable output (the
			// other entries in the list still apply). Mutates the config in
			// silent mode to remove bad entries — the rest of the run sees
			// only the valid ones.

			ValidateUnwantedPatterns(config);

			// NOTE: CmsContentList validation does NOT run here. It reads the (possibly
			// {tenant}-templated) Path and the per-site-projected PostCrawlPass, neither
			// of which is meaningful until a site has been resolved onto the config.
			// Running it pre-resolution would test an unresolved "...{tenant}..." path
			// (false IsPathFullyQualified / File.Exists results — a silently wrong
			// answer). It is invoked from
			// ValidateResolvedSite, after ResolveForSite. See that method.
		}

		/// <summary>
		/// Integrity checks for ContentUnwantedPatterns, run at load. Three rules,
		/// fail-fast (operator fixes one thing, re-runs), mirroring the CmsContentList
		/// cascade style. All three guard against SILENT no-ops — a misconfigured set
		/// that produces no error and no effect, which is the worst failure mode for a
		/// detector whose absence of output looks identical to "nothing to report":
		///
		///   A. Name uniqueness — Name is the IssueTracking identity key AND the target
		///      of any set's Reference, so a duplicate makes both ambiguous. Checked on
		///      non-empty Names only; empty-Name entries are unconfigured (see
		///      IsConfigured) and tolerated as placeholders.
		///   B. Reference resolves — a non-empty Reference must name an existing set; a
		///      typo would otherwise just quietly never coalesce.
		///   C. Reference only on an envelope — Reference is only honoured for a grouped
		///      opener/closer pair (GroupPatterns:true, exactly 2 Patterns); on any other
		///      shape the fold can never fire, so the Reference is inert. Reject it loudly.
		/// </summary>
		/// <summary>
		/// ProperNames: every entry has a name; an acronym, when given, is one word in capitals
		/// (digits allowed) with at least one letter; no name and no acronym twice. A typo must not
		/// quietly turn an entry into something else, so each mistake is named.
		/// </summary>
		internal static void ValidateProperNames(IReadOnlyList<ProperName>? names, List<string> errors)
		{
			if (names is null)
			{
				return;
			}

			var seenNames = new HashSet<string>(StringComparer.Ordinal);
			var seenAcronyms = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < names.Count; i++)
			{
				var entry = names[i];
				var at = $"ProperNames[{i}]";
				if (entry is null || string.IsNullOrWhiteSpace(entry.Name))
				{
					errors.Add($"{at} has no Name.");
					continue;
				}

				if (!seenNames.Add(CrawlerSuite.Core.Quality.ProperNames.Key(entry.Name)))
				{
					errors.Add($"{at}: the name \"{entry.Name.Trim()}\" is listed twice.");
				}

				// An empty acronym is no acronym.
				if (string.IsNullOrWhiteSpace(entry.Acronym))
				{
					continue;
				}

				var acronym = entry.Acronym;
				if (AcronymProblem(acronym) is { } problem)
				{
					errors.Add($"{at}: {problem}");
				}
				else if (!seenAcronyms.Add(acronym))
				{
					errors.Add($"{at}: the acronym \"{acronym}\" is listed twice.");
				}
			}
		}

		/// <summary>What is wrong with an acronym of <see cref="ProperNames"/>; null when it is fine or empty.</summary>
		public static string? AcronymProblem(string acronym) =>
			acronym.Length == 0 ? null
			: acronym.Any(char.IsLower) || !acronym.Any(char.IsLetter) || !acronym.All(char.IsLetterOrDigit)
				? $"The acronym \"{acronym}\" must be one word of capitals (digits allowed), e.g. \"ABCD\"."
				: null;

		private static void ValidateUnwantedPatterns(Config config)
		{
			var sets = config.ContentUnwantedPatterns;
			if (sets is null || sets.Count == 0)
			{
				return;  // Feature unused — nothing to validate.
			}

			// A — Name uniqueness (non-empty Names only).
			var duplicateNames = sets
				.Select(s => s.Name)
				.Where(n => !string.IsNullOrEmpty(n))
				.GroupBy(n => n, StringComparer.Ordinal)
				.Where(g => g.Count() > 1)
				.Select(g => g.Key)
				.ToList();
			if (duplicateNames.Count > 0)
			{
				throw new InvalidOperationException(
					"ContentUnwantedPatterns has duplicate Name(s): "
					+ string.Join(", ", duplicateNames.Select(n => $"'{n}'"))
					+ ". Names must be unique — a Name is the IssueTracking identity key and the "
					+ "target of any set's \"Reference\". Rename so each configured set is distinct.");
			}

			var names = new HashSet<string>(
				sets.Where(s => !string.IsNullOrEmpty(s.Name)).Select(s => s.Name),
				StringComparer.Ordinal);

			foreach (var set in sets)
			{
				// D — OnlyFlagUnbalanced is envelope-only: it spares well-formed bindings and
				// fires on malformed ones, which is meaningless on anything but a two-pattern
				// grouped pair. On any other shape it is silently inert, so reject it loudly
				// (mirrors rule C for Reference).
				if (set.OnlyFlagUnbalanced && (!set.GroupPatterns || set.Patterns.Count != 2))
				{
					throw new InvalidOperationException(
						$"ContentUnwantedPattern '{set.Name}' sets \"OnlyFlagUnbalanced\": true but is not an "
						+ "envelope. OnlyFlagUnbalanced only applies to a grouped opener/closer pair "
						+ "(GroupPatterns:true with exactly 2 Patterns) — on any other shape it is silently "
						+ "inert. Make this set an envelope or remove OnlyFlagUnbalanced.");
				}

				if (string.IsNullOrEmpty(set.Reference))
				{
					continue;  // The common case — no envelope linkage to check.
				}

				// C — Reference is meaningful only on an envelope. Check the shape of THIS
				// set before resolving the target, since a Reference here is already a
				// mistake regardless of whether the target exists.
				if (!set.GroupPatterns || set.Patterns.Count != 2)
				{
					throw new InvalidOperationException(
						$"ContentUnwantedPattern '{set.Name}' sets \"Reference\": \"{set.Reference}\" "
						+ "but is not an envelope. Reference only applies to a grouped opener/closer "
						+ "pair (GroupPatterns:true with exactly 2 Patterns) — on any other shape it is "
						+ "silently inert. Make this set an envelope or remove its Reference.");
				}

				// B — the named target must exist (case-sensitive, matching the detector).
				if (!names.Contains(set.Reference))
				{
					throw new InvalidOperationException(
						$"ContentUnwantedPattern '{set.Name}' has \"Reference\": \"{set.Reference}\", "
						+ "which names no configured set. Reference must match another set's Name "
						+ "exactly (case-sensitive). Fix the name or remove the Reference.");
				}
			}
		}

		/// <summary>
		/// Per-site-dependent validation that must run AFTER <see cref="ResolveForSite"/>,
		/// because it inspects values that only exist once a site is projected: the
		/// effective <see cref="Url"/> and the resolved/projected CmsContentList
		/// (Path with {tenant} substituted, PostCrawlPass stamped from the site).
		/// The caller runs this immediately after ResolveForSite and before the crawl.
		/// Throws InvalidOperationException (framed by LoadConfig's handler) on failure.
		/// </summary>
		public static void ValidateResolvedSite(Config config)
		{
			ArgumentNullException.ThrowIfNull(config);

			if (string.IsNullOrWhiteSpace(config.Url))
			{
				throw new InvalidOperationException(
					"Effective Url is empty after site resolution — the selected site has "
					+ "no Url. (This should have been caught by the per-site Url check at "
					+ "load; reaching here indicates a selection/resolution ordering bug.)");
			}

			// CmsContentList cascade — sees the resolved path and projected flag,
			// so its path-existence safety net checks the REAL path the run will use.
			ValidateJsFileScan(config);
			ValidateCmsContentList(config);
		}

		/// <summary>
		/// JS-file spell-check scan (ScanScriptFilesInDownload) is an explicit opt-in. If it is enabled
		/// but no dictionaries are named to check against, that is operator misconfiguration, not a
		/// quietly-idle diagnostic — halt at load with the rest of the config checks rather than letting
		/// the run finish and emit an empty log that looks like "nothing wrong". (This is the strict twin
		/// of BulkScanPageScript's lenient empty→no-op: that flag idle is harmless; this one explicitly
		/// asked for a scan it cannot deliver.) Checks the file scan's OWN list
		/// (ScriptFileScanDictionaries) — the inline blob's ScriptBulkScanDictionaries is a separate
		/// concern with its own lenient contract.
		/// </summary>
		private static void ValidateJsFileScan(Config config)
		{
			var js = config.SpellCheckEngine.SpellCheckJavaScript;
			if (js.ScanScriptFilesInDownload && js.ScriptFileScanDictionaries.Count == 0)
			{
				// A SETUP halt, not a failure. The empty-dictionary state is the tool's
				// opinion-free default by design; the screen teaches the model (you name the
				// languages; the linguistic judgement stays yours) and gives the two concrete fixes,
				// in a tone that empowers rather than scolds.
				throw new ConfigHaltException(
					"SETUP NEEDED · JS-file scan has no dictionaries",
					new[]
					{
						"The external JS-file scan (ScanScriptFilesInDownload) is ON, but its dictionary",
						"list (ScriptFileScanDictionaries) is empty. With nothing to check against, every",
						"word counts as a miss — the scan cannot tell prose from typos, so it will not run.",
						"",
						"This is by design. The tool ships opinion-free: it does not decide what is a word —",
						"you do, by naming the language dictionaries it should check against. The linguistic",
						"judgement, and its correctness, stays yours.",
						"",
						"Fix it by doing ONE of:",
						"",
						"  • Name the languages to check, e.g.",
						"        \"ScriptFileScanDictionaries\": [ \"de\", \"en\" ]",
						"    List every language whose prose may appear in your bundles — a word is flagged",
						"    only when ALL listed dictionaries reject it.",
						"",
						"  • Or turn the scan off:",
						"        \"ScanScriptFilesInDownload\": false",
						"",
						"Dictionaries are not bundled with the tool — source the Hunspell dictionaries you",
						"need and load them. Nothing is checked until you do.",
					});
			}
		}

		/// <summary>
		/// Cascade of CmsContentList configuration checks. Fails fast on the
		/// first condition that's actually broken — each throw produces a
		/// targeted, single-issue InvalidOperationException that the existing
		/// LoadConfig handler frames into the "CONFIG VALIDATION FAILED"
		/// block. Operator fixes one thing, re-runs.
		///
		/// Decision table:
		///   * Path empty, PostCrawlPass=false → OK (feature disabled).
		///   * Path empty, PostCrawlPass=true  → HALT (asked for feature
		///                                       with nothing configured).
		///   * Path non-empty but malformed    → HALT (typo).
		///   * Path well-formed but file missing,
		///       PostCrawlPass=false           → OK (graceful — feeds only
		///                                       ticket metadata, which
		///                                       tolerates absence).
		///   * Path well-formed but file missing,
		///       PostCrawlPass=true            → HALT (post-crawl pass
		///                                       cannot run).
		///   * Path well-formed, file present  → OK.
		/// </summary>
		private static void ValidateCmsContentList(Config config)
		{
			var list = config.CmsContentList;
			var path = list?.Path ?? string.Empty;
			var postCrawlPass = list?.PostCrawlPass ?? false;

			if (string.IsNullOrWhiteSpace(path))
			{
				if (postCrawlPass)
				{
					throw new InvalidOperationException(
						"CmsContentList.PostCrawlPass is true but CmsContentList.Path is empty. "
						+ "Either configure CmsContentList.Path or set CmsContentList.PostCrawlPass to false.");
				}

				return;  // Not configured + not asked for — feature disabled, OK.
			}

			// Path is non-empty. It must be syntactically valid. Catches typo
			// classes like ':\\Crawler\\file.csv' (missing drive letter on
			// Windows), 'home/user/file.csv' (missing leading slash on Unix),
			// or 'file.csv' (relative — ambiguous depending on CWD).
			if (!Path.IsPathFullyQualified(path))
			{
				throw new InvalidOperationException(
					$"CmsContentList.Path is not a fully-qualified absolute path: '{path}'. "
					+ "Provide a complete path including drive letter (Windows) or leading slash (Unix).");
			}

			// Path is well-formed. Existence is required only when the
			// post-crawl pass is on; otherwise a missing file is graceful
			// (the page-source lookup returns empty results).
			if (postCrawlPass && !File.Exists(path))
			{
				throw new InvalidOperationException(
					$"CmsContentList.PostCrawlPass is true but CmsContentList.Path does not exist: '{path}'. "
					+ "Refresh the file or set CmsContentList.PostCrawlPass to false.");
			}
		}

		/// <summary>
		/// Validates a crawl FilePattern: must be a glob of the form "*.ext" where
		/// ext is 1-8 letters/digits (e.g. "*.html", "*.htm", "*.aspx"). The tool
		/// is generic across sites whose pages may use different extensions, so the
		/// extension comes from config — but it flows straight into
		/// Directory.GetFiles/EnumerateFiles at many sites, so a malformed value
		/// (no "*.", a bare "*", path characters, or an implausibly long extension)
		/// must be rejected at config validation rather than silently matching
		/// nothing (a non-glob like "html" is treated by GetFiles as a literal
		/// filename and matches no files, with no error).
		/// </summary>
		internal static bool IsValidFilePattern(string pattern)
		{
			return FilePatternGlob().IsMatch(pattern);
		}

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex FilePatternGlobRegex = new(@"^\*\.[A-Za-z0-9]{1,8}$", RegexOptions.Compiled);

		private static Regex FilePatternGlob() => FilePatternGlobRegex;
	}

	public class TagItem
	{
		public string Tag { get; set; } = string.Empty;
		public List<string> ClassConditions { get; set; } = [];
	}

	public class ReplacementItem
	{
		public string Name { get; set; } = string.Empty;
		public string Value { get; set; } = string.Empty;
		public string Comment { get; set; } = string.Empty;
		public string Replacement { get; set; } = string.Empty;
		/// <summary>
		/// URL patterns (case-insensitive substring match) this replacement applies to.
		/// Empty = global, applies to all pages. Populated = scoped to matching pages only.
		/// Saves normalization overhead for site-specific replacements affecting few pages.
		/// </summary>
		public List<string> Pages { get; set; } = [];
	}

	/// <summary>
	/// Maps a language (or a flavour of it) to its Hunspell dictionary files.
	/// LanguageCode is written as pages write html lang (e.g. "de", "fr", "en-GB", "en-US").
	/// DicFile and AffFile are paths relative to the application working directory.
	///
	/// DicChecksum and AffChecksum are SHA-256 hex strings (lowercase, 64 chars,
	/// no algorithm prefix) of the corresponding files, used for dictionary
	/// integrity verification. Empty values trigger a bootstrap
	/// halt on startup: the app computes and writes the expected checksums to
	/// application.log, then halts and asks the operator to paste them into
	/// config. Non-empty mismatched values also trigger halt. See
	/// Integrity.CheckOrHalt for the verification logic.
	/// </summary>
	public class DictionaryBundleConfig
	{
		/// <summary>
		/// Human-readable language name shown wherever the app refers to this bundle — e.g. "Greek",
		/// rendered as "Greek (el)". Required: a bundle with no DisplayName halts at the dictionary
		/// check (in the calm CONFIG CHECK style), because the app cannot label an unnamed bundle.
		/// </summary>
		public string DisplayName { get; set; } = string.Empty;

		/// <summary>
		/// Optional operator note travelling with the bundle — why this specific dictionary was chosen
		/// (e.g. "pt_PT not pt_BR — audience is Portugal"). Free text, never validated, never part of
		/// any checksum. Pure documentation for the next person reading the config.
		/// </summary>
		public string Comment { get; set; } = string.Empty;

		/// <summary>
		/// The language as pages write it in lang: "de", or a flavour such as "en-GB" / "en-US".
		/// Bundles sharing the language before the hyphen are flavours of one language.
		/// </summary>
		public string LanguageCode { get; set; } = string.Empty;

		/// <summary>
		/// For a language with more than one flavour: this flavour checks pages that name only the
		/// language ("en") or none (through the default language). Ignored for a language with one
		/// dictionary. A page naming a flavour ("en-US") gets that flavour.
		/// </summary>
		public bool Primary { get; set; }

		public string DicFile { get; set; } = string.Empty;
		public string AffFile { get; set; } = string.Empty;
		public string DicChecksum { get; set; } = string.Empty;
		public string AffChecksum { get; set; } = string.Empty;
	}

	/// <summary>One pattern of <see cref="Config.ComparisonPdfSetAside"/>.</summary>
	public record ComparisonPattern
	{
		/// <summary>What the pattern stands for; the comparison counts its matches under this name.</summary>
		public string Name { get; init; } = "";

		/// <summary>The regular expression (no lookarounds, no back-references).</summary>
		public string Pattern { get; init; } = "";

		/// <summary>False keeps the entry as a note without effect.</summary>
		public bool Enabled { get; init; } = true;

		/// <summary>Why it is set aside.</summary>
		public string Comment { get; init; } = "";
	}

	/// <summary>
	/// One operator-curated exclusion entry for <see cref="Config.DownloadExclusions"/>.
	/// Applied by <see cref="Urls.Validity.IsInDownloadScope"/> as a case-insensitive substring
	/// check: any link whose URL contains <see cref="Value"/> anywhere is rejected
	/// (when <see cref="Enabled"/> is true).
	///
	/// Used for operational filtering — sections to skip because they're large,
	/// uninteresting, or noise — NOT for security. The security boundary lives
	/// in <c>IsInDownloadScope</c>'s domain gate and is enforced regardless of what
	/// is or isn't in <c>DownloadExclusions</c>.
	///
	/// Validated at startup by <see cref="DownloadExclusionsConfigValidator"/>:
	/// enabled entries with empty <see cref="Value"/> halt the app, since
	/// <c>Contains("")</c> matches every link and would reject the entire crawl.
	/// </summary>
	public record CrawlLinkExclusion
	{
		/// <summary>
		/// The substring searched (case-insensitive) inside link URLs.
		/// Required for enabled entries; empty Value on an enabled entry is a
		/// startup error (would reject every link).
		/// </summary>
		public string Value { get; init; } = "";

		/// <summary>
		/// When false, this exclusion is ignored entirely. Convenient for
		/// temporarily disabling a pattern without deleting it from config —
		/// preserves the audit trail (<see cref="Value"/> + <see cref="Comment"/>)
		/// for the next investigator. Default true.
		/// </summary>
		public bool Enabled { get; init; } = true;

		/// <summary>
		/// Operator's note about why this pattern is excluded. Not consumed
		/// by the function; surfaces in operator-facing audit reads (config
		/// inspection, future log entries). Default empty.
		/// </summary>
		public string Comment { get; init; } = "";
	}
	/// <see cref="Config.Sites"/>). A field belongs here iff it is actively WRONG
	/// when shared across sites: identity, crawl scope, the default-site marker, and
	/// the post-crawl-pass toggle. Everything structurally common stays global;
	/// tenant-varying global fields use the <c>{tenant}</c> token instead of being
	/// duplicated here.
	///
	/// These fields are projected onto the global <see cref="Config"/> by
	/// <see cref="Config.ResolveForSite"/> before the pipeline runs — <c>Url</c> →
	/// <see cref="Config.Url"/>, <c>UrlSubdomainsAllowed</c> →
	/// <see cref="Config.UrlSubdomainsAllowed"/>, <c>PostCrawlPass</c> →
	/// <see cref="CmsContentListConfig.PostCrawlPass"/> — so they must NOT also be
	/// authored at those global locations (resolution overwrites them).
	/// </summary>
	public class SiteConfig
	{
		/// <summary>
		/// Operator-facing label for the site, shown in the interactive selection
		/// menu and in logs. Cosmetic identity only — NOT the path-namespacing key
		/// (the snapshot tree and cross-run files still derive from <see cref="Url"/>
		/// exactly as in single-site operation, so they isolate for free).
		/// </summary>
		public string Name { get; set; } = string.Empty;

		/// <summary>
		/// Per-site substitution value for the <c>{tenant}</c> token. Any global
		/// string field (scalar or list element) containing <c>{tenant}</c> is
		/// resolved against this value by <see cref="Config.ResolveForSite"/> — e.g.
		/// a global CMS path <c>"...\{tenant}\content.csv"</c> or a JSON-path prefix
		/// <c>"/content/site/{tenant}/..."</c>. This is what lets one global config
		/// body serve many same-kind sites without duplicating the templated fields
		/// per site; only the token flips. Required when any resolved field uses the
		/// token (an unresolved <c>{tenant}</c> surviving resolution is a halt).
		/// </summary>
		public string Tenant { get; set; } = string.Empty;

		/// <summary>
		/// Per-site value substituted for the <c>{productiongroup}</c> token. Used
		/// for CMS deeplink routing (values like "tide01" / "floe02"). Optional:
		/// an empty/whitespace value resolves to <see cref="Config.DefaultTenant"/>
		/// ("default"), same fallback as <see cref="Tenant"/> — a deeplink built with
		/// a default group simply won't match anything in the CMS routing, which is a
		/// harmless miss (broken link the operator notices on a ticket) rather than
		/// damage (a wrong-group deeplink that silently succeeds).
		/// </summary>
		public string ProductionGroup { get; set; } = string.Empty;

		/// <summary>
		/// The one default site. Silent mode runs the primary with no prompt;
		/// interactive mode pre-selects it on Enter. EXACTLY ONE site must set this
		/// true — zero (silent mode has no default) and more than one (a
		/// contradiction, commonly a copy-pasted block whose flag was not flipped)
		/// both halt at load with a named error.
		/// </summary>
		public bool IsPrimary { get; set; } = false;

		/// <summary>Target URL to crawl for this site. Projected onto
		/// <see cref="Config.Url"/> at resolution.</summary>
		public string Url { get; set; } = string.Empty;

		/// <summary>
		/// [KEEP] Security boundary, per site. The allowed subdomain base URLs for
		/// THIS site's crawl (full scheme+host, e.g. "https://assets.example.com").
		/// Projected onto <see cref="Config.UrlSubdomainsAllowed"/> at resolution.
		/// Crawl scope is part of a site's identity, so it is authored per-site and
		/// never shared globally — and the projection must replace the list wholesale
		/// per site (no leakage of a previous site's subdomains across a re-resolve).
		/// Empty = no subdomains followed for this site.
		/// </summary>
		public List<string> UrlSubdomainsAllowed { get; set; } = [];

		/// <summary>
		/// Per-site post-crawl-pass toggle. Projected onto
		/// <see cref="CmsContentListConfig.PostCrawlPass"/> at resolution. The rest
		/// of the CMS-content-list configuration stays global (shared structure,
		/// <c>{tenant}</c>-templated path); only the on/off decision is per-site,
		/// because one sibling may want the pass and another may not.
		/// </summary>
		public bool PostCrawlPass { get; set; } = false;

		/// <summary>
		/// Whether this site uses the CMS content list at all (comparison 04/05, page source,
		/// CMS links, post-crawl pass). Absent: the site uses it when it has a tenant, a
		/// production group or the post-crawl pass.
		/// </summary>
		public bool? UsesContentList { get; set; }

		/// <summary><see cref="UsesContentList"/>, with the rule for an absent value applied.</summary>
		[System.Text.Json.Serialization.JsonIgnore]
		public bool UsesContentListResolved => UsesContentList
			?? (!string.IsNullOrWhiteSpace(Tenant) || !string.IsNullOrWhiteSpace(ProductionGroup) || PostCrawlPass);
	}

	/// <summary>
	/// Configuration for the optional CMS content-list ingestion (freshness gate +
	/// spell-ticket metadata lookup). Holds every field causally related to the
	/// CMS-exported content list: the toggle, file location and freshness metadata,
	/// parsing rules, row filtering, and value extraction. The list is currently a
	/// CSV but the name is format-agnostic ("ContentList") to allow future tabular
	/// formats. Path empty / instance null = feature disabled across all consumers.
	///
	/// Field-order convention: toggle first (makes the rest moot if false),
	/// then location, then documentation, then freshness, then parsing,
	/// then filtering, then extraction. Top-to-bottom matches the order of
	/// operations the code performs on the file.
	/// </summary>
	public class CmsContentListConfig
	{
		/// <summary>
		/// When true, pages in the content list that were not reached by the
		/// normal crawl (listed in 05-not-directly-crawlable.log) are
		/// downloaded in a post-crawl pass and treated identically to normally
		/// discovered pages.
		///
		/// PROJECTION TARGET — not authored here in JSON. The per-site value lives
		/// on <see cref="SiteConfig.PostCrawlPass"/> and is stamped onto this
		/// property by <see cref="Config.ResolveForSite"/> before the pipeline runs;
		/// the ~4 consumers (the validation cascade, ShouldSkipPostCrawlPass,
		/// CmsContentListFreshness, SpellMetadataLookup) read it from here, unaware
		/// of multi-site. A <c>"PostCrawlPass"</c> key inside the JSON
		/// <c>CmsContentList</c> block is overwritten at resolution (it would be
		/// a second source of truth for a value that belongs to the site).
		///
		/// This flag only gates the download pass. The spell-ticket metadata
		/// lookup reads the file regardless of this flag (as long as Path is valid).
		/// </summary>
		public bool PostCrawlPass { get; set; } = false;

		/// <summary>Filesystem path to the content list file. Empty disables the feature.</summary>
		public string Path { get; set; } = string.Empty;

		/// <summary>
		/// Free-form prose shown to the operator when the file is over MaxAgeDays.
		/// Typically: where the export comes from, how to refresh it, and any
		/// known caveats about treating older exports as still valid.
		/// Ignored by code — operator-facing context only.
		/// </summary>
		public string Comment { get; set; } = string.Empty;

		/// <summary>
		/// Maximum acceptable age of the content list file in days, measured
		/// from last modification time. Set to 0 (or negative) to disable the
		/// age check. When the file is older than this and PostCrawlPass is
		/// true, the operator gets the stale-file gate.
		/// </summary>
		public int MaxAgeDays { get; set; } = 14;

		/// <summary>Field delimiter for the CSV file (e.g. ";", ","). Empty is read as ";".</summary>
		public string ColumnDelimiter { get; set; } = ";";

		/// <summary>
		/// Number of rows to skip at the start of the file before the header
		/// row. Use when the CMS export includes preamble rows above the
		/// column names. Default 0 = first row is the header.
		/// </summary>
		public int SkipRows { get; set; } = 0;

		/// <summary>
		/// Substring that must appear in a row for the row to be considered
		/// for processing. Rows not matching this are skipped silently.
		/// Empty = no positive filter (all rows considered).
		/// </summary>
		public string RowFilter { get; set; } = string.Empty;

		/// <summary>
		/// Pipe-delimited substrings: rows containing ANY of these are
		/// excluded from processing. Empty = no negative filter.
		/// </summary>
		public string RowNegativeFilter { get; set; } = string.Empty;

		/// <summary>
		/// Path-prefix entries to exclude from processing (substring match
		/// against the path column). Useful for skipping folder trees that
		/// the operator knows should not produce tickets or downloads.
		/// Switchable: an entry written as <c>{"Value": "…", "Enabled": false}</c> is kept
		/// but not applied — still counted, so the settings screen shows what it would leave out.
		/// </summary>
		public Settings.SwitchableList RowsToExclude { get; set; } = [];

		/// <summary>
		/// The column (its header name) that says whether a page is online. With
		/// <see cref="OnlinePattern"/> it decides which rows are pages on the site: only a row whose
		/// value matches is listed in log 04 and so can reach log 05 and the post-crawl pass; the
		/// others are listed in log 39. Which column and value mean "online" is the export's matter —
		/// the app knows none of its own. Empty (the default) or an empty pattern: no filter. Read from
		/// the header row after <see cref="SkipRows"/>; a name the header does not have turns the filter
		/// off with a warning in the application log.
		/// </summary>
		public string OnlineColumn { get; set; } = string.Empty;

		/// <summary>
		/// The value of <see cref="OnlineColumn"/> that means online; * matches anything, case ignored
		/// (as in the labels for column values). Every other value — an empty cell too — is not online.
		/// </summary>
		public string OnlinePattern { get; set; } = string.Empty;

		/// <summary>
		/// Index of the column containing the path data. The CMS may export
		/// several columns; this selects the one whose values become URLs /
		/// paths in downstream processing.
		/// </summary>
		public int ColumnIndex { get; set; }

		/// <summary>
		/// When true, the value extracted from ColumnIndex has its leading
		/// section replaced (per the prefix replacement logic in the CSV
		/// reader) before becoming a URL. Used when the CMS exports values
		/// like "/cms/path/foo" that need to become "https://site.test/foo".
		/// </summary>
		public bool ValuePrefixReplace { get; set; } = false;

		/// <summary>
		/// String appended to the extracted value when constructing the
		/// resulting URL (e.g. ".html" to convert /content/foo into
		/// /content/foo.html).
		/// </summary>
		public string ValueSuffix { get; set; } = string.Empty;

		/// <summary>
		/// Prefix to strip from spell-error URLs to derive the relative path
		/// for matching against the content list's path column. When empty
		/// (typical case), falls back to config.Url. Set explicitly only when
		/// the CMS export uses a different host or scheme than the public
		/// site URL.
		/// </summary>
		public string PathStripPrefix { get; set; } = string.Empty;
	}

	/// <summary>
	/// Where the content list says where a page comes from and how to reach it in the CMS:
	/// the source column and its names, the path, package and CMS path columns, the CMS
	/// editor link and the special-info mappings. They feed the Source column and the CMS
	/// link in Results, triage and the ticket document. All optional — empty leaves the part out.
	/// </summary>
	public class TicketGenerationConfig
	{
		/// <summary>
		/// Column name in the content list that identifies whether a page is
		/// local or external/inherited. Leave empty to omit location from
		/// ticket drafts.
		/// </summary>
		public string UrlSourceColumn { get; set; } = string.Empty;

		/// <summary>Value in UrlSourceColumn that means the page is locally managed.</summary>
		public string UrlSourceLocalName { get; set; } = string.Empty;

		/// <summary>Value in UrlSourceColumn that means the page is external/inherited.</summary>
		public string UrlSourceExternalName { get; set; } = string.Empty;

		/// <summary>
		/// Column name in CmsContentList that holds the package/module name.
		/// Exposed as {Package} in ticket templates. Leave empty to omit.
		/// </summary>
		public string PackageColumn { get; set; } = string.Empty;

		/// <summary>
		/// Column name in CmsContentList that holds the page path for matching.
		/// Leave empty to use the default path column resolution.
		/// </summary>
		public string PathColumn { get; set; } = string.Empty;

		/// <summary>
		/// Base URL of the CMS editor. When set, {CmsLink} in templates resolves to
		/// CmsEditorBaseUrl + path column value + CmsEditorBaseUrlSuffix. Leave empty to omit.
		/// Set in crawlersuite.config.json.
		/// Example: "https://cms.example.com/editor.html"
		/// </summary>
		public string CmsEditorBaseUrl { get; set; } = string.Empty;

		/// <summary>
		/// Suffix appended to the path column value when building {CmsLink}.
		/// Use when the CMS path stored in the CSV lacks a file extension.
		/// Example: ".html" — results in BaseUrl + /content/.../page + .html
		/// Leave empty if the path column already contains the full path.
		/// </summary>
		public string CmsEditorBaseUrlSuffix { get; set; } = string.Empty;

		/// <summary>
		/// Column name in CmsContentList that holds the page content path.
		/// Used to build {CmsLink}. Leave empty to omit CMS link.
		/// </summary>
		public string CmsPathColumn { get; set; } = string.Empty;

		/// <summary>
		/// Mappings from CSV column values to {SpecialInfo} label in ticket templates.
		/// Each mapping checks a column for a contained value and returns a label.
		/// First match wins. Leave empty to omit {SpecialInfo}.
		/// Set in crawlersuite.config.json.
		/// </summary>
		public List<SpecialInfoMapping> SpecialInfoMappings { get; set; } = [];
	}

	/// <summary>
	/// One entry in TicketGenerationConfig.SpecialInfoMappings.
	/// When the named CSV column contains Value (case-insensitive),
	/// {SpecialInfo} resolves to Label in the ticket template.
	/// </summary>
	public class SpecialInfoMapping
	{
		public string Column { get; set; } = string.Empty;
		/// <summary>Wildcard pattern — * matches any sequence. Case-insensitive.</summary>
		public string Pattern { get; set; } = string.Empty;
		public string Label { get; set; } = string.Empty;
	}

	/// <summary>
	/// SEO settings. Which pages are in search is a fixed rule (log 08's search column); what
	/// triage counts are its switches (Settings › Triage).
	/// </summary>
	public class SeoConfig
	{
		/// <summary>
		/// The site's title patterns, each with exactly one <c>{title}</c> placeholder; the text
		/// before and/or after it is the part the system adds to every title (a site name, often
		/// cut off in a search result by design) — <c>"{title} | Site"</c>, <c>"Site | {title}"</c>
		/// or both ends. A title matches if it carries one entry's text exactly (after decoding
		/// entities); entries are tried in order and the first match wins. Used by triage
		/// (Triage.SeoSource): a page in search whose title matches no entry is reported; a title
		/// counts as cut off only when its own part is cut; a title that is only the added text
		/// counts as missing. Empty (the default): no pattern check, and the whole title is
		/// measured. Validated at load: one placeholder per entry, no invisible characters (any
		/// visible script is allowed).
		/// </summary>
		public List<string> TitleTemplates { get; set; } = [];
	}

	/// <summary>
	/// Controls which content quality checks are run against downloaded HTML.
	/// Checks are distinct from spell-checking — they catch editorial quality issues
	/// such as ligatures from PDF copy-paste and typographic quote problems.
	/// </summary>
	/// <summary>
	/// Governs the decomposed-text (non-NFC) content-quality finding. See
	/// <see cref="ContentQualityConfig.CheckDecomposition"/> for the rationale and
	/// default. The spell path's NFC-at-lookup normalisation is unconditional
	/// and independent of this lever.
	/// </summary>
	public enum DecompositionMode
	{
		/// <summary>Silent — no decomposed-text findings.</summary>
		Off,

		/// <summary>Flag only pages that mix both forms of the same word — provably
		/// breaks in-page search and dedup, true regardless of any opinion on NFD.</summary>
		FlagMixed,

		/// <summary>Flag any decomposed text, plus mixed-form pages. The default:
		/// decomposed content is byte-fragile across consumers that may not normalise,
		/// and the producer's own surface usually masks the harm.</summary>
		FlagAll
	}

	public class ContentQualityConfig
	{
		/// <summary>Scan for ligature characters (ﬁ ﬂ ﬀ ﬃ ﬄ ﬅ ﬆ) in visible text.</summary>
		public bool CheckLigatures { get; set; } = true;

		// Detect text stored in a decomposed Unicode form (NFD) — a base letter plus
		// a separate combining mark, e.g. "ä" as a + U+0308 rather than the precomposed
		// U+00E4. It renders identically but is byte-fragile: correctness then depends on
		// every downstream consumer normalising to NFC, and they do not reliably do so —
		// site search, app search, autosuggest, AI tokenisers and partner feeds each
		// decide independently, often locale-dependently. Real failure mode: an app user
		// on locale XYZ searches "Äpfel", autosuggest offers "Äpfel kaufen" (proving the
		// content exists), the tap returns zero results — because the suggestion and the
		// query carry different normalisations. Near-impossible to reproduce downstream
		// (works on any device whose locale happens to agree), so detecting it at the
		// content source is the only reliable catch — hence the loudest default. One of:
		//   "FlagAll"   (default) — flag any decomposed text AND mixed-form pages.
		//   "FlagMixed"           — flag only pages mixing both forms of one word
		//                           (the airtight case: provably breaks search/dedup).
		//   "off"                 — silent. (The spell path still normalises at lookup
		//                           regardless; that is correctness, not this lever.)
		// Empty also means FlagAll. Validated at startup.
		public string CheckDecomposition { get; set; } = "FlagAll";

		/// <summary>
		/// <see cref="CheckDecomposition"/> resolved to its enum. Empty/unset → FlagAll.
		/// Unknown values are rejected by ValidateConfig at startup, so by the time this
		/// is read the string is empty or a valid member name.
		/// </summary>
		public DecompositionMode ResolvedCheckDecomposition =>
			string.IsNullOrWhiteSpace(CheckDecomposition)
				? DecompositionMode.FlagAll
				: Enum.Parse<DecompositionMode>(CheckDecomposition, ignoreCase: true);

		/// <summary>
		/// Flag pages where html lang="..." and meta name="language" content="..."
		/// disagree. The html lang attribute wins for spell-checking — the meta tag
		/// is what needs correcting. Prevents false spell errors from wrong-dictionary
		/// selection caused by an incorrect meta language tag.
		/// </summary>
		public bool CheckLanguageMismatch { get; set; } = true;

		/// <summary>
		/// Scan &lt;title&gt; text and &lt;meta&gt; content attributes for control
		/// characters, bidi controls, and zero-width characters. These are
		/// invisible to the CMS author but commonly arise from copy-paste from
		/// other applications (Word, PDFs, web pages). They break downstream
		/// log parsing and can in the worst case be used as an injection vector
		/// against tooling that consumes the crawled output.
		/// Emits <c>CONTROL_CHARS_IN_CONTENT</c> with the offending codepoint
		/// named in the detail. Independent of sanitization at log-writer
		/// boundaries — this is the operator-facing diagnostic, sanitization
		/// is the operational defense.
		/// </summary>
		public bool CheckControlCharsInContent { get; set; } = true;

		/// <summary>
		/// Report non-breaking characters that cannot do what they were inserted to
		/// do. A no-break space directly after a dash, a hyphen or another space
		/// cannot prevent the line breaking there (the preceding character already
		/// permits a break); a no-break space directly before an ordinary space is
		/// likewise powerless and additionally renders as a double space. Both are
		/// facts of the Unicode line breaking algorithm, independent of viewport,
		/// font and CSS, so the check is deterministic and language-agnostic.
		///
		/// Emits two issue types, split by what the author SEES rather than by which
		/// rule fired. <c>LINE_BREAK_GLUE_INERT</c> — glue after a dash or hyphen;
		/// invisible until the line wraps. <c>LINE_BREAK_GLUE_DOUBLE_SPACE</c> — glue
		/// beside an ordinary space, on either side; the reader sees a double space
		/// and the glue holds nothing together. Each carries the offending codepoint
		/// and the character that neutralises it. Unlike every other
		/// content-quality defect this one is invisible until the viewport is narrow
		/// enough to wrap at that exact point, so both findings ship with an
		/// operator-facing explanation (see <c>Quality.QualityEnrichment</c>) that
		/// travels into TicketText.log with the raised ticket.
		///
		/// Common sources are editor autocorrect inserting a no-break space around a
		/// dash, and copy-paste between authoring tools. Findings cluster where
		/// several people or several editors have worked the same pages.
		///
		/// Scope: VISIBLE TEXT NODES ONLY. Attribute values are not examined, and
		/// neither is content a script injects at runtime — what such a value finally
		/// becomes is decided by JavaScript this tool does not execute, so any finding
		/// drawn from it would rest on an assumed string. The character table is also
		/// scoped to Latin, Cyrillic, Greek and Arabic; see
		/// <c>Quality.LineBreakData</c> for what that excludes and how to extend it.
		/// </summary>
		public bool CheckNoBreakCharEffect { get; set; } = true;

		/// <summary>
		/// Flag pages that use typographic double-quote openers from more than one
		/// system (e.g. German „ and English " on the same page).
		/// </summary>
		public bool CheckQuoteSystemMixing { get; set; } = true;

		/// <summary>
		/// Stack-based pairing analysis. Flags:
		///   QUOTE_UNMATCHED  — opener with no closer
		///   QUOTE_WRONG_CLOSE — opener and closer from different systems
		///   QUOTE_WRONG_OPEN  — closer with no matching typographic opener
		///   QUOTE_WRONG_APOSTROPHE — ‘, ´ or ` in an apostrophe's place instead of ’
		/// More thorough than system-mixing check but may produce false positives
		/// on pages with apostrophes or complex nested quotes.
		/// </summary>
		public bool CheckQuotePairing { get; set; } = true;

		/// <summary>
		/// When spell-check produces ≥ TranslationIssueErrorThreshold errors on a page,
		/// check whether ≥ TranslationIssuePassRatio of those errors pass another loaded
		/// dictionary. If so, flag the page as POTENTIAL_TRANSLATION in 10-content-quality-issues.log
		/// and remove those words from the spell error log — they are translation issues,
		/// not spelling errors. Elements containing the untranslated text are reported
		/// as a group rather than listing individual words.
		/// </summary>
		public bool CheckPotentialTranslation { get; set; } = true;
		public int TranslationIssueErrorThreshold { get; set; } = 5;
		public double TranslationIssuePassRatio { get; set; } = 0.6;

		/// <summary>
		/// Flag anchor tags that close mid-word — a common CMS authoring mistake where
		/// the closing tag is placed one character too early, splitting a word across
		/// the tag boundary (e.g. "Autofil&lt;/a&gt;l form").
		/// Detected by the pattern &lt;/a&gt;[letter][space] in simplified HTML.
		/// </summary>
		public bool CheckSplitWordAnchors { get; set; } = true;

		/// <summary>
		/// Detect structurally malformed anchor tags in raw downloaded HTML.
		/// Reports two independent defect types:
		///   MISPLACED_ANCHOR_EMPTY — anchor with no visible text content (including
		///     anchors containing only empty child elements such as empty spans).
		///   ADJACENT_ANCHOR — two consecutive sibling anchors with no whitespace-text
		///     node between them. The name avoids a "SPLIT" overlap with
		///     SPLIT_WORD_ANCHOR: the finding is purely structural ("these tags
		///     collide in source") and does NOT claim the two anchors split a word.
		/// The ADJACENT_ANCHOR check has its own finer gate
		/// (<see cref="ContentQualityAnchorDetection.DetectAdjacent"/>, default false)
		/// because adjacent anchors can be intentional design (CSS-spaced button
		/// rows, JS-driven trigger widgets, dense nav bars). Disabled by default;
		/// operator enables when a site's design rules it in. This top-level
		/// switch still controls the whole method — set false to disable both
		/// checks together.
		/// </summary>
		public bool CheckMisplacedAnchors { get; set; } = true;

		/// <summary>
		/// When true, flags text nodes that are direct children of container elements
		/// (div, section, article etc.) without being wrapped in a block element.
		/// These are HTML authoring defects — browsers handle them inconsistently
		/// and screen readers may skip them.
		/// </summary>
		public bool CheckBareTextInContainers { get; set; } = true;

		/// <summary>
		/// When true, flags word collisions where an inline phrasing element (span, b, i, …)
		/// abuts bare sibling text with no whitespace at the seam, merging two words into one
		/// (e.g. a CMS editor's &lt;span class="h2"&gt;Tide&lt;/span&gt;Scholle →
		/// "TideScholle"). Keyed on a lowercase→Uppercase transition across the seam,
		/// so genuine mid-word emphasis (&lt;b&gt;bezah&lt;/b&gt;len) is left alone.
		/// </summary>
		public bool CheckWordCollisions { get; set; } = true;

		/// <summary>
		/// When true, flags a space on the wrong side of a hyphen inside a word:
		/// SPACE_AFTER_HYPHEN ("Xyzzy- Haus") and SPACE_BEFORE_HYPHEN ("Xyzzy -Haus"), where a
		/// compound was meant. A suspended head or tail next to a joining word or punctuation
		/// ("Haus- und Gartenpflege", "Hauseingänge / -ausgänge") is correct and not reported.
		/// </summary>
		public bool CheckHyphenSpacing { get; set; } = true;

		/// <summary>
		/// When true, flags a word written twice in a row inside one block of text (WORD_DOUBLED,
		/// "Haus Haus"); punctuation between the two means it is meant. See <see cref="DoubledWordExceptions"/>.
		/// </summary>
		public bool CheckDoubledWords { get; set; } = true;

		/// <summary>
		/// Doublings that are grammar or meant, per language (base code), lower case: "die die" (a
		/// relative pronoun after an article), "had had", "doch doch". Taken from the exceptions of
		/// LanguageTool's word-repeat rules where a plain word list can follow them, plus "so so" and
		/// "doch doch". A listed word passes
		/// in every context, its slips too. A language without a list reports every doubling.
		/// </summary>
		public Dictionary<string, List<string>> DoubledWordExceptions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
		{
			["de"] = ["der", "den", "dem", "die", "das", "sie", "wer", "so", "doch", "teils", "ha", "bla", "toi"],
			["en"] = ["had", "that", "ha", "blah"],
		};

		/// <summary>
		/// The words that may stand next to a suspended head or tail, per language (base code):
		/// "Haus- und Garten…", "zwei- bis drei…", "pre- and post-…". Matched ignoring case and a
		/// final dot. A language without a list allows punctuation only.
		/// </summary>
		public Dictionary<string, List<string>> HyphenJoiningWords { get; set; } = new(StringComparer.OrdinalIgnoreCase)
		{
			["de"] = ["und", "u.", "oder", "bis", "bzw.", "beziehungsweise", "sowie", "wie", "als", "noch", "mit", "respektive", "resp.", "statt", "versus", "vs."],
			["en"] = ["and", "or", "to", "nor", "versus", "vs."],
		};

		/// <summary>
		/// When true, scans raw downloaded HTML for CMS-template-architect-class
		/// defects:
		///   EMBEDDED_BOM_IN_BODY — UTF-8 BOM (U+FEFF) bytes appearing at any
		///     position other than file start. Signature of files concatenated
		///     without stripping residual BOMs (e.g. component-based CMS build
		///     pipelines emitting "UTF-8 with signature" per template fragment).
		///     Best practice: emit templates as UTF-8 without BOM.
		///   INVISIBLE_CHAR_IN_BODY — zero-width chars (ZWSP/ZWNJ/ZWJ/WJ), bidi
		///     control marks, line/paragraph separators, or C0/C1 control codes
		///     in body text whose parent element is NOT in
		///     <see cref="ContentQualityBlockElements"/>. The parent-element scope
		///     filter routes findings: invisibles inside p/h*/li/td/th are
		///     editor-paste-class (caught elsewhere); invisibles outside those
		///     are template-emitted.
		/// Findings go to a separate log (the 22-cms-template-authoring-defects
		/// dual-locale CSV pair) so they can be routed to the CMS template/architect
		/// team rather than
		/// to content editors. One finding per (file, codepoint) — never per
		/// occurrence — with occurrence count surfaced in the Detail.
		/// </summary>
		public bool CheckCmsTemplateAuthoringDefects { get; set; } = true;

		/// <summary>
		/// Structural well-formedness checks on raw downloaded HTML, emitted to
		/// 10-content-quality-issues.log as MALFORMED_HTML with the sub-defect in
		/// Detail (composite Word MALFORMED_HTML:&lt;sub-defect&gt;, mirroring
		/// UNWANTED_PATTERN). Not interactively triaged — these are server-side /
		/// templating bugs, auto-promoted to IssueTracking as "new" and flipped to
		/// "fixed" once the source is corrected. Each sub-check is independently
		/// gateable so a noisier site can silence the parser-error bridge while
		/// keeping the zero-false-positive pre-doctype check on.
		/// </summary>
		public MalformedHtmlConfig MalformedHtml { get; set; } = new();

		/// <summary>
		/// Per-sub-check gating for the structural anchor checks. Currently scopes
		/// the ADJACENT_ANCHOR finding only; new anchor-shape checks can be added to
		/// the same block without further schema churn. ADJACENT_ANCHOR is OFF by
		/// default: adjacency is a structural fact, not a verdict, and many
		/// sites use adjacent &lt;a&gt;&lt;a&gt; intentionally — CSS-spaced button
		/// rows, JS-driven trigger widgets, dense nav. An operator enables it after
		/// reviewing whether their site's design rules adjacency in or out. The
		/// outer <see cref="CheckMisplacedAnchors"/> still controls both EMPTY and
		/// ADJACENT together; this finer block opts ADJACENT alone in or out.
		/// </summary>
		public ContentQualityAnchorDetection AnchorDetection { get; set; } = new();

		/// <summary>
		/// Per-language apostrophe / elision profiles. Disambiguates typographic
		/// single quotes (U+2018, U+2019, U+02BC) from quote openers/closers in
		/// stack-based pairing analysis. The profile is selected by the page's
		/// &lt;html lang="…"&gt; attribute (lowercase 2-letter base code, so de-DE → de);
		/// "_default" is the fallback for pages with no lang attribute or a language
		/// not listed here.
		///
		/// Two list shapes per profile:
		///   SuffixElisions — letters AFTER the apostrophe (Germanic/Anglic pattern):
		///                    it's, don't, 'ner, 'mal.
		///   PrefixElisions — letters BEFORE the apostrophe (Romance pattern):
		///                    l'accès, d'un, qu'il.
		/// ApostropheChars selects which typographic characters count as apostrophes
		/// for the language. Default [U+2018, U+2019]. Turkish / Ukrainian add U+02BC.
		///
		/// Empty profile is valid — page language is recognised, but no language-
		/// specific elisions apply. The between-letters apostrophe rule still
		/// catches the common cases.
		/// </summary>
		public Dictionary<string, ApostropheElisionProfile> ContentQualityApostropheElisions { get; set; }
			= new(StringComparer.OrdinalIgnoreCase)
			{
				["_default"] = new ApostropheElisionProfile(),

				["en"] = new ApostropheElisionProfile
				{
					SuffixElisions = ["s", "t", "d", "ll", "ve", "re", "m"],
					WordFinalSPossessive = true,
				},

				["de"] = new ApostropheElisionProfile
				{
					SuffixElisions = ["s", "n", "ne", "ner", "nem", "nen", "mal"],
				},

				["fr"] = new ApostropheElisionProfile
				{
					PrefixElisions = ["l", "d", "n", "s", "j", "m", "t", "c",
									  "qu", "lorsqu", "puisqu", "quoiqu", "jusqu"],
				},

				["it"] = new ApostropheElisionProfile
				{
					PrefixElisions = ["l", "un", "dell", "sull", "all", "dall", "nell",
									  "c", "dov", "quell", "sant", "anch", "tutt", "mezz"],
				},

				["es"] = new ApostropheElisionProfile(),

				["pt"] = new ApostropheElisionProfile
				{
					PrefixElisions = ["d", "n"],
				},

				["ca"] = new ApostropheElisionProfile
				{
					PrefixElisions = ["l", "d", "n", "s", "m", "t"],
				},

				["nl"] = new ApostropheElisionProfile
				{
					SuffixElisions = ["s", "t", "n"],
				},

				// Turkish: real-world verification pending. List from grammatical
				// reference (locative / ablative / dative / genitive / accusative /
				// plural with vowel-harmony variants). U+02BC is the orthographic
				// standard apostrophe; U+2019 is the web compromise.
				["tr"] = new ApostropheElisionProfile
				{
					ApostropheChars = ['\u2019', '\u02BC'],
					SuffixElisions = [
						"de", "da", "te", "ta",
						"den", "dan", "ten", "tan",
						"e", "a", "ye", "ya",
						"in", "ın", "un", "ün", "nin", "nın", "nun", "nün",
						"i", "ı", "u", "ü", "yi", "yı", "yu", "yü",
						"ler", "lar",
						"le", "la",
					],
				},

				// Polish: apostrophe elision mainly in foreign-name declension
				// (Kennedy'ego, Joyce'a). Native words rarely use apostrophes.
				["pl"] = new ApostropheElisionProfile
				{
					SuffixElisions = ["a", "ego", "emu", "em", "ie", "ów", "owi"],
				},

				// Czech: apostrophe elision is rare; between-letters rule covers
				// the occasional stray.
				["cs"] = new ApostropheElisionProfile(),

				// Ukrainian: U+02BC marks hardness before iotated vowels within
				// words (п'ять, ім'я) — not an elision in the Romance sense.
				// Listing U+02BC ensures it is not misread as a quote character.
				["uk"] = new ApostropheElisionProfile
				{
					ApostropheChars = ['\u2019', '\u02BC'],
				},

				["ru"] = new ApostropheElisionProfile(),
				["bg"] = new ApostropheElisionProfile(),

				["ro"] = new ApostropheElisionProfile
				{
					PrefixElisions = ["s", "ne", "mi", "te"],
				},

				["hu"] = new ApostropheElisionProfile(),
			};

		/// <summary>
		/// Block-level elements whose text is analysed for quote and punctuation issues.
		/// Each block is checked independently — cross-block quote pairs are not detected.
		/// Set in crawlersuite.config.json to add site-specific elements.
		/// </summary>
		public List<string> ContentQualityBlockElements { get; set; } =
		[
			"p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th", "dd", "dt"
		];

		/// <summary>
		/// Container elements that must not contain direct (unwrapped) text nodes.
		/// Text found directly inside these elements is flagged as BARE_TEXT_IN_CONTAINER.
		/// Set in crawlersuite.config.json to match site-specific markup patterns.
		/// </summary>
		public List<string> ContentQualityContainerElements { get; set; } =
		[
			"div", "section", "article", "aside", "header", "footer", "main"
		];

		/// <summary>
		/// Class names that style an element as a heading although it is not h1–h6 (e.g.
		/// &lt;div class="h2"&gt;). An element carrying one of them as a whole class token is
		/// reported as STYLED_HEADING (template defect) unless it is a real heading or carries
		/// role="heading" (with or without aria-level); bare text inside it is not reported again. Matched
		/// case-sensitively, as browsers match classes. Empty list: the check is off.
		/// </summary>
		public List<string> ContentQualityHeadingClasses { get; set; } =
		[
			"h1", "h2", "h3", "h4", "h5", "h6"
		];

		/// <summary>
		/// Operator-configured suppression rules for content-quality findings.
		/// Each rule names a Type (e.g. BARE_TEXT_IN_CONTAINER), optionally a
		/// case-sensitive substring (Value) to match against "{Detail} {Context}",
		/// optionally a list of *-only Pages globs to scope by filename, an
		/// Enabled flag, and a free-text Comment.
		///
		/// Matching findings are dropped from the content-quality log entirely
		/// (no parallel "suppressed" log — flip Enabled=false on a rule to
		/// audit what it was hiding). Per-rule hit counts appear in the
		/// summary at end of run, and rules with zero hits emit a
		/// warning so stale rules surface.
		///
		/// Default empty. Operators add rules in crawlersuite.config.json to dampen
		/// site-specific template noise without disabling the underlying check.
		/// See <see cref="IssueSuppressions"/> for matcher
		/// semantics and <see cref="IssueSuppressionRule"/> for field shape.
		/// </summary>
		public List<IssueSuppressionRule> ContentQualityIssueSuppressions { get; set; } = [];

		/// <summary>
		/// Radius in characters used to build the context excerpt for all non-quote
		/// issue types (LIGATURE, SPLIT_WORD_ANCHOR, UNWANTED_PATTERN, LANGUAGE_MISMATCH).
		/// The excerpt window is 2 × radius characters centred on the issue position.
		/// </summary>
		public int ContentQualityExcerptRadius { get; set; } = 120;

		/// <summary>
		/// When true, quote issue excerpts (QUOTE_SYSTEM_MIX, QUOTE_UNMATCHED,
		/// QUOTE_WRONG_CLOSE, QUOTE_WRONG_OPEN) expand outward to the nearest sentence
		/// boundary (. ! ?) instead of using a fixed radius.
		/// </summary>
		public bool ContentQualityQuoteFullSentence { get; set; } = true;

		/// <summary>
		/// Maximum character length for content-quality excerpts — the quote/anchor
		/// findings and the control-character (CONTROL_CHARS_IN_CONTENT) excerpt.
		/// German sentences can be long — increase if excerpts are still truncated.
		/// </summary>
		public int ContentQualityMaxExcerpt { get; set; } = 400;

		public bool IsEnabled => CheckLigatures || CheckLanguageMismatch || CheckQuoteSystemMixing
			|| CheckQuotePairing || CheckPotentialTranslation || CheckSplitWordAnchors
			|| CheckBareTextInContainers || CheckMisplacedAnchors
			|| CheckWordCollisions
			|| CheckHyphenSpacing
			|| CheckDoubledWords
			|| CheckCmsTemplateAuthoringDefects
			|| MalformedHtml.DetectContentBeforeDoctype
			|| MalformedHtml.DetectHtmlParseErrors;
	}

	/// <summary>
	/// Per-sub-check gating for the MALFORMED_HTML structural well-formedness
	/// checks (raw downloaded HTML, Pass 1). Both default true. Independent so a
	/// noisier site can disable the parser-error bridge without losing the
	/// zero-false-positive pre-doctype check.
	/// </summary>
	public class MalformedHtmlConfig
	{
		/// <summary>
		/// CONTENT_BEFORE_DOCTYPE — non-whitespace markup or text before the
		/// document's &lt;!doctype&gt;/&lt;html&gt;/&lt;?xml&gt; opener (after an
		/// optional leading UTF-8 BOM). HtmlAgilityPack folds such leading content
		/// into the tree, so only a direct raw-byte check catches it. Near-zero
		/// false positives; almost always a backend templating / error-injection
		/// bug (e.g. a server error block welded on ahead of the real document).
		/// </summary>
		public bool DetectContentBeforeDoctype { get; set; } = true;

		/// <summary>
		/// MALFORMED_HTML:&lt;code&gt; — bridges HtmlAgilityPack's
		/// <c>HtmlDocument.ParseErrors</c> (from the raw-HTML parse) into findings,
		/// one per (file, error code) with an occurrence count in the Context.
		/// Codes listed in <see cref="SuppressParseErrorCodes"/> are filtered at
		/// detection. If a noisier site still floods log 10, disable this entirely
		/// and keep DetectContentBeforeDoctype on.
		/// </summary>
		public bool DetectHtmlParseErrors { get; set; } = true;

		/// <summary>
		/// HtmlAgilityPack parse-error codes filtered out of DetectHtmlParseErrors
		/// findings at detection time (never become findings, never reach log 10
		/// or IssueTracking). Seeded with EndTagNotRequired — HAP emits it for
		/// valid HTML that closes optionally-closeable tags (&lt;li&gt;, &lt;p&gt;,
		/// &lt;td&gt; …), so it is cosmetic, not a defect. Add codes here to
		/// suppress further noise; clear the list to surface every code.
		/// Case-insensitive match against the HAP code name.
		/// </summary>
		public List<string> SuppressParseErrorCodes { get; set; } = ["EndTagNotRequired"];
	}

	/// <summary>
	/// Per-sub-check gating for structural anchor finding types — currently scopes
	/// the ADJACENT_ANCHOR check; new anchor-shape switches can be added here
	/// without schema churn. Grouped as a class (not a flat bool on
	/// ContentQualityConfig) so future related switches stay together and the
	/// outer config stays scannable.
	/// </summary>
	public class ContentQualityAnchorDetection
	{
		/// <summary>
		/// ADJACENT_ANCHOR — two consecutive sibling anchors with no whitespace-text
		/// node between them, AND the literal "&lt;/a&gt;&lt;a" appears in the
		/// rendered excerpt (a string-evidence post-filter on top of HAP's DOM
		/// adjacency verdict, so HAP-normalized edge cases are dropped).
		///
		/// OFF by default. Rationale: adjacency is a structural fact, not a
		/// verdict. Many sites use adjacent &lt;a&gt;&lt;a&gt; intentionally — CSS-spaced
		/// button rows, JS-driven trigger widgets, dense nav. Whether the pattern
		/// indicates a defect on a given site depends on its design paradigm, which
		/// the tool cannot know. Operator enables this after reviewing whether
		/// their site rules adjacency in (potential defect) or out (intentional
		/// design). The outer <see cref="ContentQualityConfig.CheckMisplacedAnchors"/>
		/// still controls both EMPTY and ADJACENT together; this finer switch opts
		/// ADJACENT alone in or out.
		/// </summary>
		public bool DetectAdjacent { get; set; } = false;
	}

	/// <summary>
	/// Defines a named group of patterns to detect in page content.
	/// Matches are reported as UNWANTED_PATTERN in 10-content-quality-issues.log.
	/// Configure in crawlersuite.config.json to keep internal pattern details private.
	/// </summary>
	public class ContentUnwantedPattern
	{
		/// <summary>Triage priority group. Suggested values: Security, Style, Deprecated.</summary>
		public string Category { get; set; } = string.Empty;

		/// <summary>Descriptive name shown in the quality log output.</summary>
		public string Name { get; set; } = string.Empty;

		/// <summary>
		/// When true, all matching patterns on a page are collapsed into one
		/// quality issue using the group Name as the identity key. Use when all
		/// patterns together indicate a single defect (e.g. a full CMS variable
		/// #parameter# — fix is always on the CMS side, not per pattern).
		/// When false (default), each pattern produces its own issue per page,
		/// allowing individual tracking and assignment.
		/// </summary>
		public bool GroupPatterns { get; set; } = false;

		/// <summary>
		/// When true, patterns are matched case-sensitively.
		/// When false, matching is case-insensitive.
		/// </summary>
		public bool CaseSensitive { get; set; } = false;

		/// <summary>
		/// Substring patterns to search for in the simplified HTML source.
		/// Include surrounding characters (spaces, punctuation) as anchors when needed
		/// to avoid false positives — simple substring match, no regex.
		/// </summary>
		public List<string> Patterns { get; set; } = [];

		/// <summary>
		/// Optional Name of ANOTHER configured set whose matches are expected to appear
		/// INSIDE this one, used only when this set is an envelope — a two-pattern
		/// opener/closer group with GroupPatterns = true (e.g. Patterns ["%(", ")%"]).
		/// When the envelope is detected OPEN (opener present, closing delimiter missing —
		/// the common CMS editing mistake), any matches of the Referenced set that fall in
		/// the region after the opener are folded into ONE finding instead of surfacing as
		/// separate triage cards and ticket lines. Purely subtractive: the referenced set
		/// still fires on its own everywhere else, and an open envelope with no corroborating
		/// hits still fires alone. Empty (default) disables coalescing for this set.
		/// </summary>
		public string Reference { get; set; } = string.Empty;

		/// <summary>
		/// Envelope-only (GroupPatterns:true with exactly 2 Patterns). When true, this set is
		/// a TEMPLATE-BINDING envelope and fires ONLY on a MALFORMED binding — a cleanly-
		/// flanked identifier ([A-Za-z0-9._]+) carrying a FULL doubled fence ("{{" or "}}") on
		/// exactly one side: "{foo}}", "foo}}", "{{foo}", "{{foo". A well-formed "{{foo}}" is
		/// fine and stays SILENT (the JS binds it at runtime). A token with no full fence is
		/// ignored — single-brace shapes like "{n}", "{foo", "foo}" are a different (or no)
		/// templating style and would only bury real findings. The identifier must be flanked
		/// by whitespace or a string edge on its outer side, so structural brace runs in
		/// embedded JSON/CSS ("display:none}}", "…2}},", "…null}}'") — which sit against a
		/// non-space, non-brace neighbour — are ignored. Assumes a doubled-char fence such as
		/// {{ }}. When false (default), every occurrence of either delimiter surfaces
		/// regardless of shape — for delimiters that must never reach the user even when
		/// paired, e.g. %( )%.
		/// </summary>
		public bool OnlyFlagUnbalanced { get; set; } = false;

		/// <summary>
		/// Whether this set scans inside &lt;script&gt; … &lt;/script&gt; regions. Default true.
		/// UnwantedPatterns is a general content-quality detector, so scanning script is on by
		/// default (a banned API call or leaked token in inline JS is a legitimate target). Set
		/// false to skip script regions when a pattern is structurally noisy there — e.g. a
		/// Mustache {{ }} envelope, whose braces never carry a real binding inside JS.
		/// </summary>
		public bool CheckScript { get; set; } = true;

		/// <summary>
		/// Whether this set scans inside &lt;style&gt; … &lt;/style&gt; regions. Default true.
		/// Set false to skip CSS when a pattern collides with stylesheet syntax — e.g. a
		/// Mustache "}}" envelope false-firing on a CSS block close like "30px 1fr}}", where the
		/// space-flanked value reads as a dangling-close binding. The matching content lives in
		/// the body, never in CSS, so skipping &lt;style&gt; removes the noise without weakening
		/// real detection.
		/// </summary>
		public bool CheckStyle { get; set; } = true;

		/// <summary>
		/// Whether this set skips the VALUES of href attributes when scanning. Default false.
		/// Set true when a pattern legitimately survives in link URLs after being removed from
		/// content — e.g. a retired product name still embedded in a navigation link's slug,
		/// which then repeats on every page carrying that navigation and buries the operator in
		/// noise. Only the quoted href value is skipped (href="…"/href='…'); the anchor's own
		/// text is still scanned, so a genuine leftover of the term in visible content still
		/// fires. Detection is otherwise unchanged — every other region is scanned on raw input.
		///
		/// To avoid losing the one place the term legitimately matters, when this is true the
		/// page whose OWN url contains a pattern is still flagged (a distinct "(in this page's
		/// url)" finding), so the source page — the culprit carrying the slug — stays visible
		/// even though the repeated links pointing at it are silenced. Skipping href values would
		/// otherwise hide that too. When false (default) href values are scanned like any other
		/// text and no separate url check runs.
		/// </summary>
		public bool ExcludeHref { get; set; } = false;

		/// <summary>
		/// Whether occurrences of a pattern that sit INSIDE a url collapse into one
		/// per-page summary finding instead of one finding per occurrence. Default false.
		/// Set true when a pattern that has been removed from content still survives in
		/// multiple url references on the same page — asset paths (src), link targets,
		/// canonical/og urls, url literals in inline script/json — where each occurrence
		/// is not a separate edit but the SAME underlying fix (rename/redirect the path).
		/// Reporting each separately buries the operator; collapsing them into one card
		/// per page — while still listing every occurrence so none is missed — gives the
		/// editor one work item with the full location list.
		///
		/// A match counts as "in a url" when the token it sits in is slash-bearing: the
		/// quoted attribute value (href/src/etc.) or the whitespace/&lt;&gt;-bounded run
		/// containing the match includes a path separator. A slash-less relative ref
		/// (a bare filename next to the html) or a match broken by whitespace inside the
		/// value does NOT collapse and surfaces as its own finding — acceptable, since it
		/// also surfaces the underlying defect (a stray reference, a url missing its
		/// percent-encoding). Non-url occurrences (visible text, comments) are unaffected
		/// and stay one finding each, because each is a separate edit.
		///
		/// Independent of <see cref="ExcludeHref"/> and complementary to it: ExcludeHref
		/// masks the repeated navigation href entirely (cross-page noise), so those never
		/// reach this per-page grouping; ExcludeUrl groups the url occurrences that remain
		/// on a page. When ExcludeHref is also set, the page's own url (the culprit the
		/// masked links pointed at) folds into this page's summary as one listed
		/// occurrence — the lone occurrence if nothing else survives — so the source page
		/// is never silently dropped. When false (default) every url occurrence surfaces
		/// individually.
		/// </summary>
		public bool ExcludeUrl { get; set; } = false;

		/// <summary>False keeps the group in the file without effect (switched off on the settings screen).</summary>
		public bool Enabled { get; set; } = true;

		/// <summary>
		/// Its findings are security-relevant (a leaked server address, not a retired product
		/// name): marked in Results, Triage and tickets, and sorted first. Decided here, never
		/// inferred from the Category text.
		/// </summary>
		public bool SecurityRelevant { get; set; }

		public bool IsConfigured => Enabled && !string.IsNullOrEmpty(Name) && Patterns.Count > 0;
	}

	/// <summary>
	/// How the settle phase (Step_SettleExtensions) classifies a downloaded file
	/// when the three identifying signals — requested URL extension, response
	/// Content-Type header, and a byte sniff of the saved content — do not all
	/// agree that it is HTML. Conservative → aggressive ladder. See the README
	/// for each value's risk. Default is <see cref="TrustByteSniff"/>.
	///
	/// A changed policy takes effect only on a NEW crawl — replay ([L]) reuses
	/// the classification from the original crawl and does not re-evaluate.
	/// </summary>
	public enum UnverifiedHtmlPolicy
	{
		/// <summary>Decide from the saved bytes. Not-HTML bytes → quarantine
		/// (.unverified). Default. Risk: unusual-but-valid HTML can sniff as
		/// non-HTML and be quarantined.</summary>
		TrustByteSniff,

		/// <summary>Believe the server's Content-Type header. Risk: a wrong
		/// header analyzes non-HTML as HTML, or skips real HTML. Escape hatch
		/// for fragment repositories serving text/html.</summary>
		TrustContentType,

		/// <summary>Never analyze a mismatch; keep .unverified. Risk:
		/// conservative — genuine HTML whose signals disagree is not analyzed.</summary>
		Quarantine,

		/// <summary>Analyze every mismatch as HTML regardless. Risk: "hail
		/// mary" — binary/PDF/plain-text bodies get HTML-parsed, producing
		/// spurious findings.</summary>
		AnalyseBlindly
	}

	/// <summary>
	/// PDF counterpart to <see cref="UnverifiedHtmlPolicy"/>. Governs how the
	/// settle phase classifies a downloaded file when the requested extension, the
	/// Content-Type header, and a byte sniff disagree about it being a PDF. Kept as
	/// a separate enum (not shared with the HTML policy) because PDF and HTML
	/// identity are expected to diverge as each is developed further; mixing them
	/// now would only have to be untangled later.
	/// </summary>
	public enum UnverifiedPdfPolicy
	{
		/// <summary>Decide from the saved bytes (leading "%PDF-" magic). Default.
		/// Defensive: a file that does not sniff as a PDF is not treated as one.
		/// Risk: a valid PDF that does not begin with the magic (extremely rare)
		/// would not be recognised.</summary>
		TrustByteSniff,

		/// <summary>Believe the server's Content-Type header (application/pdf).
		/// Risk: a wrong header treats a non-PDF as a PDF, or skips a real PDF.
		/// Escape hatch for servers that stream PDFs from typeless endpoints with
		/// a correct header.</summary>
		TrustContentType,

		/// <summary>Never treat a mismatch as a PDF; keep .unverified. Risk:
		/// conservative — a genuine PDF whose signals disagree is not analysed.</summary>
		Quarantine,

		/// <summary>Treat every mismatch as a PDF regardless. Risk: "hail mary" —
		/// non-PDF bodies get handed to the PDF analyser, producing spurious or
		/// failed analysis.</summary>
		AnalyseBlindly
	}

	/// <summary>
	/// How AssetQuality settles a downloaded asset whose declared type (URL
	/// extension and/or Content-Type) and actual bytes disagree about whether it
	/// is a raster image. Image counterpart to <see cref="UnverifiedPdfPolicy"/>,
	/// same four values and the same "new crawl settles, replay reuses" rule.
	/// </summary>
	public enum UnverifiedImagePolicy
	{
		/// <summary>Decide from the saved bytes (image magic). Default. A file that
		/// does not sniff as an image is not treated as one.</summary>
		TrustByteSniff,

		/// <summary>Believe the server's Content-Type header (image/*). Escape
		/// hatch for image endpoints with a correct header but no extension.</summary>
		TrustContentType,

		/// <summary>Never treat a mismatch as an image; keep .unverified.
		/// Conservative — a genuine image whose signals disagree is not analysed.</summary>
		Quarantine,

		/// <summary>Treat every mismatch as an image regardless. "Hail mary" —
		/// non-image bodies get handed to the image checks.</summary>
		AnalyseBlindly
	}

	/// <summary>
	/// Asset-level quality checks on downloaded raster images (JPEG/PNG/GIF/WebP),
	/// emitted to the asset-quality log and promoted to IssueTracking as ASSET_*
	/// findings (composite Word ASSET_*:&lt;detail&gt; where the sub-category adds
	/// identity). All checks default on; thresholds are deliberately lenient so an
	/// operator tightens from a quiet baseline. Tune against real data to avoid a
	/// noise firehose (press / popup images legitimately carry EXIF and large
	/// dimensions — list those under <see cref="SizeAndDimensionExclusions"/>).
	/// </summary>
	public class AssetQualityConfig
	{
		/// <summary>
		/// ASSET_METADATA — flag and extract publishable-leak metadata in images
		/// (GPS coordinates, camera make/model, author/artist/copyright, software,
		/// timestamps) via the native AssetMetadata reader. The finding lists which categories
		/// were present and their (sanitised) values so the operator sees the
		/// exposed data, not merely that a leak exists.
		/// </summary>
		public bool CheckMetadataLeakage { get; set; } = true;

		/// <summary>
		/// ASSET_DIMENSIONS — flag images whose pixel dimensions are degenerate
		/// (0- or 1-px tracking pixels) or implausibly large (any side exceeding
		/// <see cref="MaxImageDimensionPixels"/>). Dimensions are read from the
		/// image header bytes (no decode).
		/// </summary>
		public bool CheckDimensions { get; set; } = true;

		/// <summary>
		/// ASSET_SIZE — two signals: (a) declared Content-Length (from the header
		/// sidecar) vs actual bytes-on-disk disagree (truncated / corrupt
		/// download), and (b) byte size exceeds <see cref="MaxImageBytes"/>. Falls
		/// back to bytes-on-disk when no Content-Length was declared (chunked
		/// responses).
		/// </summary>
		public bool CheckSize { get; set; } = true;

		/// <summary>Max plausible image byte size before ASSET_SIZE flags it.
		/// Default 1 MB — lenient; tighten for sites with strict asset budgets.</summary>
		public long MaxImageBytes { get; set; } = 1_048_576;

		/// <summary>Max plausible pixel dimension (any side) before
		/// ASSET_DIMENSIONS flags it. Default 5000 px — lenient.</summary>
		public int MaxImageDimensionPixels { get; set; } = 5000;

		/// <summary>
		/// URL / path substring patterns (case-insensitive) for assets where large
		/// size and large dimensions are intentional — press kits, popup /
		/// download originals. Matching assets SKIP the size and dimension checks
		/// (metadata leakage is still checked — a press image with embedded GPS is
		/// still a leak). Empty = no exclusions.
		/// </summary>
		public List<string> SizeAndDimensionExclusions { get; set; } = [];

		/// <summary>
		/// How to settle a downloaded asset whose extension / Content-Type / bytes
		/// disagree about being an image. One of TrustByteSniff (default),
		/// TrustContentType, Quarantine, AnalyseBlindly. Stored as a string,
		/// resolved to <see cref="UnverifiedImagePolicy"/> via
		/// <see cref="ResolvedUnverifiedImagePolicy"/>. Empty = default.
		/// </summary>
		public string UnverifiedImagePolicy { get; set; } = "TrustByteSniff";

		/// <summary>The <see cref="UnverifiedImagePolicy"/> string resolved to its
		/// enum. Empty / unset → TrustByteSniff.</summary>
		public UnverifiedImagePolicy ResolvedUnverifiedImagePolicy =>
			string.IsNullOrWhiteSpace(UnverifiedImagePolicy)
				? CrawlerSuite.Core.UnverifiedImagePolicy.TrustByteSniff
				: Enum.Parse<UnverifiedImagePolicy>(UnverifiedImagePolicy, ignoreCase: true);

		public bool IsEnabled => CheckMetadataLeakage || CheckDimensions || CheckSize;
	}

	/// <summary>
	/// Per-language apostrophe-elision profile. Configured via
	/// <see cref="ContentQualityConfig.ContentQualityApostropheElisions"/> as a
	/// dictionary keyed by lowercase 2-letter language code (or "_default").
	/// See that property's documentation for selection semantics.
	/// </summary>
	public class ApostropheElisionProfile
	{
		/// <summary>
		/// Typographic characters that count as apostrophes for this language.
		/// Default [U+2018, U+2019] covers English / German / French / most Latin
		/// scripts. Turkish / Ukrainian add U+02BC (modifier letter apostrophe).
		/// A character not in this list, when encountered, falls through to
		/// standard opener / closer matching with no apostrophe disambiguation.
		/// </summary>
		public List<char> ApostropheChars { get; set; } = ['\u2018', '\u2019'];

		/// <summary>
		/// Letters that follow the apostrophe in an elision (Germanic / Anglic
		/// pattern): "s" matches it's; "ne", "ner", "mal" match 'ne, 'ner, 'mal.
		/// Match is forward from the apostrophe position; case-insensitive.
		/// </summary>
		public List<string> SuffixElisions { get; set; } = [];

		/// <summary>
		/// Letters that precede the apostrophe in an elision (Romance pattern):
		/// "l" matches l'accès; "qu" matches qu'il; "lorsqu" matches lorsqu'il.
		/// Match is backward from the apostrophe position; case-insensitive.
		/// To avoid mid-word false matches, the character immediately AFTER the
		/// apostrophe must be a letter for the prefix to be accepted.
		/// </summary>
		public List<string> PrefixElisions { get; set; } = [];

		/// <summary>
		/// When true, a U+2019 immediately after 's'/'S' and immediately before a
		/// non-letter (or end of text) is treated as a possessive apostrophe rather
		/// than a quote closer — e.g. "visitors'", "Users'", "campaigns'", "boss'".
		/// English plural possessives are always word-final 's, so this is the
		/// English convention; enable it on the "en" profile. Only suppresses when
		/// the glyph would otherwise be an orphan closer — a genuine single-quoted
		/// word ending in s (…'words') still closes normally. Default false: a
		/// language without this convention must not silently drop real closers.
		/// </summary>
		public bool WordFinalSPossessive { get; set; }
	}

	/// <summary>
	/// End-of-run dictionary maintenance for the user and site dictionaries.
	///
	/// Orphans are discovered by the cross-off usage recorder during spell-check
	/// (<see cref="Lexicon.UsageTracker"/>): any non-pinned entry that was never
	/// consulted on a crawled page is an orphan. Redundant entries (prefix-stripped
	/// remainder accepted by the system dictionary) are computed from the loaded
	/// bundles. Entries prefixed with '!' are pinned and always exempt.
	///
	/// Mode:
	///   Off         — step does not run (default).
	///   Report      — write the orphan/redundancy report (log 15) only; mutate nothing.
	///                 Runs in silent crawls too (read-only).
	///   Interactive — report like Report; each flagged word is then removed or
	///                 pinned one at a time on the Dictionaries page (the run itself
	///                 mutates nothing).
	/// Unrecognized Mode values are treated as Off.
	/// </summary>
	public sealed class DictionaryMaintenanceConfig
	{
		/// <summary>Off | Report | Interactive. Default Off. Unrecognized values are treated as Off.</summary>
		public string Mode { get; set; } = "Off";

		/// <summary>Include the user dictionary in maintenance. Default false.</summary>
		public bool UpdateUserDictionary { get; set; } = false;

		/// <summary>Include the site-specific dictionary in maintenance. Default false.</summary>
		public bool UpdateSiteSpecificDictionary { get; set; } = false;
	}

	/// <summary>One entry of <see cref="Config.ProperNames"/>. A field the class does not have (a typo) stops the load.</summary>
	[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
	public class ProperName
	{
		/// <summary>The name as pages write it.</summary>
		public string Name { get; set; } = string.Empty;

		/// <summary>Its acronym, in capitals (optional).</summary>
		[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
		public string? Acronym { get; set; }
	}
}
