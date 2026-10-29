namespace CrawlerSuite.Core.Quality
{
	using System.Text.RegularExpressions;
	using HtmlAgilityPack;

	/// <summary>
	/// Reports non-breaking characters that CANNOT do what they were inserted to do.
	///
	/// An author — or their editor's autocorrect — puts a no-break space between two
	/// words to stop the line splitting there. In two positions that is impossible by
	/// specification, regardless of viewport, font or CSS:
	///
	///   LB12a — a glue character preceded by class SP, BA, HH or HY. Those classes
	///           permit a break AFTER themselves, and LB12a exempts them from the
	///           "do not break before glue" prohibition. The break stands, and the
	///           glue character is carried onto the next line as visible white space.
	///
	///   LB18  — a glue character immediately followed by an ordinary SPACE. LB18
	///           puts a break opportunity after that space, so the words are not held
	///           together — and the reader additionally sees a double space.
	///
	/// This is a defect an operator cannot see: nothing looks wrong until the viewport
	/// is narrow enough to wrap at that exact character. That is why it ships with a
	/// <see cref="QualityEnrichment"/> — the finding is worthless to a CMS author
	/// without the explanation.
	///
	/// DELIBERATELY NOT DOWNSTREAM OF Canonicalizer. Three of that class's
	/// transformations each destroy this finding independently: it replaces every tag
	/// with a SPACE (manufacturing a false LB12a hit at each <c>&lt;/tag&gt;</c>-then-
	/// glue boundary), it DROPS soft hyphens (hiding a real LB12a hit, since SHY is
	/// class BA), and it folds U+2013 to '-' (so the operator can no longer tell which
	/// character to fix). The check therefore runs on parsed DOM text with true
	/// character adjacency intact.
	///
	/// SCOPE — TEXT NODES ONLY. Attribute values are NOT examined, including those
	/// holding a serialized HTML fragment for a script to inject. That is not an
	/// oversight and not a gap to close later:
	///
	///   * What such an attribute finally becomes is decided by JavaScript this tool
	///     never executes. A script may inject the value verbatim, concatenate it with
	///     other fragments, rewrite or sanitise it first. A fragment that is
	///     well-formed alone can be malformed once combined, and one that looks
	///     malformed alone can be a legitimate half that the script closes. Any
	///     finding drawn from it would be a claim about a string whose final form was
	///     assumed.
	///   * The served HTML is what this tool observes; the DOM after script execution
	///     is what a reader experiences. Storing prose in an attribute drives those
	///     apart, and reporting on the payload would paper over the fact that they
	///     have diverged.
	///
	/// The honest finding about such content is structural — that it exists and is
	/// outside what static analysis can verify — and belongs to a check of its own,
	/// not to this one.
	///
	/// SCOPE — ONE TEXT NODE AT A TIME. Adjacency is not tracked across node
	/// boundaries, so <c>&lt;b&gt;a &lt;/b&gt;&amp;nbsp;b</c> is not reported even
	/// though the rendered flow makes that glue inert. Joining nodes to see across the
	/// boundary is what produces false positives — the caller's own <c>pageText</c>
	/// joins with a space and would report every <c>&lt;/tag&gt;</c>-then-glue pair —
	/// so the conservative direction is taken on purpose: a missed finding, never an
	/// invented one.
	/// </summary>
	internal static partial class NoBreakChars
	{
		/// <summary>
		/// Glue that cannot prevent the break before it, where the character in front
		/// is a dash, a hyphen or a soft hyphen. Invisible until the line wraps.
		/// </summary>
		internal const string InertIssueType = "LINE_BREAK_GLUE_INERT";

		/// <summary>
		/// Glue immediately next to an ordinary space, on either side. The reader
		/// sees a double space AND the glue holds nothing together.
		/// </summary>
		internal const string DoubleSpaceIssueType = "LINE_BREAK_GLUE_DOUBLE_SPACE";

		internal static IEnumerable<QualityIssue> Check(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			foreach (var node in doc.DocumentNode.Descendants())
			{
				if (node.NodeType != HtmlNodeType.Text
					|| node.ParentNode?.Name is "script" or "style" or "noscript")
				{
					continue;
				}

				var text = HtmlEntity.DeEntitize(node.InnerText) ?? string.Empty;

				// The enclosing element's markup, not just its text. A text excerpt
				// states the defect but cannot LOCATE it: the same few words often
				// recur many times on one page, and the operator has no way to tell
				// which occurrence the finding means. The element's own tag and
				// attributes are what make it findable in a source view — which is
				// exactly where the enrichment tells the author to go. Same approach
				// as MisplacedAnchors, for the same reason.
				var markup = node.ParentNode?.OuterHtml;

				foreach (var issue in Scan(
					filename, text, node.ParentNode?.Name ?? "text", config, markup,
					atLineStart: AtLineEdge(node, start: true), atLineEnd: AtLineEdge(node, start: false)))
				{
					yield return issue;
				}
			}
		}

		/// <summary>
		/// The scanner. Exposed for tests and for any future caller that already holds
		/// decoded text with its character adjacency intact.
		///
		/// <paramref name="markup"/> is the enclosing element's OuterHtml. When given,
		/// the reported Context is a window of that MARKUP centred on the offending
		/// character, rather than a window of the text. A text excerpt states the
		/// defect but cannot locate it — the same words recur many times on a page,
		/// and the operator has to find this one in a source view. The element's tag
		/// and attributes are what make it findable. When null (unit tests, or a
		/// caller holding only text) the text excerpt is used.
		///
		/// <paramref name="atLineStart"/> / <paramref name="atLineEnd"/>: nothing visible stands
		/// before / after this text in its line (it opens or closes a block, or borders a
		/// &lt;br&gt;). The browser drops collapsible spaces there, and a space with no word on
		/// one side doubles nothing, so such a pair is not reported. Default false: content is
		/// assumed on both sides.
		///
		/// A run of three or more spacing characters (no-break spaces and ordinary spaces, as
		/// rich-text editors write several typed spaces) is one finding — spacing made with
		/// spaces — not one per pair.
		/// </summary>
		internal static IEnumerable<QualityIssue> Scan(
			string filename, string text, string source, ContentQualityConfig config,
			string? markup = null, bool atLineStart = false, bool atLineEnd = false)
		{
			if (string.IsNullOrEmpty(text))
			{
				yield break;
			}

			// HTML collapses runs of ASCII white space to a single space before the
			// line breaking algorithm ever runs, so the scan must see the collapsed
			// form. Glue characters are not collapsible and survive untouched.
			var collapsed = AsciiWhitespaceRun().Replace(text, " ");

			// How many times each two-character offender pair has already been seen.
			// The same pair can occur several times in one element, so the excerpt
			// must centre on the RIGHT one — the Nth occurrence in the markup
			// corresponds to the Nth in the text, since the markup contains the text
			// verbatim and tags do not carry these characters.
			var seen = new Dictionary<string, int>(StringComparer.Ordinal);

			string Context(string needle, int textPosition)
			{
				if (string.IsNullOrEmpty(markup))
				{
					return LogExcerpt.Truncate(
						Excerpt.Around(collapsed, textPosition, config.ContentQualityExcerptRadius),
						config.ContentQualityMaxExcerpt);
				}

				// Render markers BEFORE centring, and search for the offender in
				// marked space. Doing it the other way round centres a window of raw
				// markup, then marker expansion (22 characters per no-break space)
				// grows it and the trailing cut can push the very defect being
				// reported out of the window. Passing int.MaxValue means "expand
				// markers, do not truncate" — the window is taken afterwards.
				// The markup may write the glue as an entity (&nbsp;, &#160; …): as the character it
				// is, so the excerpt finds and shows it like a literal one.
				var marked = LogExcerpt.Truncate(GlueEntity().Replace(markup, m => GlueOf(m.Value)), int.MaxValue);
				var markedNeedle = LogExcerpt.Truncate(needle, int.MaxValue);

				seen.TryGetValue(needle, out var already);
				seen[needle] = already + 1;

				// The same pair can occur several times in one element, so centre on
				// the RIGHT one: the Nth occurrence in the markup corresponds to the
				// Nth in the text, since the markup contains the text verbatim and
				// tags do not carry these characters. Fall back to the whole element
				// if the pair cannot be located (defensive — HAP may normalise on
				// serialise).
				var at = -1;
				for (int k = 0; k <= already; k++)
				{
					var next = marked.IndexOf(markedNeedle, at + 1, StringComparison.Ordinal);
					if (next < 0) { break; }
					at = next;
				}

				return at < 0
					? marked[..Math.Min(marked.Length, config.ContentQualityMaxExcerpt)]
					: Excerpt.Centred(marked, at, config.ContentQualityMaxExcerpt);
			}

			// A pair that is not reported still counts as an occurrence, so the excerpt of a
			// later one centres on the right spot.
			void Pass(string needle)
			{
				seen.TryGetValue(needle, out var already);
				seen[needle] = already + 1;
			}

			// Something visible on both sides of [from, to) within the line.
			bool BetweenContent(int from, int to) =>
				(from > 0 || !atLineStart) && (to < collapsed.Length || !atLineEnd);

			for (int i = 0; i < collapsed.Length; i++)
			{
				var glue = collapsed[i];
				if (!LineBreakData.IsAuthorGlue(glue))
				{
					continue;
				}

				if (IsGlueSpace(glue))
				{
					int from = i;
					while (from > 0 && IsSpacing(collapsed[from - 1]))
					{
						from--;
					}

					int to = i + 1;
					while (to < collapsed.Length && IsSpacing(collapsed[to]))
					{
						to++;
					}

					var run = collapsed[from..to];
					if (run.Length >= 3 && run.Contains(' ', StringComparison.Ordinal))
					{
						if (BetweenContent(from, to))
						{
							int glueCount = run.Count(IsGlueSpace);
							yield return new QualityIssue(
								filename,
								DoubleSpaceIssueType,
								$"{source}: run of {run.Length} spaces, {glueCount} of them {LineBreakData.Describe(glue)}",
								Context(run, from));
						}
						else
						{
							Pass(run);
						}

						i = to - 1;
						continue;
					}
				}

				// LB9 makes combining marks and ZWJ transparent — they inherit the
				// behaviour of the character before them — so step back over any
				// before asking what actually precedes this glue character.
				int back = i - 1;
				while (back >= 0 && LineBreakData.IsTransparent(collapsed[back]))
				{
					back--;
				}

				if (back >= 0 && LineBreakData.AllowsBreakBeforeGlue(collapsed[back]))
				{
					// Split by what the AUTHOR SEES, not by which rule fired. A space
					// in front means a visible double space; a dash in front means a
					// protection that silently does not hold. Different symptom,
					// different fix, different explanation — so different issue type.
					var precededBySpace = LineBreakData.IsSpaceLike(collapsed[back]);
					// A no-break hyphen after a space ("in- and ‑out") is no double space: the
					// hyphen is a visible character, nothing doubles.
					if (precededBySpace && (!IsGlueSpace(glue) || !BetweenContent(back, i + 1)))
					{
						Pass($"{collapsed[back]}{glue}");
						continue;
					}

					// A dash or hyphen opening its line ("<p>–&nbsp;Text", a bullet typed after
					// <br>): a break right after a line's first character is only taken when the
					// next word does not fit a whole line, so the glue has no effect anyone sees.
					if (!precededBySpace && atLineStart && collapsed[..back].Trim(' ').Length == 0)
					{
						Pass($"{collapsed[back]}{glue}");
						continue;
					}

					yield return new QualityIssue(
						filename,
						precededBySpace ? DoubleSpaceIssueType : InertIssueType,
						$"{source}: {LineBreakData.Describe(glue)} directly after "
							+ $"{LineBreakData.Describe(collapsed[back])}",
						Context($"{collapsed[back]}{glue}", i));
					continue;
				}

				// Only reached when the branch above did not fire, so a glue character
				// is reported once even when both conditions hold. Glue followed by an
				// ordinary space is the same visible double space seen from the other
				// side, so it carries the same type.
				// Glue space followed by a space. A no-break hyphen before a space is the
				// suspended compound ("input‑ and output"): correct, nothing doubles.
				if (IsGlueSpace(glue) && i + 1 < collapsed.Length && collapsed[i + 1] == ' ')
				{
					if (!BetweenContent(i, i + 2))
					{
						Pass($"{glue} ");
						continue;
					}

					yield return new QualityIssue(
						filename,
						DoubleSpaceIssueType,
						$"{source}: {LineBreakData.Describe(glue)} directly before "
							+ $"{LineBreakData.Describe(' ')}",
						Context($"{glue} ", i));
				}
			}
		}

		// No-break spaces: glue that is white space (the no-break hyphen is a visible character).
		private static bool IsGlueSpace(char c) => c is '\u00A0' or '\u202F' or '\u2007';

		private static bool IsSpacing(char c) => c == ' ' || IsGlueSpace(c);

		// Inline elements: they continue the line of their parent. Everything else (blocks, table
		// cells, list items, buttons and other inline-blocks) starts and ends a line box of its own.
		private static readonly HashSet<string> Inline = new(StringComparer.Ordinal)
		{
			"a", "abbr", "b", "bdi", "bdo", "cite", "code", "data", "del", "dfn", "em", "font", "i",
			"ins", "kbd", "label", "mark", "q", "s", "samp", "small", "span", "strong", "sub", "sup",
			"time", "u", "var",
		};

		/// <summary>
		/// Whether nothing visible stands before (start) or after (end) this text node in its line:
		/// only white-space text, comments, script/style/template, up through inline parents to the
		/// enclosing line box, or a &lt;br&gt;. Any other element (an image, a control) counts as
		/// content.
		/// </summary>
		internal static bool AtLineEdge(HtmlNode node, bool start)
		{
			for (var n = node; ; n = n.ParentNode)
			{
				for (var s = start ? n.PreviousSibling : n.NextSibling; s is not null; s = start ? s.PreviousSibling : s.NextSibling)
				{
					if (s.NodeType == HtmlNodeType.Comment || s.Name is "script" or "style" or "template")
					{
						continue;
					}

					if (s.NodeType == HtmlNodeType.Text)
					{
						if (AsciiWhitespaceRun().Replace(s.InnerText, string.Empty).Length == 0)
						{
							continue;
						}

						return false;
					}

					return s.Name == "br";
				}

				var parent = n.ParentNode;
				if (parent is null || parent.NodeType == HtmlNodeType.Document || !Inline.Contains(parent.Name))
				{
					return true;
				}
			}
		}

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex GlueEntityRegex = new(
			@"&(?:nbsp|#160|#x0*a0|#8239|#x0*202f|#8199|#x0*2007|#8209|#x0*2011);", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static Regex GlueEntity() => GlueEntityRegex;

		private static string GlueOf(string entity) => entity.ToLowerInvariant() switch
		{
			var e when e.Contains("8239") || e.Contains("202f") => "\u202F",
			var e when e.Contains("8199") || e.Contains("2007") => "\u2007",
			var e when e.Contains("8209") || e.Contains("2011") => "\u2011",
			_ => "\u00A0",
		};

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex AsciiWhitespaceRunRegex = new(@"[ \t\r\n\f]+", RegexOptions.Compiled);

		private static Regex AsciiWhitespaceRun() => AsciiWhitespaceRunRegex;
	}
}
