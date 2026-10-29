using CrawlerSuite.Core.Quality;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// Word collisions next to superscript, subscript and line breaks; a quotation opened and
	/// closed with the same mark; glue after a dash that opens its line. Invented text.
	/// </summary>
	public class SweepRulesTests
	{
		private static ContentQualityConfig Config() => new()
		{
			ContentQualityExcerptRadius = 120,
			ContentQualityQuoteFullSentence = false,
			ContentQualityMaxExcerpt = 400,
		};

		private static List<QualityIssue> Collisions(string body)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml($"<html><body>{body}</body></html>");
			return DefectWordCollisions.CheckWordCollisions("f.html", doc, Config()).ToList();
		}

		// ── Word collisions ──────────────────────────────────────────────

		[Theory]
		[InlineData("<p>Pure water is H<span><sub>2</sub></span>O at room temperature.</p>")]
		[InlineData("<p>The brand Widget<span class=\"small\"><sup>TM</sup></span> is registered.</p>")]
		[InlineData("<p>The garden study<span><sup>Note 1</sup></span> counted bees.</p>")]
		public void SuperscriptAndSubscript_AreNoCollision(string body) =>
			Assert.Empty(Collisions(body));

		[Fact]
		public void ABreakAtTheSeam_IsNoCollision()
		{
			Assert.Empty(Collisions("<div><span>Garden party<br></span>Bring a chair and a smile.</div>"));
			Assert.Empty(Collisions("<div>Weather today<span><br>Sunny with light wind</span></div>"));
		}

		[Theory]
		// A span styled as a heading: CSS may put it on its own line, but every reader without
		// the stylesheet sees the words merged.
		[InlineData("<p><span class=\"h2\">Overview</span>Details follow below.</p>")]
		// Text only partly in superscript still merges.
		[InlineData("<p>The brand Widget<span>Pro<sup>TM</sup></span> is registered.</p>")]
		public void MergedWords_StayCollisions(string body) =>
			Assert.Single(Collisions(body));

		// ── Same mark at both ends ───────────────────────────────────────

		private static List<QualityIssue> Quotes(string text, string? lang) =>
			CrawlerSuite.Core.Quality.Quotes.CheckPairing("f.html", text, Config(), lang).ToList();

		[Fact]
		public void German_SameMarkBothEnds_IsAWrongOpener()
		{
			var issue = Assert.Single(Quotes("Das Kapitel “Ende“ fehlt noch.", "de"));
			Assert.Equal("QUOTE_WRONG_OPEN", issue.IssueType);
			Assert.Contains("should be '„'", issue.Detail);
		}

		[Fact]
		public void English_SameMarkBothEnds_IsAWrongCloser()
		{
			var issue = Assert.Single(Quotes("The chapter “Ending“ is still missing.", "en"));
			Assert.Equal("QUOTE_WRONG_CLOSE", issue.IssueType);
			Assert.Contains("should be '”'", issue.Detail);
		}

		[Fact]
		public void SeveralPageLanguages_TheFirstNamesTheEnd()
		{
			// A page declared "de, en": no single anchor for the other checks, but the same mark at
			// both ends is named as the first language reads it.
			var german = Assert.Single(CrawlerSuite.Core.Quality.Quotes.CheckPairing("f.html", "Das Kapitel “Ende“ fehlt noch.", Config(),
				pageLanguage: null, sameMarkLanguage: "de").ToList());
			Assert.Equal("QUOTE_WRONG_OPEN", german.IssueType);
			Assert.Contains("should be '„'", german.Detail);

			var english = Assert.Single(CrawlerSuite.Core.Quality.Quotes.CheckPairing("f.html", "The chapter “Ending“ is missing.", Config(),
				pageLanguage: null, sameMarkLanguage: "en").ToList());
			Assert.Equal("QUOTE_WRONG_CLOSE", english.IssueType);
		}

		[Fact]
		public void ASinglePageLanguage_StillDecides()
		{
			var issue = Assert.Single(CrawlerSuite.Core.Quality.Quotes.CheckPairing("f.html", "Das Kapitel “Ende“ fehlt noch.", Config(),
				pageLanguage: "de", sameMarkLanguage: "en").ToList());
			Assert.Equal("QUOTE_WRONG_OPEN", issue.IssueType);
		}

		[Fact]
		public void NestedQuotationThatPairs_IsUntouched() =>
			Assert.Empty(Quotes("He said “read the note “Exit” first” and left.", "en"));

		[Fact]
		public void ALoneOpener_StaysUnmatched() =>
			Assert.Equal("QUOTE_UNMATCHED", Assert.Single(Quotes("Das Kapitel „Ende fehlt noch.", "de")).IssueType);

		// ── Glue after a dash opening its line ───────────────────────────

		private static List<QualityIssue> Inert(string body)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml($"<html><body>{body}</body></html>");
			return NoBreakChars.Check("f.html", doc, Config())
				.Where(i => i.IssueType == NoBreakChars.InertIssueType).ToList();
		}

		[Theory]
		[InlineData("<p>–&nbsp;Footnote text</p>")]
		[InlineData("<p>Shopping list:<br>\n-&nbsp;apples<br>\n-&nbsp;pears</p>")]
		public void ADashOpeningItsLine_IsNotReported(string body) =>
			Assert.Empty(Inert(body));

		[Theory]
		[InlineData("<p>The cake&nbsp;–&nbsp;still warm&nbsp;– was gone.</p>")]
		[InlineData("<p>Both pre-&nbsp;and post-war houses.</p>")]
		public void ADashInsideTheLine_IsStillReported(string body) =>
			Assert.Single(Inert(body));
	}
}
