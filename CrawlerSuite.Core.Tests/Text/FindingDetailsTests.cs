using CrawlerSuite.Core.Text;
using Xunit;

namespace CrawlerSuite.Core.Tests.Text
{
	/// <summary>
	/// The details of the content-quality checks in a German document: one case per format a check
	/// writes, what the check captured kept as found; English unchanged, an unknown detail unchanged,
	/// the tracking part and the position marker kept.
	/// </summary>
	public class FindingDetailsTests
	{
		[Theory]
		// NoBreakChars
		[InlineData("LINE_BREAK_GLUE_DOUBLE_SPACE", "p: U+00A0 NO-BREAK SPACE directly before U+0020 SPACE",
			"p: U+00A0 NO-BREAK SPACE direkt vor U+0020 SPACE")]
		[InlineData("LINE_BREAK_GLUE_INERT", "li: U+00A0 NO-BREAK SPACE directly after U+2013 EN DASH",
			"li: U+00A0 NO-BREAK SPACE direkt nach U+2013 EN DASH")]
		[InlineData("LINE_BREAK_GLUE_INERT", "h2: U+202F directly after U+2014 EM DASH",
			"h2: U+202F direkt nach U+2014 EM DASH")]
		[InlineData("LINE_BREAK_GLUE_DOUBLE_SPACE", "p: run of 3 spaces, 2 of them U+00A0 NO-BREAK SPACE",
			"p: Folge von 3 Leerzeichen, 2 davon U+00A0 NO-BREAK SPACE")]
		// ControlChars
		[InlineData("CONTROL_CHARS_IN_CONTENT", "Found LF (U+000A) in <title> text", "LF (U+000A) im Text von <title> gefunden")]
		[InlineData("CONTROL_CHARS_IN_CONTENT", "Found LF (U+000A) in meta[@name=\"description\"] content",
			"LF (U+000A) im Inhalt von meta[@name=\"description\"] gefunden")]
		[InlineData("CONTROL_CHARS_IN_CONTENT", "Found ZWSP (U+200B) in <h2> text", "ZWSP (U+200B) im Text von <h2> gefunden")]
		// Decomposition
		[InlineData("DECOMPOSED_TEXT_IN_CONTENT", "Found decomposed 'ü' (u + U+0308), 'ö' (o + U+0308) in <p> text",
			"Zerlegte Zeichen im Text von <p>: 'ü' (u + U+0308), 'ö' (o + U+0308)")]
		[InlineData("MIXED_NORMALIZATION_IN_CONTENT", "Word 'Brücke' appears in 2 encodings on one page (same word, different byte forms)",
			"Das Wort 'Brücke' steht in 2 Kodierungen auf einer Seite (dasselbe Wort, verschiedene Byte-Formen)")]
		// Quotes
		[InlineData("QUOTE_SYSTEM_MIX", "Multiple quote systems: English-double, German-double (page language: de)",
			"Mehrere Anführungszeichen-Systeme: Englisch doppelt, Deutsch doppelt (Seitensprache: de)")]
		[InlineData("QUOTE_SYSTEM_MIX", "Multiple quote systems: German-double, Guillemet",
			"Mehrere Anführungszeichen-Systeme: Deutsch doppelt, Guillemets")]
		[InlineData("QUOTE_WRONG_APOSTROPHE", "'‘' (U+2018) used as an apostrophe; the apostrophe is ’ (U+2019)",
			"'‘' (U+2018) als Apostroph verwendet; der Apostroph ist ’ (U+2019)")]
		[InlineData("QUOTE_MIXED_KIND", "Typographic opener '„' (German-double) closed by straight quote \" (U+0022)",
			"Typografisches öffnendes Zeichen '„' (Deutsch doppelt) mit geradem \" (U+0022) geschlossen")]
		[InlineData("QUOTE_WRONG_OPEN", "Closer '“' (U+201C) found with no matching typographic opener",
			"Schließendes Zeichen '“' (U+201C) ohne passendes typografisches öffnendes Zeichen")]
		[InlineData("QUOTE_MIXED_KIND", "Straight opener \" (U+0022) closed by typographic '“' (U+201C)",
			"Gerades öffnendes \" (U+0022) mit typografischem '“' (U+201C) geschlossen")]
		[InlineData("QUOTE_WRONG_CLOSE", "Opener '„' (German-double) closed by '”' (English-double)",
			"Öffnendes Zeichen '„' (Deutsch doppelt) mit '”' (Englisch doppelt) geschlossen")]
		[InlineData("QUOTE_WRONG_CLOSE", "Opener '„' (German-double) closed by '»' (unknown)",
			"Öffnendes Zeichen '„' (Deutsch doppelt) mit '»' (unbekannt) geschlossen")]
		[InlineData("QUOTE_UNMATCHED", "Opener '„' (German-double) at position 12 has no closer",
			"Öffnendes Zeichen '„' (Deutsch doppelt) an Position 12 ohne schließendes Zeichen")]
		[InlineData("QUOTE_WRONG_OPEN", "Opener '“' should be '„' (German-double): the quotation opens and closes with the same mark '“'",
			"Öffnendes Zeichen '“' sollte '„' sein (Deutsch doppelt): Das Zitat öffnet und schließt mit demselben Zeichen '“'")]
		[InlineData("QUOTE_WRONG_CLOSE", "Closer '“' should be '”' (English-double): the quotation opens and closes with the same mark '“'",
			"Schließendes Zeichen '“' sollte '”' sein (Englisch doppelt): Das Zitat öffnet und schließt mit demselben Zeichen '“'")]
		// HyphenSpacing
		[InlineData("SPACE_AFTER_HYPHEN", "li: space after the hyphen in “Wasser- Welle”", "li: Leerzeichen nach dem Bindestrich in „Wasser- Welle“")]
		[InlineData("SPACE_AFTER_HYPHEN", "p: stray space after the hyphen in “10- 12”", "p: überzähliges Leerzeichen nach dem Bindestrich in „10- 12“")]
		[InlineData("SPACE_AFTER_HYPHEN", "p: “/” or a joining word missing after the hyphen in “Regen- Nebelzeit”",
			"p: „/“ oder ein Bindewort fehlt nach dem Bindestrich in „Regen- Nebelzeit“")]
		[InlineData("SPACE_BEFORE_HYPHEN", "title: space before the hyphen in “Tide -Welle”", "title: Leerzeichen vor dem Bindestrich in „Tide -Welle“")]
		[InlineData("SPACE_BEFORE_HYPHEN", "p: “/” or a joining word missing before the hyphen in “Regenzeit -schauer”",
			"p: „/“ oder ein Bindewort fehlt vor dem Bindestrich in „Regenzeit -schauer“")]
		[InlineData("SPACE_AFTER_HYPHEN", "li: space at the slash in “Regen-/ Nebelzeit”", "li: Leerzeichen am Schrägstrich in „Regen-/ Nebelzeit“")]
		// DoubledWords
		[InlineData("WORD_DOUBLED", "p: the word twice in “Eis Eis”", "p: das Wort zweimal in „Eis Eis“")]
		[InlineData("PUNCTUATION_WEAK", "li: “Wellen” before a line break and again after it — a colon would mark it as the lead-in (“Wellen:”)",
			"li: „Wellen“ vor einem Zeilenumbruch und noch einmal danach – ein Doppelpunkt würde es als Einleitung kennzeichnen („Wellen:“)")]
		// Ligatures, language
		[InlineData("LIGATURE", "Ligature ﬂ (fl)", "Ligatur ﬂ (fl)")]
		[InlineData("LANGUAGE_MISMATCH", "<html lang=\"de\"> conflicts with <meta name=\"language\" content=\"en\">",
			"<html lang=\"de\"> widerspricht <meta name=\"language\" content=\"en\">")]
		// aria-hidden
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden) and not reachable with Tab: hidden attribute on the element",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: hidden-Attribut am Element")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden) and not reachable with Tab: inline style display:none on the ancestor <div class=\"wave\">",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: Inline-Style display:none am Vorfahren <div class=\"wave\">")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden) and not reachable with Tab: inline style visibility:collapse on the element",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: Inline-Style visibility:collapse am Element")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden) and not reachable with Tab: inert attribute on the element",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: inert-Attribut am Element")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden) and not reachable with Tab: inline style content-visibility:hidden on the element",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: Inline-Style content-visibility:hidden am Element")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden) and not reachable with Tab: tabindex=\"-1\" takes it out of the Tab order",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: tabindex=\"-1\" nimmt es aus der Tab-Reihenfolge")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<button> is hidden from screen readers (aria-hidden) and not reachable with Tab: it is disabled",
			"<button> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: es ist deaktiviert")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden) and not reachable with Tab: it is inside <template>, which is not rendered",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: es liegt in <template>, das nicht dargestellt wird")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden) and not reachable with Tab: it is inside a closed <details> (<details class=\"wave\">), outside its summary",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: es liegt in einem geschlossenen <details> (<details class=\"wave\">), außerhalb seiner summary")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<input type=\"text\"> is hidden from screen readers (aria-hidden) and not reachable with Tab: it is inside a disabled <fieldset> (<fieldset class=\"wave\">)",
			"<input type=\"text\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: es liegt in einem deaktivierten <fieldset> (<fieldset class=\"wave\">)")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CLEARED", "<input type=\"text\"> is hidden from screen readers (aria-hidden) and not reachable with Tab: hidden by CSS: sea.css:12:3 .wave input",
			"<input type=\"text\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab nicht erreichbar: per CSS verborgen: sea.css:12:3 .wave input")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_UNDETERMINED", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden); whether it is reachable with Tab cannot be told: stylesheet not in the snapshot: /css/sea.css (never fetched by the crawl); /css/ice.css (fetch failed (500))",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden); ob es mit Tab erreichbar ist, lässt sich nicht sagen: Stylesheet nicht im Snapshot: /css/sea.css (vom Crawl nie abgerufen); /css/ice.css (Abruf fehlgeschlagen (500))")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE_CONDITIONAL", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden) and reachable with Tab except sea.css:4:1 .wave a under @media (max-width: 600px)",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden) und mit Tab erreichbar, außer sea.css:4:1 .wave a under @media (max-width: 600px)")]
		[InlineData("ARIA_HIDDEN_FOCUSABLE", "<a href=\"/tide.html\"> is hidden from screen readers (aria-hidden) but reachable with Tab",
			"<a href=\"/tide.html\"> ist vor Screenreadern verborgen (aria-hidden), aber mit Tab erreichbar")]
		// ARIA references
		[InlineData("ARIA_REFERENCE_BROKEN", "aria-labelledby points to missing id \"wave-title\" on <canvas>",
			"aria-labelledby verweist auf fehlende ID \"wave-title\" an <canvas>")]
		[InlineData("ARIA_REFERENCE_BROKEN", "aria-describedby points to missing ids \"tide\", \"ebb\" on <div role=\"dialog\" tabindex=\"-1\">",
			"aria-describedby verweist auf fehlende IDs \"tide\", \"ebb\" an <div role=\"dialog\" tabindex=\"-1\">")]
		// Links without a name
		[InlineData("MISPLACED_ANCHOR_EMPTY", "No accessible name — aria-labelledby points to missing id \"wave\" — <a href=\"/tide.html\">",
			"Kein zugänglicher Name – aria-labelledby verweist auf fehlende ID \"wave\" – <a href=\"/tide.html\">")]
		[InlineData("MISPLACED_ANCHOR_EMPTY", "Image link: the image has no alt attribute — <a href=\"/tide.html\">",
			"Bildlink: Das Bild hat kein alt-Attribut – <a href=\"/tide.html\">")]
		[InlineData("MISPLACED_ANCHOR_EMPTY", "Image link: the image is marked decorative (alt=\"\") — <a href=\"/tide.html\">",
			"Bildlink: Das Bild ist als dekorativ markiert (alt=\"\") – <a href=\"/tide.html\">")]
		[InlineData("MISPLACED_ANCHOR_EMPTY", "No accessible name — <a href=\"/tide.html\">", "Kein zugänglicher Name – <a href=\"/tide.html\">")]
		// Word splits and collisions
		[InlineData("SPLIT_WORD_ANCHOR", "Anchor closes mid-word — stray text after </a>: 'welle'", "Link endet mitten im Wort – Resttext nach </a>: 'welle'")]
		[InlineData("WORD_COLLISION", "[3] Inline <span> abuts bare text without separator — words merge",
			"[3] Inline-<span> stößt ohne Trennung an losen Text – Wörter verschmelzen")]
		[InlineData("WORD_COLLISION", "[1] Bare text abuts inline <b> without separator — words merge",
			"[1] Loser Text stößt ohne Trennung an Inline-<b> – Wörter verschmelzen")]
		// Template defects
		[InlineData("EMBEDDED_BOM_IN_BODY", "BOM (U+FEFF) found at 2 embedded position(s); no leading BOM.",
			"BOM (U+FEFF) an 2 eingebetteten Stelle(n) gefunden; kein BOM am Dateianfang.")]
		[InlineData("EMBEDDED_BOM_IN_BODY", "BOM (U+FEFF) found at 1 embedded position(s); file also has the legitimate leading BOM at offset 0.",
			"BOM (U+FEFF) an 1 eingebetteten Stelle(n) gefunden; die Datei hat außerdem das zulässige BOM am Anfang (Offset 0).")]
		[InlineData("INVISIBLE_CHAR_IN_BODY", "ZWSP (U+200B) found at 4 position(s) in non-editorial container content (first inside <span class=\"wave\">).",
			"ZWSP (U+200B) an 4 Stelle(n) im nicht redaktionellen Container-Inhalt gefunden (zuerst in <span class=\"wave\">).")]
		[InlineData("INVISIBLE_CHAR_IN_BODY", "bidi control (U+202E) found at 1 position(s) in non-editorial container content (first inside <q>).",
			"Bidi-Steuerzeichen (U+202E) an 1 Stelle(n) im nicht redaktionellen Container-Inhalt gefunden (zuerst in <q>).")]
		[InlineData("INVISIBLE_CHAR_IN_BODY", "C0 control (U+0007) found at 1 position(s) in non-editorial container content (first inside <div class=\"tide\">).",
			"C0-Steuerzeichen (U+0007) an 1 Stelle(n) im nicht redaktionellen Container-Inhalt gefunden (zuerst in <div class=\"tide\">).")]
		[InlineData("WORD_SPLIT_BY_FORMATTING", "The first letters of several words in a row are each formatted on their own (for example, each letter bolded), which splits the words apart for screen readers and search engines. To highlight an abbreviation, wrap the full term in an <abbr> tag instead.",
			"Die Anfangsbuchstaben mehrerer Wörter hintereinander sind jeweils einzeln formatiert (etwa jeder Buchstabe fett), was die Wörter für Screenreader und Suchmaschinen zerreißt. Um eine Abkürzung hervorzuheben, stattdessen den ganzen Begriff in ein <abbr>-Tag setzen.")]
		[InlineData("BARE_TEXT_IN_CONTAINER", "Text beside paragraphs or headings, without a paragraph of its own",
			"Text neben Absätzen oder Überschriften, ohne eigenen Absatz")]
		[InlineData("BARE_TEXT_IN_CONTAINER", "Paragraphs separated by <br><br> instead of paragraph markup",
			"Absätze durch <br><br> statt durch Absatz-Markup getrennt")]
		// Parse errors
		[InlineData("MALFORMED_HTML", "TagNotOpened", "Schließendes Tag ohne öffnendes (TagNotOpened)")]
		[InlineData("MALFORMED_HTML", "TagNotClosed", "Tag nicht geschlossen (TagNotClosed)")]
		[InlineData("MALFORMED_HTML", "CharsetMismatch", "Zeichensatz widersprüchlich angegeben (CharsetMismatch)")]
		[InlineData("MALFORMED_HTML", "EndTagNotRequired", "End-Tag überflüssig (EndTagNotRequired)")]
		[InlineData("MALFORMED_HTML", "EndTagInvalidHere", "End-Tag an dieser Stelle ungültig (EndTagInvalidHere)")]
		[InlineData("MALFORMED_HTML", "CONTENT_BEFORE_DOCTYPE", "Inhalt vor dem Doctype (CONTENT_BEFORE_DOCTYPE)")]
		// Unwanted patterns: the group and the patterns are the configuration's
		[InlineData("UNWANTED_PATTERN", "Leftover: Tide placeholder — pattern: {{wave}}", "Leftover: Tide placeholder – Muster: {{wave}}")]
		[InlineData("UNWANTED_PATTERN", "Leftover: Tide placeholder — patterns: {{wave}}, {{ebb}}", "Leftover: Tide placeholder – Muster: {{wave}}, {{ebb}}")]
		[InlineData("UNWANTED_PATTERN", "Leftover: Tide placeholder — open placeholder, missing closing '}}' — patterns: {{wave",
			"Leftover: Tide placeholder – offener Platzhalter, schließendes '}}' fehlt – Muster: {{wave")]
		[InlineData("UNWANTED_PATTERN", "Leftover: Tide placeholder — pattern: ebb (in this page's url)",
			"Leftover: Tide placeholder – Muster: ebb (in der Adresse dieser Seite)")]
		public void ADetail_InGerman(string type, string english, string german)
		{
			Assert.Equal(german, FindingDetails.In(DocumentLanguage.German, type, english));
			Assert.Equal(english, FindingDetails.In(DocumentLanguage.English, type, english));
		}

		[Fact]
		public void TheProducersOwnTexts_AreKnown()
		{
			Assert.Equal("Text neben Absätzen oder Überschriften, ohne eigenen Absatz",
				FindingDetails.In(DocumentLanguage.German, "BARE_TEXT_IN_CONTAINER", CrawlerSuite.Core.Quality.DefectBareText.BesideParagraphsDetail));
			Assert.Equal("Absätze durch <br><br> statt durch Absatz-Markup getrennt",
				FindingDetails.In(DocumentLanguage.German, "BARE_TEXT_IN_CONTAINER", CrawlerSuite.Core.Quality.DefectBareText.BreakParagraphsDetail));
			var sameMark = $"Closer '“' should be '”' (English-double): {CrawlerSuite.Core.Quality.Quotes.SameMarkNote} '“'";
			Assert.StartsWith("Schließendes Zeichen", FindingDetails.In(DocumentLanguage.German, "QUOTE_WRONG_CLOSE", sameMark));
			var pageLanguage = "Multiple quote systems: German-single" + CrawlerSuite.Core.Quality.Quotes.PageLanguageOpen + "de)";
			Assert.Equal("Mehrere Anführungszeichen-Systeme: Deutsch einfach (Seitensprache: de)",
				FindingDetails.In(DocumentLanguage.German, "QUOTE_SYSTEM_MIX", pageLanguage));
			Assert.True(FindingDetails.Count > 40);
		}

		[Fact]
		public void TheTrackingPart_AndThePosition_AreKept()
		{
			Assert.Equal("ZWSP (U+200B) im Text von <h2> gefunden ¦¦ID¦¦ <h2> :: Welle",
				FindingDetails.In(DocumentLanguage.German, "CONTROL_CHARS_IN_CONTENT", "Found ZWSP (U+200B) in <h2> text ¦¦ID¦¦ <h2> :: Welle"));
			var url = "Leftover: Tide — pattern: ebb" + CrawlerSuite.Core.Quality.UnwantedPatterns.UrlMarker + "[a] https://site.test/ebb.html";
			Assert.Equal("Leftover: Tide – Muster: ebb" + CrawlerSuite.Core.Quality.UnwantedPatterns.UrlMarker + "[a] https://site.test/ebb.html",
				FindingDetails.In(DocumentLanguage.German, "UNWANTED_PATTERN", url));
			Assert.Equal("[2] „Wasser“ + „Welle“", FindingDetails.In(DocumentLanguage.German, "ADJACENT_ANCHOR", "[2] „Wasser“ + „Welle“"));
		}

		[Fact]
		public void AnUnknownDetail_AndMarkup_StayAsFound()
		{
			Assert.Equal("Something the app does not know", FindingDetails.In(DocumentLanguage.German, "WORD_DOUBLED", "Something the app does not know"));
			Assert.Equal("<div class=\"wave\">", FindingDetails.In(DocumentLanguage.German, "STYLED_HEADING", "<div class=\"wave\">"));
			Assert.Equal("TagNotOpened", FindingDetails.In(DocumentLanguage.German, "WORD_DOUBLED", "TagNotOpened"));
			Assert.Equal(string.Empty, FindingDetails.In(DocumentLanguage.German, "WORD_DOUBLED", string.Empty));
		}
	}
}
