namespace CrawlerSuite.Core.Quality
{
	internal static partial class WordSplits
	{
		/// <summary>
		/// Detects anchor tags that close mid-word — a common CMS authoring mistake.
		/// Pattern: &lt;/a&gt; immediately followed by a RUN of letters/digits, then
		/// whitespace. The run is the orphaned tail of a token that should have been
		/// inside the link.
		/// Examples: "Hello Wor&lt;/a&gt;ld " — "ld" belongs to the preceding word;
		/// "08&lt;/a&gt;15 Uhr" — "15" belongs to the preceding number.
		///
		/// The quantifier is "+", not a single char: a one-character orphan is the
		/// EDGE case, not the norm — real splits almost always strand a multi-char
		/// fragment ("Wor|ld", "08|15"). A single-char pattern (</a>(X)\s) would
		/// fire only when EXACTLY one stray char sat before the space and miss
		/// every multi-char split; "+" covers both (length-1 runs still match).
		///
		/// The class is \p{L}\p{N} (any Unicode letter or digit) so the check fires
		/// across scripts — Latin, German/Turkish extended, Cyrillic — not only
		/// ASCII+Latin-Extended. The trailing \s is deliberately any whitespace:
		/// tightening it to ASCII-space-only would change which matches fire.
		///
		/// Tails that LEAD with punctuation/connectors (".com", "/home.html",
		/// "-Event") are NOT caught here — the run must START with a letter/digit.
		/// Distinguishing a leading-punctuation split from normal trailing
		/// punctuation ("&lt;/a&gt;." at sentence end) needs its own precise rule,
		/// which this check does not have.
		/// </summary>
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly System.Text.RegularExpressions.Regex PatternRegex = new(@"</a>([\p{L}\p{N}]+)\s", System.Text.RegularExpressions.RegexOptions.Compiled);

		private static System.Text.RegularExpressions.Regex Pattern() => PatternRegex;

		internal static IEnumerable<QualityIssue> Check(
			string filename, string html, ContentQualityConfig config)
		{
			foreach (System.Text.RegularExpressions.Match m in Pattern().Each(html))
			{
				// The position is one in the raw HTML, so the excerpt is cut from the raw HTML and only
				// then decoded (entities read as the visitor sees them). Decoding the whole page first
				// would shorten the text before the spot, so the excerpt would drift away from it — and
				// throw once the position lay past the shortened end, losing the page's later checks.
				yield return new QualityIssue(
					filename,
					"SPLIT_WORD_ANCHOR",
					$"Anchor closes mid-word — stray text after </a>: '{m.Groups[1].Value}'",
					System.Net.WebUtility.HtmlDecode(Excerpt.Around(html, m.Index, config.ContentQualityExcerptRadius)));
			}
		}
	}
}
