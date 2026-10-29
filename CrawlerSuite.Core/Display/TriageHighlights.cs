using System.Text;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Quality;

namespace CrawlerSuite.Core.Display
{
	/// <summary>
	/// What to mark inside a content-quality finding's context, as span kinds over the excerpt.
	/// Pure: no UI, no I/O. The views map the span kinds onto theme brushes (see
	/// <see cref="QualityMarks"/>).
	/// </summary>
	public static partial class TriageHighlights
	{
		/// <summary>The marker shown in place of an empty anchor's missing text.</summary>
		// Says in words what is missing, at the spot where it is missing: not every reader knows HTML or WCAG.
		// Tickets show it in their own language.
		internal const string EmptyLinkMarker = "[Missing link text]";

		/// <summary>
		/// Renders the offending glyph(s) at <paramref name="triggerPositions"/> in
		/// <paramref name="excerpt"/> as a deduplicated, counted hex list for the
		/// card's "Offenders" line — e.g. "U+201D ”" or "U+0022 \" ×2". Each entry
		/// pairs the Unicode code point with the glyph as it rendered, so a finding
		/// is self-proving: the byte and what it drew are both on the card, with no
		/// need to look the code point up in the raw HTML. Positions are the
		/// SAME offsets the colourer uses (LocateFlags over the excerpt), so
		/// the hex and the red mark cannot disagree — positions are deduped by
		/// position (as the colourer's HashSet does) and ordered left-to-right, so
		/// a glyph flagged by two findings at once is one entry, and distinct
		/// same-glyph offenders still collapse to "×N". Out-of-range positions are
		/// skipped; an empty list yields "" (caller omits the line entirely).
		/// Surrogate-guarded: a future astral-plane trigger prints the combined
		/// code point (U+XXXXX), never a lone half-pair — though every quote glyph
		/// in play is BMP.
		/// </summary>
		internal static string FormatQuoteOffenders(string excerpt, IReadOnlyList<int> triggerPositions)
		{
			if (string.IsNullOrEmpty(excerpt) || triggerPositions is not { Count: > 0 })
			{
				return string.Empty;
			}

			var order = new List<int>();                 // code points, in text order
			var counts = new Dictionary<int, int>();
			var glyphs = new Dictionary<int, string>();
			// Dedupe by POSITION first — the colourer marks red via a HashSet of
			// positions, so a glyph flagged by two findings at once (e.g. SYSTEM_MIX
			// and UNMATCHED landing on the same opener) is one red mark, not two.
			// Counting raw list entries would over-count it ("×2" with one red glyph).
			// Order left-to-right to match the paint walk.
			foreach (var pos in triggerPositions.Distinct().OrderBy(p => p))
			{
				if (pos < 0 || pos >= excerpt.Length)
				{
					continue;
				}

				char c = excerpt[pos];
				int cp;
				string glyph;
				if (char.IsHighSurrogate(c) && pos + 1 < excerpt.Length
					&& char.IsLowSurrogate(excerpt[pos + 1]))
				{
					cp = char.ConvertToUtf32(c, excerpt[pos + 1]);
					glyph = excerpt.Substring(pos, 2);
				}
				else
				{
					cp = c;
					glyph = c.ToString();
				}

				if (!counts.ContainsKey(cp))
				{
					order.Add(cp);
					glyphs[cp] = glyph;
				}
				counts[cp] = counts.GetValueOrDefault(cp) + 1;
			}

			if (order.Count == 0)
			{
				return string.Empty;
			}

			var parts = order.Select(cp =>
			{
				var tag = $"U+{cp:X4} {glyphs[cp]}";
				return counts[cp] > 1 ? $"{tag} \u00D7{counts[cp]}" : tag;
			});
			return string.Join(", ", parts);
		}

		/// <summary>
		/// Extracts the pattern string(s) to highlight from an UNWANTED_PATTERN
		/// composite Word, so every view marks identical spans on the same raw
		/// text. Word shapes:
		///   "UNWANTED_PATTERN:Category: Name — pattern: X"   → ["X"]
		///   "UNWANTED_PATTERN:Category: Name — patterns: X, Y" → ["X", "Y"]
		///   bare/other                                        → [whole-after-colon]
		/// Internal so it can be unit-tested.
		/// </summary>
		internal static string[] ExtractHighlightPatterns(string word)
		{
			if (string.IsNullOrEmpty(word))
			{
				return [];
			}

			var patternKey = word.Contains(':')
				? word[(word.IndexOf(':') + 1)..]
				: word;

			// Try singular first, then plural marker.
			var singleMarker = "— pattern: ";
			var multiMarker = "— patterns: ";
			var singleIdx = patternKey.IndexOf(singleMarker, StringComparison.OrdinalIgnoreCase);
			var multiIdx = patternKey.IndexOf(multiMarker, StringComparison.OrdinalIgnoreCase);
			if (singleIdx >= 0)
			{
				return [patternKey[(singleIdx + singleMarker.Length)..]];
			}
			if (multiIdx >= 0)
			{
				// Multiple matched patterns — split on ", " and highlight each.
				return patternKey[(multiIdx + multiMarker.Length)..]
					.Split(", ", StringSplitOptions.RemoveEmptyEntries);
			}
			return [patternKey];
		}

		/// <summary>
		/// Distinct operator-readable invisible-char markers present in a control-
		/// chars excerpt, in first-seen order. LogExcerpt writes invisibles out as
		/// bracketed tokens ([CR]/[LF]/[TAB] and [INVISIBLE &lt;kind&gt; U+XXXX]); the
		/// triage cards highlight exactly those tokens by passing them to the pattern
		/// painter. Internal so it can be unit-tested.
		/// </summary>
		internal static string[] ExtractControlCharMarkers(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return [];
			}

			var seen = new List<string>();
			var set = new HashSet<string>(StringComparer.Ordinal);
			foreach (System.Text.RegularExpressions.Match m in ControlCharMarkerRegex.Each(text))
			{
				if (set.Add(m.Value))
				{
					seen.Add(m.Value);
				}
			}
			return seen.ToArray();
		}

		private static readonly System.Text.RegularExpressions.Regex ControlCharMarkerRegex =
			new(@"\[(?:CR|LF|TAB|INVISIBLE [A-Z0-9 \-]+ U\+[0-9A-Fa-f]{4,6})\]",
				System.Text.RegularExpressions.RegexOptions.Compiled);

		// The [U+XXXX] markers AnnotateForm writes into a MIXED_NORMALIZATION excerpt,
		// so the painter can tint the combining codepoints that distinguish the two
		// encodings of the same word.
		internal static string[] ExtractDecompositionMarkers(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return [];
			}

			var seen = new List<string>();
			var set = new HashSet<string>(StringComparer.Ordinal);
			foreach (System.Text.RegularExpressions.Match m in DecompositionMarkerRegex.Each(text))
			{
				if (set.Add(m.Value))
				{
					seen.Add(m.Value);
				}
			}
			return seen.ToArray();
		}

		private static readonly System.Text.RegularExpressions.Regex DecompositionMarkerRegex =
			new(@"\[U\+[0-9A-Fa-f]{4,6}\]",
				System.Text.RegularExpressions.RegexOptions.Compiled);

		/// <summary>
		/// Splits a CONTROL_CHARS_IN_CONTENT Detail into (text before the location
		/// token, the token, trailing text) so the token — the element or meta
		/// selector naming WHERE the codepoint sits — can be tinted. The detector
		/// writes \"Found {name} in <{tag}> text\", \"… in <title> text\", and
		/// \"… in meta[@name=\\\"{n}\\\"] content\". The token runs from the first
		/// " in " to the trailing " text"/" content". Empty Token = unrecognised
		/// shape (caller renders the line plain). Pure, unit-tested.
		/// </summary>
		internal static (string Before, string Token, string After) SplitControlCharLocation(string detail)
		{
			if (string.IsNullOrEmpty(detail))
			{
				return (detail ?? string.Empty, string.Empty, string.Empty);
			}

			const string inMarker = " in ";
			var inIdx = detail.IndexOf(inMarker, StringComparison.Ordinal);
			if (inIdx < 0)
			{
				return (detail, string.Empty, string.Empty);
			}

			var tokenStart = inIdx + inMarker.Length;
			var suffixStart = -1;
			foreach (var suffix in new[] { " content", " text" })
			{
				if (detail.EndsWith(suffix, StringComparison.Ordinal))
				{
					suffixStart = detail.Length - suffix.Length;
					break;
				}
			}

			if (suffixStart <= tokenStart)
			{
				return (detail, string.Empty, string.Empty);
			}

			return (detail[..tokenStart], detail[tokenStart..suffixStart], detail[suffixStart..]);
		}

		/// <summary>
		/// Injects <see cref="EmptyLinkMarker"/> into every link of the excerpt without an
		/// accessible name, by the rules of the overload below without a finding's detail.
		/// </summary>
		internal static string InjectEmptyAnchorMarker(string excerpt) => InjectEmptyAnchorMarker(excerpt, null);

		/// <summary>
		/// Injects <see cref="EmptyLinkMarker"/> into the links of an excerpt that have no
		/// accessible name under the same rules as the detector (LinkAccessibility). With the
		/// finding's <paramref name="detail"/>, only the link it names (the "&lt;a href=…&gt;"
		/// at its end) is marked, so screen and file cannot disagree; an id an aria-labelledby
		/// points to outside the excerpt is then not held against other links. The marker goes
		/// right after the link's opening tag (before an image in an image link).
		/// </summary>
		internal static string InjectEmptyAnchorMarker(string excerpt, string? detail)
		{
			if (string.IsNullOrEmpty(excerpt))
			{
				return excerpt;
			}

			// A short excerpt the app already cut, not a whole page: no nesting cap needed (HtmlDocuments is for pages).
			var fragment = new HtmlAgilityPack.HtmlDocument();
			fragment.LoadHtml(excerpt);
			var ids = fragment.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlAgilityPack.HtmlNodeType.Element && n.Attributes["id"] is { Value.Length: > 0 })
				.GroupBy(n => n.Id, StringComparer.Ordinal)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

			var named = detail is null ? null : NamedElement(detail);
			var candidates = fragment.DocumentNode.Descendants("a")
				.Where(a => a.InnerStartIndex >= 0 && a.InnerStartIndex <= excerpt.Length
					&& Quality.LinkAccessibility.IsLinkLike(a)
					&& Quality.LinkAccessibility.IsRendered(a)
					&& !Quality.LinkAccessibility.IsAriaHidden(a))
				.ToList();

			List<HtmlAgilityPack.HtmlNode> marked;
			if (named is not null)
			{
				// The detector already decided: mark the link the Detail names (all of them when the
				// excerpt repeats it identically).
				marked = candidates.Where(a => Quality.LinkAccessibility.Describe(a) == named).ToList();
			}
			else
			{
				marked = candidates.Where(a => Quality.LinkAccessibility.MissingName(a, id => ids.GetValueOrDefault(id)) is not null).ToList();
			}

			var sb = new StringBuilder(excerpt);
			foreach (var at in marked.Select(a => a.InnerStartIndex).Distinct().OrderByDescending(i => i))
			{
				sb.Insert(at, EmptyLinkMarker);
			}

			return sb.ToString();
		}

		/// <summary>"… — &lt;a href="/x"&gt;" → "&lt;a href="/x"&gt;": the element a finding's Detail names at its end.</summary>
		private static string? NamedElement(string detail)
		{
			int at = detail.LastIndexOf(" — <", StringComparison.Ordinal);
			return at < 0 ? null : detail[(at + 3)..];
		}

		/// <summary>A string heuristic for empty anchors, kept for reference; the marker itself follows LinkAccessibility.</summary>
		internal static string InjectEmptyAnchorMarkerLegacy(string excerpt)
		{
			if (string.IsNullOrEmpty(excerpt))
			{
				return excerpt;
			}

			var sb = new StringBuilder(excerpt.Length + EmptyLinkMarker.Length);
			var pos = 0;
			while (pos < excerpt.Length)
			{
				// Find the next opening "<a" that begins a tag — i.e. "<a" followed
				// by whitespace or ">" (so "<article"/"<aside" don't match).
				var openTag = FindAnchorOpenTag(excerpt, pos);
				if (openTag < 0)
				{
					sb.Append(excerpt, pos, excerpt.Length - pos);
					break;
				}

				// End of the opening tag (the '>' that closes "<a …>").
				var openEnd = excerpt.IndexOf('>', openTag);
				if (openEnd < 0)
				{
					// Malformed / truncated excerpt — no tag close. Emit the rest
					// verbatim rather than guessing.
					sb.Append(excerpt, pos, excerpt.Length - pos);
					break;
				}

				// Matching close for this anchor.
				var closeIdx = excerpt.IndexOf("</a>", openEnd + 1, StringComparison.OrdinalIgnoreCase);
				if (closeIdx < 0)
				{
					sb.Append(excerpt, pos, excerpt.Length - pos);
					break;
				}

				// Inner content between ">" and "</a>".
				var inner = excerpt[(openEnd + 1)..closeIdx];

				// Emit up to and including the opening tag's ">".
				sb.Append(excerpt, pos, (openEnd + 1) - pos);

				if (IsEssentiallyEmptyAnchorInner(inner))
				{
					// Inject marker, then the (whitespace/empty) inner verbatim so
					// the operator still sees exactly what was on the page, then the
					// close handled on the next loop pass.
					sb.Append(EmptyLinkMarker);
				}

				sb.Append(inner);
				sb.Append("</a>");
				pos = closeIdx + "</a>".Length;
			}

			return sb.ToString();
		}

		/// <summary>
		/// Foreground colour map for a MISPLACED_ANCHOR_EMPTY excerpt that has already had
		/// the marker injected by <see cref="InjectEmptyAnchorMarker"/>. Each marked empty
		/// anchor contributes Structure spans (&lt;a, &gt;, &lt;/a&gt;), an Href span for the
		/// href attribute, Attr spans for the remaining attributes, and a Marker span for the
		/// injected token; everything else (leading text, non-empty anchors, trailing markup)
		/// is Context. The spans are ordered and contiguous over the whole string, so
		/// the view paints by walking them once.
		/// </summary>
		internal static IReadOnlyList<EmptyAnchorSpan> ComputeEmptyAnchorSpans(string injected)
		{
			var spans = new List<EmptyAnchorSpan>();
			if (string.IsNullOrEmpty(injected))
			{
				return spans;
			}

			var pos = 0;
			while (pos < injected.Length)
			{
				var anchorAt = FindNextMarkedAnchor(injected, pos, out var openEnd, out var closeIdx);
				if (anchorAt < 0)
				{
					spans.Add(new(pos, injected.Length - pos, EmptyAnchorSpanKind.Context));
					break;
				}

				if (anchorAt > pos)
				{
					spans.Add(new(pos, anchorAt - pos, EmptyAnchorSpanKind.Context));
				}

				// "<a"
				spans.Add(new(anchorAt, 2, EmptyAnchorSpanKind.Structure));

				// attributes between "<a" and ">"
				EmitAnchorAttrSpans(injected, anchorAt + 2, openEnd, spans);

				// ">"
				spans.Add(new(openEnd, 1, EmptyAnchorSpanKind.Structure));

				// injected marker, right after ">"
				var markerStart = openEnd + 1;
				spans.Add(new(markerStart, EmptyLinkMarker.Length, EmptyAnchorSpanKind.Marker));

				// any (whitespace/empty) inner content between marker and "</a>"
				var innerStart = markerStart + EmptyLinkMarker.Length;
				if (closeIdx > innerStart)
				{
					spans.Add(new(innerStart, closeIdx - innerStart, EmptyAnchorSpanKind.Context));
				}

				// "</a>"
				spans.Add(new(closeIdx, 4, EmptyAnchorSpanKind.Structure));

				pos = closeIdx + 4;
			}

			return spans;
		}

		/// <summary>
		/// Index of the next anchor open tag at/after <paramref name="from"/> whose inner
		/// content begins with the injected <see cref="EmptyLinkMarker"/> — i.e. the empty
		/// anchor the marker was placed in. Non-empty anchors are skipped. Returns -1 (and
		/// leaves the out params at -1) when none remains.
		/// </summary>
		private static int FindNextMarkedAnchor(string s, int from, out int openEnd, out int closeIdx)
		{
			openEnd = -1;
			closeIdx = -1;
			var p = from;
			while (true)
			{
				var a = FindAnchorOpenTag(s, p);
				if (a < 0)
				{
					return -1;
				}

				var oe = s.IndexOf('>', a);
				if (oe < 0)
				{
					return -1;
				}

				var markerStart = oe + 1;
				if (markerStart + EmptyLinkMarker.Length <= s.Length
					&& string.CompareOrdinal(s, markerStart, EmptyLinkMarker, 0, EmptyLinkMarker.Length) == 0)
				{
					var ci = s.IndexOf("</a>", markerStart, StringComparison.OrdinalIgnoreCase);
					if (ci < 0)
					{
						return -1;
					}

					openEnd = oe;
					closeIdx = ci;
					return a;
				}

				p = oe + 1;
			}
		}

		/// <summary>
		/// Splits an anchor's attribute region [<paramref name="attrStart"/>,
		/// <paramref name="attrEnd"/>) into an Href span (the whole <c>href="…"</c>
		/// attribute) and Attr spans for everything else. With no href the whole region is
		/// one Attr span. The emitted spans are contiguous and cover the region exactly.
		/// </summary>
		private static void EmitAnchorAttrSpans(
			string s, int attrStart, int attrEnd, List<EmptyAnchorSpan> spans)
		{
			if (attrEnd <= attrStart)
			{
				return;
			}

			var hrefAt = IndexOfHrefAttr(s, attrStart, attrEnd);
			if (hrefAt < 0)
			{
				spans.Add(new(attrStart, attrEnd - attrStart, EmptyAnchorSpanKind.Attr));
				return;
			}

			if (hrefAt > attrStart)
			{
				spans.Add(new(attrStart, hrefAt - attrStart, EmptyAnchorSpanKind.Attr));
			}

			var hrefEnd = HrefAttrEnd(s, hrefAt, attrEnd);
			spans.Add(new(hrefAt, hrefEnd - hrefAt, EmptyAnchorSpanKind.Href));

			if (hrefEnd < attrEnd)
			{
				spans.Add(new(hrefEnd, attrEnd - hrefEnd, EmptyAnchorSpanKind.Attr));
			}
		}

		/// <summary>
		/// Start index of the <c>href</c> attribute within [start, end), or -1. Matches a
		/// whitespace-preceded (or region-start) "href" followed by '=' (so "hreflang" and
		/// substrings are rejected). Case-insensitive on the name.
		/// </summary>
		private static int IndexOfHrefAttr(string s, int start, int end)
		{
			for (var i = start; i + 4 <= end; i++)
			{
				if (!(i == start || char.IsWhiteSpace(s[i - 1])))
				{
					continue;
				}

				if (string.Compare(s, i, "href", 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
				{
					continue;
				}

				var j = i + 4;
				while (j < end && char.IsWhiteSpace(s[j]))
				{
					j++;
				}

				if (j < end && s[j] == '=')
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>
		/// End index (exclusive) of the href attribute starting at <paramref name="hrefAt"/>:
		/// past the name, '=', and the quoted or unquoted value. A bare "href" with no value
		/// returns the index just past the name.
		/// </summary>
		private static int HrefAttrEnd(string s, int hrefAt, int end)
		{
			var j = hrefAt + 4;
			while (j < end && char.IsWhiteSpace(s[j]))
			{
				j++;
			}

			if (j >= end || s[j] != '=')
			{
				return Math.Min(hrefAt + 4, end);
			}

			j++;   // past '='
			while (j < end && char.IsWhiteSpace(s[j]))
			{
				j++;
			}

			if (j < end && (s[j] == '"' || s[j] == '\''))
			{
				var quote = s[j];
				j++;
				while (j < end && s[j] != quote)
				{
					j++;
				}

				if (j < end)
				{
					j++;   // include the closing quote
				}

				return j;
			}

			while (j < end && !char.IsWhiteSpace(s[j]))
			{
				j++;
			}

			return j;
		}

		/// <summary>
		/// Index of the next "&lt;a" that opens an anchor tag at or after
		/// <paramref name="from"/> — "&lt;a" followed by ASCII whitespace or "&gt;".
		/// Rejects "&lt;article", "&lt;aside", etc. Case-insensitive on the tag name.
		/// Returns -1 if none.
		/// </summary>
		private static int FindAnchorOpenTag(string text, int from)
		{
			var i = from;
			while (true)
			{
				var hit = text.IndexOf("<a", i, StringComparison.OrdinalIgnoreCase);
				if (hit < 0)
				{
					return -1;
				}

				var after = hit + 2;
				if (after >= text.Length)
				{
					return -1;   // "<a" at very end — no tag body
				}
				var c = text[after];
				if (c == '>' || char.IsWhiteSpace(c))
				{
					return hit;
				}
				i = hit + 2;   // false positive (<article …) — keep scanning
			}
		}

		/// <summary>
		/// True when an anchor's inner HTML carries no visible text — empty,
		/// whitespace-only, or only-empty-child-elements — and is not an image
		/// link. Mirrors the detector's InnerText.Trim()-is-blank verdict on a raw
		/// string: strip all tags, decode nothing (entities are not whitespace),
		/// and trim. An inner &lt;img&gt; counts as content (intentional image link),
		/// matching MisplacedAnchors.Check's &lt;img&gt; exclusion.
		/// </summary>
		private static bool IsEssentiallyEmptyAnchorInner(string inner)
		{
			if (string.IsNullOrWhiteSpace(inner))
			{
				return true;   // "" or whitespace-only
			}

			// Image link — content-bearing by the detector's rule, never marked.
			if (inner.IndexOf("<img", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}

			// Strip tags and see whether any non-whitespace text remains.
			var sb = new StringBuilder(inner.Length);
			var inTag = false;
			foreach (var c in inner)
			{
				if (c == '<') { inTag = true; continue; }
				if (c == '>') { inTag = false; continue; }
				if (!inTag)
				{
					sb.Append(c);
				}
			}
			return string.IsNullOrWhiteSpace(sb.ToString());
		}

		// Max characters of the orphaned tail to highlight after </a>. The tail is
		// otherwise bounded by the first literal space (U+0020). This cap is only a
		// runaway guard for a tail with no space within the visible window (a long
		// URL, say) — it is NOT a correctness boundary: a real orphan (even a long
		// German compound such as "-Gletscherschmelzwasser", 23 chars) is meant to
		// colour fully, and a clipped highlight still flags "investigate here". 24
		// clears realistic orphans while staying well under
		// the excerpt half-window (ContentQualityExcerptRadius, 120) so surrounding
		// context the operator needs to judge the finding is never swallowed.

		private const int SplitTailMaxHighlight = 24;

		/// <summary>
		/// Computes quote-highlight spans for a quote-issue excerpt: every
		/// typographic quote glyph (<see cref="HighlightQuoteChars"/>)
		/// becomes a one-char span; positions in <paramref name="triggerPositions"/>
		/// are marked as the trigger (offender), the rest as context. Pure and
		/// testable; returns spans ordered by Start, non-overlapping. An empty
		/// trigger set yields all-context spans — the graceful fallback when the
		/// offending glyph could not be located.
		/// </summary>
		internal static List<QuoteHighlightSpan> ComputeQuoteSpans(
			string text, IReadOnlyCollection<int> triggerPositions)
		{
			var spans = new List<QuoteHighlightSpan>();
			if (string.IsNullOrEmpty(text))
			{
				return spans;
			}

			var triggers = triggerPositions as HashSet<int>
				?? (triggerPositions is { Count: > 0 } ? new HashSet<int>(triggerPositions) : null);

			for (int i = 0; i < text.Length; i++)
			{
				if (HighlightQuoteChars.Contains(text[i]))
				{
					spans.Add(new QuoteHighlightSpan(
						i, 1, IsTrigger: triggers is not null && triggers.Contains(i)));
				}
			}

			return spans;
		}

		/// <summary>
		/// Computes 1-char highlight spans for every typographic ligature glyph in
		/// <paramref name="text"/> (U+FB00–U+FB06, per
		/// <see cref="HighlightLigatureChars"/>). Pure mirror of
		/// <see cref="ComputeQuoteSpans"/> without the trigger concept — every
		/// ligature is an offender, so all spans render in the emphasis scheme.
		/// Returns an empty list when the text has none (caller degrades to plain).
		/// </summary>
		internal static List<QuoteHighlightSpan> ComputeLigatureSpans(string text)
		{
			var spans = new List<QuoteHighlightSpan>();
			if (string.IsNullOrEmpty(text))
			{
				return spans;
			}

			for (int i = 0; i < text.Length; i++)
			{
				if (HighlightLigatureChars.Contains(text[i]))
				{
					spans.Add(new QuoteHighlightSpan(i, 1, IsTrigger: true));
				}
			}

			return spans;
		}

		// Spans for every decomposed grapheme (base + composing combining mark) in an
		// excerpt — the DECOMPOSED_TEXT highlight. Invisible on screen (renders as a
		// normal 'ü'/'ö'), so the span is the only cue. Same composability test as the
		// detector; one length-2 span per base+mark, skipping stacked marks so spans
		// never overlap (the painter requires non-overlapping ascending spans). Composed
		// graphemes naming the defect in the Detail are single chars, so they are not
		// matched — only the actual NFD sequences in the excerpt light up.
		internal static List<QuoteHighlightSpan> ComputeDecompositionSpans(string text)
		{
			var spans = new List<QuoteHighlightSpan>();
			if (string.IsNullOrEmpty(text))
			{
				return spans;
			}

			for (int i = 1; i < text.Length; i++)
			{
				if (!IsCombiningMarkChar(text[i]) || IsCombiningMarkChar(text[i - 1]) || char.IsSurrogate(text[i - 1]))
				{
					continue; // half of a surrogate pair is not a base letter (and Normalize rejects it)
				}

				var pair = text.Substring(i - 1, 2);
				if (pair.Normalize(System.Text.NormalizationForm.FormC).Length < pair.Length)
				{
					spans.Add(new QuoteHighlightSpan(i - 1, 2, IsTrigger: true));
				}
			}

			return spans;
		}

		private static bool IsCombiningMarkChar(char c)
		{
			var cat = char.GetUnicodeCategory(c);
			return cat == System.Globalization.UnicodeCategory.NonSpacingMark
				|| cat == System.Globalization.UnicodeCategory.SpacingCombiningMark
				|| cat == System.Globalization.UnicodeCategory.EnclosingMark;
		}

		/// <summary>
		/// Computes the coloured spans for a SPLIT_WORD_ANCHOR excerpt: for every
		/// closing &lt;/a&gt; that is immediately followed by a non-space character
		/// (the split signature — a token continues past the tag), three spans are
		/// emitted — the anchor's opening &lt;a…&gt; and closing &lt;/a&gt; TAGS,
		/// the INSIDE link text between them, and the orphaned TAIL after &lt;/a&gt;.
		///
		/// Pure, so the offset logic is unit-tested directly. The view only
		/// paints these spans.
		///
		/// Tail rule (deliberately mechanical — a visual indicator, not a verdict):
		/// the tail runs from just after &lt;/a&gt; up to the first literal space
		/// (U+0020) OR <see cref="SplitTailMaxHighlight"/> characters, whichever
		/// comes first. EVERY other character is consumed — tabs, all Unicode and
		/// zero-width spaces, line separators, punctuation, connectors, letters,
		/// digits. Only a plain space stops it. This is language-blind on purpose:
		/// the inside/tail colour straddle of the red close tag shows "linked part /
		/// escaped part" without the operator needing to read the script (German,
		/// Turkish, Cyrillic). Returns spans ordered by Start, non-overlapping.
		/// </summary>
		internal static IReadOnlyList<SplitSpan> ComputeSplitWordSpans(string excerpt)
		{
			var spans = new List<SplitSpan>();
			if (string.IsNullOrEmpty(excerpt))
			{
				return spans;
			}

			const string close = "</a>";
			var searchFrom = 0;
			while (true)
			{
				var closeAt = excerpt.IndexOf(close, searchFrom, StringComparison.OrdinalIgnoreCase);
				if (closeAt < 0)
				{
					break;
				}
				var afterClose = closeAt + close.Length;
				searchFrom = afterClose;

				// Split signature: </a> immediately followed by a non-space char.
				// A space (or end of excerpt) here means a clean link, not a split.
				if (afterClose >= excerpt.Length || excerpt[afterClose] == ' ')
				{
					continue;
				}

				// ── Inside + opening tag ──────────────────────────────────────
				// The inside (link text) is anchored to the ">" that closes the
				// opening tag — the last ">" before this "</a>". That ">" is in-window
				// even when the "<a" itself has scrolled off the left edge (the
				// excerpt is centred on the </a> boundary, so a long anchor's
				// open tag is frequently truncated away). Anchoring to the ">" — not
				// to "<a" — is what lets the inside colour in that common case; a raw
				// ">" never appears in link text (it would be &gt;), so the last ">"
				// before </a> reliably lands on the opening tag's close bracket and
				// the attribute markup before it stays uncoloured.
				//
				// The opening-tag SPAN ("<a…>") is emitted only when the "<a" start
				// is actually in-window; when it is truncated the inside still colours
				// (bounded by the ">"), just without a red open-tag span.
				var insideStart = -1;
				var openAt = LastIndexOfAnchorOpen(excerpt, closeAt);
				if (openAt >= 0)
				{
					var openTagEnd = excerpt.IndexOf('>', openAt);
					if (openTagEnd >= 0 && openTagEnd < closeAt)
					{
						// Opening tag span: "<a…>" inclusive of the ">".
						spans.Add(new SplitSpan(openAt, (openTagEnd + 1) - openAt, SplitSpanKind.Tag));
						insideStart = openTagEnd + 1;
					}
				}
				if (insideStart < 0)
				{
					// Open "<a" out of window — fall back to the last ">" before
					// "</a>" as the inside's left bound.
					var precedingGt = excerpt.LastIndexOf('>', closeAt - 1);
					if (precedingGt >= 0)
					{
						insideStart = precedingGt + 1;
					}
				}
				if (insideStart >= 0 && closeAt > insideStart)
				{
					spans.Add(new SplitSpan(insideStart, closeAt - insideStart, SplitSpanKind.Inside));
				}

				// ── Closing tag ───────────────────────────────────────────────
				spans.Add(new SplitSpan(closeAt, close.Length, SplitSpanKind.Tag));

				// ── Tail ──────────────────────────────────────────────────────
				// From just after </a> to first literal space or the cap.
				var tailEnd = afterClose;
				var limit = Math.Min(excerpt.Length, afterClose + SplitTailMaxHighlight);
				while (tailEnd < limit && excerpt[tailEnd] != ' ')
				{
					tailEnd++;
				}
				if (tailEnd > afterClose)
				{
					spans.Add(new SplitSpan(afterClose, tailEnd - afterClose, SplitSpanKind.Tail));
				}

				// Continue scanning after this tail for any further splits.
				searchFrom = Math.Max(searchFrom, tailEnd);
			}

			return spans;
		}

		/// <summary>
		/// Inline closing tags whose boundary glues text (mirrors the spell harvester's
		/// InlinePhrasingGlue and ContentQuality.InlinePhrasingElements).
		/// </summary>
		private static readonly string[] InlineCloseTags =
		{
			"</span>", "</b>", "</i>", "</em>", "</strong>",
			"</mark>", "</small>", "</u>", "</s>", "</wbr>",
		};

		/// <summary>
		/// Spans for a WORD_COLLISION excerpt — raw html of the shape
		/// <c>WORD1&lt;/tag&gt;WORD2</c>. Three spans: WORD1 (the inline element's text,
		/// Inside), the closing <c>&lt;/tag&gt;</c> (Tag), and WORD2 (the colliding
		/// bare tail, Tail). The seam is the
		/// inline closing tag immediately followed by an uppercase letter — unique even
		/// when WORD1 contains nested inline markup (those inner close tags are followed
		/// by lowercase or further tags, not the uppercase collider). Without such a
		/// seam, the leading-seam shape (text&lt;tag&gt;WORD) is tried with
		/// <see cref="ComputeLeadingSeamSpans"/>. The view only paints these spans.
		/// </summary>
		internal static IReadOnlyList<SplitSpan> ComputeWordCollisionSpans(string excerpt)
		{
			var spans = new List<SplitSpan>();
			if (string.IsNullOrEmpty(excerpt))
			{
				return spans;
			}

			// Locate the seam: an inline closing tag immediately followed by an
			// uppercase letter (the lowercase→Uppercase collision).
			int closeAt = -1, closeLen = 0;
			string closeTag = string.Empty;
			for (int i = 0; i < excerpt.Length && closeAt < 0; i++)
			{
				if (excerpt[i] != '<')
				{
					continue;
				}

				foreach (var tag in InlineCloseTags)
				{
					if (i + tag.Length <= excerpt.Length
						&& string.Compare(excerpt, i, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) == 0)
					{
						int after = i + tag.Length;
						if (after < excerpt.Length && char.IsUpper(excerpt[after]))
						{
							closeAt = i;
							closeLen = tag.Length;
							closeTag = tag;
						}

						break;
					}
				}
			}

			if (closeAt < 0)
			{
				// No trailing (</tag>WORD) seam — try the leading (WORD<tag>) shape.
				return ComputeLeadingSeamSpans(excerpt);
			}

			var afterClose = closeAt + closeLen;

			// ── WORD1: inline text, anchored to the ">" of the OPENING tag, so the
			// text is highlighted even when an inner tag (e.g. a <br> spacer) sits
			// between it and </tag>. Element name comes from the close tag ("</span>"
			// → "span"); we find the matching "<span…>" before the seam. Falls back to
			// the last ">" before the seam if the open tag scrolled out of the window.
			var element = closeTag.Substring(2, closeTag.Length - 3);
			var insideStart = OpenTagContentStart(excerpt, "<" + element, closeAt);
			if (insideStart < 0)
			{
				var precedingGt = excerpt.LastIndexOf('>', closeAt - 1);
				insideStart = precedingGt >= 0 ? precedingGt + 1 : 0;
			}

			// Walk the inside region: text → Inside, <br…> spacer runs →
			// BrSpacer. Only <br> is carved out — it is the spacing abuse
			// being surfaced; any other inner markup stays within the Inside run.
			var pos = insideStart;
			while (pos < closeAt)
			{
				var br = IndexOfBrTag(excerpt, pos, closeAt);
				if (br < 0)
				{
					if (closeAt > pos)
					{
						spans.Add(new SplitSpan(pos, closeAt - pos, SplitSpanKind.Inside));
					}

					break;
				}

				if (br > pos)
				{
					spans.Add(new SplitSpan(pos, br - pos, SplitSpanKind.Inside));
				}

				var gt = excerpt.IndexOf('>', br);
				var brEnd = (gt >= 0 && gt < closeAt) ? gt + 1 : closeAt;
				spans.Add(new SplitSpan(br, brEnd - br, SplitSpanKind.BrSpacer));
				pos = brEnd;
			}

			// ── </tag> ──
			spans.Add(new SplitSpan(closeAt, closeLen, SplitSpanKind.Tag));

			// ── WORD2: colliding tail, from just after </tag> to first space or cap ──
			var tailEnd = afterClose;
			var limit = Math.Min(excerpt.Length, afterClose + SplitTailMaxHighlight);
			while (tailEnd < limit && excerpt[tailEnd] != ' ')
			{
				tailEnd++;
			}
			if (tailEnd > afterClose)
			{
				spans.Add(new SplitSpan(afterClose, tailEnd - afterClose, SplitSpanKind.Tail));
			}

			return spans;
		}

		/// <summary>
		/// Spans for the leading-seam WORD_COLLISION shape — <c>WORD1&lt;tag…&gt;WORD2</c>,
		/// where a bare lowercase-ending word abuts an OPENING inline tag whose text
		/// starts uppercase (e.g. <c>Floeco&lt;span&gt;&lt;sup&gt;TM</c>). Mirrors the
		/// trailing-seam painting so both read identically: WORD1 (Inside),
		/// the opening tag(s) (Tag), WORD2 (Tail). The trailing-seam path in
		/// <see cref="ComputeWordCollisionSpans"/> is tried first; this is its fallback.
		/// One seam per excerpt; returns no spans when none is present.
		/// </summary>
		internal static IReadOnlyList<SplitSpan> ComputeLeadingSeamSpans(string excerpt)
		{
			var spans = new List<SplitSpan>();
			if (string.IsNullOrEmpty(excerpt))
			{
				return spans;
			}

			for (int i = 1; i < excerpt.Length; i++)
			{
				// Seam candidate: a '<' directly after a lowercase letter (WORD1's tail)
				// that opens a tag (not a closing tag — that is the trailing shape).
				if (excerpt[i] != '<' || !char.IsLower(excerpt[i - 1]))
				{
					continue;
				}

				if (i + 1 < excerpt.Length && excerpt[i + 1] == '/')
				{
					continue;
				}

				// Skip consecutive opening inline tags to the first text char after them.
				int p = i;
				while (p < excerpt.Length && excerpt[p] == '<'
					&& (p + 1 >= excerpt.Length || excerpt[p + 1] != '/'))
				{
					int gt = excerpt.IndexOf('>', p);
					if (gt < 0)
					{
						p = excerpt.Length;
						break;
					}

					p = gt + 1;
				}

				// A real collision only when the post-tag text starts uppercase.
				if (p >= excerpt.Length || !char.IsUpper(excerpt[p]))
				{
					continue;
				}

				// WORD1: the lowercase-ending word immediately before the tag.
				int w1 = i;
				while (w1 > 0 && (char.IsLetter(excerpt[w1 - 1]) || excerpt[w1 - 1] == '-'))
				{
					w1--;
				}

				if (w1 < i)
				{
					spans.Add(new SplitSpan(w1, i - w1, SplitSpanKind.Inside));
				}

				// Opening tag(s): the markup between WORD1 and WORD2.
				spans.Add(new SplitSpan(i, p - i, SplitSpanKind.Tag));

				// WORD2: colliding tail, to the next tag, space, or cap.
				int tailEnd = p;
				int limit = Math.Min(excerpt.Length, p + SplitTailMaxHighlight);
				while (tailEnd < limit && excerpt[tailEnd] != ' ' && excerpt[tailEnd] != '<')
				{
					tailEnd++;
				}

				if (tailEnd > p)
				{
					spans.Add(new SplitSpan(p, tailEnd - p, SplitSpanKind.Tail));
				}

				break;   // one seam per excerpt, matching the trailing-seam path
			}

			return spans;
		}
		/// <summary>
		/// Index just past the
		/// "&gt;" of the last <paramref name="openPrefix"/> ("&lt;span", "&lt;b", …)
		/// opening tag at or before <paramref name="closeAt"/>. Requires the char after
		/// the prefix to be whitespace or "&gt;" (so "&lt;span" matches but "&lt;spanner"
		/// does not). Returns -1 if no such open tag with a "&gt;" before the seam.
		/// </summary>
		private static int OpenTagContentStart(string text, string openPrefix, int closeAt)
		{
			var search = closeAt;
			while (search > 0)
			{
				var at = text.LastIndexOf(openPrefix, search - 1, StringComparison.OrdinalIgnoreCase);
				if (at < 0)
				{
					return -1;
				}

				var after = at + openPrefix.Length;
				if (after < text.Length
					&& (text[after] == '>' || char.IsWhiteSpace(text[after])))
				{
					var gt = text.IndexOf('>', at);
					if (gt >= 0 && gt < closeAt)
					{
						return gt + 1;
					}
				}

				search = at;   // false match (<spanner…) — keep walking back
			}

			return -1;
		}

		/// <summary>
		/// Index of the next "&lt;br" tag in [<paramref name="from"/>,
		/// <paramref name="end"/>) — matching &lt;br&gt;, &lt;br/&gt;, and
		/// &lt;br class="…"&gt;, but not other "&lt;br…"-prefixed names. Returns -1 if none.
		/// </summary>
		private static int IndexOfBrTag(string text, int from, int end)
		{
			var i = from;
			while (i < end)
			{
				var at = text.IndexOf("<br", i, StringComparison.OrdinalIgnoreCase);
				if (at < 0 || at >= end)
				{
					return -1;
				}

				var after = at + 3;
				if (after < text.Length
					&& (text[after] == '>' || text[after] == '/' || char.IsWhiteSpace(text[after])))
				{
					return at;
				}

				i = at + 3;   // "<break"/"<brxyz" — not a <br>, keep scanning
			}

			return -1;
		}

		/// <summary>
		/// Index of the opening "&lt;a" that begins the anchor closing at
		/// <paramref name="closeAt"/> — the last "&lt;a" (followed by whitespace or
		/// "&gt;") at or before <paramref name="closeAt"/>. Returns -1 if none is in
		/// window. Mirrors the anchor-open recognition in InjectEmptyAnchorMarker
		/// ("&lt;article" etc. are not anchors).
		/// </summary>
		private static int LastIndexOfAnchorOpen(string text, int closeAt)
		{
			var i = closeAt;
			while (i > 0)
			{
				var hit = text.LastIndexOf("<a", i - 1, StringComparison.OrdinalIgnoreCase);
				if (hit < 0)
				{
					return -1;
				}
				var after = hit + 2;
				if (after < text.Length)
				{
					var c = text[after];
					if (c == '>' || char.IsWhiteSpace(c))
					{
						return hit;
					}
				}
				i = hit;   // false positive (<article…) — keep walking back
			}
			return -1;
		}

		// ── ADJACENT_ANCHOR same-href hint computation ────────────────
		//
		// Pure span-computation for the ADJACENT_ANCHOR visualization layer. The
		// detector flags any literal </a><a collision honestly (broad on purpose);
		// this helper analyses the excerpt to surface structural facts (matching
		// hrefs, matching titles, placeholder hrefs) as coloured spans. The
		// renderer downstream is dumb — it just paints what this helper computes.
		// No interpretation: every span is a fact, the operator interprets.
		//
		// The shape of the computation:
		//   1. Find every literal "</a><a" (case-insensitive) → Collision span.
		//   2. For each collision, parse the LEFT anchor (ending at the </a>) and
		//      RIGHT anchor (starting at the <a) — extract href value, title value,
		//      and inner-text bounds for each side.
		//   3. Compare hrefs across the pair:
		//        match (real)        → HrefMatch on both sides + AnchorTextHint on inner text
		//        match (placeholder) → HrefPlaceholder on both sides
		//        differ              → HrefBaseline on each side (mild visibility, no claim)
		//        one or both missing → HrefBaseline only on present side(s)
		//   4. Compare titles: if both present and identical, emit TitleMatch on both.
		//   5. Sort all spans by Start, dedupe by Start (cluster cases like
		//      <a></a><a></a><a> produce overlapping pair analyses on the middle
		//      anchor — keep first occurrence of each Start).
		//
		// Edge cases handled:
		//   - Truncated left anchor (the <a is off the left excerpt edge): no
		//     opening-tag <a in window → fall back to last ">" before </a> as the
		//     inner-text left bound. Same approach as ComputeSplitWordSpans.
		//   - Truncated right anchor (the > of its open tag is off the right
		//     excerpt edge): no closing ">" in window → no inner-text span, but
		//     attribute parsing may still find href/title in the visible portion.
		//   - Both double-quoted (href="x") and single-quoted (href='x') values.
		//   - Attribute order variation.
		//   - Anchors with no href attribute at all (legacy <a name=…> shapes).
		//   - Empty href value ("") → treated as placeholder-class.

		/// <summary>
		/// Computes coloured spans for an ADJACENT_ANCHOR excerpt: every literal
		/// "&lt;/a&gt;&lt;a" collision plus structural-fact hints (matching
		/// hrefs/titles, placeholder hrefs, anchor-text scope when same-href).
		/// Pure and testable; returns spans ordered by Start, non-overlapping.
		/// Empty/null excerpt → empty list. Excerpts with no collision → empty list.
		/// </summary>
		internal static IReadOnlyList<AdjacentHintSpan> ComputeAdjacentHintSpans(string excerpt)
		{
			var spans = new List<AdjacentHintSpan>();
			if (string.IsNullOrEmpty(excerpt))
			{
				return spans;
			}

			const string needle = "</a><a";
			var searchFrom = 0;
			while (true)
			{
				var hit = excerpt.IndexOf(needle, searchFrom, StringComparison.OrdinalIgnoreCase);
				if (hit < 0)
				{
					break;
				}

				// Collision span (red): the literal "</a><a", 6 chars.
				spans.Add(new AdjacentHintSpan(hit, needle.Length, AdjacentHintSpanKind.Collision));

				// Parse the LEFT anchor (ends at hit + len("</a>") = hit + 4).
				var leftCloseAt = hit;                   // index of "</a>"
				var leftAnchor = ParseAnchorEndingAt(excerpt, leftCloseAt);

				// Parse the RIGHT anchor (starts at hit + len("</a>") = hit + 4).
				var rightOpenAt = hit + 4;               // index of "<a"
				var rightAnchor = ParseAnchorStartingAt(excerpt, rightOpenAt);

				EmitPairSpans(spans, leftAnchor, rightAnchor);

				// Advance: continue scanning after this collision. The "<a" we just
				// consumed may also be the left half of a NEXT collision (cluster
				// case: </a><a></a><a> — three anchors, two collisions). Restart
				// search just after the collision so the second is found too.
				searchFrom = hit + needle.Length;
			}

			// Sort by Start; dedupe overlapping/duplicate spans (cluster cases can
			// produce the same anchor's href/title under two pair analyses).
			spans.Sort((a, b) => a.Start.CompareTo(b.Start));
			return DedupeOrdered(spans);
		}

		// ── Anchor parsing helpers (excerpt-local, defensive about truncation) ──

		private readonly record struct ParsedAnchor(
			int InnerTextStart, int InnerTextEnd,    // bounds of the anchor's link text in the excerpt; -1 if not in window
			int HrefValueStart, int HrefValueLen,    // bounds of the href value (between quotes); -1 if absent
			string? HrefValue,                       // the href value itself (null if absent)
			bool HrefIsPlaceholder,                  // true for href="#", href="", javascript: scheme
			int TitleValueStart, int TitleValueLen,  // bounds of the title value; -1 if absent
			string? TitleValue);                     // the title value itself (null if absent)

		/// <summary>Parses the LEFT anchor of an adjacency — the one ending at "&lt;/a&gt;".</summary>
		private static ParsedAnchor ParseAnchorEndingAt(string excerpt, int closeAt)
		{
			// Walk back to find the opening "<a"; if not in window, fall back to
			// last ">" before closeAt to bound the inner text.
			var openAt = LastIndexOfAnchorOpen(excerpt, closeAt);
			var openTagEnd = -1;
			if (openAt >= 0)
			{
				openTagEnd = excerpt.IndexOf('>', openAt);
				if (openTagEnd >= closeAt)
				{
					openTagEnd = -1;
				}
			}
			int innerStart;
			if (openTagEnd >= 0)
			{
				innerStart = openTagEnd + 1;
			}
			else
			{
				// Fallback: last ">" before the </a>. A raw ">" never appears in
				// link text (it would be &gt;), so this reliably lands on the open
				// tag's ">" even when "<a" is truncated away.
				var gt = excerpt.LastIndexOf('>', closeAt - 1);
				innerStart = gt >= 0 ? gt + 1 : -1;
			}

			var (hrefStart, hrefLen, hrefVal, hrefPlaceholder) =
				openAt >= 0 ? ExtractAttribute(excerpt, openAt, closeAt, "href") : (-1, 0, (string?)null, false);
			var (titleStart, titleLen, titleVal, _) =
				openAt >= 0 ? ExtractAttribute(excerpt, openAt, closeAt, "title") : (-1, 0, (string?)null, false);

			return new ParsedAnchor(
				InnerTextStart: innerStart,
				InnerTextEnd: innerStart >= 0 ? closeAt : -1,
				HrefValueStart: hrefStart,
				HrefValueLen: hrefLen,
				HrefValue: hrefVal,
				HrefIsPlaceholder: hrefPlaceholder,
				TitleValueStart: titleStart,
				TitleValueLen: titleLen,
				TitleValue: titleVal);
		}

		/// <summary>Parses the RIGHT anchor of an adjacency — the one starting at "&lt;a".</summary>
		private static ParsedAnchor ParseAnchorStartingAt(string excerpt, int openAt)
		{
			// Find this open tag's ">". If off-window, attribute parsing scans the
			// visible portion only; inner-text span is omitted.
			var openTagEnd = excerpt.IndexOf('>', openAt);
			// Find this anchor's "</a>". If off-window, no inner-text span.
			var closeAt = openTagEnd >= 0
				? excerpt.IndexOf("</a>", openTagEnd, StringComparison.OrdinalIgnoreCase)
				: -1;

			var innerStart = openTagEnd >= 0 ? openTagEnd + 1 : -1;
			var innerEnd = closeAt >= 0 ? closeAt : -1;

			// Attribute parsing scans from <a to the END of its open tag, or to a
			// reasonable cap if the > is off-window (avoid running into the next
			// anchor's attributes). Cap = the next "<" character.
			var attrScanEnd = openTagEnd >= 0
				? openTagEnd
				: NextLessThan(excerpt, openAt + 2);
			if (attrScanEnd < 0)
			{
				attrScanEnd = excerpt.Length;
			}

			var (hrefStart, hrefLen, hrefVal, hrefPlaceholder) =
				ExtractAttribute(excerpt, openAt, attrScanEnd, "href");
			var (titleStart, titleLen, titleVal, _) =
				ExtractAttribute(excerpt, openAt, attrScanEnd, "title");

			return new ParsedAnchor(
				InnerTextStart: innerStart,
				InnerTextEnd: innerEnd,
				HrefValueStart: hrefStart,
				HrefValueLen: hrefLen,
				HrefValue: hrefVal,
				HrefIsPlaceholder: hrefPlaceholder,
				TitleValueStart: titleStart,
				TitleValueLen: titleLen,
				TitleValue: titleVal);
		}

		private static int NextLessThan(string s, int from)
		{
			for (var i = from; i < s.Length; i++)
			{
				if (s[i] == '<')
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>
		/// Extracts the value of <paramref name="attrName"/> from the substring
		/// [openAt, scanEnd). Returns (valueStart, valueLen, value, isPlaceholder)
		/// or (-1, 0, null, false) if absent. valueStart points at the FIRST char
		/// of the value (after the opening quote, if any). Handles double-quoted,
		/// single-quoted, and unquoted values defensively. Placeholder classification:
		/// "#", "", any value starting with "javascript:" (case-insensitive).
		/// </summary>
		private static (int Start, int Length, string? Value, bool IsPlaceholder)
			ExtractAttribute(string excerpt, int openAt, int scanEnd, string attrName)
		{
			if (scanEnd <= openAt || scanEnd > excerpt.Length)
			{
				return (-1, 0, null, false);
			}
			// Case-insensitive search for "attrName" within the tag scan range,
			// requiring a word-boundary before (whitespace or "<a" itself).
			var needle = attrName;
			var scanFrom = openAt;
			while (scanFrom < scanEnd)
			{
				var nameIdx = excerpt.IndexOf(needle, scanFrom, scanEnd - scanFrom, StringComparison.OrdinalIgnoreCase);
				if (nameIdx < 0)
				{
					return (-1, 0, null, false);
				}
				// Word-boundary check: char before must be whitespace (or this is
				// immediately after "<a"). Avoids matching "data-href" when looking
				// for "href".
				var before = nameIdx > 0 ? excerpt[nameIdx - 1] : '\0';
				if (!char.IsWhiteSpace(before))
				{
					scanFrom = nameIdx + needle.Length;
					continue;
				}
				// Must be followed by "=" (with optional whitespace between).
				var after = nameIdx + needle.Length;
				while (after < scanEnd && char.IsWhiteSpace(excerpt[after]))
				{
					after++;
				}

				if (after >= scanEnd || excerpt[after] != '=')
				{
					scanFrom = nameIdx + needle.Length;
					continue;
				}
				after++; // past '='
				while (after < scanEnd && char.IsWhiteSpace(excerpt[after]))
				{
					after++;
				}

				if (after >= scanEnd)
				{
					return (-1, 0, null, false);
				}
				// Value: quoted ("…", '…') or bare (up to whitespace or end).
				var quote = excerpt[after];
				int valStart, valEnd;
				if (quote == '"' || quote == '\'')
				{
					valStart = after + 1;
					valEnd = excerpt.IndexOf(quote, valStart, scanEnd - valStart);
					if (valEnd < 0)
					{
						valEnd = scanEnd;
					}
				}
				else
				{
					valStart = after;
					valEnd = valStart;
					while (valEnd < scanEnd && !char.IsWhiteSpace(excerpt[valEnd]) && excerpt[valEnd] != '>')
					{
						valEnd++;
					}
				}
				var value = excerpt[valStart..valEnd];
				var isPlaceholder = value.Length == 0
					|| value == "#"
					|| value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
				return (valStart, valEnd - valStart, value, isPlaceholder);
			}
			return (-1, 0, null, false);
		}

		/// <summary>
		/// Emits hint spans for a parsed adjacent pair into <paramref name="spans"/>.
		/// Compares hrefs and titles across the pair and adds the appropriate
		/// HrefMatch/HrefPlaceholder/HrefBaseline + TitleMatch + AnchorTextHint
		/// spans. No-op for missing attributes or out-of-window spans.
		/// </summary>
		private static void EmitPairSpans(
			List<AdjacentHintSpan> spans, ParsedAnchor left, ParsedAnchor right)
		{
			var leftHref = left.HrefValue;
			var rightHref = right.HrefValue;
			var bothHaveHref = leftHref is not null && rightHref is not null;
			var hrefsMatch = bothHaveHref
				&& string.Equals(leftHref, rightHref, StringComparison.Ordinal);

			// HrefMatch: both real and identical → HrefMatch on both, plus AnchorTextHint.
			// HrefPlaceholder: both placeholders (most commonly both #) → on both.
			// HrefBaseline: any present href that doesn't fall in the above (mild).
			//
			// Note: a placeholder on ONE side and a real href on the other is rare; we
			// classify each side independently (placeholder gets HrefPlaceholder, real
			// gets HrefBaseline). The "match" colour requires both real + identical.
			if (hrefsMatch && !left.HrefIsPlaceholder && !right.HrefIsPlaceholder)
			{
				AddSpan(spans, left.HrefValueStart, left.HrefValueLen, AdjacentHintSpanKind.HrefMatch);
				AddSpan(spans, right.HrefValueStart, right.HrefValueLen, AdjacentHintSpanKind.HrefMatch);
				// Same-real-href: also mark the anchors' inner text to scope the pair.
				if (left.InnerTextStart >= 0 && left.InnerTextEnd > left.InnerTextStart)
				{
					AddSpan(spans, left.InnerTextStart, left.InnerTextEnd - left.InnerTextStart, AdjacentHintSpanKind.AnchorTextHint);
				}
				if (right.InnerTextStart >= 0 && right.InnerTextEnd > right.InnerTextStart)
				{
					AddSpan(spans, right.InnerTextStart, right.InnerTextEnd - right.InnerTextStart, AdjacentHintSpanKind.AnchorTextHint);
				}
			}
			else
			{
				if (leftHref is not null)
				{
					var kind = left.HrefIsPlaceholder
						? AdjacentHintSpanKind.HrefPlaceholder
						: AdjacentHintSpanKind.HrefBaseline;
					AddSpan(spans, left.HrefValueStart, left.HrefValueLen, kind);
				}
				if (rightHref is not null)
				{
					var kind = right.HrefIsPlaceholder
						? AdjacentHintSpanKind.HrefPlaceholder
						: AdjacentHintSpanKind.HrefBaseline;
					AddSpan(spans, right.HrefValueStart, right.HrefValueLen, kind);
				}
			}

			// TitleMatch: both titles present and equal → on both.
			if (left.TitleValue is not null && right.TitleValue is not null
				&& string.Equals(left.TitleValue, right.TitleValue, StringComparison.Ordinal))
			{
				AddSpan(spans, left.TitleValueStart, left.TitleValueLen, AdjacentHintSpanKind.TitleMatch);
				AddSpan(spans, right.TitleValueStart, right.TitleValueLen, AdjacentHintSpanKind.TitleMatch);
			}
		}

		private static void AddSpan(
			List<AdjacentHintSpan> spans, int start, int length, AdjacentHintSpanKind kind)
		{
			if (start < 0 || length <= 0)
			{
				return;
			}

			spans.Add(new AdjacentHintSpan(start, length, kind));
		}

		/// <summary>
		/// Returns a deduplicated, non-overlapping copy of the given spans list
		/// (which MUST already be sorted by Start). Duplicates can arise in cluster
		/// cases where the middle anchor of "&lt;a&gt;A&lt;/a&gt;&lt;a&gt;&lt;/a&gt;&lt;a&gt;B&lt;/a&gt;" is
		/// analysed under two pair contexts. First occurrence at each Start wins;
		/// overlapping spans drop the later one (renderer's invariant: spans must
		/// not overlap).
		/// </summary>
		private static IReadOnlyList<AdjacentHintSpan> DedupeOrdered(
			IReadOnlyList<AdjacentHintSpan> sorted)
		{
			var result = new List<AdjacentHintSpan>(sorted.Count);
			var lastEnd = -1;
			foreach (var s in sorted)
			{
				if (s.Start < lastEnd)
				{
					continue;  // overlap with previous span — drop
				}

				result.Add(s);
				lastEnd = s.Start + s.Length;
			}
			return result;
		}

		/// <summary>
		/// Span kind for split-word-anchor rendering. Tag = &lt;a…&gt;/&lt;/a&gt;,
		/// Inside = the anchor's link text, Tail = the orphaned run after
		/// &lt;/a&gt;, BrSpacer = a &lt;br&gt; inside a word collision.
		/// </summary>
		internal enum SplitSpanKind { Tag, Inside, Tail, BrSpacer }

		/// <summary>
		/// A coloured span within a split-word excerpt: a half-open range
		/// [Start, Start+Length) and its kind. Computed by the (pure, tested)
		/// <see cref="ComputeSplitWordSpans"/>, so the views only paint it and
		/// carry no parsing logic of their own.
		/// </summary>
		internal readonly record struct SplitSpan(int Start, int Length, SplitSpanKind Kind);

		internal enum EmptyAnchorSpanKind { Structure, Href, Attr, Marker, Context, Emphasis }

		internal readonly record struct EmptyAnchorSpan(int Start, int Length, EmptyAnchorSpanKind Kind);

		/// <summary>
		/// Span kind for ADJACENT_ANCHOR same-href hint rendering. The
		/// detector flags any literal &lt;/a&gt;&lt;a collision; the visualization
		/// layer surfaces additional structural facts via colour so the operator
		/// can interpret design-vs-defect without the tool claiming a verdict.
		/// Spans:
		///   Collision      — the literal "&lt;/a&gt;&lt;a" 6-char defect shape (red)
		///   HrefBaseline   — any anchor's href value (mild visibility)
		///   HrefMatch      — anchor hrefs that match across the adjacent pair
		///   HrefPlaceholder— href="#" placeholder ("JS-trigger, be aware")
		///   TitleMatch     — title attributes matching across pair
		///   AnchorTextHint — anchor inner text when paired same-href (set apart)
		/// </summary>
		internal enum AdjacentHintSpanKind
		{
			Collision,
			HrefBaseline,
			HrefMatch,
			HrefPlaceholder,
			TitleMatch,
			AnchorTextHint,
		}

		/// <summary>
		/// A coloured span within an ADJACENT_ANCHOR excerpt. Computed by the
		/// (pure, tested) <see cref="ComputeAdjacentHintSpans"/>, so the views
		/// only paint it and carry no parsing of their own.
		/// </summary>
		internal readonly record struct AdjacentHintSpan(int Start, int Length, AdjacentHintSpanKind Kind);

		/// <summary>
		/// One typographic quote glyph in a quote-issue excerpt. IsTrigger marks the
		/// glyph the finding is actually about (painted as the offender); all other
		/// quote glyphs are context. Computed by the pure
		/// <see cref="ComputeQuoteSpans"/>; the views only paint it.
		/// </summary>
		internal readonly record struct QuoteHighlightSpan(int Start, int Length, bool IsTrigger);

		internal static readonly HashSet<char> HighlightQuoteChars =
		[
			'\u0022',                                // " straight (ASCII) double — the
			                                         //   QUOTE_MIXED_KIND offender glyph; without it
			                                         //   the straight quote stays uncoloured while the
			                                         //   typographic partner shows as context
			'\u201C', '\u201D', '\u201E', '\u201F', // double quotes
			'\u2018', '\u2019', '\u201A',            // single quotes
			'\u00B4', '\u0060',                      // acute and backtick: QUOTE_WRONG_APOSTROPHE offenders
			'\u00AB', '\u00BB',                      // guillemets
			'\u2039', '\u203A',                      // single guillemets
			'\u275D', '\u275E',                      // heavy quotes
		];

		internal static readonly HashSet<char> HighlightLigatureChars =
		[
			'\uFB00', '\uFB01', '\uFB02', '\uFB03', '\uFB04', '\uFB05', '\uFB06',
		];
	}

	/// <summary>Spans for line-break-glue contexts.</summary>
	internal static partial class GlueHighlight
	{
		/// <summary>Rendered markers for the glue characters this check reports.</summary>
		private static readonly string[] GlueMarkers =
		[
			"[NO-BREAK SPACE U+00A0]",
			"[NARROW NO-BREAK SPACE U+202F]",
			"[FIGURE SPACE U+2007]",
			"[NON-BREAKING HYPHEN U+2011]",
		];

		/// <summary>
		/// Any rendered marker — the glue ones above plus tabs, line feeds, soft
		/// hyphens, zero-widths and the rest of the LogExcerpt vocabulary.
		/// </summary>
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex AnyMarkerRegex = new(@"\[[A-Z][A-Z0-9 +\-]*\]", RegexOptions.Compiled);

		private static Regex AnyMarker() => AnyMarkerRegex;

		/// <summary>
		/// Spans for one rendered context, ascending and non-overlapping as the
		/// writer requires. Empty when nothing is located — the writer then
		/// degrades to plain text rather than colouring the wrong thing.
		/// </summary>
		internal static List<TriageHighlights.EmptyAnchorSpan> ComputeSpans(string rendered)
		{
			var spans = new List<TriageHighlights.EmptyAnchorSpan>();
			if (string.IsNullOrEmpty(rendered))
			{
				return spans;
			}

			var markers = AnyMarker().Each(rendered)
				.Select(m => (Start: m.Index, m.Length, Text: m.Value))
				.ToList();

			foreach (var marker in markers)
			{
				var isGlue = Array.IndexOf(GlueMarkers, marker.Text) >= 0;
				if (isGlue && IsOffender(rendered, marker.Start, marker.Length))
				{
					spans.Add(new TriageHighlights.EmptyAnchorSpan(
						marker.Start, marker.Length, TriageHighlights.EmptyAnchorSpanKind.Structure));

					// The cause, immediately before. Skipped when it is itself a
					// marker (a soft hyphen, say) — that marker is already in the
					// list and picks up its own amber span below.
					var before = marker.Start - 1;
					if (before >= 0 && rendered[before] != ']')
					{
						spans.Add(new TriageHighlights.EmptyAnchorSpan(
							before, 1, TriageHighlights.EmptyAnchorSpanKind.Emphasis));
					}

					continue;
				}

				spans.Add(new TriageHighlights.EmptyAnchorSpan(
					marker.Start, marker.Length, TriageHighlights.EmptyAnchorSpanKind.Href));
			}

			// The writer walks spans in order and skips any starting before the
			// current position, so ordering is a correctness requirement: a cause
			// span sits BEFORE the offender it explains, and marker kinds interleave.
			spans.Sort((a, b) => a.Start.CompareTo(b.Start));
			return spans;
		}

		/// <summary>
		/// The detector's rule, re-applied to rendered text. A preceding character
		/// that permits a break after itself makes the glue inert (LB12a); a
		/// following ordinary space does the same from the other side (LB18).
		///
		/// The characters that make glue inert — dash, hyphen, space — are all
		/// visible, so they survive rendering as themselves. The one exception is a
		/// soft hyphen, which becomes its own marker; that is matched in marked
		/// space instead.
		/// </summary>
		private static bool IsOffender(string rendered, int start, int length)
		{
			const string SoftHyphen = "[INVISIBLE SOFT HYPHEN U+00AD]";

			if (start > 0)
			{
				if (LineBreakData.AllowsBreakBeforeGlue(rendered[start - 1]))
				{
					return true;
				}

				if (rendered[start - 1] == ']'
					&& start >= SoftHyphen.Length
					&& string.CompareOrdinal(rendered, start - SoftHyphen.Length, SoftHyphen, 0, SoftHyphen.Length) == 0)
				{
					return true;
				}
			}

			var after = start + length;
			return after < rendered.Length && rendered[after] == ' ';
		}
	}
}
