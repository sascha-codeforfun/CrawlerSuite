namespace CrawlerSuite.Core.SpellCheck
{
	using System;
	using CrawlerSuite.Core.Lexicon;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using System.Linq;

	/// <summary>
	/// The spell-checking pass: tokenises text and reports words that no dictionary
	/// tier accepts. Brand/compound prefix handling is delegated to
	/// <see cref="WordPrefix"/>; German trailing-hyphen compound stems are resolved
	/// by <see cref="CheckTrailingHyphenStem"/>, and the parenthesised optional-prefix
	/// form "(prefix-)stem" by <see cref="TryParenthesizedPrefixJoin"/>.
	/// </summary>
	public static class SpellChecker
	{
		/// <summary>
		/// Spell-check <paramref name="text"/> against <paramref name="dictionary"/> for
		/// <paramref name="language"/>. Words starting with a configured prefix followed
		/// by a hyphen have the prefix stripped before lookup — the remainder is checked
		/// and reported instead of the full token, so errors are immediately actionable.
		/// </summary>
		[ExcludeFromCodeCoverage(Justification =
			"Coupled to Hunspell WordList.Check across multi-tier dictionary bundles. " +
			"Testing requires real .aff/.dic fixtures; exercises Hunspell library " +
			"behaviour rather than Tools logic.")]
		public static IEnumerable<KeyValuePair<string, List<string>>> Check(
			string text,
			Bundle dictionary,
			string language,
			IReadOnlyList<string>? prefixesToStrip = null,
			IReadOnlyList<string>? fugenelemente = null)
		{
			return CheckPrepared(text, Prepare(text, prefixesToStrip), dictionary, language, fugenelemente);
		}

		/// <summary>
		/// One word of a text, ready for the dictionary: tokenised, prefix-stripped, soft hyphens
		/// removed, and whether it is a trailing-hyphen compound stem. None of this depends on the
		/// language, so a text checked against several languages is prepared once.
		/// </summary>
		internal readonly record struct PreparedWord(string Word, bool IsTrailingHyphenStem);

		/// <summary>The language-independent half of <see cref="Check"/>: the words to look up, in text order.</summary>
		/// <param name="coveredByKnown">Where the words made only of Known word parts are noted (they are accepted here, before any dictionary).</param>
		internal static List<PreparedWord> Prepare(string text, IReadOnlyList<string>? prefixesToStrip, AcronymRule? acronyms = null, AcronymCollector? collector = null, List<string>? coveredByKnown = null)
		{
			var prefixes = prefixesToStrip ?? [];
			var words = new List<PreparedWord>();
			foreach (var (word, hyphenFollows) in SpellTokenizer.TokenizeTextWithFollowingHyphen(text))
			{
				var trimmedWord = word?.Trim();
				if (string.IsNullOrEmpty(trimmedWord) || !SpellTokenizer.IdentifyWord(trimmedWord))
				{
					continue;
				}

				// A word made entirely of configured prefixes ("BRAND", "BRAND-Line") is accepted.
				// Otherwise the WHOLE word is kept: the prefixes are known parts of the
				// hyphenated-word check (see HyphenatedPartsAccepted), so a typo after a prefix is
				// reported in its full word ("BRAND-Tetx"), not as a bare remainder.
				if (WordPrefix.Strip(trimmedWord, prefixes) == null)
				{
					coveredByKnown?.Add(trimmedWord);
					continue;
				}

				string? wordToCheck = trimmedWord;

				wordToCheck = wordToCheck.Replace("\u00AD", ""); // strip soft hyphen from stale normalized text
				if (string.IsNullOrEmpty(wordToCheck))
				{
					continue;
				}

				// Acronyms are not checked: no dictionary can judge them. They are noted for the
				// review list (log 37) — standing alone, as a suspended head ("ABC-"), or as a part of
				// a hyphenated word, where the part-by-part check counts them as known.
				if (acronyms is not null)
				{
					var bare = wordToCheck.TrimEnd('-');
					if (acronyms.Matches(bare))
					{
						collector?.Add(bare, text);
						continue;
					}

					if (collector is not null && bare.Contains('-'))
					{
						foreach (var part in bare.Split('-'))
						{
							if (acronyms.Matches(part))
							{
								collector.Add(part, text);
							}
						}
					}
				}

				// A suspended compound head ("Haus- und Garten", "Haus-,"): the tokenizer leaves the
				// hyphen out, so it is read from the text right after THIS occurrence. The same word
				// elsewhere in the text, without a hyphen after it, is an ordinary word.
				bool isTrailingHyphenStem = wordToCheck.EndsWith('-') || hyphenFollows;

				words.Add(new PreparedWord(wordToCheck, isTrailingHyphenStem));
			}

			CrawlerSuite.Core.Host.WorkCounters.TextRuns(1);
			CrawlerSuite.Core.Host.WorkCounters.Words(words.Count);
			return words;
		}

		/// <summary>
		/// The language-dependent half of <see cref="Check"/>: looks the prepared words up in
		/// <paramref name="dictionary"/> and returns the misses in text order.
		/// </summary>
		internal static Dictionary<string, List<string>> CheckPrepared(
			string text,
			IReadOnlyList<PreparedWord> words,
			Bundle dictionary,
			string language,
			IReadOnlyList<string>? fugenelemente = null,
			Bundle? english = null,
			IReadOnlyList<string>? knownParts = null,
			AcronymRule? acronyms = null,
			KnownPartCounter? knownUse = null)
		{
			var fugen = fugenelemente ?? [];
			var spans = knownUse is not null ? new List<string>() : null;
			Dictionary<string, List<string>> errors = [];

			foreach (var (wordToCheck, isTrailingHyphenStem) in words)
			{
				if (isTrailingHyphenStem)
				{
					if (CheckTrailingHyphenStem(
						wordToCheck.EndsWith('-') ? wordToCheck : wordToCheck + "-",
						dictionary, fugen))
					{
						continue;
					}

					// German "(prefix-)stem" optional-prefix form: the bare prefix is a
					// bound morpheme, valid only joined to the immediately following word.
					// Accept when that join is a real word (see TryParenthesizedPrefixJoin).
					if (TryParenthesizedPrefixJoin(wordToCheck, text, dictionary))
					{
						continue;
					}

					// Its parts as in any hyphenated word (known word parts, single capitals,
					// acronyms, English parts), where every part — the last one too — stands
					// before a hyphen and may be a compound head. Never stricter than the
					// ordinary check of the same word.
					spans?.Clear();
					if (HyphenatedPartsAccepted(wordToCheck.TrimEnd('-'), dictionary, english, knownParts ?? [], fugen, acronyms, suspendedHead: true, knownSpans: spans))
					{
						knownUse?.AddSpans(spans!, wordToCheck.TrimEnd('-'), language, knownParts ?? []);
						continue;
					}
				}
				else
				{
					if (dictionary.Check(wordToCheck))
					{
						continue;
					}

					spans?.Clear();
					if (HyphenatedPartsAccepted(wordToCheck, dictionary, english, knownParts ?? [], fugen, acronyms, knownSpans: spans))
					{
						knownUse?.AddSpans(spans!, wordToCheck, language, knownParts ?? []);
						continue;
					}
				}

				var reportWord = wordToCheck;
				if (!errors.TryGetValue(reportWord, out var languages))
				{
					languages = [];
					errors[reportWord] = languages;
				}

				if (!languages.Contains(language))
				{
					languages.Add(language);
				}
			}

			return errors;
		}

		/// <summary>
		/// A hyphenated word that fails as a whole is checked part by part: it passes when every
		/// part is known. A part is known when the page language's dictionary or the English one
		/// accepts it (German joins its own words with English loans by hyphen: "Online-Shop",
		/// "E-Mail-Adresse"), when it is a single capital letter ("U-Bahn", "T-Shirt"), or when a
		/// configured prefix covers it — a prefix may span several parts ("BRAND-Line") and may
		/// stand anywhere in the word. The user and site lists count through the dictionaries.
		/// A part before the last may also be a compound head, as German writes it before a
		/// hyphen: with a linking element or as a bare verb stem — checked the way a suspended
		/// head is (<see cref="CheckTrailingHyphenStem"/>). The last part must be a word.
		/// A part that is none of these keeps the whole word flagged, so a typo in any part
		/// still surfaces, reported as the full word.
		/// </summary>
		/// <param name="suspendedHead">
		/// The word stands before a hyphen ("Brand-Xyzzy- und …"): every part, the last one too,
		/// may be a compound head, and a word of one part is walked as well.
		/// </param>
		/// <param name="knownSpans">Where the spans covered by Known word parts are noted (as written in the word).</param>
		internal static bool HyphenatedPartsAccepted(string word, Bundle dictionary, Bundle? english, IReadOnlyList<string> knownParts, IReadOnlyList<string>? fugenelemente = null, AcronymRule? acronyms = null, bool suspendedHead = false, List<string>? knownSpans = null)
		{
			// The dictionaries are asked in a fixed order and the walk stops at the first
			// unknown part, so usage is recorded only for the parts the check needed (see
			// HyphenParts for the explaining walk, which records nothing).
			var other = english is not null && !ReferenceEquals(english, dictionary) ? english : null;
			return WalkHyphenParts(
				word,
				knownParts,
				(part, isLast) =>
					(part.Length == 1 && char.IsUpper(part[0])) ? HyphenPartStatus.Capital
					: acronyms is not null && acronyms.Matches(part) ? HyphenPartStatus.Acronym
					: dictionary.Check(part) ? HyphenPartStatus.Dictionary
					: other is not null && other.Check(part) ? HyphenPartStatus.English
					: (!isLast || suspendedHead) && CheckTrailingHyphenStem(part + "-", dictionary.Check, fugenelemente ?? []) ? HyphenPartStatus.CompoundHead
					: HyphenPartStatus.Unknown,
				report: knownSpans is null ? null : (span, status) =>
				{
					if (status == HyphenPartStatus.KnownWordPart)
					{
						knownSpans.Add(span);
					}
				},
				stopAtUnknown: true,
				singlePart: suspendedHead);
		}

		/// <summary>
		/// The one walk over a hyphenated word's parts, shared by the check and its explanation:
		/// a configured known word part may span several parts and is taken first (longest);
		/// every other part is classified by <paramref name="classify"/> (true for the last
		/// part as its second argument). Returns true when every part is known; false when the
		/// word has no hyphenated shape ("a--b", a leading or trailing hyphen, fewer than three
		/// characters) or a part is unknown. With <paramref name="singlePart"/>, a word without a
		/// hyphen is walked as one part (a suspended head of one word: "Xyzzy- und …").
		/// </summary>
		internal static bool WalkHyphenParts(
			string word,
			IReadOnlyList<string> knownParts,
			Func<string, bool, HyphenPartStatus> classify,
			Action<string, HyphenPartStatus>? report,
			bool stopAtUnknown,
			bool singlePart = false)
		{
			if (!(HasHyphenatedShape(word) || (singlePart && word.Length > 0 && !word.Contains('-'))))
			{
				return false;
			}

			var parts = word.Split('-');
			bool allKnown = true;
			int i = 0;
			while (i < parts.Length)
			{
				int covered = PrefixSpan(parts, i, knownParts);
				if (covered > 0)
				{
					report?.Invoke(string.Join('-', parts, i, covered), HyphenPartStatus.KnownWordPart);
					i += covered;
					continue;
				}

				var status = classify(parts[i], i == parts.Length - 1);
				report?.Invoke(parts[i], status);
				if (status == HyphenPartStatus.Unknown)
				{
					allKnown = false;
					if (stopAtUnknown)
					{
						return false;
					}
				}

				i++;
			}

			return allKnown;
		}

		/// <summary>A word the part-by-part check applies to: inner hyphens only, no empty part.</summary>
		internal static bool HasHyphenatedShape(string word) =>
			word.Length >= 3 && word[0] != '-' && word[^1] != '-' && word.Contains('-')
			&& !word.Contains("--", StringComparison.Ordinal);

		/// <summary>How many parts from <paramref name="start"/> on the longest configured prefix covers; 0 when none.</summary>
		private static int PrefixSpan(string[] parts, int start, IReadOnlyList<string> knownParts)
		{
			int best = 0;
			foreach (var prefix in knownParts)
			{
				if (string.IsNullOrEmpty(prefix))
				{
					continue;
				}

				var prefixParts = prefix.Split('-');
				if (prefixParts.Length <= best || start + prefixParts.Length > parts.Length)
				{
					continue;
				}

				bool match = true;
				for (int k = 0; k < prefixParts.Length && match; k++)
				{
					match = string.Equals(parts[start + k], prefixParts[k], StringComparison.OrdinalIgnoreCase);
				}

				if (match)
				{
					best = prefixParts.Length;
				}
			}

			return best;
		}

		// [KEEP] Ask the dictionary first, in the form it was written for. The German
		// igerman98/frami dictionary encodes suspended compound heads as words WITH their
		// hyphen: an entry like "Schul/hij" carries NEEDAFFIX (the bare "Schul" is not a
		// word) and a suffix rule that generates "Schul-" as a word of its own. So "Schul- und
		// Studienreise" is valid by the dictionary's own definition, while the bare stem is
		// not. Asking only for the stripped forms would flag every head whose compound form
		// drops a letter ("Schul-" from "Schule"). The stripped forms below stay as the
		// fallback for dictionaries without such entries.
		/// <summary>
		/// When a token ends with a hyphen (German compound-word prefix), checks it against the
		/// dictionary: (1) the word with its hyphen, as the dictionary may list suspended heads
		/// that way; (2) the stem without the hyphen; (3) the stem with each Fugenelement
		/// stripped (longest first). True if any form is accepted.
		/// </summary>
		internal static bool CheckTrailingHyphenStem(
			string word,
			Bundle dictionary,
			IReadOnlyList<string> fugenelemente) =>
			CheckTrailingHyphenStem(word, dictionary.Check, fugenelemente);

		/// <summary>The same check with any way of asking the dictionary (one that records nothing, say).</summary>
		internal static bool CheckTrailingHyphenStem(
			string word,
			Func<string, bool> accepts,
			IReadOnlyList<string> fugenelemente)
		{
			// The suspended head as the dictionary lists it: "Schul-".
			if (accepts(word))
			{
				return true;
			}

			// Strip the trailing hyphen.
			var stem = word[..^1];

			// Check stem as-is.
			if (accepts(stem))
			{
				return true;
			}

			// Try stripping each Fugenelement (longest first).
			foreach (var fuge in fugenelemente.OrderByDescending(f => f.Length))
			{
				if (!string.IsNullOrEmpty(fuge) && stem.EndsWith(fuge, StringComparison.OrdinalIgnoreCase))
				{
					var stripped = stem[..^fuge.Length];
					if (!string.IsNullOrEmpty(stripped) && accepts(stripped))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		/// German optional-prefix construction "(prefix-)stem": a parenthesised prefix
		/// with a completion hyphen that attaches to the immediately following word, so
		/// the text reads as either "stem" or "prefixstem". The bare prefix is a bound
		/// morpheme — not a standalone word — so it can only be validated joined to the
		/// next word. Scans <paramref name="text"/> for the literal shape "(prefix-)" and,
		/// for each occurrence, joins <paramref name="prefix"/> to the following run of
		/// letters and checks the compound against <paramref name="dictionary"/>. Returns
		/// true if any such join is accepted. Deliberately scoped to the parenthesised
		/// form only — bare trailing hyphens are handled by <see cref="CheckTrailingHyphenStem"/>.
		/// The suffix-after-base mirror ("Base(suffix)", e.g. "Nachhaltigkeit(smanagement)") lives as
		/// a per-occurrence rescue in <c>CrawlerSuite.Core.SpellCheck.RunChecker.TryParenthesizedSuffixJoin</c>.
		/// </summary>
		internal static bool TryParenthesizedPrefixJoin(string prefix, string text, Bundle dictionary)
		{
			if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(text))
			{
				return false;
			}

			var needle = "(" + prefix + "-)";
			int from = 0;
			while (true)
			{
				int hit = text.IndexOf(needle, from, StringComparison.Ordinal);
				if (hit < 0)
				{
					return false;
				}

				int afterNeedle = hit + needle.Length;
				int end = afterNeedle;
				while (end < text.Length && IsCompoundLetter(text[end]))
				{
					end++;
				}

				if (end > afterNeedle)
				{
					var nextWord = text[afterNeedle..end];
					if (dictionary.Check(prefix + nextWord))
					{
						return true;
					}
				}

				from = hit + 1;
			}
		}

		// Letters that may form the stem joined to a parenthesised prefix: ASCII letters
		// plus the German umlauts/eszett. Intentionally excludes digits, '_' and '-' so
		// the join takes exactly the following word, not a hyphenated continuation.
		private static bool IsCompoundLetter(char c) =>
			(c >= 'a' && c <= 'z')
			|| (c >= 'A' && c <= 'Z')
			|| c is 'ä' or 'ö' or 'ü' or 'ß' or 'Ä' or 'Ö' or 'Ü';
	}
}
