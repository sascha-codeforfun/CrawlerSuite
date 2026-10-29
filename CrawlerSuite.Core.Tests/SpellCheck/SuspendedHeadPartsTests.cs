using System.Text;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// A word before a hyphen ("Xyzzy- und Haus") is a suspended compound head. Its parts count
	/// as in any hyphenated word, and whether a hyphen follows is decided for each occurrence —
	/// not for the whole text. Invented words only.
	/// </summary>
	public class SuspendedHeadPartsTests
	{
		private static WordList List(params string[] words) =>
			WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes(words.Length + "\n" + string.Join("\n", words) + "\n")),
				new MemoryStream(Encoding.UTF8.GetBytes("SET UTF-8\n")));

		private static Bundle German() => new() { System = List("Haus", "und", "Zeitung"), Language = "xx" };

		private static Bundle English() => new()
		{
			System = List("with", "their", "new", "that", "fits", "much", "better", "and", "gives", "you", "the", "unique", "shirt", "feel", "brand", "online"),
			Language = "en",
		};

		private static readonly string[] Fugen = ["s"];

		private static IReadOnlyCollection<string> Misses(string text, Bundle bundle, string[] known, Bundle? english = null, AcronymRule? acronyms = null) =>
			SpellChecker.CheckPrepared(text, SpellChecker.Prepare(text, known, acronyms), bundle, bundle.Language, Fugen, english, known, acronyms).Keys;

		[Theory]
		[InlineData("Q-Xyzzyland Haus")]
		[InlineData("Q-Xyzzyland- und Haus")]                // a suspended head made of known word parts
		[InlineData("Q-Xyzzyland-Haus und Q-Xyzzyland")]     // the plain word next to a longer one
		[InlineData("Xyzzyland- und Haus")]                  // a known word part alone before the hyphen
		[InlineData("Q-Zeitungs- und Haus")]                 // the last part a compound head with its linking element
		public void SuspendedHead_OfKnownParts_Passes(string text)
		{
			Assert.Empty(Misses(text, German(), ["Q", "Xyzzyland"]));
		}

		[Theory]
		[InlineData("Q-Xyzzylnd- und Haus", "Q-Xyzzylnd")]
		[InlineData("Qwzz- und Haus", "Qwzz")]
		[InlineData("Q-Qwzz- und Haus", "Q-Qwzz")]
		public void SuspendedHead_WithAnUnknownPart_StaysFlagged(string text, string flagged)
		{
			Assert.Equal([flagged], Misses(text, German(), ["Q", "Xyzzyland"]));
		}

		[Fact]
		public void SuspendedHead_TakesEnglishPartsAndAcronyms_LikeAnyHyphenatedWord()
		{
			Assert.Empty(Misses("online- und Haus", German(), [], English()));
			Assert.Equal(["online"], Misses("online- und Haus", German(), []));
			Assert.Empty(Misses("XYZ-Haus- und Zeitung", German(), [], acronyms: new AcronymRule(3, true)));
		}

		[Fact]
		public void TheSameWord_IsASuspendedHeadOnlyWhereAHyphenFollowsIt()
		{
			// The lone "P" at the end is a word of its own, though "P-" stands earlier in the text.
			// Acronyms are checked here, so the single letter meets the dictionary.
			const string text = "Phantastic-Brand with their new P-Shirt that fits much better and gives you the unique P-Feel :P";
			var english = English();
			english.SharedUser.Add("P");
			Assert.Equal(["Phantastic-Brand"], Misses(text, english, []));
			Assert.Empty(Misses(text, english, ["Phantastic"]));
			Assert.Empty(Misses(text, english, ["Phantastic-Brand"]));
		}

		[Fact]
		public void TheLoneLetter_FailingTheDictionary_IsFlaggedAsAWord_NotAsAHead()
		{
			// Without an entry for "P", the lone letter is flagged — as an ordinary word.
			var english = English();
			Assert.Equal(["Phantastic-Brand", "P"], Misses("Phantastic-Brand P-Shirt :P", english, []));
			Assert.Equal(["P"], Misses("Phantastic-Brand P-Shirt :P", english, ["Phantastic"]));
		}

		[Theory]
		[InlineData("Q-Xyzzyland")]
		[InlineData("Xyzzyland")]
		[InlineData("Haus")]
		[InlineData("Q-Haus")]
		[InlineData("Q-Qwzz")]
		[InlineData("Qwzz-Haus")]
		[InlineData("Haus-Zeitungs")]
		[InlineData("online")]
		public void SuspendedHead_IsNeverStricterThanTheWord(string word)
		{
			string[] known = ["Q", "Xyzzyland"];
			bool asWord = Misses(word + " und Haus", German(), known, English()).Count == 0;
			bool asHead = Misses(word + "- und Haus", German(), known, English()).Count == 0;
			Assert.True(!asWord || asHead, $"{word}: accepted as a word, rejected before a hyphen");
		}

		[Theory]
		[InlineData("Q-Xyzzylnd- und Haus")]
		[InlineData("Q-Qwzz- und Haus")]
		[InlineData("Qwzz-Haus und Haus")]
		[InlineData("Haus-Zeitungs und Haus")]
		public void AFlaggedHyphenatedWord_IsNeverExplainedAsAllKnown(string text)
		{
			// Log 36 explains a word with the ordinary walk; the suspended-head walk is never
			// stricter, so whatever the check flagged shows at least one unknown part.
			string[] known = ["Q", "Xyzzyland"];
			var german = German();
			foreach (var word in Misses(text, german, known))
			{
				var parts = HyphenParts.Explain(word, german, null, known, Fugen);
				Assert.NotNull(parts);
				Assert.Contains(parts!, p => p.Status == HyphenPartStatus.Unknown);
			}
		}
	}
}
