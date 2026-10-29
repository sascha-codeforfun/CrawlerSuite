namespace CrawlerSuite.Core.Lexicon
{
	using System.Text.RegularExpressions;
	using CrawlerSuite.Core.Findings;

	/// <summary>One unique misspelling of a snapshot: the word, its page languages and on how many pages it is.</summary>
	public sealed record SpellWord(string Word, IReadOnlyList<string> Languages, int Pages)
	{
		/// <summary>Other reference dictionaries that know the word (information only; it stays flagged).</summary>
		public IReadOnlyList<string> ValidIn { get; init; } = [];
	}

	/// <summary>
	/// The unique misspellings of a snapshot's page text (log 11), with the number of pages each
	/// is on (log 12), for the Spell findings page. Words found in scripts are not included.
	/// </summary>
	public static class SpellWordList
	{
		// "word (de, en)" in log 11, perhaps followed by " · valid en-GB"; the word may itself hold spaces or brackets.
		private static readonly Regex WordLangs = new(@"^(?<word>.*) \((?<langs>[^()]*)\)$", RegexOptions.Compiled);

		// "url|word (de) (meta…)" in log 12.
		private static readonly Regex WordLangsMeta = new(@"^(?<word>.*?) \((?<langs>[^()]*)\)(?: \((?<meta>.*)\))?$", RegexOptions.Compiled);

		/// <summary>Whether the snapshot has a list of unique misspellings (the spell check ran).</summary>
		public static bool Exists(string snapshot) => File.Exists(Path.Combine(snapshot, LogFileNames.SpellErrorsUnique));

		/// <summary>The words of log 11 in its order; empty when the snapshot has none.</summary>
		public static IReadOnlyList<SpellWord> Read(string snapshot)
		{
			var unique = Path.Combine(snapshot, LogFileNames.SpellErrorsUnique);
			var sources = Path.Combine(snapshot, LogFileNames.SpellErrorsSources);
			if (!File.Exists(unique))
			{
				return [];
			}

			var pages = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
			if (File.Exists(sources))
			{
				foreach (var line in FindingFiles.Lines(sources))
				{
					int bar = line.IndexOf('|');
					if (bar < 0)
					{
						continue;
					}

					var m = WordLangsMeta.Match(line[(bar + 1)..]);
					var word = m.Success ? m.Groups["word"].Value : line[(bar + 1)..].Trim();
					if (!pages.TryGetValue(word, out var urls))
					{
						urls = new HashSet<string>(StringComparer.Ordinal);
						pages[word] = urls;
					}

					urls.Add(line[..bar]);
				}
			}

			var words = new List<SpellWord>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var value in FindingFiles.Values(unique))
			{
				var (line, validIn) = SpellCheck.ParallelStoreWriter.SplitValidIn(value[0]);
				var m = WordLangs.Match(line);
				var word = m.Success ? m.Groups["word"].Value : line;
				if (word.Length == 0 || !seen.Add(word))
				{
					continue;
				}

				var langs = m.Success
					? m.Groups["langs"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
					: [];
				words.Add(new SpellWord(word, langs, pages.TryGetValue(word, out var on) ? on.Count : 0) { ValidIn = validIn });
			}

			return words;
		}
	}
}
