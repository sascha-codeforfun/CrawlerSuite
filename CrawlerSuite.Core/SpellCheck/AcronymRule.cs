namespace CrawlerSuite.Core.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// Which words count as acronyms and are therefore not spell-checked: every letter a capital,
	/// at most <see cref="MaxLength"/> letters, optionally followed by a small plural s that is not
	/// counted. A word with any other character (a digit, a small letter inside) is not an acronym
	/// and is checked like any other word.
	/// </summary>
	public sealed record AcronymRule(int MaxLength, bool PluralS)
	{
		/// <summary>The rule a configuration asks for; null when acronyms are checked like any word.</summary>
		public static AcronymRule? From(SpellCheckEngineConfig config) =>
			config.IgnoreAcronyms ? new AcronymRule(Math.Clamp(config.AcronymMaxLength, 1, 5), config.AcronymPluralS) : null;

		/// <summary>True when <paramref name="word"/> is an acronym this rule leaves unchecked.</summary>
		public bool Matches(string word)
		{
			if (string.IsNullOrEmpty(word))
			{
				return false;
			}

			var core = word;
			if (PluralS && core.Length >= 3 && core[^1] == 's')
			{
				core = core[..^1]; // "ABCs": the plural s needs at least two capitals before it ("Is" stays a word)
			}

			return core.Length <= MaxLength && core.All(c => char.IsLetter(c) && char.IsUpper(c));
		}

		/// <summary>"up to 3 letters, plural s" for the run steps.</summary>
		public string Describe() => $"up to {MaxLength} letter{(MaxLength == 1 ? string.Empty : "s")}{(PluralS ? ", plural s" : string.Empty)}";
	}

	/// <summary>
	/// The acronyms a page held, in text order, each once, with the text around its first
	/// occurrence. One per page (no sharing between threads); merged in page order afterwards so
	/// the list is the same however the pages were scheduled.
	/// </summary>
	public sealed class AcronymCollector
	{
		private const int Around = 40;

		private readonly HashSet<string> seen = new(StringComparer.Ordinal);
		private readonly List<(string Acronym, string Excerpt)> found = [];

		public IReadOnlyList<(string Acronym, string Excerpt)> Found => found;

		/// <summary>Notes an acronym of <paramref name="text"/>; a second occurrence on the page is ignored.</summary>
		public void Add(string acronym, string text)
		{
			if (!seen.Add(acronym))
			{
				return;
			}

			int at = text.IndexOf(acronym, StringComparison.Ordinal);
			string excerpt;
			if (at < 0)
			{
				excerpt = acronym;
			}
			else
			{
				int from = Math.Max(0, at - Around);
				int to = Math.Min(text.Length, at + acronym.Length + Around);
				excerpt = (from > 0 ? "…" : string.Empty) + text[from..to] + (to < text.Length ? "…" : string.Empty);
			}

			found.Add((acronym, string.Join(' ', excerpt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));
		}
	}

	/// <summary>Log 37: the acronyms not checked, once each, for a person to look over.</summary>
	public static class AcronymList
	{
		/// <summary>
		/// One line per acronym: the acronym, the number of pages it is on, and the text around its
		/// first occurrence (first page in page order). Sorted by the acronym's letters regardless of
		/// their order, then the acronym itself, so look-alikes ("ABC", "ACB") sit next to each other.
		/// </summary>
		public static IReadOnlyList<string> Lines(IEnumerable<(string Page, AcronymCollector? Collector)> pagesInOrder, AcronymRule rule)
		{
			var pages = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
			var first = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var (page, collector) in pagesInOrder)
			{
				if (collector is null)
				{
					continue;
				}

				foreach (var (acronym, excerpt) in collector.Found)
				{
					if (!pages.TryGetValue(acronym, out var on))
					{
						on = new HashSet<string>(StringComparer.Ordinal);
						pages[acronym] = on;
						first[acronym] = excerpt;
					}

					on.Add(page);
				}
			}

			var lines = new List<string>
			{
				$"# 37 — acronyms not spell-checked ({rule.Describe()}): {pages.Count} distinct, for review",
				"# columns: acronym <TAB> pages <TAB> text around its first occurrence",
				"# sorted by letters regardless of order, so look-alikes sit together; nothing here is a finding",
			};
			lines.AddRange(pages.Keys
				.OrderBy(SortKey, StringComparer.Ordinal)
				.ThenBy(a => a, StringComparer.Ordinal)
				.Select(a => $"{a}\t{pages[a].Count}\t{Security.CsvInjectionGuard.Neutralize(first[a])}")); // formula guard on the page's text
			return lines;
		}

		/// <summary>The acronym's capitals in alphabetical order ("ACB" and "ABC" both "ABC"); a plural s is left out.</summary>
		internal static string SortKey(string acronym)
		{
			var core = acronym.Length >= 3 && acronym[^1] == 's' ? acronym[..^1] : acronym;
			var letters = core.ToCharArray();
			Array.Sort(letters);
			return new string(letters);
		}
	}
}
