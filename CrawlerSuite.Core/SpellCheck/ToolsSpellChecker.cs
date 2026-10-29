namespace CrawlerSuite.Core.SpellCheck
{
	using System.Collections.Generic;
	using CrawlerSuite.Core.Lexicon;
	using System.Linq;

	/// <summary>
	/// Production adapter: exposes <c>SpellChecker.Check</c> as a
	/// <see cref="RunCheck"/> delegate, VERBATIM. No spell logic is reimplemented here — the
	/// LibreOffice dic/aff + user dictionary + site dictionary + prefix-strip + fugenelemente all
	/// come from that one call.
	///
	/// This is the single thin seam coupling the run-based module to SpellChecker, so where the
	/// checker lives concerns only this adapter.
	///
	/// Cross-language handling is NOT delegated to any region-based mechanism: the module's
	/// union over a page's declared PageLanguageOverrides languages (see RunChecker) is the sole
	/// cross-language mechanism by design. Mixed-language regions are either boilerplate-pruned
	/// (chrome/cookie banners) or handled by declaring the page's languages, never by accepting
	/// a word against any loaded dictionary.
	///
	/// The bundles, prefixes and fugenelemente are supplied by the caller (sourced exactly as the
	/// pipeline sources them) — this adapter does not load or invent dictionary data.
	///
	/// One instance per page or file, used by one thread: it remembers the words of the last
	/// text it was given, because RunChecker asks for the same text once per language of the
	/// page's union and the words do not depend on the language.
	/// </summary>
	public sealed class ToolsSpellChecker
	{
		private readonly Bundle _bundle;
		private readonly IReadOnlyDictionary<string, Bundle> _allBundles;
		private readonly IReadOnlyList<string> _prefixesToStrip;
		private readonly IReadOnlyList<string> _fugenelemente;
		private readonly AcronymRule? _acronyms;
		private readonly AcronymCollector? _acronymCollector;
		private readonly KnownPartCounter? _knownUse;
		private readonly CrawlerSuite.Core.Quality.ProperNames? _properNames;

		/// <param name="acronyms">Acronyms not to check (null: checked like any word).</param>
		/// <param name="acronymCollector">Where the page's unchecked acronyms are noted for log 37.</param>
		/// <param name="knownUse">Where the page's use of Known word parts is noted.</param>
		/// <param name="properNames">Proper names: their acronyms pass, and their words pass where the whole name stands.</param>
		public ToolsSpellChecker(
			Bundle bundle,
			IReadOnlyDictionary<string, Bundle> allBundles,
			IReadOnlyList<string> prefixesToStrip,
			IReadOnlyList<string> fugenelemente,
			AcronymRule? acronyms = null,
			AcronymCollector? acronymCollector = null,
			KnownPartCounter? knownUse = null,
			CrawlerSuite.Core.Quality.ProperNames? properNames = null)
		{
			_bundle = bundle;
			_allBundles = allBundles;
			_prefixesToStrip = prefixesToStrip;
			_fugenelemente = fugenelemente;
			_acronyms = acronyms;
			_acronymCollector = acronymCollector;
			_knownUse = knownUse;
			_properNames = properNames is { IsEmpty: false } ? properNames : null;
		}

		/// <summary>
		/// The English bundle, if one is loaded: a part of a hyphenated word may be an English
		/// loan on a page in any language ("Online-Shop"). Looked up once.
		/// </summary>
		private Bundle? English => english ??= EnglishOf(_allBundles);

		/// <summary>
		/// The English bundle among <paramref name="bundles"/>: the only English one, or the one marked
		/// Primary among several flavours, else the shortest English code; null when none.
		/// </summary>
		internal static Bundle? EnglishOf(IReadOnlyDictionary<string, Bundle> bundles)
		{
			// By key first: only the English dictionaries are asked for (each loads when first asked).
			var english = bundles.Keys
				.Where(key => CrawlerSuite.Core.Lexicon.LanguageTags.BaseOf(key) == "en")
				.OrderBy(key => key.Length)
				.ThenBy(key => key, System.StringComparer.Ordinal)
				.Select(key => bundles[key])
				.ToList();
			return english.FirstOrDefault(b => b.IsPrimary) ?? english.FirstOrDefault();
		}

		private Bundle? english;

		private string? preparedText;
		private List<SpellChecker.PreparedWord>? preparedWords;
		private readonly List<string> preparedCovered = [];

		/// <summary>The <see cref="RunCheck"/> delegate to hand to <see cref="RunChecker"/>.</summary>
		public IEnumerable<CheckMiss> Check(string canonicalRunText, string language)
		{
			// Use the requested language's bundle as the primary. For a single-language page this is
			// the same bundle passed at construction; for a multi-language (union) page RunChecker calls
			// this once per language, so the primary must follow the language argument rather than the
			// fixed constructor bundle. Falls back to the constructor bundle if the language is absent.
			Bundle primary = _allBundles.TryGetValue(language, out var b) ? b : _bundle;

			// The same text again (the next language of the page's set): its words are reused.
			var words = preparedWords;
			bool sameText = words is not null && string.Equals(preparedText, canonicalRunText, System.StringComparison.Ordinal);
			_knownUse?.BeginCheck(language, sameText);
			if (words is null || !sameText)
			{
				preparedCovered.Clear();
				words = SpellChecker.Prepare(canonicalRunText, _prefixesToStrip, _acronyms, _acronymCollector, _knownUse is not null ? preparedCovered : null);
				preparedWords = words;
				preparedText = canonicalRunText;
			}

			// Words made only of Known word parts pass before any dictionary; noted per language
			// like the words the part-by-part check lets pass.
			if (_knownUse is not null)
			{
				foreach (var word in preparedCovered)
				{
					_knownUse.AddSpans(KnownParts.Cover(word, _prefixesToStrip) ?? [], word, language, _prefixesToStrip);
				}
			}

			IEnumerable<KeyValuePair<string, List<string>>> misses = SpellChecker.CheckPrepared(
					canonicalRunText,
					words,
					primary,
					language,
					_fugenelemente,
					English,
					_prefixesToStrip,
					_acronyms,
					_knownUse);

			// A proper name's acronym, or a word of a proper name that stands as a whole in this text.
			if (_properNames is { } names)
			{
				string? keyed = null;
				misses = misses.Where(kvp => !names.Covers(kvp.Key, () => keyed ??= CrawlerSuite.Core.Quality.ProperNames.Key(canonicalRunText)));
			}

			return misses.Select(kvp => new CheckMiss(kvp.Key, string.Join(",", kvp.Value)));
		}
	}
}
