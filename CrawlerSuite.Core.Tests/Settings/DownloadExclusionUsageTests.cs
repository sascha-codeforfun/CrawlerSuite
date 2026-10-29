using System;
using System.Collections.Generic;
using System.IO;
using CrawlerSuite.Core.Host;
using CrawlerSuite.Core.Settings;
using CrawlerSuite.Core.Urls;
using Xunit;

namespace CrawlerSuite.Core.Tests.Settings
{
	/// <summary>What each download exclusion kept out in a crawl: counted where the crawl decides, written into the usage file. Invented addresses.</summary>
	public class DownloadExclusionUsageTests
	{
		private static readonly List<CrawlLinkExclusion> Entries =
		[
			new() { Value = "/floe/", Enabled = true },
			new() { Value = "/floe/tide", Enabled = true },     // behind the broader one: still counted
			new() { Value = "vapour.html", Enabled = false },   // switched off: counted, not applied
			new() { Value = "/never/", Enabled = true },
		];

		private const string Site = "https://site.test";

		[Fact]
		public void EveryEntryAnAddressContains_IsCounted_ByPhase_AddressesAndTimes()
		{
			var usage = new DownloadExclusionUsage(Entries);

			Assert.False(Validity.IsInDownloadScope(Site + "/floe/tide.html", Site, Entries, usage: usage));
			Assert.False(Validity.IsInDownloadScope(Site + "/floe/tide.html", Site, Entries, usage: usage));
			Assert.False(Validity.IsInDownloadScope(Site + "/floe/ice.html", Site, Entries, usage: usage, phase: DownloadPhase.ExtraDownload));
			Assert.True(Validity.IsInDownloadScope(Site + "/water/vapour.html", Site, Entries, usage: usage));

			var main = usage.Of(DownloadPhase.MainCrawl);
			var extra = usage.Of(DownloadPhase.ExtraDownload);
			Assert.Equal(new UsageCount(1, 2), main["/floe/"]);       // one address, twice
			Assert.Equal(new UsageCount(1, 2), main["/floe/tide"]);
			Assert.Equal(new UsageCount(1, 1), main["vapour.html"]);  // would have kept it out
			Assert.Equal(new UsageCount(0, 0), main["/never/"]);      // listed, so it shows as unused
			Assert.Equal(new UsageCount(1, 1), extra["/floe/"]);
			Assert.Equal(new UsageCount(0, 0), extra["/floe/tide"]);
		}

		[Fact]
		public void AnotherHost_IsNoExclusionsBusiness()
		{
			var usage = new DownloadExclusionUsage(Entries);

			Assert.False(Validity.IsInDownloadScope("https://other.test/floe/x.html", Site, Entries, usage: usage));

			Assert.Equal(new UsageCount(0, 0), usage.Of(DownloadPhase.MainCrawl)["/floe/"]);
		}

		[Fact]
		public void TheCounts_GoIntoTheUsageFile_NextToTheOtherSettings()
		{
			var dir = Path.Combine(Path.GetTempPath(), "exclusion-usage-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				SettingsUsage.Merge(dir, new Dictionary<string, IReadOnlyDictionary<string, UsageCount>>
				{
					[SettingsUsage.Xpaths] = new Dictionary<string, UsageCount> { ["//nav"] = new(3, 4) },
				}, pageCount: 10);
				var usage = new DownloadExclusionUsage([new() { Value = " /floe/ ", Enabled = true }]);
				usage.Count(Site + "/floe/a.html", [new() { Value = " /floe/ ", Enabled = true }], DownloadPhase.MainCrawl);
				usage.Count(Site + "/floe/a.html", [new() { Value = "/floe/", Enabled = true }], DownloadPhase.MainCrawl);

				usage.Write(dir);
				var report = SettingsUsage.Read(dir)!;

				Assert.Equal(new UsageCount(3, 4), report.Of(SettingsUsage.Xpaths, "//nav"));
				Assert.Equal(10, report.Pages);
				// Keyed trimmed, as the screen looks it up; matched as written, as the crawl matches it (" /floe/ " is not in the address).
				Assert.Equal(new UsageCount(1, 1), report.Of(DownloadExclusionUsage.MainCrawl, "/floe/"));
				Assert.Equal(new UsageCount(0, 0), report.Of(DownloadExclusionUsage.ExtraDownload, "/floe/"));
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}

		[Fact]
		public void TheCrawlsCounts_StandApart_AndAreReadWithTheRest()
		{
			var dir = Path.Combine(Path.GetTempPath(), "exclusion-usage-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				var usage = new DownloadExclusionUsage(Entries);
				usage.Count(Site + "/floe/a.html", Entries, DownloadPhase.MainCrawl);
				usage.Write(dir);
				SettingsUsage.Merge(dir, new Dictionary<string, IReadOnlyDictionary<string, UsageCount>>
				{
					[SettingsUsage.Xpaths] = new Dictionary<string, UsageCount> { ["//nav"] = new(3, 4) },
					// An older usage file may still hold exclusion counts: the crawl's own file wins.
					[DownloadExclusionUsage.MainCrawl] = new Dictionary<string, UsageCount> { ["/floe/"] = new(9, 9) },
				}, pageCount: 10);

				Assert.True(File.Exists(Path.Combine(dir, SettingsUsage.CrawlFileName)));
				var report = SettingsUsage.Read(dir)!;
				Assert.Equal(new UsageCount(1, 1), report.Of(DownloadExclusionUsage.MainCrawl, "/floe/"));
				Assert.Equal(new UsageCount(3, 4), report.Of(SettingsUsage.Xpaths, "//nav"));

				// A re-run clears the analysis' file; the crawl's stays and is still read.
				File.Delete(Path.Combine(dir, SettingsUsage.FileName));
				Assert.Equal(new UsageCount(1, 1), SettingsUsage.Read(dir)!.Of(DownloadExclusionUsage.MainCrawl, "/floe/"));
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}
	}
}
