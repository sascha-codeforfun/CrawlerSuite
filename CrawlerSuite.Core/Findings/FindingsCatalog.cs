using System.Text.RegularExpressions;
using CrawlerSuite.Core.Formats;

namespace CrawlerSuite.Core.Findings
{
	/// <summary>
	/// The findings views of a snapshot, as tabs: Content quality (family → issue type), then
	/// Spelling, Scripts, Links, SEO, Assets and Crawl (view). Every view is read from the
	/// output files the run wrote; nothing is written.
	/// </summary>
	public static partial class FindingsCatalog
	{
		private static readonly FindingColumn Page = new("Page", ColumnKind.Url, IsPage: true);
		private static readonly FindingColumn Context = new("Context", ColumnKind.Text);

		// The page with the link to a failed or broken URL: the link is fixed there.
		private static readonly FindingColumn LinkedFrom = new("Linked from", ColumnKind.Url, IsPage: true);

		// "word (en, de)" — the word may itself contain spaces or brackets; the languages are the last group.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex WordLangsRegex = new(@"^(?<word>.*) \((?<langs>[^()]*)\)$", RegexOptions.Compiled);

		private static Regex WordLangs() => WordLangsRegex;

		// "word (en) (meta[@name=description])" in log 12.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex WordLangsMetaRegex = new(@"^(?<word>.*?) \((?<langs>[^()]*)\)(?: \((?<meta>.*)\))?$", RegexOptions.Compiled);

		private static Regex WordLangsMeta() => WordLangsMetaRegex;

		// "word (en) @ https://…" — a block header in log 13.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex LocatedHeaderRegex = new(@"^(?<word>.*) \((?<langs>[^()]*)\) @ (?<url>\S.*)$", RegexOptions.Compiled);

		private static Regex LocatedHeader() => LocatedHeaderRegex;

		// "     12×  someLiteral                     context…" — a row in log 27.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex SuggestionRowRegex = new(@"^\s*(?<spread>\d+)×\s+(?<literal>\S+)\s*(?<context>.*)$", RegexOptions.Compiled);

		private static Regex SuggestionRow() => SuggestionRowRegex;

		/// <summary>Reads every findings view of <paramref name="snapshotDirectory"/>. Call it off the UI thread.</summary>
		public static IReadOnlyList<FindingNode> Read(string snapshotDirectory)
		{
			var s = new Snapshot(snapshotDirectory);
			return
			[
				ContentQualityTab(s),
				SpellingTab(s),
				ScriptsTab(s),
				LinksTab(s),
				SeoTab(s),
				AssetsTab(s),
				CrawlTab(s),
			];
		}

		/// <summary>
		/// Reads one top-level findings tab (by its title) — the same view <see cref="Read"/> builds,
		/// so its counts match the Results page. Null for an unknown title. Call it off the UI thread.
		/// </summary>
		public static FindingNode? ReadTab(string snapshotDirectory, string title)
		{
			Func<Snapshot, FindingNode>? build = title switch
			{
				"Content quality" => ContentQualityTab,
				"Spelling" => SpellingTab,
				"Scripts" => ScriptsTab,
				"Links" => LinksTab,
				"SEO" => SeoTab,
				"Assets" => AssetsTab,
				"Crawl" => CrawlTab,
				_ => null,
			};
			return build?.Invoke(new Snapshot(snapshotDirectory));
		}

		// ── Content quality: family → issue type ─────────────────────────────────

		/// <summary>The families and their issue types, in the order they are shown.</summary>
		internal static readonly (string Family, string Note, (string Type, string Label)[] Types)[] ContentQualityFamilies =
		[
			("Quotes", "", [
				("QUOTE_UNMATCHED", "Unmatched"),
				("QUOTE_WRONG_OPEN", "Wrong opener"),
				("QUOTE_WRONG_CLOSE", "Wrong closer"),
				("QUOTE_WRONG_APOSTROPHE", "Wrong apostrophe"),
				("QUOTE_MIXED_KIND", "Mixed kinds"),
				("QUOTE_SYSTEM_MIX", "Mixed quote systems")]),
			("Characters", "", [
				("LIGATURE", "Ligatures"),
				("CONTROL_CHARS_IN_CONTENT", "Control characters"),
				("DECOMPOSED_TEXT_IN_CONTENT", "Decomposed text"),
				("MIXED_NORMALIZATION_IN_CONTENT", "Mixed normalisation")]),
			("Words & spacing", "", [
				("WORD_COLLISION", "Word collisions"),
				("WORD_SPLIT_BY_FORMATTING", "Split by formatting"),
				("WORD_DOUBLED", "Doubled word"),
				("PUNCTUATION_WEAK", "Weak punctuation"),
				("SPACE_AFTER_HYPHEN", "Space after hyphen"),
				("SPACE_BEFORE_HYPHEN", "Space before hyphen"),
				("LINE_BREAK_GLUE_INERT", "Line-break glue (inert)"),
				("LINE_BREAK_GLUE_DOUBLE_SPACE", "Line-break glue (double space)"),
				("BARE_TEXT_IN_CONTAINER", "Bare text in container")]),
			("Anchors", "", [
				("SPLIT_WORD_ANCHOR", "Split-word anchor"),
				("ADJACENT_ANCHOR", "Adjacent anchors")]),
			("Accessibility", "", [
				("MISPLACED_ANCHOR_EMPTY", "Links without a name"),
				("MISPLACED_ANCHOR_EMPTY_CONDITIONAL", "Links without a name (conditional)"),
				("MISPLACED_ANCHOR_EMPTY_UNDETERMINED", "Links without a name (undetermined)"),
				("MISPLACED_ANCHOR_EMPTY_CLEARED", "Links without a name, hidden (cleared)"),
				("ARIA_HIDDEN_FOCUSABLE", "Hidden but focusable"),
				("ARIA_HIDDEN_FOCUSABLE_CONDITIONAL", "Hidden but focusable (conditional)"),
				("ARIA_HIDDEN_FOCUSABLE_UNDETERMINED", "Hidden but focusable (undetermined)"),
				("ARIA_HIDDEN_FOCUSABLE_CLEARED", "Hidden, not focusable (cleared)"),
				("ARIA_REFERENCE_BROKEN", "Broken ARIA references")]),
			("Markup", "", [
				("MALFORMED_HTML", "Malformed HTML"),
				("CONTENT_BEFORE_DOCTYPE", "Content before doctype")]),
			("Language", "", [
				("LANGUAGE_MISMATCH", "Language mismatch")]),
			("Your patterns", "", [
				("UNWANTED_PATTERN", "Unwanted patterns")]),
			("Template defects", "For the template / architect team (log 22).", [
				("EMBEDDED_BOM_IN_BODY", "Embedded BOM"),
				("INVISIBLE_CHAR_IN_BODY", "Invisible characters in body"),
				("STYLED_HEADING", "Styled headings")]),
		];

		private static FindingNode ContentQualityTab(Snapshot s)
		{
			// Rows: type, page, detail, context, local file. Log 10 names the file; log 22 also the URL.
			var rows = new List<(string Type, FindingRow Row)>();
			bool written = false;
			string? problem = null;

			var editor = s.File("10");
			if (editor is not null)
			{
				written = true;
				problem = Snapshot.Guard(editor, () =>
				{
					foreach (var c in FindingFiles.Delimited(editor, "|", 4, header: true))
					{
						rows.Add((LeafType(c[1], c[2]), new FindingRow([s.UrlOf(c[0]), c[2], c[3]], s.Download(c[0]))));
					}
				});
			}

			var template = s.File("22");
			if (template is not null)
			{
				written = true;
				problem ??= Snapshot.Guard(template, () =>
				{
					foreach (var c in FindingFiles.Csv(template, 5, header: true))
					{
						var page = c[1].Length > 0 ? c[1] : s.UrlOf(c[0]);
						rows.Add((LeafType(c[2], c[3]), new FindingRow([page, c[3], c[4]], s.Download(c[0]))));
					}
				});
			}

			var byType = rows.ToLookup(r => r.Type, StringComparer.OrdinalIgnoreCase);

			// The context column knows its issue type, so the view can mark it like the triage does.
			static FindingColumn[] ColumnsFor(string type) => [Page, new("Detail"), new("Context", ColumnKind.QualityContext, type)];
			var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var families = new List<FindingNode>();
			foreach (var (family, note, types) in ContentQualityFamilies)
			{
				var leaves = new List<FindingNode>();
				foreach (var (type, label) in types)
				{
					known.Add(type);
					var matching = byType[type].Select(r => r.Row).ToList();
					if (type == Quality.SecurityPatterns.Type)
					{
						// Security-relevant groups first, marked; the rest in their order.
						leaves.Add(FindingNode.Leaf(label, type,
							written ? new FindingTable(PatternColumns, MarkSecurity(matching), problem) : null, Bases.GetValueOrDefault(type)));
						continue;
					}

					leaves.Add(type switch
					{
						"INVISIBLE_CHAR_IN_BODY" => InvisibleByCause(label, matching, written, problem, ColumnsFor(type)),
						"ADJACENT_ANCHOR" => FindingNode.Leaf(label, type + " · one row per page",
							written ? new FindingTable(ColumnsFor(type), AdjacentByPage(matching), problem) : null, Bases.GetValueOrDefault(type)),
						"EMBEDDED_BOM_IN_BODY" => FindingNode.Leaf(label, type + " · one row per file that carries a BOM",
							written ? new FindingTable(BomColumns, BomByMergedFile(matching), problem) : null, Bases.GetValueOrDefault(type)),
						"STYLED_HEADING" => FindingNode.Leaf(label, type + " · one row per pattern",
							written ? new FindingTable(StyledHeadingColumns, StyledHeadingsByPattern(matching), problem) : null, Bases.GetValueOrDefault(type)),
						_ => FindingNode.Leaf(label, type, written ? new FindingTable(ColumnsFor(type), matching, problem) : null, Bases.GetValueOrDefault(type)),
					});
				}

				families.Add(FindingNode.Group(family, leaves, note: note));
			}

			// Anything the engine writes that is not mapped yet still shows up, under Other.
			var other = rows.Where(r => !known.Contains(r.Type)).ToList();
			if (other.Count > 0)
			{
				var leaves = other.GroupBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
					.OrderBy(g => g.Key, StringComparer.Ordinal)
					.Select(g => FindingNode.Leaf(g.Key, g.Key, new FindingTable(ColumnsFor(g.Key), g.Select(r => r.Row).ToList())))
					.ToList();
				families.Add(FindingNode.Group("Other", leaves, note: "Issue types without a family yet."));
			}

			return FindingNode.Group("Content quality", families);
		}

		private const string Understanding = "https://www.w3.org/WAI/WCAG22/Understanding/";
		private const string ActRules = "https://www.w3.org/WAI/standards-guidelines/act/rules/";

		// Shared by several cards below; declared first, because static fields initialise in order.
		private static readonly IReadOnlyList<(string Label, string Url)> QuoteLinks =
		[
			("Quotation mark conventions by language", "https://en.wikipedia.org/wiki/Quotation_mark"),
			("Unicode chart: General Punctuation (U+2000)", "https://www.unicode.org/charts/PDF/U2000.pdf"),
		];

		private static readonly IReadOnlyList<(string Label, string Url)> NormalisationLinks =
		[
			("Unicode normalization forms (UAX #15)", "https://www.unicode.org/reports/tr15/"),
			("W3C: Character Model — String Matching", "https://www.w3.org/TR/charmod-norm/"),
		];

		private const string LineBreaking = "https://www.unicode.org/reports/tr14/";

		// The rules the line-break cards cite, each with a link of its own (Citations links them in place).
		private static readonly IReadOnlyList<(string Label, string Url)> LineBreakInertLinks =
		[
			("Unicode line breaking algorithm (UAX #14)", LineBreaking),
			("UAX #14, rule LB12a: no break before a no-break character, except after a space, a dash or a hyphen", LineBreaking + "#LB12a"),
			("CSS: white-space", "https://www.w3.org/TR/css-text-3/#white-space-property"),
		];

		private static readonly IReadOnlyList<(string Label, string Url)> LineBreakDoubleSpaceLinks =
		[
			("Unicode line breaking algorithm (UAX #14)", LineBreaking),
			("UAX #14, rule LB12a: no break before a no-break character, except after a space, a dash or a hyphen", LineBreaking + "#LB12a"),
			("UAX #14, rule LB18: a break is allowed after a space", LineBreaking + "#LB18"),
			("CSS: white-space", "https://www.w3.org/TR/css-text-3/#white-space-property"),
		];

		/// <summary>
		/// The explanation of a finding: what it is based on and how to fix it — the card above its
		/// Results table, so Results and triage explain a defect in the same words. Invisible
		/// characters are explained per cause, read from the detail. <c>Key</c> is the same for
		/// findings that share the explanation. Null for a type without one.
		/// </summary>
		public static (string Key, FindingBasis Basis)? BasisFor(string type, string detail)
		{
			detail ??= string.Empty;
			if (type == "INVISIBLE_CHAR_IN_BODY")
			{
				var cause = CauseOfDetail(detail);
				return InvisibleCauses.TryGetValue(cause, out var c) ? ($"{type}:{cause}", c.Basis) : null;
			}

			if (type == "CONTROL_CHARS_IN_CONTENT")
			{
				// The character decides the explanation, as for invisible characters in template text;
				// a soft hyphen here sits in title or meta, where it is reported on purpose.
				var cause = CauseOfDetail(detail);
				if (cause == InvisibleCause.SoftHyphens)
				{
					return ($"{type}:{cause}", SoftHyphenInHead);
				}

				// A line break or tab is reported only inside the title or a meta value (in text it is whitespace).
				if (IsLineBreakOrTab(detail))
				{
					return ($"{type}:LineBreak", LineBreakInHead);
				}

				return InvisibleCauses.TryGetValue(cause, out var c) && cause != InvisibleCause.Other
					? ($"{type}:{cause}", c.Basis)
					: (type, Bases[type]);
			}

			if (type == Triage.BrokenLinkSource.Type)
			{
				return (type, BrokenLinksBasis);
			}

			// Content before the doctype is written as a MALFORMED_HTML finding with this detail.
			if (type == "MALFORMED_HTML" && detail.StartsWith("CONTENT_BEFORE_DOCTYPE", StringComparison.Ordinal))
			{
				type = "CONTENT_BEFORE_DOCTYPE";
			}

			return Bases.TryGetValue(type, out var basis) ? (type, basis) : null;
		}

		/// <summary>
		/// Text the operator marked as an intended other language (the Locale verdict): it stays, but
		/// needs language markup. Shown in triage above the first such item and in the ticket document.
		/// </summary>
		public static readonly FindingBasis LanguageOfParts = new(
			"A screen reader reads this passage with the page language's pronunciation, so it comes out garbled. " +
			"Translation and spell-check tools treat it as the page language too, and so does hyphenation. The text is in " +
			"another language than the page, intended here, but it needs language markup. Not needed for proper names, technical terms and words that have become " +
			"part of the page language. Fix: put lang on the element around the passage, e.g. <span lang=\"de\">…</span>, " +
			"or on the paragraph when all of it is in that language.",
			[
				("3.1.2 Language of Parts", Understanding + "language-of-parts.html"),
				("Technique H58: lang attribute for changes of language", "https://www.w3.org/WAI/WCAG22/Techniques/html/H58"),
				("W3C: Declaring language in HTML", "https://www.w3.org/International/questions/qa-html-language-declarations"),
			],
			IsViolation: true,
			IsWcag: true,
			Requirement: "WCAG 2.2 · 3.1.2 Language of Parts (Level AA)");

		/// <summary>A soft hyphen in the title or a meta tag (control characters), where it is reported on purpose.</summary>
		private static readonly FindingBasis SoftHyphenInHead = new(
			"Typography issue. A soft hyphen (U+00AD) stands in the page title or a meta tag. In body " +
			"text it is a wanted hyphenation hint; here nothing wraps, so it does nothing — and title and description are " +
			"reused by search results, browser tabs, bookmarks and link previews, where it may show up or split the word. " +
			"Likely cause: a headline field with hyphenation hints is reused for the title or the description. Fix: once in " +
			"the template, strip U+00AD when writing the title and meta tags.",
			[("Unicode: characters not suitable for markup (UTR #20)", "https://www.unicode.org/reports/tr20/")],
			IsViolation: false);

		/// <summary>"Found LF (U+000A) in …", "Found CR …", "Found TAB …": the detail of a whitespace control in title or meta.</summary>
		private static bool IsLineBreakOrTab(string detail) =>
			detail.StartsWith("Found LF ", StringComparison.Ordinal)
			|| detail.StartsWith("Found CR ", StringComparison.Ordinal)
			|| detail.StartsWith("Found TAB ", StringComparison.Ordinal);

		private static readonly FindingBasis LineBreakInHead = new(
			"Typography issue. A line break or tab stands inside the page title or a meta value, " +
			"usually the description. It never does what was meant: search results, browser tabs, bookmarks and link " +
			"previews show it as a plain space — no line break appears there, and none can be made from this field. Likely " +
			"cause: text pasted from a PDF or a Word document, which keeps the line ends of its original layout, or Enter " +
			"pressed in the field. Fix: remove the break in the field. Paste such text as plain text and join the lines — " +
			"or type short fields like title and description directly.",
			[("Unicode: characters not suitable for markup (UTR #20)", "https://www.unicode.org/reports/tr20/")],
			IsViolation: false);

		/// <summary>What the accessibility checks are based on, shown above their tables.</summary>
		// Declared before Bases: static fields initialise in order, and Bases reads these.
		/// <summary>The card of the self-links: Results and triage.</summary>
		internal static readonly FindingBasis SelfLinkBasis = new(
			"Links. A link in the page's content (navigation, header and footer left out) that leads to the " +
			"page itself: a visitor who follows it lands where they already are, and a screen reader announces a link that " +
			"goes nowhere new. Often left over from a teaser or link list copied onto its own target page. Not counted: " +
			"links to a place on the page (#…) and links that differ only in a query the settings ignore. Fix: remove the " +
			"link, or point it where it was meant to go.",
			[],
			IsViolation: false);

		/// <summary>Where an open redirect is described: the weakness and the guidance against it.</summary>
		private static readonly IReadOnlyList<(string Label, string Url)> ForwardingLinks =
		[
			("CWE-601: URL redirection to an untrusted site (open redirect)", "https://cwe.mitre.org/data/definitions/601.html"),
			("OWASP: Unvalidated Redirects and Forwards Cheat Sheet", "https://cheatsheetseries.owasp.org/cheatsheets/Unvalidated_Redirects_and_Forwards_Cheat_Sheet.html"),
		];

		/// <summary>A link that hands the site's server another site's address (log 40).</summary>
		internal static readonly FindingBasis ForwardingExternalBasis = new(
			"Security. A link on this page leads to the site's own server and hands it the full address of another site in " +
			"its query (…?next=https://…): the server is to forward the visitor there. If it forwards to any address it is " +
			"given, anyone can build a link on the site's own address that lands on a site of their choosing — an open " +
			"redirect (CWE-601), a common lure in phishing, since the link shows the trusted address. The app reads such links " +
			"from the saved pages and never requests them, so whether the server checks the address is not known. Fix: check " +
			"by hand that the forwarding accepts only its intended destinations (a fixed list); where the destination is fixed " +
			"anyway, link it directly. A link checked and fine is decided once: the verdict keeps it out of the next triage.",
			ForwardingLinks,
			IsViolation: false);

		/// <summary>A link that hands the site's server an address of the site itself (log 40).</summary>
		internal static readonly FindingBasis ForwardingSameHostBasis = new(
			"Links. A link on this page leads to the site's own server and hands it a full address in its query, here " +
			"one of the site itself: the server is to forward the visitor there. Harmless as long as the forwarding accepts " +
			"only addresses of the site — the same link with another site's address would be an open redirect (CWE-601). The " +
			"app reads such links from the saved pages and never requests them. Fix: link the destination directly, or check " +
			"by hand that the forwarding refuses other sites. A link checked and fine is decided once: the verdict keeps it " +
			"out of the next triage.",
			ForwardingLinks,
			IsViolation: false);

		/// <summary>Where canonical tags are described: the standard and a search engine's guidance.</summary>
		private static readonly IReadOnlyList<(string Label, string Url)> CanonicalLinks =
		[
			("RFC 6596: The Canonical Link Relation", "https://www.rfc-editor.org/rfc/rfc6596"),
			("Google: Consolidate duplicate URLs", "https://developers.google.com/search/docs/crawling-indexing/consolidate-duplicate-urls"),
		];

		/// <summary>The card of the canonical issues in Results; triage shows the card of each kind.</summary>
		internal static readonly FindingBasis CanonicalBasis = new(
			"Search engines. Problems with a page's canonical tag: a target that was not found (also listed under " +
			"Broken links, where triage decides it), a target that redirects, a target kept out of search (noindex) while the " +
			"page is not, a chain (the canonical names a page with a canonical of its own), several canonical tags on one page, " +
			"or a target on another host (listed here, not triaged).",
			CanonicalLinks,
			IsViolation: false);

		/// <summary>The card of the image findings in Results.</summary>
		internal static readonly FindingBasis ImagesBasis = new(
			"Images. What the saved images show on reading the file: a file shorter than the server announced " +
			"(cut off on the way or on the server), a file or picture larger than the limits in the settings, a picture of 0 or " +
			"1 pixel (mostly a counter or a spacer), and the metadata a camera or an editing program wrote into the file. " +
			"Metadata is listed as found, not judged: location and device serial numbers can tell more than meant; a " +
			"photographer's name or a copyright notice is often there on purpose. Triage takes truncated and oversized files, " +
			"oversized pictures, location and device data by default, and tiny pictures and names of people when switched on " +
			"(Settings › Triage); rights, equipment, program and date fields stay here. Addresses listed under Size and " +
			"dimension exclusions are not checked for size.",
			[("web.dev: Learn Images", "https://web.dev/learn/images/welcome")],
			IsViolation: false);

		/// <summary>The card of log 38 in Results.</summary>
		internal static readonly FindingBasis ImageReferencesBasis = new(
			"Who uses each image that triage takes: the pages and stylesheets that reference it, found in the broken-links " +
			"pass without a request. An image is shown when a page places it (an image, a srcset candidate, a link element, " +
			"a stylesheet rule that applies); it is linked when a page only offers it as a link, for example a press photo " +
			"to download. Triage places a finding on the one page or stylesheet that uses the image, or on the image when " +
			"several do; a size finding counts only where the image is shown. Scripts are not run: an image a script adds " +
			"has no reference here.",
			[],
			IsViolation: false);

		/// <summary>Where a search result's title and description are described: a search engine's guidance.</summary>
		private static readonly IReadOnlyList<(string Label, string Url)> SeoLinks =
		[
			("Google: Title links in search results", "https://developers.google.com/search/docs/appearance/title-link"),
			("Google: Snippets in search results", "https://developers.google.com/search/docs/appearance/snippet"),
		];

		/// <summary>Where headings and the page's main heading are described.</summary>
		private static readonly IReadOnlyList<(string Label, string Url)> HeadingLinks =
		[
			("W3C WAI: Headings (page structure tutorial)", "https://www.w3.org/WAI/tutorials/page-structure/headings/"),
		];

		/// <summary>The cut-off widths named on the cards, in pixels.</summary>
		private static class SearchSnippetLimits
		{
			public static readonly string Title = Seo.SearchSnippet.TitleLimitPixels.ToString(System.Globalization.CultureInfo.InvariantCulture);
			public static readonly string Description = Seo.SearchSnippet.DescriptionLimitPixels.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		internal static readonly Dictionary<string, FindingBasis> Bases = new(StringComparer.Ordinal)
		{
			[Triage.SeoSource.TitleMissing] = new(
				"A screen reader announces the address instead of a page name, and browser tabs and bookmarks show the address " +
				"too. The page has no title, or only the text the site adds to every title; a search engine then makes a title up " +
				"from the page. Fix: give the page a title in its settings that says what this page is about.",
				[("2.4.2 Page Titled", Understanding + "page-titled.html"), .. SeoLinks],
				IsViolation: true,
				IsWcag: true,
				Requirement: "WCAG 2.2 · 2.4.2 Page Titled (Level A)"),
			[Triage.SeoSource.TitlePatternMissed] = new(
				"Search engines. The page's title does not follow the site's title pattern (Settings › SEO & links › Title " +
				"patterns): the text the site adds to every title — usually its name — is missing, written differently, or " +
				"typed by hand into the page's own title. Results from this site then look inconsistent, and a hand-typed " +
				"addition goes out of date unnoticed. Fix: enter only the page's own title in its settings and let the system " +
				"add the rest; if the page is meant to differ, add its pattern to the settings.",
				SeoLinks,
				IsViolation: false),
			[Triage.SeoSource.TitleShared] = new(
				"Search engines. Several pages in search carry the same title. A search engine cannot tell from the " +
				"result which page answers the question, may show only one of them, or rewrites the titles itself. Fix: give " +
				"each page a title of its own in its settings; the item names the other pages with the same title. Where a page " +
				"also has the same description and main heading, it is likely the same page at another address (a subdomain " +
				"counts as another address): decide which address stays in search and give the others a canonical to it or a " +
				"noindex. Rides along by default: dealt with when the page is open for something else.",
				SeoLinks,
				IsViolation: false),
			[Triage.SeoSource.TitleCut] = new(
				"Search engines. The title is likely cut off in a search result, which shows about " +
				SearchSnippetLimits.Title + " pixels of it on a desktop. The width is estimated from the widths of the letters, " +
				"so wide letters are cut sooner than narrow ones; a search engine may also rewrite a title. Fix: put what " +
				"matters first and shorten the title in the page's settings — the preview shows where it would be cut.",
				SeoLinks,
				IsViolation: false),
			[Triage.SeoSource.DescriptionMissing] = new(
				"Search engines. The page has no meta description. A search engine then picks text from the page for " +
				"the result, often a menu or a cookie notice. Fix: write a description in the page's settings: one or two " +
				"sentences on what the page offers.",
				SeoLinks,
				IsViolation: false),
			[Triage.SeoSource.DescriptionShared] = new(
				"Search engines. Several pages in search carry the same meta description. The results look alike and " +
				"a search engine tends to ignore the description and pick its own text. Fix: give each page a description of " +
				"its own in its settings; the item names the other pages with the same description, and those that look like " +
				"the same page at another address, where the fix is to choose one address. Rides along by default.",
				SeoLinks,
				IsViolation: false),
			[Triage.SeoSource.DescriptionCut] = new(
				"Search engines. The description is likely cut off in a search result, which shows about " +
				SearchSnippetLimits.Description + " pixels of it on a desktop. An estimate from the widths of the letters; a " +
				"search engine may also show other text. Fix: put what matters first and shorten the description in the " +
				"page's settings — the preview shows where it would be cut.",
				SeoLinks,
				IsViolation: false),
			[Triage.SeoSource.Keywords] = new(
				"Search engines. This page carries a keywords meta tag. Google has not used it for ranking since " +
				"2009; Bing has said that stuffed or off-topic keywords count against a page. It shows anyone who reads the " +
				"page source which terms the page aims at, and since no visitor sees it, it goes out of date unnoticed. Fix: " +
				"empty the keywords field in the page's settings. If the site's own search uses that field, check with whoever " +
				"runs it first.",
				SeoLinks,
				IsViolation: false),
			[Triage.SeoSource.H1Missing] = new(
				"Markup issue. The page has no main heading (H1), or one without text. Screen reader users jump to " +
				"the H1 to learn what the page is about and where its content starts; search engines read it as the page's " +
				"topic. Fix: give the page one H1 that names it — in the page when the editor sets the heading, in the template " +
				"when it leaves the heading out.",
				HeadingLinks,
				IsViolation: false),
			[Triage.SeoSource.H1Several] = new(
				"Markup issue. The page has several H1s with different texts. A screen reader's list of headings then " +
				"shows several main headings, and which one names the page is unclear; search engines get mixed signals too. " +
				"Often a template makes a teaser or a box title an H1. Fix: keep one H1 that names the page and make the others " +
				"H2 or lower — in the template when every page shows the same pattern.",
				HeadingLinks,
				IsViolation: false),
			[Triage.SeoSource.H1Repeated] = new(
				"Markup issue. The page has several H1s with the same text. Usually a template writes the heading once " +
				"per layout — one for wide screens, one for narrow — and hides all but one; then a screen reader hears it once, " +
				"and nothing needs fixing. The crawler reads the HTML, not what the stylesheet shows at each width, so it cannot " +
				"tell. Check once per template in the browser's developer tools, at a wide and a narrow width: all but one copy " +
				"must be hidden with display:none or the hidden attribute, not just moved off screen. Off by default.",
				HeadingLinks,
				IsViolation: false),
			[Triage.ImageSource.Truncated] = new(
				"Images. The image file is shorter than the length the server announced: the picture breaks off, " +
				"shows grey or does not show at all. Either the download was cut off — then the next crawl is fine — or the " +
				"file on the server is damaged. Fix: open the image address in a browser; when it is cut off there too, upload " +
				"the image again.",
				[],
				IsViolation: false),
			[Triage.ImageSource.FileOversize] = new(
				"Images. The image file is larger than the limit in the settings, and a page shows it: every visit " +
				"downloads it, which costs time and data, most on phones and slow connections; search engines measure loading " +
				"speed. An image only offered as a link (a high-resolution download) is not triaged. Fix: save the image at the " +
				"size it is shown, in a compressed format, and keep the large file as a download where it is wanted.",
				[("web.dev: Learn Images", "https://web.dev/learn/images/welcome")],
				IsViolation: false),
			[Triage.ImageSource.DimensionsOversize] = new(
				"Images. The picture is wider or taller than the limit in the settings, and a page shows it: the " +
				"browser downloads all its pixels and scales them down. Fix: save the image at the largest size it is shown " +
				"(twice that for sharp screens), or offer sizes with srcset.",
				[("web.dev: Learn Images", "https://web.dev/learn/images/welcome")],
				IsViolation: false),
			[Triage.ImageSource.Tiny] = new(
				"Images. A picture of 0 or 1 pixel on a side, shown by a page: mostly a visitor counter " +
				"(tracking pixel) or a spacer from older layouts. A counter is a privacy question — it reports a visit to " +
				"whoever runs it; a spacer is dead weight. Fix: remove what is not needed; for a counter, check that it is " +
				"covered where the site asks for consent.",
				[],
				IsViolation: false),
			[Triage.ImageSource.LocationOrDevice] = new(
				"Privacy. The image file carries where the picture was taken (GPS) or what identifies the device or " +
				"its owner (serial numbers, owner name). Anyone who downloads the image can read it — a home address, a " +
				"colleague's camera. Fix: remove the metadata before uploading (most editing programs offer \"export without " +
				"metadata\"), or let the system strip it on upload; then replace the file.",
				[],
				IsViolation: false),
			[Triage.ImageSource.People] = new(
				"Privacy. The image file names a person — the photographer or author. Often this is wanted: a credit " +
				"or a copyright the picture must carry. Where it is not (a staff member's name in a snapshot), anyone who " +
				"downloads the image can read it. Fix where unwanted: remove the field before uploading, then replace the file.",
				[],
				IsViolation: false),
			[Triage.SelfLinkSource.Type] = SelfLinkBasis,
			[Triage.ForwardingLinkSource.External] = ForwardingExternalBasis,
			[Triage.ForwardingLinkSource.SameHost] = ForwardingSameHostBasis,
			[Triage.CanonicalSource.Chain] = new(
				"Search engines. The page's canonical tag names another page, and that page names yet another as its " +
				"canonical. A search engine may follow the chain, stop at its first step, or ignore the hint altogether, so which " +
				"address it shows is left to chance. Fix: let the canonical name the final page directly.",
				CanonicalLinks,
				IsViolation: false),
			[Triage.CanonicalSource.Conflict] = new(
				"Search engines. The page carries more than one canonical tag. When they disagree, a search engine may " +
				"ignore all of them; the app reads the first, as the detail lists. Often a template and a page setting each add " +
				"one. Fix: keep a single canonical tag.",
				CanonicalLinks,
				IsViolation: false),
			[Triage.CanonicalSource.Redirect] = new(
				"Search engines. The page's canonical tag names an address that redirects. A canonical should name the " +
				"page to show in search as it is; a search engine that is sent on may take the hint as a mistake and ignore it. " +
				"Often the address was moved and the canonical kept the old one. Fix: let the canonical name the address the " +
				"redirect leads to — the detail names it — or the page itself.",
				CanonicalLinks,
				IsViolation: false),
			[Triage.CanonicalSource.Noindex] = new(
				"Search engines. The page's canonical tag names a page that is kept out of search (noindex), while this " +
				"page is not. One signal says \"show that page instead\", the other \"show that page nowhere\": a search engine " +
				"may then show neither. Fix: let the canonical name this page itself, or a page in search; if both are meant " +
				"to stay out of search, give this page noindex too.",
				CanonicalLinks,
				IsViolation: false),
			["MISPLACED_ANCHOR_EMPTY"] = new(
				"A screen reader announces this link as just \"link\", or reads out its address: its users can't tell where it " +
				"leads. A link that is shown and announced needs a name — its text, aria-label, aria-labelledby, an image alt, or a " +
				"title — checked following the W3C test rule \"Link has non-empty accessible name\". Not flagged: anchors without " +
				"href (unless role or tabindex makes them interactive), hidden elements, and hidden duplicates (aria-hidden with " +
				"tabindex=\"-1\"). Checked against the page's stylesheets: a link a rule hides is listed separately (cleared, or " +
				"conditional when the rule applies only under a media query or in a state; undetermined when a stylesheet is " +
				"missing from the snapshot). Fix: give the " +
				"link a name — visible text, an alt on its image, or aria-label.",
				[
					("2.4.4 Link Purpose (In Context)", Understanding + "link-purpose-in-context.html"),
					("4.1.2 Name, Role, Value", Understanding + "name-role-value.html"),
					("W3C test rule: Link has non-empty accessible name", ActRules + "c487ae/"),
					("Failure F89: image link without alt text", "https://www.w3.org/WAI/WCAG22/Techniques/failures/F89"),
				],
				IsViolation: true, IsWcag: true, Requirement: "WCAG 2.2 · 2.4.4 Link Purpose (In Context) and 4.1.2 Name, Role, Value (Level A)"),
			["MISPLACED_ANCHOR_EMPTY_CONDITIONAL"] = new(
				"At some screen widths or in some states, a link without a name is shown: a screen reader announces it as just " +
				"\"link\". The page's CSS hides it only under a media query (a screen width, an orientation …; print rules do not count — nobody tabs through paper) or only in a state (:hover, " +
				":focus …). The detail names the rule (file:line:column, selector) and its condition; outside it, the link is " +
				"there without a name. Fix: give the link a name — visible text, an alt on its image, or aria-label — or hide it " +
				"wherever it is not needed. Rides along by default.",
				[
					("2.4.4 Link Purpose (In Context)", Understanding + "link-purpose-in-context.html"),
					("4.1.2 Name, Role, Value", Understanding + "name-role-value.html"),
					("W3C test rule: Link has non-empty accessible name", ActRules + "c487ae/"),
				],
				IsViolation: false, IsWcag: true, Requirement: "WCAG 2.2 · 2.4.4 Link Purpose (In Context) and 4.1.2 Name, Role, Value (Level A)"),
			["MISPLACED_ANCHOR_EMPTY_UNDETERMINED"] = new(
				"Cannot be decided — a link without a name on a page whose stylesheet is not in the snapshot although a visitor " +
				"would get it (the crawl never fetched it, or the fetch failed). That sheet may hide the link. The detail names " +
				"the file and what the crawl log says; a stylesheet that returns 404/410 is not there for anyone and does not make " +
				"a verdict undetermined. Rides along by default; a crawl that fetches the sheet decides it.",
				[
					("W3C test rule: Link has non-empty accessible name", ActRules + "c487ae/"),
				],
				IsViolation: false),
			["MISPLACED_ANCHOR_EMPTY_CLEARED"] = new(
				"Not a violation — a record, with its evidence. A link without a name that the page's stylesheets hide wherever " +
				"it is (display:none or visibility:hidden on it or an ancestor, by a rule that always applies): nobody sees it or " +
				"reaches it with Tab. The detail names the file, line, column and selector. Off in triage: it is no work, it shows " +
				"why the link was not reported, and a change in the compare shows when that stops being true.",
				[
					("W3C test rule: Link has non-empty accessible name", ActRules + "c487ae/"),
				],
				IsViolation: false),
			["ARIA_HIDDEN_FOCUSABLE"] = new(
				"Keyboard users land on something a screen reader is told isn't there: focus goes somewhere silent. An element " +
				"inside aria-hidden is in the Tab order, checked following the W3C test rule \"Element with aria-hidden has no " +
				"content in sequential focus navigation\". Checked against the page's stylesheets: an element a rule hides is listed " +
				"separately (cleared, or conditional when the rule applies only under a media query or in a state); what is reported " +
				"here is hidden by no rule read. Fix: remove aria-hidden, or take the element out of the Tab order (tabindex=\"-1\").",
				[
					("4.1.2 Name, Role, Value", Understanding + "name-role-value.html"),
					("W3C test rule: aria-hidden with focusable content", ActRules + "6cfa84/"),
				],
				IsViolation: true, IsWcag: true, Requirement: "WCAG 2.2 · 4.1.2 Name, Role, Value (Level A)"),
			["ARIA_HIDDEN_FOCUSABLE_CONDITIONAL"] = new(
				"At some screen widths or in some states, keyboard users land on something a screen reader is told isn't there. " +
				"An element inside aria-hidden is in the Tab order, except where the page's CSS hides it: only under a media query " +
				"(a screen width, an orientation …; print rules do not count — nobody tabs through paper) or only in a state (:hover, :focus, :checked …). The detail names the rule " +
				"(file:line:column, selector) and its condition; outside it, focus goes somewhere silent. Fix: remove aria-hidden, " +
				"or take the element out of the Tab order (tabindex=\"-1\") wherever it is not hidden. Rides along by default.",
				[
					("4.1.2 Name, Role, Value", Understanding + "name-role-value.html"),
					("W3C test rule: aria-hidden with focusable content", ActRules + "6cfa84/"),
				],
				IsViolation: false, IsWcag: true, Requirement: "WCAG 2.2 · 4.1.2 Name, Role, Value (Level A)"),
			["ARIA_HIDDEN_FOCUSABLE_UNDETERMINED"] = new(
				"Cannot be decided — an element inside aria-hidden that would be in the Tab order, on a page whose stylesheet is " +
				"not in the snapshot although a visitor would get it (the crawl never fetched it, or the fetch failed). That " +
				"sheet may hide the element. The detail names the file and what the crawl log says; a stylesheet that returns " +
				"404/410 is not there for anyone and does not make a verdict undetermined. Rides along by default; a crawl that " +
				"fetches the sheet decides it.",
				[
					("W3C test rule: aria-hidden with focusable content", ActRules + "6cfa84/"),
				],
				IsViolation: false),
			["ARIA_HIDDEN_FOCUSABLE_CLEARED"] = new(
				"Not a violation — a record, with its evidence. An element inside aria-hidden that would be in the Tab order by " +
				"what it is (a link, a button, a form field …), but something takes it out: tabindex=\"-1\", disabled, the hidden " +
				"or inert attribute, display:none or visibility:hidden on it or an ancestor (inline or by the page's stylesheets, " +
				"where the rule always applies), a closed <details>, a disabled <fieldset>, <template> or <noscript>. The detail " +
				"names what and where — for CSS the file, line, column and selector. Off in triage: it is no work, it shows why " +
				"the element was not reported, and a change in the compare shows when that stops being true.",
				[
					("W3C test rule: aria-hidden with focusable content", ActRules + "6cfa84/"),
				],
				IsViolation: false),
			["EMBEDDED_BOM_IN_BODY"] = new(
				"Template defect — not a standards violation. A byte order mark (U+FEFF) stands in the middle of the page instead " +
				"of at the start of a file. Likely cause: the page is assembled from several files (component markup, includes, " +
				"concatenated fragments) and one of them was saved as \"UTF-8 with BOM\"; the merge copied its BOM along, with no " +
				"cleanup before merging. The markup right after the BOM is the start of that file. Fix: save that file without BOM, " +
				"or strip BOMs from each part before merging. Effects are mostly invisible: a stray gap or text node, broken :empty " +
				"selectors, the character in copied text and screen-reader output.",
				[("W3C: The byte-order mark (BOM) in HTML", "https://www.w3.org/International/questions/qa-byte-order-mark")],
				IsViolation: false),
			["STYLED_HEADING"] = new(
				"Screen reader users don't hear this as a heading, and skip past it when they jump from heading to heading. Sighted " +
				"readers see a heading. One change in the template fixes it on every page. An element that is not a heading " +
				"(h1–h6) carries a class that makes it look like one, e.g. <div class=\"h2\">: assistive technology sees plain " +
				"text. Found by its " +
				"class: one of the classes listed as heading styles in the settings (ContentQualityHeadingClasses); the rendered " +
				"style is not checked. Checked in the delivered HTML: a role or heading added by a script at runtime is not seen. " +
				"CSS cannot redeem this: styles change the look, not what assistive technology is told — " +
				"only markup makes a heading. Not reported: real headings, elements with role=\"heading\" (with or without " +
				"aria-level; without one the level is 2), and the label of a nested list (<li><div class=\"h4\">Group</div><ul>…) — " +
				"the nesting already tells assistive technology which links belong to the label; a heading there would only add " +
				"heading navigation. Fix once in the template: make the element a heading at the level the page's outline needs — " +
				"as an h-element, or as role=\"heading\" with aria-level. The class shows the look, not the level: a question " +
				"styled h2 under an h2 section heading is an h3. If the element opens an accordion or disclosure, put the heading " +
				"around the button (<h3><button>…</button></h3>), not inside it.",
				[
					("1.3.1 Info and Relationships", Understanding + "info-and-relationships.html"),
					("Failure F2: text presentation without the appropriate markup", "https://www.w3.org/WAI/WCAG22/Techniques/failures/F2"),
					("Technique ARIA12: role=heading", "https://www.w3.org/WAI/WCAG22/Techniques/aria/ARIA12"),
				],
				IsViolation: true, IsWcag: true, Requirement: "WCAG 2.2 · 1.3.1 Info and Relationships (Level A)"),
			["BARE_TEXT_IN_CONTAINER"] = new(
				"Markup issue. Prose that has lost its paragraph structure: text beside real paragraphs or headings without a " +
				"paragraph of its own, or paragraphs separated by <br><br> instead of paragraph markup. Screen-reader paragraph " +
				"navigation skips it (WCAG 2.2 success criterion 1.3.1 Info and Relationships). Text directly in a container is " +
				"not reported on its own: HTML allows it, and labels, widget messages and single titles are fine there. CSS " +
				"cannot redeem this: spacing that makes text look like paragraphs gives no paragraph to screen-reader navigation.",
				[("1.3.1 Info and Relationships", Understanding + "info-and-relationships.html")],
				IsViolation: false),
			["ARIA_REFERENCE_BROKEN"] = new(
				"Markup issue. aria-labelledby or aria-describedby names an id that is not on the page, so " +
				"the browser ignores the reference and the intended name or description is not announced. Ids are looked up in the " +
				"whole downloaded page, including parts the boilerplate prune removed.",
				[
					("WAI-ARIA: aria-labelledby", "https://www.w3.org/TR/wai-aria-1.2/#aria-labelledby"),
					("WAI-ARIA: aria-describedby", "https://www.w3.org/TR/wai-aria-1.2/#aria-describedby"),
				],
				IsViolation: false),

			// ── Quotes ───────────────────────────────────────────────────────────
			["QUOTE_UNMATCHED"] = new(
				"Typography issue. An opening quotation mark has no closing mark before its block ends. " +
				"Quotes are paired within one block (paragraph, heading, list item, table cell), never across blocks, and the " +
				"page language decides which marks belong together (German „…“, English “…”, French «…», Polish „…”). " +
				"Apostrophes are told apart by the language rules in ContentQualityApostropheElisions. Likely cause: the closing " +
				"mark was deleted or never typed, or a quotation runs over several paragraphs. Fix: add the closing mark.",
				QuoteLinks,
				IsViolation: false),
			["QUOTE_WRONG_OPEN"] = new(
				"Typography issue. A closing quotation mark stands where no quotation was opened in this " +
				"block. Likely cause: the opening mark was typed as the closing glyph (e.g. “ at the start of a German quote " +
				"that should open with „), or the opening mark is missing. Fix: correct or add the opening mark.",
				QuoteLinks,
				IsViolation: false),
			["QUOTE_WRONG_CLOSE"] = new(
				"Typography issue. A quotation opened in one convention is closed in another, e.g. „…” " +
				"on a German page (German closes „ with “; Polish, Romanian, Bulgarian and Czech close it with ”). Which pair " +
				"is right follows the page language (<html lang>). Likely cause: text pasted from another language or typed " +
				"with an autocorrect set to another language. Fix: use the closing mark of the page language.",
				QuoteLinks,
				IsViolation: false),
			["QUOTE_WRONG_APOSTROPHE"] = new(
				"Typography issue. An apostrophe typed with a glyph that is not the apostrophe: ‘ (U+2018, " +
				"left single quotation mark), ´ (the acute accent) or ` (the backtick) where ’ belongs, e.g. geht´s, lauf‘ los. " +
				"Reported only where the glyph stands in an apostrophe's place: between letters, before or after a common " +
				"elision, or straight after a word with no opening quote waiting for it. A ‘ that closes a German ‚…‘ is a " +
				"quotation mark and is not reported. Likely cause: the accent key next to backspace, or autocorrect set to " +
				"German quotation marks, which turns ' typed after a letter into ‘, the German closing single quote. Fix: use ’ (U+2019).",
				QuoteLinks,
				IsViolation: false),
			["QUOTE_MIXED_KIND"] = new(
				"Typography issue. A typographic quotation mark is paired with a straight one (\" U+0022), " +
				"e.g. „…\" or \"…“. A consistent straight pair \"…\" is not reported. Likely cause: one mark typed by hand, the " +
				"other set by autocorrect, or an edit in a plain-text field. Fix: use the typographic pair of the page language " +
				"for both marks.",
				QuoteLinks,
				IsViolation: false),
			["QUOTE_SYSTEM_MIX"] = new(
				"Typography issue. One block uses more than one quotation convention for its outer quotes, " +
				"e.g. „…“ next to “…”. Single and angle quotes (‚…‘, ‘…’, ‹…›) are left out. Guillemets «…» count as French. " +
				"Likely cause: text assembled from sources in different languages, or pasted from another site. Fix: use one " +
				"convention per page, the one of the page language.",
				QuoteLinks,
				IsViolation: false),

			// ── Characters ───────────────────────────────────────────────────────
			["CONTROL_CHARS_IN_CONTENT"] = new(
				"Invisible characters in the page title, a meta tag or paragraph text (one finding per element, naming the " +
				"first such character). What it means depends on the character: bidi controls can change what a text shows " +
				"and are always worth a look; zero-width spaces, word joiners and line separators are usually paste debris " +
				"from Word, PDF or chat tools; C1 controls point at a broken encoding conversion. Soft hyphens are reported " +
				"only in title and meta, where nothing wraps. Line breaks and tabs are reported only inside the title and meta " +
				"values, where they show as a plain space; in text they are ordinary whitespace. In triage each finding is " +
				"explained by its character.",
				[
					("Unicode: characters not suitable for markup (UTR #20)", "https://www.unicode.org/reports/tr20/"),
					("W3C: Unicode controls vs markup for bidi", "https://www.w3.org/International/questions/qa-bidi-unicode-controls"),
				],
				IsViolation: false),
			["LIGATURE"] = new(
				"Typography issue. A ligature character (U+FB00–FB06: ﬀ ﬁ ﬂ ﬃ ﬄ ﬅ ﬆ) stands in the text " +
				"instead of separate letters. These are compatibility characters for old encodings; Unicode advises against " +
				"them in text, because fonts form ligatures themselves. In text they break search (\"first\" does not find " +
				"\"ﬁrst\"), spell-checking, hyphenation and sorting, and screen readers may stumble over them. Likely cause: text " +
				"copied out of a PDF. Fix: replace them with the plain letters (Unicode NFKC normalisation does it).",
				[
					("Unicode FAQ: Ligatures, digraphs and presentation forms", "https://www.unicode.org/faq/ligature_digraph.html"),
					("Unicode chart: Alphabetic Presentation Forms (U+FB00)", "https://www.unicode.org/charts/PDF/UFB00.pdf"),
				],
				IsViolation: false),
			["DECOMPOSED_TEXT_IN_CONTENT"] = new(
				"Encoding issue. Letters are stored decomposed (Unicode NFD): a base letter followed by a " +
				"separate combining mark, e.g. a + U+0308 instead of ä, where a single precomposed character exists. It looks " +
				"the same, but search, sorting, spell-checking and every string comparison treat it as different text. The W3C " +
				"recommends the composed form (NFC) for web content. Likely cause: text pasted from macOS file names or from " +
				"some PDF and office tools. Fix: normalise to NFC, in the CMS on save and once for existing content.",
				NormalisationLinks,
				IsViolation: false),
			["MIXED_NORMALIZATION_IN_CONTENT"] = new(
				"Encoding issue. The same word appears on one page in two byte forms, composed (NFC) and " +
				"decomposed (NFD). Readers see one word; the browser's find, site search and spell-checking see two, so a search " +
				"finds only some of them. Likely cause: parts of the page pasted from different sources. Fix: normalise the " +
				"content to NFC.",
				NormalisationLinks,
				IsViolation: false),

			// ── Words & spacing ──────────────────────────────────────────────────
			["WORD_COLLISION"] = new(
				"Content issue. Formatted text (b, i, em, strong, span, …) touches the text next to it " +
				"without a space, where a lowercase letter meets an uppercase one, so two words run together on screen, in " +
				"search and in screen-reader output (\"orderNow\"). Emphasis inside a word (lowercase on both sides) is not " +
				"reported. CSS cannot redeem this: a gap made by margin or padding is no space in the text — screen readers, " +
				"search engines, copy and paste and reader views get the words joined. Likely cause: the space was left outside " +
				"or deleted while formatting. Fix: add the missing space.",
				[],
				IsViolation: false),
			["WORD_DOUBLED"] = new(
				"Text issue. The same word twice in a row (\"Haus Haus\", \"the the\"), usually left " +
				"over from an edit; screen readers read it twice as well. Only spaces and line breaks between the two count — " +
				"punctuation means it is meant (\"Haus, Haus\") — and inline formatting does not hide it. Doublings that are " +
				"grammar (\"die die\", \"had had\") are listed per language in the settings and not reported; in German a " +
				"capital followed by the same word in small letters (\"Leben leben\") is a noun and its verb and not reported " +
				"either, except at the start of a sentence. Fix: delete one of the two.",
				[],
				IsViolation: false),
			["PUNCTUATION_WEAK"] = new(
				"Style note, not an error. A term stands on its own line and the next line starts " +
				"with the same word (\"Gartenhaus<br> Gartenhaus heißt …\"): the text reads correctly, but nothing marks the first " +
				"line as the lead-in, and read aloud the word comes twice in a row. A colon after the term (\"Gartenhaus:\") " +
				"makes the term and its explanation clear; marking it up as a term (<strong>, or <dt>/<dd> in a list of terms) " +
				"does too. Rides along by default: it goes into the ticket of a page listed for something else.",
				[],
				IsViolation: false),
			["SPACE_AFTER_HYPHEN"] = new(
				"Content issue. A word ends in a hyphen and a space follows (\"Xyzzy- Haus\"). The spell check cannot " +
				"see it, because \"Xyzzy-\" is a correct suspended head on its own. The finding says which it is when it can tell: " +
				"\"/ or a joining word missing\" — the head takes the end of the next word and makes a word (\"Stadt- Landhaus\": " +
				"\"Stadthaus\"); fix: \"Stadt-/Landhaus\", \"Stadt- und Landhaus\" or \"Stadt- oder Landhaus\". \"Stray space\" — a number " +
				"before the hyphen; fix: delete the space. \"Space at the slash\" — a slash after the head takes no spaces; fix: " +
				"\"Xyzzy-/Haus\". Otherwise (or without a dictionary for the page's language) it is one of the first two: a compound " +
				"split by a stray space (\"Xyzzy-Haus\") or a joiner left out. The excerpt shows the page's HTML around the spot. " +
				"Correct and not reported: a suspended head before a joining word or punctuation (\"Haus- und Gartenpflege\", " +
				"\"Haus-, Hof- und …\", \"Ein-/Ausgang\"); the joining words per language are in the settings. A name written this way on " +
				"purpose (\"Xyzzy- und Hausverband\") can be kept in the user or site dictionary, exactly as written — in Triage by a " +
				"click on the finding; it then counts in every language.",
				[],
				IsViolation: false),
			["SPACE_BEFORE_HYPHEN"] = new(
				"Content issue. A word starts with a hyphen after a space, and a word stands before it (\"Xyzzy -Haus\"). " +
				"The finding says which it is when it can tell: \"/ or a joining word missing\" — the start of the word before and the " +
				"tail make a word (\"Hauseingänge -ausgänge\": \"Hausausgänge\"); fix: \"Hauseingänge/-ausgänge\" or \"Hauseingänge und " +
				"-ausgänge\". \"Space at the slash\" — a slash before the tail takes no spaces; fix: \"Hauseingänge/-ausgänge\". Otherwise " +
				"one of three: a split compound — delete the space (\"Xyzzy-Haus\"); a joiner left out — add it; or a dash missing its " +
				"space after it — add the space, or use a real dash (\"Xyzzy – Haus\"). The excerpt shows the page's HTML around the spot. " +
				"Correct and not reported: a suspended tail after a joining word or punctuation (\"Textilgroß- und -einzelhandel\").",
				[],
				IsViolation: false),
			["WORD_SPLIT_BY_FORMATTING"] = new(
				"Template defect — markup issue. The first letters of several words in a row are each formatted on their own " +
				"(e.g. a bold initial in each word of a term) to spell out an abbreviation. Screen readers may announce the " +
				"single letters apart from their words, and search and spell-checking see word fragments. A lone drop cap is " +
				"not reported (three or more per block: WordSplitByFormattingThreshold). CSS cannot redeem this: the words are " +
				"split in the markup, and styling only changes how the pieces look. Fix: write the term plainly and mark " +
				"the abbreviation with <abbr title=\"…\">.",
				[
					("Technique H28: abbreviations with the abbr element", "https://www.w3.org/WAI/WCAG22/Techniques/html/H28"),
					("HTML: the abbr element", "https://html.spec.whatwg.org/multipage/text-level-semantics.html#the-abbr-element"),
				],
				IsViolation: false),
			["LINE_BREAK_GLUE_INERT"] = new(
				"Typography issue. A no-break character (no-break space U+00A0, narrow no-break space " +
				"U+202F, figure space U+2007 or non-breaking hyphen U+2011) stands directly after a dash, hyphen, soft hyphen or " +
				"bar. There it holds nothing together: by the Unicode line-breaking rules (UAX #14, rule LB12a) a line may still " +
				"break right before it, so the line wraps there anyway and the no-break space then shows as a gap at the start " +
				"of the next line. Typical source: \"–&nbsp;\" typed to keep a dash with the next word. Fix: delete the no-break " +
				"space after the dash; one before the dash is what keeps the dash with the word before it (\"2010&nbsp;– 2020\"). " +
				"If the phrase must never wrap, that is a styling job (white-space: nowrap) for the template.",
				LineBreakInertLinks,
				IsViolation: false),
			["LINE_BREAK_GLUE_DOUBLE_SPACE"] = new(
				"Typography issue. A no-break character stands next to an ordinary space, so the text " +
				"shows a double space, and the no-break holds nothing together: a line can break at the ordinary space beside " +
				"it (UAX #14, rules LB12a and LB18). Typical source: &nbsp; typed next to a space, or added by an editor when " +
				"a space was typed twice. Fix: delete one of the two — keep the plain space, or keep only the no-break space " +
				"where two words must stay on one line, such as a number and its unit (\"10&nbsp;kg\"). A run of several " +
				"spaces (\"&nbsp; &nbsp; \" is how an editor keeps spaces typed in a row) is one finding: spacing or " +
				"indentation made with spaces, which shifts with font and width — use a list, a table or the layout instead.",
				LineBreakDoubleSpaceLinks,
				IsViolation: false),

			// ── Anchors ──────────────────────────────────────────────────────────
			["SPLIT_WORD_ANCHOR"] = new(
				"Markup issue. A link ends in the middle of a word: letters or digits follow </a> directly (e.g. " +
				"<a>Download</a>s). The link's text, which screen readers announce and link lists show, is a word fragment, and " +
				"the stray end of the word is not part of the link (WCAG 2.2 success criterion 2.4.4 Link Purpose). CSS cannot " +
				"redeem this: the link's text is the fragment, however it looks. Fix: move " +
				"the closing </a> to the end of the word.",
				[("2.4.4 Link Purpose (In Context)", Understanding + "link-purpose-in-context.html")],
				IsViolation: false),
			["ADJACENT_ANCHOR"] = new(
				"Markup issue. Two links follow each other with nothing in between (</a><a>). On screen their texts run together " +
				"into one, and it is not visible where one link ends and the next begins; screen readers announce them back to " +
				"back. CSS cannot redeem this: a gap made by margin or padding is no space in the text — search engines, copy " +
				"and paste and reader views get the link texts run together. Fix: separate them with a space or a separator, " +
				"or join them into one link when they lead to the same " +
				"place. Off unless AnchorDetection.DetectAdjacent is on.",
				[
					("2.4.4 Link Purpose (In Context)", Understanding + "link-purpose-in-context.html"),
					("Technique H2: combining adjacent links for the same resource", "https://www.w3.org/WAI/WCAG22/Techniques/html/H2"),
				],
				IsViolation: false),

			// ── Markup ───────────────────────────────────────────────────────────
			["MALFORMED_HTML"] = new(
				"Markup issue. The HTML parser had to repair the page; the detail names the " +
				"repair (e.g. TagNotClosed). Browsers repair such markup too, but not always the way the author meant, and the " +
				"repaired structure is what screen readers get — a list, heading or link may end somewhere else. WCAG 2.2 " +
				"dropped success criterion 4.1.1 Parsing, so only a changed structure or meaning counts against WCAG. Codes " +
				"in MalformedHtml.SuppressParseErrorCodes are not reported. Fix: in the template or the content, wherever the " +
				"markup comes from; the W3C validator shows the exact spot.",
				[
					("HTML: parse errors", "https://html.spec.whatwg.org/multipage/parsing.html#parse-errors"),
					("W3C Nu HTML Checker", "https://validator.w3.org/nu/"),
					("4.1.1 Parsing (obsolete in WCAG 2.2)", Understanding + "parsing.html"),
				],
				IsViolation: false),
			["CONTENT_BEFORE_DOCTYPE"] = new(
				"Template defect — markup issue. Something other than whitespace or a byte order mark stands before <!DOCTYPE " +
				"html> (the context shows it). Anything before the doctype puts the browser into quirks mode, where it lays out " +
				"the page with legacy rules — boxes, tables and line heights can render differently than designed. Likely " +
				"cause: output from a server-side include or a debug print ahead of the template. Fix: remove it, so the " +
				"doctype is the first thing in the file.",
				[
					("HTML: the DOCTYPE", "https://html.spec.whatwg.org/multipage/syntax.html#the-doctype"),
					("DOM: quirks mode", "https://dom.spec.whatwg.org/#concept-document-quirks"),
				],
				IsViolation: false),

			// ── Language ─────────────────────────────────────────────────────────
			["LANGUAGE_MISMATCH"] = new(
				"Markup issue. <html lang> and <meta name=\"language\"> name different languages. The lang attribute is the one " +
				"that counts: browsers, screen readers (pronunciation) and this app's spell check follow it, and WCAG 2.2 " +
				"success criterion 3.1.1 Language of Page needs it to be right. The meta tag is not part of the HTML standard " +
				"and is ignored by browsers. Regional variants (de-DE vs de-AT) are not reported. Fix: make both name the " +
				"page's language — and check that lang is the right one, since the app cannot tell which of the two is wrong.",
				[
					("3.1.1 Language of Page", Understanding + "language-of-page.html"),
					("W3C: Declaring language in HTML", "https://www.w3.org/International/questions/qa-html-language-declarations"),
				],
				IsViolation: false),

			// ── Your patterns ────────────────────────────────────────────────────
			["UNWANTED_PATTERN"] = new(
				"Your own rule — no standard behind it. A text from one of the ContentUnwantedPatterns sets was found in the " +
				"downloaded page (placeholder text, a forbidden term, a template binding left open). The detail names the set " +
				"and the pattern; matching is plain text, case-insensitive unless the set says otherwise. Fix: as the set's " +
				"purpose says; to stop the rule, edit the set in the configuration.",
				[],
				IsViolation: false),
		};


		// ── Your patterns: security-relevant first ───────────────────────────────

		private static readonly FindingColumn[] PatternColumns =
			[new("", ColumnKind.Flag), Page, new("Detail"), new("Context", ColumnKind.QualityContext, Quality.SecurityPatterns.Type)];

		/// <summary>Pattern rows with a leading mark column; security-relevant ones first, in their order.</summary>
		private static List<FindingRow> MarkSecurity(IEnumerable<FindingRow> rows) =>
			rows.Select(r => (Security: Quality.SecurityPatterns.IsSecurity(Quality.SecurityPatterns.Type, r.Cells[1]), Row: r))
				.OrderByDescending(x => x.Security)
				.Select(x => new FindingRow([x.Security ? "Security" : string.Empty, .. x.Row.Cells], x.Row.LocalFile))
				.ToList();

		// ── Adjacent anchors: one row per page ───────────────────────────────────

		// "[1247] „And“ + „“" — the detector's source position, for the order on the page.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex LeadingPositionRegex = new(@"^\[(\d+)\]\s*", RegexOptions.Compiled);

		private static Regex LeadingPosition() => LeadingPositionRegex;

		/// <summary>
		/// One row per page: the junctions in source order,
		/// renumbered [01], [02] … (a single one without a number), and each distinct context once.
		/// </summary>
		internal static List<FindingRow> AdjacentByPage(List<FindingRow> rows) =>
			rows.GroupBy(r => r.Cells[0], StringComparer.OrdinalIgnoreCase)
				.Select(page =>
				{
					var ordered = page
						.Select(r => (Row: r, Position: LeadingPosition().Match(r.Cells[1]) is { Success: true } m ? int.Parse(m.Groups[1].Value) : int.MaxValue))
						.OrderBy(x => x.Position)
						.Select(x => x.Row)
						.ToList();
					var details = ordered.Select((r, i) =>
					{
						var plain = LeadingPosition().Replace(r.Cells[1], string.Empty);
						return ordered.Count == 1 ? plain : $"[{i + 1:00}] {plain}";
					});
					var contexts = ordered.Select(r => r.Cells[2]).Distinct(StringComparer.Ordinal);
					return new FindingRow([page.Key, string.Join("\n", details), string.Join("\n", contexts)], ordered[0].LocalFile);
				})
				.ToList();

		// ── Embedded BOM: one row per merged-in file ─────────────────────────────

		private static readonly FindingColumn[] BomColumns =
		[
			new("File starts with", ColumnKind.Text), new("Pages", ColumnKind.Number), new("On pages", ColumnKind.Pages),
			new("Context", ColumnKind.QualityContext, "EMBEDDED_BOM_IN_BODY"),
		];

		private const string BomMarker = "[BOM U+FEFF]";

		/// <summary>The start of the file that carries the BOM: the markup right after it in the context.</summary>
		internal static string MergedFileStart(string context)
		{
			int at = context.IndexOf(BomMarker, StringComparison.Ordinal);
			if (at < 0)
			{
				return string.Empty;
			}

			var after = context[(at + BomMarker.Length)..].TrimEnd('…').Trim();
			return after.Length > 60 ? after[..60] + "…" : after;
		}

		/// <summary>
		/// One row per file that carries a BOM, recognised by how it starts (the markup after the BOM):
		/// the pages it lands on (the first ten listed), and one context as the example.
		/// </summary>
		internal static List<FindingRow> BomByMergedFile(List<FindingRow> rows) =>
			rows.GroupBy(r => MergedFileStart(r.Cells.Length > 2 ? r.Cells[2] : string.Empty), StringComparer.Ordinal)
				.OrderByDescending(g => g.Count())
				.Select(g =>
				{
					var pages = g.Select(r => r.Cells[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
					var listed = string.Join("\n", pages.Take(10)) + (pages.Count > 10 ? $"\n… and {pages.Count - 10} more" : string.Empty);
					var first = g.First();
					return new FindingRow(
						[g.Key.Length > 0 ? g.Key : "(not recognised)", pages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), listed, first.Cells[2]],
						first.LocalFile);
				})
				.ToList();

		// ── Styled headings: one row per pattern ─────────────────────────────────

		private static readonly FindingColumn[] StyledHeadingColumns =
		[
			new("Pattern", ColumnKind.Text), new("Pages", ColumnKind.Number), new("On pages", ColumnKind.Pages),
			new("Example", ColumnKind.QualityContext, "STYLED_HEADING"),
		];

		/// <summary>
		/// One row per pattern (tag + heading class): the pages it is on (the first ten listed) and
		/// the first page's headings as the example, one per line (the first five). Most pages first.
		/// </summary>
		private static string HeadingExample(string context)
		{
			var lines = Quality.StyledHeadings.Lines(context);
			return string.Join("\n", lines.Take(5)) + (lines.Count > 5 ? $"\n… and {lines.Count - 5} more" : string.Empty);
		}

		internal static List<FindingRow> StyledHeadingsByPattern(List<FindingRow> rows) =>
			rows.GroupBy(r => r.Cells[1], StringComparer.Ordinal)
				.Select(g =>
				{
					var pages = g.Select(r => r.Cells[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
					var listed = string.Join("\n", pages.Take(10)) + (pages.Count > 10 ? $"\n… and {pages.Count - 10} more" : string.Empty);
					var first = g.First();
					return (Pages: pages.Count, Row: new FindingRow(
						[g.Key, pages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), listed, HeadingExample(first.Cells[2])],
						first.LocalFile));
				})
				.OrderByDescending(x => x.Pages)
				.ThenBy(x => x.Row.Cells[0], StringComparer.Ordinal)
				.Select(x => x.Row)
				.ToList();

		// ── Invisible characters, split by likely cause ──────────────────────────

		/// <summary>The likely causes of invisible characters in template markup, in the order shown.</summary>
		public enum InvisibleCause { Bidi, DirectionMarks, PasteDebris, Controls, Joiners, SoftHyphens, Other }

		// "ZWSP (U+200B) found at …" — the detector names the character with its code point.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex CodePointRegex = new(@"U\+([0-9A-Fa-f]{4})", RegexOptions.Compiled);

		private static Regex CodePoint() => CodePointRegex;

		/// <summary>The cause a code point most likely comes from.</summary>
		public static InvisibleCause CauseOf(int cp) => cp switch
		{
			>= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069 => InvisibleCause.Bidi,
			0x200E or 0x200F or 0x061C => InvisibleCause.DirectionMarks,
			0x200B or 0x2060 or 0xFEFF or 0x2028 or 0x2029 or >= 0x2061 and <= 0x2064 => InvisibleCause.PasteDebris,
			< 0x20 or 0x7F or >= 0x80 and <= 0x9F => InvisibleCause.Controls,
			0x200C or 0x200D => InvisibleCause.Joiners,
			0x00AD => InvisibleCause.SoftHyphens,
			_ => InvisibleCause.Other,
		};

		/// <summary>The cause of a finding, read from the code point in its detail; Other when there is none.</summary>
		public static InvisibleCause CauseOfDetail(string detail)
		{
			var m = CodePoint().Match(detail);
			return m.Success ? CauseOf(Convert.ToInt32(m.Groups[1].Value, 16)) : InvisibleCause.Other;
		}

		private const string W3cI18n = "https://www.w3.org/International/";

		/// <summary>Tab title and explanation per cause. The first line says what triage should do with it.</summary>
		internal static readonly IReadOnlyDictionary<InvisibleCause, (string Title, FindingBasis Basis)> InvisibleCauses =
			new Dictionary<InvisibleCause, (string, FindingBasis)>
			{
				[InvisibleCause.Bidi] = ("Bidi controls", new(
					"Always signal — check every one. Embeddings and overrides (U+202A–202E) and isolates (U+2066–2069) change the " +
					"order in which text is displayed: an override can make text read differently from what is stored, which is how " +
					"spoofed links and file names are built. They are almost never typed on purpose; right-to-left markup uses the dir " +
					"attribute or <bdi> instead.",
					[
						("W3C: Inline markup and bidirectional text in HTML", W3cI18n + "articles/inline-bidi-markup/"),
						("W3C: Unicode controls vs markup for bidi", W3cI18n + "questions/qa-bidi-unicode-controls"),
						("Unicode Bidirectional Algorithm (UAX #9)", "https://www.unicode.org/reports/tr9/"),
					],
					IsViolation: true)),
				[InvisibleCause.DirectionMarks] = ("Direction marks", new(
					"Usually intended. LRM (U+200E), RLM (U+200F) and ALM (U+061C) are zero-width marks that set the direction of " +
					"punctuation and numbers next to right-to-left text — common on Arabic, Hebrew and mixed-language pages. In " +
					"left-to-right-only content they are paste debris.",
					[("W3C: Unicode controls vs markup for bidi", W3cI18n + "questions/qa-bidi-unicode-controls")],
					IsViolation: false)),
				[InvisibleCause.PasteDebris] = ("Paste debris", new(
					"Systemic — one central fix, not page by page. Zero-width spaces (U+200B), word joiners (U+2060), zero-width " +
					"no-break spaces (U+FEFF), line and paragraph separators (U+2028, U+2029) and invisible math operators " +
					"(U+2061–2064) arrive with copy and paste from Word, PDF, web pages or chat tools. Fix: a paste sanitizer in the " +
					"CMS editor for new content, and a one-time database cleanup for existing content. Report once, with these pages " +
					"as evidence.",
					[("Unicode: characters not suitable for markup (UTR #20)", "https://www.unicode.org/reports/tr20/")],
					IsViolation: false)),
				[InvisibleCause.Controls] = ("Control characters", new(
					"Likely corruption from an import or conversion. C1 controls (U+0080–009F) usually mean Windows-1252 text was " +
					"read as ISO-8859-1 somewhere along the way: its curly quotes and dashes turn into invisible controls. C0 controls " +
					"and DEL come from pasted binary or terminal text. Line breaks and tabs are not counted here — in markup they are " +
					"ordinary whitespace. Find the step that produced them.",
					[("Unicode: characters not suitable for markup (UTR #20)", "https://www.unicode.org/reports/tr20/")],
					IsViolation: false)),
				[InvisibleCause.Joiners] = ("Joiners", new(
					"Often intended. The zero-width joiner (U+200D) combines emoji into one symbol (families, flags, skin tones) and " +
					"shapes some scripts; the zero-width non-joiner (U+200C) prevents joining in Persian and suppresses ligatures in " +
					"compound words. Outside those uses they are paste debris.",
					[("Unicode: emoji ZWJ sequences", "https://www.unicode.org/emoji/charts/emoji-zwj-sequences.html")],
					IsViolation: false)),
				[InvisibleCause.SoftHyphens] = ("Soft hyphens", new(
					"Not reported. A soft hyphen (U+00AD) is a hyphenation hint, invisible unless the browser breaks the word at " +
					"that point; editors and CMS tools insert them on purpose for long words, in prose and template text alike. " +
					"Only in the page title and meta tags, where nothing wraps, is it a finding (Characters › Control characters).",
					[],
					IsViolation: false)),
				[InvisibleCause.Other] = ("Other", new(
					"Invisible characters not assigned to a cause yet.",
					[],
					IsViolation: false)),
			};

		/// <summary>
		/// The invisible-character findings as one tab per likely cause, each with its explanation.
		/// Causes without findings still show (dimmed), so the split is the same on every snapshot.
		/// </summary>
		private static FindingNode InvisibleByCause(string label, List<FindingRow> rows, bool written, string? problem, FindingColumn[] columns)
		{
			// Rows: page, detail, context — the detail names the code point.
			var byCause = rows.ToLookup(r => CauseOfDetail(r.Cells.Length > 1 ? r.Cells[1] : string.Empty));
			var tabs = new List<FindingNode>();
			foreach (var cause in Enum.GetValues<InvisibleCause>())
			{
				var (title, basis) = InvisibleCauses[cause];
				var matching = byCause[cause].ToList();
				if (cause == InvisibleCause.Other && matching.Count == 0)
				{
					continue;
				}

				tabs.Add(FindingNode.Leaf(title, "INVISIBLE_CHAR_IN_BODY · " + title.ToLowerInvariant(),
					written ? new FindingTable(columns, matching, problem) : null, basis));
			}

			return FindingNode.Group(label, tabs, note: "Split by likely cause. Each tab says what the characters are, how they get there, and what triage should do with them.");
		}

		/// <summary>
		/// The Results leaf and triage switch of a finding: its base type, except content before the
		/// doctype, which the detector writes as MALFORMED_HTML with the detail CONTENT_BEFORE_DOCTYPE
		/// but which has its own leaf and switch.
		/// </summary>
		internal static string LeafType(string type, string detail)
		{
			var baseType = BaseType(type);
			return baseType == "MALFORMED_HTML"
				&& (detail.StartsWith(ContentBeforeDoctype, StringComparison.Ordinal) || type.EndsWith(":" + ContentBeforeDoctype, StringComparison.Ordinal))
				? ContentBeforeDoctype
				: baseType;
		}

		private const string ContentBeforeDoctype = "CONTENT_BEFORE_DOCTYPE";

		/// <summary>"MALFORMED_HTML:12" → "MALFORMED_HTML".</summary>
		private static string BaseType(string type)
		{
			int colon = type.IndexOf(':');
			return (colon < 0 ? type : type[..colon]).Trim();
		}

		// ── Spelling ─────────────────────────────────────────────────────────────

		private static FindingNode SpellingTab(Snapshot s)
		{
			var unique = s.Table("11", path => FindingFiles.Values(path).Select(v => SplitWord(v[0])),
				[new("Word"), new("Languages"), new("Valid in")]);

			var byPage = s.Table("12", path => FindingFiles.Lines(path).Select(line =>
			{
				int bar = line.IndexOf('|');
				var url = bar < 0 ? string.Empty : line[..bar];
				var rest = bar < 0 ? line : line[(bar + 1)..];
				var m = WordLangsMeta().Match(rest);
				return m.Success
					? new[] { url, m.Groups["word"].Value, m.Groups["langs"].Value, m.Groups["meta"].Value }
					: [url, rest, string.Empty, string.Empty];
			}), [Page, new("Word"), new("Languages"), new("Meta field")], urlColumn: 0);

			var located = s.Table("13", path => Located(path), [new("Word"), new("Languages"), Page, new("Source"), new("Excerpt", ColumnKind.Text)], urlColumn: 2);

			var orphans = s.Table("15", path => Sections(path), [new("Section"), new("Word")]);

			return FindingNode.Group("Spelling",
			[
				FindingNode.Leaf("Unique misspellings", "11-spell-errors-unique.log", unique),
				FindingNode.Leaf("By page", "12-spell-errors-sources.log", byPage),
				FindingNode.Leaf("Exact location", "13-spell-errors-source-location.log", located),
				FindingNode.Leaf("Dictionary orphans", "15-user-dictionary-orphan-words.log", orphans),
			], headline: unique?.Rows.Count ?? 0);
		}

		private static string[] SplitWord(string value)
		{
			var (line, validIn) = SpellCheck.ParallelStoreWriter.SplitValidIn(value);
			var valid = string.Join(", ", validIn);
			var m = WordLangs().Match(line);
			return m.Success ? [m.Groups["word"].Value, m.Groups["langs"].Value, valid] : [line, string.Empty, valid];
		}

		/// <summary>Log 13: a "word (langs) @ url" header, then one indented line per occurrence.</summary>
		private static IEnumerable<string[]> Located(string path)
		{
			string word = string.Empty, langs = string.Empty, url = string.Empty;
			foreach (var line in FindingFiles.Lines(path))
			{
				if (!line.StartsWith(' '))
				{
					var m = LocatedHeader().Match(line);
					(word, langs, url) = m.Success
						? (m.Groups["word"].Value, m.Groups["langs"].Value, m.Groups["url"].Value.Trim())
						: (line.Trim(), string.Empty, string.Empty);
					continue;
				}

				var body = line.Trim();
				int gap = body.IndexOf("  ", StringComparison.Ordinal);
				var source = gap < 0 ? body : body[..gap];
				var excerpt = gap < 0 ? string.Empty : body[(gap + 2)..].Trim();
				yield return [word, langs, url, source, excerpt];
			}
		}

		/// <summary>Log 15: sections that start with a "// heading" line.</summary>
		private static IEnumerable<string[]> Sections(string path)
		{
			string section = string.Empty;
			foreach (var line in FindingFiles.Lines(path))
			{
				var trimmed = line.Trim();
				if (trimmed.StartsWith("//", StringComparison.Ordinal))
				{
					section = trimmed.TrimStart('/', ' ');
					continue;
				}

				yield return [section, trimmed];
			}
		}

		// ── Scripts ──────────────────────────────────────────────────────────────

		private static FindingNode ScriptsTab(Snapshot s)
		{
			var inline = s.Table("29", path => FindingFiles.Tabbed(path, 3), [new("Word"), Page, Context], urlColumn: 1);
			var files = s.Table("31", path => FindingFiles.Tabbed(path, 3), [new("Word"), new("File"), Context]);
			var unique = s.Table("32", path => FindingFiles.Values(path), [new("Word")]);
			var suggestions = s.Table("27", path => Suggestions(path), [new("Tier"), new("Pages", ColumnKind.Number), new("Literal"), Context]);

			return FindingNode.Group("Scripts",
			[
				FindingNode.Leaf("Inline scripts", "29-bulk-script-findings.log", inline),
				FindingNode.Leaf("JS files", "31-js-files-spell-check-trimmed.log (full list: log 30)", files),
				FindingNode.Leaf("JS unique words", "32-js-files-spell-check-unique.log", unique),
				FindingNode.Leaf("Filter suggestions", "27-spellcheck-suppression-suggestions.log", suggestions),
			], headline: (inline?.Rows.Count ?? 0) + (files?.Rows.Count ?? 0));
		}

		/// <summary>Log 27: the SUGGEST and REVIEW sections; the paste blocks are left to the file.</summary>
		private static IEnumerable<string[]> Suggestions(string path)
		{
			string? tier = null;
			foreach (var line in FindingFiles.Lines(path))
			{
				if (line.StartsWith("## ", StringComparison.Ordinal))
				{
					tier = line.StartsWith("## SUGGEST", StringComparison.Ordinal) ? "Suggest"
						: line.StartsWith("## REVIEW", StringComparison.Ordinal) ? "Review"
						: null;
					continue;
				}

				if (tier is null)
				{
					continue;
				}

				var m = SuggestionRow().Match(line);
				if (m.Success)
				{
					yield return [tier, m.Groups["spread"].Value, m.Groups["literal"].Value, m.Groups["context"].Value.Trim()];
				}
			}
		}

		// ── Links ────────────────────────────────────────────────────────────────

		private static FindingNode LinksTab(Snapshot s)
		{
			var answers = s.Table("06", path => FindingFiles.Values(path), [new("URL", ColumnKind.Url)]);
			var broken = s.Table("07", path => ReachFirst(CollapsedReferences(FindingFiles.Csv(path, 7, header: true))),
				[new("Broken address", ColumnKind.Url), new("Reach"), new("Kind"), new("Referenced from", ColumnKind.Url, IsPage: true), new("As written", ColumnKind.Text),
				 new("When requested"), new("Evidence", ColumnKind.Text)], urlColumn: 3);
			int? addresses = broken is null ? null : BrokenAddresses(broken);
			var redirects = s.Table("03", path => FindingFiles.Delimited(path, " | ", 3, header: false),
				[new("URL", ColumnKind.Url), new("Status", ColumnKind.Status), new("Chain", ColumnKind.Chain)]);
			var selfLinks = s.Csv("09", 4, [new("File"), Page, new("Link", ColumnKind.Url), Context], fileColumn: 0, hide: [0]);
			// The targets are text, not links: the app does not open another site for the operator by a slip of the mouse.
			var forwarding = s.Csv("40", 6,
				[Page, new("Link as written", ColumnKind.Text), new("Parameter"), new("Target", ColumnKind.Text), new("Target host"), new("Scope")]);

			return FindingNode.Group("Links",
			[
				FindingNode.Leaf("Broken links", "07-404-sources (CSV) · one row per reference; the tab counts addresses", broken, BrokenLinksBasis, addresses),
				FindingNode.Leaf("404 answers", "06-404.log", answers),
				FindingNode.Leaf("Redirect chains", "03-redirect-analysis.log", redirects),
				FindingNode.Leaf("Self-links", "09-self-link-analysis (CSV)", selfLinks, SelfLinkBasis),
				FindingNode.Leaf("Forwarding links", "40-forwarding-links (CSV) · links handing the server an address to forward to; external first",
					forwarding, ForwardingExternalBasis),
			], headline: addresses ?? 0);
		}

		/// <summary>The card of the pages heavier than the template (log 21); its first row is the baseline, no page.</summary>
		internal static readonly FindingBasis AboveBaselineBasis = new(
			"Pages heavier than the template. Every page loads the template's scripts and stylesheets — the files 90% or more " +
			"of the pages use. That shared load is the baseline (first row): what every page pays. The pages listed load clearly " +
			"more than that. Why some pages load more: almost always an integration on the page — a calculator, a check, a " +
			"configurator, a map or a media player that brings its own scripts, stylesheets, fonts and images, often bundled by " +
			"its supplier, sometimes with images, fonts or program code embedded as text (Base64) inside the script. Such a " +
			"bundle is downloaded on every visit and cannot share or cache what it carries. When it is an error: the same asset " +
			"embedded more than once in one bundle, or images of hundreds of kilobytes embedded instead of loaded as files — " +
			"Base64 adds about a third to the size; log 19 lists each embedded asset with its size, so duplicates show as equal " +
			"sizes in the same file. Why it matters: everything a page loads costs time and data before it is usable, most on " +
			"phones and slow connections; search engines measure loading speed. A pointer, not a verdict: the thresholds are " +
			"rough, and an integration may need what it loads — check it with its supplier; a fix usually lands in the " +
			"supplier's next release. The tab counts the pages, not the baseline row.",
			[("web.dev: Core Web Vitals", "https://web.dev/articles/vitals")],
			IsViolation: false);

		/// <summary>References of one address of the same kind and grade above this are shown as one row.</summary>
		internal const int ReferencesShown = 5;

		/// <summary>
		/// Log 07 for the table: an address referenced the same way from many pages (a setting in
		/// a data attribute on every page) is one row naming the first and how many; the log keeps them all.
		/// </summary>
		internal static IEnumerable<string[]> CollapsedReferences(IEnumerable<string[]> rows)
		{
			foreach (var same in rows.GroupBy(r => (r[0], r[1], r[4])))
			{
				var list = same.ToList();
				if (list.Count <= ReferencesShown)
				{
					foreach (var r in list)
					{
						yield return r;
					}

					continue;
				}

				var first = list[0];
				var note = $"Referenced this way from {list.Count} places; the first is shown — all are in 07-404-sources.";
				var row = (string[])first.Clone();
				row[5] = first[5].Length > 0 ? $"{note} {first[5]}" : note;
				yield return row;
			}
		}

		/// <summary>The broken addresses of log 07, each once; one referenced only by a canonical is the canonical's finding.</summary>
		internal static int BrokenAddresses(FindingTable table) =>
			table.Rows.GroupBy(r => r.Cells[0], StringComparer.OrdinalIgnoreCase)
				.Count(g => g.Any(r => r.Cells[5] != Links.BrokenLinkOrigins.CanonicalOnly));

		/// <summary>Log 07 rows (reach last) in the table's order: the reach next to the address.</summary>
		internal static IEnumerable<string[]> ReachFirst(IEnumerable<string[]> rows) =>
			rows.Select(r => new[] { r[0], r.Length > 6 ? r[6] : string.Empty, r[1], r[2], r[3], r[4], r[5] });

		internal static readonly FindingBasis BrokenLinksBasis = new(
			"Broken — not found. Addresses of the site that answered 404 Not Found, each with every place that references it: a " +
			"link, an image, a script, a stylesheet's url(), a data attribute, inline data, a redirect or the content list. The " +
			"places are found after the crawl in the saved pages and stylesheets, so the fix can be made where the reference is. " +
			"The tab counts addresses; one that only a page's canonical points to is left out — it is fixed in the canonical " +
			"(SEO › Canonical issues). A link written without a leading '/' on a page in a folder works only from a page at the " +
			"top level: it may pass a click test (a script can take over the click) while it breaks on open in new tab, copy " +
			"link, without JavaScript and for search engines. A reference in a stylesheet says when a browser requests it: fires " +
			"on load (an element of a page matches the rule), fires on matching devices (it matches under a media or container " +
			"query on screen size, orientation or colour scheme: requested on load wherever that holds), fires in a state " +
			"(:hover, :focus, :checked — a visitor acts), fires when printed (a print media query), can fire if a script adds " +
			"the class (the rule needs a class only a script names — the snapshot cannot show that the script ever adds it), " +
			"dead reference (the rule needs a class no crawled page and no script names — unused here; before removing it, " +
			"check that the stylesheet serves nothing beyond the crawl, such as a login area), or undecided (the app cannot " +
			"judge the rule; the evidence says why). Reach says who runs into it: every page view, when opened, only " +
			"if a script acts, reach undecided, never requested, error elsewhere on site (a canonical, the content list, an " +
			"alternate version search engines read) or origin unknown (nothing saved points to it). " +
			"Scripts are not run: a class or an address a script builds at run time is not seen. An address referenced the same " +
			"way from many pages is one row; the log keeps every reference.",
			[("HTTP: 404 Not Found (RFC 9110)", "https://www.rfc-editor.org/rfc/rfc9110#name-404-not-found")],
			IsViolation: false);

		// ── SEO ──────────────────────────────────────────────────────────────────

		private static FindingNode SeoTab(Snapshot s)
		{
			var meta = s.CsvFile("08", "08-seo-data.csv", 12,
				[Page, new("Robots"), new("Title", ColumnKind.Text), new("Title length", ColumnKind.Number), new("Description", ColumnKind.Text),
				 new("Description length", ColumnKind.Number), new("Keywords"), new("Source"), new("H1 count", ColumnKind.Number), new("Search"),
				 new("Canonical", ColumnKind.Url), new("H1", ColumnKind.Text)], urlColumn: 0);
			var canonical = s.Csv("16", 4, [Page, new("Issue"), new("Canonical", ColumnKind.Url), new("Detail", ColumnKind.Text)], urlColumn: 0);

			return FindingNode.Group("SEO",
			[
				FindingNode.Leaf("Canonical issues", "16-canonical-issues (CSV)", canonical, CanonicalBasis),
				FindingNode.Leaf("Metadata", "08-seo-data.csv", meta),
			], headline: canonical?.Rows.Count ?? 0);
		}

		// ── Assets ───────────────────────────────────────────────────────────────

		private static FindingNode AssetsTab(Snapshot s)
		{
			var images = s.Csv("25", 8,
				[new("File"), Page, new("Issue"), new("Detail"), Context, new("EXIF"), new("IPTC"), new("XMP")],
				fileColumn: 0, hide: [0]);
			var imageUses = s.Csv("38", 6,
				[new("Image", ColumnKind.Url), new("Kind"), new("Referenced from", ColumnKind.Url, IsPage: true), new("As written", ColumnKind.Text),
				 new("When requested"), new("Evidence", ColumnKind.Text)], urlColumn: 2);
			var pdf = s.Csv("17", 14,
				[Page, new("Title"), new("Description"), new("Keywords"), new("Language"), new("Tags"), new("PDF/A"),
				 new("Structure"), new("Role map"), new("Outlines"), new("Alt text"), new("Form fields"), new("PDF/UA"), new("Protection")], urlColumn: 0);
			var remediation = s.Csv("18", 5, [Page, new("Priority"), new("Effort"), new("Gap"), new("Action", ColumnKind.Text)], urlColumn: 0);
			var base64 = s.Table("19", path => FindingFiles.Delimited(path, "@@@", 5, header: true),
				[Page, new("Saved as"), new("Media type"), new("Encoded bytes", ColumnKind.Number), new("Decoded bytes", ColumnKind.Number)], urlColumn: 0);
			FindingColumn[] bloatColumns =
			[
				Page, new("JS/CSS files", ColumnKind.Number), new("JS/CSS bytes", ColumnKind.Number), new("JS bytes", ColumnKind.Number),
				new("CSS bytes", ColumnKind.Number), new("Base64 assets", ColumnKind.Number), new("Base64 bytes", ColumnKind.Number),
				new("Large Base64 assets", ColumnKind.Text), new("Inlined CSS bytes", ColumnKind.Number), new("JSON blobs", ColumnKind.Number),
				new("JSON bytes", ColumnKind.Number), new("Issues", ColumnKind.Text),
			];
			var bloat = s.Table("20", path => FindingFiles.Delimited(path, "@@@", 12, header: true), bloatColumns, urlColumn: 0);
			var baseline = s.Table("21", path => FindingFiles.Delimited(path, "@@@", 12, header: true), bloatColumns, urlColumn: 0);

			return FindingNode.Group("Assets",
			[
				FindingNode.Leaf("Images", "25-asset-quality (CSV)", images, ImagesBasis),
				FindingNode.Leaf("Image references", "38-image-references (CSV) · who uses each image triage takes", imageUses, ImageReferencesBasis),
				FindingNode.Leaf("PDF remediation", "18-pdf-remediation (CSV)", remediation),
				FindingNode.Leaf("PDF quality", "17-pdf-quality (CSV)", pdf),
				FindingNode.Leaf("Base64 assets", "19-base64assets.log", base64),
				FindingNode.Leaf("Above baseline", "21-resource-bloat-above-baseline.log", baseline, AboveBaselineBasis,
					baseline is null ? null : baseline.Rows.Count(r => !r.Cells[0].Equals("BASELINE", StringComparison.Ordinal))),
				FindingNode.Leaf("Resource bloat", "20-resource-bloat.log", bloat),
			], headline: (images?.Rows.Count ?? 0) + (remediation?.Rows.Count ?? 0));
		}

		// ── Crawl ────────────────────────────────────────────────────────────────

		private static FindingNode CrawlTab(Snapshot s)
		{
			var unreachable = s.Table("05", path => FindingFiles.Values(path), [new("Path", ColumnKind.Url)]);
			var notOnline = s.Table("39", path => FindingFiles.Values(path), [new("Path", ColumnKind.Url)]);
			FindingColumn[] mismatchColumns = [new("Time"), new("URL", ColumnKind.Url), new("Requested"), new("Content-Type"), new("Sniffed"), new("Policy"), new("Action", ColumnKind.Text)];
			var html = s.Table("23", path => FindingFiles.Delimited(path, "|", 7, header: true), mismatchColumns, urlColumn: 1);
			var pdf = s.Table("24", path => FindingFiles.Delimited(path, "|", 7, header: true), mismatchColumns, urlColumn: 1);
			var image = s.Table("26", path => FindingFiles.Delimited(path, "|", 7, header: true), mismatchColumns, urlColumn: 1);
			var oversized = s.Table("34", path => FindingFiles.Delimited(path, "|", 8, header: true),
				[new("Time"), new("URL", ColumnKind.Url), new("Size", ColumnKind.Number), new("Content-Type"), new("Detected"), new("Disposition"), new("Sample"), LinkedFrom]);

			var failed = s.Table("01", path => FailedDownloads(s, path),
				[new("URL", ColumnKind.Url), new("Status", ColumnKind.Status), new("Reason", ColumnKind.Text), new("Origin"), LinkedFrom],
				urlColumn: 4);
			if (failed is null && s.File("00") is { } raw)
			{
				failed = s.Build(raw, path => FailedDownloads(s, path),
					[new("URL", ColumnKind.Url), new("Status", ColumnKind.Status), new("Reason", ColumnKind.Text), new("Origin"), LinkedFrom], 4);
			}

			var mismatches = new[] { html, pdf, image }.Sum(t => t?.Rows.Count ?? 0);
			// Where the site's names pointed, across its snapshots (information, never a finding).
			var dns = Path.GetDirectoryName(s.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is not { } siteFolder ? null
				: s.Build(Path.Combine(siteFolder, Security.DnsHistory.FileName), path => DnsRows(Path.GetDirectoryName(path)!),
				[new("Host"), new("Address"), new("Class"), new("First seen"), new("Last seen"), new("Seen as", ColumnKind.Text)], -1);
			return FindingNode.Group("Crawl",
			[
				FindingNode.Leaf("Failed downloads", "01-crawler.log (pages and assets that could not be downloaded)", failed),
				FindingNode.Leaf("Not directly discoverable", "05-not-directly-crawlable.log", unreachable),
				// Information, not a finding: the export says these are not on the site, and they were not requested.
				FindingNode.Leaf("Not online in the CMS", "39-content-list-not-online.log · rows whose online column does not hold the online value", notOnline),
				FindingNode.Leaf("HTML type", "23-contenttype-extension-mismatch.log", html),
				FindingNode.Leaf("PDF type", "24-pdf-contenttype-extension-mismatch.log", pdf),
				FindingNode.Leaf("Image type", "26-image-contenttype-extension-mismatch.log", image),
				FindingNode.Leaf("Oversized", "34-oversized-files.log", oversized),
				FindingNode.Leaf("DNS", "dns-history.json (site folder) · where the site's host names pointed, across its crawls; recorded, never refused", dns),
			], headline: (failed?.Rows.Count ?? 0) + (unreachable?.Rows.Count ?? 0) + mismatches + (oversized?.Rows.Count ?? 0));
		}

		/// <summary>
		/// Every address the site's hosts had across its crawls, from the site's DNS history: its class,
		/// when first and last seen, and whether it was looked up at crawl start or connected to (or both).
		/// Newest last seen first.
		/// </summary>
		private static IEnumerable<string[]> DnsRows(string siteFolder) =>
			(Security.DnsHistory.Read(siteFolder)?.Entries ?? []).Select(e => new[]
			{
				e.Host, e.Address, Security.DnsRecord.Describe(e.Class), e.FirstSeen, e.LastSeen, string.Join(", ", e.SeenAs),
			});

		/// <summary>
		/// The failure rows of the crawl log: "N/A" (stall, no answer, connection), an exception name,
		/// or an HTTP status of 400 and up. Successes, redirects and the oversized rows (their own view)
		/// are left out. Origin tells the main crawl from the list-05 extra download; "Linked from"
		/// comes from log 07, which knows it for 404s only.
		/// </summary>
		private static IEnumerable<string[]> FailedDownloads(Snapshot s, string path)
		{
			var listPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (s.File("05") is { } list)
			{
				foreach (var line in FindingFiles.Lines(list))
				{
					listPaths.Add(line.Trim());
				}
			}

			var linkedFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (s.File("07") is { } sources)
			{
				foreach (var c in FindingFiles.Csv(sources, 6, header: true))
				{
					if (c[2].Length > 0 && c[4] != Links.BrokenLinkOrigins.CanonicalOnly)
					{
						linkedFrom.TryAdd(c[0], c[2]);
					}
				}
			}

			foreach (var c in FindingFiles.Delimited(path, " | ", 5, header: false))
			{
				var (url, status, message) = (c[1], c[2], c[3]);
				if (!IsFailure(status) || url.Length == 0)
				{
					continue;
				}

				// "Response status code does not indicate success: 404 (Not Found)." only repeats the chip.
				var reason = message.StartsWith("Response status code does not indicate success", StringComparison.Ordinal) ? string.Empty : message;
				var origin = Uri.TryCreate(url, UriKind.Absolute, out var uri) && listPaths.Contains(uri.PathAndQuery) ? "list 05" : "main crawl";
				yield return [url, status, reason, origin, linkedFrom.GetValueOrDefault(url, string.Empty)];
			}
		}

		private static bool IsFailure(string status)
		{
			if (status.Equals("N/A", StringComparison.OrdinalIgnoreCase) || status.EndsWith("Exception", StringComparison.Ordinal))
			{
				return true;
			}

			return Display.HttpCodes.Parse(status) is { } code && code >= 400;
		}

		// ── The snapshot being read ──────────────────────────────────────────────

		/// <summary>Paths, the file ↔ URL index, and table building with a local-file link per row.</summary>
		private sealed class Snapshot
		{
			private readonly string directory;
			private readonly string downloadDirectory;
			private readonly Dictionary<string, string> urlByFile = new(StringComparer.OrdinalIgnoreCase);
			private readonly Dictionary<string, string> fileByUrl = new(StringComparer.OrdinalIgnoreCase);
			private readonly HashSet<string> downloaded = new(StringComparer.OrdinalIgnoreCase);

			/// <summary>The snapshot's folder.</summary>
			public string Folder => directory;

			public Snapshot(string directory)
			{
				this.directory = directory;
				downloadDirectory = Path.Combine(directory, "download");

				// Listed once: checking the disk for every row is slow on large snapshots.
				Guard(downloadDirectory, () =>
				{
					if (Directory.Exists(downloadDirectory))
					{
						foreach (var file in Directory.EnumerateFiles(downloadDirectory))
						{
							downloaded.Add(Path.GetFileName(file));
						}
					}
				});

				var index = Path.Combine(directory, LogFileNames.CrawlerIndex);
				if (System.IO.File.Exists(index))
				{
					Guard(index, () =>
					{
						foreach (var c in FindingFiles.Delimited(index, "|", 3, header: false))
						{
							if (c[0].Length > 0 && c[1].Length > 0)
							{
								urlByFile.TryAdd(c[0], c[1]);
								fileByUrl.TryAdd(c[1], c[0]);
							}
						}
					});
				}
			}

			/// <summary>Runs a read; returns why it failed (a locked or unreadable file), or null.</summary>
			public static string? Guard(string path, Action read)
			{
				try
				{
					read();
					return null;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					Logger.LogWarning($"Findings: could not read {path} — {ex.Message}");
					return $"Could not be read: {ex.Message}";
				}
			}

			/// <summary>The written file for an output number (the comma file of a pair), or null.</summary>
			public string? File(string number)
			{
				var format = OutputFormats.ByNumber(number);
				if (format is null)
				{
					return null;
				}

				if (format.IsCsvPair)
				{
					return FindingFiles.PairPath(directory, format.PrimaryFileName);
				}

				var path = Path.Combine(directory, format.PrimaryFileName);
				return System.IO.File.Exists(path) ? path : null;
			}

			public string UrlOf(string fileName) => urlByFile.TryGetValue(fileName, out var url) ? url : fileName;

			public string? Download(string fileName)
			{
				if (fileName.Length == 0 || fileName.IndexOfAny(['/', '\\']) >= 0)
				{
					return null;
				}

				return downloaded.Contains(fileName) ? Path.Combine(downloadDirectory, fileName) : null;
			}

			public string? DownloadOfUrl(string url) =>
				fileByUrl.TryGetValue(url.Trim(), out var file) ? Download(file) : null;

			public FindingTable? Table(string number, Func<string, IEnumerable<string[]>> read, FindingColumn[] columns, int urlColumn = -1) =>
				Build(File(number), read, columns, urlColumn, -1, []);

			public FindingTable? Csv(string number, int count, FindingColumn[] columns, int urlColumn = -1, int fileColumn = -1, int[]? hide = null) =>
				Build(File(number), path => FindingFiles.Csv(path, count, header: true), columns, urlColumn, fileColumn, hide ?? []);

			public FindingTable? CsvFile(string number, string fileName, int count, FindingColumn[] columns, int urlColumn = -1)
			{
				var path = Path.Combine(directory, fileName);
				return Build(System.IO.File.Exists(path) ? path : null, p => FindingFiles.Csv(p, count, header: true), columns, urlColumn, -1, []);
			}

			public FindingTable? Build(string path, Func<string, IEnumerable<string[]>> read, FindingColumn[] columns, int urlColumn) =>
				Build(path, read, columns, urlColumn, -1, []);

			private FindingTable? Build(string? path, Func<string, IEnumerable<string[]>> read, FindingColumn[] columns,
				int urlColumn, int fileColumn, int[] hide)
			{
				if (path is null)
				{
					return null;
				}

				var rows = new List<FindingRow>();
				var shown = hide.Length == 0 ? columns : columns.Where((_, i) => !hide.Contains(i)).ToArray();
				var problem = Guard(path, () =>
				{
					foreach (var cells in read(path))
					{
						string? local = fileColumn >= 0 ? Download(cells[fileColumn])
							: urlColumn >= 0 ? DownloadOfUrl(cells[urlColumn])
							: null;
						rows.Add(new FindingRow(hide.Length == 0 ? cells : cells.Where((_, i) => !hide.Contains(i)).ToArray(), local));
					}
				});

				return new FindingTable(shown, problem is null ? rows : [], problem);
			}
		}
	}
}
