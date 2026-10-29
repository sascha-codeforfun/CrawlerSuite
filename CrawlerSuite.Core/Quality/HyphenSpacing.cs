namespace CrawlerSuite.Core.Quality
{
	using System.Text.RegularExpressions;
	using HtmlAgilityPack;

	/// <summary>
	/// A space on the wrong side of a hyphen inside a word: "Xyzzy- Haus" (after) or
	/// "Xyzzy -Haus" (before), where a compound was meant ("Xyzzy-Haus"). The spell check
	/// cannot see this — it reads "Xyzzy-" as a suspended head and "-Haus" as a suspended
	/// tail, both correct German forms.
	///
	/// The legitimate forms it must leave alone:
	///   * a suspended HEAD before a joining word or punctuation — "Haus- und Gartenpflege",
	///     "Haus-, Hof- und …", "Ein-/Ausgang";
	///   * a suspended TAIL after a joining word or punctuation — "Hauseingänge / -ausgänge",
	///     "Textilgroß- und -einzelhandel";
	///   * a dash used as punctuation — a hyphen with space on both sides ("Text - mehr")
	///     or none ("Haus-Garten") is not this finding.
	/// The joining words are a list per language in the configuration
	/// (<see cref="ContentQualityConfig.HyphenJoiningWords"/>); the page's language decides. When
	/// they do not cover a spot, the proper names of the configuration are asked
	/// (<see cref="ProperNames"/>): an institution's name ("Xyzzy- und Hausverband") applies in
	/// every language version. Soft hyphens are invisible and read as nothing.
	///
	/// SCOPE — ONE TEXT NODE AT A TIME, as <see cref="NoBreakChars"/>: a hyphen at the end of
	/// a text node is not judged by what the next node holds. Code, preformatted text and
	/// scripts are skipped.
	/// </summary>
	/// <summary>
	/// A name offered by a hyphen-spacing finding: <see cref="Core"/> the two words at the hyphen,
	/// <see cref="Extra"/> the word next to them when the finding has one (after them, or before
	/// them when <see cref="ExtraFirst"/>). The operator chooses whether the extra word belongs to it.
	/// </summary>
	public sealed record HyphenNameCandidate(string Core, string? Extra, bool ExtraFirst)
	{
		public string With(bool extra) =>
			!extra || Extra is null ? Core : ExtraFirst ? $"{Extra} {Core}" : $"{Core} {Extra}";
	}

	public static class HyphenSpacing
	{
		internal const string AfterIssueType = "SPACE_AFTER_HYPHEN";
		internal const string BeforeIssueType = "SPACE_BEFORE_HYPHEN";

		private static readonly HashSet<string> SkippedParents = new(StringComparer.OrdinalIgnoreCase)
		{
			"script", "style", "noscript", "code", "pre", "kbd", "samp", "textarea",
		};

		internal static IEnumerable<QualityIssue> Check(
			string filename, HtmlDocument doc, ContentQualityConfig config,
			IReadOnlyDictionary<string, List<string>>? pageLanguageOverrides = null,
			string defaultLanguage = "",
			ProperNames? names = null,
			Func<string, string, bool?>? wordCheck = null)
		{
			var url = pageLanguageOverrides is { Count: > 0 } ? CrawlIndex.LookUpUrlForFile(filename) : string.Empty;
			var pageLanguages = CrawlerSuite.Core.Html.PageLanguageSet.Resolve(url, doc, pageLanguageOverrides, defaultLanguage);
			var pageFallback = pageLanguages.Count == 1 ? pageLanguages[0] : string.Empty;

			foreach (var node in doc.DocumentNode.Descendants())
			{
				if (node.NodeType != HtmlNodeType.Text || InSkippedElement(node))
				{
					continue;
				}

				var text = HtmlEntity.DeEntitize(node.InnerText) ?? string.Empty;
				if (text.IndexOf('-') < 0 && text.IndexOf('‐') < 0)
				{
					continue;
				}

				// The element's own lang wins over the page's; a page declared in several
				// languages takes the joining words of all of them.
				var island = CrawlerSuite.Core.Html.Language.NearestElementLanguage(node.ParentNode, pageFallback);
				IEnumerable<string> languages = island.Length > 0 && !string.Equals(island, pageFallback, StringComparison.OrdinalIgnoreCase)
					? [island]
					: pageLanguages;

				var asked = languages.ToList();
				Func<string, bool>? accepts = wordCheck is null ? null : WordsIn(asked, wordCheck);
				foreach (var issue in Scan(filename, text, node.ParentNode?.Name ?? "text", Joiners(config, asked), config, names, accepts))
				{
					// The excerpt is the page's HTML around the spot, as written: the plain text of one
					// node hides the markup and the words around it that tell what was meant.
					yield return RawExcerpt(BlockOf(node), issue, config) is { } raw ? issue with { Context = raw } : issue;
				}
			}
		}

		/// <summary>
		/// "Is this a word?" in the text's languages; null when none of them has a dictionary — then the
		/// check cannot tell a left-out joiner from a stray space and names both.
		/// </summary>
		private static Func<string, bool>? WordsIn(IReadOnlyList<string> languages, Func<string, string, bool?> wordCheck)
		{
			var known = languages.Where(l => wordCheck("a", l) is not null).ToList();
			return known.Count == 0 ? null : word => known.Any(l => wordCheck(word, l) == true);
		}

		private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
		{
			"p", "li", "td", "th", "dt", "dd", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "figcaption",
			"caption", "label", "legend", "title", "option", "button", "div", "section", "article", "header",
			"footer", "main", "aside", "nav", "summary", "body",
		};

		/// <summary>The block the text node stands in (a list item, a paragraph, a cell …); its parent when there is none.</summary>
		internal static HtmlNode? BlockOf(HtmlNode node)
		{
			for (var n = node.ParentNode; n is not null && n.NodeType == HtmlNodeType.Element; n = n.ParentNode)
			{
				if (Blocks.Contains(n.Name))
				{
					return n;
				}
			}

			return node.ParentNode;
		}

		/// <summary>The block's HTML around the spot on one line; null when the spot cannot be found in it.</summary>
		private static string? RawExcerpt(HtmlNode? block, QualityIssue issue, ContentQualityConfig config)
		{
			if (block is null || Spot(block.OuterHtml, issue.IssueType, issue.Detail) is not { } spot)
			{
				return null;
			}

			var html = block.OuterHtml;
			var excerpt = Excerpt.Centred(html, spot.Gap.Index, Math.Max(config.ContentQualityExcerptRadius * 2, 80));
			return LogExcerpt.Truncate(WhitespaceRun.Replace(excerpt, " "), config.ContentQualityMaxExcerpt);
		}

		/// <summary>
		/// One letter as the source may write it: itself, or — beyond ASCII — a character reference
		/// ("ü" as "&amp;uuml;", "&amp;#252;" or "&amp;#xFC;").
		/// </summary>
		internal static string Letter(char c)
		{
			var plain = Regex.Escape(c.ToString());
			return c < 128 ? plain : $"(?:{plain}|&#{(int)c};|&#[xX]0*(?i:{(int)c:X});|&[A-Za-z][A-Za-z0-9]*;)";
		}

		// A soft hyphen as the source may write it: the character or one of its entities.
		private const string SoftHyphen = @"(?:\u00AD|&shy;|&#173;|&#[xX][aA][dD];)?";

		/// <summary>
		/// Where a finding's spot is in <paramref name="html"/>: the word at the hyphen (the head
		/// before it, or the tail after it), soft hyphens allowed between its letters, and the gap —
		/// the hyphen with its space. Null for other findings or when the text is not there.
		/// </summary>
		internal static (Group Word, Group Gap)? Spot(string html, string issueType, string detail)
		{
			bool after = issueType == AfterIssueType;
			int open = detail.LastIndexOf('“');
			int close = detail.LastIndexOf('”');
			if ((!after && issueType != BeforeIssueType) || open < 0 || close <= open)
			{
				return null;
			}

			var phrase = detail[(open + 1)..close];
			bool slash = phrase.Contains('/');
			var parts = phrase.Split([' ', '/'], StringSplitOptions.RemoveEmptyEntries);
			var word = after ? parts[0].TrimEnd('-', '‐') : parts[^1].TrimStart('-', '‐');
			if (word.Length == 0)
			{
				return null;
			}

			const string Space = @"(?:\s|&nbsp;|&#160;)";
			var letters = string.Join(SoftHyphen, word.Select(Letter));
			var gap = slash
				? (after ? $@"[-‐]{Space}*/{Space}*" : $@"{Space}*/{Space}*[-‐]")
				: (after ? $@"[-‐]{Space}+" : $@"{Space}+[-‐]");
			var pattern = after
				? $@"(?<![\p{{L}}\p{{N}}])(?<word>{letters})(?<gap>{gap})"
				: $@"(?<gap>{gap})(?<word>{letters})(?![\p{{L}}\p{{N}}])";
			var match = Regex.Match(html, pattern, RegexOptions.CultureInvariant);
			return match.Success ? (match.Groups["word"], match.Groups["gap"]) : null;
		}

		/// <summary>The joining words of the given languages (base codes, "de-DE" → "de"), lower case, without a final dot.</summary>
		internal static HashSet<string> Joiners(ContentQualityConfig config, IEnumerable<string> languages)
		{
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var language in languages)
			{
				var code = language.Split('-')[0];
				foreach (var (key, words) in config.HyphenJoiningWords)
				{
					if (string.Equals(key.Split('-')[0], code, StringComparison.OrdinalIgnoreCase))
					{
						foreach (var word in words)
						{
							set.Add(Normalise(word));
						}
					}
				}
			}

			return set;
		}

		/// <summary>
		/// The scanner, for tests and callers holding decoded text. <paramref name="accepts"/> asks the
		/// dictionaries of the text's languages; null when there are none.
		/// </summary>
		internal static IEnumerable<QualityIssue> Scan(
			string filename, string text, string source, IReadOnlySet<string> joiners, ContentQualityConfig config,
			ProperNames? names = null, Func<string, bool>? accepts = null)
		{
			// HTML shows any run of white space as one space; a line break after the hyphen
			// in the source is a space on the page. A soft hyphen shows as nothing
			// ("beziehungs\u00ADweise" reads "beziehungsweise"), so it is taken out first.
			var collapsed = WhitespaceRun.Replace(text.Replace("\u00AD", string.Empty, StringComparison.Ordinal), " ");
			names ??= ProperNames.None;
			for (int i = 0; i < collapsed.Length; i++)
			{
				if (!IsHyphen(collapsed[i]))
				{
					continue;
				}

				bool wordBefore = i > 0 && char.IsLetterOrDigit(collapsed[i - 1]);
				bool spaceAfter = i + 1 < collapsed.Length && char.IsWhiteSpace(collapsed[i + 1]);
				bool spaceBefore = i > 0 && char.IsWhiteSpace(collapsed[i - 1]);
				bool wordAfter = i + 1 < collapsed.Length && char.IsLetter(collapsed[i + 1]);

				// "Xyzzy- / Haus", "Xyzzy-/ Haus": a slash after a suspended head is written without spaces ("Xyzzy-/Haus").
				if (wordBefore && SlashAfter(collapsed, i) is { } slashed)
				{
					yield return new QualityIssue(
						filename,
						AfterIssueType,
						$"{source}: space at the slash in “{WordBefore(collapsed, i)}{slashed}”",
						Context(collapsed, i, config));
					continue;
				}

				// "Xyzzy / -haus", "Xyzzy/ -haus": the same before a suspended tail ("Xyzzy/-haus").
				if (wordAfter && SlashBefore(collapsed, i) is { } before)
				{
					yield return new QualityIssue(
						filename,
						BeforeIssueType,
						$"{source}: space at the slash in “{before}{WordAfter(collapsed, i)}”",
						Context(collapsed, i, config));
					continue;
				}

				// "Xyzzy- Haus": the head is followed by a word that is not a joining word.
				if (wordBefore && spaceAfter && NextWord(collapsed, i + 1) is { } next && !joiners.Contains(Normalise(next))
					&& !IsNameAfter(collapsed, i, next, names))
				{
					var head = WordBefore(collapsed, i);
					var what = head.All(char.IsDigit) ? "stray space after the hyphen"
						: accepts is not null && BorrowsTail(head, next, accepts) ? "“/” or a joining word missing after the hyphen"
						: "space after the hyphen";
					yield return new QualityIssue(
						filename,
						AfterIssueType,
						$"{source}: {what} in “{head}- {next}{Spaced(FollowingWord(collapsed, i, next))}”",
						Context(collapsed, i, config));
					continue;
				}

				// "Xyzzy -Haus": the tail follows a word that is not a joining word.
				if (spaceBefore && wordAfter && PreviousWord(collapsed, i - 1) is { } previous && !joiners.Contains(Normalise(previous))
					&& !IsNameBefore(collapsed, i, previous, names))
				{
					var tail = WordAfter(collapsed, i);
					var what = accepts is not null && BorrowsHead(previous, tail, accepts)
						? "“/” or a joining word missing before the hyphen"
						: "space before the hyphen";
					yield return new QualityIssue(
						filename,
						BeforeIssueType,
						$"{source}: {what} in “{Spaced(EarlierWord(collapsed, i, previous), before: true)}{previous} -{tail}”",
						Context(collapsed, i, config));
				}
			}
		}

		// A borrowed part is at least this long: shorter ends ("aus", "en") make words by chance.
		private const int MinBorrowed = 4;

		/// <summary>
		/// "Stadt- Landhaus": the head takes the end of the next word and makes a word ("Stadthaus") —
		/// a suspended head whose joiner was left out, not a split compound.
		/// </summary>
		private static bool BorrowsTail(string head, string next, Func<string, bool> accepts)
		{
			if (!head.All(char.IsLetter))
			{
				return false;
			}

			for (int start = 1; start + MinBorrowed <= next.Length; start++)
			{
				var end = next[start..];
				if (end.All(char.IsLetter) && accepts(head + end))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>"Hauseingänge -ausgänge": the start of the word before plus the tail makes a word ("Hausausgänge").</summary>
		private static bool BorrowsHead(string previous, string tail, Func<string, bool> accepts)
		{
			if (!tail.All(char.IsLetter))
			{
				return false;
			}

			for (int length = MinBorrowed; length < previous.Length; length++)
			{
				var start = previous[..length];
				if (start.All(char.IsLetter) && accepts(start + tail))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// After the hyphen at <paramref name="hyphen"/>: a slash with a space on either side and a word
		/// after it — the part from the hyphen to that word ("- / Haus"); null otherwise.
		/// </summary>
		private static string? SlashAfter(string text, int hyphen)
		{
			int at = hyphen + 1;
			while (at < text.Length && text[at] == ' ')
			{
				at++;
			}

			if (at >= text.Length || text[at] != '/')
			{
				return null;
			}

			int slash = at++;
			while (at < text.Length && text[at] == ' ')
			{
				at++;
			}

			bool spaced = slash > hyphen + 1 || at > slash + 1;
			if (!spaced || at >= text.Length || !char.IsLetterOrDigit(text[at]))
			{
				return null;
			}

			return text[hyphen..at] + (NextWord(text, at) ?? string.Empty);
		}

		/// <summary>
		/// Before the hyphen at <paramref name="hyphen"/>: a word, a slash with a space on either side —
		/// the part from that word to the hyphen ("Haus / -"); null otherwise.
		/// </summary>
		private static string? SlashBefore(string text, int hyphen)
		{
			int at = hyphen - 1;
			while (at >= 0 && text[at] == ' ')
			{
				at--;
			}

			if (at < 0 || text[at] != '/')
			{
				return null;
			}

			int slash = at--;
			while (at >= 0 && text[at] == ' ')
			{
				at--;
			}

			bool spaced = slash < hyphen - 1 || at < slash - 1;
			if (!spaced || at < 0 || !char.IsLetterOrDigit(text[at]))
			{
				return null;
			}

			return PreviousWord(text, at) is { } word ? word + text[(at + 1)..(hyphen + 1)] : null;
		}

		/// <summary>
		/// "Xyzzy- und Hausverband" kept as a proper name: the head with its hyphen and the next word
		/// belong to a listed name, and that whole name stands in this text.
		/// </summary>
		private static bool IsNameAfter(string text, int hyphen, string next, ProperNames names) =>
			names.Accepts($"{WordBefore(text, hyphen)}- {next}", text);

		/// <summary>The word after <paramref name="next"/> (the word after the hyphen's space); null when punctuation or nothing follows.</summary>
		private static string? FollowingWord(string text, int hyphen, string next)
		{
			int after = text.IndexOf(next, hyphen + 1, StringComparison.Ordinal) + next.Length;
			return NextWord(text, after);
		}

		/// <summary>The word before <paramref name="previous"/> (the word before the tail's space); null when punctuation or the start stands there.</summary>
		private static string? EarlierWord(string text, int hyphen, string previous)
		{
			int start = text.LastIndexOf(previous, hyphen, StringComparison.Ordinal);
			return start > 0 ? PreviousWord(text, start - 1) : null;
		}

		private static string Spaced(string? word, bool before = false) =>
			word is null ? string.Empty : before ? word + " " : " " + word;

		/// <summary>
		/// The name a finding offers to keep in a dictionary, read from its text: the two words at the
		/// hyphen ("Xyzzy- und") and, when the text has it, the word after them ("Hausverband") — or
		/// before them for a space before the hyphen. Null for other findings.
		/// </summary>
		public static HyphenNameCandidate? NameFrom(string issueType, string detail)
		{
			bool after = issueType == AfterIssueType;
			if (!after && issueType != BeforeIssueType)
			{
				return null;
			}

			int open = detail.LastIndexOf('“');
			int close = detail.LastIndexOf('”');
			if (open < 0 || close <= open)
			{
				return null;
			}

			var words = detail[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (words.Any(w => w.Contains('/')))
			{
				return null; // a space at a slash: the fix is plain, no name to keep
			}

			return words.Length switch
			{
				2 => new HyphenNameCandidate($"{words[0]} {words[1]}", null, false),
				3 when after => new HyphenNameCandidate($"{words[0]} {words[1]}", words[2], false),
				3 => new HyphenNameCandidate($"{words[1]} {words[2]}", words[0], true),
				_ => null,
			};
		}

		/// <summary>"Hausverband und -xyzzy" kept as a proper name: the word before and the tail belong to a listed name that stands whole in this text.</summary>
		private static bool IsNameBefore(string text, int hyphen, string previous, ProperNames names) =>
			names.Accepts($"{previous} -{WordAfter(text, hyphen)}", text);

		/// <summary>
		/// The word after the space(s) following position <paramref name="from"/>; null when what
		/// comes next is not a word (punctuation — "/", ",", "&", a bracket — or nothing), which
		/// is a legitimate suspended head.
		/// </summary>
		private static string? NextWord(string text, int from)
		{
			int start = from;
			while (start < text.Length && char.IsWhiteSpace(text[start]))
			{
				start++;
			}

			if (start >= text.Length || !char.IsLetterOrDigit(text[start]))
			{
				return null;
			}

			int end = start;
			while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '.' || IsHyphen(text[end])))
			{
				end++;
			}

			return text[start..end].TrimEnd('.');
		}

		/// <summary>
		/// The word before the space(s) ending at position <paramref name="from"/>; null when what
		/// stands there is not a word (punctuation, or the start of the text), which is a
		/// legitimate suspended tail.
		/// </summary>
		private static string? PreviousWord(string text, int from)
		{
			int end = from;
			while (end >= 0 && char.IsWhiteSpace(text[end]))
			{
				end--;
			}

			if (end < 0)
			{
				return null;
			}

			// "bzw. -abfahrt": a dot that ends an abbreviation belongs to the word.
			int stop = end;
			if (text[stop] == '.' && stop > 0 && char.IsLetter(text[stop - 1]))
			{
				stop--;
			}

			if (!char.IsLetterOrDigit(text[stop]))
			{
				return null;
			}

			int start = stop;
			while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || IsHyphen(text[start - 1])))
			{
				start--;
			}

			return text[start..(stop + 1)];
		}

		private static string WordBefore(string text, int hyphen)
		{
			int start = hyphen;
			while (start > 0 && char.IsLetterOrDigit(text[start - 1]))
			{
				start--;
			}

			return text[start..hyphen];
		}

		private static string WordAfter(string text, int hyphen)
		{
			int end = hyphen + 1;
			while (end < text.Length && char.IsLetterOrDigit(text[end]))
			{
				end++;
			}

			return text[(hyphen + 1)..end];
		}

		private static string Context(string text, int at, ContentQualityConfig config) =>
			LogExcerpt.Truncate(Excerpt.Around(text, at, config.ContentQualityExcerptRadius), config.ContentQualityMaxExcerpt);

		private static string Normalise(string word) => word.Trim().TrimEnd('.').ToLowerInvariant();

		private static bool IsHyphen(char c) => c is '-' or '‐';

		internal static bool InSkippedElement(HtmlNode node)
		{
			for (var current = node.ParentNode; current is not null && current.NodeType != HtmlNodeType.Document; current = current.ParentNode)
			{
				if (SkippedParents.Contains(current.Name))
				{
					return true;
				}
			}

			return false;
		}

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);
	}
}
