using CrawlerSuite.Core.Display;
using Xunit;

namespace CrawlerSuite.Core.Tests.Display
{
	/// <summary>Highlight tests for the triage marks the findings views show.</summary>
	public class TriageHighlightTests
	{
		[Fact]
		public void ComputeQuoteSpans_MarksEveryQuoteGlyph_AsContextByDefault()
		{
			// Two curly quotes; no triggers supplied → both are context, none trigger.
			var text = "say \u201Chi\u201D ok";
			var spans = TriageHighlights.ComputeQuoteSpans(text, []);
			Assert.Equal(2, spans.Count);
			Assert.All(spans, s => Assert.False(s.IsTrigger));
			Assert.All(spans, s => Assert.Equal(1, s.Length));
			Assert.Equal(text.IndexOf('\u201C'), spans[0].Start);
			Assert.Equal(text.IndexOf('\u201D'), spans[1].Start);
		}

		[Fact]
		public void ComputeQuoteSpans_StraightQuote_IsHighlightable_AndPaintsAsTrigger()
		{
			// „Weiter" — German opener (context) + straight ASCII closer, which is
			// the QUOTE_MIXED_KIND offender. The straight quote must be a highlightable
			// glyph and, when supplied as a trigger, paint as the trigger — not stay
			// invisible while only the typographic partner shows.
			var text = "Klicken Sie auf \u201EWeiter\u0022.";
			var straightPos = text.IndexOf('\u0022');
			var spans = TriageHighlights.ComputeQuoteSpans(text, new[] { straightPos });

			Assert.Equal(2, spans.Count);                                       // „ and " both highlighted
			var straight = Assert.Single(spans, s => s.Start == straightPos);
			Assert.True(straight.IsTrigger);                                    // offender → red
			var opener = Assert.Single(spans, s => s.Start == text.IndexOf('\u201E'));
			Assert.False(opener.IsTrigger);                                     // context → blue
		}

		[Fact]
		public void ComputeQuoteSpans_MarksOnlyTriggerPosition_AsTrigger()
		{
			// Three U+2019: two possessive apostrophes + the flagged orphan. Only the
			// supplied trigger position is red; the others stay context (blue).
			var text = "users\u2019 and firms\u2019 data\u2019";
			var orphan = text.LastIndexOf('\u2019');
			var spans = TriageHighlights.ComputeQuoteSpans(text, new[] { orphan });
			Assert.Equal(3, spans.Count);
			Assert.Single(spans, s => s.IsTrigger);
			Assert.True(spans.Single(s => s.IsTrigger).Start == orphan);
		}

		[Fact]
		public void ComputeQuoteSpans_EmptyText_ReturnsEmpty()
		{
			Assert.Empty(TriageHighlights.ComputeQuoteSpans(string.Empty, new[] { 0 }));
		}

		[Fact]
		public void FormatQuoteOffenders_SingleOffender_ShowsCodepointAndGlyph()
		{
			// „FROST”  — German opener + U+201D wrong closer.
			var text = "(\u201EFROST\u201D)";
			var pos = text.IndexOf('\u201D');
			var line = TriageHighlights.FormatQuoteOffenders(text, new[] { pos });
			Assert.Equal("U+201D \u201D", line);
		}

		[Fact]
		public void FormatQuoteOffenders_DuplicateOffenders_CollapseWithCount()
		{
			// „123ab"  „55555"  — two straight U+0022 closers;
			// they dedupe to one entry with ×2.
			var text = "\u201E123ab\u0022 \u201E55555\u0022";
			var positions = new List<int>();
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] == '\u0022')
				{
					positions.Add(i);
				}
			}
			var line = TriageHighlights.FormatQuoteOffenders(text, positions);
			Assert.Equal("U+0022 \" \u00D72", line);
		}

		[Fact]
		public void FormatQuoteOffenders_DistinctOffenders_ListedInTextOrder()
		{
			// A U+201D then a U+0022 → two entries, comma-joined, left-to-right in
			// text order (matching the colourer's paint walk), regardless of the
			// order the positions are supplied.
			var text = "a\u201Db\u0022";
			var line = TriageHighlights.FormatQuoteOffenders(
				text, new[] { text.IndexOf('\u0022'), text.IndexOf('\u201D') });
			Assert.Equal("U+201D \u201D, U+0022 \"", line);
		}

		[Fact]
		public void FormatQuoteOffenders_SamePositionFlaggedTwice_CountedOnce()
		{
			// A glyph flagged by BOTH SYSTEM_MIX and UNMATCHED arrives as the same
			// position twice. The colourer de-dupes (HashSet) and reds it once; the
			// hex must agree — one glyph, no "×2" against a single red U+201C.
			var text = "a\u201Cb";
			var pos = text.IndexOf('\u201C');
			var line = TriageHighlights.FormatQuoteOffenders(text, new[] { pos, pos });
			Assert.Equal("U+201C \u201C", line);
		}

		[Fact]
		public void FormatQuoteOffenders_NoPositions_ReturnsEmpty()
		{
			Assert.Equal(string.Empty, TriageHighlights.FormatQuoteOffenders("abc", []));
		}

		[Fact]
		public void FormatQuoteOffenders_OutOfRangePosition_Skipped()
		{
			Assert.Equal(string.Empty, TriageHighlights.FormatQuoteOffenders("ab", new[] { 99 }));
		}

		[Fact]
		public void FormatQuoteOffenders_AstralOffender_DecodesSurrogatePair()
		{
			// An offender outside the BMP arrives as a high+low surrogate pair. The
			// formatter must combine the pair into one code point (U+1Fxxx) and emit
			// the two-char glyph, not format the lone high surrogate. \U0001F600 is a
			// stand-in astral code point; the trigger sits on its high surrogate.
			var text = "x\U0001F600y";
			var pos = 1; // the astral code point begins at index 1 (its high surrogate)
			var line = TriageHighlights.FormatQuoteOffenders(text, new[] { pos });
			Assert.Equal("U+1F600 \U0001F600", line);
		}

		[Fact]
		public void ComputeWordCollisionSpans_TrailingSeam_ThreeSpansInsideTagTail()
		{
			var html = "<span class=\"h2\">Treib</span>Eisrand der Scholle";
			var spans = TriageHighlights.ComputeWordCollisionSpans(html);
			Assert.Equal(3, spans.Count);

			var inside = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.Inside);
			Assert.Equal("Treib", html.Substring(inside.Start, inside.Length));

			var tag = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.Tag);
			Assert.Equal("</span>", html.Substring(tag.Start, tag.Length));

			var tail = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.Tail);
			Assert.Equal("Eisrand", html.Substring(tail.Start, tail.Length));
		}

		[Fact]
		public void ComputeWordCollisionSpans_SpaceAfterTag_NoSpans()
		{
			// A space after </span> means a clean boundary, not a collision.
			var spans = TriageHighlights.ComputeWordCollisionSpans("<span>Treib</span> Eisrand");
			Assert.Empty(spans);
		}

		[Fact]
		public void ComputeWordCollisionSpans_BrSpacerInsideSpan_TextInsideBrSeparate()
		{
			// <span>Foo<br></span>Bar — the <br> spacer abuse inside the span.
			// "Foo" stays Inside (light blue), the <br> becomes its own BrSpacer span.
			var html = "<span>Foo<br></span>Bar baz";
			var spans = TriageHighlights.ComputeWordCollisionSpans(html);

			var inside = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.Inside);
			Assert.Equal("Foo", html.Substring(inside.Start, inside.Length));

			var br = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.BrSpacer);
			Assert.Equal("<br>", html.Substring(br.Start, br.Length));

			var tag = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.Tag);
			Assert.Equal("</span>", html.Substring(tag.Start, tag.Length));

			var tail = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.Tail);
			Assert.Equal("Bar", html.Substring(tail.Start, tail.Length));
		}

		[Fact]
		public void ComputeWordCollisionSpans_AttributedBr_CapturedWhole()
		{
			// A line break with a class inside an inline element: <br class="tidebreak">.
			var html = "<span>Foo<br class=\"tidebreak\"></span>Bar";
			var spans = TriageHighlights.ComputeWordCollisionSpans(html);
			var br = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.BrSpacer);
			Assert.Equal("<br class=\"tidebreak\">", html.Substring(br.Start, br.Length));
		}

		[Fact]
		public void ComputeWordCollisionSpans_NonBrInnerTag_NotCarvedAsBrSpacer()
		{
			// A non-<br> inner tag (<b>) stays within Inside — only <br> is carved out.
			var html = "<span>Foo<b>X</b></span>Bar";
			var spans = TriageHighlights.ComputeWordCollisionSpans(html);
			Assert.DoesNotContain(spans, s => s.Kind == TriageHighlights.SplitSpanKind.BrSpacer);
		}

		[Fact]
		public void ComputeWordCollisionSpans_LeadingSeam_ThreeSpansWord1TagWord2()
		{
			// Leading seam: bare "Floeco" abuts opening <span><sup> whose text starts
			// uppercase ("TM"). The trailing path finds nothing (no </tag> before an
			// uppercase), so the leading fallback paints WORD1 / opening tag(s) / WORD2.
			var html = "Floeco<span class=\"small\"><sup>TM</sup></span>";
			var spans = TriageHighlights.ComputeWordCollisionSpans(html);
			Assert.Equal(3, spans.Count);

			var inside = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.Inside);
			Assert.Equal("Floeco", html.Substring(inside.Start, inside.Length));

			var tag = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.Tag);
			Assert.Equal("<span class=\"small\"><sup>", html.Substring(tag.Start, tag.Length));

			var tail = spans.Single(s => s.Kind == TriageHighlights.SplitSpanKind.Tail);
			Assert.Equal("TM", html.Substring(tail.Start, tail.Length));
		}

		[Fact]
		public void ComputeWordCollisionSpans_LeadingSeam_LowercaseAfterTag_NoSpans()
		{
			// Opening tag after a lowercase word, but the inside text is lowercase too —
			// no lower→Upper merge, so nothing is painted.
			var spans = TriageHighlights.ComputeWordCollisionSpans("wort<span>lower text");
			Assert.Empty(spans);
		}

		[Fact]
		public void ExtractControlCharMarkers_ReturnsDistinctMarkersInOrder()
		{
			var markers = TriageHighlights.ExtractControlCharMarkers(
				"a[INVISIBLE ZERO-WIDTH SPACE U+200B]b[CR]c[INVISIBLE ZERO-WIDTH SPACE U+200B]d");
			Assert.Equal(
				new[] { "[INVISIBLE ZERO-WIDTH SPACE U+200B]", "[CR]" },
				markers);
		}

		[Theory]
		[InlineData("Found ZWSP (U+200B) in <p> text", "Found ZWSP (U+200B) in ", "<p>", " text")]
		[InlineData("Found ZWSP (U+200B) in <h2> text", "Found ZWSP (U+200B) in ", "<h2>", " text")]
		[InlineData("Found ZWSP (U+200B) in <title> text", "Found ZWSP (U+200B) in ", "<title>", " text")]
		[InlineData("Found LF (U+000A) in meta[@name=\"description\"] content", "Found LF (U+000A) in ", "meta[@name=\"description\"]", " content")]
		public void SplitControlCharLocation_KnownShapes_IsolatesToken(
			string detail, string before, string token, string after)
		{
			var (b, t, a) = TriageHighlights.SplitControlCharLocation(detail);
			Assert.Equal(before, b);
			Assert.Equal(token, t);
			Assert.Equal(after, a);
		}

		[Theory]
		[InlineData("")]
		[InlineData("no recognisable shape here")]
		[InlineData("Found ZWSP (U+200B) in <p>")] // has \" in \" but no trailing text/content
		public void SplitControlCharLocation_UnrecognisedShape_EmptyToken(string detail)
		{
			var (_, token, _) = TriageHighlights.SplitControlCharLocation(detail);
			Assert.Equal(string.Empty, token);
		}

		[Fact]
		public void ComputeDecompositionSpans_TwoLetters_TwoSpansAtBasePositions()
		{
			// f u◌̈ r   k o◌̈ nnen  — base+mark spans at the 'u' (1) and 'o' (6).
			var spans = TriageHighlights.ComputeDecompositionSpans("fu\u0308r ko\u0308nnen");
			Assert.Equal(2, spans.Count);
			Assert.Equal((1, 2), (spans[0].Start, spans[0].Length));
			Assert.Equal((6, 2), (spans[1].Start, spans[1].Length));
		}

		[Fact]
		public void ComputeDecompositionSpans_Composed_NoSpans()
		{
			// Precomposed ü is a single char, not base+mark — nothing to light.
			Assert.Empty(TriageHighlights.ComputeDecompositionSpans("f\u00FCr"));
		}

		[Fact]
		public void ExtractDecompositionMarkers_FindsCodepointMarkers()
		{
			var markers = TriageHighlights.ExtractDecompositionMarkers("f\u00FCr  |  fu[U+0308]r");
			Assert.Equal(new[] { "[U+0308]" }, markers);
		}

		[Fact]
		public void ExtractHighlightPatterns_SingularMarker_ReturnsOnePattern()
		{
			var result = TriageHighlights.ExtractHighlightPatterns(
				"UNWANTED_PATTERN:Legacy: Old Name — pattern: oldword");
			Assert.Equal(new[] { "oldword" }, result);
		}

		[Fact]
		public void ExtractHighlightPatterns_PluralMarker_SplitsOnCommaSpace()
		{
			var result = TriageHighlights.ExtractHighlightPatterns(
				"UNWANTED_PATTERN:Legacy: Old Name — patterns: oldword, otherword");
			Assert.Equal(new[] { "oldword", "otherword" }, result);
		}

		[Fact]
		public void ExtractHighlightPatterns_NoMarker_ReturnsAfterColon()
		{
			var result = TriageHighlights.ExtractHighlightPatterns("UNWANTED_PATTERN:oldword");
			Assert.Equal(new[] { "oldword" }, result);
		}

		[Fact]
		public void ExtractHighlightPatterns_Empty_ReturnsEmpty()
		{
			Assert.Empty(TriageHighlights.ExtractHighlightPatterns(string.Empty));
		}
	}
}
