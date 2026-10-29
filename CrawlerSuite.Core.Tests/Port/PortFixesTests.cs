using System.Text;
using CrawlerSuite.Core.Security;
using CrawlerSuite.Core.Urls;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>Engine rules pinned one test each: the host allowlist, file names, index rows, redirect chains, sitemap escaping, configuration errors.</summary>
	[Collection("Logger")]
	public class PortFixesTests : IDisposable
	{
		private readonly string dir;

		public PortFixesTests()
		{
			dir = Path.Combine(Path.GetTempPath(), "cs-fixes-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			Logger.Initialize(Path.Combine(dir, "test.log"));
			RunUi.Attach(null);
		}

		public void Dispose()
		{
			try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
		}

		// One host means that host on its default port only.
		[Fact]
		public void HostAllowlist_RejectsSameHostOnAnotherPort()
		{
			var policy = CrawlPolicy.FromConfig("https://example.com", null);
			Assert.True(policy.IsHostAllowed(new Uri("https://example.com/a")));
			Assert.True(policy.IsHostAllowed(new Uri("https://example.com:443/a")));
			Assert.False(policy.IsHostAllowed(new Uri("https://example.com:8443/a")));
			Assert.False(policy.IsHostAllowed(new Uri("https://example.com:22/")));
		}

		// A dotted folder name is no file ending.
		[Theory]
		[InlineData("https://site.test/docs/v1.2/intro")]
		[InlineData("https://site.test/docs/v1.2/")]
		public void DottedFolder_GivesAFileNameWithoutSeparator(string url)
		{
			var name = Naming.GenerateFileName(url);
			Assert.DoesNotContain("/", name);
			Assert.EndsWith(".htm", name);
		}

		[Fact]
		public void DottedLastSegment_StillSuppliesTheSuffix()
		{
			Assert.EndsWith("logo.png", Naming.GenerateFileName("https://site.test/img/logo.png"));
		}

		// The index takes the status column, not the word anywhere in the row.
		[Fact]
		public void Index_IgnoresRowsThatOnlyMentionSaved()
		{
			var log = Path.Combine(dir, "01-crawler.log");
			File.WriteAllLines(log,
			[
				"2026-10-10 10:00:00 | https://site.test/saved-searches.html | crawled | info",
				"2026-10-10 10:00:01 | https://site.test/saved-searches.html | NotFound | Response status code does not indicate success: 404 (Not Found).",
				"2026-10-10 10:00:02 | https://site.test/a.html | saved | aaa.html | discovery",
				"2026-10-10 10:00:03 | https://site.test/logo.png | savedAsset | bbblogo.png | discovery",
			]);
			var index = Path.Combine(dir, "02-crawler-index.log");

			CrawlIndex.CreateLookupFile(log, index);

			var lines = File.ReadAllLines(index).Where(l => l.Trim().Length > 0).ToList();
			Assert.Equal(2, lines.Count);
			Assert.DoesNotContain(lines, l => l.Contains("saved-searches"));
		}

		// Redirect chains are followed past the first hop.
		[Fact]
		public void RedirectChain_IsFollowedPastTheFirstHop_IncludingTemporaryRedirect()
		{
			var log = Path.Combine(dir, "01-crawler.log");
			File.WriteAllLines(log,
			[
				"2026-10-10 10:00:00 | https://site.test/a | crawled | info",
				"2026-10-10 10:00:01 | https://site.test/a | TemporaryRedirect | https://site.test/b",
				"2026-10-10 10:00:02 | https://site.test/b | crawled | info",
				"2026-10-10 10:00:03 | https://site.test/b | MovedPermanently | https://site.test/c",
				"2026-10-10 10:00:04 | https://site.test/c | crawled | info",
				"2026-10-10 10:00:05 | https://site.test/c | saved | ccc.html | discovery",
			]);
			var output = Path.Combine(dir, "03-redirect-analysis.log");

			RedirectAnalyzer.AnalyzeRedirects(log, output);

			var first = File.ReadAllLines(output).First(l => l.StartsWith("https://site.test/a "));
			Assert.Contains("TemporaryRedirect", first);
			Assert.Contains("https://site.test/c", first);
		}

		// Sitemap addresses are XML-escaped.
		[Fact]
		public void Sitemap_EscapesAmpersands()
		{
			var download = Path.Combine(dir, "download");
			Directory.CreateDirectory(download);
			File.WriteAllText(Path.Combine(download, "p.html"), "<html><head><title>t</title></head><body>x</body></html>");
			var index = Path.Combine(dir, "02-crawler-index.log");
			File.WriteAllLines(index, ["p.html|https://site.test/p.html?a=1&b=2|discovery"]);
			Cache.Load(index);
			var sitemap = Path.Combine(dir, "sitemap.xml");

			Sitemap.Generate(download, sitemap, [], [], "*.html");

			var xml = File.ReadAllText(sitemap);
			Assert.Contains("a=1&amp;b=2", xml);
			System.Xml.Linq.XDocument.Parse(xml); // valid XML
		}

		// A JSON error's line number counts the comment lines too.
		[Fact]
		public void JsonError_LineNumber_CountsCommentLines()
		{
			var path = Path.Combine(dir, "config.json");
			File.WriteAllText(path, "// one\n// two\n// three\n{\n  \"Sites\": [ ,\n}\n");

			var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Config.LoadFromJson(path));

			Assert.Equal("5", ConfigErrors.HumanLine(ex.LineNumber));
		}

		// Boilerplate XPaths are checked when the configuration loads.
		[Fact]
		public void BoilerplateXPath_IsValidatedWhenTheConfigLoads()
		{
			var path = Path.Combine(dir, "config.json");
			File.WriteAllText(path, """
				{
					"Sites": [ { "Name": "S", "IsPrimary": true, "Url": "https://example.com" } ],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"SpellCheckEngine": {
						"BoilerplateGroups": [
							{ "PathPrefix": "/", "BoilerplateSelectors": [ { "Type": "xpath", "Value": "//div[@id='x'" } ] }
						]
					}
				}
				""");

			var ex = Assert.ThrowsAny<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("BoilerplateGroups[0].BoilerplateSelectors[0]", ex.Message);
		}

		// The download limits' defaults.
		[Fact]
		public void NewLimitSettings_HaveTheAgreedDefaults()
		{
			var config = new Config();
			Assert.Equal(150, config.MaxSingleFileMB);
			Assert.Equal(10, config.MaxCrawlDownloadGB);
			Assert.Equal(30, config.StallTimeoutSeconds);
			Assert.Equal(150L * 1024 * 1024, config.MaxSingleFileBytes);
			Assert.Equal(10L * 1024 * 1024 * 1024, config.MaxCrawlDownloadBytes);
		}

		// Deep nesting fails the parse, not the process.
		[Fact]
		public void DeeplyNestedPage_FailsTheParseInsteadOfTheProcess()
		{
			var html = string.Concat(Enumerable.Repeat("<div>", 5000));
			var doc = CrawlerSuite.Core.Html.HtmlDocuments.Create();

			Assert.ThrowsAny<Exception>(() => doc.LoadHtml(html));
		}

		// Previous-crawl comparison
		[Fact]
		public void PreviousCrawl_CountsMainCrawlFilesAndTime()
		{
			var log = Path.Combine(dir, "01-crawler.log");
			File.WriteAllLines(log,
			[
				"2026-10-10 10:00:00 | https://site.test | crawled | info",
				"2026-10-10 10:00:01 | https://site.test/ | saved | a.html | discovery",
				"2026-10-10 10:00:02 | https://site.test/x.png | savedAsset | bx.png | discovery",
				"2026-10-10 10:04:52 | https://site.test | completed | info",
				"2026-10-10 10:05:00 | https://site.test/l.html | saved | l.html | list",
				"2026-10-10 10:06:00 | https://site.test | completed | post-crawl-pass",
			]);

			var previous = PreviousCrawlReader.FromLog("snap", log, "https://site.test");

			Assert.Equal(2, previous.FileCount);
			Assert.Equal(TimeSpan.FromSeconds(292), previous.Duration);
		}

		[Theory]
		[InlineData("https://site.test/en/products/item/a.pdf", "/en/products/")]
		[InlineData("https://site.test/a.pdf", "/")]
		[InlineData("https://site.test/en/a.pdf", "/en/")]
		public void CrawlLimitReport_GroupsByUpToTwoFolders(string url, string folder)
		{
			Assert.Equal(folder, CrawlLimitReportBuilder.FolderOf(url));
		}

		[Fact]
		public void SnapshotFlows_CleanSweep_KeepsTheOversizedFolder()
		{
			Assert.True(SnapshotFlows.ShouldKeepEntry(OversizedFiles.FolderName));
		}
	}
}
