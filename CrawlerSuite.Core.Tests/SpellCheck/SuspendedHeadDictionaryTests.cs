using System.Text;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// German dictionaries of the igerman98 family list a suspended compound head as a word WITH
	/// its hyphen: the bare form carries NEEDAFFIX (not a word on its own) and a suffix rule adds
	/// "Head-" as a word. These rules are written for this test (same mechanism, own entries), so
	/// the test pins the mechanism the engine relies on, not a copy of any dictionary.
	/// </summary>
	public class SuspendedHeadDictionaryTests
	{
		private const string Aff =
			"SET UTF-8\n" +
			"COMPOUNDBEGIN x\n" +
			"COMPOUNDEND z\n" +
			"ONLYINCOMPOUND o\n" +
			"COMPOUNDPERMITFLAG c\n" +
			"NEEDAFFIX h\n" +
			"SFX j Y 2\n" +
			"SFX j 0 0/xoc .\n" +
			"SFX j 0 -/cz .\n";

		// "Schul" and "Hilfs" exist only as compound heads (a letter of the full word is dropped or
		// a linking s is added), so neither the bare form nor a stripped linking element is a word.
		private const string Dic = "4\nSchul/hij\nHilfs/hij\nStudienreise\nund\n";

		private static Bundle Bundle() => new()
		{
			System = WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes(Dic)),
				new MemoryStream(Encoding.UTF8.GetBytes(Aff))),
		};

		[Fact]
		public void Dictionary_ListsTheHeadWithItsHyphen_NotTheBareHead()
		{
			var bundle = Bundle();
			Assert.True(bundle.Check("Schul-"));
			Assert.False(bundle.Check("Schul"));
			Assert.False(bundle.Check("Schhul-"));
		}

		[Fact]
		public void TrailingHyphenCheck_AsksForTheHyphenForm()
		{
			var bundle = Bundle();
			Assert.True(SpellChecker.CheckTrailingHyphenStem("Schul-", bundle, ["s", "n"]));
			Assert.True(SpellChecker.CheckTrailingHyphenStem("Hilfs-", bundle, ["s", "n"]));
			Assert.False(SpellChecker.CheckTrailingHyphenStem("Schhul-", bundle, ["s", "n"]));
		}

		[Fact]
		public void SuspendedHead_InText_IsNotAFinding_ATypoIs()
		{
			var bundle = Bundle();
			var ok = SpellChecker.Check("Schul- und Studienreise", bundle, "de", [], ["s", "n"]);
			Assert.Empty(ok);

			var typo = SpellChecker.Check("Schhul- und Studienreise", bundle, "de", [], ["s", "n"]);
			Assert.Equal(new[] { "Schhul" }, typo.Select(kv => kv.Key));
		}
	}
}
