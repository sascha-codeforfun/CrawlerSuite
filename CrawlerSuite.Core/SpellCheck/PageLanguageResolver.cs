namespace CrawlerSuite.Core.SpellCheck
{
	using System;
	using CrawlerSuite.Core.Lexicon;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// Resolves which language(s) a page is spell-checked against, applying the operator-declared
	/// <see cref="SpellCheckEngineConfig.PageLanguageOverrides"/> on top of the single branch-detected
	/// language. The override exists because some pages mix languages (or carry a branch language that
	/// is wrong for their content) and the site's markup cannot be trusted to declare it (no reliable
	/// lang attribute) — so the operator declares ground truth by URL path prefix.
	///
	/// Matching: the key is a URL PATH PREFIX (from the first slash, domain stripped), compared with
	/// <see cref="string.StartsWith(string, StringComparison)"/> using ordinal-ignore-case. When more
	/// than one key matches a path, the LONGEST key wins (lex specialis derogat legi generali) so a
	/// specific page override beats a section-wide one regardless of declaration order. A page that
	/// matches no key resolves to its single branch language.
	///
	/// The resolved set is checked as a UNION by <see cref="RunChecker"/>: a word is a miss only if
	/// every listed dictionary misses it. This is intentionally more permissive than a single-language
	/// check, so overrides should be declared narrowly — only for genuinely mixed pages.
	/// </summary>
	public static class PageLanguageResolver
	{
		/// <summary>
		/// Resolve the language set for <paramref name="url"/>. If a <paramref name="overrides"/> key is
		/// a prefix of the URL's path, the longest matching key's language list is returned (order
		/// preserved); otherwise a single-element list holding <paramref name="branchLanguage"/>.
		/// </summary>
		// Resolution lives in the Html layer (CrawlerSuite.Core.Html.PageLanguageSet) so spelling
		// and content-quality share one resolver and cannot drift apart. This forwards;
		// spelling always passes a non-empty branch language, so the result is the
		// override or the branch language. MissingBundles
		// stays here because it is spell-config-specific (needs loaded dictionary bundles).
		public static IReadOnlyList<string> Resolve(
			string url,
			string branchLanguage,
			IReadOnlyDictionary<string, List<string>>? overrides)
			=> CrawlerSuite.Core.Html.PageLanguageSet.Resolve(url, branchLanguage, overrides);

		/// <summary>
		/// The languages named in the overrides that have no loaded bundle, one line each with the
		/// paths that name it ("cs — pages under /a/, /b/ are checked without it"); empty when all
		/// are there. A path whose every language is missing is not spell-checked at all.
		/// </summary>
		public static IReadOnlyList<string> MissingBundles(
			IReadOnlyDictionary<string, List<string>>? overrides,
			IReadOnlyDictionary<string, Bundle> loadedBundles) =>
			MissingLanguages(overrides, code => loadedBundles.ContainsKey(code));

		/// <summary>As <see cref="MissingBundles"/>, against the language codes of the configured bundles.</summary>
		public static IReadOnlyList<string> MissingLanguages(
			IReadOnlyDictionary<string, List<string>>? overrides,
			Func<string, bool> isInstalled)
		{
			if (overrides is not { Count: > 0 })
			{
				return [];
			}

			return overrides
				.SelectMany(o => (o.Value ?? []).Where(l => !string.IsNullOrWhiteSpace(l) && !isInstalled(l)).Select(l => (Language: l, Path: o.Key)))
				.GroupBy(x => x.Language, StringComparer.OrdinalIgnoreCase)
				.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
				.Select(g => $"{g.Key} has no dictionary bundle — pages under {string.Join(", ", g.Select(x => x.Path))} are checked without it.")
				.ToList();
		}


	}
}
