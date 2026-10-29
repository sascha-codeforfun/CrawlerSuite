namespace CrawlerSuite.Core.Boilerplate
{
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>What an XPath's steps say without running it: whether every branch ends in an attribute step.</summary>
	public static class XpathShape
	{
		/// <summary>
		/// True when every branch of <paramref name="xpath"/> ends in an attribute step (@name, @*,
		/// attribute::name, with predicates). Such an XPath, used to remove elements, hands the
		/// parser's caller the attributes' elements instead.
		/// </summary>
		public static bool EndsInAttribute(string? xpath)
		{
			var text = xpath?.Trim() ?? string.Empty;
			return text.Length > 0 && SplitTopLevel(text, '|').All(EndsInAttributeBranch);
		}

		/// <summary>Whether one branch of an XPath ends in an attribute step.</summary>
		private static bool EndsInAttributeBranch(string branch)
		{
			var text = branch.Trim();

			// "(…)[n]": the branch is what the parentheses hold, filtered.
			if (text.StartsWith('(') && Closing(text, 0) is int close and > 0)
			{
				var rest = text[(close + 1)..].Trim();
				if (OnlyPredicates(rest))
				{
					return SplitTopLevel(text[1..close], '|').All(EndsInAttributeBranch);
				}
			}

			int slash = LastTopLevel(text, '/');
			var step = (slash >= 0 ? text[(slash + 1)..] : text).Trim();
			return step.StartsWith('@') || step.StartsWith("attribute::", StringComparison.Ordinal);
		}

		/// <summary>Splits at <paramref name="separator"/> outside quotes, brackets and parentheses.</summary>
		private static IEnumerable<string> SplitTopLevel(string text, char separator)
		{
			int depth = 0, from = 0;
			char quote = '\0';
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (quote != '\0')
				{
					if (c == quote)
					{
						quote = '\0';
					}
				}
				else if (c is '\'' or '"')
				{
					quote = c;
				}
				else if (c is '[' or '(')
				{
					depth++;
				}
				else if (c is ']' or ')')
				{
					depth--;
				}
				else if (c == separator && depth == 0)
				{
					yield return text[from..i];
					from = i + 1;
				}
			}

			yield return text[from..];
		}

		/// <summary>The last <paramref name="c"/> outside quotes, brackets and parentheses; -1 if none.</summary>
		private static int LastTopLevel(string text, char c)
		{
			int depth = 0, last = -1;
			char quote = '\0';
			for (int i = 0; i < text.Length; i++)
			{
				char ch = text[i];
				if (quote != '\0')
				{
					if (ch == quote)
					{
						quote = '\0';
					}
				}
				else if (ch is '\'' or '"')
				{
					quote = ch;
				}
				else if (ch is '[' or '(')
				{
					depth++;
				}
				else if (ch is ']' or ')')
				{
					depth--;
				}
				else if (ch == c && depth == 0)
				{
					last = i;
				}
			}

			return last;
		}

		/// <summary>The index of the bracket closing the one at <paramref name="open"/>; -1 if none.</summary>
		private static int Closing(string text, int open)
		{
			int depth = 0;
			char quote = '\0';
			for (int i = open; i < text.Length; i++)
			{
				char c = text[i];
				if (quote != '\0')
				{
					if (c == quote)
					{
						quote = '\0';
					}
				}
				else if (c is '\'' or '"')
				{
					quote = c;
				}
				else if (c is '(' or '[')
				{
					depth++;
				}
				else if (c is ')' or ']')
				{
					depth--;
					if (depth == 0)
					{
						return i;
					}
				}
			}

			return -1;
		}

		/// <summary>True for "" or "[…][…]".</summary>
		private static bool OnlyPredicates(string text)
		{
			int i = 0;
			while (i < text.Length)
			{
				if (text[i] != '[')
				{
					return false;
				}

				int close = Closing(text, i);
				if (close < 0)
				{
					return false;
				}

				i = close + 1;
				while (i < text.Length && char.IsWhiteSpace(text[i]))
				{
					i++;
				}
			}

			return true;
		}
	}
}
