using CrawlerSuite.Core.Seo;
using Xunit;

namespace CrawlerSuite.Core.Tests.Seo
{
	/// <summary>The search result preview: text as read, width estimated from letter widths, cut at a word.</summary>
	public class SearchSnippetTests
	{
		[Fact]
		public void EntitiesAreDecoded_AndSpaceCollapsed()
		{
			Assert.Equal("Water & ice", SearchSnippet.Clean("  Water &amp;\r\n  ice "));
			Assert.Equal("Tide – flow", SearchSnippet.Clean("Tide&nbsp;&ndash; flow".Replace("&nbsp;", " ")));
		}

		[Fact]
		public void WideLettersMeasureWiderThanNarrowOnes()
		{
			Assert.True(SearchSnippet.WidthOf("WWWWWWWWWW", 20) > 2 * SearchSnippet.WidthOf("iiiiiiiiii", 20));
			Assert.Equal(0, SearchSnippet.WidthOf(string.Empty, 20));
		}

		[Fact]
		public void AShortTitle_IsShownWhole()
		{
			var s = SearchSnippet.Of("https://site.test/water.html", "Water and ice", "Where the tide goes.");
			Assert.False(s.TitleCut);
			Assert.Equal("Water and ice", s.TitleShown);
			Assert.False(s.DescriptionCut);
		}

		[Fact]
		public void ALongTitle_IsCutAtAWord_WithTheEllipsis_WithinTheLimit()
		{
			var title = string.Join(" ", Enumerable.Repeat("Vapour rising over the cold morning water", 3));
			var s = SearchSnippet.Of("https://site.test/water.html", title, string.Empty);

			Assert.True(s.TitleCut);
			Assert.EndsWith(SearchSnippet.Ellipsis, s.TitleShown);
			Assert.True(SearchSnippet.WidthOf(s.TitleShown, SearchSnippet.TitleFontPixels) <= SearchSnippet.TitleLimitPixels);
			Assert.StartsWith(s.TitleShown[..^SearchSnippet.Ellipsis.Length], title);
			Assert.True(s.TitleWidth > SearchSnippet.TitleLimitPixels);
		}

		[Fact]
		public void ALongDescription_IsCut()
		{
			var text = string.Join(" ", Enumerable.Repeat("Rain falls on the sea and rises again as vapour.", 6));
			var s = SearchSnippet.Of("https://site.test/water.html", "Rain", text);
			Assert.True(s.DescriptionCut);
			Assert.True(SearchSnippet.WidthOf(s.DescriptionShown, SearchSnippet.DescriptionFontPixels) <= SearchSnippet.DescriptionLimitPixels);
		}

		[Fact]
		public void OneOverlongWord_IsCutInside()
		{
			var s = SearchSnippet.Of("https://site.test/water.html", new string('W', 80), string.Empty);
			Assert.True(s.TitleCut);
			Assert.StartsWith("WWW", s.TitleShown);
			Assert.True(SearchSnippet.WidthOf(s.TitleShown, SearchSnippet.TitleFontPixels) <= SearchSnippet.TitleLimitPixels);
		}
	}
}

namespace CrawlerSuite.Core.Tests.Seo
{
	/// <summary>The site's title patterns: the page's own part, and whether a title follows one.</summary>
	public class TitlePatternTests
	{
		private static readonly string[] Suffix = ["{title} | Water Works"];

		[Fact]
		public void NoPattern_TheWholeTitle_Matching()
		{
			Assert.Equal(("Tide", string.Empty, true), TitlePattern.Of("Tide", null));
			Assert.Equal(("Tide", string.Empty, true), TitlePattern.Of("Tide", ["", ""]));
		}

		[Fact]
		public void ASuffixPattern_GivesThePagesOwnPart()
		{
			Assert.Equal(("Tide tables", string.Empty, true), TitlePattern.Of("Tide tables | Water Works", Suffix));
			Assert.Equal((string.Empty, string.Empty, true), TitlePattern.Of(" | Water Works", Suffix));
		}

		[Fact]
		public void APrefixPattern_GivesTheTextBefore()
		{
			Assert.Equal(("Ice", "Water Works | ", true), TitlePattern.Of("Water Works | Ice", ["Water Works | {title}"]));
		}

		[Fact]
		public void ATitleWrittenOtherwise_MatchesNone_TheFirstMatchingPatternWins()
		{
			Assert.False(TitlePattern.Of("Tide tables-Water Works", Suffix).Matches);
			Assert.False(TitlePattern.Of("Tide tables | water works", Suffix).Matches); // exact, case included
			Assert.Equal("Tide", TitlePattern.Of("Tide-Water Works", ["{title} | Water Works", "{title}-Water Works"]).Own);
		}
	}
}
