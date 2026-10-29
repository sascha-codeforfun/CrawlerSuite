using System.Text;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// The triage search: a page is found by its address (the text anywhere in it) or by a word
	/// it has a spelling finding for (the whole word, ignoring case). A word a dictionary holds by
	/// now is no finding any more, so it finds nothing.
	/// </summary>
	public static class PageSearch
	{
		/// <summary>Whether the page is found by <paramref name="search"/>; an empty search finds every page.</summary>
		public static bool Lets(PageSummary page, string search)
		{
			search = search.Trim();
			return search.Length == 0
				|| page.Url.Contains(search, StringComparison.OrdinalIgnoreCase)
				|| HasWord(page, search);
		}

		/// <summary>Whether the page has a spelling finding (triaged or riding along) for exactly <paramref name="word"/>, ignoring case.</summary>
		public static bool HasWord(PageSummary page, string word)
		{
			var wanted = Normal(word);
			return wanted.Length > 0 && page.All.Any(i => TriageSwitches.IsSpelling(i.Type) && Same(Normal(SpellingSource.WordOf(i.Detail)), wanted));
		}

		/// <summary>How many of the pages have a spelling finding for <paramref name="word"/>.</summary>
		public static int PagesWithWord(IEnumerable<PageSummary> pages, string word) =>
			pages.Count(p => HasWord(p, word));

		/// <summary>
		/// For each flagged word, how many of the pages have it (keys composed, ignoring case): what
		/// a page's spelling items show as "other pages", worked out once instead of per item.
		/// </summary>
		public static IReadOnlyDictionary<string, int> WordPages(IEnumerable<PageSummary> pages)
		{
			var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (var page in pages)
			{
				var words = page.All.Where(i => TriageSwitches.IsSpelling(i.Type))
					.Select(i => Normal(SpellingSource.WordOf(i.Detail))).Where(w => w.Length > 0)
					.Distinct(StringComparer.OrdinalIgnoreCase);
				foreach (var word in words)
				{
					counts[word] = counts.GetValueOrDefault(word) + 1;
				}
			}

			return counts;
		}

		/// <summary>The count of <paramref name="word"/> in what <see cref="WordPages"/> returned; 0 when no page has it.</summary>
		public static int PagesOf(IReadOnlyDictionary<string, int> wordPages, string word) =>
			wordPages.TryGetValue(Normal(word), out var n) ? n : 0;

		private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

		// The same word whether typed or pasted composed or decomposed (a + combining diaeresis → ä).
		private static string Normal(string word)
		{
			word = word.Trim();
			return word.IsNormalized(NormalizationForm.FormC) ? word : word.Normalize(NormalizationForm.FormC);
		}
	}
}
