using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// The enrichment is the whole value of an invisible, conditional finding — a
	/// codepoint pair tells a content author nothing they can act on. These pin the
	/// contract the ticket writer depends on, without driving it.
	/// </summary>
	public class QualityEnrichmentDeliveryTests
	{
		private static QualityEnrichmentContext Ctx(string issueType) =>
			new(issueType, string.Empty, "<p>x</p>", "de");

		/// <summary>
		/// The card looks the enrichment up by DISPLAY TYPE, which for these groups
		/// is the issue type itself. If a group's DisplayType ever stops matching the
		/// detector's issue type, the card silently loses its explanation — nothing
		/// throws, the block just does not render.
		/// </summary>
		[Theory]
		[InlineData("LINE_BREAK_GLUE_INERT")]
		[InlineData("LINE_BREAK_GLUE_DOUBLE_SPACE")]
		public void DisplayTypeResolvesToAnEnrichment(string displayType)
		{
			Assert.NotEmpty(QualityEnrichments.For(Ctx(displayType)));
		}

		[Fact]
		public void DisplayTypesMatchTheDetectorConstants()
		{
			Assert.NotEmpty(QualityEnrichments.For(Ctx(NoBreakChars.InertIssueType)));
			Assert.NotEmpty(QualityEnrichments.For(Ctx(NoBreakChars.DoubleSpaceIssueType)));
		}

		/// <summary>
		/// The card renders Lines[0] as the headline and the rest as the body, so a
		/// single-line enrichment would render a heading with nothing under it.
		/// </summary>
		[Theory]
		[InlineData("LINE_BREAK_GLUE_INERT")]
		[InlineData("LINE_BREAK_GLUE_DOUBLE_SPACE")]
		public void EnrichmentHasAHeadlineAndABody(string issueType)
		{
			var lines = QualityEnrichments.For(Ctx(issueType))[0].Lines;
			Assert.True(lines.Count >= 2, "headline plus at least one body line");
			Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l)));
		}

		/// <summary>
		/// The note is folded into IssueRecord.Comment, which IssueLogWriter
		/// sanitises — CR, LF and TAB are rewritten to protect the row format. A
		/// note containing them would render as a run-on line broken by stray
		/// separators in TicketText.log.
		/// </summary>
		[Theory]
		[InlineData("LINE_BREAK_GLUE_INERT")]
		[InlineData("LINE_BREAK_GLUE_DOUBLE_SPACE")]
		public void TicketNoteSurvivesFieldSanitisation(string issueType)
		{
			var note = QualityEnrichments.TicketNote(Ctx(issueType));
			Assert.NotEmpty(note);
			Assert.False(note.Contains('\r'));
			Assert.False(note.Contains('\n'));
			Assert.False(note.Contains('\t'));
		}

		/// <summary>
		/// A finding with no enrichment must leave the ticket comment exactly as it
		/// was — the fold is conditional, so an empty note means the group's own
		/// comment (the log pointer) is used unchanged.
		/// </summary>
		[Fact]
		public void UnenrichedTypesYieldAnEmptyNote()
		{
			Assert.Equal(string.Empty, QualityEnrichments.TicketNote(Ctx("BARE_TEXT_IN_CONTAINER")));
			Assert.Equal(string.Empty, QualityEnrichments.TicketNote(Ctx("WORD_COLLISION")));
		}

		/// <summary>
		/// The routing line is why a ticket worker can queue this without
		/// understanding line breaking. It must be the last thing in the note, so it
		/// survives any downstream truncation of the comment.
		/// </summary>
		[Theory]
		[InlineData("LINE_BREAK_GLUE_INERT")]
		[InlineData("LINE_BREAK_GLUE_DOUBLE_SPACE")]
		public void TicketNoteEndsWithTheRoute(string issueType)
		{
			var note = QualityEnrichments.TicketNote(Ctx(issueType));
			var routeAt = note.IndexOf("Suggested action:", StringComparison.Ordinal);
			Assert.True(routeAt >= 0, "note must carry a route");
			Assert.DoesNotContain("Suggested action:", note[(routeAt + 1)..]);
		}
	}
}
