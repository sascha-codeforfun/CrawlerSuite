using CrawlerSuite.Core.SpellCheck;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Pins SpellTokenizer.IndexOfWholeWord — the single locator shared by the excerpt builder, the
	/// raw-HTML triage view, and the per-occurrence highlight. It must agree with the tokenizer: a
	/// flagged word is "present" only where it stands as a whole token, never as a substring of a
	/// longer word or a fragment of a hyphenated compound (tight or spaced).
	/// </summary>
	public class SpellTokenizerWholeWordTests
	{
		[Fact]
		public void StandaloneWord_IsFound()
		{
			Assert.Equal(8, SpellTokenizer.IndexOfWholeWord("im Feld Adress Line", "Adress"));
		}

		[Fact]
		public void SubstringInLongerWord_IsNotMatched()
		{
			// "Adress" is only a prefix of "Adressen" — not a whole token.
			Assert.Equal(-1, SpellTokenizer.IndexOfWholeWord("unstrukturierte Adressen hier", "Adress"));
		}

		[Fact]
		public void TightHyphenCompound_IsNotMatched()
		{
			// The tokenizer keeps "Adress-daten" as one token; "Adress" is not a whole token in it.
			Assert.Equal(-1, SpellTokenizer.IndexOfWholeWord("Angabe von Adress-daten.", "Adress"));
		}

		[Fact]
		public void SpacedDash_SeparatesWords()
		{
			// A spaced dash is punctuation between two words: "Adress"
			// before " - daten" is a whole word of its own, so it is found and spell-checked.
			Assert.Equal(11, SpellTokenizer.IndexOfWholeWord("Angabe von Adress - daten.", "Adress"));
		}

		[Fact]
		public void PrefersStandalone_OverEarlierCompoundAndSubstring()
		{
			// "Adress-daten" (compound) and "Adressen" (substring) come first and must be skipped;
			// the index returned is the standalone "Adress" in "Adress Line".
			var text = "Adress-daten und Adressen, dann im Feld Adress Line";
			int at = SpellTokenizer.IndexOfWholeWord(text, "Adress");
			Assert.Equal(text.IndexOf("Adress Line", System.StringComparison.Ordinal), at);
		}

		[Fact]
		public void CaseInsensitive_MatchesOnlyWhenRequested()
		{
			Assert.Equal(0, SpellTokenizer.IndexOfWholeWord("ADRESS Line", "Adress", ignoreCase: true));
			Assert.Equal(-1, SpellTokenizer.IndexOfWholeWord("ADRESS Line", "Adress"));
		}

		[Fact]
		public void AbsentWord_ReturnsMinusOne()
		{
			Assert.Equal(-1, SpellTokenizer.IndexOfWholeWord("nichts hier", "Adress"));
		}
	}
}
