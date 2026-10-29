using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrawlerSuite.Core.Debugging;
using CrawlerSuite.Core.Pdf;
using CrawlerSuite.Core.Tests.Pdf;
using Xunit;

namespace CrawlerSuite.Core.Tests.Debugging
{
	/// <summary>Debug mode's ticket sweep: folders, shapes, the cheap checks, the report and the comparison. Invented texts.</summary>
	public class TicketSweepTests
	{
		private static SweepTicket Ticket(string file, string shape, int items = 1, string[]? checks = null, long bytes = 30_000, int pages = 2) =>
			new(file, "https://site.test/" + file, pages, bytes, items, shape, checks ?? []);

		[Fact]
		public void Sweeps_SitApartFromRuns_AndAreNumberedLikeThem()
		{
			var archive = Path.Combine(Path.GetTempPath(), "sweep-" + Guid.NewGuid().ToString("N"));
			try
			{
				Directory.CreateDirectory(Path.Combine(archive, "2.0.164-001"));
				var sweeps = TicketSweep.Folder(archive);
				Assert.Equal("2.0.165-001", TicketSweep.NextName(sweeps, "2.0.165"));

				Directory.CreateDirectory(Path.Combine(sweeps, "2.0.165-001"));
				Assert.Equal("2.0.165-002", TicketSweep.NextName(sweeps, "2.0.165"));
				Directory.CreateDirectory(Path.Combine(sweeps, "2.0.165-002"));
				Assert.Equal("2.0.165-001", TicketSweep.Previous(sweeps, "2.0.165-002"));
				Assert.Null(TicketSweep.Previous(sweeps, "2.0.165-001"));

				// The sweeps folder is never taken for a run of the snapshot.
				Assert.Equal(["2.0.164-001"], RunArchive.Runs(archive));
			}
			finally
			{
				Directory.Delete(archive, recursive: true);
			}
		}

		[Fact]
		public void UniqueNames_CountOn()
		{
			var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Assert.Equal("floe", TicketSweep.UniqueName("floe", taken));
			Assert.Equal("floe-2", TicketSweep.UniqueName("floe", taken));
			Assert.Equal("FLOE-3", TicketSweep.UniqueName("FLOE", taken)); // the stem as given, the count case-blind
		}

		[Fact]
		public void TheShape_IsTheKinds_InAFixedOrder()
		{
			var a = TicketSweep.ShapeOf([("Spelling", "Misspelled words"), ("Quotes", "Wrong closer"), ("Spelling", "Misspelled words")]);
			var b = TicketSweep.ShapeOf([("Quotes", "Wrong closer"), ("Spelling", "Misspelled words")]);

			Assert.Equal("Quotes › Wrong closer + Spelling › Misspelled words", a);
			Assert.Equal(a, b);
			Assert.Equal("(no items)", TicketSweep.ShapeOf([]));
		}

		[Fact]
		public void OneRepresentativePerShape_AFlaggedOneFirst_ElseTheRichest()
		{
			var picked = TicketSweep.Representatives(
			[
				Ticket("a.pdf", "S", items: 2),
				Ticket("b.pdf", "S", items: 5),
				Ticket("c.pdf", "Q", items: 9),
				Ticket("d.pdf", "Q", items: 1, checks: ["size 900 KB (budget 400 KB)"]),
			]).Select(t => t.File).Order(StringComparer.Ordinal);

			Assert.Equal(["b.pdf", "d.pdf"], picked);
		}

		[Fact]
		public void Size_AboveTheBudget()
		{
			Assert.Null(TicketSweep.Size(TicketSweep.SizeBudget));
			Assert.StartsWith("size 8000 KB", TicketSweep.Size(8_192_000));
		}

		[Theory]
		[InlineData("im Feld „Floe Id“ ( ) eintragen", "( )")]
		[InlineData("die Liste [] ist leer", "[]")]
		[InlineData("das Wort „ “ fehlt", "„ “")]
		[InlineData("the word “” is gone", "“”")]
		public void EmptyBracketsAndQuotes_AreFound(string text, string form)
		{
			var found = TicketSweep.EmptyPairs(text);

			Assert.NotNull(found);
			Assert.Contains(form, found);
		}

		[Fact]
		public void AFunctionsBrackets_AreNotEmptyText()
		{
			Assert.Null(TicketSweep.EmptyPairs("ein url() in einem Stylesheet, ein Call foo() davor"));
			Assert.NotNull(TicketSweep.EmptyPairs("ein Feld ( ) leer"));
		}

		[Fact]
		public void PageText_HasScriptsAndAttributes_TooSoAnExcerptFromThemIsFound()
		{
			var page = TicketSweep.PageText("<p>Eis</p><script>var hint = 'Schollen treiben weit';</script><input placeholder=\"Eisscholle suchen hier\">");

			Assert.Null(TicketSweep.MissingExcerpts(["var hint = 'Schollen treiben weit';"], page));
			Assert.Null(TicketSweep.MissingExcerpts(["placeholder of <input>: Eisscholle suchen hier"], page));
		}

		[Fact]
		public void TheExcerptCheck_IgnoresSpaces_PlaceLabels_AndScriptStrings()
		{
			var page = TicketSweep.PageText("<p><sup>1</sup>Quelle: die Scholle treibt</p><meta name=\"description\" content=\"Alles \u00FCber Schollen und Eis\">");

			Assert.Null(TicketSweep.MissingExcerpts(["1Quelle: die Scholle treibt"], page));
			Assert.Null(TicketSweep.MissingExcerpts(["Description: Alles \u00FCber Schollen und Eis"], page));
			Assert.Null(TicketSweep.MissingExcerpts(["In the page's script: \"hint\": \"eine \u00E4ltere Scholle\""], page));
		}

		[Fact]
		public void ASentenceWithLinksInside_ReadsOn_AndNoAttributeLandsInIt()
		{
			// The attribute values of the link and the span must not land between the sentence's pieces.
			var page = TicketSweep.PageText(
				"<p>Die <a href=\"https://site.test/floe\" title=\"Zur Scholle\">Scholle</a> treibt<sup class=\"note\">2</sup> " +
				"<span lang=\"en\">weit</span> nach Norden.</p>");

			Assert.Null(TicketSweep.MissingExcerpts(["Die Scholle treibt2 weit nach Norden."], page));
			Assert.Null(TicketSweep.MissingExcerpts(["title of <a>: Zur Scholle"], page));
			// Two attribute values are no sentence: an excerpt found only across them is missing.
			Assert.NotNull(TicketSweep.MissingExcerpts(["https://site.test/floeZur Scholle"], page));
		}

		[Fact]
		public void FilledBracketsAndQuotes_AreNot()
		{
			Assert.Null(TicketSweep.EmptyPairs("im Feld „Floe Id“ (<FloeId>) eintragen, [siehe oben] und “ice”"));
		}

		[Fact]
		public void VisibleText_HasTheBodyMetaAndAltTexts_NotScripts()
		{
			var text = TicketSweep.VisibleText(
				"<html><head><title>Eis und Wasser</title><meta name=\"description\" content=\"Alles über Schollen\">" +
				"<script>var hidden = 'Geheimnis';</script></head>" +
				"<body><p>Im Feld (&lt;FloeId&gt;) steht die Nummer.</p><img alt=\"Eine Scholle\"></body></html>");

			Assert.Contains("Eis und Wasser", text);
			Assert.Contains("Alles über Schollen", text);
			Assert.Contains("Im Feld (<FloeId>) steht die Nummer.", text);
			Assert.Contains("Eine Scholle", text);
			Assert.DoesNotContain("Geheimnis", text);
		}

		[Fact]
		public void AnExcerptThatLostText_IsFound_AWholeOneIsNot()
		{
			var visible = TicketSweep.VisibleText("<p>Die Nummer steht im Feld „Floe Idd“ (&lt;FloeId&gt;) der Datei, wie vereinbart.</p>");

			Assert.Null(TicketSweep.MissingExcerpts(["…steht im Feld „Floe Idd“ (<FloeId>) der Datei…"], visible));
			var missing = TicketSweep.MissingExcerpts(["…steht im Feld „Floe Idd“ ( ) der Datei…"], visible);
			Assert.NotNull(missing);
			Assert.StartsWith("excerpt not in the page's text: 1 piece(s)", missing);
		}

		[Fact]
		public void ShortPiecesAndNbspDifferences_DoNotCount()
		{
			var visible = TicketSweep.VisibleText("<p>Eis&nbsp;und Wasser sind zwei Formen.</p>");

			Assert.Null(TicketSweep.MissingExcerpts(["Eis und Wasser sind zwei Formen."], visible));
			Assert.Null(TicketSweep.MissingExcerpts(["…kurz…"], visible));
		}

		[Fact]
		public void QuoteAdvice_AgainstThePagesOwnQuotes_IsFound()
		{
			var german = TicketSweep.VisibleText("<p>Das „Eis“ und das „Wasser“, dann “Dampf“.</p>");
			const string English = "Closer '“' should be '”' (English-double): the quotation opens and closes with the same mark '“'";
			const string German = "Opener '“' should be '„' (German-double): the quotation opens and closes with the same mark '“'";

			Assert.NotNull(TicketSweep.QuoteAdvice([English], german));
			Assert.Null(TicketSweep.QuoteAdvice([German], german));

			var english = TicketSweep.VisibleText("<p>The “ice” and the “water”.</p>");
			Assert.Null(TicketSweep.QuoteAdvice([English], english));
			Assert.NotNull(TicketSweep.QuoteAdvice([German], english));
		}

		[Fact]
		public void FontTables_ATableAPdfDoesNotUse_IsNamed()
		{
			var heavy = TestFont.Build([8, 12], extra: [("COLR", new byte[64]), ("hhea", new byte[36])]);
			PdfFontData Font(byte[] program) => new()
			{
				BaseName = "XyzzySans-Regular",
				FontProgram = program,
				IsSubset = true,
				Widths = new SortedDictionary<ushort, int> { [1] = 500 },
				Unicode = new SortedDictionary<ushort, string> { [1] = "a" },
				Ascent = 900,
				Descent = -200,
				CapHeight = 700,
			};

			byte[] Pdf(byte[] program)
			{
				var doc = new PdfDocumentBuilder("CrawlerSuite");
				doc.AddFont(Font(program));
				doc.AddPage(595.28, 841.89, new PdfContentBuilder().ToBytes());
				return doc.Build();
			}

			Assert.Equal("font tables a PDF does not use: COLR", TicketSweep.FontTables(Pdf(heavy)));
			Assert.Null(TicketSweep.FontTables(Pdf(SfntTables.KeepForPdf(heavy))));
		}

		[Fact]
		public void TheReport_CountsChecksByKind_AndListsRepresentativesFirst()
		{
			var tickets = new[]
			{
				Ticket("floe.pdf", "Spelling › Misspelled words", items: 3, checks: ["excerpt not in the page's text: 1 piece(s), e.g. ‚( )‘"]),
				Ticket("frost.pdf", "Spelling › Misspelled words", items: 1),
				Ticket("tide.pdf", "Quotes › Wrong closer", checks: ["quote advice against the page's own quotes (3 „, 0 ”): ‚”‘ (English-double)"]),
				new SweepTicket("vapour.pdf", "https://site.test/vapour", 0, 0, 2, "Links › Broken", [], "the layout failed"),
			};

			var md = TicketSweep.Report("2026-10-25-01-05-27", "2.0.165", new DateTime(2026, 10, 26), tickets, cancelled: false);

			Assert.Contains("4 ticket(s), 3 PDF(s), 3 shape(s), 3 with a check finding or an error.", md);
			Assert.Contains("| excerpt not in the page's text | 1 | floe.pdf |", md);
			Assert.Contains("| quote advice against the page's own quotes | 1 | tide.pdf |", md);
			Assert.Contains("| no PDF | 1 | vapour.pdf |", md);
			Assert.Contains("the triage ledger is not touched", md);
			Assert.True(md.IndexOf("## Read first", StringComparison.Ordinal) < md.IndexOf("## Every ticket", StringComparison.Ordinal));
			Assert.DoesNotContain("Stopped before the end", md);
		}

		[Fact]
		public void TheComparison_NamesWhatChanged()
		{
			var before = TicketSweep.Table([
				Ticket("floe.pdf", "S", bytes: 8_000_000, pages: 3, checks: ["size 7813 KB (budget 400 KB)"]),
				Ticket("frost.pdf", "S", bytes: 30_000),
				Ticket("gone.pdf", "Q"),
			]);
			var now = TicketSweep.Table([
				Ticket("floe.pdf", "S", bytes: 150_000, pages: 3),
				Ticket("frost.pdf", "S", bytes: 31_000),
				Ticket("new.pdf", "Q"),
			]);

			var md = TicketSweep.Compare("2.0.164-001", before, "2.0.165-001", now);

			Assert.Contains("3 ticket(s) changed.", md);
			Assert.Contains("| new.pdf | new |", md);
			Assert.Contains("| gone.pdf | gone |", md);
			Assert.Contains("| floe.pdf | size 7813 KB → 146 KB | | size → – |", md);
			Assert.DoesNotContain("frost.pdf", md);
		}

		[Fact]
		public void TheComparison_OfTwoEqualSweeps_SaysSo()
		{
			var table = TicketSweep.Table([Ticket("floe.pdf", "S")]);

			Assert.Contains("No ticket changed", TicketSweep.Compare("a", table, "b", table));
		}
	}
}
