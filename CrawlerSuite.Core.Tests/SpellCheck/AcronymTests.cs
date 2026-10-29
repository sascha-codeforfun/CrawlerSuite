using System.Text;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>Ignore acronyms: which words count, how they are skipped, and the review list (log 37).</summary>
	public class AcronymTests
	{
		private static readonly AcronymRule Three = new(3, PluralS: true);

		[Theory]
		[InlineData("A", true)]
		[InlineData("AB", true)]
		[InlineData("ABC", true)]
		[InlineData("ÄÖÜ", true)]
		[InlineData("ABCD", false)] // longer than 3: checked
		[InlineData("ABCs", true)] // plural s, not counted
		[InlineData("ABCDs", false)]
		[InlineData("Is", false)] // one capital before the s: a word, checked
		[InlineData("Abc", false)] // mixed case: checked
		[InlineData("AbC", false)]
		[InlineData("B2B", false)] // digits: checked
		[InlineData("A-B", false)] // the hyphenated word is not one acronym; its parts are
		[InlineData("", false)]
		public void Rule_Length3_PluralS(string word, bool matches)
		{
			Assert.Equal(matches, Three.Matches(word));
		}

		[Fact]
		public void Rule_PluralS_Off_ChecksThePlural()
		{
			var rule = new AcronymRule(3, PluralS: false);
			Assert.True(rule.Matches("ABC"));
			Assert.False(rule.Matches("ABCs"));
		}

		[Theory]
		[InlineData(1, "A", true)]
		[InlineData(1, "AB", false)]
		[InlineData(5, "ABCDE", true)]
		[InlineData(5, "ABCDEF", false)]
		public void Rule_LengthBoundaries(int max, string word, bool matches)
		{
			Assert.Equal(matches, new AcronymRule(max, PluralS: true).Matches(word));
		}

		[Fact]
		public void From_Config_OffMeansNoRule_AndTheLengthIsClamped()
		{
			Assert.Null(AcronymRule.From(new SpellCheckEngineConfig { IgnoreAcronyms = false }));
			var defaults = AcronymRule.From(new SpellCheckEngineConfig())!;
			Assert.Equal(3, defaults.MaxLength);
			Assert.True(defaults.PluralS);
			Assert.Equal(5, AcronymRule.From(new SpellCheckEngineConfig { AcronymMaxLength = 9 })!.MaxLength);
			Assert.Equal("up to 3 letters, plural s", defaults.Describe());
		}

		private static Bundle German() => new()
		{
			System = WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes("2\nScholle\nHaus\n")),
				new MemoryStream(Encoding.UTF8.GetBytes("SET UTF-8\n"))),
			Language = "xx",
		};

		[Fact]
		public void Prepare_SkipsAcronyms_AndNotesThemOnce()
		{
			const string text = "Das ABC und ABCs im XYZ-Scholle, dazu ABCD und QRS- und Haus.";
			var collector = new AcronymCollector();
			var words = SpellChecker.Prepare(text, [], Three, collector).Select(w => w.Word).ToList();

			Assert.DoesNotContain("ABC", words);
			Assert.DoesNotContain("ABCs", words);
			Assert.Contains("ABCD", words); // longer: checked
			Assert.Contains("XYZ-Scholle", words); // the word stays; its acronym part counts as known
			Assert.Equal(["ABC", "ABCs", "XYZ", "QRS"], collector.Found.Select(f => f.Acronym));
			Assert.Contains("Das ABC und", collector.Found[0].Excerpt);

			// Without the rule nothing is skipped.
			Assert.Contains("ABC", SpellChecker.Prepare(text, []).Select(w => w.Word));
		}

		[Fact]
		public void HyphenatedWord_WithAnAcronymPart_PassesOnlyWithTheRule()
		{
			var bundle = German();
			Assert.True(SpellChecker.HyphenatedPartsAccepted("XYZ-Scholle", bundle, null, [], [], Three));
			Assert.False(SpellChecker.HyphenatedPartsAccepted("XYZ-Scholle", bundle, null, [], []));
			Assert.False(SpellChecker.HyphenatedPartsAccepted("WXYZ-Scholle", bundle, null, [], [], Three));

			var parts = HyphenParts.Explain("XYZ-Kontx", bundle, null, [], [], Three)!;
			Assert.Equal([("XYZ", HyphenPartStatus.Acronym), ("Kontx", HyphenPartStatus.Unknown)], parts.Select(p => (p.Text, p.Status)));
		}

		[Fact]
		public void List_SortsLookAlikesTogether_CountsPages_AndKeepsTheFirstExcerpt()
		{
			var page1 = new AcronymCollector();
			page1.Add("PQR", "first PQR here");
			page1.Add("ABC", "one ABC");
			var page2 = new AcronymCollector();
			page2.Add("PRQ", "the PRQ typo");
			page2.Add("ABC", "two ABC");
			page2.Add("ZZs", "ZZs plural");

			var lines = AcronymList.Lines([("p1", page1), ("p2", page2), ("p3", null)], Three);
			var rows = lines.Where(l => !l.StartsWith('#')).ToList();

			Assert.StartsWith("# 37 — acronyms not spell-checked (up to 3 letters, plural s): 4 distinct", lines[0]);
			Assert.Equal(
				["ABC\t2\tone ABC", "PQR\t1\tfirst PQR here", "PRQ\t1\tthe PRQ typo", "ZZs\t1\tZZs plural"],
				rows);
		}

		[Fact]
		public void Collector_ShortensTheExcerpt_AndCollapsesWhitespace()
		{
			var collector = new AcronymCollector();
			var text = new string('x', 100) + " \n ABC \t " + new string('y', 100);
			collector.Add("ABC", text);
			var excerpt = collector.Found.Single().Excerpt;
			Assert.StartsWith("…", excerpt);
			Assert.EndsWith("…", excerpt);
			Assert.Contains(" ABC ", excerpt);
			Assert.DoesNotContain("\n", excerpt);
		}

		[Fact]
		public void Settings_HaveTheAcronymRows_OnTheSpellingTab()
		{
			var length = CrawlerSuite.Core.Settings.SettingsCatalog.Find("SpellCheckEngine.AcronymMaxLength")!;
			Assert.Equal("Spelling", length.Tab);
			Assert.Equal(1, length.Min);
			Assert.Equal(5, length.Max);
			Assert.Equal(CrawlerSuite.Core.Settings.RowExtra.Slider, length.Extra);
			Assert.Equal("SpellCheckEngine.IgnoreAcronyms", length.ActiveWhen);
			Assert.NotNull(CrawlerSuite.Core.Settings.SettingsCatalog.Find("SpellCheckEngine.IgnoreAcronyms"));
			Assert.Equal("SpellCheckEngine.IgnoreAcronyms", CrawlerSuite.Core.Settings.SettingsCatalog.Find("SpellCheckEngine.AcronymPluralS")!.ActiveWhen);
		}
	}
}
