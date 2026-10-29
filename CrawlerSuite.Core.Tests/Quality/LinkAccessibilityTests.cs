using CrawlerSuite.Core.Quality;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// The accessibility decisions behind MISPLACED_ANCHOR_EMPTY, ARIA_HIDDEN_FOCUSABLE and
	/// ARIA_REFERENCE_BROKEN — one test per agreed case (W3C ACT rules c487ae and 6cfa84).
	/// </summary>
	public class LinkAccessibilityTests
	{
		private static List<QualityIssue> Check(string body, string? raw = null)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml($"<html><body>{body}</body></html>");
			HtmlDocument? Raw()
			{
				if (raw is null)
				{
					return null;
				}

				var r = new HtmlDocument();
				r.LoadHtml(raw);
				return r;
			}

			return MisplacedAnchors.Check("p.html", doc, new ContentQualityConfig(), Raw).ToList();
		}

		private static List<QualityIssue> Of(List<QualityIssue> issues, string type) => issues.Where(i => i.IssueType == type).ToList();

		// ── Decision 1: what counts as a link ────────────────────────────────

		[Fact]
		public void AnchorWithoutHref_IsNotALink_TheBridgeLinkCase()
		{
			var issues = Check("<p>konfigurieren</p><a id=\"klick_komponente_item_885b_bridge_link\" hidden aria-hidden=\"true\"></a>");
			Assert.Empty(issues);
		}

		[Fact]
		public void AnchorWithoutHref_Visible_IsNotALink() => Assert.Empty(Check("<a id=\"top\"></a>"));

		[Theory]
		[InlineData("<a role=\"link\" tabindex=\"0\"></a>")]
		[InlineData("<a role=\"button\"></a>")]
		[InlineData("<a tabindex=\"0\"></a>")]
		public void ScriptedPseudoLink_IsChecked(string markup) =>
			Assert.Single(Of(Check(markup), "MISPLACED_ANCHOR_EMPTY"));

		[Theory]
		[InlineData("<a href=\"\"></a>")]
		[InlineData("<a href=\"#\"></a>")]
		public void EmptyOrHashHref_IsALink(string markup) =>
			Assert.Single(Of(Check(markup), "MISPLACED_ANCHOR_EMPTY"));

		// ── Decision 2: not rendered ─────────────────────────────────────────

		[Theory]
		[InlineData("<a href=\"/x\" hidden></a>")]
		[InlineData("<div hidden><a href=\"/x\"></a></div>")]
		[InlineData("<div style=\"display: none\"><a href=\"/x\"></a></div>")]
		[InlineData("<a href=\"/x\" style=\"visibility:hidden\"></a>")]
		[InlineData("<template><a href=\"/x\"></a></template>")]
		public void NotRendered_IsNotFlagged(string markup) => Assert.Empty(Check(markup));

		[Fact]
		public void NoscriptContent_IsChecked() =>
			Assert.Single(Of(Check("<noscript><a href=\"/x\"></a></noscript>"), "MISPLACED_ANCHOR_EMPTY"));

		// ── Decision 3: aria-hidden ──────────────────────────────────────────

		[Fact]
		public void AriaHiddenWithNegativeTabindex_IsTheAcceptedDuplicatePattern()
		{
			// No violation; the record says why, as evidence.
			var issues = Check("<a href=\"/x\" aria-hidden=\"true\" tabindex=\"-1\"></a>");
			Assert.Empty(Of(issues, "ARIA_HIDDEN_FOCUSABLE"));
			Assert.Contains("tabindex=\"-1\"", Assert.Single(Of(issues, "ARIA_HIDDEN_FOCUSABLE_CLEARED")).Detail);
		}

		[Fact]
		public void AriaHiddenButFocusable_EmptyLink_IsHiddenFocusable_NotEmpty()
		{
			var issues = Check("<a href=\"/x\" aria-hidden=\"true\"></a>");
			Assert.Empty(Of(issues, "MISPLACED_ANCHOR_EMPTY"));
			var hidden = Assert.Single(Of(issues, "ARIA_HIDDEN_FOCUSABLE"));
			Assert.StartsWith("<a href=\"/x\"> is hidden from screen readers", hidden.Detail);
		}

		[Fact]
		public void AriaHiddenButFocusable_LinkWithText_IsReported() =>
			Assert.Single(Of(Check("<a href=\"/x\" aria-hidden=\"true\">Read more</a>"), "ARIA_HIDDEN_FOCUSABLE"));

		[Theory]
		[InlineData("<div aria-hidden=\"true\"><button>Go</button></div>", "<button>")]
		[InlineData("<div aria-hidden=\"true\"><input type=\"text\" name=\"q\"></div>", "<input type=\"text\" name=\"q\">")]
		[InlineData("<div aria-hidden=\"true\"><span tabindex=\"0\">x</span></div>", "<span tabindex=\"0\">")]
		public void AriaHiddenButFocusable_AnyElement_IsReported(string markup, string described) =>
			Assert.StartsWith(described, Assert.Single(Of(Check(markup), "ARIA_HIDDEN_FOCUSABLE")).Detail);

		[Theory]
		[InlineData("<div aria-hidden=\"true\"><button disabled>Go</button></div>")]
		[InlineData("<div aria-hidden=\"true\"><input type=\"hidden\" name=\"t\"></div>")]
		[InlineData("<div aria-hidden=\"true\"><a href=\"/x\" tabindex=\"-1\">x</a></div>")]
		[InlineData("<div aria-hidden=\"true\" hidden><a href=\"/x\">x</a></div>")]
		public void AriaHidden_NotInTabOrder_IsNotReported(string markup) =>
			Assert.Empty(Of(Check(markup), "ARIA_HIDDEN_FOCUSABLE"));

		// ── Decision 4: accessible name ──────────────────────────────────────

		[Theory]
		[InlineData("<a href=\"/x\" aria-label=\"Close\"></a>")]
		[InlineData("<span id=\"l\">Home</span><a href=\"/x\" aria-labelledby=\"l\"></a>")]
		[InlineData("<a href=\"/x\" title=\"Home\"></a>")]
		[InlineData("<a href=\"/x\"><svg><title>Home</title></svg></a>")]
		[InlineData("<a href=\"/x\"><svg aria-label=\"Home\"></svg></a>")]
		[InlineData("<a href=\"/x\"><span>Home</span></a>")]
		public void Named_IsNotFlagged(string markup) => Assert.Empty(Of(Check(markup), "MISPLACED_ANCHOR_EMPTY"));

		[Theory]
		[InlineData("<a href=\"/x\"><span aria-hidden=\"true\">→</span></a>")]
		[InlineData("<a href=\"/x\">&nbsp; </a>")]
		[InlineData("<a href=\"/x\" aria-label=\"  \"></a>")]
		public void TextThatDoesNotCount_IsFlagged(string markup) =>
			Assert.Single(Of(Check(markup), "MISPLACED_ANCHOR_EMPTY"));

		[Fact]
		public void LabelledByMissingId_FallsThrough_AndSaysWhy()
		{
			var issues = Check("<a href=\"/x\" aria-labelledby=\"nav-label\"></a>");
			Assert.Contains("aria-labelledby points to missing id \"nav-label\"", Assert.Single(Of(issues, "MISPLACED_ANCHOR_EMPTY")).Detail);
			Assert.Single(Of(issues, "ARIA_REFERENCE_BROKEN"));
		}

		[Fact]
		public void LabelledByMissingId_WithOtherName_IsOnlyAReferenceIssue()
		{
			var issues = Check("<a href=\"/x\" aria-labelledby=\"nav-label\">Home</a>");
			Assert.Empty(Of(issues, "MISPLACED_ANCHOR_EMPTY"));
			Assert.Equal("aria-labelledby points to missing id \"nav-label\" on <a href=\"/x\">", Assert.Single(Of(issues, "ARIA_REFERENCE_BROKEN")).Detail);
		}

		[Fact]
		public void ReferenceToPrunedBoilerplate_IsFoundInTheRawPage()
		{
			var issues = Check(
				"<a href=\"/x\" aria-labelledby=\"site-title\"></a>",
				raw: "<html><body><header><span id=\"site-title\">Shop</span></header><a href=\"/x\" aria-labelledby=\"site-title\"></a></body></html>");
			Assert.Empty(issues);
		}

		[Fact]
		public void DescribedByMissingIds_OnAnyElement()
		{
			var issue = Assert.Single(Check("<input type=\"text\" aria-describedby=\"hint err\">"));
			Assert.Equal("ARIA_REFERENCE_BROKEN", issue.IssueType);
			Assert.Equal("aria-describedby points to missing ids \"hint\", \"err\" on <input type=\"text\">", issue.Detail);
		}

		// ── Decision 5: image links ──────────────────────────────────────────

		[Theory]
		[InlineData("<a href=\"/x\"><img src=\"logo.png\"></a>", "Image link: the image has no alt attribute")]
		[InlineData("<a href=\"/x\"><img src=\"logo.png\" alt=\"\"></a>", "Image link: the image is marked decorative")]
		[InlineData("<a href=\"/x\"><img src=\"logo.png\" alt=\"Logo\" role=\"presentation\"></a>", "Image link: the image is marked decorative")]
		public void ImageLinkWithoutUsableAlt_IsFlagged(string markup, string detail) =>
			Assert.StartsWith(detail, Assert.Single(Of(Check(markup), "MISPLACED_ANCHOR_EMPTY")).Detail);

		[Theory]
		[InlineData("<a href=\"/x\"><img alt=\"Home\"></a>")]
		[InlineData("<a href=\"/x\"><img alt=\"\">Home</a>")]
		public void ImageLinkWithName_IsNotFlagged(string markup) => Assert.Empty(Of(Check(markup), "MISPLACED_ANCHOR_EMPTY"));
	}
}
