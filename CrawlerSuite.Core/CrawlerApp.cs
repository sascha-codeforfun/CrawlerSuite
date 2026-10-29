namespace CrawlerSuite.Core
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using System.IO;
	using System.Linq;
	using System.Text;
	using System.Text.Json;
	using System.Threading;
	using System.Threading.Tasks;
	using CrawlerSuite.Core.Host;
	using CrawlerSuite.Core.Lexicon;
	using CrawlerSuite.Core.Snapshots;

	/// <summary>The outcome of loading the configuration.</summary>
	public sealed record ConfigLoadResult(Config? Config, string ConfigPath, string? ErrorHeading, IReadOnlyList<CheckBlock> ErrorBlocks)
	{
		public bool Ok => Config is not null;
	}

	/// <summary>How a run ended.</summary>
	public enum RunOutcome
	{
		Completed,
		Aborted,
		Halted,
		Failed,
	}

	/// <summary>
	/// The engine's entry points for a host application: process start-up, configuration
	/// loading and the start-up checks, and one complete run (connectivity check, crawl or
	/// replay, analysis).
	/// </summary>
	public static class CrawlerApp
	{
		private static int initialized;

		/// <summary>Once per process: code pages, the application log, the licence notices.</summary>
		public static void InitializeProcess()
		{
			if (System.Threading.Interlocked.Exchange(ref initialized, 1) == 1)
			{
				return;
			}

			// Windows-1252 and other legacy code pages are used as the fallback when a page
			// declares no charset; .NET only knows them once the provider is registered.
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			Logger.Initialize("application.log", AppPaths.BaseDirectory);
			LicenseAssets.EmitIfMissing();
		}

		/// <summary>
		/// Loads and validates the configuration: crawlersuite.config.json next to the
		/// executable, the only configuration file. Never throws: a failure comes back as a
		/// heading plus explanation blocks the host can show.
		/// </summary>
		[RequiresUnreferencedCode("Configuration is loaded with reflection-based JSON.")]
		public static ConfigLoadResult LoadConfiguration()
		{
			var path = AppPaths.ConfigFile;
			try
			{
				var config = Config.LoadFromJson(path);
				Logger.LogInfo($"Configuration loaded from {path}.");
				return new ConfigLoadResult(config, path, null, []);
			}
			catch (FileNotFoundException)
			{
				Logger.LogError($"Configuration file not found: {path}");
				return new ConfigLoadResult(null, path, "Configuration file not found", ConfigErrors.NotFound(path));
			}
			catch (JsonException ex)
			{
				Logger.LogDetailToFile(
					$"Config JSON parse error in {path}: {ex.Message} " +
					$"| Path: {ex.Path} | LineNumber(0-based): {ex.LineNumber} | BytePositionInLine: {ex.BytePositionInLine}");
				return new ConfigLoadResult(null, path, "Configuration file has a formatting error", ConfigErrors.JsonError(path, ex));
			}
			catch (ConfigHaltException halt)
			{
				Logger.LogError($"Configuration halted: {halt.Heading}");
				return new ConfigLoadResult(null, path, halt.Heading, ConfigErrors.FromLines(halt.Lines));
			}
			catch (InvalidOperationException ex)
			{
				Logger.LogError(ex.Message);
				return new ConfigLoadResult(null, path, "Configuration validation failed", ConfigErrors.FromLines(ex.Message.Split('\n')));
			}
			catch (Exception ex)
			{
				Logger.LogError($"Unexpected error loading configuration: {ex}");
				return new ConfigLoadResult(null, path, "Configuration could not be loaded", ConfigErrors.FromLines([ex.Message]));
			}
		}

		/// <summary>
		/// The start-up checks that do not depend on the chosen site: working-folder drive,
		/// dictionary signpost, dictionary checksums, crawl-history regexes, download
		/// exclusions. A failing check reports itself through the host (a config-check
		/// screen) and this returns false.
		/// </summary>
		public static bool RunStartupChecks(Config config, IRunHost host)
		{
			RunUi.Attach(host);
			try
			{
				if (!BaseDirectoryCheck.CheckOrHalt(config))
				{
					return false;
				}

				Signpost.EmitIfUnconfigured(config);

				return Integrity.CheckOrHalt(config)
					&& DownloadExclusionsConfigValidator.CheckOrHalt(config);
			}
			finally
			{
				RunUi.Attach(null);
			}
		}

		/// <summary>
		/// One complete run for one site: the configuration is loaded fresh (site resolution
		/// rewrites it in place), the site resolved, connectivity checked, the snapshot chosen,
		/// the crawl or replay done and every analysis written. Questions go to the host.
		/// A crawl stopped at the total download limit restarts here when the operator asks.
		/// </summary>
		[RequiresUnreferencedCode("Configuration is loaded with reflection-based JSON.")]
		/// <param name="quickReplay">
		/// A quick re-run of the latest snapshot: no connectivity check. The host answers the
		/// snapshot questions itself (latest snapshot, clean sweep, nothing renamed or promoted).
		/// </param>
		/// <param name="findUnneededEntries">
		/// The Dictionaries page asked for the dictionary entries no longer needed: maintenance
		/// runs as Report for this run, followed by a second spell-check pass without the user and
		/// site lists (log 35). Every other result is the same as without it.
		/// </param>
		public static async Task<RunOutcome> RunAsync(string siteName, IRunHost host, bool quickReplay = false, bool findUnneededEntries = false)
		{
			// The proxy sign-in of this job: Windows first, typed only after a refusal; a start
			// over keeps it, the job's end drops it. Never stored.
			var signIn = new ProxySignIn();
			Host.RunOptions.FindUnneededEntries = findUnneededEntries;
			RunUi.Attach(host, run: true);
			try
			{
				while (true)
				{
					var outcome = await RunOnceAsync(siteName, signIn, host, quickReplay).ConfigureAwait(false);
					if (outcome is not (RunOutcome.Completed, true))
					{
						return outcome.Outcome;
					}

					Logger.LogInfo("Starting a fresh crawl after the stopped one, as the operator asked.");
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Run failed: {ex}");
				RunUi.WriteErrorBlock("The run stopped because of an unexpected error", [ex.Message, "Details are in application.log."]);
				return RunOutcome.Failed;
			}
			finally
			{
				Crawl.EndSession();
				RunUi.Attach(null);
				Host.RunOptions.FindUnneededEntries = false;
			}
		}

		[RequiresUnreferencedCode("Configuration is loaded with reflection-based JSON.")]
		private static async Task<(RunOutcome Outcome, bool Recrawl)> RunOnceAsync(
			string siteName, ProxySignIn signIn, IRunHost host, bool quickReplay)
		{
			var load = LoadConfiguration();
			if (!load.Ok)
			{
				host.ConfigCheck(load.ErrorHeading ?? "Configuration", load.ErrorBlocks);
				return (RunOutcome.Halted, false);
			}

			var config = load.Config!;
			var site = config.Sites.FirstOrDefault(s => s.Name.Equals(siteName, StringComparison.Ordinal))
				?? config.Sites.FirstOrDefault(s => s.IsPrimary)
				?? config.Sites.First();

			try
			{
				config.ResolveForSite(site);
				Config.ValidateResolvedSite(config);
			}
			catch (ConfigHaltException halt)
			{
				Logger.LogError($"Halted at setup: {halt.Heading}");
				RunUi.WriteActionBlock(halt.Heading, halt.Lines);
				return (RunOutcome.Halted, false);
			}
			catch (InvalidOperationException ex)
			{
				Logger.LogError(ex.Message);
				RunUi.WriteErrorBlock("Site resolution failed", ex.Message.Split('\n'));
				return (RunOutcome.Halted, false);
			}

			string workingFolder = config.BaseDirectory;
			string url = config.Url;
			string urlDirectory = Findings.SnapshotLocator.FolderName(url);

			var ctx = new CrawlerRunContext
			{
				IsDebugSession = false, // a replay is decided by the snapshot choice (or Re-run latest)
				SkipConnectivity = quickReplay,
			};

			// The whole road up front; the snapshot choice may turn it into a replay later.
			RunUi.Plan(Host.RunPlan.Build(config, quickReplay, quickReplay));
			if (!quickReplay)
			{
				RunUi.WriteHeader(Host.RunPhases.Connectivity);
				RunUi.BeginStep(Host.RunSteps.ConnectivityCheck);
				var connectivityLogPath = Path.Combine(workingFolder, urlDirectory, "connectivity.log");
				if (!await ConnectivityCheck.RunAsync(
						url, config.UseProxy ? config.ProxyUrl : null, connectivityLogPath, silent: false, signIn).ConfigureAwait(false))
				{
					return (RunOutcome.Aborted, false);
				}
			}

			// The sign-in the check settled on: the Windows one (empty), or what was typed after a refusal.
			ctx.ProxyUser = config.UseProxy ? signIn.User : string.Empty;
			ctx.ProxyPassword = config.UseProxy ? signIn.Password : string.Empty;

			ctx.TimeStamp = SnapshotFolder.NewName();
			ctx.RebuildTimestampPaths(Path.Combine(workingFolder, urlDirectory, ctx.TimeStamp));
			ctx.IsLatestSnapshotPath = true;

			var crawlResult = await CrawlOrchestrator.RunAsync(ctx, config, url, urlDirectory, workingFolder).ConfigureAwait(false);
			if (crawlResult == null)
			{
				return (RunOutcome.Aborted, false);
			}

			if (crawlResult.RecrawlRequested)
			{
				return (RunOutcome.Completed, true);
			}

			if (!File.Exists(ctx.CrawlerLogPath) && !ctx.IsDebugSession)
			{
				// A stopped crawl the operator chose not to restart: nothing to analyse.
				return (RunOutcome.Aborted, false);
			}

			var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = config.ResolvedDegreeOfParallelism };
			var analysis = await AnalysisPipeline.RunAsync(ctx, config, url, urlDirectory, workingFolder, options).ConfigureAwait(false);
			if (analysis == null)
			{
				return (RunOutcome.Halted, false);
			}

			int runOfProcess = Interlocked.Increment(ref completedRuns);
			string? archive = config.DebugMode ? ArchiveRun(ctx.SaveDirectory, load.ConfigPath, config, urlDirectory, runOfProcess) : null;

			LastRun = new RunSummary(config.ResolvedSiteLabel, ctx.SaveDirectory, ctx.IsDebugSession, archive);
			RunUi.ReportWorkTotals();
			Logger.LogInfo("Run complete.");
			RunUi.WriteHeader(Host.RunPhases.Complete);
			return (RunOutcome.Completed, false);
		}

		/// <summary>The snapshot the last completed run produced or analysed.</summary>
		public static RunSummary? LastRun { get; private set; }

		/// <summary>Runs completed since the app started (debug mode tells a first run from a later one).</summary>
		private static int completedRuns;

		/// <summary>
		/// Debug mode: archives the run's output files next to the program and compares them with
		/// the previous run of the same snapshot and with its baseline. Never fails the run: a
		/// problem is reported in the run steps.
		/// </summary>
		/// <returns>The run's archive folder, or null when it could not be written.</returns>
		private static string? ArchiveRun(string snapshotDirectory, string configPath, Config config, string urlDirectory, int runOfProcess)
		{
			RunUi.WriteHeader(Host.RunPhases.Debug);
			RunUi.BeginStep(Host.RunSteps.RunArchive);
			string snapshotArchive;
			string runName;
			string runFolder;
			string? previous;
			try
			{
				snapshotArchive = Debugging.RunArchive.SnapshotFolder(Debugging.RunArchive.Root, snapshotDirectory);
				var version = typeof(CrawlerApp).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
				previous = Debugging.RunArchive.Runs(snapshotArchive).LastOrDefault();
				runName = Debugging.RunArchive.NextRunName(snapshotArchive, version);
				runFolder = Path.Combine(snapshotArchive, runName);
				var (files, bytes) = Debugging.RunArchive.CopyRunOutput(snapshotDirectory, runFolder);
				var nl = Environment.NewLine;
				var info = new StringBuilder()
					.Append($"CrawlerSuite {version} · archived run {runName}").Append(nl)
					.Append($"Snapshot: {Path.GetFileName(snapshotDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}").Append(nl)
					.Append(runOfProcess == 1
						? "Run of this process: 1 (the first since the app started)"
						: $"Run of this process: {runOfProcess} (the app was not restarted before it)").Append(nl)
					.Append($"Test levers: {Lexicon.Bundle.ActiveLevers.Describe()}").Append(nl)
					.Append($"Configuration: SHA-256 {Debugging.RunArchive.Checksum(configPath)}").Append(nl)
					.Append($"User dictionary after the run: SHA-256 {Debugging.RunArchive.Checksum(AppPaths.Dictionary(config.CustomDictionaryFile))}").Append(nl)
					.Append($"Site dictionary after the run: SHA-256 {Debugging.RunArchive.Checksum(AppPaths.Dictionary($"{urlDirectory}.dic"))}").Append(nl)
					.Append($"Files: {files} · {bytes / 1048576.0:0.0} MB").Append(nl);
				File.WriteAllText(Path.Combine(runFolder, Debugging.RunArchive.RunInfoFile), info.ToString());
				RunUi.WriteStepRow("Run archive",
					$"{runName} · {files} file(s) · {bytes / 1048576.0:0.0} MB · run {runOfProcess} of this process");
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Run archive: {ex}");
				RunUi.WriteStepRow("Run archive", $"not written: {ex.Message}");
				return null;
			}

			RunUi.BeginStep(Host.RunSteps.RunCompare);
			Compare("Compare with previous", previous, Debugging.RunArchive.CompareFile, "no earlier run of this snapshot");
			var baseline = Debugging.RunArchive.Baseline(snapshotArchive);
			Compare("Compare with baseline", baseline, Debugging.RunArchive.CompareBaselineFile, "no run marked as baseline");
			return runFolder;

			void Compare(string label, string? other, string reportFile, string none)
			{
				if (other is null)
				{
					RunUi.WriteStepRow(label, none, dimmed: true);
					return;
				}

				try
				{
					var result = Debugging.RunArchive.Compare(Path.Combine(snapshotArchive, other), runFolder);
					File.WriteAllText(Path.Combine(runFolder, reportFile), Debugging.RunArchive.Report(other, runName, result));
					RunUi.WriteStepRow(label, $"{other}: {Debugging.RunArchive.Summary(result)}");
				}
				catch (Exception ex)
				{
					Logger.LogWarning($"{label}: {ex}");
					RunUi.WriteStepRow(label, $"not done: {ex.Message}");
				}
			}
		}
	}

	/// <summary>Where the last completed run's results are, and its debug archive (null outside debug mode).</summary>
	public sealed record RunSummary(string SiteLabel, string SnapshotDirectory, bool WasReplay, string? ArchiveFolder = null);
}
