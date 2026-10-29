namespace CrawlerSuite.Core.Settings
{
	using System.Reflection;
	using System.Text.Json;
	using System.Text.Json.Nodes;

	/// <summary>How a setting is edited on the settings screen.</summary>
	public enum SettingKind
	{
		/// <summary>On or off.</summary>
		Switch,

		/// <summary>A whole number, with a range.</summary>
		Number,

		/// <summary>A decimal number, with a range.</summary>
		Decimal,

		/// <summary>Free text.</summary>
		Text,

		/// <summary>A folder path.</summary>
		Folder,

		/// <summary>A file path.</summary>
		File,

		/// <summary>One of a fixed set of values.</summary>
		Choice,

		/// <summary>A list of text values, one per line.</summary>
		Lines,

		/// <summary>A list of entries or a map, written as JSON (one entry per line).</summary>
		Json,
	}

	/// <summary>Something a row shows besides its field.</summary>
	public enum RowExtra
	{
		None,

		/// <summary>The free space of the drive the folder is on, rated against the crawl download limit.</summary>
		FreeSpace,

		/// <summary>A slider from All to the processors of this computer, with what Windows reports about them.</summary>
		Processors,

		/// <summary>A slider over the setting's range (Min to Max).</summary>
		Slider,
	}

	/// <summary>A setting with an editor of its own instead of a plain field or table.</summary>
	public enum SettingEditor
	{
		/// <summary>The field or table its kind gives.</summary>
		Field,

		/// <summary>Tag names as &lt;tag&gt; chips: new, edit in place, remove.</summary>
		TagChips,

		/// <summary>XPaths: coloured, checked, switchable, with last-run usage.</summary>
		Xpaths,

		/// <summary>Attribute names: switchable, with last-run usage.</summary>
		Attributes,

		/// <summary>Known word parts: one per line, with how many words each let pass in the last run.</summary>
		KnownParts,

		/// <summary>Content-list paths to leave out: switchable, with how many rows of the export each left out.</summary>
		ContentPaths,

		/// <summary>Boilerplate groups as cards: reference pages and parts with usage.</summary>
		Boilerplate,

		/// <summary>File kinds as named chips (Pages, Scripts …) plus other extensions.</summary>
		FileKinds,

		/// <summary>h1–h6 as checkboxes plus the site's own classes as chips.</summary>
		HeadingClasses,

		/// <summary>Suppression rules as cards: type, value, active, required comment, usage.</summary>
		Suppressions,

		/// <summary>Unwanted-pattern groups as cards by shape, with explanation, security switch and usage.</summary>
		Patterns,

		/// <summary>Whole script strings, one per line: switchable, with how often each was skipped.</summary>
		ScriptStrings,

		/// <summary>The triage switches as three-state switches by family: on, ride-along, off.</summary>
		TriageModes,
	}

	/// <summary>How strongly a choice's card speaks.</summary>
	public enum CardTone
	{
		/// <summary>The recommended choice.</summary>
		Good,

		/// <summary>A deliberate choice with a cost.</summary>
		Caution,

		/// <summary>A choice that takes a risk.</summary>
		Danger,
	}

	/// <summary>The card shown next to a choice when that value is selected.</summary>
	public sealed record ChoiceCard(CardTone Tone, string Text);

	/// <summary>One setting of crawlersuite.config.json: where it is, how it is edited, and what it does.</summary>
	/// <param name="Path">The JSON path, dotted ("ContentQuality.CheckLigatures").</param>
	/// <param name="Tab">The settings tab it is shown on.</param>
	/// <param name="Section">A heading within the tab; empty for none.</param>
	/// <param name="Label">The name shown, in plain words.</param>
	/// <param name="Help">The explanation shown under the field.</param>
	public sealed record SettingInfo(
		string Path,
		string Tab,
		string Section,
		string Label,
		SettingKind Kind,
		string Help,
		string? Unit = null,
		IReadOnlyList<string>? Choices = null,
		double? Min = null,
		double? Max = null)
	{
		/// <summary>What the default means, when its bare value does not say it ("your Windows sign-in").</summary>
		public string? DefaultMeaning { get; init; }

		/// <summary>No default: the file must set it. The row has no lock and says "Required.".</summary>
		public bool Required { get; init; }

		/// <summary>What to do when a required setting is empty: "choose a folder", "enter a pattern".</summary>
		public string RequiredText { get; init; } = "enter a value";

		/// <summary>Something shown besides the field.</summary>
		public RowExtra Extra { get; init; }

		/// <summary>How a list or map is edited as a table; null for a JSON field.</summary>
		public TableSpec? Table { get; init; }

		/// <summary>Another setting (a switch) this one only matters while it is on; shown as a note while off.</summary>
		public string? ActiveWhen { get; init; }

		/// <summary>An editor of its own (tag chips, XPaths, attributes, boilerplate groups).</summary>
		public SettingEditor Editor { get; init; }

		/// <summary>A choice whose values are the languages of the configured dictionary bundles.</summary>
		public bool ChoicesFromBundles { get; init; }

		/// <summary>A number above this shows <see cref="WarnText"/> as a caution card; null for none.</summary>
		public double? WarnAbove { get; init; }

		/// <summary>The card shown while the number is above <see cref="WarnAbove"/>.</summary>
		public string WarnText { get; init; } = string.Empty;

		/// <summary>Cards for choices, by value; a value without a card shows none.</summary>
		public IReadOnlyDictionary<string, ChoiceCard>? Cards { get; init; }

		/// <summary>Whether the field is a table (all plain lists, and the lists and maps with flat entries).</summary>
		public TableSpec? EffectiveTable => Table ?? (Kind == SettingKind.Lines ? TableSpec.OneColumn : null);
	}

	/// <summary>
	/// Every setting the settings screen offers, in the order the tabs show them, with its
	/// explanation — the help lives here, not as comments in the file. The built-in
	/// default of each setting comes from a fresh <see cref="Config"/>, so it can never disagree
	/// with what the app uses when the setting is absent from the file. Sites have a tab of their
	/// own and are not listed here. The proxy password is never a setting: it is asked per crawl.
	/// </summary>
	public static class SettingsCatalog
	{
		public const string SitesTab = "Sites";

		/// <summary>The tab that carries Debug mode and the test levers of the dictionary checks.</summary>
		public const string LeversTab = "Debug";

		/// <summary>A fixed rule a tab states at its top, where a setting might be expected; or empty.</summary>
		public static string TabNote(string tab) => tab == "SEO & links"
			? "Pages with noindex or none — in the page's robots meta tag or in the X-Robots-Tag header — are out of search: they stay out of the sitemap. Fixed, not a setting."
			: string.Empty;

		/// <summary>The tabs in order. Sites first; it has its own form.</summary>
		public static readonly IReadOnlyList<string> Tabs =
			[SitesTab, "Crawl", "Network", "CMS", "Spelling", "Dictionaries", "Content quality", "SEO & links", "Assets", TriageTab, "Comparison", "Debug"];

		/// <summary>The tab with what triage counts.</summary>
		public const string TriageTab = "Triage";

		private static readonly string[] Policies = ["TrustByteSniff", "TrustContentType", "Quarantine", "AnalyseBlindly"];

		private const string PolicyHelp = " Takes effect on a new crawl only.";

		private static readonly IReadOnlyDictionary<string, ChoiceCard> PolicyCards = new Dictionary<string, ChoiceCard>(StringComparer.Ordinal)
		{
			["TrustByteSniff"] = new(CardTone.Good, "The file's bytes decide what it is, and it is analysed as that."),
			["TrustContentType"] = new(CardTone.Caution,
				"The server's label decides, even when the bytes say otherwise. Files can be analysed as something they are not."),
			["Quarantine"] = new(CardTone.Caution,
				"A file whose label and bytes disagree is stored but never analysed. Nothing questionable is opened; its findings are missing."),
			["AnalyseBlindly"] = new(CardTone.Danger,
				"Every disputed file is analysed anyway, whatever it turns out to be. Only for sites whose every file you trust."),
		};

		private const string JsonListHelp = " One entry per line, written as JSON.";

		private const string NoDefault = "none — it must be set";

		/// <summary>Every setting, in screen order.</summary>
		public static readonly IReadOnlyList<SettingInfo> All =
		[
			// ── Crawl ───────────────────────────────────────────────────────────
			new("BaseDirectory", "Crawl", "Where", "Download folder", SettingKind.Folder,
				"The folder that holds one folder per site, each with its timestamped snapshots, triage ledger and DNS history. " +
				"A relative path is taken from the program's folder.")
				{ DefaultMeaning = NoDefault, Required = true, RequiredText = "choose a folder", Extra = RowExtra.FreeSpace },
			new("FilePattern", "Crawl", "Where", "Page file pattern", SettingKind.Text,
				"The file pattern of the pages to crawl, as \"*.ext\" — \"*.html\" for most sites.") { DefaultMeaning = NoDefault, Required = true, RequiredText = "enter a pattern" },
			new("UnverifiedHtmlPolicy", "Crawl", "Unclear file types", "Pages that may not be HTML", SettingKind.Choice,
				"How a download is settled when its address, its Content-Type and its bytes disagree about being HTML." + PolicyHelp,
				Choices: Policies) { Cards = PolicyCards },
			new("UnverifiedPdfPolicy", "Crawl", "Unclear file types", "Files that may not be PDF", SettingKind.Choice,
				"How a download is settled when its address, its Content-Type and its bytes disagree about being a PDF." + PolicyHelp,
				Choices: Policies) { Cards = PolicyCards },
			new("MaxDegreeOfParallelism", "Crawl", "Work and limits", "Parallel work", SettingKind.Number,
				"How many processors the analysis uses at once. All (stored as 0) uses every processor of the computer it runs on, " +
				"which is also right when the file is copied to another computer. Fewer keeps the computer more responsive while it runs.",
				Min: 0, Max: 256)
				{ DefaultMeaning = "All", Extra = RowExtra.Processors },
			new("MaxConcurrentPageDownloads", "Crawl", "Work and limits", "Page downloads at once", SettingKind.Number,
				"How many pages are requested from the site at the same time.", Min: 1, Max: 1000),
			new("MaxConcurrentAssetDownloads", "Crawl", "Work and limits", "Asset downloads at once", SettingKind.Number,
				"How many images, scripts, style sheets and documents are requested at the same time.", Min: 1, Max: 1000),
			new("MaxRequestsPerSecond", "Crawl", "Work and limits", "Requests per second", SettingKind.Number,
				"The most requests the crawl sends to one server each second, pages and assets together. One view of a modern " +
				"page is some 30 to 40 requests within a second; 50 is about one and a half such views every second. 0 means no ceiling.",
				Unit: "per second", Min: 0, Max: 100000)
				{
					WarnAbove = Config.GentleRequestsPerSecond,
					WarnText = "Faster than a crawl needs to be gentle. Fine for a site whose infrastructure you know can take it; a " +
						"smaller site, or one behind a protection that counts requests, may slow the crawl down or refuse it.",
				},
			new("MaxConnectionsPerServer", "Crawl", "Work and limits", "Connections per server", SettingKind.Number,
				"The most connections open to one server at a time, pages and assets together. A browser opens 6. Downloads beyond " +
				"this wait for a free connection; many connections opened at once is what servers' connection limits react to.",
				Min: 1, Max: 1000),
			new("HighestHttpVersion", "Crawl", "Work and limits", "Highest HTTP version", SettingKind.Choice,
				"The newest protocol the crawl offers; each server is spoken to in the best version it agrees to, up to this. " +
				"The run's step row says which version the servers answered in.",
				Choices: Config.HttpVersions)
				{
					Cards = new Dictionary<string, ChoiceCard>
					{
						["1.1"] = new(CardTone.Good, "Every request has a connection of its own while it runs: a failure, a stall and every " +
							"measurement belong to that request alone. What a browser gets from a server that offers no newer version."),
						["2"] = new(CardTone.Caution, "Many requests share one connection, as in a browser where the server offers HTTP/2: " +
							"one broken connection fails them all, and a slow answer can hold up the others."),
						["3"] = new(CardTone.Caution, "As 2, over QUIC, where the server offers it (and Windows 11 or Server 2022 and later " +
							"support it); otherwise the crawl uses 2 or 1.1. One broken connection fails all its requests."),
					},
				},
			new("MaxSingleFileMB", "Crawl", "Work and limits", "Largest file", SettingKind.Number,
				"A larger file is not stored: its first 64 KB and its headers are kept in the snapshot's oversized folder and it is " +
				"listed in log 34. 0 means no limit.", Unit: "MB", Min: 0, Max: 100000),
			new("MaxCrawlDownloadGB", "Crawl", "Work and limits", "Total download limit", SettingKind.Number,
				"When a crawl downloads more than this, all downloads stop and you decide what to do with the snapshot. Reaching it " +
				"usually means something on the site is badly wrong. 0 means no limit.", Unit: "GB", Min: 0, Max: 100000),
			new("StallTimeoutSeconds", "Crawl", "Work and limits", "Stall timeout", SettingKind.Number,
				"A download that receives no data for this long is abandoned and logged. It measures silence, not total time.",
				Unit: "seconds", Min: 1, Max: 3600),
			new("ModalQueryParameters", "Crawl", "What is followed", "Modal query parameters", SettingKind.Json,
				"Query parameters that carry the address of a page shown in an overlay; the crawler follows that address, with the " +
				"suffix appended, as a page of its own.")
				{ Table = TableSpecs.ModalQueryParameters },
			new("DownloadExclusions", "Crawl", "What is followed", "Download exclusions", SettingKind.Json,
				"Links whose address contains an entry (ignoring case) are not crawled — for sections that are uninteresting or very " +
				"large. An inactive entry is kept as a note without effect.")
				{ Table = TableSpecs.DownloadExclusions },
			new("ComparisonPdfSetAside", "Comparison", "PDFs", "Patterns set aside in PDF text", SettingKind.Json,
				"What this site's PDF generator prints anew each time it writes a PDF again — a date inside a form number, a dated " +
				"line. Each match in a PDF's page text is set aside before A and B are compared; a PDF that is then the same counts " +
				"as identical \u201cbut for configured patterns\u201d, with each pattern's count in the summary. Its font subsets are then " +
				"not compared by their bytes, as they follow from the text. Any other difference still counts. Regular expressions, " +
				"run without backtracking (no lookarounds, no back-references). A loose pattern hides real changes: keep each one " +
				"narrow. An inactive entry is kept as a note without effect.")
				{ Table = TableSpecs.ComparisonPdfSetAside, DefaultMeaning = "none — nothing set aside" },
			new("ExtendedCrawlJsonPathPrefixes", "Crawl", "What is followed", "Path prefixes in JSON and scripts", SettingKind.Lines,
				"Path prefixes the crawler looks for in JSON and script blocks to find further pages. No entries turns this off.")
				{ DefaultMeaning = "none — off" },

			// ── Network ─────────────────────────────────────────────────────────
			new("UseProxy", "Network", "Proxy", "Use a proxy", SettingKind.Switch,
				"Sends every request through the proxy below."),
			new("ProxyUrl", "Network", "Proxy", "Proxy address", SettingKind.Text,
				"The proxy, e.g. http://proxy.example.local:8080. Who signs in is not stored: every crawl signs in with your Windows " +
				"account first; only when the proxy refuses it are you asked for a user (your Windows user name filled in) and a password, " +
				"which the crawl forgets when it ends."),

			// ── CMS ─────────────────────────────────────────────────────────────
			new("CmsContentList.Path", "CMS", "Content list", "Content list file", SettingKind.File,
				"A delimited export of your CMS's page list. Empty turns off everything that depends on it: the comparison with the " +
				"crawl, the page source and the CMS links.") { DefaultMeaning = "not set — the content list is off" },
			new("CmsContentList.Comment", "CMS", "Content list", "Note on refreshing", SettingKind.Text,
				"Shown when the file is older than the age limit — say how to export a fresh one."),
			new("CmsContentList.MaxAgeDays", "CMS", "Content list", "Age limit", SettingKind.Number,
				"When the file is older, a new crawl asks whether to go on. 0 turns the check off.", Unit: "days", Min: 0, Max: 3650),
			new("CmsContentList.ColumnDelimiter", "CMS", "Reading the file", "Column delimiter", SettingKind.Text,
				"The character between columns, e.g. ; or ,"),
			new("CmsContentList.SkipRows", "CMS", "Reading the file", "Rows before the header", SettingKind.Number,
				"Rows above the column names, e.g. a title or export date. Blank rows count.", Min: 0, Max: 1000),
			new("CmsContentList.RowFilter", "CMS", "Reading the file", "Row filter", SettingKind.Text,
				"Only rows containing this text are read."),
			new("CmsContentList.RowNegativeFilter", "CMS", "Reading the file", "Rows to leave out", SettingKind.Text,
				"Rows containing any of these are skipped; separate alternatives with |."),
			new("CmsContentList.RowsToExclude", "CMS", "Reading the file", "Paths to leave out", SettingKind.Lines,
				"Rows whose path contains one of these are skipped: not compared with the crawl and not fetched by the extra " +
				"download (the main crawl still follows links to them). An entry can be switched off without deleting it; the " +
				"numbers come from the export as the primary site's last run read it.") { Editor = SettingEditor.ContentPaths },
			new("CmsContentList.OnlineColumn", "CMS", "Reading the file", "Online column", SettingKind.Text,
				"The column (its name in the header row) that says whether a page is online. Only rows whose value matches the " +
				"online value are pages of the site: the crawl compares and fetches those; the others are listed apart (log 39) " +
				"and never requested. Empty: every row is a page.") { DefaultMeaning = "none — every row is a page" },
			new("CmsContentList.OnlinePattern", "CMS", "Reading the file", "Online value", SettingKind.Text,
				"The value in the online column that means online; * matches anything, case ignored. Any other value, an empty " +
				"cell too, means not online. Empty: every row is a page.") { DefaultMeaning = "none — every row is a page" },
			new("CmsContentList.ColumnIndex", "CMS", "Reading the file", "Path column", SettingKind.Number,
				"The position of the column with the page path, counted from 0.", Min: 0, Max: 1000),
			new("CmsContentList.ValuePrefixReplace", "CMS", "Reading the file", "Build the path from filter and suffix", SettingKind.Switch,
				"Puts the row filter in front of the column value and the suffix after it to form the page path."),
			new("CmsContentList.ValueSuffix", "CMS", "Reading the file", "Path suffix", SettingKind.Text,
				"Appended to the column value, e.g. .html."),
			new("CmsContentList.PathStripPrefix", "CMS", "Reading the file", "Address prefix to strip", SettingKind.Text,
				"Removed from page addresses before they are matched with the path column. Empty uses the site's address — set it " +
				"only when the export uses another host or scheme.") { DefaultMeaning = "the site's address" },
			new("TicketGeneration.UrlSourceColumn", "CMS", "Page source", "Source column", SettingKind.Text,
				"The column that says where a page comes from; its value is shown as the page's Source in Results, triage and the " +
				"ticket document."),
			new("TicketGeneration.UrlSourceLocalName", "CMS", "Page source", "Value for local pages", SettingKind.Text,
				"The source value of pages managed on this site."),
			new("TicketGeneration.UrlSourceExternalName", "CMS", "Page source", "Value for inherited pages", SettingKind.Text,
				"The source value of pages that come from elsewhere."),
			new("TicketGeneration.SpecialInfoMappings", "CMS", "Page source", "Labels for column values", SettingKind.Json,
				"Each entry checks Column against Pattern (* matches anything, case ignored) and adds Label to the page's source. " +
				"The first match wins.") { Table = TableSpecs.SpecialInfoMappings },
			new("TicketGeneration.PathColumn", "CMS", "CMS link", "Path column name", SettingKind.Text,
				"The column with the page path used for matching. Empty uses the file's first column.") { DefaultMeaning = "the file's first column" },
			new("TicketGeneration.PackageColumn", "CMS", "CMS link", "Package column", SettingKind.Text,
				"The column with the page's package or module name."),
			new("TicketGeneration.CmsPathColumn", "CMS", "CMS link", "CMS path column", SettingKind.Text,
				"The column with the page's path in the CMS, used to build the CMS link."),
			new("TicketGeneration.CmsEditorBaseUrl", "CMS", "CMS link", "CMS editor address", SettingKind.Text,
				"The CMS link is this address, the CMS path and the suffix below, e.g. https://cms.example.com/editor.html"),
			new("TicketGeneration.CmsEditorBaseUrlSuffix", "CMS", "CMS link", "CMS link suffix", SettingKind.Text,
				"Appended to the CMS path when it lacks one, e.g. .html."),

			// ── Spelling ────────────────────────────────────────────────────────
			new("SpellCheckEngine.DefaultLanguage", "Spelling", "Languages", "Default language", SettingKind.Choice,
				"The language of pages that declare none, chosen from the installed dictionary bundles.") { ChoicesFromBundles = true },
			new("SpellCheckEngine.PageLanguageOverrides", "Spelling", "Languages", "Languages by page", SettingKind.Json,
				"Pages whose address path starts with a path are checked against all its ticked languages together; the longest " +
				"matching path wins. A path with no language ticked is not spell-checked. A language without a dictionary bundle " +
				"stays in the file, greyed out, and is skipped — the start-up checks and the run say so.")
				{ Table = TableSpecs.PageLanguageOverrides },
			new("SpellCheckEngine.KnownChromeLanguageDefects", "Spelling", "Languages", "Known template texts", SettingKind.Json,
				"Text the template repeats on every page, muted by where it is (the source path as in findings) and what it says. " +
				"\"Text*\" mutes only the words before the *.") { Table = TableSpecs.KnownChromeLanguageDefects },
			new("SpellCheckEngine.GlobalHtmlTagsToIgnore", "Spelling", "What is not checked", "Elements not checked", SettingKind.Lines,
				"Elements whose whole content is skipped, e.g. svg, iframe, select.") { Editor = SettingEditor.TagChips },
			new("SpellCheckEngine.GlobalXpathToIgnore", "Spelling", "What is not checked", "Technical elements not checked", SettingKind.Lines,
				"XPaths of elements that are never content, on every page — tracking pixels, technical injections. An entry can be " +
				"switched off without deleting it; the numbers come from the primary site's last run.") { Editor = SettingEditor.Xpaths },
			new("SpellCheckEngine.GlobalAttributesToIgnore", "Spelling", "What is not checked", "Attributes not checked", SettingKind.Lines,
				"Attribute names, besides the built-in ones, whose values are tokens, never prose. An entry can be switched off " +
				"without deleting it; the numbers come from the primary site's last run.") { Editor = SettingEditor.Attributes },
			new("SpellCheckEngine.MetaContentNamesToSpellCheck", "Spelling", "What is not checked", "Meta tags checked", SettingKind.Lines,
				"The meta names whose content is checked. Unlocking starts from the built-in list; what is set here replaces it, and " +
				"no entries checks no meta content."),
			new("SpellCheckEngine.GlobalNonProseHtmlAttributesThatWillBeIgnored", "Spelling", "What is not checked", "Structural attributes", SettingKind.Lines,
				"Attributes that are never prose — identifiers, addresses, references, keywords, numbers. Unlocking starts from the " +
				"built-in list; what is set here replaces it."),
			new("SpellCheckEngine.GlobalBooleanHtmlAttributesThatWillBeIgnored", "Spelling", "What is not checked", "Boolean attributes", SettingKind.Lines,
				"On/off attributes such as disabled or checked, whose value is never prose. Unlocking starts from the built-in list; " +
				"what is set here replaces it."),
			new("SpellCheckEngine.BoilerplateGroups", "Spelling", "What is not checked", "Boilerplate groups", SettingKind.Json,
				"Repeated page parts (header, footer, navigation) spell-checked once, on their reference pages, and cut from every other " +
				"page. The longest matching path decides the group of a page. A part of type attribute (@data-x for every element, " +
				"script/@data-x for one) removes only that attribute — a value the template changes on every delivery — on every page, " +
				"reference pages included (they keep the rest of the template whole); attributes the analysis reads are refused. " +
				"The numbers come from the primary site's last run.")
				{ Editor = SettingEditor.Boilerplate },
			new("SpellCheckEngine.HeuristicNonProseDataAttributeSuppression", "Spelling", "What is not checked", "Skip machine-like data attributes", SettingKind.Switch,
				"Skips data-* values that look like selectors, id lists or machine tokens. Values that could be prose are still checked."),
			new("SpellCheckEngine.SuppressSpellFindingsCoveredByWordCollision", "Spelling", "What is not checked", "One report per word collision", SettingKind.Switch,
				"A merged word already reported as a word collision is not reported again as a misspelling."),
			new("SpellCheckWordPrefixesToStrip", "Spelling", "Words", "Known word parts", SettingKind.Lines,
				"Brand and product names that may stand anywhere in a hyphenated word. A word made only of these is accepted, " +
				"also one entry alone. An entry can be switched off without deleting it; the numbers come from the primary site's last run.") { Editor = SettingEditor.KnownParts },
			new("SpellCheckEngine.IgnoreAcronyms", "Spelling", "Words", "Ignore acronyms", SettingKind.Switch,
				"Words in all capitals up to the length below are not spell-checked, alone or as part of a hyphenated word: " +
				"abbreviations no dictionary can judge. They are listed for review in log 37, sorted so that look-alikes sit together."),
			new("SpellCheckEngine.AcronymMaxLength", "Spelling", "Words", "Acronym length", SettingKind.Number,
				"The longest acronym that is not checked. From one letter more on, words in capitals are checked like any other.",
				Unit: "letters", Min: 1, Max: 5)
				{ Extra = RowExtra.Slider, ActiveWhen = "SpellCheckEngine.IgnoreAcronyms" },
			new("SpellCheckEngine.AcronymPluralS", "Spelling", "Words", "Plural s after an acronym", SettingKind.Switch,
				"An acronym followed by a small s (\"ABCs\") counts as an acronym too; the s is not counted in its length. " +
				"Needs at least two capitals, so a word like \"Is\" is still checked.")
				{ ActiveWhen = "SpellCheckEngine.IgnoreAcronyms" },
			new("GermanFugenelemente", "Spelling", "Words", "Linking elements", SettingKind.Lines,
				"Linking elements stripped from a German compound head before a hyphen (\"Lieferungs-\" → \"Lieferung\")."),
			new("ProperNames", "Spelling", "Words", "Proper names", SettingKind.Json,
				"Names no dictionary can judge, with their acronym if they have one. A word of a name passes the spell check, " +
				"and a space at a hyphen in it passes the hyphen check, only where the whole name stands; the acronym passes " +
				"anywhere, with its exact capitals. Triage adds names here from hyphen findings.")
				{ Table = TableSpecs.ProperNames },
			new("SpellCheckEngine.SpellCheckJavaScript.Enabled", "Spelling", "Scripts", "Check text in inline scripts", SettingKind.Switch,
				"Checks text held in strings of inline scripts; code-like strings are filtered out."),
			new("SpellCheckEngine.SpellCheckJavaScript.TokensToFilter", "Spelling", "Scripts", "Script strings to skip", SettingKind.Lines,
				"Whole script strings that are this site's identifiers, never prose.") { Editor = SettingEditor.ScriptStrings },
			new("SpellCheckEngine.SpellCheckJavaScript.ScriptFallbackDictionaries", "Spelling", "Scripts", "Fallback dictionaries for scripts", SettingKind.Lines,
				"Dictionary languages, e.g. en-GB and en-US: a script word any of them accepts is not flagged. Applies to scripts only.") { DefaultMeaning = "none" },
			new("SpellCheckEngine.SpellCheckJavaScript.BulkScanPageScript", "Spelling", "Scripts", "Scan all inline scripts together", SettingKind.Switch,
				"Also scans every inline script of the snapshot as one text (logs 28 and 29)."),
			new("SpellCheckEngine.SpellCheckJavaScript.ScriptBulkScanDictionaries", "Spelling", "Scripts", "Dictionaries for that scan", SettingKind.Lines,
				"Dictionary keys the inline scan checks against; a word is flagged only when all of them reject it."),
			new("SpellCheckEngine.SpellCheckJavaScript.ScanScriptFilesInDownload", "Spelling", "Scripts", "Scan script files", SettingKind.Switch,
				"Scans the downloaded .js files (logs 30 to 33)."),
			new("SpellCheckEngine.SpellCheckJavaScript.ScriptFileScanDictionaries", "Spelling", "Scripts", "Dictionaries for script files", SettingKind.Lines,
				"Dictionary keys the script-file scan checks against — list every language the files may contain."),
			new("SpellCheckEngine.SpellCheckJavaScript.JsFindingPageReachThreshold", "Spelling", "Scripts", "Pages for a site-wide script", SettingKind.Number,
				"A script file used on more pages than this counts as site-wide: its findings are listed once, not per page.",
				Unit: "pages", Min: 0, Max: 100000),

			// ── Dictionaries ────────────────────────────────────────────────────
			new("DictionaryBundles", "Dictionaries", "System dictionaries", "Dictionary bundles", SettingKind.Json,
				"One entry per language: LanguageCode as in the pages' lang, the .dic and .aff files, and their checksums — leave " +
				"those empty at first, the app reports them. Files are taken from the program's folder when the path is relative. " +
				"Flavours of one language (en-GB, en-US): a page naming a flavour gets it; one naming only the language (en), or " +
				"none, gets the flavour marked Primary — mark exactly one per language.")
				{ Table = TableSpecs.DictionaryBundles },
			new("CustomDictionaryFile", "Dictionaries", "Your dictionaries", "User dictionary", SettingKind.Text,
				"The user dictionary's file name in the dictionaries folder. Shared by every site you crawl; a word right on one site only belongs in that site's dictionary.") { DefaultMeaning = NoDefault, Required = true, RequiredText = "enter a file name" },
			new("CustomForeignDictionaryFile", "Dictionaries", "Your dictionaries", "Foreign-word dictionary", SettingKind.Text,
				"An optional list of foreign words accepted on every page.") { DefaultMeaning = "none" },
			new("DictionaryMaintenance.Mode", "Dictionaries", "Clean-up", "Dictionary report", SettingKind.Choice,
				"Report writes the list of entries no page needed (log 15) at the end of each run; Interactive does the same. " +
				"Entries are removed or pinned on the Dictionaries page.", Choices: ["Off", "Report", "Interactive"]),
			new("DictionaryMaintenance.UpdateUserDictionary", "Dictionaries", "Clean-up", "Include the user dictionary", SettingKind.Switch,
				"The user dictionary takes part in the report and in finding entries no longer needed."),
			new("DictionaryMaintenance.UpdateSiteSpecificDictionary", "Dictionaries", "Clean-up", "Include the site dictionary", SettingKind.Switch,
				"The site's dictionary takes part in the report and in finding entries no longer needed."),

			// ── Content quality ─────────────────────────────────────────────────
			new("ContentQuality.CheckQuotePairing", "Content quality", "Checks", "Quote pairing", SettingKind.Switch,
				"Unmatched quotes, wrong openers and wrong closers."),
			new("ContentQuality.CheckQuoteSystemMixing", "Content quality", "Checks", "Quote systems", SettingKind.Switch,
				"Pages that mix the quotes of different languages."),
			new("ContentQuality.CheckLigatures", "Content quality", "Checks", "Ligatures", SettingKind.Switch,
				"Ligature characters (ﬁ, ﬂ …) from text pasted out of PDFs or layout programs."),
			new("ContentQuality.CheckDecomposition", "Content quality", "Checks", "Decomposed text", SettingKind.Choice,
				"Letters stored as base letter plus accent. FlagAll reports every page with them; FlagMixed only pages that mix both " +
				"forms of a word; Off turns the check off.", Choices: ["FlagAll", "FlagMixed", "Off"]),
			new("ContentQuality.CheckControlCharsInContent", "Content quality", "Checks", "Control characters", SettingKind.Switch,
				"Invisible and control characters in title, meta tags and text."),
			new("ContentQuality.CheckNoBreakCharEffect", "Content quality", "Checks", "Line-break glue", SettingKind.Switch,
				"No-break characters next to spaces that do nothing, or make a double space."),
			new("ContentQuality.CheckWordCollisions", "Content quality", "Checks", "Word collisions", SettingKind.Switch,
				"Two words run together where markup ends one and starts the next."),
			new("ContentQuality.CheckHyphenSpacing", "Content quality", "Checks", "Space at a hyphen", SettingKind.Switch,
				"A space after or before a hyphen inside a word (\"Xyzzy- Haus\", \"Xyzzy -Haus\"). A suspended head or tail next to a joining word or punctuation is correct and not reported."),
			new("ContentQuality.HyphenJoiningWords", "Content quality", "Checks", "Words next to a suspended hyphen", SettingKind.Json,
				"Per language: the words that may follow a suspended head (\"Haus- und …\") or precede a suspended tail (\"… und -ausgänge\"). Ignoring case and a final dot. A language without a list allows punctuation only."),
			new("ContentQuality.CheckDoubledWords", "Content quality", "Checks", "Doubled words", SettingKind.Switch,
				"A word written twice in a row inside one block (\"Haus Haus\"). Punctuation between the two means it is meant; words with digits " +
				"or an underscore are not judged. Across a line break (\"Gartenhaus<br> Gartenhaus heißt …\") it is a style note instead: weak punctuation, a colon missing after the term."),
			new("ContentQuality.DoubledWordExceptions", "Content quality", "Checks", "Doublings that are correct", SettingKind.Json,
				"Per language: words whose doubling is grammar or meant (\"die die\", \"doch doch\", \"had had\", \"so so\"). A listed word passes everywhere, " +
				"its slips too. In German a capital followed by the same word in small letters (\"Leben leben\") is not reported, except at the " +
				"start of a sentence. A language without a list reports every doubling.")
				{ Table = TableSpecs.DoubledWordExceptions, ActiveWhen = "ContentQuality.CheckDoubledWords" },
			new("ContentQuality.CheckSplitWordAnchors", "Content quality", "Checks", "Split-word anchors", SettingKind.Switch,
				"Links that close in the middle of a word."),
			new("ContentQuality.CheckMisplacedAnchors", "Content quality", "Checks", "Links and ARIA", SettingKind.Switch,
				"Links without a name, focusable elements hidden from assistive technology, broken ARIA references and adjacent links."),
			new("ContentQuality.AnchorDetection.DetectAdjacent", "Content quality", "Checks", "Adjacent links", SettingKind.Switch,
				"Also reports two links directly next to each other — many sites do this on purpose."),
			new("ContentQuality.CheckBareTextInContainers", "Content quality", "Checks", "Lost paragraphs", SettingKind.Switch,
				"Prose that lost its paragraph markup."),
			new("ContentQuality.CheckLanguageMismatch", "Content quality", "Checks", "Language mismatch", SettingKind.Switch,
				"Pages whose html lang and meta language disagree."),
			new("ContentQuality.CheckPotentialTranslation", "Content quality", "Checks", "Untranslated text", SettingKind.Switch,
				"A page whose misspellings are mostly valid words of another language is reported once as possibly untranslated."),
			new("ContentQuality.TranslationIssueErrorThreshold", "Content quality", "Checks", "Misspellings before that check", SettingKind.Number,
				"How many misspellings a page needs before it is checked for untranslated text.", Min: 1, Max: 10000),
			new("ContentQuality.TranslationIssuePassRatio", "Content quality", "Checks", "Share of foreign words", SettingKind.Decimal,
				"The share of a page's misspellings another dictionary must accept, from 0 to 1.", Min: 0, Max: 1),
			new("ContentQuality.CheckCmsTemplateAuthoringDefects", "Content quality", "Checks", "Template defects", SettingKind.Switch,
				"Embedded byte-order marks and invisible characters in template text (log 22)."),
			new("ContentQuality.MalformedHtml.DetectContentBeforeDoctype", "Content quality", "Markup", "Content before doctype", SettingKind.Switch,
				"Text or markup before the start of the document — almost always a template or server error."),
			new("ContentQuality.MalformedHtml.DetectHtmlParseErrors", "Content quality", "Markup", "HTML parse errors", SettingKind.Switch,
				"Structural errors the HTML parser reports."),
			new("ContentQuality.MalformedHtml.SuppressParseErrorCodes", "Content quality", "Markup", "Parse errors not reported", SettingKind.Lines,
				"Parser error codes that are not defects, e.g. EndTagNotRequired."),
			new("ContentQuality.ContentQualityBlockElements", "Content quality", "Markup", "Text blocks", SettingKind.Lines,
				"Elements whose text is checked block by block for quote and punctuation issues."),
			new("ContentQuality.ContentQualityContainerElements", "Content quality", "Markup", "Containers", SettingKind.Lines,
				"Elements checked for prose that lost its paragraph markup."),
			new("ContentQuality.ContentQualityHeadingClasses", "Content quality", "Markup", "Heading classes", SettingKind.Lines,
				"Classes that make an element look like a heading although it is none (reported as styled headings). Tick the " +
				"h-classes and add the site's own; none at all turns the check off.") { Editor = SettingEditor.HeadingClasses },
			new("ContentQuality.ContentQualityApostropheElisions", "Content quality", "Markup", "Apostrophes by language", SettingKind.Json,
				"Per language: which characters are apostrophes and which elisions (it's, l'accès) are not quotes. _default applies " +
				"to pages with no listed language. One language per line, written as JSON."),
			new("ContentQuality.ContentQualityIssueSuppressions", "Content quality", "Reporting", "Suppressions", SettingKind.Json,
				"Rules that take findings of one type out of the report, site-wide — all of them, or only those containing a text. " +
				"For defects whose fix is not on the page. Each rule needs a comment saying why.") { Editor = SettingEditor.Suppressions },
			new("ContentUnwantedPatterns", "Content quality", "Reporting", "Unwanted patterns", SettingKind.Json,
				"Named groups of plain text patterns reported wherever they occur in a page's source: words and markers, " +
				"placeholders that must never reach the reader, and template bindings that are wrong only when broken.")
				{ Editor = SettingEditor.Patterns },
			new("ContentQualityExcludedUrls", "Content quality", "Reporting", "Pages without content-quality checks", SettingKind.Lines,
				"Pages whose address contains one of these get no content-quality checks."),
			new("ContentQuality.ContentQualityExcerptRadius", "Content quality", "Reporting", "Excerpt radius", SettingKind.Number,
				"Characters shown on each side of a finding.", Unit: "characters", Min: 10, Max: 5000),
			new("ContentQuality.ContentQualityQuoteFullSentence", "Content quality", "Reporting", "Whole sentences for quotes", SettingKind.Switch,
				"Quote findings show the whole sentence instead of a fixed radius."),
			new("ContentQuality.ContentQualityMaxExcerpt", "Content quality", "Reporting", "Longest excerpt", SettingKind.Number,
				"The longest excerpt shown with a finding.", Unit: "characters", Min: 50, Max: 100000),

			// ── SEO & links ─────────────────────────────────────────────────────
			new("Seo.TitleTemplates", "SEO & links", "Titles", "Title patterns", SettingKind.Lines,
				"The site's title patterns, each with one {title} where the page's own title goes, e.g. \"{title} | Company\". Triage reports a page in search whose title matches none; the added text may be cut off in a result, so only the page's own part counts as cut off. Empty: no pattern check."),
			new("QueryStringsToIgnoreForSelfLinkDetermination", "SEO & links", "Links", "Query parameters ignored for self-links", SettingKind.Lines,
				"Parameters ignored when deciding whether a link points to its own page."),
			new("SitemapGeneratorExclusions", "SEO & links", "Sitemap", "Left out of the sitemap", SettingKind.Lines,
				"Pages whose address contains one of these are left out of the generated sitemap."),
			new("SitemapGenerateForcedInclusions", "SEO & links", "Sitemap", "Always in the sitemap", SettingKind.Lines,
				"Pages always included in the generated sitemap."),

			// ── Assets ──────────────────────────────────────────────────────────
			new("AssetQuality.CheckMetadataLeakage", "Assets", "Images", "Metadata in images", SettingKind.Switch,
				"Reports and extracts metadata that should not be published: location, camera, author, software, dates."),
			new("AssetQuality.CheckDimensions", "Assets", "Images", "Image dimensions", SettingKind.Switch,
				"Reports images of 1 pixel or less, or larger than the limit below."),
			new("AssetQuality.MaxImageDimensionPixels", "Assets", "Images", "Largest image side", SettingKind.Number,
				"", Unit: "pixels", Min: 1, Max: 100000),
			new("AssetQuality.CheckSize", "Assets", "Images", "Image size", SettingKind.Switch,
				"Reports truncated images and images larger than the limit below."),
			new("AssetQuality.MaxImageBytes", "Assets", "Images", "Largest image file", SettingKind.Number,
				"", Unit: "bytes", Min: 1, Max: 10_000_000_000),
			new("AssetQuality.SizeAndDimensionExclusions", "Assets", "Images", "Images allowed to be large", SettingKind.Lines,
				"Addresses containing one of these skip the size and dimension checks; metadata is still checked."),
			new("AssetQuality.UnverifiedImagePolicy", "Assets", "Images", "Files that may not be images", SettingKind.Choice,
				"How a download is settled when its address, its Content-Type and its bytes disagree about being an image." + PolicyHelp,
				Choices: Policies) { Cards = PolicyCards },
			new("BloatThresholdBase64Kilobytes", "Assets", "Resource bloat", "Large embedded data", SettingKind.Number,
				"Base64 data embedded in scripts or style sheets that decodes to more than this is reported (log 19).", Unit: "KB", Min: 1, Max: 1_000_000),
			new("BloatThresholdJsKilobytes", "Assets", "Resource bloat", "Extra scripts", SettingKind.Number,
				"Script above the site's baseline that is reported (log 21).", Unit: "KB", Min: 1, Max: 1_000_000),
			new("BloatThresholdCssKilobytes", "Assets", "Resource bloat", "Extra style sheets", SettingKind.Number,
				"Style sheets above the site's baseline that are reported (log 21).", Unit: "KB", Min: 1, Max: 1_000_000),
			new("BloatThresholdAboveBaselineKilobytes", "Assets", "Resource bloat", "Page total above baseline", SettingKind.Number,
				"Pages whose scripts and style sheets exceed the baseline by more than this are listed (log 21).", Unit: "KB", Min: 1, Max: 1_000_000),
			new("Base64AssetFileExtensions", "Assets", "Resource bloat", "Files scanned for embedded data", SettingKind.Lines,
				"The kinds of files scanned for embedded Base64 data.")
				{ Editor = SettingEditor.FileKinds },

			// ── Triage ──────────────────────────────────────────────────────────
			new("TriageModes", TriageTab, "What triage counts", "Triage switches", SettingKind.Json,
				"One switch per kind of finding, as the Results tabs group them. Off: not triaged. Ride-along: goes into the " +
				"ticket of a page that has other defects and never lists a page by itself — nor an entry shared by several pages. " +
				"On: listed. One set for every site; Triage shows what it counts and changes nothing. A mode never changes a " +
				"verdict: an item switched off keeps its verdict for when it is switched on again. Bidi controls are always on.")
				{ DefaultMeaning = "the built-in defaults", Editor = SettingEditor.TriageModes },
			new("TicketLanguage", TriageTab, "Ticket documents", "Ticket language", SettingKind.Choice,
				"The language of the ticket documents (PDF and XPS) handed to the people who fix the pages. Texts from the site — " +
				"page text, addresses, markup — stay as found; the app's screens stay English.",
				Choices: [Text.DocumentText.EnglishName, Text.DocumentText.GermanName]),

			// ── Debug ───────────────────────────────────────────────────────────
			new("DebugMode", "Debug", "Debug mode", "Archive and compare every run", SettingKind.Switch,
				"After every completed run, its output files are copied to the \"debug\" folder next to the program, one folder per " +
				"run of a snapshot, and compared with the previous run of the same snapshot and with a run marked as baseline. " +
				"The run steps say which files are identical and which differ; the lines that differ are in compare.txt in the " +
				"run's folder. The crawled pages themselves are not copied. The test levers below apply only in Debug mode. " +
				"In Triage, Ticket sweep writes every page's ticket as PDF into the folder (ticket-sweeps) and checks them; " +
				"the triage ledger is not touched. Nothing in the folder is ever deleted by the app."),
		];

		private static readonly Dictionary<string, SettingInfo> ByPath = All.ToDictionary(s => s.Path, StringComparer.Ordinal);

		private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

		/// <summary>The setting at a path, or null when the screen does not offer it.</summary>
		public static SettingInfo? Find(string path) => ByPath.GetValueOrDefault(path);

		/// <summary>
		/// The built-in default of a setting as JSON — what applies when the file does not have it.
		/// Null for a setting whose default is decided at run time (a built-in list the app keeps).
		/// </summary>
		[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reads the defaults by reflection.")]
		public static JsonNode? DefaultOf(string path)
		{
			object? current = new Config();
			foreach (var part in path.Split('.'))
			{
				if (current is null)
				{
					return null;
				}

				var property = current.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance)
					?? throw new InvalidOperationException($"No setting {path}.");
				var value = property.GetValue(current);

				// A group the file may leave out entirely (the content list): its values default as in a new group.
				current = value ?? (property.PropertyType.IsClass && property.PropertyType != typeof(string)
					&& !typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)
					? Activator.CreateInstance(property.PropertyType)
					: null);
			}

			return current is null ? null : JsonSerializer.SerializeToNode(current, current.GetType(), Json);
		}

		/// <summary>
		/// The lists the app keeps built in for settings whose default is "absent": the engine
		/// uses them unless the file names its own list, which then replaces them entirely.
		/// </summary>
		private static readonly Dictionary<string, Func<IEnumerable<string>>> BuiltInLists = new(StringComparer.Ordinal)
		{
			["SpellCheckEngine.MetaContentNamesToSpellCheck"] = () => SpellCheck.DomTraverser.DefaultMetaContentNames,
			["SpellCheckEngine.GlobalNonProseHtmlAttributesThatWillBeIgnored"] = () => SpellCheck.DomTraverser.DefaultNonProseAttributes,
			["SpellCheckEngine.GlobalBooleanHtmlAttributesThatWillBeIgnored"] = () => SpellCheck.DomTraverser.DefaultBooleanAttributes,
		};

		/// <summary>
		/// What the app uses when the file does not have the setting: the default value, or for a
		/// built-in list the list itself. Unlocking writes this into the file, so nothing changes
		/// until the value is edited.
		/// </summary>
		[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reads the defaults by reflection.")]
		public static JsonNode? EffectiveDefaultOf(string path) =>
			DefaultOf(path) ?? (BuiltInLists.TryGetValue(path, out var list)
				? new JsonArray(list().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())
				: null);

		/// <summary>The default in words: "150 MB", "on", "none", "built-in list of 3 entries", or the setting's own meaning.</summary>
		[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reads the defaults by reflection.")]
		public static string DescribeDefault(SettingInfo setting)
		{
			if (setting.DefaultMeaning is { } meaning)
			{
				return meaning;
			}

			var unit = setting.Unit is { } u ? " " + u : string.Empty;
			if (DefaultOf(setting.Path) is not { } value)
			{
				return BuiltInLists.TryGetValue(setting.Path, out var list)
					? $"built-in list of {list().Count()} entries"
					: "decided by the app";
			}

			return value switch
			{
				JsonArray { Count: 0 } => "none",
				JsonArray list => list.Count == 1 ? "1 entry" : $"{list.Count} entries",
				JsonObject { Count: 0 } => "none",
				JsonObject map => map.Count == 1 ? "1 entry" : $"{map.Count} entries",
				JsonValue v when v.TryGetValue<bool>(out var b) => b ? "on" : "off",
				JsonValue v when v.TryGetValue<string>(out var text) => text.Length == 0 ? "not set" : text,
				_ => value.ToJsonString() + unit,
			};
		}
	}
}
