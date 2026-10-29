using CrawlerSuite.Core.Links;
using CrawlerSuite.Core.Text;
using Xunit;

namespace CrawlerSuite.Core.Tests.Text
{
	/// <summary>
	/// The sentences other steps write into logs 07, 38, 25 and 16, in German by pattern: what comes
	/// from the site, its stylesheets or the configuration stays as found; English and unknown text
	/// stay as they are.
	/// </summary>
	public class LogSentencesTests
	{
		private const DocumentLanguage De = DocumentLanguage.German;

		[Theory]
		// Whole sentences.
		[InlineData("The page's canonical points here — fix the canonical (SEO › Canonical issues).",
			"Das Canonical der Seite zeigt hierher – das Canonical korrigieren (SEO › Canonical issues).")]
		[InlineData("Not referenced by a saved page or stylesheet, a redirect or the content list — e.g. a script builds it, or the page that links it was not saved.",
			"Weder eine gespeicherte Seite noch ein Stylesheet, eine Weiterleitung oder die Inhaltsliste verweist darauf – etwa weil ein Skript die Adresse zusammensetzt oder die verlinkende Seite nicht gespeichert wurde.")]
		[InlineData("Not referenced by a saved page or stylesheet — e.g. a script builds the address, or the page that uses it was not saved.",
			"Weder eine gespeicherte Seite noch ein Stylesheet verweist darauf – etwa weil ein Skript die Adresse zusammensetzt oder die verwendende Seite nicht gespeichert wurde.")]
		[InlineData("The stylesheet's address cannot be read.", "Die Adresse des Stylesheets lässt sich nicht lesen.")]
		[InlineData("In a rule whose selector the app cannot read: .wave :is(.tide.",
			"In einer Regel, deren Selektor die App nicht lesen kann: .wave :is(.tide.")]
		[InlineData("Not in a style rule (e.g. @font-face or a nested rule): loaded when that font or rule is used.",
			"Nicht in einer Stilregel (etwa @font-face oder eine verschachtelte Regel): wird geladen, wenn diese Schrift oder Regel verwendet wird.")]
		// On load, no count of pages.
		[InlineData(".wave .tide applies on https://site.test/wasser/ebbe.html.", ".wave .tide greift auf https://site.test/wasser/ebbe.html.")]
		// Graded, with the pages checked.
		[InlineData(".wave .tide applies on https://site.test/wasser/ebbe.html under @media (max-width: 600px): requested on load on every device that matches. Checked on 3 page(s) that load it.",
			".wave .tide greift auf https://site.test/wasser/ebbe.html unter @media (max-width: 600px): wird beim Laden auf jedem passenden Gerät angefordert. Geprüft auf 3 Seite(n), die es laden.")]
		[InlineData(".wave a:hover applies on https://site.test/wasser/ebbe.html only in a state (:hover). Checked on 1 page(s) that load it.",
			".wave a:hover greift auf https://site.test/wasser/ebbe.html nur in einem Zustand (:hover). Geprüft auf 1 Seite(n), die es laden.")]
		[InlineData(".wave a:hover, .wave a:focus applies on https://site.test/x.html only in a state (:hover, :focus, under @media screen). No saved page loads the stylesheet directly.",
			".wave a:hover, .wave a:focus greift auf https://site.test/x.html nur in einem Zustand (:hover, :focus, unter @media screen). Keine gespeicherte Seite lädt das Stylesheet direkt.")]
		[InlineData(".wave input + label applies on https://site.test/x.html only in a state (named in the selector). Checked on 2 page(s) that load it.",
			".wave input + label greift auf https://site.test/x.html nur in einem Zustand (im Selektor genannt). Geprüft auf 2 Seite(n), die es laden.")]
		[InlineData(".wave .tide applies on https://site.test/x.html only when printed (@media print). Checked on 2 page(s) that load it.",
			".wave .tide greift auf https://site.test/x.html nur beim Drucken (@media print). Geprüft auf 2 Seite(n), die es laden.")]
		[InlineData(".wave .is-open does not apply on load; .is-open is named in https://site.test/js/wave.js. Checked on 4 page(s) that load it.",
			".wave .is-open greift nicht beim Laden; .is-open wird in https://site.test/js/wave.js genannt. Geprüft auf 4 Seite(n), die es laden.")]
		[InlineData(".wave .is-open does not apply on load; .is-open is named in an inline script on https://site.test/wasser/ebbe.html. Checked on 4 page(s) that load it.",
			".wave .is-open greift nicht beim Laden; .is-open wird in einem Inline-Skript auf https://site.test/wasser/ebbe.html genannt. Geprüft auf 4 Seite(n), die es laden.")]
		[InlineData(".wave > .tide: its classes are on the pages, but no element matches on load. Checked on 5 page(s) that load it.",
			".wave > .tide: Die Klassen stehen auf den Seiten, aber beim Laden passt kein Element. Geprüft auf 5 Seite(n), die es laden.")]
		[InlineData(".wave .flood needs .flood, which no crawled page and no script names. Checked on 5 page(s) that load it.",
			".wave .flood braucht .flood, das weder eine gecrawlte Seite noch ein Skript nennt. Geprüft auf 5 Seite(n), die es laden.")]
		// The same stylesheet at other addresses, after any graded evidence.
		[InlineData(".wave .flood needs .flood, which no crawled page and no script names. Checked on 5 page(s) that load it. The same stylesheet is also at https://site.test/css/wave.v2.css.",
			".wave .flood braucht .flood, das weder eine gecrawlte Seite noch ein Skript nennt. Geprüft auf 5 Seite(n), die es laden. Dasselbe Stylesheet liegt auch unter https://site.test/css/wave.v2.css.")]
		[InlineData(".wave .tide applies on https://site.test/x.html. The same stylesheet is also at https://site.test/a.css, https://site.test/b.css.",
			".wave .tide greift auf https://site.test/x.html. Dasselbe Stylesheet liegt auch unter https://site.test/a.css, https://site.test/b.css.")]
		public void Evidence_InGerman(string english, string german) =>
			Assert.Equal(german, LogSentences.Evidence(De, english));

		[Fact]
		public void Evidence_OfARelativeLink_AsTheProducerWritesIt()
		{
			var (_, english) = BrokenLinkOrigins.Relative("wasser/ebbe.html", new Uri("https://site.test/meer/welle.html"), _ => true);
			Assert.StartsWith("Written without a leading '/'", english);
			Assert.Equal(
				"Ohne führenden „/“ geschrieben: Der Browser setzt den Ordner der Seite /meer/ davor. Als /wasser/ebbe.html geschrieben führt es " +
				"auf eine Seite der Website; so wie geschrieben funktioniert es nur auf einer Seite der obersten Ebene.",
				LogSentences.Evidence(De, english));
		}

		[Theory]
		[InlineData("declared Content-Length 10 != actual 5 bytes (truncated or corrupt download)",
			"angegebene Content-Length 10 ≠ tatsächlich 5 Byte (Download abgeschnitten oder Datei beschädigt)")]
		[InlineData("2000000 bytes exceeds threshold 1000000", "2000000 Byte, mehr als die Grenze von 1000000")]
		[InlineData("1x1 px (tracking-pixel or degenerate image)", "1x1 px (Zählpixel oder Bild ohne sichtbaren Inhalt)")]
		[InlineData("5000x3000 px exceeds 4000 px on a side", "5000x3000 px, mehr als 4000 px auf einer Seite")]
		[InlineData("GPS, camera, author present (3 fields)", "GPS, Kamera, Urheber vorhanden (3 Felder)")]
		[InlineData("copyright, device-id, software present (1 field)", "Copyright, Geräte-ID, Software vorhanden (1 Feld)")]
		[InlineData("metadata present (2 fields)", "Metadaten vorhanden (2 Felder)")]
		public void AssetFinding_InGerman(string english, string german) =>
			Assert.Equal(german, LogSentences.AssetFinding(De, english));

		[Fact]
		public void CanonicalDetail_AsTheAnalyzerWritesIt_InGerman()
		{
			var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://site.test/a.html"] = ["https://site.test/b.html", "https://site.test/c.html"], // several, and a chain
				["https://site.test/b.html"] = ["https://site.test/c.html"],
				["https://site.test/d.html"] = ["https://other.test/d.html"],
				["https://site.test/e.html"] = ["https://site.test/gone.html"],
				["https://site.test/f.html"] = ["https://site.test/old.html"],
				["https://site.test/g.html"] = ["https://site.test/ice.html"],
			};
			var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["https://site.test/b.html"] = "b.html",
				["https://site.test/c.html"] = "c.html",
				["https://site.test/ice.html"] = "ice.html",
			};
			var redirects = new Dictionary<string, (string, string)> { ["https://site.test/old.html"] = ("Found", "https://site.test/f.html") };
			var search = new Dictionary<string, string> { ["https://site.test/ice.html"] = "noindex in page" };

			var german = CanonicalAnalyzer.DetectIssues(map, index, "https://site.test", null, redirects, search)
				.ToDictionary(i => i.IssueType + "|" + i.PageUrl, i => LogSentences.CanonicalDetail(De, i.Detail));

			Assert.Equal("2 Canonical-Tags gefunden – nur das erste gilt: https://site.test/b.html, https://site.test/c.html",
				german["CANONICAL_CONFLICT|https://site.test/a.html"]);
			Assert.Equal("Kette: https://site.test/a.html → https://site.test/b.html → https://site.test/c.html",
				german["CANONICAL_CHAIN|https://site.test/a.html"]);
			Assert.Equal("Das Canonical zeigt außerhalb des erlaubten Crawl-Bereichs (site.test) auf: other.test",
				german["CANONICAL_EXTERNAL|https://site.test/d.html"]);
			Assert.Equal("Das Canonical-Ziel ist nicht im Crawler-Index: https://site.test/gone.html",
				german["CANONICAL_404|https://site.test/e.html"]);
			Assert.Equal("Das Canonical-Ziel leitet weiter (Found) auf: https://site.test/f.html",
				german["CANONICAL_REDIRECT|https://site.test/f.html"]);
			Assert.Equal("Das Canonical-Ziel ist von der Suche ausgeschlossen (noindex in der Seite): https://site.test/ice.html",
				german["CANONICAL_NOINDEX|https://site.test/g.html"]);
		}

		[Fact]
		public void EnglishAndUnknown_StayAsTheyAre()
		{
			const string Evidence = ".wave .tide applies on https://site.test/x.html.";
			Assert.Equal(Evidence, LogSentences.Evidence(DocumentLanguage.English, Evidence));
			Assert.Equal("1x1 px (tracking-pixel or degenerate image)", LogSentences.AssetFinding(DocumentLanguage.English, "1x1 px (tracking-pixel or degenerate image)"));
			Assert.Equal("Chain: a → b → c", LogSentences.CanonicalDetail(DocumentLanguage.English, "Chain: a → b → c"));

			Assert.Equal("Something the tide wrote.", LogSentences.Evidence(De, "Something the tide wrote."));
			Assert.Equal("wave", LogSentences.AssetFinding(De, "wave"));
			Assert.Equal("Rain over the ice", LogSentences.CanonicalDetail(De, "Rain over the ice"));
			Assert.Equal(string.Empty, LogSentences.Evidence(De, string.Empty));
			Assert.Equal((15, 6, 6), LogSentences.Count);
		}
	}
}
