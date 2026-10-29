using System.Text;
using Xunit;

namespace CrawlerSuite.Core.Tests.Crawling
{
	/// <summary>
	/// Tests for Content.CompareCrawlAndContent — particularly that the index
	/// column count does not affect whether crawled pages are correctly excluded
	/// from log 05. A strict parts.Length == N check would silently empty the
	/// crawl set whenever a new column is added to the index format, causing
	/// every CMS page to appear in log 05 as "not directly crawlable".
	/// </summary>
	[Collection("Logger")]
	public class ContentTests : IDisposable
	{
		private readonly string _tempDir;

		public ContentTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"ContentTests_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"));
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── Helpers ──────────────────────────────────────────────────────────────

		private string WriteIndex(string filename, params string[] lines)
		{
			var path = Path.Combine(_tempDir, filename);
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		private string WriteContentList(string filename, params string[] paths)
		{
			var path = Path.Combine(_tempDir, filename);
			File.WriteAllLines(path, paths, Encoding.UTF8);
			return path;
		}

		private string OutputPath() =>
			Path.Combine(_tempDir, $"out_{Guid.NewGuid():N}.log");

		// ── 2-column index (still read) ───────────────────────────────────────────

		[Fact]
		public void CompareCrawlAndContent_TwoColumnIndex_CrawledPageNotInOutput()
		{
			var index = WriteIndex("index2.log",
				"abc.html|https://example.com/page-a.html");
			var content = WriteContentList("content.log", "/page-a.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.DoesNotContain("/page-a.html", result);
		}

		// ── 3-column index (filename|url|source) ──────────────────────────────────

		[Fact]
		public void CompareCrawlAndContent_ThreeColumnIndex_CrawledPageNotInOutput()
		{
			var index = WriteIndex("index3.log",
				"abc.html|https://example.com/page-a.html|discovery");
			var content = WriteContentList("content.log", "/page-a.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.DoesNotContain("/page-a.html", result);
		}

		[Fact]
		public void CompareCrawlAndContent_ThreeColumnIndex_UncrawledPageInOutput()
		{
			var index = WriteIndex("index3.log",
				"abc.html|https://example.com/page-a.html|discovery");
			var content = WriteContentList("content.log", "/page-a.html", "/page-b.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.Contains("/page-b.html", result);
		}

		[Fact]
		public void CompareCrawlAndContent_ThreeColumnIndex_DoesNotTreatAllPagesAsMissing()
		{
			// A strict parts.Length == 2 check would empty crawlEntries when the
			// index has 3 columns, causing every CMS page to appear in log 05.
			var index = WriteIndex("index3.log",
				"a.html|https://example.com/page-a.html|discovery",
				"b.html|https://example.com/page-b.html|discovery",
				"c.html|https://example.com/page-c.html|list");
			var content = WriteContentList("content.log",
				"/page-a.html", "/page-b.html", "/page-c.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.DoesNotContain(result, l => l.Length > 0);
		}

		// ── 4-column index (future proofing) ──────────────────────────────────────

		[Fact]
		public void CompareCrawlAndContent_FourColumnIndex_CrawledPageNotInOutput()
		{
			// If a fourth column is ever added to the index, the >= 2 check must
			// still correctly populate crawlEntries. This test catches a strict
			// equality check.
			var index = WriteIndex("index4.log",
				"abc.html|https://example.com/page-a.html|discovery|extra-future-column");
			var content = WriteContentList("content.log", "/page-a.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.DoesNotContain("/page-a.html", result);
		}

		// ── Content.Listing ────────────────────────────────────────────────────────

		[Fact]
		public void Listing_ValidCsv_FiltersAndWritesMatchingRows()
		{
			var csvPath = Path.Combine(_tempDir, "listing.csv");
			var outPath = OutputPath();
			File.WriteAllLines(csvPath, [
				"/content/mysite/de/home/page-a.html",
				"/content/mysite/de/home/page-b.html",
				"/other/path/not-matching.html",
			], System.Text.Encoding.UTF8);

			// rowNegativeFilter must be a non-matching pattern — empty string
			// would cause line.Contains("") == true, excluding everything.
			Content.Listing(outPath, csvPath,
				rowMustContain: "/content/mysite",
				columnDelimiter: ";",
				columnPosition: 0,
				replacePrefix: false,
				addSuffix: "",
				exclusions: [],
				rowNegativeFilter: "NOMATCH_SENTINEL",
				silent: true);

			var result = File.ReadAllLines(outPath);
			Assert.Equal(2, result.Length);
			Assert.Contains("/content/mysite/de/home/page-a.html", result);
			Assert.Contains("/content/mysite/de/home/page-b.html", result);
			Assert.DoesNotContain("/other/path/not-matching.html", result);
		}

		[Fact]
		public void Listing_MissingCsv_SilentMode_WritesNoOutput()
		{
			var csvPath = Path.Combine(_tempDir, "nonexistent.csv");
			var outPath = OutputPath();

			Content.Listing(outPath, csvPath,
				rowMustContain: "/content",
				columnDelimiter: ";",
				columnPosition: 0,
				replacePrefix: false,
				addSuffix: "",
				exclusions: [],
				rowNegativeFilter: "",
				silent: true);

			// Output file should not have been created
			Assert.False(File.Exists(outPath));
		}

		[Fact]
		public void Listing_NegativeFilter_ExcludesMatchingRows()
		{
			var csvPath = Path.Combine(_tempDir, "listing2.csv");
			var outPath = OutputPath();
			File.WriteAllLines(csvPath, [
				"/content/mysite/de/home/page-a.html",
				"/content/mysite/de/home/excluded.html",
			], System.Text.Encoding.UTF8);

			Content.Listing(outPath, csvPath,
				rowMustContain: "/content/mysite",
				columnDelimiter: ";",
				columnPosition: 0,
				replacePrefix: false,
				addSuffix: "",
				exclusions: [],
				rowNegativeFilter: "excluded",
				silent: true);

			var result = File.ReadAllLines(outPath);
			Assert.Single(result);
			Assert.Contains("/content/mysite/de/home/page-a.html", result);
		}

		// ── Mixed crawled and uncrawled ───────────────────────────────────────────

		[Fact]
		public void CompareCrawlAndContent_MixedPages_OnlyMissingInOutput()
		{
			var index = WriteIndex("index3.log",
				"a.html|https://example.com/crawled.html|discovery");
			var content = WriteContentList("content.log",
				"/crawled.html", "/missing.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.DoesNotContain("/crawled.html", result);
			Assert.Contains("/missing.html", result);
		}

		// ── The online filter: column and value from the configuration only ────────

		private string Export(params string[] lines)
		{
			var path = Path.Combine(_tempDir, $"export_{Guid.NewGuid():N}.csv");
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		private (string[] Listed, string[]? NotOnline, Content.ListingResult Result) List(string export, Content.OnlineFilter? online)
		{
			var listed = OutputPath();
			var result = Content.Listing(listed, export, "/content/", ";", 1, replacePrefix: false, addSuffix: "",
				exclusions: [], rowNegativeFilter: "\u0001", silent: true, online: online);
			var notOnline = online?.NotOnlineFile is { } f && File.Exists(f) ? File.ReadAllLines(f) : null;
			return (File.ReadAllLines(listed), notOnline, result);
		}

		[Fact]
		public void Listing_OnlineFilter_KeepsOnlyRowsWhoseColumnMatches_AndListsTheOthersApart()
		{
			var export = Export(
				"Export of 01.01.2030",
				"TITLE;PATH;STATE;NOTE",
				"Ice;/content/ice.html;Live;",
				"Floe;/content/floe.html;draft;mentions /content/ in a note",
				"Tide;/content/tide.html;;",
				"Vapour;/content/vapour.html;LIVE (since today);");
			var notOnlineFile = OutputPath();

			var (listed, notOnline, result) = List(export, new Content.OnlineFilter(" state ", "live*", SkipRows: 1, notOnlineFile));

			Assert.Equal(["/content/ice.html", "/content/vapour.html"], listed);
			Assert.Equal(["/content/floe.html", "/content/tide.html"], notOnline); // an empty cell is no match either
			Assert.Equal((2, 2, (string?)null, 4), (result.Listed, result.NotOnline, result.FilterOff, result.RowsRead));
		}

		[Fact]
		public void Listing_WithoutTheFilter_ReadsEveryRowAsBefore()
		{
			var export = Export("TITLE;PATH;STATE", "Ice;/content/ice.html;Live", "Floe;/content/floe.html;draft");
			var notOnlineFile = OutputPath();

			var (listed, notOnline, result) = List(export, new Content.OnlineFilter("", "live", 0, notOnlineFile));

			Assert.Equal(["/content/ice.html", "/content/floe.html"], listed);
			Assert.Null(notOnline);
			Assert.Null(result.FilterOff);
		}

		[Fact]
		public void Listing_AColumnTheHeaderDoesNotHave_TurnsTheFilterOff_AndSaysWhy()
		{
			var export = Export("TITLE;PATH;STATE", "Ice;/content/ice.html;Live", "Floe;/content/floe.html;draft");
			var notOnlineFile = OutputPath();
			File.WriteAllText(notOnlineFile, "/content/from-an-earlier-crawl.html");

			var (listed, notOnline, result) = List(export, new Content.OnlineFilter("STATUS", "live", 0, notOnlineFile));

			Assert.Equal(["/content/ice.html", "/content/floe.html"], listed);
			Assert.Null(notOnline); // an earlier crawl's list is not left to look current
			Assert.Contains("STATUS", result.FilterOff);
		}

		[Fact]
		public void Listing_TheHeaderRow_IsNoPage_EvenWhenTheRowFilterLetsItThrough()
		{
			var export = Export("TITLE;PATH /content/;STATE", "Ice;/content/ice.html;Live");

			var (listed, notOnline, _) = List(export, new Content.OnlineFilter("STATE", "Live", 0, OutputPath()));

			Assert.Equal(["/content/ice.html"], listed);
			Assert.Equal([], notOnline);
		}

		// ── Paths to leave out: counted per entry, switched-off ones too ───────────

		[Fact]
		public void Listing_CountsTheRowsEachPathToLeaveOutMatches_SwitchedOffAndOverlappingToo()
		{
			var export = Export(
				"TITLE;PATH",
				"Ice;/content/floe/ice.html",
				"Tide;/content/floe/tide.html",
				"Water;/content/water.html",
				"Other;/elsewhere/vapour.html"); // not past the row filter: not read
			var listed = OutputPath();

			var result = Content.Listing(listed, export, "/content/", ";", 1, replacePrefix: false, addSuffix: "",
				exclusions: ["/floe/", "/floe/tide"], rowNegativeFilter: "\u0001", silent: true,
				switchedOffExclusions: ["water", "/never/"]);

			Assert.Equal(["/content/water.html"], File.ReadAllLines(listed)); // the switched-off entry is not applied
			Assert.Equal(3, result.RowsRead);
			Assert.Equal(2, result.PathsLeftOut["/floe/"]);
			Assert.Equal(1, result.PathsLeftOut["/floe/tide"]);              // behind the broader entry, still counted
			Assert.Equal(1, result.PathsLeftOut["water"]);                   // would leave out one row
			Assert.Equal(0, result.PathsLeftOut["/never/"]);                 // listed, so it shows as unused
		}

		[Fact]
		public void TheCounts_GoIntoTheUsageFile_ForTheSettingsScreen()
		{
			var dir = Path.Combine(_tempDir, "snapshot");
			Directory.CreateDirectory(dir);
			var listing = new Content.ListingResult(2, 0, null)
			{
				RowsRead = 3,
				PathsLeftOut = new Dictionary<string, int> { ["/floe/"] = 2, ["/never/"] = 0 },
			};

			CrawlerSuite.Core.Settings.ContentPathUsage.Write(dir, listing);
			var report = CrawlerSuite.Core.Settings.SettingsUsage.Read(dir)!;

			Assert.Equal(3, CrawlerSuite.Core.Settings.ContentPathUsage.RowsRead(report));
			Assert.Equal(2, report.Of(CrawlerSuite.Core.Settings.ContentPathUsage.Setting, "/floe/")!.Pages);
			Assert.Equal(0, report.Of(CrawlerSuite.Core.Settings.ContentPathUsage.Setting, "/never/")!.Pages);
		}

		[Fact]
		public void PathsToLeaveOut_ReadFromTheFile_PlainAndSwitchedOff()
		{
			var list = System.Text.Json.JsonSerializer.Deserialize<CmsContentListConfig>(
				"""{ "RowsToExclude": ["/floe/", {"Value": "/tide/", "Enabled": false}] }""")!;

			Assert.Equal(["/floe/"], list.RowsToExclude);
			Assert.Equal(["/tide/"], list.RowsToExclude.Disabled);
		}
	}
}
