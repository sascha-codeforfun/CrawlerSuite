using CrawlerSuite.Core.Display;
using System.Linq;
using System.Text;
using Xunit;

namespace CrawlerSuite.Core.Tests
{
	/// <summary>
	/// Tests for TriageHighlights.InjectEmptyAnchorMarker — the pure matcher
	/// that injects the WCAG empty-link marker into essentially-empty anchors.
	/// The matcher must mirror the detector's verdict in
	/// MisplacedAnchors.Check (InnerText.Trim() blank, no &lt;img&gt;):
	/// it marks exactly the anchors the detector flags, no more, no less.
	///
	/// Pure (no Console / no Logger), so no test collection is needed. Accessed
	/// via InternalsVisibleTo. The marker rendering / colour is not covered
	/// here.
	/// </summary>
	public class ContentQualityEmptyAnchorMarkerTests
	{
		private const string Marker = "[Missing link text]";

		private static bool Marked(string excerpt) =>
			TriageHighlights.InjectEmptyAnchorMarker(excerpt).Contains(Marker);

		// ── Essentially-empty shapes → marked ─────────────────────────────────

		[Fact]
		public void Literal_AdjacentTags_IsMarked()
		{
			Assert.True(Marked("<a href=\"#x\"></a>"));
		}

		[Fact]
		public void WhitespaceOnly_IsMarked()
		{
			// Whitespace between the tags: a literal "></a>" search would miss
			// it.
			Assert.True(Marked("<a class=\"elementor-icon\" href=\"#start\">    </a>"));
		}

		[Fact]
		public void NewlinesAndTabs_CountAsWhitespace_IsMarked()
		{
			Assert.True(Marked("<a href=\"#x\">\n  \t</a>"));
		}

		[Fact]
		public void EmptyInlineChild_IsMarked()
		{
			Assert.True(Marked("<a href=\"#x\"><i></i></a>"));
		}

		[Fact]
		public void NestedEmptyChildren_IsMarked()
		{
			Assert.True(Marked("<a href=\"#x\"><span><i></i></span></a>"));
		}

		// ── Content-bearing shapes → NOT marked ───────────────────────────────

		[Fact]
		public void TextBearing_IsNotMarked()
		{
			Assert.False(Marked("<a href=\"#x\">Click</a>"));
		}

		[Fact]
		public void TextInsideChild_IsNotMarked()
		{
			Assert.False(Marked("<a href=\"#x\"><span>Go</span></a>"));
		}

		[Fact]
		public void ImageLinkWithoutAlt_IsMarked()
		{
			// An image link without usable alt has no accessible name
			// (WCAG failure F89), so it is marked.
			Assert.True(Marked("<a href=\"#x\"><img src=\"a.png\"></a>"));
		}

		[Fact]
		public void ImageLinkWithAlt_IsNotMarked() =>
			Assert.False(Marked("<a href=\"#x\"><img src=\"a.png\" alt=\"Home\"></a>"));

		[Fact]
		public void TitleOnlyLink_HasAName_IsNotMarked() =>
			Assert.False(Marked("<a href=\"#\" class=\"close-icon\" title=\"Close\"></a>"));

		[Fact]
		public void AnchorWithoutHref_IsNotMarked() =>
			Assert.False(Marked("<a id=\"klick_komponente_item_885b_bridge_link\" hidden aria-hidden=\"true\"></a>"));

		[Fact]
		public void WithDetail_OnlyTheNamedLinkIsMarked()
		{
			var html = TriageHighlights.InjectEmptyAnchorMarker(
				"<a href=\"/a\" aria-labelledby=\"elsewhere\"></a> <a href=\"/b\"></a>",
				"No accessible name — <a href=\"/b\">");
			Assert.Equal("<a href=\"/a\" aria-labelledby=\"elsewhere\"></a> <a href=\"/b\">" + Marker + "</a>", html);
		}

		// ── Identity / non-interference ───────────────────────────────────────

		[Fact]
		public void NonEmptyExcerpt_IsReturnedUnchanged()
		{
			var input = "<a href=\"#x\">Click</a>";
			Assert.Equal(input, TriageHighlights.InjectEmptyAnchorMarker(input));
		}

		[Fact]
		public void NullOrEmpty_IsReturnedUnchanged()
		{
			Assert.Equal(string.Empty, TriageHighlights.InjectEmptyAnchorMarker(string.Empty));
			Assert.Null(TriageHighlights.InjectEmptyAnchorMarker(null!));
		}

		[Fact]
		public void ArticleTag_IsNotMistakenForAnchor()
		{
			// "<article" begins with "<a" but is not an anchor — must be skipped.
			var input = "<article>text</article>";
			Assert.Equal(input, TriageHighlights.InjectEmptyAnchorMarker(input));
		}

		// ── Multiple anchors in one excerpt ───────────────────────────────────

		[Fact]
		public void MixedExcerpt_MarksOnlyTheEmptyAnchor()
		{
			var input = "<div><a href=\"#a\">  </a> sep <a href=\"#b\">Real</a></div>";
			var result = TriageHighlights.InjectEmptyAnchorMarker(input);

			// Exactly one marker, injected on the empty anchor only.
			Assert.Equal(1, CountOccurrences(result, Marker));
			// The content-bearing anchor is untouched.
			Assert.Contains("Real</a>", result);
			// Marker precedes the preserved whitespace of the empty anchor.
			Assert.Contains(Marker + "  </a>", result);
		}

		[Fact]
		public void TwoEmptyAnchors_BothMarked()
		{
			var input = "<a href=\"#a\"></a><a href=\"#b\">   </a>";
			var result = TriageHighlights.InjectEmptyAnchorMarker(input);
			Assert.Equal(2, CountOccurrences(result, Marker));
		}

		// ── Marker placement ──────────────────────────────────────────────────

		[Fact]
		public void Marker_IsInjectedAfterOpeningTagBeforeInner()
		{
			var result = TriageHighlights.InjectEmptyAnchorMarker("<a href=\"#x\">  </a>");
			// Opening tag's ">" then marker then the preserved inner then close.
			Assert.Equal("<a href=\"#x\">" + Marker + "  </a>", result);
		}

		// ── Colour-map spans (ComputeEmptyAnchorSpans) ────────────────────────

		[Fact]
		public void Spans_CloseIconAnchor_StructureHrefAttrMarker()
		{
			var html = TriageHighlights.InjectEmptyAnchorMarker(
				"<a href=\"#\" class=\"close-icon\" data-x=\"Close\"></a>");
			var spans = TriageHighlights.ComputeEmptyAnchorSpans(html);

			var structure = spans
				.Where(s => s.Kind == TriageHighlights.EmptyAnchorSpanKind.Structure)
				.Select(s => html.Substring(s.Start, s.Length))
				.ToList();
			Assert.Contains("<a", structure);
			Assert.Contains(">", structure);
			Assert.Contains("</a>", structure);

			var href = spans.Single(s => s.Kind == TriageHighlights.EmptyAnchorSpanKind.Href);
			Assert.Equal("href=\"#\"", html.Substring(href.Start, href.Length));

			var marker = spans.Single(s => s.Kind == TriageHighlights.EmptyAnchorSpanKind.Marker);
			Assert.Equal(Marker, html.Substring(marker.Start, marker.Length));

			var attrText = string.Concat(spans
				.Where(s => s.Kind == TriageHighlights.EmptyAnchorSpanKind.Attr)
				.Select(s => html.Substring(s.Start, s.Length)));
			Assert.Contains("class=\"close-icon\"", attrText);
			Assert.Contains("data-x=\"Close\"", attrText);
		}

		[Fact]
		public void Spans_TrailingMarkup_IsContext()
		{
			var html = TriageHighlights.InjectEmptyAnchorMarker(
				"<a href=\"#\"></a><div class=\"wall\">noise</div>");
			var spans = TriageHighlights.ComputeEmptyAnchorSpans(html);

			var context = string.Concat(spans
				.Where(s => s.Kind == TriageHighlights.EmptyAnchorSpanKind.Context)
				.Select(s => html.Substring(s.Start, s.Length)));
			Assert.Contains("<div class=\"wall\">noise</div>", context);
		}

		[Fact]
		public void Spans_OrderedContiguousCoverWholeString()
		{
			var html = TriageHighlights.InjectEmptyAnchorMarker(
				"lead<a target=\"_blank\" href=\"https://x.example/p\">  </a>tail");
			var spans = TriageHighlights.ComputeEmptyAnchorSpans(html);

			var pos = 0;
			var sb = new StringBuilder();
			foreach (var s in spans)
			{
				Assert.Equal(pos, s.Start);   // ordered and contiguous
				sb.Append(html.Substring(s.Start, s.Length));
				pos += s.Length;
			}

			Assert.Equal(html.Length, pos);
			Assert.Equal(html, sb.ToString());
		}

		private static int CountOccurrences(string haystack, string needle)
		{
			int count = 0, idx = 0;
			while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
			{
				count++;
				idx += needle.Length;
			}
			return count;
		}
	}
}
