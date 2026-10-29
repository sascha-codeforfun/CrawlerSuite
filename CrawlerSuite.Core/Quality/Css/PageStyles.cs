namespace CrawlerSuite.Core.Quality.Css
{
	using System.Collections.Concurrent;
	using System.Text;
	using HtmlAgilityPack;

	/// <summary>What the page's CSS does to an element's visibility.</summary>
	internal enum CssVerdict
	{
		/// <summary>No rule hides it.</summary>
		Shown,

		/// <summary>Hidden by rules that always apply.</summary>
		Hidden,

		/// <summary>Hidden only under a media or container query, or in a state.</summary>
		Conditional,
	}

	/// <summary>A stylesheet a page references that is not in the snapshot.</summary>
	internal sealed record MissingSheet(string Url, string Reason, bool NotThere);

	/// <summary>
	/// The stylesheets of the whole run: each file parsed once, and the referenced files the
	/// snapshot does not have, with what the crawl log says about them (a 404/410 means the file is
	/// not there for any visitor either; anything else is a gap in the crawl). Thread-safe.
	/// </summary>
	internal sealed class StyleSheets
	{
		private readonly string downloadDirectory;
		private readonly Func<string, string?> crawlStatus;
		private readonly ConcurrentDictionary<string, CssSheet?> parsed = new(StringComparer.Ordinal);
		private readonly ConcurrentDictionary<string, MissingSheet> missing = new(StringComparer.Ordinal);

		/// <param name="downloadDirectory">The snapshot's download folder.</param>
		/// <param name="crawlStatus">The status the crawl log records for an address ("404", "N/A" …); null when it is not in the log.</param>
		public StyleSheets(string downloadDirectory, Func<string, string?> crawlStatus)
		{
			this.downloadDirectory = downloadDirectory;
			this.crawlStatus = crawlStatus;
		}

		/// <summary>The stylesheet files read from the snapshot, over the whole run.</summary>
		public int Read => parsed.Values.Count(sheet => sheet is not null);

		/// <summary>The referenced stylesheets not in the snapshot, over the whole run.</summary>
		public IReadOnlyCollection<MissingSheet> Missing => missing.Values.ToList();

		/// <summary>The parsed sheet at <paramref name="url"/>; null and recorded as missing when the snapshot does not have it.</summary>
		public CssSheet? Get(string url, out MissingSheet? gap)
		{
			gap = null;
			string file;
			try
			{
				file = Path.Combine(downloadDirectory, Urls.Naming.GenerateAssetFileName(url));
				// A quarantined stylesheet that the settle step identified carries ".css".
				var settled = Path.ChangeExtension(file, ".css");
				if (file.EndsWith(Urls.Naming.AssetQuarantine, StringComparison.OrdinalIgnoreCase) && !File.Exists(file) && File.Exists(settled))
				{
					file = settled;
				}
			}
			catch (UriFormatException)
			{
				return null;
			}

			var sheet = parsed.GetOrAdd(file, path =>
			{
				try
				{
					return File.Exists(path) ? CssSheet.Parse(File.ReadAllText(path, Encoding.UTF8), Path.GetFileName(path)) : null;
				}
				catch (IOException)
				{
					return null;
				}
			});

			if (sheet is null)
			{
				gap = missing.GetOrAdd(url, u =>
				{
					var status = crawlStatus(u.Replace(":443", string.Empty, StringComparison.Ordinal));
					return status switch
					{
						"404" or "410" => new MissingSheet(u, $"returns {status}", NotThere: true),
						null => new MissingSheet(u, "never fetched by the crawl", NotThere: false),
						_ => new MissingSheet(u, $"fetch failed ({status})", NotThere: false),
					};
				});
			}

			return sheet;
		}
	}

	/// <summary>
	/// The CSS of one page, in the order the browser applies it: &lt;link rel="stylesheet"&gt; and
	/// &lt;style&gt; in document order, each sheet's @import first. A media attribute or an @import
	/// media list makes rules conditional; disabled and alternate sheets do not apply. Answers what
	/// the cascade does to an element's display, visibility and content-visibility.
	/// </summary>
	internal sealed class PageStyles
	{
		private const int MaxImportDepth = 8;

		private readonly List<(CssRule Rule, string? ExtraCondition)> rules = [];
		private readonly List<MissingSheet> missing = [];

		public PageStyles(HtmlDocument rawPage, string pageUrl, StyleSheets sheets)
		{
			Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri);
			foreach (var node in rawPage.DocumentNode.Descendants())
			{
				if (node.NodeType != HtmlNodeType.Element || InInert(node))
				{
					continue;
				}

				if (node.Name == "link")
				{
					var rel = node.GetAttributeValue("rel", string.Empty).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
					if (!rel.Contains("stylesheet") || rel.Contains("alternate") || node.Attributes["disabled"] is not null
						|| baseUri is null || !Uri.TryCreate(baseUri, HtmlEntity.DeEntitize(node.GetAttributeValue("href", string.Empty)), out var href))
					{
						continue;
					}

					// A print stylesheet hides nothing from a screen reader or the keyboard: not read.
					if (PrintOnly(Media(node)))
					{
						continue;
					}

					AddSheet(href, Media(node), sheets, 0);
				}
				else if (node.Name == "style")
				{
					var type = node.GetAttributeValue("type", string.Empty).Trim();
					if (type.Length > 0 && !type.Equals("text/css", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					if (PrintOnly(Media(node)))
					{
						continue;
					}

					var sheet = CssSheet.Parse(node.InnerText, "inline <style>");
					AddParsed(sheet, baseUri, Media(node), sheets, 0);
				}
			}
		}

		/// <summary>The page's stylesheets the snapshot does not have and that a visitor would get (not 404/410).</summary>
		public IReadOnlyList<MissingSheet> Gaps => missing.Where(m => !m.NotThere).ToList();

		/// <summary>Referenced sheets that do not exist (404/410): the page is read without them, as a visitor sees it.</summary>
		public IReadOnlyList<MissingSheet> NotThere => missing.Where(m => m.NotThere).ToList();

		/// <summary>Rules with selectors the matcher could not read.</summary>
		public int UnsupportedRules { get; private set; }

		/// <summary>Whether any rule read sits in a cascade layer (read in source order only).</summary>
		public bool UsesLayers { get; private set; }

		private static string? Media(HtmlNode node)
		{
			var media = node.GetAttributeValue("media", string.Empty).Trim();
			return media.Length == 0 || media.Equals("all", StringComparison.OrdinalIgnoreCase) || media.Equals("screen", StringComparison.OrdinalIgnoreCase)
				? null
				: $"@media {media}";
		}

		private static bool InInert(HtmlNode node)
		{
			for (var n = node.ParentNode; n is not null; n = n.ParentNode)
			{
				if (n.Name is "noscript" or "template")
				{
					return true;
				}
			}

			return false;
		}

		private void AddSheet(Uri url, string? condition, StyleSheets sheets, int depth)
		{
			var sheet = sheets.Get(url.AbsoluteUri, out var gap);
			if (gap is not null && !missing.Contains(gap))
			{
				missing.Add(gap);
			}

			if (sheet is not null)
			{
				AddParsed(sheet, url, condition, sheets, depth);
			}
		}

		private void AddParsed(CssSheet sheet, Uri? baseUri, string? condition, StyleSheets sheets, int depth)
		{
			if (depth < MaxImportDepth && baseUri is not null)
			{
				foreach (var import in sheet.Imports)
				{
					if (!PrintOnly(Join(condition, import.Condition)) && Uri.TryCreate(baseUri, import.Url, out var target))
					{
						AddSheet(target, Join(condition, import.Condition), sheets, depth + 1);
					}
				}
			}

			UnsupportedRules += sheet.UnsupportedRules;
			foreach (var rule in sheet.Rules)
			{
				// Applies only when printing: on screen — where screen readers and Tab work — it never does.
				if (PrintOnly(Join(condition, rule.Condition)))
				{
					continue;
				}

				UsesLayers |= rule.InLayer;
				rules.Add((rule, condition));
			}
		}

		/// <summary>
		/// Whether a condition ("@media print", "@media screen and (max-width: 600px) and @media print")
		/// can only hold when printing: one of its @media parts lists only the print type ("only print",
		/// "print and (orientation: portrait)", "print, print"). "not print", "all", "screen" and a
		/// query without a type do apply on screen.
		/// </summary>
		internal static bool PrintOnly(string? condition)
		{
			if (string.IsNullOrWhiteSpace(condition))
			{
				return false;
			}

			foreach (var part in System.Text.RegularExpressions.Regex.Split(condition, @"(?=@(?:media|container|supports)\b)"))
			{
				var trimmed = part.Trim();
				if (trimmed.EndsWith(" and", StringComparison.Ordinal))
				{
					trimmed = trimmed[..^4].TrimEnd();
				}

				if (!trimmed.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var queries = SplitTopLevel(trimmed["@media".Length..]);
				if (queries.Count > 0 && queries.All(IsPrintQuery))
				{
					return true;
				}
			}

			return false;
		}

		private static bool IsPrintQuery(string query)
		{
			var words = query.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			int at = words.Length > 0 && words[0] == "only" ? 1 : 0;
			return at < words.Length && words[at] == "print";
		}

		// "a, b (c, d)" → ["a", "b (c, d)"]: commas inside brackets do not separate queries.
		private static List<string> SplitTopLevel(string list)
		{
			var parts = new List<string>();
			int depth = 0, start = 0;
			for (int i = 0; i < list.Length; i++)
			{
				switch (list[i])
				{
					case '(':
						depth++;
						break;
					case ')':
						depth = Math.Max(0, depth - 1);
						break;
					case ',' when depth == 0:
						parts.Add(list[start..i]);
						start = i + 1;
						break;
				}
			}

			parts.Add(list[start..]);
			return parts.Where(p => p.Trim().Length > 0).ToList();
		}

		private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : $"{a} and {b}";

		// ── The cascade ───────────────────────────────────────────────────────

		/// <summary>A declaration that applies to an element, with what decides between them.</summary>
		private sealed record Applied(CssDeclaration Declaration, CssRule? Rule, string? Condition, bool InState, bool Inline,
			(int, int, int) Specificity, int Order, string Selector);

		/// <summary>
		/// What the CSS does to <paramref name="element"/>: hidden by an ancestor's or its own
		/// display:none, by an inherited visibility:hidden/collapse (a nearer visible wins), or by
		/// content-visibility:hidden on an ancestor. Hidden by rules that always apply wins;
		/// otherwise hidden once conditional rules count is Conditional. The evidence names the rule.
		/// </summary>
		public (CssVerdict Verdict, string Evidence) Resolve(HtmlNode element)
		{
			if (Decide(element, conditional: false) is { } hidden)
			{
				return (CssVerdict.Hidden, hidden);
			}

			return Decide(element, conditional: true) is { } conditional
				? (CssVerdict.Conditional, conditional)
				: (CssVerdict.Shown, string.Empty);
		}

		private string? Decide(HtmlNode element, bool conditional)
		{
			bool visibilityDecided = false;
			for (var node = element; node is not null && node.NodeType == HtmlNodeType.Element; node = CssSelector.Parent(node))
			{
				bool self = ReferenceEquals(node, element);
				var where = self ? string.Empty : $" on the ancestor {LinkAccessibility.Describe(node)}";

				if (Winner(node, "display", conditional) is { Declaration.Value: "none" } display)
				{
					return Evidence(display, where);
				}

				if (!visibilityDecided && Winner(node, "visibility", conditional) is { } visibility
					&& visibility.Declaration.Value is not ("inherit" or "unset"))
				{
					visibilityDecided = true;
					if (visibility.Declaration.Value is "hidden" or "collapse")
					{
						return Evidence(visibility, where);
					}
				}

				if (!self && Winner(node, "content-visibility", conditional) is { Declaration.Value: "hidden" } content)
				{
					return Evidence(content, where);
				}
			}

			return null;
		}

		private static string Evidence(Applied a, string where)
		{
			var declaration = $"{a.Declaration.Property}:{a.Declaration.Value}{(a.Declaration.Important ? " !important" : string.Empty)}";
			if (a.Inline)
			{
				return $"inline style {declaration}{where}";
			}

			var rule = a.Rule!;
			var when = a.Condition is not null ? $"only under {a.Condition}: " : a.InState ? "only in a state (:hover, :focus, :checked …): " : string.Empty;
			var layer = rule.InLayer ? " (in a cascade layer, read in source order)" : string.Empty;
			return $"{when}{rule.File}:{rule.Line}:{rule.Column} {Shorten(a.Selector)} {{{declaration}}}{where}{layer}";
		}

		private static string Shorten(string selector)
		{
			var s = string.Join(' ', selector.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
			return s.Length <= 120 ? s : s[..117] + "…";
		}

		// The declaration of one property that wins on one element: !important, inline, specificity, order.
		private Applied? Winner(HtmlNode node, string property, bool conditional)
		{
			Applied? best = null;
			for (int order = 0; order < rules.Count; order++)
			{
				var (rule, extra) = rules[order];
				var condition = Join(extra, rule.Condition);
				if (!conditional && condition is not null)
				{
					continue;
				}

				foreach (var declaration in rule.Declarations)
				{
					if (declaration.Property != property)
					{
						continue;
					}

					foreach (var selector in rule.Selectors)
					{
						var match = selector.Match(node);
						if (match == CssMatch.No || (match == CssMatch.InState && !conditional))
						{
							continue;
						}

						var candidate = new Applied(declaration, rule, condition, match == CssMatch.InState, false, selector.Specificity, order, Selector(rule, selector));
						if (best is null || Beats(candidate, best))
						{
							best = candidate;
						}
					}
				}
			}

			if (InlineDeclaration(node, property) is { } inline && (best is null || Beats(inline, best)))
			{
				best = inline;
			}

			return best;
		}

		private static string Selector(CssRule rule, CssSelector selector) =>
			rule.Selectors.Count == 1 ? rule.SelectorText : CssSelector.SplitList(rule.SelectorText).ElementAtOrDefault(rule.Selectors.ToList().IndexOf(selector)) ?? rule.SelectorText;

		private static bool Beats(Applied a, Applied b)
		{
			if (a.Declaration.Important != b.Declaration.Important)
			{
				return a.Declaration.Important;
			}

			if (a.Inline != b.Inline)
			{
				return a.Inline;
			}

			var cmp = a.Specificity.CompareTo(b.Specificity);
			return cmp != 0 ? cmp > 0 : a.Order > b.Order;
		}

		private static Applied? InlineDeclaration(HtmlNode node, string property)
		{
			Applied? found = null;
			foreach (var part in node.GetAttributeValue("style", string.Empty).Split(';'))
			{
				int colon = part.IndexOf(':');
				if (colon <= 0 || !part[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var value = part[(colon + 1)..].Trim();
				bool important = value.EndsWith("!important", StringComparison.OrdinalIgnoreCase);
				if (important)
				{
					value = value[..^"!important".Length].Trim();
				}

				found = new Applied(new CssDeclaration(property, value.ToLowerInvariant(), important), null, null, false, true, (1, 0, 0), int.MaxValue, "style");
			}

			return found;
		}
	}
}
