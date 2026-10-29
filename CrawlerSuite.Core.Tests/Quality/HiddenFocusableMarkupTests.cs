using CrawlerSuite.Core.Quality;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// aria-hidden with focusable content: what the markup alone shows. A finding is impossible when
	/// something in the element or an ancestor takes it out of the Tab order; that is recorded as
	/// cleared, with the reason as evidence. Invented markup.
	/// </summary>
	public class HiddenFocusableMarkupTests
	{
		private static List<QualityIssue> Check(string body)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml($"<html><body>{body}</body></html>");
			return MisplacedAnchors.Check("f.html", doc, new ContentQualityConfig()).ToList();
		}

		private static string Verdict(string body)
		{
			var issues = Check(body).Where(i => i.IssueType.StartsWith("ARIA_HIDDEN_FOCUSABLE", StringComparison.Ordinal)).ToList();
			return issues.Count == 0 ? "none" : issues.Count == 1 ? issues[0].IssueType : "several";
		}

		[Theory]
		[InlineData("<div aria-hidden=\"true\" hidden><button>Go</button></div>", "hidden attribute")]
		[InlineData("<div aria-hidden=\"true\" inert><a href=\"/x\">Go</a></div>", "inert")]
		[InlineData("<div aria-hidden=\"true\" style=\"color:red; display: none\"><button>Go</button></div>", "display:none")]
		[InlineData("<div aria-hidden=\"true\" style=\"visibility:collapse\"><button>Go</button></div>", "visibility:collapse")]
		[InlineData("<section style=\"content-visibility:hidden\"><input aria-hidden=\"true\" type=\"checkbox\"></section>", "content-visibility:hidden")]
		[InlineData("<details><summary>More</summary><a href=\"/x\" aria-hidden=\"true\">Go</a></details>", "closed <details>")]
		[InlineData("<input aria-hidden=\"true\" type=\"checkbox\" disabled>", "disabled")]
		[InlineData("<fieldset disabled><legend>A</legend><input aria-hidden=\"true\" type=\"text\"></fieldset>", "disabled <fieldset>")]
		[InlineData("<a href=\"/x\" aria-hidden=\"true\" tabindex=\"-1\">Go</a>", "tabindex")]
		public void TakenOutByMarkup_IsClearedWithItsReason(string body, string reason)
		{
			var issue = Assert.Single(Check(body), i => i.IssueType.StartsWith("ARIA_HIDDEN_FOCUSABLE", StringComparison.Ordinal));
			Assert.Equal("ARIA_HIDDEN_FOCUSABLE_CLEARED", issue.IssueType);
			Assert.Contains(reason, issue.Detail);
		}

		[Theory]
		[InlineData("<div aria-hidden=\"true\"><button>Go</button></div>")]
		// The summary of a closed <details> stays reachable.
		[InlineData("<details><summary aria-hidden=\"true\">More</summary><p>Text</p></details>")]
		// A control in the first legend of a disabled fieldset stays enabled.
		[InlineData("<fieldset disabled><legend><input aria-hidden=\"true\" type=\"checkbox\"></legend></fieldset>")]
		// disabled does nothing on a link.
		[InlineData("<a href=\"/x\" aria-hidden=\"true\" disabled>Go</a>")]
		// content-visibility:hidden hides the contents, not the element itself.
		[InlineData("<button aria-hidden=\"true\" style=\"content-visibility:hidden\">Go</button>")]
		// A nearer visibility:visible overrides a farther hidden.
		[InlineData("<div aria-hidden=\"true\" style=\"visibility:hidden\"><span style=\"visibility:visible\"><button>Go</button></span></div>")]
		// Visually hidden by clipping is still in the Tab order.
		[InlineData("<input aria-hidden=\"true\" type=\"checkbox\" class=\"sr-only-box\" style=\"position:absolute;clip:rect(0 0 0 0);width:1px;height:1px;opacity:0\">")]
		public void StillReachable_IsAViolation(string body) =>
			Assert.Equal("ARIA_HIDDEN_FOCUSABLE", Verdict(body));

		[Theory]
		[InlineData("<div aria-hidden=\"true\"><input type=\"hidden\" name=\"t\"></div>")]   // never focusable: no candidate
		[InlineData("<div aria-hidden=\"true\"><p>Text</p></div>")]
		public void NotFocusableByNature_IsNoCandidate(string body) =>
			Assert.Equal("none", Verdict(body));

		[Fact]
		public void InsideTemplateOrNoscript_IsCleared()
		{
			Assert.Equal("ARIA_HIDDEN_FOCUSABLE_CLEARED", Verdict("<noscript><a href=\"/x\" aria-hidden=\"true\">Go</a></noscript>"));
		}
	}
}
