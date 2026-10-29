namespace CrawlerSuite.Core.Quality
{
	using System.Text;
	using System.Text.RegularExpressions;
	using HtmlAgilityPack;

	/// <summary>
	/// A word written twice in a row ("Haus Haus"): WORD_DOUBLED. Read per block of text (a paragraph,
	/// a list item, a cell …), so inline formatting does not hide it ("&lt;b&gt;Haus&lt;/b&gt; Haus"); only
	/// spaces and line breaks may stand between the two — punctuation means it is meant ("Haus, Haus").
	/// Words with a digit are not judged ("20 20").
	///
	/// Some doublings are grammar, not slips: a list per language in the configuration
	/// (<see cref="ContentQualityConfig.DoubledWordExceptions"/>; "die die", "had had"). The list
	/// cannot see the grammar around the pair, so a slip of a listed word passes too — the price of
	/// no grammar tagging. In German a capitalised word followed by the same word in small letters is
	/// usually a noun and its verb ("Leben leben"): there only the same spelling counts, except for the
	/// first word of a sentence ("Die die …").
	/// </summary>
	public static class DoubledWords
	{
		internal const string IssueType = "WORD_DOUBLED";

		/// <summary>
		/// A soft note, not an error: a term on its own line, then the same word starting the next
		/// line ("Gartenhaus&lt;br&gt; Gartenhaus heißt …"). A colon would mark it as the lead-in.
		/// </summary>
		internal const string WeakPunctuationType = "PUNCTUATION_WEAK";

		// A <br> in a block's text: white space to the reader, but the end of a line — a pair across it
		// is a lead-in term without its colon, not a doubled word.
		private const char LineBreak = '\u2028';

		internal static IEnumerable<QualityIssue> Check(
			string filename, HtmlDocument doc, ContentQualityConfig config,
			IReadOnlyDictionary<string, List<string>>? pageLanguageOverrides = null,
			string defaultLanguage = "")
		{
			var url = pageLanguageOverrides is { Count: > 0 } ? CrawlIndex.LookUpUrlForFile(filename) : string.Empty;
			var pageLanguages = CrawlerSuite.Core.Html.PageLanguageSet.Resolve(url, doc, pageLanguageOverrides, defaultLanguage);
			var pageFallback = pageLanguages.Count == 1 ? pageLanguages[0] : string.Empty;

			foreach (var (block, text) in Segments(doc))
			{
				var island = CrawlerSuite.Core.Html.Language.NearestElementLanguage(block, pageFallback);
				IReadOnlyList<string> languages = island.Length > 0 && !string.Equals(island, pageFallback, StringComparison.OrdinalIgnoreCase)
					? [island]
					: pageLanguages;

				foreach (var issue in Scan(filename, text, block?.Name ?? "text", languages, config))
				{
					yield return RawExcerpt(block, issue, config) is { } raw ? issue with { Context = raw } : issue;
				}
			}
		}

		/// <summary>The findings in one block's text, read in <paramref name="languages"/>.</summary>
		internal static IEnumerable<QualityIssue> Scan(
			string filename, string text, string source, IReadOnlyList<string> languages, ContentQualityConfig config)
		{
			var exceptions = Exceptions(config, languages);
			bool german = languages.Any(l => string.Equals(l.Split('-')[0], "de", StringComparison.OrdinalIgnoreCase));
			var words = Words(text);
			for (int i = 1; i < words.Count; i++)
			{
				var (first, firstStart) = words[i - 1];
				var (second, secondStart) = words[i];
				if (first.Length == 0 || second.Length == 0
					|| !OnlySpaceBetween(text, firstStart + first.Length, secondStart)
					|| !string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
					|| exceptions.Contains(first.ToLowerInvariant()))
				{
					continue;
				}

				// German: a capital then the same word in small letters is a noun and its verb, unless
				// the capital is only the start of the sentence.
				if (german && !string.Equals(first, second, StringComparison.Ordinal)
					&& !(StartsSentence(text, firstStart) && string.Equals(first.ToLowerInvariant(), second.ToLowerInvariant(), StringComparison.Ordinal)
						&& string.Equals(first[1..], second[1..], StringComparison.Ordinal)))
				{
					continue;
				}

				var plain = text.Replace(LineBreak, ' ');
				var context = LogExcerpt.Truncate(Excerpt.Centred(plain, secondStart, Math.Max(config.ContentQualityExcerptRadius * 2, 80)), config.ContentQualityMaxExcerpt);
				yield return text.IndexOf(LineBreak, firstStart + first.Length, secondStart - firstStart - first.Length) >= 0
					? new QualityIssue(filename, WeakPunctuationType,
						$"{source}: “{first}” before a line break and again after it — a colon would mark it as the lead-in (“{first}:”)", context)
					: new QualityIssue(filename, IssueType, $"{source}: the word twice in “{first} {second}”", context);
			}
		}

		/// <summary>The listed doublings of the languages, lower case.</summary>
		internal static HashSet<string> Exceptions(ContentQualityConfig config, IEnumerable<string> languages)
		{
			var set = new HashSet<string>(StringComparer.Ordinal);
			foreach (var language in languages)
			{
				var code = language.Split('-')[0];
				foreach (var (key, words) in config.DoubledWordExceptions ?? [])
				{
					if (string.Equals(key.Split('-')[0], code, StringComparison.OrdinalIgnoreCase))
					{
						foreach (var word in words ?? [])
						{
							if (!string.IsNullOrWhiteSpace(word))
							{
								set.Add(word.Trim().ToLowerInvariant());
							}
						}
					}
				}
			}

			return set;
		}

		/// <summary>
		/// The text of each block in document order: the text nodes that share a block, joined as the
		/// reader sees them; a nested block starts its own text, and so does an element with a role or
		/// aria-live — a widget or live region is announced on its own, not as part of the sentence
		/// around it. A &lt;br&gt; is kept as a line break. Code, preformatted text and scripts are left out.
		/// </summary>
		private static IEnumerable<(HtmlNode? Block, string Text)> Segments(HtmlDocument doc)
		{
			HtmlNode? current = null;
			var text = new StringBuilder();
			foreach (var node in doc.DocumentNode.Descendants())
			{
				if (node.NodeType == HtmlNodeType.Element && node.Name == "br")
				{
					text.Append(LineBreak);
					continue;
				}

				if (node.NodeType != HtmlNodeType.Text || HyphenSpacing.InSkippedElement(node))
				{
					continue;
				}

				var block = OwnerOf(node);
				if (!ReferenceEquals(block, current))
				{
					if (text.Length > 0)
					{
						yield return (current, text.ToString());
					}

					text.Clear();
					current = block;
				}

				text.Append(HtmlEntity.DeEntitize(node.InnerText) ?? string.Empty);
			}

			if (text.Length > 0)
			{
				yield return (current, text.ToString());
			}
		}

		/// <summary>The element whose text a text node belongs to: an element with a role or aria-live on the way up, else the block.</summary>
		private static HtmlNode? OwnerOf(HtmlNode node)
		{
			var block = HyphenSpacing.BlockOf(node);
			for (var n = node.ParentNode; n is not null && n.NodeType == HtmlNodeType.Element; n = n.ParentNode)
			{
				if (n.Attributes.Contains("role") || n.Attributes.Contains("aria-live"))
				{
					return n;
				}

				if (ReferenceEquals(n, block))
				{
					break;
				}
			}

			return block;
		}

		/// <summary>
		/// The words of a text with where they start: runs of letters (and an inner ' or ’). A token with
		/// a digit or an underscore ("20", "TIDE_ID") is a number or a name, not a word: it breaks a pair.
		/// </summary>
		private static List<(string Word, int Start)> Words(string text)
		{
			var words = new List<(string, int)>();
			int i = 0;
			while (i < text.Length)
			{
				if (!char.IsLetterOrDigit(text[i]) && text[i] != '_')
				{
					i++;
					continue;
				}

				int start = i;
				bool digit = false;
				while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'
					|| (text[i] is '\'' or '’' && i + 1 < text.Length && char.IsLetter(text[i + 1]) && i > start)))
				{
					digit |= char.IsDigit(text[i]) || text[i] == '_';
					i++;
				}

				if (!digit)
				{
					words.Add((text[start..i], start));
				}
				else
				{
					// A number breaks the pair: "Haus 20 Haus" is no doubling.
					words.Add((string.Empty, start));
				}
			}

			return words;
		}

		// Only white space (a line break, a no-break space) between the two, and at least some.
		private static bool OnlySpaceBetween(string text, int from, int to)
		{
			if (to <= from)
			{
				return false;
			}

			for (int k = from; k < to; k++)
			{
				if (!char.IsWhiteSpace(text[k]) && text[k] != '­')
				{
					return false;
				}
			}

			return true;
		}

		// The word at "at" begins the text or follows a sentence end (. ! ? and space).
		private static bool StartsSentence(string text, int at)
		{
			int k = at - 1;
			while (k >= 0 && char.IsWhiteSpace(text[k]))
			{
				k--;
			}

			return k < 0 || text[k] is '.' or '!' or '?' or ':' or '„' or '"' or '“' or '»' or '«';
		}

		/// <summary>The block's HTML around the pair on one line; null when the pair cannot be found in it.</summary>
		private static string? RawExcerpt(HtmlNode? block, QualityIssue issue, ContentQualityConfig config)
		{
			if (block is null || Spot(block.OuterHtml, issue.Detail) is not { } spot)
			{
				return null;
			}

			var excerpt = Excerpt.Centred(block.OuterHtml, spot.Second.Index, Math.Max(config.ContentQualityExcerptRadius * 2, 80));
			return LogExcerpt.Truncate(WhitespaceRun.Replace(excerpt, " "), config.ContentQualityMaxExcerpt);
		}

		/// <summary>
		/// Where the pair named in <paramref name="detail"/> stands in <paramref name="html"/>: the first
		/// and the second word, with spaces, line breaks, no-break spaces and tags allowed between them.
		/// Null when it is not there.
		/// </summary>
		internal static (Group First, Group Second)? Spot(string html, string detail)
		{
			// The first quoted part: the pair ("Haus Haus"), or the one term written twice at a line break.
			int open = detail.IndexOf('“');
			int close = open < 0 ? -1 : detail.IndexOf('”', open + 1);
			if (open < 0 || close <= open)
			{
				return null;
			}

			var parts = detail[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 1)
			{
				parts = [parts[0], parts[0]];
			}

			if (parts.Length != 2)
			{
				return null;
			}

			var pattern = $@"(?<![\p{{L}}\p{{N}}])(?<first>{Word(parts[0])})(?:\s|&nbsp;|&#160;|&#[xX][aA]0;|<[^>]*>)+(?<second>{Word(parts[1])})(?![\p{{L}}\p{{N}}])";
			var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			return match.Success ? (match.Groups["first"], match.Groups["second"]) : null;

			static string Word(string word) => string.Concat(word.Select(HyphenSpacing.Letter));
		}

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);
	}
}
