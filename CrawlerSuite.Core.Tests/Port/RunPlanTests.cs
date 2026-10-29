using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.IO;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Formats;
using CrawlerSuite.Core.Host;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>The phases and steps a run plans before it starts.</summary>
	public class RunPlanTests
	{
		private static Config DefaultConfig() => new() { CmsContentList = null };

		private static PlannedPhase Phase(IReadOnlyList<PlannedPhase> plan, string title) =>
			plan.Single(p => p.Title == title);

		[Fact]
		public void Crawl_ListsEveryPhaseInRunOrder()
		{
			var plan = RunPlan.Build(DefaultConfig(), replay: false);

			Assert.Equal(
				[RunPhases.Connectivity, RunPhases.Crawl, RunPhases.Prepare, RunPhases.ExtraDownload, RunPhases.Analysis,
				 RunPhases.SpellCheck, RunPhases.Output, RunPhases.Dictionaries, RunPhases.Complete],
				plan.Select(p => p.Title));
		}

		[Fact]
		public void Crawl_RunsTheCrawlPhase()
		{
			var crawl = Phase(RunPlan.Build(DefaultConfig(), replay: false), RunPhases.Crawl);

			Assert.False(crawl.Skipped);
			Assert.Equal([RunSteps.MainCrawl, RunSteps.SettleFileTypes], crawl.Steps);
		}

		[Fact]
		public void Replay_KeepsTheCrawlPhaseMarkedSkipped()
		{
			var plan = RunPlan.Build(DefaultConfig(), replay: true);

			var crawl = Phase(plan, RunPhases.Crawl);
			Assert.True(crawl.Skipped);
			Assert.Empty(crawl.Steps);
			Assert.Contains("replay", crawl.SkipReason);
		}

		[Fact]
		public void QuickReRun_SkipsConnectivity_AndIsAReplay()
		{
			var plan = RunPlan.Build(DefaultConfig(), replay: true, skipConnectivity: true);

			var connectivity = Phase(plan, RunPhases.Connectivity);
			Assert.True(connectivity.Skipped);
			Assert.Empty(connectivity.Steps);
			Assert.Contains("quick re-run", connectivity.SkipReason);
			Assert.True(Phase(plan, RunPhases.Crawl).Skipped);
		}

		[Fact]
		public void Replay_SettlesFileTypesUnderPrepare()
		{
			var prepare = Phase(RunPlan.Build(DefaultConfig(), replay: true), RunPhases.Prepare);

			Assert.Equal(RunSteps.SettleFileTypes, prepare.Steps[0]);
		}

		[Fact]
		public void Crawl_PrepareDoesNotSettle()
		{
			var prepare = Phase(RunPlan.Build(DefaultConfig(), replay: false), RunPhases.Prepare);

			Assert.DoesNotContain(RunSteps.SettleFileTypes, prepare.Steps);
			Assert.Equal(RunSteps.ProjectSize, prepare.Steps[0]);
		}

		[Fact]
		public void ExtraDownload_SkippedWhenOffInConfig()
		{
			var extra = Phase(RunPlan.Build(DefaultConfig(), replay: false), RunPhases.ExtraDownload);

			Assert.True(extra.Skipped);
			Assert.Contains("configuration", extra.SkipReason);
		}

		[Fact]
		public void ExtraDownload_PlannedWhenOn()
		{
			var config = new Config { CmsContentList = new CmsContentListConfig { PostCrawlPass = true } };

			var extra = Phase(RunPlan.Build(config, replay: false), RunPhases.ExtraDownload);

			Assert.False(extra.Skipped);
			Assert.Equal([RunSteps.ExtraDownload, RunSteps.SettleFileTypes, RunSteps.CrawlerIndex], extra.Steps);
		}

		[Fact]
		public void ExtraDownload_SkippedOnReplayEvenWhenOn()
		{
			var config = new Config { CmsContentList = new CmsContentListConfig { PostCrawlPass = true } };

			var extra = Phase(RunPlan.Build(config, replay: true), RunPhases.ExtraDownload);

			Assert.True(extra.Skipped);
			Assert.Contains("replay", extra.SkipReason);
		}

		[Fact]
		public void Analysis_DefaultsIncludeBase64AndAssetQuality()
		{
			var analysis = Phase(RunPlan.Build(DefaultConfig(), replay: false), RunPhases.Analysis);

			Assert.Contains(RunSteps.Base64Assets, analysis.Steps);
			Assert.Contains(RunSteps.AssetQuality, analysis.Steps);
			Assert.Equal(RunSteps.LoadIndex, analysis.Steps[0]);
			Assert.Equal(RunSteps.ContentQuality, analysis.Steps[^1]);
			Assert.Equal(RunSteps.LoadDictionaries, analysis.Steps[^2]); // the content checks ask the dictionaries
		}

		[Fact]
		public void Analysis_LeavesOutChecksSwitchedOff()
		{
			var config = DefaultConfig();
			config.Base64AssetFileExtensions = [];
			config.AssetQuality.CheckMetadataLeakage = false;
			config.AssetQuality.CheckDimensions = false;
			config.AssetQuality.CheckSize = false;

			var analysis = Phase(RunPlan.Build(config, replay: false), RunPhases.Analysis);

			Assert.DoesNotContain(RunSteps.Base64Assets, analysis.Steps);
			Assert.DoesNotContain(RunSteps.AssetQuality, analysis.Steps);
		}

		[Fact]
		public void SpellCheck_OptInScansOnlyWhenOn()
		{
			var off = Phase(RunPlan.Build(DefaultConfig(), replay: false), RunPhases.SpellCheck);
			Assert.Equal([RunSteps.SpellCheck], off.Steps);

			var config = DefaultConfig();
			config.SpellCheckEngine.SpellCheckJavaScript.BulkScanPageScript = true;
			config.SpellCheckEngine.SpellCheckJavaScript.ScanScriptFilesInDownload = true;
			var on = Phase(RunPlan.Build(config, replay: false), RunPhases.SpellCheck);
			Assert.Equal([RunSteps.SpellCheck, RunSteps.BulkPageScriptScan, RunSteps.JsFileScan], on.Steps);
		}

		[Theory]
		[InlineData("Off", true, true, false)]
		[InlineData("Report", false, false, false)]
		[InlineData("Report", true, false, true)]
		[InlineData("interactive", false, true, true)]
		[InlineData("Nonsense", true, true, false)]
		public void Dictionaries_FollowsTheMaintenanceMode(string mode, bool user, bool site, bool runs)
		{
			var config = DefaultConfig();
			config.DictionaryMaintenance.Mode = mode;
			config.DictionaryMaintenance.UpdateUserDictionary = user;
			config.DictionaryMaintenance.UpdateSiteSpecificDictionary = site;

			var phase = Phase(RunPlan.Build(config, replay: false), RunPhases.Dictionaries);

			Assert.Equal(!runs, phase.Skipped);
			Assert.Equal(runs ? [RunSteps.DictionaryMaintenance] : [], phase.Steps);
		}

		[Fact]
		public void EveryPhase_HasNoRepeatedStep()
		{
			var config = new Config { CmsContentList = new CmsContentListConfig { PostCrawlPass = true } };
			foreach (var replay in new[] { false, true })
			{
				foreach (var phase in RunPlan.Build(config, replay))
				{
					Assert.Equal(phase.Steps.Count, phase.Steps.Distinct().Count());
				}
			}
		}
	}

	/// <summary>Step progress as the engine reports it: ids, totals, throttling.</summary>
	[Collection("Logger")]
	public class RunUiStepProgressTests : IDisposable
	{
		private readonly RecordingHost host = new();

		public RunUiStepProgressTests() => RunUi.Attach(host);

		public void Dispose() => RunUi.Attach(null);

		[Fact]
		public void BeginStep_GivesEachStepANewId()
		{
			RunUi.BeginStep("A");
			RunUi.BeginStep("B");

			Assert.Equal([(1, "A"), (2, "B")], host.Started);
		}

		[Fact]
		public void StepTotal_ReportsZeroOfTotalAtOnce()
		{
			RunUi.BeginStep("A");
			RunUi.StepTotal(40);

			Assert.Equal((1, 0, 40), host.Progress.Single());
		}

		[Fact]
		public void StepAdvance_AlwaysReportsTheLastFile()
		{
			RunUi.BeginStep("A");
			RunUi.StepTotal(500);
			for (int i = 0; i < 500; i++)
			{
				RunUi.StepAdvance();
			}

			Assert.Equal((1, 500, 500), host.Progress[^1]);
		}

		[Fact]
		public void StepAdvance_IsThrottled()
		{
			RunUi.BeginStep("A");
			RunUi.StepTotal(10_000);
			for (int i = 0; i < 10_000; i++)
			{
				RunUi.StepAdvance();
			}

			// Total, perhaps one or two timed reports, and the final one — never one per file.
			Assert.InRange(host.Progress.Count, 2, 20);
		}

		[Fact]
		public void StepAdvance_FromParallelLoops_CountsEveryFile()
		{
			RunUi.BeginStep("A");
			RunUi.StepTotal(2_000);
			System.Threading.Tasks.Parallel.For(0, 2_000, _ => RunUi.StepAdvance());

			Assert.Contains((1, 2_000, 2_000), host.Progress);
		}

		[Fact]
		public void BeginStep_ResetsTheCount()
		{
			RunUi.BeginStep("A");
			RunUi.StepTotal(3);
			RunUi.StepAdvance();
			RunUi.BeginStep("B");
			RunUi.StepTotal(2);
			RunUi.StepAdvance();
			RunUi.StepAdvance();

			Assert.Equal((2, 2, 2), host.Progress[^1]);
		}

		[Fact]
		public void FinishedStep_SendsTheCountOfItsResultsTab()
		{
			var dir = Path.Combine(Path.GetTempPath(), "cs-stepfindings-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				File.WriteAllLines(Path.Combine(dir, OutputFormats.ByNumber("07")!.PrimaryFileName),
					["BrokenUrl,Kind,Referrer,AsWritten,Grade,Evidence", "https://example.test/a,link,,,,", "https://example.test/b,link,,,,"]);
				RunUi.SetSnapshot(dir);

				RunUi.BeginStep(RunSteps.BrokenLinks);
				RunUi.BeginStep(RunSteps.SeoData);   // no tab of its own: nothing sent when it ends
				RunUi.WriteHeader("Next phase");

				Assert.Equal([(RunSteps.BrokenLinks, 2)], host.Findings);
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}

		[Fact]
		public void FinishedStep_FileNotWritten_SendsNothing()
		{
			var dir = Path.Combine(Path.GetTempPath(), "cs-stepfindings-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				RunUi.SetSnapshot(dir);
				RunUi.BeginStep(RunSteps.BrokenLinks);
				RunUi.BeginStep(RunSteps.SeoData);

				Assert.Empty(host.Findings);
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}

		[Fact]
		public void StepRow_CarriesTimeAndMemoryCost()
		{
			RunUi.WriteHeader("Analysis");
			var junk = new List<byte[]>();
			for (int i = 0; i < 200; i++)
			{
				junk.Add(new byte[64 * 1024]);
			}

			RunUi.WriteStepRow("Work", $"{junk.Count} blocks");

			var row = host.Rows.Single();
			Assert.Equal("Analysis", row.Phase);
			Assert.NotNull(row.Elapsed);
			Assert.NotNull(row.Cost);
			Assert.True(row.Cost!.AllocatedBytes >= 200L * 64 * 1024 / 2, $"allocated {row.Cost.AllocatedBytes}");
			Assert.True(row.Cost.Pause >= TimeSpan.Zero);
		}

		[Fact]
		public void StepRow_BeforeAnyPhase_HasNoTime_UnlessMarked()
		{
			RunUi.WriteStepRow("Early", "x");
			RunUi.MarkStep();
			RunUi.WriteStepRow(RunPhases.Prepare, "Clean sweep", "y");

			Assert.Null(host.Rows[0].Elapsed);
			Assert.Null(host.Rows[0].Cost);
			Assert.Equal(RunPhases.Prepare, host.Rows[1].Phase);
			Assert.NotNull(host.Rows[1].Elapsed);
			Assert.NotNull(host.Rows[1].Cost);
		}

		[Fact]
		public void MemoryHeld_IsSampledPerStepAndRun_AndStartsAgainWithEachRun()
		{
			RunUi.Attach(host, run: true);
			RunUi.MarkStep();
			var keep = new byte[8 * 1024 * 1024];
			System.Array.Fill(keep, (byte)1); // touched, so it is held
			RunUi.WriteStepRow(RunPhases.Prepare, "Water", "x");
			GC.KeepAlive(keep);
			RunUi.WriteStepRow(RunPhases.Prepare, "Ice", "y");

			var water = host.Rows[0].Cost!;
			var ice = host.Rows[1].Cost!;
			Assert.True(water.PeakMemoryBytes > 0);
			Assert.True(water.AfterCleanUpBytes >= 0);
			Assert.True(ice.PeakMemoryBytes > 0);
			Assert.Equal(System.Math.Max(water.PeakMemoryBytes, ice.PeakMemoryBytes), RunUi.RunPeakMemory);

			RunUi.Attach(null);
			Assert.Equal(0, RunUi.RunPeakMemory);
			RunUi.Attach(host, run: true);
			Assert.True(RunUi.RunPeakMemory > 0);
		}

		[Fact]
		public void AHostWithoutARun_SamplesNothing()
		{
			// The start-up checks attach a host too; they are no run, so nothing is sampled and the
			// first run of the process is still to come.
			RunUi.Attach(host);
			Assert.Equal(0, RunUi.RunPeakMemory);
			RunUi.Attach(host, run: true);
			Assert.True(RunUi.RunPeakMemory > 0);
		}

		[Fact]
		public void NoHost_StepCallsAreHarmless()
		{
			RunUi.Attach(null);
			RunUi.BeginStep("A");
			RunUi.StepTotal(2);
			RunUi.StepAdvance();

			Assert.Empty(host.Started);
		}

		private sealed class RecordingHost : IRunHost
		{
			private readonly Lock gate = new();

			public List<(int, string)> Started { get; } = [];

			public List<(int, int, int)> Progress { get; } = [];

			public List<(string, int)> Findings { get; } = [];

			public void WorkTotals(StepWork totals) { }

			public void StepFindings(string label, int count)
			{
				lock (gate)
				{
					Findings.Add((label, count));
				}
			}

			public void StepStarted(int id, string label)
			{
				lock (gate)
				{
					Started.Add((id, label));
				}
			}

			public void StepProgress(int id, int done, int total)
			{
				lock (gate)
				{
					Progress.Add((id, done, total));
				}
			}

			public void Phase(string title) { }
			public List<StepRow> Rows { get; } = [];

			public void Step(StepRow row)
			{
				lock (gate)
				{
					Rows.Add(row);
				}
			}
			public void Message(MessageTone tone, string text) { }
			public void Notice(Notice notice) { }
			public void ConfigCheck(string subtitle, IReadOnlyList<CheckBlock> blocks) { }
			void IRunOutput.Progress(CrawlProgress progress) { }

			public SnapshotChoice AskSnapshotChoice(SnapshotPrompt prompt) => throw new NotSupportedException();
			public IncompleteSnapshotChoice AskIncompleteSnapshot(IncompleteSnapshotPrompt prompt) => throw new NotSupportedException();
			public bool AskCleanSweep(string snapshotPath) => throw new NotSupportedException();
			public bool AskEmptyDownloadStartFresh(string downloadDirectory) => throw new NotSupportedException();
			public bool AskProceedWithStaleContentList(CmsContentListFreshness freshness) => throw new NotSupportedException();
			public IReadOnlyCollection<string> AskOrphansToRename(IReadOnlyList<string> orphans, string backupExtension) => throw new NotSupportedException();
			public bool AskReSettle(ReSettlePrompt prompt) => throw new NotSupportedException();
			public bool AskRetryLockedFile(string displayName, string error, bool isDelete) => false;
			public bool AskRetryLockedCsv(string path, string error) => false;
			public (string User, string Password)? AskProxySignIn(string proxyUrl, bool typedSignInRefused) => throw new NotSupportedException();
			public bool AskContinueAfterConnectivityFailure(IReadOnlyList<string> details, bool proxyTip) => throw new NotSupportedException();
			public CrawlLimitChoice AskCrawlLimitExceeded(CrawlLimitReport report) => throw new NotSupportedException();
		}
	}

	/// <summary>Which Results tab each step feeds.</summary>
	public class StepFindingsTests
	{
		[Fact]
		public void EveryTarget_IsATabOfTheResultsPage()
		{
			var dir = Path.Combine(Path.GetTempPath(), "cs-stepfindings-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				var tabs = FindingsCatalog.Read(dir);
				foreach (var step in StepFindings.Steps)
				{
					var target = StepFindings.TargetOf(step)!;
					Assert.True(StepFindings.Find(tabs, target) is not null, $"{step} → {target}");
					Assert.True(FindingsCatalog.ReadTab(dir, target.Tab) is not null, target.Tab);
				}
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}

		[Fact]
		public void EveryTarget_IsAPlannedStep()
		{
			var config = new Config { CmsContentList = new CmsContentListConfig { PostCrawlPass = true } };
			config.SpellCheckEngine.SpellCheckJavaScript.BulkScanPageScript = true;
			config.SpellCheckEngine.SpellCheckJavaScript.ScanScriptFilesInDownload = true;
			config.DictionaryMaintenance.Mode = "Report";
			config.DictionaryMaintenance.UpdateUserDictionary = true;
			var planned = RunPlan.Build(config, replay: false).SelectMany(p => p.Steps).ToHashSet();

			Assert.All(StepFindings.Steps, step => Assert.Contains(step, planned));
		}

		[Fact]
		public void StepsWithoutFindings_HaveNoTarget()
		{
			Assert.Null(StepFindings.TargetOf(RunSteps.LoadIndex));
			Assert.Null(StepFindings.TargetOf(RunSteps.Sitemap));
			Assert.Null(StepFindings.TargetOf(RunSteps.SettleFileTypes));
		}

		[Fact]
		public void CountIn_NotWritten_IsNull_Written_IsTheTabCount()
		{
			var dir = Path.Combine(Path.GetTempPath(), "cs-stepfindings-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				var target = StepFindings.TargetOf(RunSteps.BrokenLinks)!;
				Assert.Null(StepFindings.CountIn(FindingsCatalog.Read(dir), target));

				File.WriteAllLines(Path.Combine(dir, OutputFormats.ByNumber("07")!.PrimaryFileName),
					["BrokenUrl,Kind,Referrer,AsWritten,Grade,Evidence", "https://example.test/a,link,,,,"]);
				var tabs = FindingsCatalog.Read(dir);
				Assert.Equal(StepFindings.Find(tabs, target)!.Count, StepFindings.CountIn(tabs, target));
				Assert.Equal(1, StepFindings.CountIn(tabs, target));
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}
	}
}
