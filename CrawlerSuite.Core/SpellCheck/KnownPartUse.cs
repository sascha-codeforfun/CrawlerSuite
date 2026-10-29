namespace CrawlerSuite.Core.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using CrawlerSuite.Core.Lexicon;
	using CrawlerSuite.Core.Settings;

	/// <summary>
	/// Which Known word parts let a word pass on one page: per entry (as configured), the words it
	/// covered and the languages the text was checked in. A word counts only when the check
	/// accepted it — a part of a word that stayed flagged is not counted. One per page, one thread.
	/// </summary>
	public sealed class KnownPartCounter
	{
		private readonly Dictionary<string, Dictionary<(string Word, int Check), HashSet<string>>> covered = new(StringComparer.Ordinal);
		private readonly List<HashSet<string>> checks = [[]];

		/// <summary>
		/// A text is checked in the next language. The languages one text is checked in together
		/// (a page's language set) form one check: a word fails only when all of them reject it.
		/// A new text, or a language the current check already had, starts the next one.
		/// </summary>
		public void BeginCheck(string language, bool sameText)
		{
			if (!sameText || checks[^1].Contains(language))
			{
				checks.Add([]);
			}

			checks[^1].Add(language);
		}

		/// <summary>
		/// Per entry, the distinct words it helped pass on this page, each with the languages its
		/// text was checked in together ("de,en", sorted).
		/// </summary>
		public IReadOnlyDictionary<string, IReadOnlyCollection<(string Word, string Languages)>> Covered =>
			covered.ToDictionary(
				c => c.Key,
				c => (IReadOnlyCollection<(string Word, string Languages)>)c.Value
					.Select(w => (w.Key.Word, string.Join(",", w.Value.OrderBy(l => l, StringComparer.Ordinal))))
					.ToHashSet(),
				StringComparer.Ordinal);

		/// <summary>Notes that <paramref name="entry"/> covered part of <paramref name="word"/>, which passed.</summary>
		public void Add(string entry, string word, string language)
		{
			if (!covered.TryGetValue(entry, out var words))
			{
				words = [];
				covered[entry] = words;
			}

			int check = checks.Count - 1;
			checks[check].Add(language);
			words[(word, check)] = checks[check];
		}

		/// <summary>Notes every entry that covered a span of an accepted word (spans as written on the page).</summary>
		internal void AddSpans(IEnumerable<string> spans, string word, string language, IReadOnlyList<string> knownParts)
		{
			foreach (var span in spans)
			{
				if (KnownParts.EntryOf(span, knownParts) is { } entry)
				{
					Add(entry, word, language);
				}
			}
		}
	}

	/// <summary>
	/// Known word parts across a run: how much each entry was needed (settings-usage.json, shown
	/// on the settings screen), which entries the words they covered would pass without, which
	/// words still need an entry, and what a switched-off entry would do.
	/// </summary>
	public sealed class KnownPartTally
	{
		/// <summary>The settings-usage key of the counts: [pages, words covered].</summary>
		public const string Setting = "SpellCheckWordPrefixesToStrip";

		/// <summary>The settings-usage key of the entries whose words would all pass without them.</summary>
		public const string NotNeededSetting = "SpellCheckWordPrefixesToStrip.NotNeeded";

		/// <summary>The settings-usage key of switched-off entries: [pages, flagged words they would let pass].</summary>
		public const string SwitchedOffSetting = "SpellCheckWordPrefixesToStrip.Off";

		/// <summary>How many words that need an entry are kept as examples.</summary>
		public const int Examples = 3;

		private readonly IReadOnlyList<string> entries;
		private readonly Dictionary<string, int[]> counts = new(StringComparer.Ordinal);
		private readonly Dictionary<string, HashSet<(string Word, string Languages)>> words = new(StringComparer.Ordinal);

		/// <param name="knownParts">The active entries (switched-off ones are judged apart, <see cref="WouldLetPass"/>).</param>
		public KnownPartTally(IReadOnlyList<string> knownParts)
		{
			entries = knownParts.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
			foreach (var entry in entries)
			{
				counts[entry] = new int[2];
				words[entry] = [];
			}
		}

		/// <summary>Adds one page's counter: one page per entry it holds, its distinct words.</summary>
		public void Add(KnownPartCounter? page)
		{
			if (page is null)
			{
				return;
			}

			foreach (var (entry, covered) in page.Covered)
			{
				if (!counts.TryGetValue(entry, out var cell))
				{
					continue;
				}

				cell[0]++;
				cell[1] += covered.Select(w => w.Word).Distinct(StringComparer.Ordinal).Count();
				words[entry].UnionWith(covered);
			}
		}

		/// <summary>Every entry with its [pages, words covered]; an entry that covered nothing has [0, 0].</summary>
		public IReadOnlyDictionary<string, UsageCount> Counts() =>
			entries.ToDictionary(e => e, e => new UsageCount(counts[e][0], counts[e][1]), StringComparer.Ordinal);

		/// <summary>
		/// The entries that covered words, every one of which would pass without the entry — in at
		/// least one of the languages its text was checked in, as the check itself decides: its
		/// parts are single capitals, acronyms, dictionary words or English parts, or another entry
		/// covers them. And, for every other entry that covered words, up to <see cref="Examples"/>
		/// of the words that need it ("word (de, en)"). Asks the dictionaries without recording
		/// usage. A word that cannot be judged the way the check judged it counts as needing the
		/// entry, so an entry is never wrongly called not needed.
		/// </summary>
		public (IReadOnlyDictionary<string, UsageCount> NotNeeded, IReadOnlyDictionary<string, IReadOnlyList<string>> NeededFor) Judge(
			IReadOnlyDictionary<string, Bundle> bundles,
			IReadOnlyList<string> fugenelemente,
			AcronymRule? acronyms)
		{
			var english = ToolsSpellChecker.EnglishOf(bundles);
			var notNeeded = new Dictionary<string, UsageCount>(StringComparer.Ordinal);
			var neededFor = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
			foreach (var entry in entries)
			{
				var covered = words[entry];
				if (covered.Count == 0)
				{
					continue;
				}

				var without = entries.Where(e => !ReferenceEquals(e, entry)).ToList();
				var needing = covered
					.Where(w => !w.Languages.Split(',').Any(l => KnownParts.WouldPass(w.Word, l, bundles, english, without, fugenelemente, acronyms)))
					.Select(w => $"{w.Word} ({w.Languages.Replace(",", ", ")})")
					.Distinct(StringComparer.Ordinal)
					.Order(StringComparer.Ordinal)
					.ToList();
				if (needing.Count == 0)
				{
					notNeeded[entry] = new UsageCount(counts[entry][0], counts[entry][1]);
				}
				else
				{
					neededFor[entry] = needing.Take(Examples).ToList();
				}
			}

			return (notNeeded, neededFor);
		}

		/// <summary>
		/// What switched-off entries would do: for each, the run's flagged words (log 11) it would let
		/// pass with the active entries — [pages they are on, words]. Every switched-off entry is
		/// listed, [0, 0] when it would let none pass. Asks the dictionaries without recording usage.
		/// </summary>
		public IReadOnlyDictionary<string, UsageCount> WouldLetPass(
			IEnumerable<string> switchedOff,
			IReadOnlyList<CrawlerSuite.Core.Lexicon.SpellWord> findings,
			IReadOnlyDictionary<string, Bundle> bundles,
			IReadOnlyList<string> fugenelemente,
			AcronymRule? acronyms)
		{
			var english = ToolsSpellChecker.EnglishOf(bundles);
			var result = new Dictionary<string, UsageCount>(StringComparer.Ordinal);
			foreach (var entry in switchedOff.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.Ordinal))
			{
				var with = entries.Append(entry).ToList();
				int pages = 0;
				int count = 0;
				foreach (var finding in findings)
				{
					if (finding.Languages.Any(l => KnownParts.WouldPass(finding.Word, l, bundles, english, with, fugenelemente, acronyms)))
					{
						count++;
						pages += finding.Pages;
					}
				}

				result[entry] = new UsageCount(pages, count);
			}

			return result;
		}
	}

	/// <summary>Known word parts: how a word is covered by them, and whether it passes without one.</summary>
	public static class KnownParts
	{
		/// <summary>The configured entry a span of a word stands for (any case); null when none.</summary>
		public static string? EntryOf(string span, IReadOnlyList<string> knownParts)
		{
			foreach (var entry in knownParts)
			{
				if (entry is not null && entry.Length == span.Length && string.Equals(entry, span, StringComparison.OrdinalIgnoreCase))
				{
					return entry;
				}
			}

			return null;
		}

		/// <summary>
		/// The spans of a word made only of known word parts ("Brand-Line-Xyzzy", or one entry
		/// alone), longest entry first as the check takes them; null when some part is not an entry.
		/// </summary>
		public static IReadOnlyList<string>? Cover(string word, IReadOnlyList<string> knownParts)
		{
			var spans = new List<string>();
			bool all = SpellChecker.WalkHyphenParts(
				word,
				knownParts,
				(_, _) => HyphenPartStatus.Unknown,
				(span, status) =>
				{
					if (status == HyphenPartStatus.KnownWordPart)
					{
						spans.Add(span);
					}
				},
				stopAtUnknown: true,
				singlePart: true);
			return all ? spans : null;
		}

		/// <summary>
		/// Whether <paramref name="word"/>, checked in <paramref name="language"/>, passes with the
		/// Known word parts <paramref name="knownParts"/> as an ordinary word: made of entries, an
		/// acronym, a dictionary word, or a hyphenated word whose parts all pass. Records nothing.
		/// </summary>
		public static bool WouldPass(
			string word,
			string language,
			IReadOnlyDictionary<string, Bundle> bundles,
			Bundle? english,
			IReadOnlyList<string> knownParts,
			IReadOnlyList<string> fugenelemente,
			AcronymRule? acronyms)
		{
			if (WordPrefix.Strip(word, knownParts) is null || (acronyms is not null && acronyms.Matches(word)))
			{
				return true;
			}

			if (!bundles.TryGetValue(language, out var bundle))
			{
				return false;
			}

			if (bundle.AcceptedBy(word) != Bundle.Tier.None)
			{
				return true;
			}

			return HyphenParts.Explain(word, bundle, english, knownParts, fugenelemente, acronyms) is { } parts
				&& parts.All(p => p.Status != HyphenPartStatus.Unknown);
		}
	}
}
