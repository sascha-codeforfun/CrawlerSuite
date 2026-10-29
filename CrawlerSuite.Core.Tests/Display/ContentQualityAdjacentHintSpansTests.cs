using CrawlerSuite.Core.Display;
using Xunit;

namespace CrawlerSuite.Core.Tests
{
	/// <summary>
	/// Tests for TriageHighlights.ComputeAdjacentHintSpans — the pure helper
	/// that computes coloured hint spans for an ADJACENT_ANCHOR excerpt: every
	/// literal "&lt;/a&gt;&lt;a" collision plus the structural-fact hints
	/// (matching hrefs, matching titles, placeholder hrefs, anchor-text scope when
	/// same-href). The renderer
	/// (TriageHighlights.WriteWithAdjacentAnchorHintHighlight) only paints these spans
	/// so the structural logic is unit-tested here without Console.
	///
	/// Span-kind contract:
	///   Collision       — literal "&lt;/a&gt;&lt;a" (red, the alarm)
	///   HrefMatch       — both anchors' real hrefs are identical strings
	///   HrefPlaceholder — href="#" / "" / "javascript:..."
	///   HrefBaseline    — present href that isn't a match/placeholder
	///   TitleMatch      — both anchors' titles present and identical
	///   AnchorTextHint  — anchor inner text when paired same-real-href
	/// </summary>
	public class ContentQualityAdjacentHintSpansTests
	{
		private static string SpanText(string excerpt, TriageHighlights.AdjacentHintSpanKind kind, int index = 0)
		{
			var spans = TriageHighlights.ComputeAdjacentHintSpans(excerpt);
			var s = spans.Where(x => x.Kind == kind).ElementAt(index);
			return excerpt.Substring(s.Start, s.Length);
		}

		private static int CountSpans(string excerpt, TriageHighlights.AdjacentHintSpanKind kind)
		{
			var spans = TriageHighlights.ComputeAdjacentHintSpans(excerpt);
			return spans.Count(x => x.Kind == kind);
		}

		private static int TotalSpans(string excerpt) =>
			TriageHighlights.ComputeAdjacentHintSpans(excerpt).Count;

		// ── Collision: the basic </a><a alarm ─────────────────────────────────

		[Fact]
		public void Collision_BasicPair()
		{
			Assert.Equal("</a><a", SpanText(
				"<a href=\"/x\">A</a><a href=\"/y\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.Collision));
		}

		[Fact]
		public void Collision_TwoCollisionsInCluster()
		{
			// <a>A</a><a></a><a>B</a> — split-word cluster shape, two collisions.
			Assert.Equal(2, CountSpans(
				"<a href=\"/x\">A</a><a href=\"/x\"></a><a href=\"/x\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.Collision));
		}

		[Fact]
		public void Collision_None_ReturnsEmpty()
		{
			Assert.Empty(TriageHighlights.ComputeAdjacentHintSpans(
				"<a href=\"/x\">A</a>  <a href=\"/y\">B</a>"));
		}

		// ── HrefMatch: same real hrefs across pair ────────────────────────────

		[Fact]
		public void HrefMatch_BothSidesPainted()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"/docs\">Click </a><a href=\"/docs\">here</a>",
				TriageHighlights.AdjacentHintSpanKind.HrefMatch));
		}

		[Fact]
		public void HrefMatch_ValueIsTheHrefContents()
		{
			Assert.Equal("/docs", SpanText(
				"<a href=\"/docs\">Click </a><a href=\"/docs\">here</a>",
				TriageHighlights.AdjacentHintSpanKind.HrefMatch));
		}

		[Fact]
		public void HrefMatch_AlsoPaintsAnchorTextHint()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"/docs\">Click </a><a href=\"/docs\">here</a>",
				TriageHighlights.AdjacentHintSpanKind.AnchorTextHint));
		}

		[Fact]
		public void HrefMatch_AnchorTextHintContent()
		{
			var first = SpanText(
				"<a href=\"/docs\">Click </a><a href=\"/docs\">here</a>",
				TriageHighlights.AdjacentHintSpanKind.AnchorTextHint, index: 0);
			var second = SpanText(
				"<a href=\"/docs\">Click </a><a href=\"/docs\">here</a>",
				TriageHighlights.AdjacentHintSpanKind.AnchorTextHint, index: 1);
			Assert.Equal("Click ", first);
			Assert.Equal("here", second);
		}

		// ── HrefBaseline: distinct hrefs ──────────────────────────────────────

		[Fact]
		public void HrefBaseline_DistinctHrefs()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"/std\">A</a><a href=\"/gold\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.HrefBaseline));
		}

		[Fact]
		public void HrefBaseline_NoMatchEmitted()
		{
			Assert.Equal(0, CountSpans(
				"<a href=\"/std\">A</a><a href=\"/gold\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.HrefMatch));
		}

		[Fact]
		public void HrefBaseline_NoAnchorTextHintEmitted()
		{
			// AnchorTextHint is reserved for same-real-href pairs only.
			Assert.Equal(0, CountSpans(
				"<a href=\"/std\">A</a><a href=\"/gold\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.AnchorTextHint));
		}

		// ── HrefPlaceholder: # and javascript: ────────────────────────────────

		[Fact]
		public void HrefPlaceholder_HashOnBoth()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"#\">A</a><a href=\"#\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.HrefPlaceholder));
		}

		[Fact]
		public void HrefPlaceholder_NoMatchWhenBothPlaceholders()
		{
			// Even if both placeholders are byte-identical "#", they don't get the
			// HrefMatch (real-destination) colour — placeholders are placeholders.
			Assert.Equal(0, CountSpans(
				"<a href=\"#\">A</a><a href=\"#\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.HrefMatch));
		}

		[Fact]
		public void HrefPlaceholder_EmptyHref_NoVisibleSpan()
		{
			// href="" is structurally a placeholder, but the value between the
			// quotes is zero characters — there is nothing to paint. The visual
			// layer surfaces facts via coloured spans; where there is no surface
			// (zero-length value), the fact remains in the markup but the eye gets
			// no colour claim. This is honest: visualization is a hint mechanism,
			// not a substitute for reading the markup. The red </a><a collision
			// still fires; the operator sees the empty href context by reading
			// the excerpt directly.
			Assert.Equal(0, CountSpans(
				"<a href=\"\">A</a><a href=\"\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.HrefPlaceholder));
		}

		[Fact]
		public void HrefPlaceholder_JavascriptScheme()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"javascript:doX()\">A</a><a href=\"javascript:doY()\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.HrefPlaceholder));
		}

		// ── TitleMatch: matching title across pair ────────────────────────────

		[Fact]
		public void TitleMatch_BothPresentAndEqual()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"/x\" title=\"Docs\">A</a><a href=\"/y\" title=\"Docs\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.TitleMatch));
		}

		[Fact]
		public void TitleMatch_NoSpanWhenTitlesDiffer()
		{
			Assert.Equal(0, CountSpans(
				"<a href=\"/x\" title=\"Docs\">A</a><a href=\"/y\" title=\"Other\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.TitleMatch));
		}

		[Fact]
		public void TitleMatch_NoSpanWhenOneTitleAbsent()
		{
			Assert.Equal(0, CountSpans(
				"<a href=\"/x\" title=\"Docs\">A</a><a href=\"/y\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.TitleMatch));
		}

		[Fact]
		public void TitleMatch_AttributeOrder_TitleBeforeHref()
		{
			Assert.Equal(2, CountSpans(
				"<a title=\"Docs\" href=\"/x\">A</a><a title=\"Docs\" href=\"/y\">B</a>",
				TriageHighlights.AdjacentHintSpanKind.TitleMatch));
		}

		// ── Mixed: real href on one side, placeholder on the other ────────────

		[Fact]
		public void Mixed_RealAndPlaceholder_ClassifiedIndependently()
		{
			var spans = TriageHighlights.ComputeAdjacentHintSpans(
				"<a href=\"/real\">A</a><a href=\"#\">B</a>");
			Assert.Contains(spans, s => s.Kind == TriageHighlights.AdjacentHintSpanKind.HrefBaseline);
			Assert.Contains(spans, s => s.Kind == TriageHighlights.AdjacentHintSpanKind.HrefPlaceholder);
			// And NO match (one side is placeholder).
			Assert.DoesNotContain(spans, s => s.Kind == TriageHighlights.AdjacentHintSpanKind.HrefMatch);
		}

		// ── Quote-style variations ────────────────────────────────────────────

		[Fact]
		public void Quote_SingleQuotedHref()
		{
			Assert.Equal(2, CountSpans(
				"<a href='/docs'>Click </a><a href='/docs'>here</a>",
				TriageHighlights.AdjacentHintSpanKind.HrefMatch));
		}

		// ── Icon-widget shape — three placeholder anchors ─────────────────────

		[Fact]
		public void IconWidget_ThreeAdjacentPlaceholderAnchors()
		{
			// An icon widget's shape: three placeholder anchors with one title.
			const string excerpt =
				"<div class=\"floe-icons\"><a href=\"#\" title=\"Tidearea\">Tidearea</a>"
				+ "<a href=\"#\" title=\"Tidearea\">Tidearea</a>"
				+ "<a href=\"#\" title=\"Tidearea\">Tidearea</a>";
			// Two collisions (3 anchors, 2 boundaries between them).
			Assert.Equal(2, CountSpans(excerpt, TriageHighlights.AdjacentHintSpanKind.Collision));
			// Each side of each collision contributes a HrefPlaceholder span; the
			// middle anchor participates in BOTH pairs and would normally emit
			// twice — DedupeOrdered suppresses the duplicate so we have 3 unique
			// placeholder spans (one per anchor).
			Assert.Equal(3, CountSpans(excerpt, TriageHighlights.AdjacentHintSpanKind.HrefPlaceholder));
			// Title matches across both pairs → 3 unique title spans (same dedupe).
			Assert.Equal(3, CountSpans(excerpt, TriageHighlights.AdjacentHintSpanKind.TitleMatch));
			// Placeholders never produce AnchorTextHint (no real-destination match).
			Assert.Equal(0, CountSpans(excerpt, TriageHighlights.AdjacentHintSpanKind.AnchorTextHint));
		}

		// ── Split-word cluster shape — three anchors, all same real href ──────

		[Fact]
		public void SplitWordCluster_SameRealHrefAcrossThreeAnchors()
		{
			const string excerpt =
				"<a href=\"https://apps.example.test/details?id=test.floe\" target=\"_blank\">Gle</a>"
				+ "<a href=\"https://apps.example.test/details?id=test.floe\" target=\"_blank\"></a>"
				+ "<a href=\"https://apps.example.test/details?id=test.floe\" target=\"_blank\">tscher</a>";
			Assert.Equal(2, CountSpans(excerpt, TriageHighlights.AdjacentHintSpanKind.Collision));
			// Three anchors, all same real href → 3 unique HrefMatch spans.
			Assert.Equal(3, CountSpans(excerpt, TriageHighlights.AdjacentHintSpanKind.HrefMatch));
			// AnchorTextHint on each anchor's inner text. Middle anchor is empty
			// (no text content), so only outer two contribute; dedupe leaves 2.
			var textHints = CountSpans(excerpt, TriageHighlights.AdjacentHintSpanKind.AnchorTextHint);
			Assert.True(textHints >= 2, $"expected ≥2 AnchorTextHint, got {textHints}");
		}

		// ── Empty / null / no-collision corner cases ─────────────────────────

		[Fact]
		public void Empty_ReturnsEmpty()
			=> Assert.Empty(TriageHighlights.ComputeAdjacentHintSpans(""));

		[Fact]
		public void Null_ReturnsEmpty()
			=> Assert.Empty(TriageHighlights.ComputeAdjacentHintSpans(null!));

		[Fact]
		public void NoAdjacency_NoSpans()
			=> Assert.Empty(TriageHighlights.ComputeAdjacentHintSpans(
				"<a href=\"/x\">Plain link</a> some text then <a href=\"/y\">another</a>"));

		// ── Spans-ordered invariant ───────────────────────────────────────────

		[Fact]
		public void Spans_AreOrderedByStart()
		{
			var spans = TriageHighlights.ComputeAdjacentHintSpans(
				"<a href=\"/docs\" title=\"D\">Click </a><a href=\"/docs\" title=\"D\">here</a>");
			Assert.True(TotalSpans(
				"<a href=\"/docs\" title=\"D\">Click </a><a href=\"/docs\" title=\"D\">here</a>") > 1);
			for (var i = 1; i < spans.Count; i++)
			{
				Assert.True(spans[i].Start >= spans[i - 1].Start,
					$"span {i} starts at {spans[i].Start}, expected >= {spans[i - 1].Start}");
			}
		}

		[Fact]
		public void Spans_DoNotOverlap()
		{
			var spans = TriageHighlights.ComputeAdjacentHintSpans(
				"<a href=\"/docs\" title=\"D\">Click </a><a href=\"/docs\" title=\"D\">here</a>");
			for (var i = 1; i < spans.Count; i++)
			{
				var prevEnd = spans[i - 1].Start + spans[i - 1].Length;
				Assert.True(spans[i].Start >= prevEnd,
					$"span {i} at {spans[i].Start} overlaps previous ending at {prevEnd}");
			}
		}
	}
}
