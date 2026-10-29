using CrawlerSuite.Core.Text;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Text
{
	/// <summary>How a ticket words what the engine recorded, for a reader who cannot open the page. Invented texts.</summary>
	public class TicketTextTests
	{
		[Fact]
		public void AnEmptyLinkText_IsSaidInWords()
		{
			Assert.Equal("„Ice“ + (kein Text)", TicketText.Readable("„Ice“ + „“", "(kein Text)"));
			Assert.Equal("(no text) + “floe”", TicketText.Readable("“” + “floe”", "(no text)"));
		}

		[Fact]
		public void ACharacterReference_InAQuotedElement_IsTheCharacter_ProseIsUntouched()
		{
			Assert.Equal("Kein Name – <a href=\"https://site.test/app?id=ice\">",
				TicketText.Readable("Kein Name – <a href=\"https://site.test/app?id&#61;ice\">", "(kein Text)"));
			Assert.Equal("the entity &#61; in prose stays", TicketText.Readable("the entity &#61; in prose stays", "(no text)"));
			Assert.Equal("<img alt=\"\">", TicketText.Readable("<img alt=\"\">", "(no text)"));
		}

		[Theory]
		[InlineData("Lösung, einmal im Template: das Element ändern.", "Einmal im Template: das Element ändern.")]
		[InlineData("Lösung: die Datei ohne BOM speichern.", "Die Datei ohne BOM speichern.")]
		[InlineData("Fix: save the file without a BOM.", "Save the file without a BOM.")]
		[InlineData("Save the file without a BOM.", "Save the file without a BOM.")]
		public void TheFix_LosesItsOwnFixWord(string fix, string shown) =>
			Assert.Equal(shown, TicketText.WithoutFixWord(fix));

		[Fact]
		public void AnExcerptCutInsideATag_StartsAndEndsOnWholeTags()
		{
			var context = "/de/floe.html?x=1\n" +
				"2F%2Fsite.test%2Ffloe&ety=box\" target=\"_blank\" title=\"Weiter zum\n" +
				"!--   --><li class=\"active\"><a href=\"/de/floe.html\">Floe</a></li><!--  --><li class=\"x";

			var tidy = TicketText.WholeTags(context).Split('\n');

			Assert.Equal(2, tidy.Length);
			Assert.Equal("/de/floe.html?x=1", tidy[0]);
			Assert.Equal("…<li class=\"active\"><a href=\"/de/floe.html\">Floe</a></li><!--  -->…", tidy[1]);
		}

		[Fact]
		public void TextExcerpts_AreUntouched()
		{
			const string Text = "…Die Scholle (<FloeId>) treibt 3 > 2 km weit…";
			Assert.Equal(Text, TicketText.WholeTags(Text));
			Assert.Equal("a single line=\"x\" stays when it is all there is", TicketText.WholeTags("a single line=\"x\" stays when it is all there is"));
		}

		[Fact]
		public void TheEngineMarker_BecomesWords_ItsMarkMovesAlong()
		{
			const string Marker = "[Missing link text]";
			var text = "<a>" + Marker + "</a>";
			var marks = new int?[text.Length];
			for (int i = 3; i < 3 + Marker.Length; i++)
			{
				marks[i] = 7;
			}

			var (shown, moved) = TicketText.MarkerInWords(text, marks, Marker, "[Fehlender Linktext]");

			Assert.Equal("<a>[Fehlender Linktext]</a>", shown);
			Assert.Equal(shown.Length, moved.Length);
			Assert.Null(moved[0]);
			Assert.All(moved[3..23], m => Assert.Equal(7, m));
			Assert.Null(moved[23]);
		}

		[Theory]
		[InlineData("p[#text]", "…die Eisscholle treibt…", "…die Eisscholle treibt…")]
		[InlineData("meta[@name=description]", "Alles über Schollen", "Description: Alles über Schollen")]
		[InlineData("meta[@name=keywords]", "Eis, Scholle", "Keywords: Eis, Scholle")]
		[InlineData("meta[@name=robots-floe]", "Eis", "Meta-Tag robots-floe: Eis")]
		[InlineData("img[@alt]", "Eisschole", "alt von <img>: Eisschole")]
		[InlineData("script[L3:12]", "\"hint\": \"eine \\u00e4ltere Scholle\"", "Im Skript der Seite: \"hint\": \"eine ältere Scholle\"")]
		[InlineData("button[@onclick]", "alert('Sch\\x6flle')", "Im Skript der Seite: alert('Scholle')")]
		[InlineData("meta[@name=description]", "", "Description")]
		public void AnOccurrence_SaysWhereItStands_InTheDocumentsLanguage(string source, string excerpt, string german) =>
			Assert.Equal(german, SpellingSource.Shown(source, excerpt, DocumentLanguage.German));

		[Fact]
		public void AnOccurrence_InEnglish()
		{
			Assert.Equal("alt of <img>: Eisschole", SpellingSource.Shown("img[@alt]", "Eisschole", DocumentLanguage.English));
			Assert.Equal("In the page's script: ä", SpellingSource.Shown("script[L1:1]", "\\u00e4", DocumentLanguage.English));
		}
	}
}
