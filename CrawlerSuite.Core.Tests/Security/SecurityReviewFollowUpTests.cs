using System;
using System.IO;
using CrawlerSuite.Core.Urls;
using Xunit;

namespace CrawlerSuite.Core.Tests.Security
{
	/// <summary>Asset names and their settle: handler endings kept, others quarantined, a page and an asset named apart. Invented addresses.</summary>
	public class SecurityReviewFollowUpTests
	{
		[Theory]
		[InlineData("https://site.test/WebResource.axd?d=floe", ".axd")]
		[InlineData("https://site.test/tide.ashx", ".ashx")]
		[InlineData("https://site.test/feed/ice.rss", ".asset.unverified")]
		[InlineData("https://site.test/api/v1.2", ".asset.unverified")]
		public void HandlerEndings_AreKept_OthersQuarantined(string url, string ending) =>
			Assert.EndsWith(ending, Naming.GenerateAssetFileName(url));

		[Fact]
		public void AUrlFetchedAsPageAndAsAsset_GetsTwoNames_AndTwoSidecars()
		{
			const string url = "https://site.test/feed/ice.rss";
			var page = Path.ChangeExtension(Naming.GenerateFileName(url), "unverified");
			var asset = Naming.GenerateAssetFileName(url);

			Assert.NotEqual(page, asset);
			Assert.NotEqual(Path.ChangeExtension(page, "header"), Path.ChangeExtension(asset, "header"));
			// A settled asset keeps its sidecar: everything before the last ending stays.
			Assert.Equal(Path.ChangeExtension(asset, "header"), Path.ChangeExtension(Path.ChangeExtension(asset, ".css"), "header"));
		}

		[Theory]
		[InlineData("text/css", ".css")]
		[InlineData(" Text/JavaScript ", ".js")]
		[InlineData("application/javascript", ".js")]
		[InlineData("application/x-msdownload", null)]
		[InlineData("text/html", null)]
		[InlineData(null, null)]
		public void TheMediaType_PicksOneOfTwoEndings(string? type, string? ending) =>
			Assert.Equal(ending, FileTypeClassifier.TextAssetEnding(type));

		[Fact]
		public void PlainText_HasNoControlCharacters()
		{
			Assert.True(FileTypeClassifier.LooksLikePlainText("body { color: red }\r\n\tp {}"u8.ToArray()));
			Assert.True(FileTypeClassifier.LooksLikePlainText([]));
			Assert.False(FileTypeClassifier.LooksLikePlainText([0x4D, 0x5A, 0x90, 0x00])); // a program
			Assert.False(FileTypeClassifier.LooksLikePlainText([0x1F, 0x8B, 0x08]));       // gzip
		}

		[Fact]
		public void AnAssetRow_TakesTheNameItsFileWasSettledTo()
		{
			var dir = Path.Combine(Path.GetTempPath(), "sr13-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				File.WriteAllText(Path.Combine(dir, "abcice.asset.css"), "p {}");
				const string row = "2026-10-27 10:00:00 | https://site.test/ice.rss | savedAsset | abcice.asset.unverified | page.html | text/css";

				Assert.Equal("2026-10-27 10:00:00 | https://site.test/ice.rss | savedAsset | abcice.asset.css | page.html | text/css",
					CrawlOrchestrator.WithSettledAssetName(row, dir));
				// Still quarantined, a page row, or no file settled: as it was.
				File.WriteAllText(Path.Combine(dir, "abcice.asset.unverified"), "x");
				Assert.Equal(row, CrawlOrchestrator.WithSettledAssetName(row, dir));
				Assert.Equal(row.Replace("savedAsset", "saved"), CrawlOrchestrator.WithSettledAssetName(row.Replace("savedAsset", "saved"), dir));
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}
	}
}
