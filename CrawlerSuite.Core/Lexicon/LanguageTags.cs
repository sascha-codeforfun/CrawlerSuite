namespace CrawlerSuite.Core.Lexicon
{
	using System.Text.RegularExpressions;

	/// <summary>
	/// Language tags as pages write them in <c>lang</c> (BCP 47: "en", "en-GB", "de-CH"): compared
	/// case-insensitively, "_" read as "-". "en-UK" is read as "en-GB" — the United Kingdom's region
	/// code is GB; UK is only reserved, but authors write it and the intent is unambiguous.
	/// </summary>
	public static class LanguageTags
	{
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex TagRegex = new(@"^[a-z]{2,3}(-[a-z0-9]{2,8})*$", RegexOptions.Compiled);

		/// <summary>The tag in one spelling: lowercase, "-" between subtags, en-UK as en-GB.</summary>
		public static string Normalize(string? tag)
		{
			var t = (tag ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
			return t == "en-uk" || t.StartsWith("en-uk-", System.StringComparison.Ordinal) ? "en-gb" + t[5..] : t;
		}

		/// <summary>The language without region or script: "en-GB" → "en".</summary>
		public static string BaseOf(string? tag) => Normalize(tag).Split('-')[0];

		/// <summary>Whether <paramref name="value"/> is written as a language tag ("de", "en-US"; not "enUS").</summary>
		public static bool IsTag(string? value) => TagRegex.IsMatch(Normalize(value));
	}

	/// <summary>
	/// The loaded dictionaries as languages and their flavours. A language with one dictionary is
	/// that dictionary; a language with several (en-GB, en-US) has one marked Primary. A tag that
	/// names a loaded flavour gets it; any other tag of the language gets the language's dictionary
	/// or its Primary — so a page saying "en" is checked by the Primary, one saying "en-US" by en-US.
	/// </summary>
	public sealed class DictionaryFlavours
	{
		private readonly Dictionary<string, string> byTag = new(System.StringComparer.Ordinal);
		private readonly Dictionary<string, string> byLanguage = new(System.StringComparer.Ordinal);
		private readonly Dictionary<string, List<string>> families = new(System.StringComparer.Ordinal);

		/// <param name="bundles">Each dictionary's key (its code as configured) and whether it is marked Primary, in configuration order.</param>
		public DictionaryFlavours(IEnumerable<(string Key, bool Primary)> bundles)
		{
			var primaries = new Dictionary<string, string>(System.StringComparer.Ordinal);
			foreach (var (key, primary) in bundles)
			{
				if (string.IsNullOrWhiteSpace(key))
				{
					continue;
				}

				var tag = LanguageTags.Normalize(key);
				if (!byTag.TryAdd(tag, key.Trim()))
				{
					continue; // the same tag twice: the first entry counts
				}

				var language = LanguageTags.BaseOf(tag);
				if (!families.TryGetValue(language, out var family))
				{
					family = [];
					families[language] = family;
				}

				family.Add(key.Trim());
				if (primary)
				{
					primaries.TryAdd(language, key.Trim());
				}
			}

			foreach (var (language, family) in families)
			{
				// One flavour: that one. Several: the Primary, else (a configuration to fix, named by
				// Problems) the first listed, so the run stays deterministic.
				byLanguage[language] = family.Count == 1 ? family[0] : primaries.GetValueOrDefault(language) ?? family[0];
			}
		}

		/// <summary>The flavours from the configured bundles (their codes and Primary marks).</summary>
		public static DictionaryFlavours From(IEnumerable<DictionaryBundleConfig>? bundles) =>
			new((bundles ?? []).Select(b => (b.LanguageCode ?? string.Empty, b.Primary)));

		/// <summary>
		/// The dictionary key for a language tag: the flavour it names, else its language's dictionary
		/// or Primary; null when no dictionary of that language is loaded.
		/// </summary>
		public string? Resolve(string? tag)
		{
			var normal = LanguageTags.Normalize(tag);
			if (normal.Length == 0)
			{
				return null;
			}

			return byTag.TryGetValue(normal, out var key) ? key
				: byLanguage.TryGetValue(LanguageTags.BaseOf(normal), out key) ? key
				: null;
		}

		/// <summary>The keys of every flavour of the language <paramref name="tag"/> belongs to, in configuration order.</summary>
		public IReadOnlyList<string> FamilyOf(string? tag) =>
			families.TryGetValue(LanguageTags.BaseOf(tag), out var family) ? family : [];

		/// <summary>The languages with more than one flavour, and their Primary (the key a bare language tag resolves to).</summary>
		public IEnumerable<(string Language, string Primary)> MultiFlavour() =>
			families.Where(f => f.Value.Count > 1).Select(f => (f.Key, byLanguage[f.Key]));

		/// <summary>
		/// What is wrong with the bundles' codes and Primary marks, in plain words: a code not written
		/// as a language tag, a tag listed twice, a language with several flavours and no Primary or
		/// more than one. Empty when all is well. Not fatal: the run resolves deterministically.
		/// </summary>
		public static IReadOnlyList<string> Problems(IEnumerable<DictionaryBundleConfig>? bundles)
		{
			var list = (bundles ?? []).Where(b => !string.IsNullOrWhiteSpace(b.LanguageCode)).ToList();
			var problems = new List<string>();
			foreach (var b in list.Where(b => !LanguageTags.IsTag(b.LanguageCode)))
			{
				problems.Add($"Language \"{b.LanguageCode.Trim()}\" is no language tag as pages write it in lang — e.g. de, en-GB, en-US.");
			}

			foreach (var twice in list.GroupBy(b => LanguageTags.Normalize(b.LanguageCode)).Where(g => g.Count() > 1))
			{
				problems.Add($"Language \"{twice.First().LanguageCode.Trim()}\" is listed {twice.Count()} times; only the first is used.");
			}

			foreach (var family in list.Where(b => LanguageTags.IsTag(b.LanguageCode))
				.GroupBy(b => LanguageTags.BaseOf(b.LanguageCode))
				.Where(g => g.Select(b => LanguageTags.Normalize(b.LanguageCode)).Distinct().Count() > 1))
			{
				var codes = string.Join(", ", family.Select(b => b.LanguageCode.Trim()));
				int marked = family.Count(b => b.Primary);
				if (marked == 0)
				{
					problems.Add($"{codes}: one of them needs to be marked Primary (it checks pages that say only \"{family.Key}\"); until then the first is used.");
				}
				else if (marked > 1)
				{
					problems.Add($"{codes}: only one may be marked Primary; the first marked is used.");
				}
			}

			return problems;
		}
	}
}
