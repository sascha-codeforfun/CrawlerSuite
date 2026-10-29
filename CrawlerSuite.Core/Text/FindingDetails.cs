using System.Text.RegularExpressions;

namespace CrawlerSuite.Core.Text
{
	/// <summary>
	/// The details the content-quality checks write into logs 10 and 22 ("p: U+00A0 NO-BREAK SPACE
	/// directly before U+0020 SPACE"), in the document's language. The logs keep the English: other
	/// steps read them again. A German document reads each detail through one pattern per format a
	/// check writes; what the pattern captures — words and characters from the page, element names,
	/// markup, counts, code points, Unicode names, names from the configuration — stays as found.
	/// A detail no pattern knows stays English, as does every detail in an English document.
	/// </summary>
	public static class FindingDetails
	{
		/// <summary>Marks the tracking part of a detail (" ¦¦ID¦¦ …", " ¦¦URL¦¦ …"): kept as written.</summary>
		private const string TrackingMark = " ¦¦";

		// Plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex LeadingPosition = new(@"^\[\d+\] ", RegexOptions.Compiled);

		/// <summary>The detail as the document language shows it: German for a detail a check writes, by pattern; anything unknown, and any language but German, unchanged.</summary>
		public static string In(DocumentLanguage language, string type, string detail)
		{
			if (language != DocumentLanguage.German || string.IsNullOrEmpty(detail))
			{
				return detail;
			}

			int cut = detail.IndexOf(TrackingMark, StringComparison.Ordinal);
			var head = cut < 0 ? detail : detail[..cut];
			var tail = cut < 0 ? string.Empty : detail[cut..];
			var prefix = LeadingPosition.Match(head).Value;
			head = head[prefix.Length..];

			if (type == "MALFORMED_HTML" && ParseErrors.TryGetValue(head, out var parse))
			{
				return prefix + parse + tail;
			}

			if (Literals.TryGetValue(head, out var literal))
			{
				return prefix + literal + tail;
			}

			foreach (var (english, german) in Rules)
			{
				var m = english.Match(head);
				if (m.Success)
				{
					return prefix + german(m) + tail;
				}
			}

			return detail;
		}

		/// <summary>How many detail formats are known: the literals, the parse-error codes and the patterns.</summary>
		internal static int Count => Literals.Count + ParseErrors.Count + Rules.Length;

		/// <summary>HtmlAgilityPack's parse-error codes (MALFORMED_HTML) and content before the doctype, the code kept.</summary>
		private static readonly Dictionary<string, string> ParseErrors = new(StringComparer.Ordinal)
		{
			["TagNotClosed"] = "Tag nicht geschlossen (TagNotClosed)",
			["TagNotOpened"] = "Schließendes Tag ohne öffnendes (TagNotOpened)",
			["CharsetMismatch"] = "Zeichensatz widersprüchlich angegeben (CharsetMismatch)",
			["EndTagNotRequired"] = "End-Tag überflüssig (EndTagNotRequired)",
			["EndTagInvalidHere"] = "End-Tag an dieser Stelle ungültig (EndTagInvalidHere)",
			["CONTENT_BEFORE_DOCTYPE"] = "Inhalt vor dem Doctype (CONTENT_BEFORE_DOCTYPE)",
		};

		/// <summary>Details written as fixed sentences.</summary>
		private static readonly Dictionary<string, string> Literals = new(StringComparer.Ordinal)
		{
			[Quality.DefectBareText.BesideParagraphsDetail] = "Text neben Absätzen oder Überschriften, ohne eigenen Absatz",
			[Quality.DefectBareText.BreakParagraphsDetail] = "Absätze durch <br><br> statt durch Absatz-Markup getrennt",
			["The first letters of several words in a row are each formatted on their own (for example, each letter bolded), " +
				"which splits the words apart for screen readers and search engines. To highlight an abbreviation, wrap the full " +
				"term in an <abbr> tag instead."] =
				"Die Anfangsbuchstaben mehrerer Wörter hintereinander sind jeweils einzeln formatiert (etwa jeder Buchstabe fett), " +
				"was die Wörter für Screenreader und Suchmaschinen zerreißt. Um eine Abkürzung hervorzuheben, stattdessen den ganzen " +
				"Begriff in ein <abbr>-Tag setzen.",
		};

		/// <summary>The quote systems by their German names; an unknown name stays as written.</summary>
		private static readonly Dictionary<string, string> QuoteSystems = new(StringComparer.Ordinal)
		{
			["German-double"] = "Deutsch doppelt",
			["Guillemet"] = "Guillemets",
			["English-double"] = "Englisch doppelt",
			["Slavic-double"] = "Slawisch doppelt",
			["Heavy"] = "Heavy",
			["German-single"] = "Deutsch einfach",
			["English-single"] = "Englisch einfach",
			["Angle-single"] = "Winkel einfach",
			["unknown"] = "unbekannt",
		};

		private static string System(string name) => QuoteSystems.TryGetValue(name, out var german) ? german : name;

		private static string Systems(string list) => string.Join(", ", list.Split(", ").Select(System));

		/// <summary>What a hyphen-spacing finding names (HyphenSpacing).</summary>
		private static readonly Dictionary<string, string> HyphenWhat = new(StringComparer.Ordinal)
		{
			["stray space after the hyphen"] = "überzähliges Leerzeichen nach dem Bindestrich",
			["“/” or a joining word missing after the hyphen"] = "„/“ oder ein Bindewort fehlt nach dem Bindestrich",
			["space after the hyphen"] = "Leerzeichen nach dem Bindestrich",
			["“/” or a joining word missing before the hyphen"] = "„/“ oder ein Bindewort fehlt vor dem Bindestrich",
			["space before the hyphen"] = "Leerzeichen vor dem Bindestrich",
			["space at the slash"] = "Leerzeichen am Schrägstrich",
		};

		/// <summary>Where an attribute or style sits (LinkAccessibility): on the element, or on an ancestor.</summary>
		private static string Where(string where) =>
			where == "on the element" ? "am Element"
			: where.StartsWith("on the ancestor ", StringComparison.Ordinal) ? "am Vorfahren " + where["on the ancestor ".Length..]
			: where;

		private static readonly Regex StyleReason = new(@"^(?<what>hidden attribute|inert attribute|inline style display:none|inline style visibility:\S+|inline style content-visibility:hidden) (?<where>on the element|on the ancestor .+)$", RegexOptions.Compiled);
		private static readonly Regex TabIndexReason = new(@"^(?<attr>tabindex=""[^""]*"") takes it out of the Tab order$", RegexOptions.Compiled);
		private static readonly Regex NotRenderedReason = new(@"^it is inside (?<el><[^>]*>), which is not rendered$", RegexOptions.Compiled);
		private static readonly Regex DetailsReason = new(@"^it is inside a closed <details> \((?<el>.+)\), outside its summary$", RegexOptions.Compiled);
		private static readonly Regex FieldsetReason = new(@"^it is inside a disabled <fieldset> \((?<el>.+)\)$", RegexOptions.Compiled);

		/// <summary>Why an element inside aria-hidden is out of the Tab order (LinkAccessibility).</summary>
		private static string Reason(string reason)
		{
			if (reason == "it is disabled")
			{
				return "es ist deaktiviert";
			}

			if (TabIndexReason.Match(reason) is { Success: true } t)
			{
				return $"{t.Groups["attr"].Value} nimmt es aus der Tab-Reihenfolge";
			}

			if (NotRenderedReason.Match(reason) is { Success: true } n)
			{
				return $"es liegt in {n.Groups["el"].Value}, das nicht dargestellt wird";
			}

			if (DetailsReason.Match(reason) is { Success: true } d)
			{
				return $"es liegt in einem geschlossenen <details> ({d.Groups["el"].Value}), außerhalb seiner summary";
			}

			if (FieldsetReason.Match(reason) is { Success: true } f)
			{
				return $"es liegt in einem deaktivierten <fieldset> ({f.Groups["el"].Value})";
			}

			if (StyleReason.Match(reason) is { Success: true } s)
			{
				var what = s.Groups["what"].Value switch
				{
					"hidden attribute" => "hidden-Attribut",
					"inert attribute" => "inert-Attribut",
					var style => "Inline-Style " + style["inline style ".Length..],
				};
				return $"{what} {Where(s.Groups["where"].Value)}";
			}

			return reason;
		}

		private static readonly Regex NeverFetched = new(@"\(never fetched by the crawl\)", RegexOptions.Compiled);
		private static readonly Regex FetchFailed = new(@"\(fetch failed \((?<status>[^)]*)\)\)", RegexOptions.Compiled);

		/// <summary>Stylesheets not in the snapshot, each with why (PageStyles.MissingSheet).</summary>
		private static string Sheets(string list) =>
			FetchFailed.Replace(NeverFetched.Replace(list, "(vom Crawl nie abgerufen)"), m => $"(Abruf fehlgeschlagen ({m.Groups["status"].Value}))");

		private static readonly Regex KindName = new(@"^(?<kind>bidi control|bidi isolate|C0 control|C1 control|invisible) (?<code>\(U\+[0-9A-F]{4,6}\))$", RegexOptions.Compiled);

		/// <summary>An invisible character's name (DefectTemplateAuthoring): a kind in German, a Unicode short name as written.</summary>
		private static string CharacterName(string name)
		{
			var m = KindName.Match(name);
			if (!m.Success)
			{
				return name;
			}

			var kind = m.Groups["kind"].Value switch
			{
				"bidi control" => "Bidi-Steuerzeichen",
				"bidi isolate" => "Bidi-Isolat",
				"C0 control" => "C0-Steuerzeichen",
				"C1 control" => "C1-Steuerzeichen",
				_ => "unsichtbares Zeichen",
			};
			return $"{kind} {m.Groups["code"].Value}";
		}

		private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled);

		private const string Code = @"U\+[0-9A-F]{4,6}";
		private const string Element = @"<[^>]*>";

		/// <summary>One pattern per detail format, the most specific first.</summary>
		private static readonly (Regex English, Func<Match, string> German)[] Rules =
		[
			// NoBreakChars
			(R($@"^(?<src>[^:]+): (?<a>{Code}(?: [A-Z][A-Z -]*[A-Z])?) directly (?<dir>before|after) (?<b>{Code}(?: [A-Z][A-Z -]*[A-Z])?)$"),
				m => $"{m.Groups["src"].Value}: {m.Groups["a"].Value} direkt {(m.Groups["dir"].Value == "before" ? "vor" : "nach")} {m.Groups["b"].Value}"),
			(R(@"^(?<src>[^:]+): run of (?<n>\d+) spaces, (?<g>\d+) of them (?<a>.+)$"),
				m => $"{m.Groups["src"].Value}: Folge von {m.Groups["n"].Value} Leerzeichen, {m.Groups["g"].Value} davon {m.Groups["a"].Value}"),

			// Decomposition (first: its details start with "Found" like those of ControlChars)
			(R(@"^Found decomposed (?<list>.+) in <(?<el>[\w-]+)> text$"),
				m => $"Zerlegte Zeichen im Text von <{m.Groups["el"].Value}>: {m.Groups["list"].Value}"),
			// ControlChars
			(R(@"^Found (?<name>.+?) in <title> text$"),
				m => $"{m.Groups["name"].Value} im Text von <title> gefunden"),
			(R(@"^Found (?<name>.+?) in meta\[@name=""(?<meta>[^""]*)""\] content$"),
				m => $"{m.Groups["name"].Value} im Inhalt von meta[@name=\"{m.Groups["meta"].Value}\"] gefunden"),

			(R(@"^Word '(?<w>.+)' appears in (?<n>\d+) encodings on one page \(same word, different byte forms\)$"),
				m => $"Das Wort '{m.Groups["w"].Value}' steht in {m.Groups["n"].Value} Kodierungen auf einer Seite (dasselbe Wort, verschiedene Byte-Formen)"),

			(R(@"^Found (?<name>.+?) in <(?<el>[\w-]+)> text$"),
				m => $"{m.Groups["name"].Value} im Text von <{m.Groups["el"].Value}> gefunden"),

			// Quotes
			(R(@"^Multiple quote systems: (?<list>.+?)(?: \(page language: (?<lang>[^)]*)\))?$"),
				m => $"Mehrere Anführungszeichen-Systeme: {Systems(m.Groups["list"].Value)}"
					+ (m.Groups["lang"].Success ? $" (Seitensprache: {m.Groups["lang"].Value})" : string.Empty)),
			(R($@"^'(?<g>.)' \((?<u>{Code})\) used as an apostrophe; the apostrophe is ’ \(U\+2019\)$"),
				m => $"'{m.Groups["g"].Value}' ({m.Groups["u"].Value}) als Apostroph verwendet; der Apostroph ist ’ (U+2019)"),
			(R(@"^Typographic opener '(?<o>.)' \((?<s>[^)]+)\) closed by straight quote "" \(U\+0022\)$"),
				m => $"Typografisches öffnendes Zeichen '{m.Groups["o"].Value}' ({System(m.Groups["s"].Value)}) mit geradem \" (U+0022) geschlossen"),
			(R($@"^Closer '(?<c>.)' \((?<u>{Code})\) found with no matching typographic opener$"),
				m => $"Schließendes Zeichen '{m.Groups["c"].Value}' ({m.Groups["u"].Value}) ohne passendes typografisches öffnendes Zeichen"),
			(R($@"^Straight opener "" \(U\+0022\) closed by typographic '(?<c>.)' \((?<u>{Code})\)$"),
				m => $"Gerades öffnendes \" (U+0022) mit typografischem '{m.Groups["c"].Value}' ({m.Groups["u"].Value}) geschlossen"),
			(R(@"^Opener '(?<o>.)' \((?<s>[^)]+)\) closed by '(?<c>.)' \((?<s2>[^)]+)\)$"),
				m => $"Öffnendes Zeichen '{m.Groups["o"].Value}' ({System(m.Groups["s"].Value)}) mit '{m.Groups["c"].Value}' ({System(m.Groups["s2"].Value)}) geschlossen"),
			(R(@"^Opener '(?<o>.)' \((?<s>[^)]+)\) at position (?<p>\d+) has no closer$"),
				m => $"Öffnendes Zeichen '{m.Groups["o"].Value}' ({System(m.Groups["s"].Value)}) an Position {m.Groups["p"].Value} ohne schließendes Zeichen"),
			(R(@"^(?<role>Opener|Closer) '(?<m>.)' should be '(?<x>.)' \((?<s>[^)]+)\): the quotation opens and closes with the same mark '(?<m2>.)'$"),
				m => $"{(m.Groups["role"].Value == "Opener" ? "Öffnendes" : "Schließendes")} Zeichen '{m.Groups["m"].Value}' sollte '{m.Groups["x"].Value}' sein ({System(m.Groups["s"].Value)}): " +
					$"Das Zitat öffnet und schließt mit demselben Zeichen '{m.Groups["m2"].Value}'"),

			// HyphenSpacing
			(R(@"^(?<src>[^:]+): (?<what>stray space after the hyphen|“/” or a joining word missing after the hyphen|space after the hyphen|“/” or a joining word missing before the hyphen|space before the hyphen|space at the slash) in “(?<t>.*)”$"),
				m => $"{m.Groups["src"].Value}: {HyphenWhat[m.Groups["what"].Value]} in „{m.Groups["t"].Value}“"),

			// DoubledWords (PUNCTUATION_WEAK and WORD_DOUBLED)
			(R(@"^(?<src>[^:]+): “(?<w>.*)” before a line break and again after it — a colon would mark it as the lead-in \(“(?<w2>.*)”\)$"),
				m => $"{m.Groups["src"].Value}: „{m.Groups["w"].Value}“ vor einem Zeilenumbruch und noch einmal danach – ein Doppelpunkt würde es als Einleitung kennzeichnen („{m.Groups["w2"].Value}“)"),
			(R(@"^(?<src>[^:]+): the word twice in “(?<t>.*)”$"),
				m => $"{m.Groups["src"].Value}: das Wort zweimal in „{m.Groups["t"].Value}“"),

			// Ligatures
			(R(@"^Ligature (?<n>.+)$"),
				m => $"Ligatur {m.Groups["n"].Value}"),

			// LanguageMismatch
			(R(@"^<html lang=""(?<h>[^""]*)""> conflicts with <meta name=""language"" content=""(?<m>[^""]*)"">$"),
				m => $"<html lang=\"{m.Groups["h"].Value}\"> widerspricht <meta name=\"language\" content=\"{m.Groups["m"].Value}\">"),

			// MisplacedAnchors: aria-hidden
			(R($@"^(?<el>{Element}) is hidden from screen readers \(aria-hidden\) and not reachable with Tab: hidden by CSS: (?<e>.+)$"),
				m => $"{m.Groups["el"].Value} ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: per CSS verborgen: {m.Groups["e"].Value}"),
			(R($@"^(?<el>{Element}) is hidden from screen readers \(aria-hidden\) and not reachable with Tab: (?<r>.+)$"),
				m => $"{m.Groups["el"].Value} ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: {Reason(m.Groups["r"].Value)}"),
			(R($@"^(?<el>{Element}) is hidden from screen readers \(aria-hidden\); whether it is reachable with Tab cannot be told: stylesheet not in the snapshot: (?<l>.+)$"),
				m => $"{m.Groups["el"].Value} ist vor Screenreadern verborgen (aria-hidden); ob es mit Tab erreichbar ist, lässt sich nicht sagen: Stylesheet nicht im Snapshot: {Sheets(m.Groups["l"].Value)}"),
			(R($@"^(?<el>{Element}) is hidden from screen readers \(aria-hidden\) and reachable with Tab except (?<e>.+)$"),
				m => $"{m.Groups["el"].Value} ist vor Screenreadern verborgen (aria-hidden) und mit Tab erreichbar, außer {m.Groups["e"].Value}"),
			(R($@"^(?<el>{Element}) is hidden from screen readers \(aria-hidden\) but reachable with Tab$"),
				m => $"{m.Groups["el"].Value} ist vor Screenreadern verborgen (aria-hidden), aber mit Tab erreichbar"),

			// MisplacedAnchors: ARIA references
			(R($@"^(?<attr>[\w-]+) points to missing (?<ids>ids?) (?<list>.+) on (?<el>{Element})$"),
				m => $"{m.Groups["attr"].Value} verweist auf fehlende {(m.Groups["ids"].Value == "ids" ? "IDs" : "ID")} {m.Groups["list"].Value} an {m.Groups["el"].Value}"),

			// MisplacedAnchors: links without a name, against the page's stylesheets (the reason follows)
			(R(@"^Hidden by CSS \((?<e>.+)\): (?<r>(?:No accessible name|Image link:) .+)$"),
				m => $"Per CSS verborgen ({m.Groups["e"].Value}): {In(DocumentLanguage.German, "MISPLACED_ANCHOR_EMPTY", m.Groups["r"].Value)}"),
			(R(@"^Whether it is shown cannot be told \(stylesheet not in the snapshot: (?<l>.+)\): (?<r>(?:No accessible name|Image link:) .+)$"),
				m => $"Ob er angezeigt wird, lässt sich nicht sagen (Stylesheet nicht im Snapshot: {Sheets(m.Groups["l"].Value)}): {In(DocumentLanguage.German, "MISPLACED_ANCHOR_EMPTY", m.Groups["r"].Value)}"),
			(R(@"^Shown except (?<e>.+?): (?<r>(?:No accessible name|Image link:) .+)$"),
				m => $"Angezeigt außer {m.Groups["e"].Value}: {In(DocumentLanguage.German, "MISPLACED_ANCHOR_EMPTY", m.Groups["r"].Value)}"),

			// LinkAccessibility: links without a name
			(R(@"^No accessible name — aria-labelledby points to missing id (?<l>.+) — (?<el><.+)$"),
				m => $"Kein zugänglicher Name – aria-labelledby verweist auf fehlende ID {m.Groups["l"].Value} – {m.Groups["el"].Value}"),
			(R(@"^Image link: the image has no alt attribute — (?<el>.+)$"),
				m => $"Bildlink: Das Bild hat kein alt-Attribut – {m.Groups["el"].Value}"),
			(R(@"^Image link: the image is marked decorative \(alt=""""\) — (?<el>.+)$"),
				m => $"Bildlink: Das Bild ist als dekorativ markiert (alt=\"\") – {m.Groups["el"].Value}"),
			(R(@"^No accessible name — (?<el>.+)$"),
				m => $"Kein zugänglicher Name – {m.Groups["el"].Value}"),

			// WordSplits
			(R(@"^Anchor closes mid-word — stray text after </a>: '(?<t>.*)'$"),
				m => $"Link endet mitten im Wort – Resttext nach </a>: '{m.Groups["t"].Value}'"),

			// DefectWordCollisions
			(R(@"^Inline <(?<el>[\w-]+)> abuts bare text without separator — words merge$"),
				m => $"Inline-<{m.Groups["el"].Value}> stößt ohne Trennung an losen Text – Wörter verschmelzen"),
			(R(@"^Bare text abuts inline <(?<el>[\w-]+)> without separator — words merge$"),
				m => $"Loser Text stößt ohne Trennung an Inline-<{m.Groups["el"].Value}> – Wörter verschmelzen"),

			// DefectTemplateAuthoring
			(R(@"^BOM \(U\+FEFF\) found at (?<n>\d+) embedded position\(s\); file also has the legitimate leading BOM at offset 0\.$"),
				m => $"BOM (U+FEFF) an {m.Groups["n"].Value} eingebetteten Stelle(n) gefunden; die Datei hat außerdem das zulässige BOM am Anfang (Offset 0)."),
			(R(@"^BOM \(U\+FEFF\) found at (?<n>\d+) embedded position\(s\); no leading BOM\.$"),
				m => $"BOM (U+FEFF) an {m.Groups["n"].Value} eingebetteten Stelle(n) gefunden; kein BOM am Dateianfang."),
			(R($@"^(?<name>.+) found at (?<n>\d+) position\(s\) in non-editorial container content \(first inside (?<el>{Element})\)\.$"),
				m => $"{CharacterName(m.Groups["name"].Value)} an {m.Groups["n"].Value} Stelle(n) im nicht redaktionellen Container-Inhalt gefunden (zuerst in {m.Groups["el"].Value})."),

			// UnwantedPatterns: the group's category and name, and the patterns, are the configuration's
			(R(@"^(?<group>.+?) — open placeholder, missing closing '(?<c>.+)' — patterns: (?<p>.+)$"),
				m => $"{m.Groups["group"].Value} – offener Platzhalter, schließendes '{m.Groups["c"].Value}' fehlt – Muster: {m.Groups["p"].Value}"),
			(R(@"^(?<group>.+?) — patterns: (?<p>.+)$"),
				m => $"{m.Groups["group"].Value} – Muster: {m.Groups["p"].Value}"),
			(R(@"^(?<group>.+?) — pattern: (?<p>.+) \(in this page's url\)$"),
				m => $"{m.Groups["group"].Value} – Muster: {m.Groups["p"].Value} (in der Adresse dieser Seite)"),
			(R(@"^(?<group>.+?) — pattern: (?<p>.+)$"),
				m => $"{m.Groups["group"].Value} – Muster: {m.Groups["p"].Value}"),
		];
	}
}
