using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// An apostrophe typed with the wrong glyph (‘ ´ `) is reported as such, not as an orphan
	/// quote; German single quotes are ‚…‘ and pair correctly.
	/// </summary>
	public class WrongApostropheTests
	{
		private const string Type = "QUOTE_WRONG_APOSTROPHE";

		private static ContentQualityConfig Config() => new()
		{
			ContentQualityExcerptRadius = 120,
			ContentQualityQuoteFullSentence = false,
			ContentQualityMaxExcerpt = 400,
		};

		private static List<QualityIssue> Check(string text, string lang = "de") =>
			Quotes.CheckPairing("f.html", text, Config(), lang).ToList();

		[Theory]
		[InlineData("Lauf‘ los und probier es.")]          // ‘ word-final
		[InlineData("Komm´ rein, es ist offen.")]          // ´ word-final
		[InlineData("Wie geht‘s dir heute?")]              // ‘ before an elision
		[InlineData("Wie geht´s dir heute?")]              // ´ before an elision
		[InlineData("Ich hab`s gesehen.")]                 // ` between letters
		public void WrongGlyph_InApostrophePlace_IsWrongApostrophe(string text)
		{
			var issues = Check(text);
			var issue = Assert.Single(issues);
			Assert.Equal(Type, issue.IssueType);
			Assert.Contains("U+2019", issue.Detail);
		}

		[Fact]
		public void WordFinalOpeningQuote_IsNotReportedAsUnmatched()
		{
			var issues = Check("Lauf‘ los.");
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_UNMATCHED");
		}

		[Theory]
		[InlineData("Wie geht’s dir heute?")]
		[InlineData("Sie sagte ‚komm mit‘ und ging.")]   // correct German single pair
		[InlineData("Der Befehl `start` hilft.")]        // backticks around code
		[InlineData("Ein Akzent ´ allein.")]                  // lone accent, no apostrophe place
		public void RightApostropheOrNoApostrophePlace_NoFinding(string text) =>
			Assert.Empty(Check(text));

		[Fact]
		public void GermanSingleOpener_ClosedWithEnglishCloser_IsWrongClose()
		{
			var issues = Check("Sie sagte ‚komm mit’ und ging.");
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_CLOSE");
		}

		[Fact]
		public void FrenchElision_WithOpeningQuote_IsWrongApostrophe()
		{
			var issues = Check("C’est l‘ami du jardin.", "fr");
			var issue = Assert.Single(issues);
			Assert.Equal(Type, issue.IssueType);
		}

		[Fact]
		public void EnglishQuote_StillPairs()
		{
			Assert.Empty(Check("She said ‘hello’ twice.", "en"));
		}

		[Fact]
		public void LocateFlags_PointsAtTheWrongGlyph()
		{
			var text = "Komm´ rein.";
			var flags = Quotes.LocateFlags(text, Config(), "de");
			var flag = Assert.Single(flags);
			Assert.Equal(4, flag.Pos);
			Assert.Equal(Type, flag.Type);
		}
	}
}
