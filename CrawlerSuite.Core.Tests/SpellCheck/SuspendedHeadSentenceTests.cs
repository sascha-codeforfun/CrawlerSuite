using System.Text;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;
using HtmlAgilityPack;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// A sentence pair: "… an Sonn- und Fiertagen" (deliberate typo) and "… an Sonn- und
	/// Feiertagen". The suspended head must stand or fall by itself: a typo in its partner does not
	/// make it fail, the correct partner is not what makes it pass, and the typo is flagged on its own.
	/// </summary>
	public class SuspendedHeadSentenceTests
	{
		private const string WithTypo = "Der Hauseingang ist verschlossen Hauseingänge / -ausgänge sollten immer verschlossen sein - auch an Sonn- und Fiertagen.";
		private const string Correct = "Der Hauseingang ist verschlossen Hauseingänge / -ausgänge sollten immer verschlossen sein - auch an Sonn- und Feiertagen.";

		// A small dictionary built the way the German one lists suspended heads: "Sonn" is no word
		// on its own (NEEDAFFIX), and a suffix rule makes "Sonn-" one.
		private static Bundle German()
		{
			const string aff = "SET UTF-8\nNEEDAFFIX n\nSFX j Y 1\nSFX j 0 - .\n";
			string[] words = ["Der", "Hauseingang", "ist", "verschlossen", "Hauseingänge", "ausgänge", "sollten", "immer", "sein", "auch", "an", "und", "Feiertagen", "Sonn/jn"];
			var dic = words.Length + "\n" + string.Join("\n", words) + "\n";
			return new Bundle
			{
				System = WordList.CreateFromStreams(new MemoryStream(Encoding.UTF8.GetBytes(dic)), new MemoryStream(Encoding.UTF8.GetBytes(aff))),
				Language = "de",
			};
		}

		private static List<string> Misses(string text, Bundle bundle) =>
			SpellChecker.CheckPrepared(text, SpellChecker.Prepare(text, []), bundle, "de", ["s"]).Keys.ToList();

		[Fact]
		public void TheDictionaryKnowsTheHead_AndNotTheBareStem()
		{
			var german = German();
			Assert.True(german.System.Check("Sonn-"));
			Assert.False(german.System.Check("Sonn"));
		}

		[Fact]
		public void TheHead_StandsByItself_TheTypoIsFlaggedByItself()
		{
			var german = German();
			Assert.Equal(["Fiertagen"], Misses(WithTypo, german));
			Assert.Empty(Misses(Correct, german));
		}

		// A dictionary that does not list "Sonn-" (as the real German one does not): the head is
		// rescued by borrowing its partner's tail ("Sonn" + "tagen" = "Sonntagen"). The borrowed tail
		// is intact in both sentences, so the head passes in both; the typo is still flagged.
		private static readonly HashSet<string> WithoutTheHead = new(StringComparer.Ordinal)
		{
			"Der", "Hauseingang", "ist", "verschlossen", "Hauseingänge", "ausgänge", "sollten", "immer", "sein", "auch", "an", "und",
			"Feiertagen", "Sonntagen",
		};

		private static RunCheck Stub() => (text, language) =>
			SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text))
				.Select(t => t.Text)
				.Where(w => w.Any(char.IsLetter) && !WithoutTheHead.Contains(w))
				.Distinct()
				.Select(w => new CheckMiss(w, string.Empty));

		[Theory]
		[InlineData(WithTypo, new[] { "Fiertagen" })]
		[InlineData(Correct, new string[0])]
		public void WithoutTheHeadInTheDictionary_ItBorrowsItsPartnersTail(string sentence, string[] flagged)
		{
			var run = new TextRun(HtmlNode.CreateNode("<p>" + sentence + "</p>"), RunSource.TextNode, "p[#text]", sentence);
			Assert.Equal(flagged, RunChecker.Check(run, "de", Stub()).Select(f => f.Word).Distinct().ToArray());
		}
	}
}
