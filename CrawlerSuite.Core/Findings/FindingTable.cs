namespace CrawlerSuite.Core.Findings
{
	/// <summary>How a findings column wants to be laid out.</summary>
	public enum ColumnKind
	{
		/// <summary>Sized to its content (short values: a code, a count, a language).</summary>
		Auto,

		/// <summary>A URL or page: a generous fixed width.</summary>
		Url,

		/// <summary>Free text (context, excerpt, action): takes the remaining width; HTML in it is highlighted.</summary>
		Text,

		/// <summary>A whole number (bytes, counts, lengths): grouped and right-aligned.</summary>
		Number,

		/// <summary>An HTTP status, written as a .NET name or a number: shown as a code chip.</summary>
		Status,

		/// <summary>A redirect chain from log 03: one hop per line.</summary>
		Chain,

		/// <summary>A content-quality context: free text with the triage's marks for the issue type in <see cref="FindingColumn.Tag"/>.</summary>
		QualityContext,

		/// <summary>A mark such as "Security": a red chip when set, nothing when empty.</summary>
		Flag,

		/// <summary>A list of pages, one address per line (lines that are not addresses, such as "… and 3 more", shown as written).</summary>
		Pages,
	}

	/// <summary>One column of a findings table.</summary>
	/// <param name="Tag">Extra information for the column's kind (the issue type of a quality context).</param>
	/// <param name="IsPage">The page the finding is fixed on (the page itself, or the page linking to a broken URL): its source is shown.</param>
	public sealed record FindingColumn(string Header, ColumnKind Kind = ColumnKind.Auto, string? Tag = null, bool IsPage = false);

	/// <summary>One finding: its cells, and the downloaded file it is about, when known.</summary>
	public sealed record FindingRow(string[] Cells, string? LocalFile);

	/// <summary>The rows of one findings view, read from the snapshot's output files.</summary>
	public sealed class FindingTable
	{
		public FindingTable(IReadOnlyList<FindingColumn> columns, IReadOnlyList<FindingRow> rows, string? problem = null)
		{
			Columns = columns;
			Rows = rows;
			Problem = problem;
		}

		public IReadOnlyList<FindingColumn> Columns { get; }

		public IReadOnlyList<FindingRow> Rows { get; }

		/// <summary>Why the file could not be read (it exists, but was locked or unreadable); null when it was read.</summary>
		public string? Problem { get; }
	}

	/// <summary>
	/// What a check is based on, shown above its table: the standard in plain words, links to it,
	/// and whether a finding is a violation of it or a softer markup issue.
	/// </summary>
	/// <param name="IsWcag">
	/// A likely WCAG issue: the app names it so (<see cref="WcagLabel"/>), the text opens with what it
	/// does to a person using assistive technology, and the card ends with the requirement and the legal
	/// benchmark (<see cref="LegalNote"/>) — a reason to fix it, not the opening of a debate.
	/// </param>
	/// <param name="Requirement">For a likely WCAG issue, the success criterion: "WCAG 2.2 · 2.4.2 Page Titled (Level A)".</param>
	public sealed record FindingBasis(string Text, IReadOnlyList<(string Label, string Url)> Links, bool IsViolation, bool IsWcag = false,
		string? Requirement = null)
	{
		/// <summary>What the app calls a WCAG finding: the operator's word, never the text's opening.</summary>
		public const string WcagLabel = "likely WCAG issue";

		/// <summary>Introduces <see cref="Requirement"/> at the end of a card.</summary>
		public const string RequirementLabel = "Accessibility requirement";

		/// <summary>
		/// Why WCAG is not optional for many sites. Dates are given only where they are facts; when
		/// v4.1.1 is cited in the Official Journal is not known yet, so it carries no date — update
		/// this text once it is. A standard requires nothing by itself: the law presumes conformity
		/// through a harmonised standard, so the text says "benchmark in practice" and "incorporates".
		/// </summary>
		public const string LegalNote =
			"Legal benchmark: for services covered by the European Accessibility Act — in Germany the " +
			"Barrierefreiheitsstärkungsgesetz (BFSG), applicable since 28 June 2025 — the benchmark in practice is " +
			"EN 301 549 v3.2.1 (2021), which incorporates WCAG 2.1 Level AA. Its successor v4.1.1, published September " +
			"2026, incorporates WCAG 2.2 Level AA and is expected to become the harmonised reference once cited in the " +
			"Official Journal of the EU. This criterion is the same in WCAG 2.1 and 2.2.";

		/// <summary>The sources of <see cref="LegalNote"/>.</summary>
		public static readonly IReadOnlyList<(string Label, string Url)> LegalLinks =
		[
			("BFSG (law text)", "https://www.gesetze-im-internet.de/bfsg/"),
			("BFSG § 4: presumption of conformity", "https://www.gesetze-im-internet.de/bfsg/__4.html"),
			("ETSI: EN 301 549", "https://www.etsi.org/human-factors-accessibility/en-301-549-v3-the-harmonized-european-standard-for-ict-accessibility"),
		];

		/// <summary>The text as shown: the explanation, and for a likely WCAG issue the requirement and the legal benchmark after it.</summary>
		public string FullText => IsWcag
			? Text + (Requirement is { Length: > 0 } r ? $"\n\n{RequirementLabel}: {r}." : string.Empty) + "\n\n" + LegalNote
			: Text;

		/// <summary>The short name of the card's kind on a folded line: "likely WCAG issue", else the verdict.</summary>
		public string Headline => IsWcag ? WcagLabel : Verdict;

		/// <summary>The links as shown: the card's own, and for a likely WCAG issue the legal sources after them.</summary>
		public IReadOnlyList<(string Label, string Url)> AllLinks => IsWcag ? [.. Links, .. LegalLinks] : Links;

		/// <summary>The text's first sentence: its verdict ("Markup issue."), for a likely WCAG issue what it does to a person.</summary>
		public string Verdict => VerdictOf(Text);

		/// <summary>The first sentence of a card's text, in whatever language it is written.</summary>
		public static string VerdictOf(string text)
		{
			int end = text.IndexOf(". ", StringComparison.Ordinal);
			return end < 0 ? text : text[..(end + 1)];
		}
	}

	/// <summary>
	/// A tab in the findings views: either a group of further tabs, or a leaf with a table.
	/// </summary>
	public sealed class FindingNode
	{
		private readonly int? headline;

		private FindingNode(string title, string note, IReadOnlyList<FindingNode> children, FindingTable? table, bool written, int? headline, FindingBasis? basis = null)
		{
			Title = title;
			Note = note;
			Children = children;
			Table = table;
			IsWritten = written;
			Basis = basis;
			this.headline = headline;
		}

		/// <summary>The standard the check is based on, when it has one.</summary>
		public FindingBasis? Basis { get; }

		public string Title { get; }

		/// <summary>Where the rows come from, or why there are none.</summary>
		public string Note { get; }

		public IReadOnlyList<FindingNode> Children { get; }

		/// <summary>The rows of a leaf; null for a group.</summary>
		public FindingTable? Table { get; }

		/// <summary>False when the source file was not written in this snapshot.</summary>
		public bool IsWritten { get; }

		/// <summary>
		/// The number shown on the tab: a leaf's row count; a group's chosen headline, or the sum
		/// of its children.
		/// </summary>
		public int Count => headline ?? (Table is not null ? Table.Rows.Count : Children.Sum(c => c.Count));

		/// <param name="headline">The number on the tab when it is not the row count (e.g. broken addresses, while the rows are their references).</param>
		public static FindingNode Leaf(string title, string note, FindingTable? table, FindingBasis? basis = null, int? headline = null) =>
			new(title, note, [], table, table is not null, table is null ? null : headline, basis);

		public static FindingNode Group(string title, IReadOnlyList<FindingNode> children, int? headline = null, string note = "") =>
			new(title, note, children, null, children.Any(c => c.IsWritten), headline);
	}
}
