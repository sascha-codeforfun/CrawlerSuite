namespace CrawlerSuite.Core.Display
{
	/// <summary>The parts of an HTML snippet that get their own colour.</summary>
	public enum HtmlTokenKind
	{
		Text,
		Punctuation,
		Tag,
		AttributeName,
		AttributeValue,
		Comment,
		Entity,
	}

	/// <summary>
	/// Splits an HTML excerpt into coloured parts for display. Excerpts are cut out of pages, so
	/// a tag may be open at either end: the tokenizer never fails, it colours what it can.
	/// </summary>
	public static class HtmlTokens
	{
		/// <summary>True when the text contains something that reads as a tag or comment.</summary>
		public static bool LooksLikeHtml(string? text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}

			for (int i = 0; i < text.Length - 1; i++)
			{
				if (text[i] == '<' && (char.IsAsciiLetter(text[i + 1]) || text[i + 1] is '/' or '!'))
				{
					return text.IndexOf('>', i) > i;
				}
			}

			return false;
		}

		public static IReadOnlyList<(HtmlTokenKind Kind, string Text)> Tokenize(string text)
		{
			var tokens = new List<(HtmlTokenKind, string)>();
			int i = 0;
			while (i < text.Length)
			{
				if (text.AsSpan(i).StartsWith("<!--"))
				{
					int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
					end = end < 0 ? text.Length : end + 3;
					tokens.Add((HtmlTokenKind.Comment, text[i..end]));
					i = end;
				}
				else if (text[i] == '<' && i + 1 < text.Length && (char.IsAsciiLetter(text[i + 1]) || text[i + 1] is '/' or '!' or '?'))
				{
					i = ReadTag(text, i, tokens);
				}
				else if (text[i] == '&' && EntityEnd(text, i) is var entityEnd && entityEnd > i)
				{
					tokens.Add((HtmlTokenKind.Entity, text[i..entityEnd]));
					i = entityEnd;
				}
				else
				{
					int start = i;
					while (i < text.Length && text[i] != '<' && !(text[i] == '&' && EntityEnd(text, i) > i))
					{
						i++;
					}

					if (i == start)
					{
						i++; // a lone '<' that opens nothing
					}

					tokens.Add((HtmlTokenKind.Text, text[start..i]));
				}
			}

			return tokens;
		}

		/// <summary>Reads "&lt;tag attr="value" …&gt;"; returns where the tag ended.</summary>
		private static int ReadTag(string text, int i, List<(HtmlTokenKind, string)> tokens)
		{
			int start = i;
			i++;
			if (i < text.Length && text[i] is '/' or '!' or '?')
			{
				i++;
			}

			tokens.Add((HtmlTokenKind.Punctuation, text[start..i]));

			start = i;
			while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '>' and not '/')
			{
				i++;
			}

			if (i > start)
			{
				tokens.Add((HtmlTokenKind.Tag, text[start..i]));
			}

			while (i < text.Length)
			{
				char c = text[i];
				if (c == '>' || (c == '/' && i + 1 < text.Length && text[i + 1] == '>'))
				{
					int end = c == '>' ? i + 1 : i + 2;
					tokens.Add((HtmlTokenKind.Punctuation, text[i..end]));
					return end;
				}

				if (char.IsWhiteSpace(c))
				{
					start = i;
					while (i < text.Length && char.IsWhiteSpace(text[i]))
					{
						i++;
					}

					tokens.Add((HtmlTokenKind.Text, text[start..i]));
				}
				else if (c == '=')
				{
					tokens.Add((HtmlTokenKind.Punctuation, "="));
					i++;
				}
				else if (c is '"' or '\'')
				{
					int close = text.IndexOf(c, i + 1);
					int end = close < 0 ? text.Length : close + 1;
					tokens.Add((HtmlTokenKind.AttributeValue, text[i..end]));
					i = end;
				}
				else
				{
					// An attribute name, or an unquoted value right after '='.
					bool value = tokens.Count > 0 && tokens[^1] is (HtmlTokenKind.Punctuation, "=");
					start = i;
					while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '>' and not '=' && !(text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>'))
					{
						i++;
					}

					if (i == start)
					{
						i++;
					}

					tokens.Add((value ? HtmlTokenKind.AttributeValue : HtmlTokenKind.AttributeName, text[start..i]));
				}
			}

			return i; // cut off inside a tag
		}

		/// <summary>
		/// Where the values of URL attributes are (href, src, srcset, action, poster, data-src …),
		/// quotes included, as character spans of <paramref name="text"/>. The screen mask hides
		/// these like URLs; the spans follow <see cref="Tokenize"/>, so highlights stay in place.
		/// </summary>
		public static IReadOnlyList<(int Start, int Length)> UrlValueSpans(string text)
		{
			var spans = new List<(int, int)>();
			if (!LooksLikeHtml(text))
			{
				return spans;
			}

			int at = 0;
			string? name = null;      // the attribute the next value belongs to
			bool afterEquals = false;
			foreach (var (kind, part) in Tokenize(text))
			{
				switch (kind)
				{
					case HtmlTokenKind.AttributeName:
						name = part;
						afterEquals = false;
						break;
					case HtmlTokenKind.Punctuation when part == "=":
						afterEquals = name is not null;
						break;
					case HtmlTokenKind.AttributeValue:
						if (afterEquals && name is not null && IsUrlAttribute(name))
						{
							spans.Add((at, part.Length));
						}

						name = null;
						afterEquals = false;
						break;
					case HtmlTokenKind.Text when part.Trim().Length == 0:
						break; // whitespace between attributes
					default:
						name = null;
						afterEquals = false;
						break;
				}

				at += part.Length;
			}

			return spans;
		}

		/// <summary>Attributes whose value is a URL or a list of URLs.</summary>
		public static bool IsUrlAttribute(string name)
		{
			var n = name.Trim().ToLowerInvariant();
			return n is "href" or "src" or "srcset" or "imagesrcset" or "action" or "formaction" or "poster" or "cite"
					or "background" or "longdesc" or "data" or "manifest" or "codebase" or "archive" or "usemap" or "ping"
					or "xlink:href" or "itemid" or "itemtype"
				|| (n.StartsWith("data-", StringComparison.Ordinal)
					&& (n.Contains("src") || n.Contains("href") || n.Contains("url") || n.Contains("link")
						|| n.Contains("path") || n.Contains("image") || n.Contains("img") || n.Contains("poster")));
		}

		/// <summary>The end of "&amp;name;" or "&amp;#123;" starting at i; i when it is not an entity.</summary>
		private static int EntityEnd(string text, int i)
		{
			int j = i + 1;
			if (j < text.Length && text[j] == '#')
			{
				j++;
			}

			int start = j;
			while (j < text.Length && j - start < 32 && char.IsAsciiLetterOrDigit(text[j]))
			{
				j++;
			}

			return j > start && j < text.Length && text[j] == ';' ? j + 1 : i;
		}
	}
}
