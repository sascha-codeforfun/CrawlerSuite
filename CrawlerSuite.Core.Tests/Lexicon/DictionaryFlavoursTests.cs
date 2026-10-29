using System.Text.Json.Nodes;
using CrawlerSuite.Core.Html;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.Settings;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Lexicon
{
	/// <summary>
	/// Languages and their flavours: a page naming a flavour gets it, one naming only the language
	/// gets the flavour marked Primary; codes are compared as language tags.
	/// </summary>
	public class DictionaryFlavoursTests
	{
		private static DictionaryFlavours English(bool gbPrimary = true, bool usPrimary = false) =>
			new([("de", false), ("en-GB", gbPrimary), ("en-US", usPrimary)]);

		[Theory]
		[InlineData("en-US", "en-US")]
		[InlineData("EN_us", "en-US")]
		[InlineData("en-GB", "en-GB")]
		[InlineData("en-UK", "en-GB")]   // the region is GB; UK is what authors write
		[InlineData("en", "en-GB")]      // the language alone: the Primary
		[InlineData("en-AU", "en-GB")]   // a flavour not loaded: the Primary
		[InlineData("de-CH", "de")]      // one dictionary: that one
		[InlineData("de", "de")]
		public void ATag_ResolvesToTheFlavourOrThePrimary(string tag, string key) =>
			Assert.Equal(key, English().Resolve(tag));

		[Fact]
		public void ALanguageNotLoaded_ResolvesToNothing() =>
			Assert.Null(English().Resolve("fr"));

		[Fact]
		public void ThePrimaryDecides_TheBareLanguage() =>
			Assert.Equal("en-US", English(gbPrimary: false, usPrimary: true).Resolve("en"));

		[Fact]
		public void NoPrimaryMarked_TheFirstListedIsUsed_AndNamed()
		{
			Assert.Equal("en-GB", English(gbPrimary: false).Resolve("en"));
			var problems = DictionaryFlavours.Problems([
				new DictionaryBundleConfig { LanguageCode = "en-GB" },
				new DictionaryBundleConfig { LanguageCode = "en-US" }]);
			Assert.Contains("needs to be marked Primary", Assert.Single(problems));
		}

		[Fact]
		public void Problems_NameCodesThatAreNoTags_TwoPrimaries_AndDoubles()
		{
			var problems = DictionaryFlavours.Problems([
				new DictionaryBundleConfig { LanguageCode = "enUS" },
				new DictionaryBundleConfig { LanguageCode = "de" },
				new DictionaryBundleConfig { LanguageCode = "de" },
				new DictionaryBundleConfig { LanguageCode = "pt-PT", Primary = true },
				new DictionaryBundleConfig { LanguageCode = "pt-BR", Primary = true }]);
			Assert.Contains(problems, p => p.Contains("\"enUS\" is no language tag"));
			Assert.Contains(problems, p => p.Contains("\"de\" is listed 2 times"));
			Assert.Contains(problems, p => p.Contains("only one may be marked Primary"));
		}

		[Fact]
		public void OneDictionaryPerLanguage_NeedsNoPrimary() =>
			Assert.Empty(DictionaryFlavours.Problems([
				new DictionaryBundleConfig { LanguageCode = "de" },
				new DictionaryBundleConfig { LanguageCode = "en-GB" }]));

		[Theory]
		[InlineData("<html lang=\"en-US\">", "en-us")]
		[InlineData("<html lang=\"en-UK\">", "en-gb")]
		[InlineData("<html lang=\"de\">", "de")]
		[InlineData("<html>", "fallback")]
		public void ThePageTag_KeepsItsFlavour(string html, string tag)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html + "<body><p>x</p></body></html>");
			Assert.Equal(tag, Language.TagFromMeta(doc, "fallback"));
		}

		[Fact]
		public void AnElementTag_KeepsItsFlavour()
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html lang=\"de\"><body><p lang=\"en-US\"><b id=\"t\">x</b></p></body></html>");
			Assert.Equal("en-us", Language.NearestElementTag(doc.GetElementbyId("t"), "de"));
			Assert.Equal("en", Language.NearestElementLanguage(doc.GetElementbyId("t"), "de")); // the other checks: the language alone
		}

		[Fact]
		public void EnglishForLoanParts_IsThePrimary()
		{
			var gb = new Bundle { Language = "en-GB" };
			var us = new Bundle { Language = "en-US", IsPrimary = true };
			var english = CrawlerSuite.Core.SpellCheck.ToolsSpellChecker.EnglishOf(
				new Dictionary<string, Bundle> { ["de"] = new Bundle(), ["en-GB"] = gb, ["en-US"] = us });
			Assert.Same(us, english);
		}

		[Fact]
		public void TheSettingsOffer_TheLanguageBeforeItsFlavours()
		{
			var bundles = JsonNode.Parse("[{\"LanguageCode\":\"de\"},{\"LanguageCode\":\"en-GB\"},{\"LanguageCode\":\"en-US\"}]");
			Assert.Equal(new[] { "de", "en", "en-GB", "en-US" }, SettingEditors.BundleLanguages(bundles).ToArray());
		}

		[Theory]
		[InlineData("enUS", false)]
		[InlineData("en-US", true)]
		[InlineData("de", true)]
		[InlineData("en-UK", true)]
		public void ATableRow_IsCheckedForATag(string code, bool ok)
		{
			var row = new Dictionary<string, string> { ["LanguageCode"] = code };
			Assert.Equal(ok, TableSpecs.DictionaryBundles.Check!(row) is null);
		}
	}
}
