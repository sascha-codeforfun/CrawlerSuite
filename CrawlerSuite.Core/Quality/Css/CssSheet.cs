namespace CrawlerSuite.Core.Quality.Css
{
	using System.Text;

	/// <summary>One declaration of a rule that the hidden-focusable check reads.</summary>
	internal sealed record CssDeclaration(string Property, string Value, bool Important);

	/// <summary>
	/// A style rule that sets display, visibility or content-visibility: its selectors, those
	/// declarations, the condition it stands under (a media or container query; null when none),
	/// and where it is written (file, line, column — minified files are one line).
	/// </summary>
	internal sealed record CssRule(
		IReadOnlyList<CssSelector> Selectors,
		string SelectorText,
		IReadOnlyList<CssDeclaration> Declarations,
		string? Condition,
		bool InLayer,
		string File,
		int Line,
		int Column);

	/// <summary>An @import of a sheet: the address as written and its media condition (null when none).</summary>
	internal sealed record CssImport(string Url, string? Condition);

	/// <summary>
	/// The parts of a stylesheet the hidden-focusable check needs, read without a CSS library:
	/// @import, style rules that set display / visibility / content-visibility (with !important),
	/// and the media and container queries around them. @supports applies as written; @layer is
	/// read in source order (a simplification, marked on its rules); @font-face, @keyframes and the
	/// like are skipped, as are rules nested inside rules. A selector the matcher cannot read is
	/// counted, never guessed.
	/// </summary>
	internal sealed class CssSheet
	{
		internal static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
		{
			"display", "visibility", "content-visibility",
		};

		private readonly string text;
		private readonly string file;
		private readonly List<CssRule> rules = [];
		private readonly List<CssImport> imports = [];
		private readonly int[] lineStarts;

		// Reading for url() references (which rule uses an image) instead of for hiding.
		private readonly bool urls;

		private CssSheet(string text, string file, bool urls = false)
		{
			this.urls = urls;
			this.text = BlankComments(text);
			this.file = file;
			var starts = new List<int> { 0 };
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] == '\n')
				{
					starts.Add(i + 1);
				}
			}

			lineStarts = [.. starts];
		}

		public IReadOnlyList<CssRule> Rules => rules;

		public IReadOnlyList<CssImport> Imports => imports;

		/// <summary>Rules whose selectors could not be read.</summary>
		public int UnsupportedRules { get; private set; }

		/// <summary>Read for url(): the rules whose selectors could not be read, with their declarations (no selectors).</summary>
		public IReadOnlyList<CssRule> UnreadableRules => unreadable;

		private readonly List<CssRule> unreadable = [];

		/// <summary>
		/// The style rules whose declarations hold a url() (background, list-style, content …), values
		/// kept as written — to tell which rule a referenced file belongs to.
		/// </summary>
		public static CssSheet ParseUrls(string text, string file)
		{
			var sheet = new CssSheet(text ?? string.Empty, file, urls: true);
			sheet.ParseBlock(0, sheet.text.Length, condition: null, inLayer: false, topLevel: true);
			return sheet;
		}

		public static CssSheet Parse(string text, string file)
		{
			var sheet = new CssSheet(text ?? string.Empty, file);
			sheet.ParseBlock(0, sheet.text.Length, condition: null, inLayer: false, topLevel: true);
			return sheet;
		}

		// Comments become spaces (line breaks kept), so positions stay the file's own.
		private static string BlankComments(string css)
		{
			var sb = new StringBuilder(css);
			int i = 0;
			while (i < sb.Length)
			{
				char c = sb[i];
				if (c is '"' or '\'')
				{
					i = SkipString(css, i);
					continue;
				}

				if (c == '/' && i + 1 < sb.Length && sb[i + 1] == '*')
				{
					int end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
					end = end < 0 ? sb.Length : end + 2;
					for (int k = i; k < end; k++)
					{
						if (sb[k] != '\n')
						{
							sb[k] = ' ';
						}
					}

					i = end;
					continue;
				}

				i++;
			}

			return sb.ToString();
		}

		private static int SkipString(string s, int i)
		{
			char quote = s[i++];
			while (i < s.Length && s[i] != quote)
			{
				i += s[i] == '\\' ? 2 : 1;
			}

			return Math.Min(i + 1, s.Length);
		}

		// The position after the block opened at "open" ('{'), strings and nesting respected.
		private int CloseOf(int open, int end)
		{
			int depth = 0;
			for (int i = open; i < end; i++)
			{
				char c = text[i];
				if (c is '"' or '\'')
				{
					i = SkipString(text, i) - 1;
				}
				else if (c == '{')
				{
					depth++;
				}
				else if (c == '}' && --depth == 0)
				{
					return i + 1;
				}
			}

			return end;
		}

		// The next '{' or ';' at depth 0 from i (strings and brackets respected); end when none.
		private int NextStop(int i, int end, out char stop)
		{
			int paren = 0;
			for (; i < end; i++)
			{
				char c = text[i];
				if (c is '"' or '\'')
				{
					i = SkipString(text, i) - 1;
				}
				else if (c is '(' or '[')
				{
					paren++;
				}
				else if (c is ')' or ']')
				{
					paren = Math.Max(0, paren - 1);
				}
				else if (paren == 0 && c is '{' or ';' or '}')
				{
					stop = c;
					return i;
				}
			}

			stop = '\0';
			return end;
		}

		private void ParseBlock(int from, int end, string? condition, bool inLayer, bool topLevel)
		{
			int i = from;
			while (i < end)
			{
				while (i < end && (char.IsWhiteSpace(text[i]) || text[i] is ';' or '}'))
				{
					i++;
				}

				if (i >= end)
				{
					break;
				}

				int stopAt = NextStop(i, end, out char stop);
				var prelude = text[i..stopAt].Trim();
				int preludeStart = i;
				if (stop != '{')
				{
					if (topLevel && prelude.StartsWith("@import", StringComparison.OrdinalIgnoreCase))
					{
						ReadImport(prelude[7..].Trim(), condition);
					}

					i = stopAt + 1;
					continue;
				}

				int close = CloseOf(stopAt, end);
				int inner = stopAt + 1;
				int innerEnd = Math.Max(inner, close - 1);
				if (prelude.StartsWith('@'))
				{
					var name = prelude.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
					var query = prelude[name.Length..].Trim();
					switch (name)
					{
						case "@media":
						case "@container":
							ParseBlock(inner, innerEnd, Join(condition, $"{name} {query}"), inLayer, topLevel: false);
							break;
						case "@supports":
						case "@document":
						case "@scope":
							ParseBlock(inner, innerEnd, condition, inLayer, topLevel: false);
							break;
						case "@layer":
							ParseBlock(inner, innerEnd, condition, inLayer: true, topLevel: false);
							break;
						default:
							break;   // @font-face, @keyframes, @page …: nothing that hides an element
					}
				}
				else
				{
					ReadRule(prelude, preludeStart, inner, innerEnd, condition, inLayer);
				}

				i = close;
			}
		}

		private static string Join(string? outer, string inner) => outer is null ? inner : $"{outer} and {inner}";

		private void ReadImport(string rest, string? condition)
		{
			string url;
			string media;
			if (rest.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
			{
				int close = rest.IndexOf(')');
				if (close < 0)
				{
					return;
				}

				url = rest[4..close].Trim().Trim('"', '\'');
				media = rest[(close + 1)..].Trim();
			}
			else if (rest.Length > 0 && rest[0] is '"' or '\'')
			{
				int close = rest.IndexOf(rest[0], 1);
				if (close < 0)
				{
					return;
				}

				url = rest[1..close];
				media = rest[(close + 1)..].Trim();
			}
			else
			{
				return;
			}

			// "layer(…)" and "supports(…)" before the media list do not condition it.
			while (media.StartsWith("layer", StringComparison.OrdinalIgnoreCase) || media.StartsWith("supports(", StringComparison.OrdinalIgnoreCase))
			{
				int space = media.IndexOf(')') is var p and >= 0 ? p + 1 : media.IndexOf(' ');
				media = space < 0 ? string.Empty : media[space..].Trim();
			}

			var own = media.Length == 0 || media.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : $"@media {media}";
			imports.Add(new CssImport(url, own is null ? condition : Join(condition, own)));
		}

		private void ReadRule(string prelude, int at, int inner, int innerEnd, string? condition, bool inLayer)
		{
			var declarations = new List<CssDeclaration>();
			int i = inner;
			while (i < innerEnd)
			{
				int stopAt = NextStop(i, innerEnd, out char stop);
				if (stop == '{')
				{
					// A rule nested in a rule (CSS nesting): skipped.
					i = CloseOf(stopAt, innerEnd);
					continue;
				}

				var declaration = text[i..stopAt];
				i = stopAt + 1;
				int colon = declaration.IndexOf(':');
				if (colon <= 0)
				{
					continue;
				}

				var property = declaration[..colon].Trim().ToLowerInvariant();
				var value = declaration[(colon + 1)..].Trim();
				if (urls ? value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) < 0 : !Properties.Contains(property))
				{
					continue;
				}

				bool important = false;
				int bang = value.LastIndexOf('!');
				if (bang >= 0 && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
				{
					important = true;
					value = value[..bang].Trim();
				}

				declarations.Add(new CssDeclaration(property, urls ? value : value.ToLowerInvariant(), important));
			}

			if (declarations.Count == 0)
			{
				return;
			}

			var selectors = new List<CssSelector>();
			foreach (var part in CssSelector.SplitList(prelude))
			{
				if (CssSelector.Parse(part) is { } selector)
				{
					selectors.Add(selector);
				}
				else
				{
					UnsupportedRules++;
				}
			}

			if (selectors.Count == 0)
			{
				if (urls)
				{
					var (l, c) = Position(at);
					unreadable.Add(new CssRule([], prelude, declarations, condition, inLayer, file, l, c));
				}

				return;
			}

			var (line, column) = Position(at);
			rules.Add(new CssRule(selectors, prelude, declarations, condition, inLayer, file, line, column));
		}

		private (int Line, int Column) Position(int at)
		{
			int index = Array.BinarySearch(lineStarts, at);
			int line = index >= 0 ? index : ~index - 1;
			return (line + 1, at - lineStarts[line] + 1);
		}
	}
}
