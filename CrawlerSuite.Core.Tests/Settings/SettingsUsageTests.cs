using System.Text.Json;
using CrawlerSuite.Core.Boilerplate;
using CrawlerSuite.Core.Settings;
using CrawlerSuite.Core.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Settings
{
	/// <summary>Entries that can be switched off, and the per-run usage counts the settings screen shows.</summary>
	public class SettingsUsageTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), "st-usage-" + Guid.NewGuid().ToString("N"), "2026-10-13-09-30-00");

		public SettingsUsageTests() => Directory.CreateDirectory(dir);

		public void Dispose()
		{
			try { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); } catch { }
		}

		[Fact]
		public void SwitchableList_ReadsBothForms_AndWritesThemBack()
		{
			var config = Config.Parse("""
				{ "Sites": [ { "Name": "A", "Url": "https://www.example.com", "IsPrimary": true } ],
				  "BaseDirectory": "x", "FilePattern": "*.html", "CustomDictionaryFile": "u.dic",
				  "SpellCheckEngine": { "GlobalXpathToIgnore": [ "//a", { "Value": "//b", "Enabled": false }, { "Value": "//c" } ] } }
				""");
			var list = config.SpellCheckEngine.GlobalXpathToIgnore;
			Assert.Equal(new[] { "//a", "//c" }, list.ToArray());
			Assert.Equal(new[] { "//b" }, list.Disabled.ToArray());
			Assert.Equal("""["//a","//c",{"Value":"//b","Enabled":false}]""", JsonSerializer.Serialize(list));
		}

		[Fact]
		public void Boilerplate_SwitchedOffPart_IsNotApplied()
		{
			var groups = new List<BoilerplateGroupConfig>
			{
				new()
				{
					PathPrefix = "/",
					BoilerplateSelectors =
					[
						new() { Type = "class", Value = "head" },
						new() { Type = "class", Value = "foot", Enabled = false },
					],
				},
			};
			var (matcher, _) = new BoilerplateResolver(groups).Resolve("/page.html");
			var doc = new HtmlDocument();
			doc.LoadHtml("<div class='head'>a</div><div class='foot'>b</div>");
			var nodes = doc.DocumentNode.SelectNodes("//div");
			Assert.True(matcher!.IsBoilerplate(nodes[0]));
			Assert.False(matcher.IsBoilerplate(nodes[1]));
			Assert.Single(new BoilerplateResolver(groups).ResolveSelectors("/page.html").Selectors);
		}

		[Fact]
		public void Usage_CountsPagesAndMatches_IncludingSwitchedOffEntries()
		{
			var engine = new SpellCheckEngineConfig
			{
				GlobalXpathToIgnore = ["//img[@id='pixel']"],
				GlobalAttributesToIgnore = ["data-token"],
				BoilerplateGroups =
				[
					new()
					{
						PathPrefix = "/",
						PagesToCheckBoiler = ["/home.html"],
						BoilerplateSelectors =
						[
							new() { Type = "class", Value = "nav main" },
							new() { Type = "xpath", Value = "//footer" },
							new() { Type = "class", Value = "gone", Enabled = false },
						],
					},
				],
			};
			engine.GlobalAttributesToIgnore.Disabled.Add("data-old");

			var usage = new SettingsUsage(engine);
			usage.CountPage(Doc("<img id='pixel'><div class='nav main x' data-token='q'></div><footer></footer>"), "https://www.example.com/home.html");
			usage.CountPage(Doc("<div class='nav' data-token='q'><p data-token='r'></p></div>"), "https://www.example.com/a.html");
			usage.Write(dir);

			var report = SettingsUsage.Read(dir)!;
			Assert.Equal(2, report.Pages);
			Assert.Equal(new UsageCount(1, 1), report.Of(SettingsUsage.Xpaths, "//img[@id='pixel']"));
			Assert.Equal(new UsageCount(2, 3), report.Of(SettingsUsage.Attributes, "data-token"));
			Assert.Equal(new UsageCount(0, 0), report.Of(SettingsUsage.Attributes, "data-old"));   // switched off, still counted
			Assert.Equal(new UsageCount(1, 1), report.Of(SettingsUsage.Boilerplate, SettingsUsage.PartKey("/", "class", "nav main")));
			Assert.Equal(new UsageCount(1, 1), report.Of(SettingsUsage.Boilerplate, SettingsUsage.PartKey("/", "xpath", "//footer")));
			Assert.Equal(new UsageCount(0, 0), report.Of(SettingsUsage.Boilerplate, SettingsUsage.PartKey("/", "class", "gone")));
			Assert.Contains("/home.html", report.ReferencePagesSeen);
			Assert.Equal(new DateOnly(2026, 10, 13), report.Day);
			Assert.Null(report.Of(SettingsUsage.Xpaths, "//changed"));
		}

		private static HtmlDocument Doc(string body)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html><body>" + body + "</body></html>");
			return doc;
		}
	}
}
