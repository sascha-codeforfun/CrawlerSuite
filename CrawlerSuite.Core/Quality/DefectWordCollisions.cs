using System.Net;
using HtmlAgilityPack;

namespace CrawlerSuite.Core.Quality
{
	internal static class DefectWordCollisions
	{
		/// <summary>
		/// Inline phrasing elements that add no implicit whitespace at their boundary —
		/// the same set the spell harvester glues across (see DomTraverser.InlinePhrasingGlue).
		/// Text on either side of such a boundary touches with no separator, exactly as the
		/// browser renders it.
		/// </summary>
		private static readonly HashSet<string> InlinePhrasingElements =
			new(StringComparer.OrdinalIgnoreCase)
			{
				"b", "i", "em", "strong", "mark", "small", "u", "s", "span", "wbr",
			};

		/// <summary>
		/// Flags word collisions where an inline phrasing element (e.g. a CMS editor's
		/// <c>&lt;span class="h2"&gt;</c> used to fake a heading size) abuts bare sibling
		/// text with no whitespace at the seam, so two words merge into one
		/// (e.g. <c>&lt;span&gt;Treib&lt;/span&gt;Eisrand</c> → "TreibEisrand").
		/// CSS (display:block) often hides the visual mash, so this is invisible to visual
		/// QA — but every reader that does not apply the stylesheet (search snippets, text
		/// extraction, reader modes, the spell harvester) sees the merged token: content must
		/// not rely on CSS to keep its words apart. The high-precision signal is a lowercase→Uppercase transition
		/// straddling the seam: natural words carry no internal capital, whereas a true
		/// mid-word emphasis (<c>&lt;b&gt;versen&lt;/b&gt;den</c> → "versenden") is
		/// lowercase→lowercase and is correctly left alone.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckWordCollisions(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			// Document-order rank of the inline-phrasing node, prepended to each
			// finding's Detail as a "[N]" prefix. The log freezes findings in
			// ConcurrentBag/LIFO order and WriteLog sorts only by (Filename,
			// IssueType), so equal-key findings land in arbitrary (observed-LIFO)
			// order; this rank lets BuildGroups recover page order so the operator
			// reads 1-2-3, not 3-2-1. Deterministic across runs because the
			// simplified file is frozen and Descendants() is document-ordered.
			// Mirrors the ADJACENT_ANCHOR "[boundaryAt]" prefix pattern.
			// Run-local ordinal only - NOT a raw-HTML coordinate.
			int rank = -1;
			foreach (var node in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element
					&& InlinePhrasingElements.Contains(n.Name)))
			{
				rank++;
				var inner = WebUtility.HtmlDecode(node.InnerText);
				if (string.IsNullOrEmpty(inner))
				{
					continue;
				}

				// Superscript and subscript attach to the word by design (™, footnote numbers,
				// H₂O): an element whose text is all in <sup>/<sub> is no collision.
				if (AllRaisedOrLowered(node))
				{
					continue;
				}

				// Trailing seam: <span>…Treib</span>Eisrand…
				// left char = inner's last char; right char = next text sibling's first char.
				// A <br> at the element's edge is a line break at the seam: no collision.
				if (node.NextSibling is { NodeType: HtmlNodeType.Text } nextText && !EndsWithBreak(node))
				{
					var right = WebUtility.HtmlDecode(nextText.InnerText);
					if (right.Length > 0
						&& IsLowerUpperSeam(inner[^1], right[0]))
					{
						// Context is the RAW html around the seam (e.g.
						// "<span class=\"h2\">Treib</span>Eisrand der Scholle…"),
						// so triage can show actual markup and highlight WORD1/</tag>/WORD2.
						yield return new QualityIssue(
							filename,
							"WORD_COLLISION",
							$"[{rank}] Inline <{node.Name}> abuts bare text without separator — words merge",
							node.OuterHtml + CapExcerpt(nextText.InnerText, config));
						continue;   // one finding per element; don't also test the leading seam
					}
				}

				// Leading seam: …Eisrand<span>Treib</span>
				// left char = previous text sibling's last char; right char = inner's first char.
				if (node.PreviousSibling is { NodeType: HtmlNodeType.Text } prevText && !StartsWithBreak(node))
				{
					var left = WebUtility.HtmlDecode(prevText.InnerText);
					if (left.Length > 0
						&& IsLowerUpperSeam(left[^1], inner[0]))
					{
						yield return new QualityIssue(
							filename,
							"WORD_COLLISION",
							$"[{rank}] Bare text abuts inline <{node.Name}> without separator — words merge",
							CapExcerptEnd(prevText.InnerText, config) + node.OuterHtml);
					}
				}
			}
		}

		/// <summary>Whether every non-blank text in the element lies inside a &lt;sup&gt; or &lt;sub&gt;.</summary>
		private static bool AllRaisedOrLowered(HtmlNode node)
		{
			bool any = false;
			foreach (var text in node.Descendants().Where(n => n.NodeType == HtmlNodeType.Text))
			{
				if (string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(text.InnerText)))
				{
					continue;
				}

				any = true;
				var inside = false;
				for (var p = text.ParentNode; p is not null && p != node.ParentNode; p = p.ParentNode)
				{
					if (p.Name is "sup" or "sub")
					{
						inside = true;
						break;
					}
				}

				if (!inside)
				{
					return false;
				}
			}

			return any;
		}

		/// <summary>Whether the element's last content (blank text and comments aside) is a &lt;br&gt;.</summary>
		private static bool EndsWithBreak(HtmlNode node) => EdgeIsBreak(node, last: true);

		/// <summary>Whether the element's first content (blank text and comments aside) is a &lt;br&gt;.</summary>
		private static bool StartsWithBreak(HtmlNode node) => EdgeIsBreak(node, last: false);

		private static bool EdgeIsBreak(HtmlNode node, bool last)
		{
			var n = node;
			while (true)
			{
				var children = n.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Element
					|| (c.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(c.InnerText)));
				var edge = last ? children.LastOrDefault() : children.FirstOrDefault();
				if (edge is null || edge.NodeType != HtmlNodeType.Element)
				{
					return false;
				}

				if (edge.Name == "br")
				{
					return true;
				}

				n = edge;
			}
		}

		/// <summary>
		/// True when the seam straddles a lowercase letter immediately followed by an
		/// uppercase letter — the high-precision word-collision signal. Whitespace on
		/// either side breaks the seam (the characters compared are the literal adjacent
		/// ones, so a leading/trailing space yields a non-letter and returns false).
		/// </summary>
		private static bool IsLowerUpperSeam(char left, char right)
			=> char.IsLower(left) && char.IsUpper(right);

		/// <summary>Caps to the leading <c>ContentQualityExcerptRadius</c> chars (head kept).</summary>
		private static string CapExcerpt(string text, ContentQualityConfig config)
			=> text.Length > config.ContentQualityExcerptRadius
				? text[..config.ContentQualityExcerptRadius] + "…"
				: text;

		/// <summary>Caps to the trailing <c>ContentQualityExcerptRadius</c> chars (tail kept) — for the leading-seam fragment.</summary>
		private static string CapExcerptEnd(string text, ContentQualityConfig config)
			=> text.Length > config.ContentQualityExcerptRadius
				? "…" + text[^config.ContentQualityExcerptRadius..]
				: text;
	}
}
