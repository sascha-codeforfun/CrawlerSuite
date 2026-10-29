namespace CrawlerSuite.Core.SpellCheck
{
	using System.Collections.Generic;
	using System.Text.RegularExpressions;

	/// <summary>
	/// Cuts a <see cref="TextRun"/> into span-bearing <see cref="Token"/>s. Each token keeps
	/// its character offset into the run, so the originating node and exact position stay
	/// recoverable for excerpt building and highlighting — no re-derivation, ever.
	///
	/// This word pattern is the single shared source for tokenization: both Tokenize (the
	/// located occurrences) and TokenizeText (the word verdict in SpellChecker) run off it, so
	/// the two CUT words identically by construction. It is parity of the PATTERN only: the
	/// skip spans below (email, URL, HTML character reference) are applied by Tokenize and not
	/// by TokenizeText, so a word inside such a span may be judged but never becomes a located
	/// finding. That difference is deliberate and one-directional — the located output is
	/// strictly quieter.
	///
	/// This stage tokenizes the run text AS GIVEN. Canonicalization (entity decoding, soft
	/// hyphen, dashes, smart quotes) is a separate stage and is applied to the run text before
	/// tokenization in the assembled pipeline, so the tokenizer and the excerpt see the same
	/// canonical form and cannot diverge.
	/// </summary>
	public static partial class SpellTokenizer
	{
		public static IEnumerable<Token> Tokenize(TextRun run)
		{
			// Pre-scan the run for spans whose characters are NOT prose — emails and URLs/domains —
			// then drop any word token that falls inside one. The word pattern would otherwise split
			// "name@example.de" or "www.example.de" on '@' / '.' into fake words (name, example,
			// de). Suppressing by SPAN (not by editing the word pattern) preserves tokenization
			// parity with TokenizeText for everything else, and keeps surrounding prose checked.
			//
			//  - Email: '@'-anchored — unambiguous, no prose token contains '@'.
			//  - URL/domain: a scheme (http://, https://), a www. prefix, or a dotted host ending in a
			//    known TLD. The TLD anchor is the safety property: it matches host.de / sub-domain.info
			//    but NOT German abbreviations like "z.B." or "e.g" (B / g are not TLDs), so sentence
			//    fragments and abbreviations are never eaten. Scheme URLs in ATTRIBUTES are already
			//    handled by ValueClassifier; this span scan additionally covers the visible-TEXT case.
			string text = run.RawText;
			List<(int Start, int End)> skipSpans = CollectSkipSpans(text);

			foreach (Match m in WordPattern().Each(text))
			{
				if (OverlapsAny(m.Index, m.Length, skipSpans))
				{
					continue;
				}

				yield return new Token(run, m.Value, m.Index, m.Length);
			}
		}

		private static List<(int Start, int End)> CollectSkipSpans(string text)
		{
			var spans = new List<(int Start, int End)>();
			foreach (Match m in EmailPattern().Each(text))
			{
				spans.Add((m.Index, m.Index + m.Length));
			}

			foreach (Match m in UrlOrDomainPattern().Each(text))
			{
				spans.Add((m.Index, m.Index + m.Length));
			}

			foreach (Match m in HtmlEntityPattern().Each(text))
			{
				spans.Add((m.Index, m.Index + m.Length));
			}

			return spans;
		}

		private static bool OverlapsAny(int start, int length, List<(int Start, int End)> spans)
		{
			int end = start + length;
			foreach (var s in spans)
			{
				if (start < s.End && s.Start < end)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Index of the first occurrence of <paramref name="word"/> in <paramref name="text"/> that
		/// is a WHOLE token under this tokenizer: it tokenizes the text with the real word pattern
		/// and returns the offset of the first token whose text equals <paramref name="word"/>. So
		/// "Adress" is found in "Adress Line" but NOT inside "Adressen" (one token) or "Adress-daten"
		/// (a hyphenated compound is one token, tight or spaced). Returns -1 when the word is not
		/// present as a whole token. The single locator shared by the excerpt builder, the raw-HTML
		/// triage view, and the per-occurrence highlight, so all three agree with what the tokenizer
		/// flagged instead of re-deriving with regex \b (which treats '-' as a boundary and matches
		/// inside a compound).
		/// </summary>
		internal static int IndexOfWholeWord(string text, string word, bool ignoreCase = false)
		{
			if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
			{
				return -1;
			}

			var comparison = ignoreCase ? System.StringComparison.OrdinalIgnoreCase : System.StringComparison.Ordinal;
			foreach (Match m in WordPattern().Each(text))
			{
				if (m.Value.Equals(word, comparison))
				{
					return m.Index;
				}
			}

			return -1;
		}

		internal static IEnumerable<string> TokenizeText(string text)
		{
			// The same words as Matches(text) → Value, without a Match object per word.
			var words = new List<string>();
			foreach (var m in WordPattern().EnumerateMatches(text))
			{
				words.Add(text.Substring(m.Index, m.Length));
			}

			return words;
		}

		/// <summary>
		/// The same words as <see cref="TokenizeText"/>, each with whether a hyphen follows it
		/// right there in the text ("Haus- und Garten"): the token itself never ends on a hyphen,
		/// so this is the only place that tells a suspended head from the same word elsewhere.
		/// </summary>
		internal static List<(string Word, bool HyphenFollows)> TokenizeTextWithFollowingHyphen(string text)
		{
			var words = new List<(string, bool)>();
			foreach (var m in WordPattern().EnumerateMatches(text))
			{
				int end = m.Index + m.Length;
				words.Add((text.Substring(m.Index, m.Length), end < text.Length && text[end] == '-'));
			}

			return words;
		}

		internal static bool IdentifyWord(string token)
		{
			return WordIdentifier().IsMatch(token);
		}

		// The single source for the word-token pattern, shared by Tokenize (located occurrences) and TokenizeText (the SpellChecker verdict).
		// A spaced dash ("Text – mehr", canonicalised to "Text - mehr") separates two words; as
		// one token with spaces inside it would fail the word check and leave every word next to
		// a spaced dash unchecked. Hyphens inside a word ("E-Mail") stay
		// part of the token through the character class. \p{M} keeps combining marks (Devanagari,
		// Tamil vowel signs) inside their word instead of splitting it.
		// The token ends on a word character or mark that is not followed by another one (the
		// trailing \b would otherwise cut a spacing mark such as U+093F off the word's end).
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex WordPatternRegex = new(@"\b[\w\p{M}'äöüßÄÖÜ-]*[\w\p{M}](?![\w\p{M}])|[.,!?;:()""[\]<>]", RegexOptions.Compiled);

		private static Regex WordPattern() => WordPatternRegex;

		// Email shape: local-part @ domain with a dotted TLD. Used only to SUPPRESS word tokens that
		// fall within an email span (never to validate addresses), so it is deliberately permissive
		// on the local part and conservative in requiring a dotted, alphabetic TLD.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex EmailPatternRegex = new(@"[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)*\.[a-zA-Z]{2,}", RegexOptions.Compiled);

		private static Regex EmailPattern() => EmailPatternRegex;

		// URL / bare domain in text. Three unambiguous forms, all optionally followed by a path:
		//   1. scheme://…            (http, https)
		//   2. www.host.tld          (the www. prefix is never prose)
		//   3. host(.sub)*.<TLD>     (bare domain anchored on a KNOWN TLD)
		// The fixed TLD list (case-insensitive) is the safety anchor: it excludes German abbreviations
		// ("z.B.", "e.g.", "d.h.") and decimals because their trailing segment is not a TLD. The list
		// is intentionally explicit (NOT \w{2,}) so nothing accidentally matches an abbreviation. Used
		// only to SUPPRESS overlapping word tokens, never to validate URLs.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex UrlOrDomainPatternRegex = new(@"\b(?:https?://[^\s]+|(?:www\.)[\w-]+(?:\.[\w-]+)*|[\w-]+(?:\.[\w-]+)*\.(?:de|com|org|net|info|eu|biz|xyz|io|app|gov|edu|at|ch|fr|it|es|nl|uk|co))\b(?:/[^\s]*)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static Regex UrlOrDomainPattern() => UrlOrDomainPatternRegex;

		// An HTML character reference that SURVIVED canonicalization. Canonicalizer decodes ONCE, on
		// purpose (see its class note), so a double-encoded source — "&amp;nbsp;" in the markup —
		// leaves the LITERAL TEXT "&nbsp;" in the run. The word pattern then emits the entity NAME
		// as a word: "nbsp", "quot", "amp", "shy", "mdash". No dictionary contains those, so every
		// one becomes a spelling finding for what is really an encoding defect — the detection is
		// right, the classification is wrong, and the operator is invited to "fix" a misspelling
		// that does not exist. Worse, the obvious way to silence the noise is a dictionary entry,
		// which would blind the tool to the whole class permanently.
		//
		// Suppressing by SPAN rather than by editing the word pattern (exactly as the email and URL
		// spans above do) has a property that matters here: the entity STAYS VISIBLE IN THE EXCERPT.
		// The operator still reads "&nbsp;" in context, so the evidence survives for the
		// content-quality checks that report the encoding defect under its own name.
		//
		// Matches named (&nbsp;), decimal (&#160;) and hex (&#xA0;) forms. The named form requires
		// at least two characters before ';' so prose like "R&D;" cannot match, and the ASCII-only
		// body means "Fischer&Söhne;" cannot match either.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex HtmlEntityPatternRegex = new(@"&(?:[a-zA-Z][a-zA-Z0-9]{1,31}|#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6});", RegexOptions.Compiled);

		private static Regex HtmlEntityPattern() => HtmlEntityPatternRegex;

		// Accept a word token in ANY script the dictionaries cover: \p{L} = any
		// Unicode letter (Latin incl. diacritics, Cyrillic, Greek, Arabic, …),
		// \p{M} = combining marks (so an NFD-decomposed diacritic passes the gate).
		// Still rejects digits, underscore and symbols; internal hyphens only.
		// A token that clears this gate is handed to the script-agnostic dictionary
		// (Hunspell Bundle.Check) for the actual spelling decision.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex WordIdentifierRegex = new(@"^[\p{L}\p{M}-]+(-[\p{L}\p{M}]+)*$", RegexOptions.Compiled);

		private static Regex WordIdentifier() => WordIdentifierRegex;
	}
}
