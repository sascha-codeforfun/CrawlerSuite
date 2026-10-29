using CrawlerSuite.Core.Snapshots;
using Xunit;

namespace CrawlerSuite.Core.Tests.Snapshots
{
	/// <summary>Why a file is in one snapshot and not the other, from the other crawl's log.</summary>
	public class CrawlOutcomesTests : IDisposable
	{
		private readonly string snapshot = Path.Combine(Path.GetTempPath(), "outcomes-" + Guid.NewGuid().ToString("N"));

		public CrawlOutcomesTests()
		{
			Directory.CreateDirectory(snapshot);
			File.WriteAllLines(Path.Combine(snapshot, LogFileNames.CrawlerRaw),
			[
				"2026-01-01 10:00:00 | https://site.test/ice.html | crawled | info",
				"2026-01-01 10:00:30 | https://site.test/ice.html | N/A | No data received for 30 s — download aborted (stall timeout)",
				"2026-01-01 10:00:01 | https://site.test/gone.html | crawled | info",
				"2026-01-01 10:00:02 | https://site.test/gone.html | NotFound | Response status code does not indicate success: 404 (Not Found).",
				"2026-01-01 10:00:03 | https://site.test/old.html | crawled | info",
				"2026-01-01 10:00:03 | https://site.test/old.html | Found | https://site.test/new.html?n=true",
				"2026-01-01 10:00:03 | https://site.test/old.html | Found | Response status code does not indicate success: 302 (Found).",
				"2026-01-01 10:00:04 | https://site.test:443/open.html | crawled | info",
			]);
		}

		public void Dispose()
		{
			try
			{
				Directory.Delete(snapshot, recursive: true);
			}
			catch (IOException)
			{
			}
		}

		[Theory]
		[InlineData("https://site.test/ice.html", "A's crawl could not download it: No data received for 30 s — download aborted (stall timeout)")]
		[InlineData("https://site.test/gone.html", "A's crawl got 404 (Not Found).")]
		[InlineData("https://site.test/old.html", "A's crawl was redirected (Found) to https://site.test/new.html?n=true")]
		[InlineData("https://site.test/open.html", "A's crawl asked for this address but recorded no outcome (the crawl did not finish).")]
		[InlineData("https://site.test/water.html", "A's crawl never asked for this address: no page it saw linked to it.")]
		public void Describe_SaysWhatTheOtherCrawlDid(string url, string expected)
		{
			Assert.Equal(expected, CrawlOutcomes.Describe("A", CrawlOutcomes.Read(snapshot), url));
		}

		[Fact]
		public void Describe_WithoutALog_SaysNothing()
		{
			var empty = Path.Combine(snapshot, "none");
			Directory.CreateDirectory(empty);
			Assert.Equal(string.Empty, CrawlOutcomes.Describe("B", CrawlOutcomes.Read(empty), "https://site.test/ice.html"));
		}
	}
}
