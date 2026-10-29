using CrawlerSuite.Core.Display;
using CrawlerSuite.Core.Findings;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>Status codes, link defects, HTML highlighting, redirect chains, number grouping, snapshot folders.</summary>
	public class DisplayHelpersTests
	{
		[Theory]
		[InlineData("Found", 302)]
		[InlineData("MovedPermanently", 301)]
		[InlineData("Moved", 301)]
		[InlineData("RedirectKeepVerb", 307)]
		[InlineData("PermanentRedirect", 308)]
		[InlineData("NotFound", 404)]
		[InlineData("404", 404)]
		public void HttpCodes_ParseNamesAndNumbers(string text, int expected) => Assert.Equal(expected, HttpCodes.Parse(text));

		[Theory]
		[InlineData("N/A")]
		[InlineData("")]
		[InlineData("12")]
		[InlineData("HttpRequestException")]
		public void HttpCodes_RejectNonStatus(string text) => Assert.Null(HttpCodes.Parse(text));

		[Fact]
		public void HttpCodes_DescribeAndKind()
		{
			Assert.StartsWith("302 Found — A temporary move", HttpCodes.Describe(302));
			Assert.Equal("200 OK", HttpCodes.Describe(200));
			Assert.Equal("permanent", HttpCodes.KindWord(308));
			Assert.Equal("temporary", HttpCodes.KindWord(303));
			Assert.Equal(string.Empty, HttpCodes.KindWord(404));
		}

		[Theory]
		[InlineData("https://a.test/page.html.html", "Doubled extension")]
		[InlineData("https://a.test/doc.PDF.pdf?x=1", "Doubled extension")]
		[InlineData("https://a.test/en//products/", "Double slash in the path")]
		[InlineData("https://a.test/en/undefined", "Script leftover in the URL")]
		[InlineData("https://a.test/{{slug}}/", "Script leftover in the URL")]
		[InlineData("https://a.test/my page.html", "Whitespace in the URL")]
		[InlineData("https://a.test/page.html?a=1?b=2", "Two '?' in one URL")]
		public void UrlDefects_FindsTheClassics(string url, string defect) => Assert.Contains(defect, UrlDefects.Find(url));

		[Theory]
		[InlineData("https://a.test/en/products/widget.html?a=1&b=2#top")]
		[InlineData("https://a.test/nullable-things/")]
		[InlineData("https://a.test/")]
		public void UrlDefects_LeavesCleanUrlsAlone(string url) => Assert.Empty(UrlDefects.Find(url));

		[Fact]
		public void HtmlTokens_ColoursATag()
		{
			Assert.True(HtmlTokens.LooksLikeHtml("text <a href=\"/x\">link</a>"));
			Assert.False(HtmlTokens.LooksLikeHtml("a < b and c > d"));

			var tokens = HtmlTokens.Tokenize("<a href=\"/x\" class=y>Hi&nbsp;</a><!-- c -->");

			Assert.Contains((HtmlTokenKind.Tag, "a"), tokens);
			Assert.Contains((HtmlTokenKind.AttributeName, "href"), tokens);
			Assert.Contains((HtmlTokenKind.AttributeValue, "\"/x\""), tokens);
			Assert.Contains((HtmlTokenKind.AttributeValue, "y"), tokens);
			Assert.Contains((HtmlTokenKind.Entity, "&nbsp;"), tokens);
			Assert.Contains((HtmlTokenKind.Comment, "<!-- c -->"), tokens);
			Assert.Equal("<a href=\"/x\" class=y>Hi&nbsp;</a><!-- c -->", string.Concat(tokens.Select(t => t.Text)));
		}

		[Theory]
		[InlineData("…ass=\"x\">text <div class=\"open")]
		[InlineData("<")]
		[InlineData("a & b < c")]
		public void HtmlTokens_CutExcerptsLoseNothing(string text) =>
			Assert.Equal(text, string.Concat(HtmlTokens.Tokenize(text).Select(t => t.Text)));

		[Fact]
		public void RedirectChain_OneHopPerLine_WithEnding()
		{
			var hops = RedirectChain.Parse("https://a.test/b > Found https://a.test/b -> OK https://a.test/c > (Loop)");

			Assert.Equal(2, hops.Count);
			Assert.Equal(new RedirectHop("https://a.test/b", 302, null), hops[0]);
			Assert.Equal(new RedirectHop("https://a.test/c", 200, "Loop"), hops[1]);
		}

		[Fact]
		public void RedirectChain_TargetOnly() =>
			Assert.Equal(new RedirectHop("https://a.test/b", null, null), RedirectChain.Parse("https://a.test/b").Single());

		[Fact]
		public void NumberText_GroupsWithNarrowSpace()
		{
			Assert.Equal("1 234 567", NumberText.Cell("1234567"));
			Assert.Equal("12", NumberText.Cell("12"));
			Assert.Equal("-1", NumberText.Cell("-1"));
			Assert.Equal("n/a", NumberText.Cell("n/a"));
		}

		[Fact]
		public void SnapshotLocator_FolderNameMatchesTheCrawl() =>
			Assert.Equal("https___www_a_test", SnapshotLocator.FolderName("https://www.a.test"));
	}
}
