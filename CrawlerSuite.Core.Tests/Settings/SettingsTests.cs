using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CrawlerSuite.Core.Settings;
using Xunit;

namespace CrawlerSuite.Core.Tests.Settings
{
	/// <summary>The settings screen's core: the catalog, the document, field values and the site checks.</summary>
	public class SettingsTests
	{
		/// <summary>Config properties that are not settings: sites have their own tab; the address, subdomains and post-crawl pass come from the chosen site; proxy credentials are no configuration at all.</summary>
		private static readonly HashSet<string> NotSettings = new(StringComparer.Ordinal) { "Sites", "Url", "UrlSubdomainsAllowed", "CmsContentList.PostCrawlPass" };

		[Fact]
		public void Catalog_CoversEveryConfigValue_AndNothingElse()
		{
			var leaves = new List<string>();
			void Walk(Type type, string prefix)
			{
				foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
				{
					if (!p.CanWrite || p.GetCustomAttribute<JsonIgnoreAttribute>() != null || p.GetIndexParameters().Length > 0)
					{
						continue;
					}

					var path = prefix + p.Name;
					var t = p.PropertyType;
					bool collection = typeof(System.Collections.IEnumerable).IsAssignableFrom(t) && t != typeof(string);
					if (t.IsClass && t != typeof(string) && !collection)
					{
						Walk(t, path + ".");
						continue;
					}

					leaves.Add(path);
				}
			}

			Walk(typeof(Config), string.Empty);
			var expected = leaves.Where(l => !NotSettings.Contains(l)).OrderBy(x => x, StringComparer.Ordinal).ToList();
			var catalog = SettingsCatalog.All.Select(s => s.Path).OrderBy(x => x, StringComparer.Ordinal).ToList();
			Assert.Equal(expected, catalog);
			Assert.Equal(catalog.Count, catalog.Distinct().Count());
		}

		[Fact]
		public void Catalog_EveryDefault_MatchesItsKind()
		{
			foreach (var s in SettingsCatalog.All)
			{
				var d = SettingsCatalog.DefaultOf(s.Path);
				switch (s.Kind)
				{
					case SettingKind.Switch: Assert.True(d is JsonValue v && v.TryGetValue<bool>(out _), s.Path); break;
					case SettingKind.Number: Assert.True(d is JsonValue n && (n.TryGetValue<int>(out _) || n.TryGetValue<long>(out _)), s.Path); break;
					case SettingKind.Decimal: Assert.True(d is JsonValue m && m.TryGetValue<double>(out _), s.Path); break;
					case SettingKind.Lines: Assert.True(d is null or JsonArray, s.Path); break;
					case SettingKind.Json: Assert.True(d is JsonArray or JsonObject, s.Path); break;
					default: Assert.True(d is JsonValue t && t.TryGetValue<string>(out _), s.Path); break;
				}

				Assert.Contains(s.Tab, SettingsCatalog.Tabs);
				Assert.False(string.IsNullOrWhiteSpace(s.Label), s.Path);
			}
		}

		[Fact]
		public void Document_SetAndRemove_GroupsComeAndGo()
		{
			var doc = SettingsDocument.From(new JsonObject());
			doc.Set("ContentQuality.MalformedHtml.DetectHtmlParseErrors", false);
			Assert.True(doc.Has("ContentQuality.MalformedHtml.DetectHtmlParseErrors"));
			Assert.True(doc.IsDirty);

			doc.Remove("ContentQuality.MalformedHtml.DetectHtmlParseErrors");
			Assert.False(doc.Has("ContentQuality"));    // the empty groups went with it
			Assert.Equal("{" + FileFormat.NewLine + "}" + FileFormat.NewLine, doc.ToSavedText().Replace("{}", "{" + FileFormat.NewLine + "}"));
		}

		[Fact]
		public void Document_UnknownKeys_OldConsoleKeysAndThePassword()
		{
			var doc = SettingsDocument.From(JsonNode.Parse("""
				{ "Sites": [], "UseProxy": false, "ProxyPassword": "x", "DebugDisableCrawl": true,
				  "ContentQuality": { "CheckLigatures": true, "Gone": 1 }, "TicketGeneration": { "TicketPrefix": "A", "PathColumn": "P" } }
				""")!.AsObject());

			Assert.Equal(new[] { "ProxyPassword", "DebugDisableCrawl", "ContentQuality.Gone", "TicketGeneration.TicketPrefix" }, doc.UnknownKeys().ToArray());
			var saved = doc.ToSavedText();
			Assert.DoesNotContain("ProxyPassword", saved, StringComparison.Ordinal);
			Assert.DoesNotContain("TicketPrefix", saved, StringComparison.Ordinal);
			Assert.Contains("\"PathColumn\": \"P\"", saved, StringComparison.Ordinal);
		}

		[Fact]
		public void Document_RestatedDefaults_AreFound()
		{
			var doc = SettingsDocument.From(JsonNode.Parse("""
				{ "MaxSingleFileMB": 150, "MaxCrawlDownloadGB": 20, "GermanFugenelemente": ["s"] }
				""")!.AsObject());

			Assert.Equal(new[] { "GermanFugenelemente", "MaxSingleFileMB" }, doc.RestatedDefaults().Select(s => s.Path).OrderBy(x => x).ToArray());
		}

		[Fact]
		public void Values_Lines_EmptyEntryRoundTrips()
		{
			var setting = SettingsCatalog.Find("SitemapGeneratorExclusions")!;
			var (value, error) = SettingValues.FromText(setting, "\"\"\nindex\n\n  index,follow ");
			Assert.Null(error);
			Assert.Equal("[\"\",\"index\",\"index,follow\"]", value!.ToJsonString());
			Assert.Equal(string.Join(Environment.NewLine, "\"\"", "index", "index,follow"), SettingValues.ToText(setting, value));
		}

		[Fact]
		public void Values_Numbers_RangeAndForm()
		{
			var mb = SettingsCatalog.Find("MaxSingleFileMB")!;
			Assert.Null(SettingValues.FromText(mb, "200").Error);
			Assert.NotNull(SettingValues.FromText(mb, "-1").Error);
			Assert.NotNull(SettingValues.FromText(mb, "2,5").Error);
			var ratio = SettingsCatalog.Find("ContentQuality.TranslationIssuePassRatio")!;
			Assert.Equal(0.5, SettingValues.FromText(ratio, "0,5").Value!.GetValue<double>());
			Assert.NotNull(SettingValues.FromText(ratio, "1.5").Error);
		}

		[Fact]
		public void Values_Json_ListOrMapAsExpected()
		{
			var bundles = SettingsCatalog.Find("DictionaryBundles")!;
			Assert.Null(SettingValues.FromText(bundles, "[ {\"LanguageCode\":\"de\"} ]").Error);
			Assert.NotNull(SettingValues.FromText(bundles, "{ }").Error);
			Assert.NotNull(SettingValues.FromText(bundles, "[ {").Error);
			var map = SettingsCatalog.Find("SpellCheckEngine.PageLanguageOverrides")!;
			var (value, _) = SettingValues.FromText(map, "{ \"/a/\": [\"de\",\"en\"], \"/b/\": [\"fr\"] }");
			Assert.Equal(string.Join(Environment.NewLine, "{", "  \"/a/\": [\"de\",\"en\"],", "  \"/b/\": [\"fr\"]", "}"), SettingValues.ToText(map, value));
		}

		[Theory]
		[InlineData("https://www.example.com", null)]
		[InlineData("https://www.example.com/de/", null)]
		[InlineData("http://example.org", null)]
		[InlineData("https://www.exämple.de", null)]
		[InlineData("www.example.com", "must start with https://")]
		[InlineData("https://www.example .com", "contains a space")]
		[InlineData("https://user:pw@www.example.com", "user name or password")]
		[InlineData("https://www.example.com/?a=1", "? or #")]
		[InlineData("https://-bad.example.com", "hyphen")]
		[InlineData("https://localhost", "needs a domain")]
		[InlineData("", "An address is needed")]
		public void SiteRules_Url(string url, string? problem)
		{
			var result = SiteRules.CheckUrl(url);
			if (problem is null)
			{
				Assert.Null(result);
			}
			else
			{
				Assert.Contains(problem, result, StringComparison.Ordinal);
			}
		}

		[Fact]
		public void SiteRules_SubdomainsAreBaseAddresses_DuplicatesAndOnePrimary()
		{
			var sites = new List<SiteDraft>
			{
				new() { Name = "A", Url = "https://www.example.com", IsPrimary = true, UrlSubdomainsAllowed = ["https://help.example.com", "https://x.example.com/path"] },
				new() { Name = "B", Url = "https://www.example.com/", IsPrimary = true },
			};
			var problems = SiteRules.Check(sites);
			Assert.Contains(problems, p => p.Contains("without a path", StringComparison.Ordinal));
			Assert.Contains(problems, p => p.Contains("listed 2 times", StringComparison.Ordinal));
			Assert.Contains(problems, p => p.Contains("Exactly one site", StringComparison.Ordinal));
			Assert.Equal("www.xn--exmple-cua.de", SiteRules.AsciiHost("https://www.exämple.de"));
		}

		[Fact]
		public void SiteRules_EmptySubdomainEntry_IsNoSubdomain()
		{
			// Older files carry [ "" ]: the crawl drops it, so the form must not trap on it.
			var site = new SiteDraft { Name = "A", Url = "https://www.example.com", IsPrimary = true, UrlSubdomainsAllowed = [""] };
			Assert.Empty(SiteRules.Check([site]));
		}

		[Fact]
		public void Document_Sites_RoundTrip()
		{
			var doc = SettingsDocument.From(new JsonObject());
			doc.WriteSites([new SiteDraft { Name = "Examplix", Url = "https://www.example.com", IsPrimary = true }]);
			var back = Assert.Single(doc.ReadSites());
			Assert.Equal("Examplix", back.Name);
			Assert.Contains("{\"Name\":\"Examplix\",\"Tenant\":\"\",", doc.ToSavedText(), StringComparison.Ordinal);
		}
	}
}
