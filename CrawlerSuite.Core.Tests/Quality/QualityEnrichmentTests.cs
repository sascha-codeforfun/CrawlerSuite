using System.Linq;
using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	public class QualityEnrichmentTests
	{
		private static QualityEnrichmentContext Ctx(string issueType) =>
			new(issueType, "text: U+00A0 NO-BREAK SPACE directly after U+2013 EN DASH", "...x...", "de");

		// ── Dispatch ─────────────────────────────────────────────────────

		[Theory]
		[InlineData(NoBreakChars.InertIssueType)]
		[InlineData(NoBreakChars.DoubleSpaceIssueType)]
		public void For_ExplainsBothArms(string issueType)
		{
			var enrichments = QualityEnrichments.For(Ctx(issueType));
			Assert.Single(enrichments);
			Assert.Equal(QualityEnrichmentConfidence.Certain, enrichments[0].Confidence);
			Assert.Equal(3, enrichments[0].Lines.Count);
		}

		[Fact]
		public void For_StaysSilentOnUnrelatedFindings()
		{
			Assert.Empty(QualityEnrichments.For(Ctx("LIGATURE")));
			Assert.Empty(QualityEnrichments.For(Ctx("CONTROL_CHARS_IN_CONTENT")));
		}

		// ── The note that travels into TicketText.log ────────────────────

		[Fact]
		public void TicketNote_IsASingleLineThatSurvivesFieldSanitisation()
		{
			var note = QualityEnrichments.TicketNote(Ctx(NoBreakChars.InertIssueType));
			Assert.NotEmpty(note);
			Assert.False(note.Contains('\r'), "carriage return would be replaced by SanitizeField");
			Assert.False(note.Contains('\n'), "line feed would be replaced by SanitizeField");
			Assert.False(note.Contains('\t'), "tab would be replaced by SanitizeField");
		}

		[Fact]
		public void TicketNote_IsEmptyWhenNothingApplies()
		{
			Assert.Equal(string.Empty, QualityEnrichments.TicketNote(Ctx("LIGATURE")));
		}

		[Fact]
		public void TicketNote_CarriesTheWholeExplanation()
		{
			var note = QualityEnrichments.TicketNote(Ctx(NoBreakChars.InertIssueType));
			foreach (var line in QualityEnrichments.For(Ctx(NoBreakChars.InertIssueType))[0].Lines)
			{
				Assert.Contains(line, note);
			}
		}

		// ── The readability contract, made executable ────────────────────

		/// <summary>
		/// The people who fix these defects are content authors and ticket workers,
		/// not typographers. The copy is the whole value of the finding — an
		/// invisible, conditional defect cannot be acted on without it — so the
		/// house rules are pinned here rather than left to reviewer discipline.
		/// If a future edit reintroduces mechanism-first language this fails.
		/// </summary>
		[Theory]
		[InlineData(NoBreakChars.InertIssueType)]
		[InlineData(NoBreakChars.DoubleSpaceIssueType)]
		public void Copy_AvoidsJargonTheAudienceDoesNotHave(string issueType)
		{
			var prose = string.Join(" ", QualityEnrichments.For(Ctx(issueType))[0].Lines);

			Assert.DoesNotContain("U+", prose);
			Assert.DoesNotContain("UAX", prose);
			Assert.DoesNotContain("LB12a", prose);
			Assert.DoesNotContain("LB18", prose);
			Assert.DoesNotContain("no-op", prose);
			Assert.DoesNotContain("glue", prose);
			Assert.DoesNotContain("codepoint", prose);
			Assert.DoesNotContain("Unicode", prose);
		}

		/// <summary>
		/// "Marked in the excerpt" is a console affordance that does not survive into
		/// a plain-text ticket, and a no-break space is indistinguishable from a
		/// space in a WYSIWYG. The instruction must therefore name what the CMS
		/// actually shows.
		/// </summary>
		[Theory]
		[InlineData(NoBreakChars.InertIssueType)]
		[InlineData(NoBreakChars.DoubleSpaceIssueType)]
		public void Copy_TellsTheAuthorWhatTheyWillSeeInTheEditor(string issueType)
		{
			var prose = string.Join(" ", QualityEnrichments.For(Ctx(issueType))[0].Lines);
			Assert.Contains("&nbsp;", prose);
			Assert.Contains("source or HTML view", prose);
		}

		/// <summary>
		/// A ticket worker cannot infer which team owns a fix, and these two arms do
		/// not route the same way.
		/// </summary>
		[Theory]
		[InlineData(NoBreakChars.InertIssueType)]
		[InlineData(NoBreakChars.DoubleSpaceIssueType)]
		public void Copy_EndsWithARoute(string issueType)
		{
			var last = QualityEnrichments.For(Ctx(issueType))[0].Lines.Last();
			Assert.StartsWith("Suggested action:", last);
			Assert.Contains("content team", last);
		}

		/// <summary>
		/// A single INERT copy would name a dash although many instances have none,
		/// sending the author hunting for something that is not there. Each type's
		/// prose must match its own symptom.
		/// </summary>
		[Fact]
		public void Copy_DoubleSpaceVariantNeverMentionsADash()
		{
			var prose = string.Join(" ",
				QualityEnrichments.For(Ctx(NoBreakChars.DoubleSpaceIssueType))[0].Lines);
			Assert.DoesNotContain("dash", prose);
			Assert.Contains("double space", prose);
		}

		[Fact]
		public void Copy_InertVariantNamesTheDash()
		{
			var prose = string.Join(" ",
				QualityEnrichments.For(Ctx(NoBreakChars.InertIssueType))[0].Lines);
			Assert.Contains("dash", prose);
		}

		[Fact]
		public void Copy_SendsTheStylingVariantToTheTemplateOwner()
		{
			var last = QualityEnrichments.For(Ctx(NoBreakChars.InertIssueType))[0].Lines.Last();
			Assert.Contains("template owner", last);
		}
	}
}
