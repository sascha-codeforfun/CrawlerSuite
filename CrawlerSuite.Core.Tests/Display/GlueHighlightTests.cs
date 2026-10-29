using CrawlerSuite.Core.Display;
using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// Three visual roles in a rendered finding context: the offending glue marker
	/// (red), the character that makes it inert (amber), and every other invisible
	/// in the window (amber). Everything else dims.
	///
	/// The distinction matters — a protective no-break space in the same element
	/// must not be red, or the card contradicts its own Detail line.
	/// </summary>
	public class GlueHighlightTests
	{
		private const string NB = "[NO-BREAK SPACE U+00A0]";
		private const string NNB = "[NARROW NO-BREAK SPACE U+202F]";
		private const string SHY = "[INVISIBLE SOFT HYPHEN U+00AD]";
		private const string TAB = "[TAB]";
		private const string LF = "[LF]";

		private static List<TriageHighlights.EmptyAnchorSpan> Spans(string s) =>
			GlueHighlight.ComputeSpans(s);

		private static List<TriageHighlights.EmptyAnchorSpan> Red(string s) =>
			Spans(s).Where(x => x.Kind == TriageHighlights.EmptyAnchorSpanKind.Structure).ToList();

		private static List<TriageHighlights.EmptyAnchorSpan> Amber(string s) =>
			Spans(s).Where(x => x.Kind == TriageHighlights.EmptyAnchorSpanKind.Href).ToList();

		private static List<TriageHighlights.EmptyAnchorSpan> Filled(string s) =>
			Spans(s).Where(x => x.Kind == TriageHighlights.EmptyAnchorSpanKind.Emphasis).ToList();

		// ── The offender ─────────────────────────────────────────────────

		[Theory]
		[InlineData("\u2013")]  // EN DASH
		[InlineData("-")]       // HYPHEN-MINUS
		[InlineData("\u2010")]  // HYPHEN
		[InlineData(" ")]       // SPACE
		public void GlueAfterABreakAllowingCharacter_IsRed(string preceding)
		{
			Assert.Single(Red($"<p>Alpha{preceding}{NB}Beta</p>"));
		}

		[Fact]
		public void GlueBeforeAnOrdinarySpace_IsRed()
		{
			Assert.Single(Red($"<p>Alpha{NB} Beta</p>"));
		}

		[Theory]
		[InlineData("a")]
		[InlineData("7")]
		[InlineData(":")]
		[InlineData("\u2014")]  // EM DASH — class B2, glue after it protects
		public void GlueAfterAProtectingCharacter_IsNotRed(string preceding)
		{
			Assert.Empty(Red($"<p>Alpha{preceding}{NB}Beta</p>"));
		}

		/// <summary>The common real-world shape: one inert, one protecting.</summary>
		[Fact]
		public void OnlyTheInertGlueIsRed_ItsProtectiveNeighbourIsAmber()
		{
			var rendered = $"<p>\u2013{NB}Gamma{NB}\u2013</p>";
			Assert.Single(Red(rendered));
			Assert.Equal(rendered.IndexOf(NB, StringComparison.Ordinal), Red(rendered)[0].Start);
			Assert.Contains(Amber(rendered),
				a => a.Start == rendered.LastIndexOf(NB, StringComparison.Ordinal));
		}

		// ── The cause ────────────────────────────────────────────────────

		/// <summary>
		/// The dash is what makes the glue inert, and usually the character the
		/// operator ends up editing. Without it marked, the Detail line is the only
		/// place the cause appears.
		/// </summary>
		[Fact]
		public void TheCausingCharacterIsFilled_ImmediatelyBeforeTheOffender()
		{
			// Filled rather than tinted: one tinted character against dimmed markup
			// is easy to miss, and this is usually a single dash.
			var rendered = $"<p>Alpha\u2013{NB}Beta</p>";
			var at = rendered.IndexOf(NB, StringComparison.Ordinal);
			Assert.Contains(Filled(rendered), a => a.Start == at - 1 && a.Length == 1);
		}

		/// <summary>
		/// The fill must not be the red or amber pairs. Red fill means "offender,
		/// live pass" and amber fill means "offender, review pass" throughout this
		/// codebase, so either would make a live card read as something it is not.
		/// Emphasis paints in the SplitInside pair — already the "region related to
		/// the offender" fill, used beside a red offender by the split-word painter.
		/// </summary>
		[Fact]
		public void TheCauseUsesItsOwnRole_NotTheOffenderRole()
		{
			var rendered = $"<p>Alpha\u2013{NB}Beta</p>";
			Assert.Single(Red(rendered));
			Assert.Single(Filled(rendered));
			Assert.NotEqual(Red(rendered)[0].Start, Filled(rendered)[0].Start);
		}

		[Fact]
		public void ACausingSoftHyphenIsAmberAsAMarker_NotAsASingleCharacter()
		{
			// It renders as its own marker, so the amber span covers the marker
			// rather than the ']' that happens to precede the glue.
			var rendered = $"<p>Alpha{SHY}{NB}Beta</p>";
			Assert.Single(Red(rendered));
			Assert.Contains(Amber(rendered),
				a => a.Start == rendered.IndexOf(SHY, StringComparison.Ordinal) && a.Length == SHY.Length);
			Assert.Empty(Filled(rendered));
		}

		// ── Other invisibles ─────────────────────────────────────────────

		[Fact]
		public void TabsAndLineFeedsAreAmber()
		{
			var rendered = $"<p style=\"{TAB}x;{LF}\">Alpha\u2013{NB}Beta</p>";
			var amber = Amber(rendered);
			Assert.Contains(amber, a => a.Start == rendered.IndexOf(TAB, StringComparison.Ordinal));
			Assert.Contains(amber, a => a.Start == rendered.IndexOf(LF, StringComparison.Ordinal));
			Assert.Single(Red(rendered));
		}

		[Fact]
		public void MarkupWithNoInvisibles_YieldsNoSpans()
		{
			Assert.Empty(Spans("<p class=\"c1\">Alpha \u2013 Beta</p>"));
		}

		[Fact]
		public void EmptyInput_YieldsNoSpans()
		{
			Assert.Empty(Spans(string.Empty));
		}

		// ── Writer contract ──────────────────────────────────────────────

		/// <summary>
		/// The writer walks spans in order and skips any starting before the current
		/// position, so out-of-order spans silently lose their colour. A cause span
		/// sits before the offender it explains, and marker kinds interleave, so
		/// ordering is a correctness requirement rather than tidiness.
		/// </summary>
		[Fact]
		public void SpansAreOrderedAndNonOverlapping()
		{
			var rendered = $"<p>{TAB}a \u2013{NB}b \u2013{NNB}c {NB}d \u2013{NB}e</p>";
			var spans = Spans(rendered);

			Assert.True(spans.Count >= 6);
			for (int i = 1; i < spans.Count; i++)
			{
				Assert.True(spans[i].Start >= spans[i - 1].Start + spans[i - 1].Length,
					"spans must be ascending and non-overlapping");
			}
		}

		[Fact]
		public void EveryGlueMarkerKindIsRecognised()
		{
			Assert.Single(Red($"<p>a\u2013{NB}b</p>"));
			Assert.Single(Red($"<p>a\u2013{NNB}b</p>"));
			Assert.Single(Red($"<p>a\u2013[FIGURE SPACE U+2007]b</p>"));
			Assert.Single(Red($"<p>a\u2013[NON-BREAKING HYPHEN U+2011]b</p>"));
		}
	}
}
