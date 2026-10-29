namespace CrawlerSuite.Core.Lexicon
{
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using WeCantSpell.Hunspell;

	public class Bundle
	{
		public WordList System { get; set; } = null!;
		public HashSet<string> SharedSite { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		public HashSet<string> SharedUser { get; set; } = new(StringComparer.OrdinalIgnoreCase);

		// Foreign-language allow-list (user_foreign_languages.dic). A researched,
		// comment-justified archive of legitimate non-Latin-script / diacritic words that
		// the strict user/site policy rejects. CONSULT-ONLY: words here are accepted by
		// Check but are deliberately exempt from ALL maintenance — never recorded in
		// UsageTracker, never passed to orphan/prune analysis, never written from triage.
		// A legitimately rare foreign word must not be nagged for removal, which is why it
		// lives outside the cross-off machinery entirely.
		public HashSet<string> SharedForeign { get; set; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Whether this language's own dictionary knows the word — the system dictionary alone, no
		/// user, site or foreign list, nothing recorded. For telling that a flagged word is valid in
		/// another language or flavour.
		/// </summary>
		public bool KnowsInSystem(string word)
		{
			if (string.IsNullOrWhiteSpace(word) || System == null)
			{
				return false;
			}

			if (!word.IsNormalized())
			{
				word = word.Normalize();
			}

			return CheckSystem(System, word);
		}

		/// <summary>
		/// Check a word against this bundle's System dictionary first, then the SharedUser and
		/// SharedSite lists, then the foreign allow-list. Returns true (accepted) if any of them
		/// accepts it.
		///
		/// Side effect (cross-off dictionary maintenance): when the System dictionary rejects
		/// the word and it is found in the SharedUser and/or SharedSite list, that is recorded in
		/// <see cref="UsageTracker"/> — the entry was needed. Both lists are probed for
		/// recording, so a word present in both crosses off both entries.
		/// </summary>
		public bool Check(string word)
		{
			if (string.IsNullOrWhiteSpace(word))
			{
				return true;
			}

			// NFC-normalise the lookup query. The dictionaries (Hunspell System
			// plus the SharedUser/SharedSite lists) hold composed forms; page/script
			// content can arrive decomposed — e.g. "für" as u + U+0308 combining
			// diaeresis — which renders identically but mismatches a composed entry
			// byte-for-byte and would false-flag a valid word. Query-only: callers keep
			// the original token for the displayed miss/excerpt, so offsets are
			// untouched. IsNormalized() guards the hot path — already-composed tokens
			// (the vast majority) skip the allocation; only decomposed ones pay it.
			// (Parameterless Normalize()/IsNormalized() default to Form C, and avoid
			// the System.Text namespace clashing with this class's System member.)
			if (!word.IsNormalized())
			{
				word = word.Normalize();
			}

			// The system dictionary is asked first. A word it accepts never touches the user or
			// site list, so an entry there counts as used only when it is what saves the word —
			// "used" means needed, and an entry the system dictionary has caught up with shows as
			// an orphan. The outcome is the same as asking the lists first: a word passes when
			// any of them accepts it.
			if (System != null && CheckSystem(System, word))
			{
				return true;
			}

			bool inUser = SharedUser.Contains(word);
			bool inSite = SharedSite.Contains(word);

			if (inUser)
			{
				UsageTracker.RecordUser(word);
			}

			if (inSite)
			{
				UsageTracker.RecordSite(word);
			}

			if (inUser || inSite)
			{
				return true;
			}

			// Foreign-language allow-list: consult-only, never recorded in UsageTracker — the
			// foreign dictionary is exempt from orphan/prune maintenance by design (see SharedForeign).
			return SharedForeign.Contains(word);
		}

		/// <summary>Which tier of a bundle accepts a word.</summary>
		public enum Tier
		{
			None,
			System,
			User,
			Site,
			Foreign,
		}

		/// <summary>
		/// Which tier accepts the word, asked in the order <see cref="Check"/> asks them, without
		/// recording usage: for explaining a finding after the check (an explanation must not
		/// change which dictionary entries count as needed).
		/// </summary>
		public Tier AcceptedBy(string word)
		{
			if (string.IsNullOrWhiteSpace(word))
			{
				return Tier.System;
			}

			if (!word.IsNormalized())
			{
				word = word.Normalize();
			}

			return System != null && CheckSystem(System, word) ? Tier.System
				: SharedUser.Contains(word) ? Tier.User
				: SharedSite.Contains(word) ? Tier.Site
				: SharedForeign.Contains(word) ? Tier.Foreign
				: Tier.None;
		}

		// ── System-dictionary checks: no clock, remembered for the run ─────────
		//
		// The library has four clocks. The compound check gives up after 50 ms and then reports
		// a valid compound as unknown. A compound of three or more parts that passes is then
		// tested once more: if it is a simple misspelling of a dictionary word, it is rejected.
		// That test has its own clocks (100 ms for the compound suggestion, 50 ms per step, 250 ms
		// overall); when one runs out, the test finds nothing and the misspelling is accepted.
		// So with a clock on, a verdict depends on how much CPU the machine gives the check: on a
		// busy machine, under parallel load, valid compounds come out misspelled and misspelt ones
		// pass, and the same input gives different results run to run. All four clocks are off (a negative
		// limit disables the library's timer), so a verdict depends only on the word and the
		// dictionary. The work stays bounded by the word itself: the library rejects words
		// longer than its MaxWordLen (100) before any compound search.
		//
		// The test levers (CheckLevers) can switch clocks back on and make checks take turns, for
		// this session only and only in Debug mode; a run takes them as they are when it starts.
		//
		// The same words reach the system dictionary over and over: on every page, in every
		// script literal, and once per language of a page's union. With the clocks off a
		// verdict never changes, so it is asked once per word and the answer remembered. Only
		// the system tier is remembered: the user/site lookups above stay live, because they
		// record usage for dictionary maintenance on every hit. One budget is shared by all
		// dictionaries of a run, so memory stays small on a small machine; past the budget
		// words are simply asked again, with the same answer.

		/// <summary>The levers of the current run, with their options and the lock for taking turns.</summary>
		private sealed class RunLevers(CheckLevers levers)
		{
			public CheckLevers Levers { get; } = levers;

			public QueryOptions Options { get; } = levers.ToOptions();

			public object WholeProgram { get; } = new();

			public ConditionalWeakTable<WordList, object> PerList { get; } = new();
		}

		private static RunLevers active = new(CheckLevers.Default);

		/// <summary>How the system dictionary is asked in this run: by default no clock at all.</summary>
		internal static QueryOptions CheckOptions => Volatile.Read(ref active).Options;

		/// <summary>The test levers this run uses.</summary>
		public static CheckLevers ActiveLevers => Volatile.Read(ref active).Levers;

		/// <summary>Most distinct words remembered across all dictionaries of a run (a few tens of MB).</summary>
		internal const int VerdictBudget = 250_000;

		private static int verdictsRemembered;

		/// <summary>The language this bundle was registered under (for the run diagnostics).</summary>
		public string Language { get; set; } = string.Empty;

		/// <summary>The flavour of its language that checks pages naming only the language (see DictionaryBundleConfig.Primary).</summary>
		public bool IsPrimary { get; set; }

		/// <summary>
		/// Starts a run: the shared budget and the slowest-check record begin empty, and the run's
		/// test levers are set (the defaults when none are given).
		/// </summary>
		public static void ResetRunState(CheckLevers? levers = null)
		{
			Volatile.Write(ref active, new RunLevers(levers ?? CheckLevers.Default));
			Interlocked.Exchange(ref verdictsRemembered, 0);
			WordCheckStats.Reset();
		}

		private sealed class VerdictCache(WordList list)
		{
			public WordList List { get; } = list;

			public ConcurrentDictionary<string, bool> Verdicts { get; } = new(StringComparer.Ordinal);
		}

		private VerdictCache? verdictCache;

		private bool CheckSystem(WordList list, string word)
		{
			var cache = verdictCache;
			if (cache is null || !ReferenceEquals(cache.List, list))
			{
				// First use, or the word list was replaced: start an empty cache for this list.
				cache = new VerdictCache(list);
				verdictCache = cache;
			}

			if (cache.Verdicts.TryGetValue(word, out bool known))
			{
				CrawlerSuite.Core.Host.WorkCounters.Lookup(remembered: true);
				return known;
			}

			CrawlerSuite.Core.Host.WorkCounters.Lookup(remembered: false);

			var run = Volatile.Read(ref active);
			bool verdict;
			switch (run.Levers.TakeTurns)
			{
				case TakeTurns.WholeProgram:
					lock (run.WholeProgram)
					{
						verdict = Ask(list, word, run.Options);
					}

					break;
				case TakeTurns.PerLanguage:
					lock (run.PerList.GetValue(list, _ => new object()))
					{
						verdict = Ask(list, word, run.Options);
					}

					break;
				default:
					verdict = Ask(list, word, run.Options);
					break;
			}

			// Counted here, not with ConcurrentDictionary.Count, which takes every lock.
			if (Volatile.Read(ref verdictsRemembered) < VerdictBudget
				&& cache.Verdicts.TryAdd(word, verdict))
			{
				Interlocked.Increment(ref verdictsRemembered);
			}

			return verdict;
		}

		/// <summary>Asks the library; the time measured is the check alone, not a wait for a turn.</summary>
		private bool Ask(WordList list, string word, QueryOptions options)
		{
			long started = Stopwatch.GetTimestamp();
			bool answer = list.Check(word, options);
			WordCheckStats.Record(Stopwatch.GetTimestamp() - started, word.Length, Language);
			return answer;
		}

		/// <summary>
		/// Check a word against a collection of bundles from multiple languages.
		/// Returns true if the word is accepted by ANY of the provided bundles.
		/// Used where a word may legitimately be in any of several languages (e.g. multi-language
		/// page checks and prefix-stripped stem lookups).
		/// </summary>
		public static bool CheckAny(string word, IEnumerable<Bundle> bundles)
		{
			if (string.IsNullOrWhiteSpace(word))
			{
				return true;
			}

			foreach (var bundle in bundles)
			{
				if (bundle.Check(word))
				{
					return true;
				}
			}
			return false;
		}
	}
}
