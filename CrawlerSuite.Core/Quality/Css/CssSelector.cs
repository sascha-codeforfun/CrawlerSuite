namespace CrawlerSuite.Core.Quality.Css
{
	using System.Text;
	using HtmlAgilityPack;

	/// <summary>How a selector applies to an element.</summary>
	internal enum CssMatch
	{
		/// <summary>It does not.</summary>
		No,

		/// <summary>It does, whatever the visitor does.</summary>
		Yes,

		/// <summary>Only in a state the page does not show by itself (:hover, :focus, :checked …).</summary>
		InState,
	}

	/// <summary>
	/// One complex selector ("main .card > input[type=checkbox]"), parsed for matching against the
	/// page's elements. Supported: type and *, #id, .class, every attribute form ([a], =, ~=, |=,
	/// ^=, $=, *=, with i/s), the four combinators, :not() :is() :where() (:matches, :-webkit-any),
	/// the structural pseudo-classes, :root, :empty, :lang(), :link/:any-link, :disabled/:enabled.
	/// The state pseudo-classes match "in state"; a pseudo-element means the rule does not style
	/// the element. Anything else makes the selector unreadable (Parse returns null).
	/// </summary>
	internal sealed class CssSelector
	{
		private static readonly HashSet<string> StatePseudo = new(StringComparer.Ordinal)
		{
			"hover", "focus", "focus-within", "focus-visible", "active", "target", "target-within", "visited",
			"checked", "indeterminate", "placeholder-shown", "valid", "invalid", "user-valid", "user-invalid",
			"in-range", "out-of-range", "required", "optional", "read-only", "read-write", "autofill",
			"-webkit-autofill", "fullscreen", "modal", "popover-open", "open", "closed", "default", "playing",
			"paused", "current", "past", "future", "blank", "defined",
		};

		private static readonly HashSet<string> PseudoElements = new(StringComparer.Ordinal)
		{
			"before", "after", "first-line", "first-letter", "marker", "placeholder", "selection", "backdrop",
			"file-selector-button", "cue", "part", "slotted", "spelling-error", "grammar-error", "highlight",
			"target-text", "view-transition",
		};

		private readonly List<(Compound Compound, char Combinator)> parts;

		private CssSelector(List<(Compound, char)> parts, (int A, int B, int C) specificity)
		{
			this.parts = parts;
			Specificity = specificity;
		}

		/// <summary>(ids, classes/attributes/pseudo-classes, types/pseudo-elements).</summary>
		public (int A, int B, int C) Specificity { get; }

		/// <summary>The selector styles a pseudo-element, never the element itself.</summary>
		public bool TargetsPseudoElement { get; private set; }

		/// <summary>The selector as written (one selector of a list, trimmed).</summary>
		public string Text { get; private set; } = string.Empty;

		/// <summary>
		/// The state pseudo-classes a selector names, in order and each once (":hover", ":checked" …):
		/// what a visitor must do for it to apply. Pseudo-elements ("::before") are not states.
		/// </summary>
		internal static IReadOnlyList<string> StatesIn(string selectorText) =>
			RegexWalk.Each(selectorText, @"(?<!:):(?<name>[\w-]+)")
				.Select(m => m.Groups["name"].Value.ToLowerInvariant())
				.Where(StatePseudo.Contains)
				.Distinct(StringComparer.Ordinal)
				.Select(n => ":" + n)
				.ToList();

		// ── Parsing ───────────────────────────────────────────────────────────

		/// <summary>The selectors of a list ("a, b"), split at top-level commas.</summary>
		internal static IEnumerable<string> SplitList(string list)
		{
			int depth = 0;
			int start = 0;
			for (int i = 0; i < list.Length; i++)
			{
				char c = list[i];
				if (c is '"' or '\'')
				{
					char q = c;
					for (i++; i < list.Length && list[i] != q; i++)
					{
						if (list[i] == '\\')
						{
							i++;
						}
					}
				}
				else if (c is '(' or '[')
				{
					depth++;
				}
				else if (c is ')' or ']')
				{
					depth--;
				}
				else if (c == ',' && depth == 0)
				{
					yield return list[start..i].Trim();
					start = i + 1;
				}
			}

			if (start < list.Length)
			{
				yield return list[start..].Trim();
			}
		}

		/// <summary>The parsed selector; null when it uses something the matcher cannot read.</summary>
		internal static CssSelector? Parse(string text)
		{
			try
			{
				var reader = new Reader(text.Trim());
				var parts = new List<(Compound, char)>();
				char combinator = ' ';
				bool pseudoElement = false;
				while (true)
				{
					reader.SkipSpace(out bool hadSpace);
					if (reader.End)
					{
						break;
					}

					if (parts.Count > 0)
					{
						char c = reader.Peek;
						if (c is '>' or '+' or '~')
						{
							combinator = c;
							reader.Next();
							reader.SkipSpace(out _);
						}
						else if (hadSpace)
						{
							combinator = ' ';
						}
					}

					var compound = Compound.Read(reader, ref pseudoElement);
					if (compound is null)
					{
						return null;
					}

					parts.Add((compound, parts.Count == 0 ? '\0' : combinator));
				}

				if (parts.Count == 0)
				{
					return null;
				}

				var spec = (0, 0, 0);
				foreach (var (compound, _) in parts)
				{
					spec = Add(spec, compound.Specificity);
				}

				return new CssSelector(parts, spec) { TargetsPseudoElement = pseudoElement, Text = text.Trim() };
			}
			catch (FormatException)
			{
				return null;
			}
		}

		private static (int, int, int) Add((int A, int B, int C) x, (int A, int B, int C) y) => (x.A + y.A, x.B + y.B, x.C + y.C);

		// ── Matching ──────────────────────────────────────────────────────────

		/// <summary>How the selector applies to <paramref name="element"/>.</summary>
		public CssMatch Match(HtmlNode element)
		{
			if (TargetsPseudoElement)
			{
				return CssMatch.No;
			}

			return MatchFrom(parts.Count - 1, element);
		}

		/// <summary>
		/// How the selector applies to <paramref name="element"/> as the element a pseudo-element
		/// (::before, ::after …) hangs on: such a rule's image is fetched when its element is there.
		/// </summary>
		internal CssMatch MatchOrigin(HtmlNode element) => MatchFrom(parts.Count - 1, element);

		private CssMatch MatchFrom(int index, HtmlNode element)
		{
			var (compound, combinator) = parts[index];
			var own = compound.Match(element);
			if (own == CssMatch.No || index == 0)
			{
				return own;
			}

			CssMatch best = CssMatch.No;
			foreach (var candidate in Candidates(element, combinator))
			{
				var rest = MatchFrom(index - 1, candidate);
				if (rest == CssMatch.Yes)
				{
					return own;   // Yes, or InState when this compound needs a state
				}

				if (rest == CssMatch.InState)
				{
					best = CssMatch.InState;
				}

				if (combinator is '>' or '+')
				{
					break;
				}
			}

			return best;
		}

		private static IEnumerable<HtmlNode> Candidates(HtmlNode element, char combinator)
		{
			switch (combinator)
			{
				case '>':
					if (Parent(element) is { } parent)
					{
						yield return parent;
					}

					break;
				case ' ':
					for (var p = Parent(element); p is not null; p = Parent(p))
					{
						yield return p;
					}

					break;
				case '+':
					if (PreviousElement(element) is { } previous)
					{
						yield return previous;
					}

					break;
				case '~':
					for (var s = PreviousElement(element); s is not null; s = PreviousElement(s))
					{
						yield return s;
					}

					break;
			}
		}

		internal static HtmlNode? Parent(HtmlNode node) =>
			node.ParentNode is { NodeType: HtmlNodeType.Element } p ? p : null;

		private static HtmlNode? PreviousElement(HtmlNode node)
		{
			for (var s = node.PreviousSibling; s is not null; s = s.PreviousSibling)
			{
				if (s.NodeType == HtmlNodeType.Element)
				{
					return s;
				}
			}

			return null;
		}

		private static HtmlNode? NextElement(HtmlNode node)
		{
			for (var s = node.NextSibling; s is not null; s = s.NextSibling)
			{
				if (s.NodeType == HtmlNodeType.Element)
				{
					return s;
				}
			}

			return null;
		}

		// ── Compound selectors ────────────────────────────────────────────────

		private sealed class Compound
		{
			private string? type;   // null = any
			private readonly List<Func<HtmlNode, CssMatch>> tests = [];

			public (int A, int B, int C) Specificity { get; private set; }

			public CssMatch Match(HtmlNode element)
			{
				if (element.NodeType != HtmlNodeType.Element || (type is not null && !element.Name.Equals(type, StringComparison.OrdinalIgnoreCase)))
				{
					return CssMatch.No;
				}

				var result = CssMatch.Yes;
				foreach (var test in tests)
				{
					var r = test(element);
					if (r == CssMatch.No)
					{
						return CssMatch.No;
					}

					if (r == CssMatch.InState)
					{
						result = CssMatch.InState;
					}
				}

				return result;
			}

			public static Compound? Read(Reader reader, ref bool pseudoElement)
			{
				var compound = new Compound();
				bool any = false;
				if (reader.Peek == '*')
				{
					reader.Next();
					any = true;
				}
				else if (Reader.IsNameStart(reader.Peek))
				{
					compound.type = reader.Name().ToLowerInvariant();
					compound.Specificity = (0, 0, 1);
					any = true;
				}

				while (!reader.End)
				{
					char c = reader.Peek;
					if (c == '#')
					{
						reader.Next();
						var id = reader.Name();
						compound.tests.Add(e => e.Id == id ? CssMatch.Yes : CssMatch.No);
						compound.Specificity = Add(compound.Specificity, (1, 0, 0));
					}
					else if (c == '.')
					{
						reader.Next();
						var cls = reader.Name();
						compound.tests.Add(e => HasClass(e, cls) ? CssMatch.Yes : CssMatch.No);
						compound.Specificity = Add(compound.Specificity, (0, 1, 0));
					}
					else if (c == '[')
					{
						compound.tests.Add(Attribute(reader));
						compound.Specificity = Add(compound.Specificity, (0, 1, 0));
					}
					else if (c == ':')
					{
						reader.Next();
						bool element = reader.Peek == ':';
						if (element)
						{
							reader.Next();
						}

						var name = reader.Name().ToLowerInvariant();
						string? argument = null;
						if (!reader.End && reader.Peek == '(')
						{
							argument = reader.Parenthesised();
						}

						var bare = name.StartsWith('-') && name.IndexOf('-', 1) is var dash and > 0 && (name.StartsWith("-webkit-") || name.StartsWith("-moz-") || name.StartsWith("-ms-"))
							? name[(dash + 1)..]
							: name;
						if (element || PseudoElements.Contains(bare) || (bare is "before" or "after" or "first-line" or "first-letter"))
						{
							pseudoElement = true;
							compound.Specificity = Add(compound.Specificity, (0, 0, 1));
						}
						else if (!compound.Pseudo(bare, argument))
						{
							return null;
						}
					}
					else
					{
						break;
					}

					any = true;
				}

				return any ? compound : null;
			}

			private bool Pseudo(string name, string? argument)
			{
				switch (name)
				{
					case "not" or "is" or "matches" or "any" or "where" when argument is not null:
						var list = SplitList(argument).Select(Parse).ToList();
						if (list.Count == 0 || list.Any(s => s is null))
						{
							return false;
						}

						var selectors = list.Select(s => s!).ToList();
						bool negate = name == "not";
						tests.Add(e =>
						{
							var best = CssMatch.No;
							foreach (var s in selectors)
							{
								var m = s.Match(e);
								if (m == CssMatch.Yes)
								{
									best = CssMatch.Yes;
									break;
								}

								if (m == CssMatch.InState)
								{
									best = CssMatch.InState;
								}
							}

							return !negate ? best : best == CssMatch.Yes ? CssMatch.No : best == CssMatch.InState ? CssMatch.InState : CssMatch.Yes;
						});
						if (name != "where")
						{
							var max = selectors.Select(s => s.Specificity).Max();
							Specificity = Add(Specificity, max);
						}

						return true;

					case "root":
						tests.Add(e => Parent(e) is null || e.Name == "html" ? CssMatch.Yes : CssMatch.No);
						break;
					case "empty":
						tests.Add(e => e.ChildNodes.All(n => n.NodeType == HtmlNodeType.Comment) ? CssMatch.Yes : CssMatch.No);
						break;
					case "first-child":
						tests.Add(e => PreviousElement(e) is null ? CssMatch.Yes : CssMatch.No);
						break;
					case "last-child":
						tests.Add(e => NextElement(e) is null ? CssMatch.Yes : CssMatch.No);
						break;
					case "only-child":
						tests.Add(e => PreviousElement(e) is null && NextElement(e) is null ? CssMatch.Yes : CssMatch.No);
						break;
					case "first-of-type":
						tests.Add(e => Index(e, sameType: true, fromEnd: false) == 1 ? CssMatch.Yes : CssMatch.No);
						break;
					case "last-of-type":
						tests.Add(e => Index(e, sameType: true, fromEnd: true) == 1 ? CssMatch.Yes : CssMatch.No);
						break;
					case "only-of-type":
						tests.Add(e => Index(e, true, false) == 1 && Index(e, true, true) == 1 ? CssMatch.Yes : CssMatch.No);
						break;
					case "nth-child" or "nth-last-child" or "nth-of-type" or "nth-last-of-type" when argument is not null:
						if (argument.Contains(" of ", StringComparison.OrdinalIgnoreCase) || Nth(argument) is not { } nth)
						{
							return false;
						}

						bool ofType = name.Contains("of-type", StringComparison.Ordinal);
						bool fromEnd = name.Contains("last", StringComparison.Ordinal);
						tests.Add(e => Fits(Index(e, ofType, fromEnd), nth.A, nth.B) ? CssMatch.Yes : CssMatch.No);
						break;
					case "lang" when argument is not null:
						var wanted = argument.Trim().Trim('"', '\'').ToLowerInvariant();
						tests.Add(e => LangOf(e) is { } l && (l == wanted || l.StartsWith(wanted + "-", StringComparison.Ordinal)) ? CssMatch.Yes : CssMatch.No);
						break;
					case "link" or "any-link":
						tests.Add(e => e.Name is "a" or "area" && e.Attributes["href"] is not null ? CssMatch.Yes : CssMatch.No);
						break;
					case "disabled":
						tests.Add(e => e.Attributes["disabled"] is not null ? CssMatch.Yes : CssMatch.No);
						break;
					case "enabled":
						tests.Add(e => e.Attributes["disabled"] is null ? CssMatch.Yes : CssMatch.No);
						break;
					case "scope":
						tests.Add(e => Parent(e) is null || e.Name == "html" ? CssMatch.Yes : CssMatch.No);
						break;
					default:
						if (!StatePseudo.Contains(name))
						{
							return false;   // :has() and anything unknown: not read
						}

						tests.Add(_ => CssMatch.InState);
						break;
				}

				Specificity = Add(Specificity, (0, 1, 0));
				return true;
			}

			private static string? LangOf(HtmlNode e)
			{
				for (var n = e; n is not null && n.NodeType == HtmlNodeType.Element; n = n.ParentNode)
				{
					if (n.Attributes["lang"] is { } lang)
					{
						return lang.Value.Trim().ToLowerInvariant();
					}
				}

				return null;
			}

			private static int Index(HtmlNode e, bool sameType, bool fromEnd)
			{
				int n = 1;
				for (var s = fromEnd ? NextElement(e) : PreviousElement(e); s is not null; s = fromEnd ? NextElement(s) : PreviousElement(s))
				{
					if (!sameType || s.Name == e.Name)
					{
						n++;
					}
				}

				return n;
			}

			private static (int A, int B)? Nth(string argument)
			{
				var s = argument.Replace(" ", string.Empty).ToLowerInvariant();
				if (s == "odd")
				{
					return (2, 1);
				}

				if (s == "even")
				{
					return (2, 0);
				}

				int n = s.IndexOf('n');
				if (n < 0)
				{
					return int.TryParse(s, out var b) ? (0, b) : null;
				}

				var aText = s[..n];
				int a = aText is "" or "+" ? 1 : aText == "-" ? -1 : int.TryParse(aText, out var av) ? av : int.MinValue;
				if (a == int.MinValue)
				{
					return null;
				}

				var bText = s[(n + 1)..];
				int bv = 0;
				if (bText.Length > 0 && !int.TryParse(bText, out bv))
				{
					return null;
				}

				return (a, bv);
			}

			private static bool Fits(int index, int a, int b) =>
				a == 0 ? index == b : (index - b) % a == 0 && (index - b) / a >= 0;

			private static bool HasClass(HtmlNode e, string cls)
			{
				var value = e.GetAttributeValue("class", string.Empty);
				foreach (var token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
				{
					if (token == cls)
					{
						return true;
					}
				}

				return false;
			}

			private static Func<HtmlNode, CssMatch> Attribute(Reader reader)
			{
				var inside = reader.Bracketed().Trim();
				int op = inside.IndexOfAny(['=', '~', '|', '^', '$', '*']);
				if (op < 0)
				{
					var bare = inside.ToLowerInvariant();
					return e => e.Attributes[bare] is not null ? CssMatch.Yes : CssMatch.No;
				}

				var name = inside[..op].Trim().ToLowerInvariant();
				string kind = inside[op] == '=' ? "=" : inside.Substring(op, 2);
				if (kind.Length == 2 && kind[1] != '=')
				{
					throw new FormatException();
				}

				var rest = inside[(op + kind.Length)..].Trim();
				bool ignoreCase = false;
				string value;
				if (rest.Length > 0 && rest[0] is '"' or '\'')
				{
					int close = rest.IndexOf(rest[0], 1);
					if (close < 0)
					{
						throw new FormatException();
					}

					value = rest[1..close];
					ignoreCase = rest[(close + 1)..].Trim().Equals("i", StringComparison.OrdinalIgnoreCase);
				}
				else
				{
					var bits = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
					value = bits.Length > 0 ? bits[0] : string.Empty;
					ignoreCase = bits.Length > 1 && bits[1].Equals("i", StringComparison.OrdinalIgnoreCase);
				}

				var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
				return e =>
				{
					if (e.Attributes[name] is not { } attribute)
					{
						return CssMatch.No;
					}

					var v = HtmlEntity.DeEntitize(attribute.Value) ?? string.Empty;
					bool ok = kind switch
					{
						"=" => v.Equals(value, cmp),
						"~=" => v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(t => t.Equals(value, cmp)),
						"|=" => v.Equals(value, cmp) || v.StartsWith(value + "-", cmp),
						"^=" => value.Length > 0 && v.StartsWith(value, cmp),
						"$=" => value.Length > 0 && v.EndsWith(value, cmp),
						"*=" => value.Length > 0 && v.Contains(value, cmp),
						_ => false,
					};
					return ok ? CssMatch.Yes : CssMatch.No;
				};
			}
		}

		// ── Reading ───────────────────────────────────────────────────────────

		private sealed class Reader(string text)
		{
			private int position;

			public bool End => position >= text.Length;

			public char Peek => position < text.Length ? text[position] : '\0';

			public void Next() => position++;

			public void SkipSpace(out bool hadSpace)
			{
				hadSpace = false;
				while (!End && char.IsWhiteSpace(text[position]))
				{
					position++;
					hadSpace = true;
				}
			}

			public static bool IsNameStart(char c) => char.IsLetter(c) || c is '_' or '-' or '\\' || c > 127;

			public string Name()
			{
				var sb = new StringBuilder();
				while (!End)
				{
					char c = text[position];
					if (c == '\\' && position + 1 < text.Length)
					{
						// An escape: hex code point, or the next character as itself.
						int start = ++position;
						while (position < text.Length && position - start < 6 && Uri.IsHexDigit(text[position]))
						{
							position++;
						}

						if (position > start)
						{
							sb.Append(char.ConvertFromUtf32(Convert.ToInt32(text[start..position], 16)));
							if (position < text.Length && text[position] == ' ')
							{
								position++;
							}
						}
						else
						{
							sb.Append(text[position++]);
						}

						continue;
					}

					if (char.IsLetterOrDigit(c) || c is '-' or '_' || c > 127)
					{
						sb.Append(c);
						position++;
						continue;
					}

					break;
				}

				if (sb.Length == 0)
				{
					throw new FormatException();
				}

				return sb.ToString();
			}

			public string Parenthesised() => Enclosed('(', ')');

			public string Bracketed() => Enclosed('[', ']');

			private string Enclosed(char open, char close)
			{
				if (Peek != open)
				{
					throw new FormatException();
				}

				int depth = 0;
				int start = position + 1;
				for (; position < text.Length; position++)
				{
					char c = text[position];
					if (c is '"' or '\'')
					{
						char q = c;
						for (position++; position < text.Length && text[position] != q; position++)
						{
							if (text[position] == '\\')
							{
								position++;
							}
						}
					}
					else if (c == open)
					{
						depth++;
					}
					else if (c == close && --depth == 0)
					{
						var inner = text[start..position];
						position++;
						return inner;
					}
				}

				throw new FormatException();
			}
		}
	}
}
