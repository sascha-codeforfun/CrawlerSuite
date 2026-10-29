namespace CrawlerSuite.Core.Quality
{
	/// <summary>
	/// Operator-facing explanations for content-quality findings — the sibling of
	/// <c>SpellCheck.SpellEnrichment</c>, built for the same reason and consumed the
	/// same way (triage card, then <c>IssueRecord.Comment</c>, then TicketText.log).
	///
	/// WHY CONTENT QUALITY HAS ONE. Most CQ defects are visible: a ligature, a
	/// mixed quote system, an empty anchor, text before the doctype. You point at
	/// it and the author sees it, so the finding needs no prose.
	/// <see cref="NoBreakChars"/> is invisible AND conditional — nothing looks
	/// wrong until the viewport is narrow enough to wrap at that exact character.
	/// The copy therefore has to convince someone that a defect exists in
	/// something they are looking at and cannot see, and then tell them which
	/// team fixes it.
	///
	/// HOUSE RULES FOR THE COPY:
	///   * Assume no Unicode knowledge. The audience is content authors and ticket
	///     workers, not typographers.
	///   * No codepoints, no rule numbers, no "no-op", no "glue". Those belong in
	///     the finding Detail and in the XML docs on the check, where the reader who
	///     wants them will look. A mechanism-first sentence loses the audience.
	///   * Name the fix in terms the CMS shows. "&amp;nbsp; in the source view" is
	///     actionable; "the marked character" is not, because TicketText.log is
	///     plain text with no marking, and a no-break space is indistinguishable
	///     from a space in a WYSIWYG.
	///   * End with the routing. A ticket worker cannot infer which team owns a fix.
	/// </summary>
	public enum QualityEnrichmentConfidence
	{
		/// <summary>The defect is certain — the finding states a fact, not a suspicion.</summary>
		Certain,

		/// <summary>Possible; the card reads "review before ticketing".</summary>
		Medium,
	}

	/// <summary>
	/// What an enricher receives. The WHOLE finding, so a future provider can key on
	/// the detail or the page language without a contract change.
	/// </summary>
	public sealed record QualityEnrichmentContext(
		string IssueType, string Detail, string Context, string Language);

	/// <summary>
	/// An explanation attached to one finding type. <see cref="Lines"/> is calm
	/// English prose and is the payload — a non-fluent operator must be able to act
	/// on it alone.
	///
	/// No in-text highlight offsets, unlike the spelling equivalent. A spelling
	/// finding highlights offending letters inside one word; here the offender is an
	/// invisible character inside a sentence, and the identification is carried by
	/// the <c>LogExcerpt</c> markers already rendered into the excerpt — which works
	/// on screen AND in the plain-text ticket, where colour does not exist.
	/// </summary>
	public sealed record QualityEnrichment(
		string Kind,
		QualityEnrichmentConfidence Confidence,
		IReadOnlyList<string> Lines);

	/// <summary>
	/// A pluggable explainer for a content-quality finding. Returns true plus an
	/// enrichment when it has something to say; false to stay silent.
	/// </summary>
	public interface IQualityEnricher
	{
		bool TryEnrich(QualityEnrichmentContext context, out QualityEnrichment enrichment);
	}

	/// <summary>
	/// Explains both arms of <see cref="NoBreakChars"/>.
	///
	/// The prose is TYPE-LEVEL and carries no values from the individual occurrence.
	/// Quality tickets group per page and defect type with N occurrences under one
	/// heading, so an interpolated note would repeat the same three paragraphs for
	/// every occurrence on the page. The per-occurrence specifics (which character,
	/// after which character) ride in <c>QualityIssue.Detail</c>, which the card and
	/// the ticket already print beside each excerpt.
	/// </summary>
	public sealed class NoBreakCharEnricher : IQualityEnricher
	{
		public bool TryEnrich(QualityEnrichmentContext context, out QualityEnrichment enrichment)
		{
			enrichment = null!;

			if (context.IssueType == NoBreakChars.InertIssueType)
			{
				enrichment = new QualityEnrichment(
					NoBreakChars.InertIssueType,
					QualityEnrichmentConfidence.Certain,
					new[]
					{
						"A non-breaking space here has no effect. The text can still break onto a "
							+ "new line at this point.",
						"A non-breaking space was placed directly after a dash or a hyphen. A dash "
							+ "already allows the line to break after it, so the space changes "
							+ "nothing. On a narrow screen the line still splits after the dash, and "
							+ "the space then moves to the next line, where it shows as a small gap "
							+ "before the first word. On a wide screen nothing looks wrong, which is "
							+ "why this is easy to miss.",
						"Suggested action: content team — in the editor's source or HTML view this "
							+ "character appears as &nbsp; directly after the dash. Delete that one "
							+ "only. A second &nbsp; before the dash may be doing useful work and "
							+ "should stay. If the phrase must never split across two lines, that is "
							+ "a styling change and belongs with the template owner instead.",
					});
				return true;
			}

			if (context.IssueType == NoBreakChars.DoubleSpaceIssueType)
			{
				enrichment = new QualityEnrichment(
					NoBreakChars.DoubleSpaceIssueType,
					QualityEnrichmentConfidence.Certain,
					new[]
					{
						"A non-breaking space sits next to a normal space. Readers see a double "
							+ "space where one was meant.",
						"The normal space beside it already allows the line to break at this point, "
							+ "so the non-breaking space is holding nothing together either. One "
							+ "pair of characters causes both problems at once. This usually arrives "
							+ "by copy and paste from a word processor or a PDF.",
						"Suggested action: content team — in the editor's source or HTML view, "
							+ "delete one of the two spaces. Keep the plain space in ordinary text. "
							+ "Keep the &nbsp; instead only where the two words must stay together "
							+ "on one line, such as a number and its unit.",
					});
				return true;
			}

			return false;
		}
	}

	/// <summary>
	/// The registry and run point. Every consumer — triage card, ticket note, and
	/// any later passive log — funnels through here so they all see the same
	/// explanations.
	/// </summary>
	public static class QualityEnrichments
	{
		private static readonly IReadOnlyList<IQualityEnricher> Enrichers = new IQualityEnricher[]
		{
			new NoBreakCharEnricher(),
		};

		/// <summary>All enrichments that apply to this finding (empty when none do).</summary>
		public static IReadOnlyList<QualityEnrichment> For(QualityEnrichmentContext context)
		{
			var result = new List<QualityEnrichment>();
			foreach (var enricher in Enrichers)
			{
				if (enricher.TryEnrich(context, out var enrichment))
				{
					result.Add(enrichment);
				}
			}

			return result;
		}

		/// <summary>
		/// The note that rides into <c>IssueRecord.Comment</c> when a ticket is
		/// raised, so the operator's comprehension travels into TicketText.log with
		/// it. Empty when nothing applies.
		///
		/// Joined with a SPACE, not a newline: Comment is written through
		/// <c>IssueLogWriter.SanitizeField</c>, which replaces CR/LF/TAB to protect
		/// the row format, so a newline-joined note would render as a run-on line
		/// broken by stray separators. The Lines are complete sentences, so a space
		/// reads as clean prose and survives sanitisation untouched. The triage CARD
		/// still renders the Lines individually; only this persisted note flattens.
		/// </summary>
		public static string TicketNote(QualityEnrichmentContext context)
		{
			var lines = new List<string>();
			foreach (var enrichment in For(context))
			{
				lines.AddRange(enrichment.Lines);
			}

			return lines.Count == 0 ? string.Empty : string.Join(" ", lines);
		}
	}
}
