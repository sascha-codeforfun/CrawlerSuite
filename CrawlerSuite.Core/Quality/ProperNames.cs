namespace CrawlerSuite.Core.Quality
{
	/// <summary>
	/// The proper names of <see cref="Config.ProperNames"/>, as the checks ask them. The hyphen-spacing
	/// check asks whether a phrase at a hyphen is (part of) a name written that way on purpose; the
	/// spell check asks whether a word it would flag is an acronym of the list, or a word of a name
	/// that stands as a whole in the same text. Case, line breaks and double spaces do not matter,
	/// and the hyphen may be written as - or ‐.
	/// </summary>
	public sealed class ProperNames
	{
		private readonly List<(string Key, HashSet<string> Words)> names = [];
		private readonly HashSet<string> acronyms = new(StringComparer.Ordinal);

		public ProperNames(IEnumerable<ProperName>? entries)
		{
			foreach (var entry in entries ?? [])
			{
				if (entry is null)
				{
					continue;
				}

				if (!string.IsNullOrWhiteSpace(entry.Name))
				{
					names.Add((Key(entry.Name), Words(entry.Name).ToHashSet(StringComparer.Ordinal)));
				}

				if (!string.IsNullOrWhiteSpace(entry.Acronym))
				{
					acronyms.Add(entry.Acronym.Trim());
				}
			}
		}

		/// <summary>No names.</summary>
		public static ProperNames None { get; } = new([]);

		/// <summary>The names of a configuration.</summary>
		public static ProperNames From(Config config) => new(config.ProperNames);

		/// <summary>True when the list holds nothing to ask.</summary>
		public bool IsEmpty => names.Count == 0 && acronyms.Count == 0;

		/// <summary>
		/// True when <paramref name="phrase"/> is part of a listed name (whole words) and that whole
		/// name stands in <paramref name="text"/>: a phrase that merely starts like a name elsewhere does not count.
		/// </summary>
		public bool Accepts(string phrase, string text)
		{
			var key = Key(phrase);
			if (key.Length == 0)
			{
				return false;
			}

			string? keyedText = null;
			return names.Any(n => ContainsWords(n.Key, key) && ContainsWords(keyedText ??= Key(text), n.Key));
		}

		/// <summary>True when a name of the list is <paramref name="phrase"/> itself (as the checks read it).</summary>
		public bool Has(string phrase)
		{
			var key = Key(phrase);
			return names.Any(n => n.Key == key);
		}

		/// <summary>
		/// True when <paramref name="word"/> is an acronym of the list (exact capitals), or every
		/// part of it is a word of a name that stands as a whole in <paramref name="keyedText"/>
		/// (the text through <see cref="Key"/>).
		/// </summary>
		public bool Covers(string word, Func<string> keyedText)
		{
			if (acronyms.Contains(word))
			{
				return true;
			}

			var parts = Words(word);
			if (parts.Count == 0)
			{
				return false;
			}

			string? text = null;
			foreach (var (key, words) in names)
			{
				if (parts.All(words.Contains) && ContainsWords(text ??= keyedText(), key))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>One space between words, lower case, one kind of hyphen, no soft hyphens: how names and texts are compared.</summary>
		public static string Key(string text) =>
			string.Join(' ', text.Replace("\u00AD", string.Empty, StringComparison.Ordinal).Replace('\u2010', '-').Replace('\u2011', '-')
				.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

		/// <summary>The words of a name or token: runs of letters and digits, lower case.</summary>
		private static List<string> Words(string text)
		{
			var words = new List<string>();
			int start = -1;
			for (int i = 0; i <= text.Length; i++)
			{
				bool inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
				if (inWord && start < 0)
				{
					start = i;
				}
				else if (!inWord && start >= 0)
				{
					words.Add(text[start..i].ToLowerInvariant());
					start = -1;
				}
			}

			return words;
		}

		// "needle" stands in "haystack" with no letter or digit glued to either end.
		private static bool ContainsWords(string haystack, string needle)
		{
			for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
				at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal))
			{
				int end = at + needle.Length;
				bool startOk = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]) || !char.IsLetterOrDigit(needle[0]);
				bool endOk = end == haystack.Length || !char.IsLetterOrDigit(haystack[end]) || !char.IsLetterOrDigit(needle[^1]);
				if (startOk && endOk)
				{
					return true;
				}
			}

			return false;
		}
	}
}
