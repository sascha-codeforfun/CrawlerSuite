namespace CrawlerSuite.Core.Host
{
	using System;
	using System.Collections.Generic;

	/// <summary>The phase titles of a run, as the engine announces them.</summary>
	public static class RunPhases
	{
		public const string Connectivity = "Connectivity";
		public const string Crawl = "Crawl";
		public const string Prepare = "Prepare";
		public const string ExtraDownload = "Extra download";
		public const string Analysis = "Analysis";
		public const string SpellCheck = "Spell-check";
		public const string Output = "Output";
		public const string Dictionaries = "Dictionaries";
		public const string Debug = "Debug";
		public const string Complete = "Complete";
	}

	/// <summary>The step names of a run, shared by the plan and the engine that starts them.</summary>
	public static class RunSteps
	{
		public const string ConnectivityCheck = "Connectivity check";

		public const string MainCrawl = "Main crawl";
		public const string SettleFileTypes = "Settle file types";

		public const string ProjectSize = "Project size";
		public const string Redirects = "Redirects";
		public const string CrawlerIndex = "Crawler index";
		public const string IndexIntegrity = "Index integrity";
		public const string ContentLists = "Pages not directly discoverable";

		public const string ExtraDownload = "Extra download";

		public const string LoadIndex = "Load crawler index";
		public const string OversizedFiles = "Oversized files";
		public const string Sitemap = "Sitemap";
		public const string BrokenLinks = "Broken links (404)";
		public const string SeoData = "SEO data";
		public const string CanonicalLinks = "Canonical links";
		public const string PdfQuality = "PDF quality";
		public const string Base64Assets = "Base64 assets";
		public const string AssetQuality = "Asset quality";
		public const string ResourceBloat = "Resource bloat";
		public const string ResourceBloatBaseline = "Resource bloat (baseline)";
		public const string BoilerplatePrune = "Boilerplate prune";
		public const string SelfLinks = "Self-links";
		public const string ContentQuality = "Content quality";

		public const string LoadDictionaries = "Load dictionaries";
		public const string SpellCheck = "Spell-check";
		public const string BulkPageScriptScan = "Bulk page-script scan";
		public const string JsFileScan = "JS-file scan";

		public const string SitemapCopy = "Sitemap copy";

		public const string DictionaryMaintenance = "Dictionary maintenance";
		public const string UnneededEntries = "Entries no longer needed";

		public const string RunArchive = "Run archive";
		public const string RunCompare = "Compare";
	}

	/// <summary>
	/// Works out the phases and steps of a run from the configuration, before it starts, so the
	/// operator sees the whole road: phases that will not run are kept and marked skipped;
	/// steps switched off in the configuration are left out.
	/// </summary>
	public static class RunPlan
	{
		/// <param name="config">The configuration resolved for the site.</param>
		/// <param name="replay">True when an existing snapshot is analysed again (no downloads).</param>
		/// <param name="skipConnectivity">True for a quick re-run, which does not check the connection.</param>
		public static IReadOnlyList<PlannedPhase> Build(Config config, bool replay, bool skipConnectivity = false)
		{
			ArgumentNullException.ThrowIfNull(config);

			var phases = new List<PlannedPhase>
			{
				skipConnectivity
					? new(RunPhases.Connectivity, [], Skipped: true, SkipReason: "quick re-run — no network needed")
					: new(RunPhases.Connectivity, [RunSteps.ConnectivityCheck]),
				replay
					? new(RunPhases.Crawl, [], Skipped: true, SkipReason: "replay of a snapshot")
					: new(RunPhases.Crawl, [RunSteps.MainCrawl, RunSteps.SettleFileTypes]),
			};

			// On a replay the file types are settled under Prepare (the crawl phase does not run).
			var prepare = new List<string>();
			if (replay)
			{
				prepare.Add(RunSteps.SettleFileTypes);
			}

			prepare.AddRange([RunSteps.ProjectSize, RunSteps.Redirects, RunSteps.CrawlerIndex,
				RunSteps.IndexIntegrity, RunSteps.ContentLists]);
			phases.Add(new(RunPhases.Prepare, prepare));

			bool extraEnabled = config.CmsContentList?.PostCrawlPass ?? false;
			phases.Add(replay
				? new(RunPhases.ExtraDownload, [], Skipped: true, SkipReason: "replay of a snapshot")
				: !extraEnabled
					? new(RunPhases.ExtraDownload, [], Skipped: true, SkipReason: "off in the configuration")
					: new(RunPhases.ExtraDownload, [RunSteps.ExtraDownload, RunSteps.SettleFileTypes, RunSteps.CrawlerIndex]));

			var analysis = new List<string>
			{
				RunSteps.LoadIndex, RunSteps.OversizedFiles, RunSteps.Sitemap,
			};

			// Asset quality before broken links: that pass also looks up who uses each flagged image.
			if (config.AssetQuality.IsEnabled)
			{
				analysis.Add(RunSteps.AssetQuality);
			}

			analysis.AddRange([RunSteps.BrokenLinks, RunSteps.SeoData, RunSteps.CanonicalLinks, RunSteps.PdfQuality]);
			if (config.Base64AssetFileExtensions.Count > 0)
			{
				analysis.Add(RunSteps.Base64Assets);
			}

			analysis.AddRange([RunSteps.ResourceBloat, RunSteps.ResourceBloatBaseline,
				RunSteps.BoilerplatePrune, RunSteps.SelfLinks, RunSteps.LoadDictionaries, RunSteps.ContentQuality]);
			phases.Add(new(RunPhases.Analysis, analysis));

			// The dictionaries load in the analysis phase, before the content checks that ask them.
			var spell = new List<string> { RunSteps.SpellCheck };
			if (config.SpellCheckEngine.SpellCheckJavaScript.BulkScanPageScript)
			{
				spell.Add(RunSteps.BulkPageScriptScan);
			}

			if (config.SpellCheckEngine.SpellCheckJavaScript.ScanScriptFilesInDownload)
			{
				spell.Add(RunSteps.JsFileScan);
			}

			phases.Add(new(RunPhases.SpellCheck, spell));
			phases.Add(new(RunPhases.Output, [RunSteps.SitemapCopy]));
			phases.Add(DictionaryMaintenanceRuns(config)
				? new(RunPhases.Dictionaries, RunOptions.FindUnneededEntries
					? [RunSteps.DictionaryMaintenance, RunSteps.UnneededEntries]
					: [RunSteps.DictionaryMaintenance])
				: new(RunPhases.Dictionaries, [], Skipped: true, SkipReason: "off in the configuration"));
			if (config.DebugMode)
			{
				phases.Add(new(RunPhases.Debug, [RunSteps.RunArchive, RunSteps.RunCompare]));
			}

			phases.Add(new(RunPhases.Complete, []));
			return phases;
		}

		/// <summary>The same test the maintenance step makes before it starts.</summary>
		internal static bool DictionaryMaintenanceRuns(Config config)
		{
			var maint = config.DictionaryMaintenance;
			var mode = (maint.Mode ?? "Off").Trim();
			bool on = mode.Equals("Report", StringComparison.OrdinalIgnoreCase)
				|| mode.Equals("Interactive", StringComparison.OrdinalIgnoreCase)
				|| RunOptions.FindUnneededEntries; // the Dictionaries page asked for it: Report for this run
			return on && (maint.UpdateUserDictionary || maint.UpdateSiteSpecificDictionary);
		}
	}

	/// <summary>
	/// Options of the run in progress that come from the screen, not the configuration. One run
	/// at a time per process, like <see cref="RunUi"/>; set by <see cref="CrawlerApp.RunAsync"/> and
	/// cleared when the run ends.
	/// </summary>
	public static class RunOptions
	{
		/// <summary>
		/// The Dictionaries page asked for the entries no longer needed: dictionary maintenance runs
		/// as Report for this run, and a second spell-check pass without the user and site lists
		/// finds the entries the app accepts without (log 35).
		/// </summary>
		public static bool FindUnneededEntries { get; internal set; }
	}
}
