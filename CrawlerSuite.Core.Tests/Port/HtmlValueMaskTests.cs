using System.Linq;
using CrawlerSuite.Core.Display;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>Masking URL attribute values inside HTML contexts.</summary>
	public class HtmlValueMaskTests
	{
		private static string[] Values(string html) =>
			HtmlTokens.UrlValueSpans(html).Select(s => html.Substring(s.Start, s.Length)).ToArray();

		[Fact]
		public void Spans_FindUrlAttributesOnly()
		{
			var html = "<div class=\"teaser\"><a class=\"x\" href=\"/de/produkte/\"><img src='/media/a.webp' alt=\"/not/a/url\" data-src=/lazy/b.webp></a></div>";

			Assert.Equal(new[] { "\"/de/produkte/\"", "'/media/a.webp'", "/lazy/b.webp" }, Values(html));
		}

		[Fact]
		public void Spans_PlainTextHasNone()
		{
			Assert.Empty(HtmlTokens.UrlValueSpans("href=\"/x\" is not inside a tag"));
		}

		[Fact]
		public void Spans_CutOffValueAtTheEnd_IsStillFound()
		{
			Assert.Equal(new[] { "\"/de/produkte/konf" }, Values("<b>x</b> <a href=\"/de/produkte/konf"));
		}

		[Theory]
		[InlineData("href")]
		[InlineData("SRC")]
		[InlineData("srcset")]
		[InlineData("action")]
		[InlineData("poster")]
		[InlineData("data-src")]
		[InlineData("data-bg-image")]
		[InlineData("xlink:href")]
		public void UrlAttributes(string name) => Assert.True(HtmlTokens.IsUrlAttribute(name));

		[Theory]
		[InlineData("alt")]
		[InlineData("class")]
		[InlineData("id")]
		[InlineData("title")]
		[InlineData("data-id")]
		public void OtherAttributes(string name) => Assert.False(HtmlTokens.IsUrlAttribute(name));

		[Theory]
		[InlineData(MaskLevel.Clear, "\"/de/produkte/konfigurator/teaser.webp\"")]
		[InlineData(MaskLevel.Host, "\"/de/produkte/konfigurator/teaser.webp\"")]
		[InlineData(MaskLevel.HostAndPath, "\"/•••/•••/•••/teaser.webp\"")]
		[InlineData(MaskLevel.SchemeOnly, "\"/•••\"")]
		public void RelativePath_FollowsTheLevel(MaskLevel level, string expected)
		{
			Assert.Equal(expected, new ScreenMask().ApplyToAttributeValue("\"/de/produkte/konfigurator/teaser.webp\"", level));
		}

		[Fact]
		public void RelativeWithoutSlash_AndQuery()
		{
			Assert.Equal("'•••/page.html?•••'", new ScreenMask().ApplyToAttributeValue("'images/page.html?id=7'", MaskLevel.HostAndPath));
		}

		[Theory]
		[InlineData(MaskLevel.Host, "\"https://•••••/en/a.html\"")]
		[InlineData(MaskLevel.SchemeOnly, "\"https://•••••\"")]
		public void FullUrl_AsAnyUrl(MaskLevel level, string expected)
		{
			Assert.Equal(expected, new ScreenMask().ApplyToAttributeValue("\"https://www.site.test/en/a.html\"", level));
		}

		[Fact]
		public void ProtocolRelative_HostHidden()
		{
			Assert.Equal("\"//•••••/lib/x.js\"", new ScreenMask().ApplyToAttributeValue("\"//cdn.site.test/lib/x.js\"", MaskLevel.Host));
		}

		[Fact]
		public void Srcset_EachUrlMasked_DescriptorsKept()
		{
			Assert.Equal("\"/•••/a-480.webp 480w, /•••/a-960.webp 960w\"",
				new ScreenMask().ApplyToAttributeValue("\"/img/a-480.webp 480w, /img/a-960.webp 960w\"", MaskLevel.HostAndPath));
		}

		[Theory]
		[InlineData("\"mailto:info@site.test\"", "\"mailto:•••\"")]
		[InlineData("\"#main\"", "\"#•••\"")]
		[InlineData("\"data:image/png;base64,iVBOR/w0KGgo=\"", "\"data:•••\"")]
		public void SchemesAndFragments(string value, string expected)
		{
			Assert.Equal(expected, new ScreenMask().ApplyToAttributeValue(value, MaskLevel.HostAndPath));
		}

		[Fact]
		public void Unquoted_AndUnclosed_KeepTheirShape()
		{
			var mask = new ScreenMask();
			Assert.Equal("/•••", mask.ApplyToAttributeValue("/lazy/b.webp", MaskLevel.SchemeOnly));
			Assert.Equal("\"/•••/•••/konf", mask.ApplyToAttributeValue("\"/de/produkte/konf", MaskLevel.HostAndPath));
		}
	
		[Fact]
		public void Apply_PlainTextQuotingAnAnchor_MasksItsHref()
		{
			var detail = "No accessible name — <a class=\"teaser-link\" href=\"/de/produkte/konfigurator/\">";
			Assert.Equal("No accessible name — <a class=\"teaser-link\" href=\"/•••\">",
				new ScreenMask().Apply(detail, MaskLevel.SchemeOnly));
		}

		[Fact]
		public void Apply_CutOffExcerpt_StillMasksTheValue()
		{
			Assert.Equal("…<img src=\"/•••/•••/teas", new ScreenMask().Apply("…<img src=\"/media/de/teas", MaskLevel.HostAndPath));
		}

		[Fact]
		public void Apply_OtherAttributesAndClear_Untouched()
		{
			var mask = new ScreenMask();
			Assert.Equal("<p class=\"intro\" id=\"x\">", mask.Apply("<p class=\"intro\" id=\"x\">", MaskLevel.SchemeOnly));
			Assert.Equal("<a href=\"/a/b\">", mask.Apply("<a href=\"/a/b\">", MaskLevel.Clear));
		}

		[Fact]
		public void Apply_IsStable_WhenAppliedTwice()
		{
			var mask = new ScreenMask();
			var once = mask.Apply("<a href=\"/a/b/c.html?x=1\" src='https://h.test/p/q.png'>", MaskLevel.HostAndPath);
			Assert.Equal(once, mask.Apply(once, MaskLevel.HostAndPath));
		}
	}
}
