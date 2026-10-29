using System.Text;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Quality;
using CrawlerSuite.Core.Snapshots;
using CrawlerSuite.Core.Triage;
using CrawlerSuite.Core.Urls;
using Xunit;

namespace CrawlerSuite.Core.Tests.Review
{
	/// <summary>Engine rules pinned one test each: formula triggers, emoji, the completed marker, the index, the doctype leaf, the screen mask, the ledger. Invented data only.</summary>
	[Collection("Logger")]
	public class ReviewFixesTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), "st-review-" + Guid.NewGuid().ToString("N"));

		public ReviewFixesTests()
		{
			Directory.CreateDirectory(dir);
			Logger.Initialize(Path.Combine(dir, "test.log"));
		}

		public void Dispose()
		{
			try { Directory.Delete(dir, recursive: true); } catch { }
		}

		// A leading zero-width character must not uncover a formula trigger.
		[Theory]
		[InlineData("​=HYPERLINK(\"x\")")]
		[InlineData("﻿+1")]
		[InlineData("‮-2")]
		[InlineData("=1")]
		public void Csv_FormulaTrigger_IsNeutralized_AfterCleaning(string field)
		{
			var line = IssueLogWriter.ComposeCsvLine(',', field);
			var cell = line.StartsWith('"') ? line[1..] : line;
			Assert.Equal('\'', cell[0]);
		}

		// An emoji with a variation selector is no decomposed letter, and must not throw.
		[Theory]
		[InlineData("Die Flagge \U0001F3F3️ weht")]
		[InlineData("Herz ❤️ und Hand")]
		public void Decomposition_EmojiWithVariationSelector_NoThrowNoFinding(string text)
		{
			Assert.Null(Decomposition.FindFirstDecomposition(text));
			Assert.Empty(Decomposition.FindAllDecompositions(text));
		}

		[Fact]
		public void Decomposition_RealDecomposedLetter_StillFound()
		{
			Assert.NotNull(Decomposition.FindFirstDecomposition("Haüser \U0001F3F3️"));
		}

		// The completed marker is the status column, not a word anywhere in the line.
		[Theory]
		[InlineData("2026-01-01 10:00:00 | https://example.test/ | completed | info", true)]
		[InlineData("2026-01-01 10:00:00 | https://example.test/ | completed | post-crawl-pass", true)]
		[InlineData("2026-01-01 10:00:00 | https://example.test/ | OK  | completed | info", true)]   // the marker in the fourth column
		[InlineData("2026-01-01 10:00:00 | https://example.test/completed-projects | 200 | crawled", false)]
		[InlineData("2026-01-01 10:00:00 | https://example.test/x | 404 | NotFound completed", false)]
		public void CrawlLog_Completed_IsTheStatusColumn(string lastLine, bool complete)
		{
			var log = Path.Combine(dir, "00.log");
			File.WriteAllLines(log, ["2026-01-01 09:00:00 | https://example.test/ | 200 | crawled", lastLine], Encoding.UTF8);
			Assert.Equal(complete, CrawlLog.Analyse(log, "https://example.test/").IsComplete);
		}

		// A run reads only its own index; a missing one leaves nothing from before.
		[Fact]
		public void Cache_Load_ForgetsTheEarlierIndex()
		{
			var index = Path.Combine(dir, "02.log");
			File.WriteAllLines(index, ["aaa.html|https://example.test/a"], Encoding.UTF8);
			Cache.Load(index);
			Assert.Equal("https://example.test/a", Cache.UrlFor("aaa.html"));

			Cache.Load(Path.Combine(dir, "missing.log"));
			Assert.Equal("error", Cache.UrlFor("aaa.html"));
		}

		// Content before the doctype has its own Results leaf and triage switch.
		[Fact]
		public void ContentBeforeDoctype_HasItsOwnLeafAndSwitch()
		{
			Assert.Equal("CONTENT_BEFORE_DOCTYPE", FindingsCatalog.LeafType("MALFORMED_HTML", "CONTENT_BEFORE_DOCTYPE"));
			Assert.Equal("CONTENT_BEFORE_DOCTYPE", TriageSwitches.IdOf("MALFORMED_HTML", "CONTENT_BEFORE_DOCTYPE"));
			Assert.Equal("MALFORMED_HTML", FindingsCatalog.LeafType("MALFORMED_HTML:12", "unexpected end tag"));
			Assert.Equal("MALFORMED_HTML", TriageSwitches.IdOf("MALFORMED_HTML", "unexpected end tag"));
		}

		// A site's CMS tenant and production group are hidden like its host; the default tenant is not a name.
		[Fact]
		public void ScreenMask_HidesTenantAndProductionGroup()
		{
			var mask = new CrawlerSuite.Core.Display.ScreenMask();
			mask.Learn(new Config
			{
				Sites =
				[
					new SiteConfig { Name = "S", Url = "https://a.example.test/", IsPrimary = true, Tenant = "xq012", ProductionGroup = "pgq7" },
					new SiteConfig { Name = "T", Url = "https://b.example.test/", Tenant = Config.DefaultTenant },
				],
			}, null);
			var level = CrawlerSuite.Core.Display.MaskLevel.HostAndPath;
			Assert.DoesNotContain("xq012", mask.Apply("content/xyzzy/xq012/haus", level), StringComparison.Ordinal);
			Assert.DoesNotContain("pgq7", mask.Apply("group pgq7", level), StringComparison.Ordinal);
			Assert.Equal("the default value", mask.Apply("the default value", level));
		}

		// Without logs 10/22 the ledger is not matched; before a removal it is backed up.
		[Fact]
		public void Triage_WithoutFindingLogs_IsNotAMatch()
		{
			Assert.False(TriageSource.HasFindingLogs(dir));
		}

		[Fact]
		public void Ledger_Backup_KeepsTheNewestFive()
		{
			var path = Path.Combine(dir, "triage-ledger.json");
			for (int i = 1; i <= 7; i++)
			{
				File.WriteAllText(path, $"v{i}");
				TriageLedger.Backup(path);
			}

			Assert.Equal("v7", File.ReadAllText(path + ".1"));
			Assert.Equal("v3", File.ReadAllText(path + ".5"));
			Assert.False(File.Exists(path + ".6"));
			Assert.Equal("v7", File.ReadAllText(path)); // the ledger itself is untouched
		}
	}
}
