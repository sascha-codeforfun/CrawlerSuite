using System.Text.Json.Nodes;
using CrawlerSuite.Core.Settings;
using Xunit;

namespace CrawlerSuite.Core.Tests.Settings
{
	/// <summary>What the settings screen shows beyond plain fields: defaults in words, tables, placeholders, free space.</summary>
	public class SettingsScreenTests
	{
		[Fact]
		public void Defaults_AreNamed_NotEmpty()
		{
			var config = new Config();
			Assert.Equal("TrustByteSniff", config.UnverifiedHtmlPolicy);
			Assert.Equal("TrustByteSniff", config.UnverifiedPdfPolicy);
			Assert.Equal("TrustByteSniff", config.AssetQuality.UnverifiedImagePolicy);
			Assert.Equal("FlagAll", config.ContentQuality.CheckDecomposition);
			Assert.Equal(";", new CmsContentListConfig().ColumnDelimiter);

			// Older files: empty and other casing still mean the same.
			config.UnverifiedHtmlPolicy = string.Empty;
			Assert.Equal(UnverifiedHtmlPolicy.TrustByteSniff, config.ResolvedUnverifiedHtmlPolicy);
			config.ContentQuality.CheckDecomposition = "off";
			Assert.Equal(DecompositionMode.Off, config.ContentQuality.ResolvedCheckDecomposition);
		}

		[Fact]
		public void Defaults_Choices_ContainTheirDefault_AndOldValuesShowAsTheirChoice()
		{
			foreach (var s in SettingsCatalog.All.Where(s => s.Kind == SettingKind.Choice && !s.ChoicesFromBundles))
			{
				Assert.Contains(SettingsCatalog.DefaultOf(s.Path)!.GetValue<string>(), s.Choices!);
			}

			var decomposition = SettingsCatalog.Find("ContentQuality.CheckDecomposition")!;
			Assert.Equal("Off", SettingValues.ToText(decomposition, JsonValue.Create("off")));
			Assert.Equal("FlagAll", SettingValues.ToText(decomposition, JsonValue.Create("")));
		}

		[Fact]
		public void Defaults_InWords()
		{
			string Of(string path) => SettingsCatalog.DescribeDefault(SettingsCatalog.Find(path)!);
			Assert.Equal("none — it must be set", Of("BaseDirectory"));
			Assert.Equal("All", Of("MaxDegreeOfParallelism"));
			Assert.Equal("the file's first column", Of("TicketGeneration.PathColumn"));
			Assert.Equal("150 MB", Of("MaxSingleFileMB"));
			Assert.Equal("none", Of("DownloadExclusions"));
			Assert.Equal("on", Of("ContentQuality.CheckLigatures"));
			Assert.Equal("TrustByteSniff", Of("UnverifiedHtmlPolicy"));
			Assert.Equal("built-in list of 2 entries", Of("SpellCheckEngine.MetaContentNamesToSpellCheck"));

			// Every setting says something better than an empty value or "decided by the app".
			foreach (var s in SettingsCatalog.All)
			{
				var text = SettingsCatalog.DescribeDefault(s);
				Assert.False(string.IsNullOrWhiteSpace(text), s.Path);
				Assert.NotEqual("decided by the app", text);
			}
		}

		[Fact]
		public void Defaults_BuiltInList_IsWhatUnlockingWrites()
		{
			var meta = SettingsCatalog.EffectiveDefaultOf("SpellCheckEngine.MetaContentNamesToSpellCheck") as JsonArray;
			Assert.NotNull(meta);
			Assert.Equal(new[] { "description", "keywords" }, meta!.Select(v => v!.GetValue<string>()).ToArray());
			Assert.True(((JsonArray)SettingsCatalog.EffectiveDefaultOf("SpellCheckEngine.GlobalNonProseHtmlAttributesThatWillBeIgnored")!).Count > 10);
		}

		[Fact]
		public void Table_Values_EmptyEntryAndBlankRows()
		{
			var spec = TableSpec.OneColumn;
			var rows = SettingTables.Read(spec, JsonNode.Parse("""["", "index"]"""));
			Assert.Equal(SettingTables.EmptyEntry, rows[0]["Value"]);
			rows.Add(SettingTables.NewRow(spec));   // added, nothing typed: not written
			Assert.Equal("""["","index"]""", SettingTables.Write(spec, rows).ToJsonString());
		}

		[Fact]
		public void Table_Records_KeepFieldsTheTableDoesNotShow()
		{
			var spec = TableSpecs.DownloadExclusions;
			var value = JsonNode.Parse("""[{"Value":"/archive/","Enabled":false,"Comment":"note","Extra":1}]""");
			Assert.True(SettingTables.Fits(spec, value));
			var rows = SettingTables.Read(spec, value);
			Assert.Equal("false", rows[0]["Enabled"]);
			rows[0]["Value"] = "/old/";
			var written = SettingTables.Write(spec, rows).ToJsonString();
			Assert.Equal("""[{"Value":"/old/","Enabled":false,"Comment":"note","Extra":1}]""", written);

			var fresh = SettingTables.NewRow(spec);
			Assert.Equal("true", fresh["Enabled"]);
		}

		[Fact]
		public void Table_Records_ChecksAndExample()
		{
			var exclusions = TableSpecs.DownloadExclusions;
			var row = SettingTables.NewRow(exclusions);
			row["Comment"] = "typed only a note";
			Assert.Contains("would exclude every link", SettingTables.Problems(exclusions, [row])[0], StringComparison.Ordinal);

			var modal = TableSpecs.ModalQueryParameters;
			var a = SettingTables.NewRow(modal);
			a["Param"] = "overlay";
			a["AppendSuffix"] = ".html";
			var b = SettingTables.NewRow(modal);
			b["Param"] = "Overlay";
			var problems = SettingTables.Problems(modal, [a, b]);
			Assert.Null(problems[0]);
			Assert.Contains("already in row 1", problems[1], StringComparison.Ordinal);
			Assert.Equal("/path/page.html?overlay=/path/overlay-content  →  follows /path/overlay-content.html", modal.Example!(a.Cells));
		}

		[Fact]
		public void Table_Map_RoundTripAndDuplicateKeys()
		{
			var spec = TableSpecs.PageLanguageOverrides;
			var value = JsonNode.Parse("""{"/a/":["de","en"],"/b/":["fr"]}""");
			var rows = SettingTables.Read(spec, value);
			Assert.Equal("de, en", rows[0]["Values"]);
			Assert.Equal(value!.ToJsonString(), SettingTables.Write(spec, rows).ToJsonString());
			rows[1]["Key"] = "/A/";
			Assert.Contains("already in row 1", SettingTables.Problems(spec, rows)[1], StringComparison.Ordinal);

			var texts = TableSpecs.KnownChromeLanguageDefects;
			var t = SettingTables.Read(texts, JsonNode.Parse("""{"/x":["One, two","Three"]}"""));
			Assert.Equal("""{"/x":["One, two","Three"]}""", SettingTables.Write(texts, t).ToJsonString());
		}

		[Fact]
		public void Table_ValueThatDoesNotFit_StaysJson()
		{
			Assert.False(SettingTables.Fits(TableSpecs.DownloadExclusions, JsonNode.Parse("""[{"Value":1}]""")));
			Assert.False(SettingTables.Fits(TableSpecs.PageLanguageOverrides, JsonNode.Parse("""{"/a/":"de"}""")));
			Assert.True(SettingTables.Fits(TableSpec.OneColumn, null));
		}

		[Fact]
		public void Tables_CoverTheirSettingsFields()
		{
			// Every field of a record type has a column, so nothing is hidden in a table.
			Assert.Equal(
				typeof(CrawlLinkExclusion).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").OrderBy(n => n),
				TableSpecs.DownloadExclusions.Columns.Select(c => c.Field).OrderBy(n => n));
			Assert.Equal(
				typeof(DictionaryBundleConfig).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).OrderBy(n => n),
				TableSpecs.DictionaryBundles.Columns.Select(c => c.Field).OrderBy(n => n));
			Assert.Equal(
				typeof(SpecialInfoMapping).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).OrderBy(n => n),
				TableSpecs.SpecialInfoMappings.Columns.Select(c => c.Field).OrderBy(n => n));
		}

		[Fact]
		public void Placeholders_ResolvedSettings_MatchTheSiteResolution()
		{
			var config = new Config
			{
				ExtendedCrawlJsonPathPrefixes = ["/x"],
				CmsContentList = new CmsContentListConfig { RowsToExclude = ["/y"] },
			};
			Assert.Equal(
				Config.SubstitutableFieldNames(config).OrderBy(n => n, StringComparer.Ordinal),
				Config.SubstitutableSettings.OrderBy(n => n, StringComparer.Ordinal));
			Assert.All(Config.SubstitutableSettings, p => Assert.NotNull(SettingsCatalog.Find(p)));
		}

		[Fact]
		public void Placeholders_ResolvePerSite_EmptyMeansDefault()
		{
			var value = JsonNode.Parse("""["/content/{tenant}/pages", "/plain"]""");
			Assert.True(Placeholders.HasAny(value));
			Assert.True(Placeholders.IsResolved("ExtendedCrawlJsonPathPrefixes"));
			Assert.False(Placeholders.IsResolved("ContentQualityExcludedUrls"));

			var lines = Placeholders.Resolve(value, [
				new SiteDraft { Name = "One", Tenant = "alpha", IsPrimary = true },
				new SiteDraft { Name = "Two" },
			]);
			Assert.Equal(new[] { "/content/alpha/pages", "/plain" }, lines[0].Values);
			Assert.Equal(new[] { "/content/default/pages", "/plain" }, lines[1].Values);
			Assert.Equal("Primary site", lines[0].MaskedLabel);
			Assert.Equal("Site 2", lines[1].MaskedLabel);
			Assert.False(Placeholders.HasAny(JsonValue.Create("/plain")));
		}

		[Theory]
		[InlineData(49L, 10, SpaceRating.Low)]
		[InlineData(50L, 10, SpaceRating.Some)]
		[InlineData(199L, 10, SpaceRating.Some)]
		[InlineData(200L, 10, SpaceRating.Plenty)]
		[InlineData(5L, 0, SpaceRating.Unrated)]
		public void FreeSpace_RatedAgainstTheLimit(long freeGb, int limitGb, SpaceRating expected)
		{
			Assert.Equal(expected, FreeSpace.Rate(freeGb * 1024L * 1024L * 1024L, limitGb));
		}

		[Fact]
		public void Paths_InsideTheProgramFolder_AreKeptRelative()
		{
			var inside = Path.Combine(AppPaths.BaseDirectory, "dictionaries", "x.dic");
			Assert.Equal(Path.Combine("dictionaries", "x.dic"), AppPaths.Relativize(inside));
			var outside = Path.GetFullPath(Path.Combine(AppPaths.BaseDirectory, "..", "elsewhere", "x.dic"));
			Assert.Equal(outside, AppPaths.Relativize(outside));
		}

		[Fact]
		public void FreeSpace_Sizes()
		{
			Assert.Equal("38 GB", FreeSpace.Size(38L * 1024 * 1024 * 1024));
			Assert.Equal("1.5 TB", FreeSpace.Size(1536L * 1024 * 1024 * 1024));
			Assert.Equal("512 bytes", FreeSpace.Size(512));
		}
	}
}
