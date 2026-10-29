using CrawlerSuite.Core.Display;
using Xunit;

namespace CrawlerSuite.Core.Tests.Display
{
	/// <summary>The issue type picks the triage's highlighting; marks always lie inside the text.</summary>
	public class QualityMarksTests
	{
		[Fact]
		public void Ligature_MarksTheGlyph()
		{
			var marked = QualityMarks.For("LIGATURE", "the ﬁrst", "Ligature fi", null);
			var mark = Assert.Single(marked.Marks, m => m.Role != MarkRole.Highlight);
			Assert.Equal(new Mark(4, 1, MarkRole.Offender), mark);
		}

		[Fact]
		public void UnwantedPattern_MarksEveryOccurrence_IgnoringCase()
		{
			var marked = QualityMarks.For("UNWANTED_PATTERN", "Lorem ipsum … lorem", "Placeholder: Lorem — pattern: lorem", null);
			Assert.Equal(2, marked.Marks.Count);
			Assert.All(marked.Marks, m => Assert.Equal(MarkRole.Offender, m.Role));
		}

		[Fact]
		public void EmptyAnchor_GetsAVisibleMarker()
		{
			var marked = QualityMarks.For("MISPLACED_ANCHOR_EMPTY", "text <a href=\"/x\"></a> more", string.Empty, null);
			Assert.Contains(TriageHighlights.EmptyLinkMarker, marked.Text);
			Assert.Contains(marked.Marks, m => m.Role == MarkRole.Fill && marked.Text.Substring(m.Start, m.Length) == TriageHighlights.EmptyLinkMarker);
		}

		[Fact]
		public void QuoteWithoutConfig_MarksQuotesAsPartners_NoOffender()
		{
			var marked = QualityMarks.For("QUOTE_UNMATCHED", "„Hallo Welt", "unmatched", null);
			Assert.Contains(marked.Marks, m => m.Role == MarkRole.Partner);
			Assert.DoesNotContain(marked.Marks, m => m.Role == MarkRole.Offender);
			Assert.Null(marked.Note);
		}

		[Fact]
		public void QuoteWithConfig_MarksTheOffender_AndNamesIt()
		{
			var marked = QualityMarks.For("QUOTE_UNMATCHED", "Er sagte „Hallo und ging.", "unmatched", new ContentQualityConfig());
			if (marked.Marks.Any(m => m.Role == MarkRole.Offender))
			{
				Assert.StartsWith("Offender: U+", marked.Note);
			}
		}

		[Fact]
		public void HiddenFocusable_MarksAriaHiddenAndTheReachableTag()
		{
			var context = "<div aria-hidden=\"true\"><a href=\"/x\">Read more</a></div>";
			var marked = QualityMarks.For("ARIA_HIDDEN_FOCUSABLE", context, "<a href=\"/x\"> is hidden …", null);
			Assert.Contains(marked.Marks, m => m.Role == MarkRole.Warn && context.Substring(m.Start, m.Length) == "aria-hidden=\"true\"");
			Assert.Contains(marked.Marks, m => m.Role == MarkRole.Offender && context.Substring(m.Start, m.Length) == "<a href=\"/x\">");
		}

		[Fact]
		public void BrokenReference_MarksOnlyTheMissingId()
		{
			var context = "<input aria-describedby=\"hint err\">";
			var marked = QualityMarks.For("ARIA_REFERENCE_BROKEN", context, "aria-describedby points to missing id \"err\" on <input>", null);
			var mark = Assert.Single(marked.Marks);
			Assert.Equal("err", context.Substring(mark.Start, mark.Length));
			Assert.Equal(MarkRole.Offender, mark.Role);
		}

		[Fact]
		public void EmptyLink_ImageLink_GetsTheMarkerBeforeTheImage()
		{
			var marked = QualityMarks.For("MISPLACED_ANCHOR_EMPTY", "<a href=\"/x\"><img src=\"a.png\"></a>",
				"Image link: the image has no alt attribute — <a href=\"/x\">", null);
			Assert.Equal("<a href=\"/x\">" + TriageHighlights.EmptyLinkMarker + "<img src=\"a.png\"></a>", marked.Text);
		}

		[Theory]
		[InlineData("SPLIT_WORD_ANCHOR", "<p><a href=\"/x\">Bei</a>spiel</p>")]
		[InlineData("WORD_COLLISION", "<p><b>Hello</b>World</p>")]
		[InlineData("ADJACENT_ANCHOR", "<a href=\"/a\">A</a><a href=\"/a\">B</a>")]
		[InlineData("LINE_BREAK_GLUE_INERT", "a [NBSP U+00A0]b")]
		[InlineData("CONTROL_CHARS_IN_CONTENT", "a[INVISIBLE ZERO WIDTH SPACE U+200B]b")]
		[InlineData("LANGUAGE_MISMATCH", "anything")]
		public void Marks_StayInsideTheText(string type, string context)
		{
			var marked = QualityMarks.For(type, context, string.Empty, new ContentQualityConfig());
			Assert.All(marked.Marks, m => Assert.True(m.Start >= 0 && m.Start + m.Length <= marked.Text.Length));
		}
	
		[Theory]
		[InlineData("EMBEDDED_BOM_IN_BODY", "offset 5: …x\">[BOM U+FEFF]<div>", "[BOM U+FEFF]")]
		[InlineData("INVISIBLE_CHAR_IN_BODY", "[<sup>] 1[ZWSP (U+200B)]", "[ZWSP (U+200B)]")]
		public void InvisibleMarker_IsTheOffender(string type, string context, string marker)
		{
			var marked = CrawlerSuite.Core.Display.QualityMarks.For(type, context, string.Empty, null);
			var mark = Assert.Single(marked.Marks, m => m.Role != MarkRole.Highlight);
			Assert.Equal(context.IndexOf(marker, StringComparison.Ordinal), mark.Start);
			Assert.Equal(marker.Length, mark.Length);
			Assert.Equal(CrawlerSuite.Core.Display.MarkRole.Offender, mark.Role);
		}

		[Fact]
		public void ControlChars_TheNamedCharacterIsTheOffender_OthersAreBystanders()
		{
			const string context = "one[INVISIBLE SOFT HYPHEN U+00AD]two[INVISIBLE LINE SEPARATOR U+2028]three";
			var marked = QualityMarks.For("CONTROL_CHARS_IN_CONTENT", context, "Found LINE SEPARATOR (U+2028) in <p> text", null);
			var culprit = Assert.Single(marked.Marks, m => m.Role == MarkRole.Offender);
			Assert.Equal("[INVISIBLE LINE SEPARATOR U+2028]", context.Substring(culprit.Start, culprit.Length));
			var bystander = Assert.Single(marked.Marks, m => m.Role == MarkRole.Partner);
			Assert.Equal("[INVISIBLE SOFT HYPHEN U+00AD]", context.Substring(bystander.Start, bystander.Length));
		}

		[Fact]
		public void ControlChars_WithoutANamedCharacter_EveryMarkerStaysTheOffender()
		{
			var marked = QualityMarks.For("CONTROL_CHARS_IN_CONTENT", "a[LF]b[INVISIBLE SOFT HYPHEN U+00AD]c", "no code point here", null);
			var marks = marked.Marks.Where(m => m.Role != MarkRole.Highlight).ToList();
			Assert.Equal(2, marks.Count);
			Assert.All(marks, m => Assert.Equal(MarkRole.Offender, m.Role));
		}

		[Fact]
		public void ControlChars_ShortMarkers_AreMatchedByTheirCharacter()
		{
			var marked = QualityMarks.For("CONTROL_CHARS_IN_CONTENT", "a[LF]b[TAB]c", "Found LF (U+000A) in <title> text", null);
			var marks = marked.Marks.Where(m => m.Role != MarkRole.Highlight).ToList();
			Assert.Equal(MarkRole.Offender, marks[0].Role);
			Assert.Equal(MarkRole.Partner, marks[1].Role);
		}

		[Fact]
		public void Highlight_UnderTheWordTheOffenderTouches()
		{
			var marked = QualityMarks.For("LIGATURE", "the ﬁrst one", "Ligature fi", null);
			var highlights = marked.Marks.Where(m => m.Role == MarkRole.Highlight).Select(m => marked.Text.Substring(m.Start, m.Length)).ToList();
			Assert.Equal(new[] { "rst" }, highlights); // the rest of the word, not "the" or "one"
		}

		[Fact]
		public void Highlight_InvisibleMarkerInsideAWord_BothHalves()
		{
			const string context = "Sicherheits[INVISIBLE SOFT HYPHEN U+00AD]Check folgt";
			var marked = QualityMarks.For("CONTROL_CHARS_IN_CONTENT", context, "Found SHY (U+00AD) in <title> text", null);
			var highlights = marked.Marks.Where(m => m.Role == MarkRole.Highlight).Select(m => context.Substring(m.Start, m.Length)).ToList();
			Assert.Equal(new[] { "Sicherheits", "Check" }, highlights);
		}

		[Fact]
		public void Highlight_QuotedPhraseBetweenTheQuotes()
		{
			// A quote offender with its partner nearby: the phrase between them is highlighted.
			var marked = QualityMarks.WithHighlights(new MarkedContext("Er sagte \u201EHallo Welt\u201C und ging.",
				[new Mark(9, 1, MarkRole.Tint), new Mark(20, 1, MarkRole.Offender)], null), quotes: true);
			var highlight = Assert.Single(marked.Marks, m => m.Role == MarkRole.Highlight);
			Assert.Equal("Hallo Welt", marked.Text.Substring(highlight.Start, highlight.Length));
		}

		[Fact]
		public void Highlight_NotForTypesThatMarkAStretch()
		{
			var marked = QualityMarks.For("UNWANTED_PATTERN", "xLoremx ipsum", "Placeholder: Lorem — pattern: lorem", null);
			Assert.DoesNotContain(marked.Marks, m => m.Role == MarkRole.Highlight);
		}

		[Fact]
		public void SameMarkQuotation_RedOnTheEndTheDetailNames()
		{
			var config = new ContentQualityConfig();
			var text = "Das Kapitel \u201CEnde\u201C fehlt noch.";
			var issue = Assert.Single(CrawlerSuite.Core.Quality.Quotes.CheckPairing("f.html", text, config, "de"));
			var marked = QualityMarks.For(issue.IssueType, text, issue.Detail, config);
			var offender = Assert.Single(marked.Marks, m => m.Role == MarkRole.Offender);
			Assert.Equal(text.IndexOf('\u201C'), offender.Start);   // the opener, as "wrong opener" says
			Assert.NotNull(marked.Note);
		}

		[Theory]
		[InlineData("<a href=\"/x\" aria-hidden=\"true\" tabindex=\"-1\">Go</a>", "tabindex=\"-1\"")]
		[InlineData("<div aria-hidden=\"true\" hidden><button>Go</button></div>", "hidden")]
		[InlineData("<div aria-hidden=\"true\" style=\"color:red; display: none\"><button>Go</button></div>", "display: none")]
		public void Cleared_NoRed_TheEvidenceInGreen(string context, string evidence)
		{
			var marked = QualityMarks.For("ARIA_HIDDEN_FOCUSABLE_CLEARED", context, "cleared", new ContentQualityConfig());
			Assert.DoesNotContain(marked.Marks, m => m.Role is MarkRole.Offender or MarkRole.Fill);
			var ok = Assert.Single(marked.Marks, m => m.Role == MarkRole.Ok);
			Assert.Equal(evidence, context.Substring(ok.Start, ok.Length));
		}
	}
}
