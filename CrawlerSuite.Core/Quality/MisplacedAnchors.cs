using System.Net;
using HtmlAgilityPack;

namespace CrawlerSuite.Core.Quality
{
	internal static class MisplacedAnchors
	{
		/// <summary>
		/// Detects structurally malformed anchor tags in raw downloaded HTML.
		/// Two defect types are reported independently:
		///
		///   MISPLACED_ANCHOR_EMPTY — a link that is rendered and exposed to assistive
		///     technology but has no accessible name (WCAG 2.4.4, 4.1.2; W3C ACT rule
		///     c487ae). An &lt;a&gt; without href is not a link and is not checked unless
		///     role="link"/"button" or a tabindex of 0+ makes it interactive. Not rendered
		///     (hidden attribute, inline display:none/visibility:hidden, &lt;template&gt;)
		///     and aria-hidden links are left out here; an aria-hidden link that is still
		///     reachable with Tab is reported as ARIA_HIDDEN_FOCUSABLE instead. Image links
		///     without usable alt are reported (WCAG failure F89). Rules: LinkAccessibility.
		///     Checked against the page's stylesheets like the hidden-focusable check: a link a
		///     rule always hides is _CLEARED, one hidden only under a query or in a state is
		///     _CONDITIONAL, one on a page with a stylesheet missing from the snapshot _UNDETERMINED.
		///
		///   ARIA_HIDDEN_FOCUSABLE — any element inside aria-hidden="true" that is still in
		///     the Tab order (WCAG 4.1.2; W3C ACT rule 6cfa84), empty or not.
		///
		///   ARIA_REFERENCE_BROKEN — aria-labelledby / aria-describedby naming an id that is
		///     not on the page. A markup issue, not a WCAG violation (the browser ignores the
		///     reference). Ids are looked up in the pruned page, then in the raw download, so a
		///     target inside pruned boilerplate is not reported as missing.
		///
		///   ADJACENT_ANCHOR —
		///     two consecutive sibling anchor tags with no whitespace-text node
		///     between them, AND the rendered excerpt contains the literal
		///     "&lt;/a&gt;&lt;a" (a string-evidence post-filter on HAP's DOM
		///     verdict). Adjacency alone is a structural fact, not a verdict;
		///     OFF by default (AnchorDetection.DetectAdjacent), opted in per
		///     site. The name says what is checked and stays apart from
		///     SPLIT_WORD_ANCHOR.
		///
		/// Runs on the already-parsed simplified HTML document — navigation chrome,
		/// scripts, and framework noise are stripped before this check runs, eliminating
		/// false positives from icon anchors, hidden UI controls, and CMS scaffolding.
		/// The caller passes the already-parsed <paramref name="doc"/> to avoid a second
		/// parse of the same file.
		/// </summary>
		internal static IEnumerable<QualityIssue> Check(
			string filename, HtmlDocument doc, ContentQualityConfig config, Func<HtmlDocument?>? rawDocument = null,
			Css.StyleSheets? styleSheets = null)
		{
			var resolve = IdResolver(doc, rawDocument);
			var css = styleSheets is null ? null : new PageCss(filename, rawDocument, styleSheets);

			foreach (var issue in AriaChecks(filename, doc, config, resolve, css))
			{
				yield return issue;
			}

			foreach (var anchor in doc.DocumentNode.Descendants("a").ToList())
			{
				// ── MISPLACED_ANCHOR_EMPTY ────────────────────────────────────────────
				if (LinkAccessibility.IsLinkLike(anchor)
					&& LinkAccessibility.IsRendered(anchor)
					&& !LinkAccessibility.IsAriaHidden(anchor)
					&& LinkAccessibility.MissingName(anchor, resolve) is { } reason)
				{
					yield return EmptyLinkVerdict(filename, anchor, reason, config, css);
				}

				// ── ADJACENT_ANCHOR ───────────────────────────────────────────────────
				// Flag when the immediately following sibling is also an anchor with no
				// whitespace-text node between them. Check current anchor against its next
				// sibling only — avoids double-reporting the same adjacent pair.
				//
				// Gated on AnchorDetection.DetectAdjacent (default false) because adjacency
				// alone is a structural fact, not a defect verdict — many sites use
				// adjacent <a><a> intentionally (CSS-spaced button rows, JS-trigger
				// widgets, dense nav). Operator opts in per site.
				if (!config.AnchorDetection.DetectAdjacent)
				{
					continue;
				}

				var next = anchor.NextSibling;

				// Skip comment nodes — invisible and not a real separator.
				while (next is { NodeType: HtmlNodeType.Comment })
				{
					next = next.NextSibling;
				}

				if (next is not { NodeType: HtmlNodeType.Element } || next.Name != "a")
				{
					continue;
				}

				// Walk siblings between anchor and next to find any whitespace text node.
				var between = anchor.NextSibling;
				var hasSeparator = false;
				while (between != null && between != next)
				{
					if (between.NodeType == HtmlNodeType.Text
						&& between.InnerText.Any(char.IsWhiteSpace))
					{
						hasSeparator = true;
						break;
					}
					between = between.NextSibling;
				}

				if (!hasSeparator)
				{
					var textA = WebUtility.HtmlDecode(anchor.InnerText).Trim();
					var textB = WebUtility.HtmlDecode(next.InnerText).Trim();

					// Centre the excerpt on the </a><a split BOUNDARY, not on the first
					// anchor's OuterHtml start (which sits before a potentially long SVG
					// body, pushing the actual split out of the window). The
					// boundary is where anchor.OuterHtml ends within the source. Fall back
					// to centring on the source midpoint if the anchor markup can't be
					// located (defensive — OuterHtml should always be present in source).
					var source = anchor.ParentNode?.OuterHtml ?? $"{anchor.OuterHtml}{next.OuterHtml}";
					var anchorAt = source.IndexOf(anchor.OuterHtml, StringComparison.Ordinal);
					var boundaryAt = anchorAt >= 0 ? anchorAt + anchor.OuterHtml.Length : source.Length / 2;

					var excerpt = Excerpt.Centred(source, boundaryAt, config.ContentQualityMaxExcerpt);

					// [KEEP] String-evidence post-filter: require the literal
					// "</a><a" to appear in the rendered excerpt before firing.
					// HAP's DOM-level adjacency verdict is what flagged this pair, but
					// HAP may normalize source whitespace during parsing/serialization,
					// so a DOM "adjacent" pair occasionally lacks literal source-level
					// adjacency. Operator decision: drop those edge cases rather
					// than ship a finding the operator cannot confirm by looking at the
					// shown excerpt. Case-insensitive to match the existing anchor-tag
					// highlighter's stance. Do NOT remove this gate — it is the honest
					// bridge between HAP's verdict and the source-bytes claim.
					if (excerpt.IndexOf("</a><a", StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					yield return new QualityIssue(
						filename,
						"ADJACENT_ANCHOR",
						// Prepend [boundaryAt] — the source-byte position
						// of this collision's "</a><a" boundary in the parent's
						// OuterHtml. Persists structural position in the log
						// (4-column shape unchanged) so BuildGroups can sort
						// multiple findings within one page-cluster reliably,
						// independent of ConcurrentBag emission order (which is
						// undefined and observably LIFO under current .NET).
						// Display layer renders this as sequential [01]/[02]
						// for clusters of 2+; bracket stays raw in the log so
						// the position info is intact across crawls. Stripping
						// is a trivial regex if the raw value is unwanted in
						// downstream tools (Excel: replace `\[\d+\] ` with "").
						$"[{boundaryAt}] \u201e{textA}\u201c + \u201e{textB}\u201c",
						excerpt);
				}
			}
		}

		/// <summary>A link without a name that the page's CSS always hides: nobody meets it; a record with its evidence.</summary>
		internal const string EmptyLinkCleared = "MISPLACED_ANCHOR_EMPTY_CLEARED";

		/// <summary>A link without a name hidden by CSS only under a media/container query or in a state: shown otherwise.</summary>
		internal const string EmptyLinkConditional = "MISPLACED_ANCHOR_EMPTY_CONDITIONAL";

		/// <summary>A link without a name on a page whose stylesheet is not in the snapshot: whether it is shown cannot be told.</summary>
		internal const string EmptyLinkUndetermined = "MISPLACED_ANCHOR_EMPTY_UNDETERMINED";

		/// <summary>
		/// The verdict on a link without a name, against the page's stylesheets as for the
		/// hidden-focusable check: hidden by rules that always apply is cleared (nobody sees it or
		/// reaches it with Tab), a stylesheet missing from the snapshot leaves it undetermined, hidden
		/// only under a query or in a state is conditional, shown is the finding. The reason stays at
		/// the end of the detail, so the element it names is still found there.
		/// </summary>
		private static QualityIssue EmptyLinkVerdict(string filename, HtmlNode anchor, string reason, ContentQualityConfig config, PageCss? css)
		{
			var excerpt = ExcerptAround(anchor, config);
			var (verdict, evidence, gaps) = css?.Resolve(anchor) ?? (Css.CssVerdict.Shown, string.Empty, []);
			if (verdict == Css.CssVerdict.Hidden)
			{
				return new QualityIssue(filename, EmptyLinkCleared, $"Hidden by CSS ({evidence}): {reason}", excerpt);
			}

			if (gaps.Count > 0)
			{
				var list = string.Join("; ", gaps.Select(g => $"{ShortUrl(g.Url)} ({g.Reason})"));
				return new QualityIssue(filename, EmptyLinkUndetermined,
					$"Whether it is shown cannot be told (stylesheet not in the snapshot: {list}): {reason}", excerpt);
			}

			if (verdict == Css.CssVerdict.Conditional)
			{
				return new QualityIssue(filename, EmptyLinkConditional, $"Shown except {evidence}: {reason}", excerpt);
			}

			return new QualityIssue(filename, "MISPLACED_ANCHOR_EMPTY", reason, excerpt);
		}

		/// <summary>An aria-hidden element that would be focusable but is taken out of the Tab order: not a violation, with its evidence.</summary>
		internal const string HiddenFocusableCleared = "ARIA_HIDDEN_FOCUSABLE_CLEARED";

		/// <summary>
		/// The verdict on an element inside aria-hidden that is focusable by what it is: cleared when
		/// its markup or the page's CSS takes it out of the Tab order (with the evidence),
		/// undetermined when a stylesheet is missing from the snapshot, conditional when only a media
		/// query or a state hides it, a violation otherwise.
		/// </summary>
		private static QualityIssue HiddenFocusableVerdict(string filename, HtmlNode element, ContentQualityConfig config, PageCss? css)
		{
			var described = LinkAccessibility.Describe(element);
			if (LinkAccessibility.NotReachable(element) is { } reason)
			{
				return new QualityIssue(filename, HiddenFocusableCleared,
					$"{described} is hidden from screen readers (aria-hidden) and not reachable with Tab: {reason}",
					ExcerptAround(element, config));
			}

			// The page's CSS: hidden by rules that always apply clears it; a stylesheet the
			// crawl did not get leaves it open; hidden only under a query or in a state is conditional.
			var (verdict, evidence, gaps) = css?.Resolve(element) ?? (Css.CssVerdict.Shown, string.Empty, []);
			if (verdict == Css.CssVerdict.Hidden)
			{
				return new QualityIssue(filename, HiddenFocusableCleared,
					$"{described} is hidden from screen readers (aria-hidden) and not reachable with Tab: hidden by CSS: {evidence}",
					ExcerptAround(element, config));
			}

			if (gaps.Count > 0)
			{
				var list = string.Join("; ", gaps.Select(g => $"{ShortUrl(g.Url)} ({g.Reason})"));
				return new QualityIssue(filename, HiddenFocusableUndetermined,
					$"{described} is hidden from screen readers (aria-hidden); whether it is reachable with Tab cannot be told: stylesheet not in the snapshot: {list}",
					ExcerptAround(element, config));
			}

			if (verdict == Css.CssVerdict.Conditional)
			{
				return new QualityIssue(filename, HiddenFocusableConditional,
					$"{described} is hidden from screen readers (aria-hidden) and reachable with Tab except {evidence}",
					ExcerptAround(element, config));
			}

			return new QualityIssue(filename, "ARIA_HIDDEN_FOCUSABLE",
				$"{described} is hidden from screen readers (aria-hidden) but reachable with Tab",
				ExcerptAround(element, config));
		}

		/// <summary>Hidden by CSS only under a media/container query or in a state: reachable otherwise.</summary>
		internal const string HiddenFocusableConditional = "ARIA_HIDDEN_FOCUSABLE_CONDITIONAL";

		/// <summary>A stylesheet the page references is not in the snapshot (and not a 404): the verdict cannot be given.</summary>
		internal const string HiddenFocusableUndetermined = "ARIA_HIDDEN_FOCUSABLE_UNDETERMINED";

		private static string ShortUrl(string url) =>
			Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath.Split('/').LastOrDefault(s => s.Length > 0) ?? url : url;

		/// <summary>
		/// One page's CSS for the hidden-focusable and empty-link checks, built on first need: the raw page (as the
		/// browser loads it — pruning removes siblings that +, ~ and :nth-child count), its
		/// stylesheets, and each candidate of the pruned page found again in the raw one.
		/// </summary>
		private sealed class PageCss(string filename, Func<HtmlDocument?>? rawDocument, Css.StyleSheets sheets)
		{
			private bool built;
			private Css.PageStyles? styles;
			private Dictionary<string, Queue<HtmlNode>>? rawCandidates;

			public (Css.CssVerdict Verdict, string Evidence, IReadOnlyList<Css.MissingSheet> Gaps) Resolve(HtmlNode element)
			{
				Build();
				if (styles is null)
				{
					return (Css.CssVerdict.Shown, string.Empty, []);
				}

				// The same element in the raw page; the pruned one when it cannot be found.
				var target = rawCandidates!.TryGetValue(Key(element), out var queue) && queue.Count > 0 ? queue.Dequeue() : element;
				var (verdict, evidence) = styles.Resolve(target);
				return (verdict, evidence, verdict == Css.CssVerdict.Hidden ? [] : styles.Gaps);
			}

			private void Build()
			{
				if (built)
				{
					return;
				}

				built = true;
				var raw = rawDocument?.Invoke();
				var url = CrawlIndex.LookUpUrlForFile(filename);
				if (raw is null || url == "error")
				{
					return;
				}

				styles = new Css.PageStyles(raw, url, sheets);
				rawCandidates = new Dictionary<string, Queue<HtmlNode>>(StringComparer.Ordinal);
				foreach (var e in raw.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
				{
					if ((LinkAccessibility.IsAriaHidden(e) && LinkAccessibility.IsFocusableByNature(e))
						|| (e.Name == "a" && LinkAccessibility.IsLinkLike(e)))
					{
						var key = Key(e);
						if (!rawCandidates.TryGetValue(key, out var q))
						{
							rawCandidates[key] = q = new Queue<HtmlNode>();
						}

						q.Enqueue(e);
					}
				}
			}

			// The element as written, with the names, ids and classes of its ancestors: pruning removes
			// whole subtrees, so a kept element keeps this chain.
			private static string Key(HtmlNode e)
			{
				var sb = new System.Text.StringBuilder();
				for (var n = e.ParentNode; n is not null && n.NodeType == HtmlNodeType.Element; n = n.ParentNode)
				{
					sb.Append(n.Name).Append('#').Append(n.GetAttributeValue("id", string.Empty))
						.Append('.').Append(n.GetAttributeValue("class", string.Empty)).Append('/');
				}

				return sb.Append(e.OuterHtml).ToString();
			}
		}

		/// <summary>ARIA_HIDDEN_FOCUSABLE and ARIA_REFERENCE_BROKEN over every element of the page.</summary>
		private static IEnumerable<QualityIssue> AriaChecks(
			string filename, HtmlDocument doc, ContentQualityConfig config, LinkAccessibility.IdLookup resolve, PageCss? css = null)
		{
			foreach (var element in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element).ToList())
			{
				// Inside aria-hidden and focusable by what it is: a violation, unless something in the
				// markup takes it out of the Tab order — then it is cleared, and the reason is its evidence.
				if (LinkAccessibility.IsAriaHidden(element) && LinkAccessibility.IsFocusableByNature(element))
				{
					yield return HiddenFocusableVerdict(filename, element, config, css);
				}

				if (InTemplate(element))
				{
					continue;
				}

				foreach (var attribute in LinkAccessibility.ReferenceAttributes)
				{
					var missing = LinkAccessibility.Tokens(element.GetAttributeValue(attribute, string.Empty))
						.Where(id => resolve(id) is null)
						.ToList();
					if (missing.Count > 0)
					{
						var ids = string.Join(", ", missing.Select(id => $"\"{id}\""));
						yield return new QualityIssue(filename, "ARIA_REFERENCE_BROKEN",
							$"{attribute} points to missing id{(missing.Count > 1 ? "s" : string.Empty)} {ids} on {LinkAccessibility.Describe(element)}",
							ExcerptAround(element, config));
					}
				}
			}
		}

		/// <summary>Ids in the pruned page first; the raw download only when an id is not found there.</summary>
		private static LinkAccessibility.IdLookup IdResolver(HtmlDocument doc, Func<HtmlDocument?>? rawDocument)
		{
			Dictionary<string, HtmlNode>? pruned = null;
			Dictionary<string, HtmlNode>? raw = null;
			bool rawLoaded = false;

			static Dictionary<string, HtmlNode> Index(HtmlDocument d)
			{
				var map = new Dictionary<string, HtmlNode>(StringComparer.Ordinal);
				foreach (var n in d.DocumentNode.Descendants())
				{
					if (n.NodeType == HtmlNodeType.Element && n.Attributes["id"] is { } id && id.Value.Length > 0)
					{
						map.TryAdd(id.Value, n);
					}
				}

				return map;
			}

			return id =>
			{
				pruned ??= Index(doc);
				if (pruned.TryGetValue(id, out var node))
				{
					return node;
				}

				if (!rawLoaded)
				{
					rawLoaded = true;
					raw = rawDocument?.Invoke() is { } r ? Index(r) : null;
				}

				return raw is not null && raw.TryGetValue(id, out var rawNode) ? rawNode : null;
			};
		}

		private static bool InTemplate(HtmlNode element)
		{
			for (var node = element.ParentNode; node is not null; node = node.ParentNode)
			{
				if (node.Name == "template")
				{
					return true;
				}
			}

			return false;
		}

		private static string ExcerptAround(HtmlNode element, ContentQualityConfig config) =>
			Excerpt.Centred(element.ParentNode?.OuterHtml ?? element.OuterHtml, element.OuterHtml, config.ContentQualityMaxExcerpt);
	}
}
