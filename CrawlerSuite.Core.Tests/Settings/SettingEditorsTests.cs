using System.Text.Json.Nodes;
using CrawlerSuite.Core.Settings;
using Xunit;

namespace CrawlerSuite.Core.Tests.Settings
{
	/// <summary>The settings that have editors of their own: switchable lists, tags, XPaths, boilerplate, languages.</summary>
	public class SettingEditorsTests
	{
		[Fact]
		public void Entries_RoundTrip_SwitchedOffAsObject_ActiveAsText()
		{
			var value = JsonNode.Parse("""["//a", {"Value":"//b","Enabled":false}, {"Value":"//c"}]""");
			Assert.True(SettingEditors.FitsEntries(value));
			var entries = SettingEditors.ReadEntries(value);
			Assert.Equal(new[] { true, false, true }, entries.Select(e => e.Enabled).ToArray());
			Assert.Equal("""["//a",{"Value":"//b","Enabled":false},"//c"]""", SettingEditors.WriteEntries(entries).ToJsonString());
			Assert.False(SettingEditors.FitsEntries(JsonNode.Parse("[1]")));
		}

		[Theory]
		[InlineData("svg", true)]
		[InlineData("my-widget", true)]
		[InlineData("h1", true)]
		[InlineData("<svg>", false)]
		[InlineData("1x", false)]
		[InlineData("a b", false)]
		public void TagNames(string name, bool valid) => Assert.Equal(valid, SettingEditors.CheckTagName(name) is null);

		[Fact]
		public void Xpath_Checked_ByTheRunsParser()
		{
			Assert.Null(SettingEditors.CheckXpath("//*[contains(@id, 'x-')]"));
			Assert.NotNull(SettingEditors.CheckXpath("//a["));
			Assert.NotNull(SettingEditors.CheckXpath(" "));
		}

		[Theory]
		[InlineData("//img[@id='px']", "Matches any img element whose id is exactly 'px'.")]
		[InlineData("//*[contains(@id, 'x-')]", "Matches any element whose id contains 'x-'.")]
		[InlineData("//div[starts-with(@class,'ad')]", "Matches any div element whose class starts with 'ad'.")]
		[InlineData("//link", "Matches any link element, anywhere on the page.")]
		[InlineData("//div/span[2]", null)]
		public void Xpath_InPlainWords_ForSimpleShapes(string xpath, string? words) =>
			Assert.Equal(words, SettingEditors.DescribeXpath(xpath));

		[Fact]
		public void Xpath_Pieces_CoverTheWholeText()
		{
			const string xpath = "//*[contains(@id, 'x-')]";
			var pieces = SettingEditors.SplitXpath(xpath);
			Assert.Equal(xpath, string.Concat(pieces.Select(p => p.Text)));
			Assert.Contains(pieces, p => p.Part == SettingEditors.XpathPart.Function && p.Text == "contains");
			Assert.Contains(pieces, p => p.Part == SettingEditors.XpathPart.Attribute && p.Text == "@id");
			Assert.Contains(pieces, p => p.Part == SettingEditors.XpathPart.Text && p.Text == "'x-'");
		}

		[Fact]
		public void Boilerplate_Parts_AndShape()
		{
			Assert.Null(SettingEditors.CheckPart("class", "nav main"));
			Assert.NotNull(SettingEditors.CheckPart("class", ".nav"));
			Assert.NotNull(SettingEditors.CheckPart("xpath", "//a["));
			Assert.NotNull(SettingEditors.CheckPart("id", "x"));
			Assert.True(SettingEditors.FitsBoilerplate(JsonNode.Parse("""[{"PathPrefix":"/","PagesToCheckBoiler":["/a"],"BoilerplateSelectors":[{"Type":"class","Value":"x"}]}]""")));
			Assert.False(SettingEditors.FitsBoilerplate(JsonNode.Parse("""[{"PagesToCheckBoiler":"/a"}]""")));
		}

		[Fact]
		public void BundleLanguages_FromTheBundlesValue()
		{
			var bundles = JsonNode.Parse("""[{"LanguageCode":"de"},{"LanguageCode":"en"},{"LanguageCode":"DE"},{"DisplayName":"x"}]""");
			Assert.Equal(new[] { "de", "en" }, SettingEditors.BundleLanguages(bundles).ToArray());
		}

		[Fact]
		public void Catalog_SpellExclusionSettingIsGone_LanguagesByPageTakesItsPlace()
		{
			Assert.Null(SettingsCatalog.Find("SpellCheckExcludedUrls"));
			Assert.Equal(CellKind.Languages, TableSpecs.PageLanguageOverrides.Columns[1].Kind);
			Assert.True(SettingsCatalog.Find("SpellCheckEngine.DefaultLanguage")!.ChoicesFromBundles);
		}
	}
}
