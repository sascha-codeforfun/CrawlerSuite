using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// A spelling finding as the page's own markup: where the spell check's logs name only a place
	/// ("data-caption of &lt;div&gt;: word"), the saved page is read back and the element shown as it
	/// stands, so triage and the ticket mark the word in the real HTML.
	/// <list type="bullet">
	/// <item>An attribute or a meta field: the element's start tag. Every attribute is shown; long
	/// values that do not hold the word are shortened, and one that does is cut to a window around
	/// it. One element holding the word in several attributes is one line.</item>
	/// <item>Page text: the markup of the element around the word, inline tags included, cut to a
	/// window that never ends inside a tag.</item>
	/// <item>A script string: as the log has it (the script line is already the real code).</item>
	/// </list>
	/// Text is shown decoded (entities as the letters they stand for) and on one line. An occurrence
	/// that cannot be found in the saved page keeps its plain line, so nothing wrong is ever shown;
	/// when none is found, there is no markup at all and the plain lines stand.
	/// </summary>
	public static class SpellingMarkup
	{
		/// <summary>A saved page read for the markup: parsed once, its elements indexed by name, each element's text worked out once.</summary>
		public sealed class Page
		{
			private readonly Dictionary<string, List<HtmlNode>> byName = new(StringComparer.OrdinalIgnoreCase);
			private readonly Dictionary<HtmlNode, string> texts = [];

			internal Page(HtmlDocument document)
			{
				Document = document;
				foreach (var node in document.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
				{
					if (!byName.TryGetValue(node.Name, out var list))
					{
						list = [];
						byName[node.Name] = list;
					}

					list.Add(node);
				}
			}

			internal HtmlDocument Document { get; }

			/// <summary>The elements of a name, in document order.</summary>
			internal IReadOnlyList<HtmlNode> Named(string name) => byName.TryGetValue(name, out var list) ? list : [];

			/// <summary>An element's text, decoded and on one line.</summary>
			internal string TextOf(HtmlNode node)
			{
				if (!texts.TryGetValue(node, out var text))
				{
					text = OneLine(Decoded(node.InnerText));
					texts[node] = text;
				}

				return text;
			}
		}

		/// <summary>Elements of one place listed before the rest are left out.</summary>
		internal const int ElementsListed = 3;

		/// <summary>Characters of page text shown on each side of the word.</summary>
		internal const int TextRadius = 90;

		/// <summary>An attribute value without the word longer than this is shortened.</summary>
		internal const int OtherValueMax = 40;

		/// <summary>An attribute value with the word longer than this is cut to a window around it.</summary>
		internal const int WordValueMax = 160;

		// "img[@alt]", "meta[@name=description]", "script[L12:34]", "p[#text]": where an occurrence stands.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex SourcePath = new(@"^(?<element>[\w-]+)\[(?:@(?<attribute>[\w:-]+)(?:=(?<name>[^\]]*))?|(?<script>L\d+:\d+)|(?<text>#text))\]$", RegexOptions.Compiled);

		private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

		private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
		{
			"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
		};

		/// <summary>
		/// The markup lines of a finding, one per element, in the order of the occurrences; null when
		/// no occurrence is found in <paramref name="page"/> (or there is no page).
		/// </summary>
		/// <param name="page">The saved page, parsed; null: no markup.</param>
		/// <param name="word">The flagged word as written.</param>
		/// <param name="occurrences">The occurrences as log 13 has them: the place and the plain excerpt.</param>
		/// <param name="language">The language of the plain lines kept for occurrences not found.</param>
		public static string? For(Page? page, string word, IReadOnlyList<(string Source, string Excerpt)> occurrences, Text.DocumentLanguage language)
		{
			if (page is null || word.Length == 0 || occurrences.Count == 0)
			{
				return null;
			}

			var lines = new List<string>();
			var shownElements = new HashSet<HtmlNode>();
			bool any = false;
			foreach (var (source, excerpt) in occurrences)
			{
				var m = SourcePath.Match(source);
				List<string>? found = null;
				if (m.Success && !m.Groups["script"].Success && m.Groups["element"].Value != "script"
					&& !m.Groups["attribute"].Value.StartsWith("on", StringComparison.OrdinalIgnoreCase))
				{
					var element = m.Groups["element"].Value;
					found = m.Groups["text"].Success
						? TextLines(page, element, word, excerpt, shownElements)
						: AttributeLines(page, element, m.Groups["attribute"].Value, m.Groups["name"].Success ? m.Groups["name"].Value : null, word, shownElements);
				}

				if (found is null)
				{
					lines.Add(SpellingSource.Shown(source, excerpt, language));
					continue;
				}

				any = true;
				lines.AddRange(found);
			}

			return any ? string.Join("\n", lines.Where(l => l.Length > 0).Distinct(StringComparer.Ordinal)) : null;
		}

		/// <summary>Reads and parses a saved page as the analysis does (byte-level encoding detection); null when it cannot be read.</summary>
		public static Page? Load(string? localFile)
		{
			if (string.IsNullOrEmpty(localFile) || !File.Exists(localFile))
			{
				return null;
			}

			try
			{
				var bytes = File.ReadAllBytes(localFile);
				return new Page(Html.HtmlDocuments.Parse(DetectEncoding.FromBytes(bytes).GetString(bytes)));
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				// Unreadable, or nested past the parser's cap (a plain Exception): the plain lines stand.
				return null;
			}
		}

		// ── attributes and meta fields ─────────────────────────────────────

		// The start tags of the elements whose attribute holds the word; null when none does. An
		// element already shown (the same word in another of its attributes) adds no line.
		private static List<string>? AttributeLines(Page page, string element, string attribute, string? metaName, string word, HashSet<HtmlNode> shown)
		{
			bool meta = string.Equals(element, "meta", StringComparison.OrdinalIgnoreCase) && metaName is { Length: > 0 };
			var valueAttribute = meta ? "content" : attribute;
			// Written as flagged where an element has it so; else in any case ("image" listed under "Image").
			List<HtmlNode> Matching(StringComparison comparison) => page.Named(element)
				.Where(n => !meta || string.Equals(n.GetAttributeValue("name", string.Empty).Trim(), metaName, StringComparison.OrdinalIgnoreCase))
				.Where(n => n.Attributes[valueAttribute] is { } a && HasWholeWord(Decoded(a.Value), word, comparison))
				.ToList();
			var matches = Matching(StringComparison.Ordinal);
			if (matches.Count == 0)
			{
				matches = Matching(StringComparison.OrdinalIgnoreCase);
			}
			if (matches.Count == 0)
			{
				return null;
			}

			var lines = new List<string>();
			foreach (var node in matches.Where(shown.Add).Take(ElementsListed))
			{
				lines.Add(StartTag(node, word));
			}

			if (matches.Count > ElementsListed)
			{
				lines.Add($"… +{matches.Count - ElementsListed}");
			}

			return lines;
		}

		// "<name a="v" b>": every attribute, values decoded and on one line; a long value without the
		// word shortened, one with it cut around the word.
		internal static string StartTag(HtmlNode node, string word)
		{
			var sb = new StringBuilder("<").Append(node.Name);
			foreach (var a in node.Attributes)
			{
				sb.Append(' ').Append(a.Name);
				if (a.Value is null || (a.Value.Length == 0 && a.QuoteType == AttributeValueQuote.None))
				{
					continue;
				}

				var value = OneLine(Decoded(a.Value));
				value = HasWholeWord(value, word, StringComparison.OrdinalIgnoreCase) ? Window(value, word, WordValueMax / 2)
					: value.Length > OtherValueMax ? value[..(OtherValueMax - 10)] + "…" : value;
				sb.Append("=\"").Append(value.Replace("\"", "&quot;")).Append('"');
			}

			return sb.Append('>').ToString();
		}

		// ── page text ──────────────────────────────────────────────────────

		// The markup around the word in the elements the text stands in; null when none holds it.
		private static List<string>? TextLines(Page page, string element, string word, string excerpt, HashSet<HtmlNode> shown)
		{
			// Written as flagged where an element has it so; else in any case ("image" listed under "Image").
			var comparison = StringComparison.Ordinal;
			var candidates = page.Named(element).Where(n => HasWholeWord(page.TextOf(n), word, comparison)).ToList();
			if (candidates.Count == 0)
			{
				comparison = StringComparison.OrdinalIgnoreCase;
				candidates = page.Named(element).Where(n => HasWholeWord(page.TextOf(n), word, comparison)).ToList();
			}

			// The innermost: an element holding another candidate shows nothing the inner one does not.
			candidates = candidates.Where(c => !candidates.Any(o => o != c && IsAncestor(c, o))).ToList();
			if (candidates.Count == 0)
			{
				return null;
			}

			// Where the excerpt's text is found, those elements; else every one with the word.
			var core = Core(excerpt);
			var fitting = core.Length > 0
				? candidates.Where(c => page.TextOf(c).Contains(core, StringComparison.Ordinal)).ToList()
				: [];
			var chosen = fitting.Count > 0 ? fitting : candidates;

			var lines = new List<string>();
			foreach (var node in chosen.Where(shown.Add).Take(ElementsListed))
			{
				var markup = OneLine(Render(node, word));
				var at = WordOutsideTags(markup, word, comparison);
				if (at < 0)
				{
					continue; // the word is split by markup: no line rather than a wrong one
				}

				lines.Add(TagSafeWindow(markup, at, word.Length, TextRadius));
			}

			if (lines.Count == 0)
			{
				return null;
			}

			if (chosen.Count > ElementsListed)
			{
				lines.Add($"… +{chosen.Count - ElementsListed}");
			}

			return lines;
		}

		// The element as markup: start tags as StartTag draws them, text decoded, comments left out.
		internal static string Render(HtmlNode node, string word)
		{
			var sb = new StringBuilder();
			void Walk(HtmlNode n)
			{
				switch (n.NodeType)
				{
					case HtmlNodeType.Text:
						sb.Append(Decoded(((HtmlTextNode)n).Text).Replace("<", "&lt;"));
						return;
					case HtmlNodeType.Comment:
						return;
					case HtmlNodeType.Element:
						sb.Append(StartTag(n, word));
						if (VoidElements.Contains(n.Name))
						{
							return;
						}

						foreach (var child in n.ChildNodes)
						{
							Walk(child);
						}

						sb.Append("</").Append(n.Name).Append('>');
						return;
					default:
						foreach (var child in n.ChildNodes)
						{
							Walk(child);
						}

						return;
				}
			}

			Walk(node);
			return sb.ToString();
		}

		// The first whole-word occurrence of the word in text, not inside a tag; -1 when none.
		internal static int WordOutsideTags(string markup, string word, StringComparison comparison = StringComparison.Ordinal)
		{
			bool inTag = false;
			for (int i = 0; i < markup.Length; i++)
			{
				char c = markup[i];
				if (c == '<')
				{
					inTag = true;
				}
				else if (c == '>')
				{
					inTag = false;
				}
				else if (!inTag && i + word.Length <= markup.Length && string.Compare(markup, i, word, 0, word.Length, comparison) == 0
					&& (i == 0 || !char.IsLetterOrDigit(markup[i - 1]))
					&& (i + word.Length >= markup.Length || !char.IsLetterOrDigit(markup[i + word.Length])))
				{
					return i;
				}
			}

			return -1;
		}

		// A window of radius characters around [at, at+length), widened so it neither starts nor ends inside a tag.
		internal static string TagSafeWindow(string markup, int at, int length, int radius)
		{
			int start = Math.Max(0, at - radius);
			int end = Math.Min(markup.Length, at + length + radius);

			int lt = markup.LastIndexOf('<', Math.Max(0, start));
			int gt = lt < 0 ? -1 : markup.IndexOf('>', lt);
			if (lt >= 0 && lt < start && gt >= start)
			{
				start = lt; // the start fell inside a tag: take the tag whole
			}

			if (end < markup.Length)
			{
				int open = markup.LastIndexOf('<', end - 1);
				int close = open < 0 ? -1 : markup.IndexOf('>', open);
				if (open >= 0 && open < end && (close < 0 || close >= end))
				{
					end = close < 0 ? markup.Length : close + 1; // the end fell inside a tag
				}
			}

			return (start > 0 ? "…" : string.Empty) + markup[start..end] + (end < markup.Length ? "…" : string.Empty);
		}

		// ── helpers ────────────────────────────────────────────────────────

		private static string Window(string value, string word, int radius)
		{
			int at = value.IndexOf(word, StringComparison.Ordinal);
			if (at < 0)
			{
				at = value.IndexOf(word, StringComparison.OrdinalIgnoreCase);
			}

			if (at < 0 || value.Length <= 2 * radius + word.Length)
			{
				return value;
			}

			int start = Math.Max(0, at - radius);
			int end = Math.Min(value.Length, at + word.Length + radius);
			return (start > 0 ? "…" : string.Empty) + value[start..end] + (end < value.Length ? "…" : string.Empty);
		}

		private static string Decoded(string? raw) => HtmlEntity.DeEntitize(raw ?? string.Empty) ?? string.Empty;

		private static string OneLine(string text) => Spaces.Replace(text, " ").Trim();

		// The excerpt's longest piece between ellipses, on one line: what the element's text must hold.
		private static string Core(string excerpt) =>
			excerpt.Split('…').Select(OneLine).OrderByDescending(p => p.Length).FirstOrDefault() ?? string.Empty;

		private static bool IsAncestor(HtmlNode ancestor, HtmlNode node)
		{
			for (var p = node.ParentNode; p != null; p = p.ParentNode)
			{
				if (p == ancestor)
				{
					return true;
				}
			}

			return false;
		}

		internal static bool HasWholeWord(string text, string word, StringComparison comparison = StringComparison.Ordinal)
		{
			for (int at = text.IndexOf(word, comparison); at >= 0; at = text.IndexOf(word, at + 1, comparison))
			{
				bool starts = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
				bool ends = at + word.Length >= text.Length || !char.IsLetterOrDigit(text[at + word.Length]);
				if (starts && ends)
				{
					return true;
				}
			}

			return false;
		}
	}
}
