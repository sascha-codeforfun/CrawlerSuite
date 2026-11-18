namespace CrawlerSuite.Core.SpellCheck
{
	using System.Collections.Generic;

	// Finds look-alike hyphens inside code identifiers of a script string BEFORE it is tokenized.
	//
	// Why a pre-scan: the spell tokenizer splits words at most dash-like characters (en dash,
	// em dash, minus sign, non-breaking hyphen …), so "margin－left" becomes two known words and
	// no unknown token ever forms for ConfusablePunctuationEnricher to label. Only look-alikes the
	// tokenizer keeps inside a word (the katakana prolonged sound marks) reached it. Reading the raw
	// string closes that gap without touching the tokenizer, so no other finding can change.
	//
	// What counts — all of it, so prose in a script string stays silent:
	//   • a lowercase ASCII identifier with at least one mapped look-alike between two of its
	//     letters, ASCII hyphens allowed elsewhere ("max−width", "border-top−color");
	//   • directly followed (spaces aside) by ':' — a CSS declaration or an object key;
	//   • not inside a quoted value nested in the string (content:"a—b" is text, not syntax);
	//   • digits or a look-alike at the edge never count ("a2−b", "—left").
	// An em dash between words in a sentence ("soon—later") is followed by no colon and is left
	// alone, as is anything with a capital ("E‑Mail").
	internal static class ScriptConfusables
	{
		/// <summary>The identifiers in <paramref name="literal"/> that hold a look-alike hyphen, in order, each once.</summary>
		internal static IReadOnlyList<string> Find(string literal)
		{
			var found = new List<string>();
			if (string.IsNullOrEmpty(literal))
			{
				return found;
			}

			var seen = new HashSet<string>(System.StringComparer.Ordinal);
			char quote = '\0';
			int i = 0;
			while (i < literal.Length)
			{
				char c = literal[i];

				// A quoted value nested in the string: skipped whole.
				if (quote != '\0')
				{
					if (c == quote)
					{
						quote = '\0';
					}

					i++;
					continue;
				}

				if (c is '"' or '\'')
				{
					quote = c;
					i++;
					continue;
				}

				if (!IsLower(c) || (i > 0 && IsIdentifierChar(literal[i - 1])))
				{
					i++;
					continue;
				}

				// An identifier starting here: lowercase letters joined by single hyphens or look-alikes.
				int start = i;
				int end = i;
				bool lookAlike = false;
				while (end < literal.Length)
				{
					char d = literal[end];
					if (IsLower(d))
					{
						end++;
						continue;
					}

					bool joiner = d == '-' || ConfusablePunctuationMap.HyphenConfusables.ContainsKey(d);
					if (joiner && end + 1 < literal.Length && IsLower(literal[end + 1]) && IsLower(literal[end - 1]))
					{
						lookAlike |= d != '-';
						end++;
						continue;
					}

					break;
				}

				i = end;
				if (!lookAlike || (end < literal.Length && (IsIdentifierChar(literal[end]) || ConfusablePunctuationMap.HyphenConfusables.ContainsKey(literal[end]))))
				{
					continue;
				}

				int k = end;
				while (k < literal.Length && literal[k] == ' ')
				{
					k++;
				}

				if (k < literal.Length && literal[k] == ':')
				{
					string token = literal[start..end];
					if (seen.Add(token))
					{
						found.Add(token);
					}
				}
			}

			return found;
		}

		private static bool IsLower(char c) => c >= 'a' && c <= 'z';

		private static bool IsIdentifierChar(char c) =>
			(c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
	}
}
