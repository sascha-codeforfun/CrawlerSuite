using CrawlerSuite.Core.Quality;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>A word written twice in a row; invented sentences.</summary>
	public class DoubledWordsTests
	{
		private static readonly ContentQualityConfig Config = new();

		private static List<QualityIssue> Scan(string text, string language = "de") =>
			DoubledWords.Scan("f.html", text, "p", [language], Config).ToList();

		private static List<QualityIssue> Page(string body, string lang = "de")
		{
			var doc = new HtmlDocument();
			doc.LoadHtml($"<html lang=\"{lang}\"><body>{body}</body></html>");
			return DoubledWords.Check("f.html", doc, Config, null, lang).ToList();
		}

		[Theory]
		[InlineData("Wir bauen das Haus Haus für Haus.")]
		[InlineData("Wir bauen das Haus\nHaus für Haus.")]
		[InlineData("Wir bauen das Haus Haus für Haus.")]
		public void AWordTwice_IsReported(string text)
		{
			var issue = Assert.Single(Scan(text));
			Assert.Equal("WORD_DOUBLED", issue.IssueType);
			Assert.EndsWith("“Haus Haus”", issue.Detail);
		}

		[Theory]
		[InlineData("Haus, Haus und Garten.")]          // punctuation: meant
		[InlineData("Am 20 20 Uhr.")]                    // numbers are not judged
		[InlineData("Haus 20 Haus.")]                    // a number between breaks the pair
		[InlineData("Ich weiß, dass die die Akte haben.")] // listed
		[InlineData("Doch doch, das stimmt so.")]         // listed, at the start of a sentence
		[InlineData("Wir Leben leben gern.")]            // capital, then small: noun and verb
		public void Meant_OrListed_IsNotReported(string text) => Assert.Empty(Scan(text));

		[Fact]
		public void German_AtTheStartOfASentence_TheCapitalDoesNotSaveIt()
		{
			Assert.Single(Scan("Gut. Garten garten wir."));
			Assert.Empty(Scan("Gut. Die die kamen, blieben."));
		}

		[Fact]
		public void English_CaseDoesNotMatter_AndItsOwnListApplies()
		{
			Assert.Single(Scan("We saw the The garden.", "en"));
			Assert.Empty(Scan("If I had had time.", "en"));
			Assert.Single(Scan("This is is wrong.", "en"));
		}

		[Fact]
		public void ALanguageWithoutAList_ReportsEveryDoubling()
		{
			Assert.Single(Scan("la la la", "xx").Take(1));
			Assert.Single(Scan("ich die die Akte", "xx"));
		}

		[Fact]
		public void InlineFormatting_DoesNotHideIt_AndTheExcerptIsThePagesHtml()
		{
			var issue = Assert.Single(Page("<p>Wir bauen das <b>Haus</b> Haus für Haus.</p>"));
			Assert.Contains("<b>Haus</b> Haus", issue.Context);
			var spot = DoubledWords.Spot(issue.Context, issue.Detail);
			Assert.NotNull(spot);
			Assert.Equal("Haus", spot!.Value.Second.Value);
		}

		[Fact]
		public void ATermBeforeALineBreak_RepeatedAfterIt_IsWeakPunctuation_NotADoubledWord()
		{
			var issue = Assert.Single(Page("<ul><li>Gartenhaus<br /> Gartenhaus heißt auch Laube.</li></ul>"));
			Assert.Equal("PUNCTUATION_WEAK", issue.IssueType);
			Assert.Contains("“Gartenhaus:”", issue.Detail);
			var spot = DoubledWords.Spot(issue.Context, issue.Detail);
			Assert.NotNull(spot);
			Assert.Contains("<br", issue.Context[spot!.Value.First.Index..spot.Value.Second.Index]);

			// With the colon it reads right; blocks do not run together.
			Assert.Empty(Page("<ul><li>Gartenhaus:<br /> Gartenhaus heißt auch Laube.</li></ul>"));
			Assert.Empty(Page("<p>Wir bauen das Haus</p><p>Haus für Haus.</p>"));
		}

		[Fact]
		public void ANameWithAnUnderscore_IsNoWord()
		{
			Assert.Empty(Scan("The XYZ_TOKEN token keeps the session.", "en"));
			Assert.Empty(Scan("Wir setzen HAUS_1 Haus ein."));
		}

		[Fact]
		public void RegionsWithARole_AreReadOnTheirOwn()
		{
			Assert.Empty(Page("<div><span role=\"status\" aria-live=\"polite\">Leer</span> <span role=\"status\">Leer</span></div>"));
			Assert.Empty(Page("<p>Der Wert <span aria-live=\"polite\">steht</span> steht fest.</p>"));
			// Plain inline formatting still joins.
			Assert.Single(Page("<p>Der Wert <span>steht</span> steht fest.</p>"));
		}

		[Fact]
		public void ADoubledWordInRunningText_IsStillFound()
		{
			var issue = Assert.Single(Page("<p>The cookie used here is is called a token and expires.</p>", "en"));
			Assert.Equal("WORD_DOUBLED", issue.IssueType);
			Assert.EndsWith("“is is”", issue.Detail);
		}
	}
}
