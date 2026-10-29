using System.Text;
using CrawlerSuite.Core.Html;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests.Lexicon
{
	/// <summary>
	/// "Valid in": a flagged word another reference dictionary knows is named with it in log 11
	/// (information only, the word stays flagged); the readers split it off again.
	/// </summary>
	public class ValidInTests
	{
		private static Bundle Dictionary(string language, params string[] words) => new()
		{
			Language = language,
			System = WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes(words.Length + "\n" + string.Join("\n", words) + "\n")),
				new MemoryStream(Encoding.UTF8.GetBytes("SET UTF-8\n"))),
		};

		private static Dictionary<string, Bundle> Bundles() => new(StringComparer.OrdinalIgnoreCase)
		{
			["de"] = Dictionary("de", "wasser", "molekül"),
			["en-GB"] = Dictionary("en-GB", "colour", "water"),
			["en-US"] = Dictionary("en-US", "color", "water"),
			["fr"] = Dictionary("fr", "eau"),
		};

		[Fact]
		public void AWordFlaggedInGerman_IsNamedValidInTheEnglishThatKnowIt() =>
			Assert.Equal(new[] { "en-GB", "en-US" }, NewSpellEngineRunner.ValidIn(Bundles(), "de")("water", "de"));

		[Fact]
		public void AWordFlaggedInOneFlavour_IsNamedValidInTheOther() =>
			Assert.Equal(new[] { "en-GB" }, NewSpellEngineRunner.ValidIn(Bundles(), "de")("colour", "en-US"));

		[Fact]
		public void AWordFlaggedInEnglish_IsNamedValidInTheDefaultLanguage_WithItsCapitalDropped() =>
			Assert.Equal(new[] { "de" }, NewSpellEngineRunner.ValidIn(Bundles(), "de")("Wasser", "en-GB"));

		[Fact]
		public void OtherLanguages_AreNoReference() =>
			Assert.Empty(NewSpellEngineRunner.ValidIn(Bundles(), "de")("eau", "de"));

		[Fact]
		public void AWordNoReferenceKnows_GetsNoSuffix()
		{
			var tickets = new[]
			{
				new WordTicket("Wassre", "http://x/a", "de", []),
				new WordTicket("water", "http://x/b", "de", []),
			};

			var lines = ParallelStoreWriter.UniqueView(tickets, NewSpellEngineRunner.ValidIn(Bundles(), "de"))
				.Split('\n', StringSplitOptions.RemoveEmptyEntries);

			Assert.Contains("Wassre (de)", lines);
			Assert.Contains("water (de) · valid en-GB, en-US", lines);
		}

		[Theory]
		[InlineData("water (de) · valid en-GB, en-US", "water (de)", "en-GB|en-US")]
		[InlineData("Wassre (de)", "Wassre (de)", "")]
		[InlineData("H2O · valid", "H2O · valid", "")]
		public void TheSuffix_IsSplitOff(string line, string head, string valid)
		{
			var (rest, validIn) = ParallelStoreWriter.SplitValidIn(line);
			Assert.Equal(head, rest);
			Assert.Equal(valid, string.Join("|", validIn));
		}

		[Fact]
		public void TheSpellFindingsList_ReadsTheSuffix()
		{
			var folder = Directory.CreateTempSubdirectory("validin-").FullName;
			try
			{
				File.WriteAllText(Path.Combine(folder, LogFileNames.SpellErrorsUnique), "water (de) · valid en-GB, en-US\nWassre (de)\n");
				File.WriteAllText(Path.Combine(folder, LogFileNames.SpellErrorsSources), "http://x/a|water (de)\nhttp://x/b|water (de)\n");

				var words = SpellWordList.Read(folder);

				var water = Assert.Single(words, w => w.Word == "water");
				Assert.Equal(new[] { "de" }, water.Languages);
				Assert.Equal(new[] { "en-GB", "en-US" }, water.ValidIn);
				Assert.Equal(2, water.Pages);
				Assert.Empty(Assert.Single(words, w => w.Word == "Wassre").ValidIn);
			}
			finally
			{
				Directory.Delete(folder, recursive: true);
			}
		}

		[Fact]
		public void ContentChecks_SeeBaseLanguagesOnly() =>
			Assert.Equal(new[] { "en", "de" }, PageLanguageSet.Languages(["en-gb", "en-us", "de", "de-ch"]));
	}
}
