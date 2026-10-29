using System.Diagnostics.CodeAnalysis;
using System.Text;
using CrawlerSuite.Core.Host;
using CrawlerSuite.Core.Logging;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.Html;
using CrawlerSuite.Core.Urls;

namespace CrawlerSuite.Core
{
	// ── AnalysisPipeline ──────────────────────────────────────────────────────
	//
	// The pipeline operates on a complete, indexed downloaded snapshot. All
	// path mutation happened earlier (during interactive prompts in phase 1);
	// ctx fields are read-only here.
	//
	// Three issue collections — pdfIssues, base64Issues, and canonicalIssues —
	// must survive to the caller's end-of-run merge. They are returned
	// via AnalysisPipelineResult rather than mutating shared state.
	//
	// ─────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Values that must survive AnalysisPipeline.RunAsync to reach the
	/// caller's end-of-run IssueTracking merge. Everything else is
	/// either in ctx, on disk, or already handled by the spell-check and
	/// content-quality triage helpers.
	///
	/// CanonicalIssues are surfaced here rather than merged mid-
	/// pipeline — see Step_AnalyseCanonicals for the rationale.
	/// </summary>
	internal sealed record AnalysisPipelineResult(
		List<IssueTracking.IssueRecord> PdfIssues,
		List<IssueTracking.IssueRecord> Base64Issues,
		List<IssueTracking.IssueRecord> CanonicalIssues,
		List<IssueTracking.IssueRecord> AssetIssues);

	internal static class AnalysisPipeline
	{
		// ── Spell-check shared state ─────────────────────────────────────────
		// Populated by Step_LoadDictionaries, read by Step_RunSpellCheck and
		// its helpers. Static because the spell-check helpers are static and
		// these need to be reachable without threading them through every call.

		// Every configured language from the start; each dictionary loads when first asked for and
		// all are let go after the run's last use (Step_ReleaseDictionaries).
		private static Lexicon.BundleSet _dictionaryBundles = Lexicon.BundleSet.Empty;

		// Log 27 asks every dictionary whether a script string resembles a word: written after the
		// script scans (Step_WriteSuggestions), which load those dictionaries anyway, so the
		// spell-check holds only the languages its pages need.
		private static Action? _writeSuggestions;

		// The loaded dictionaries as languages and flavours: a page's language tag to the key of the
		// dictionary that checks it (Step_LoadDictionaries sets it).
		private static Lexicon.DictionaryFlavours _flavours = new([]);

		/// <summary>The key of the loaded dictionary for a language tag, or null (see <see cref="Lexicon.DictionaryFlavours.Resolve"/>).</summary>
		private static string? LanguageKey(string tag) => _flavours.Resolve(tag);

		// One-line summary of the foreign-language dictionary's load state, shown next to
		// the Dictionaries banner. The foreign dict is opt-in (empty default), and a
		// silently-skipped one would leave the operator guessing why it has no effect — so
		// its state is made visible: off / a word count / NOT FOUND.
		private static string _foreignDictionarySummary = "off (CustomForeignDictionaryFile not set)";

		private static readonly object _fileLock = new();

		// ── Top-level orchestrator ───────────────────────────────────────────

		/// <summary>
		/// Runs the full post-crawl analysis pipeline. Returns null if the
		/// run should abort before the end-of-run merge (e.g. dictionary
		/// validation failed). Returns the issue collections that need to
		/// survive to the end-of-run merge otherwise.
		/// </summary>
		[RequiresUnreferencedCode("Calls steps that use reflection-based config loading.")]
		internal static async Task<AnalysisPipelineResult?> RunAsync(
			CrawlerRunContext ctx,
			Config config,
			string url,
			string urlDirectory,
			string workingFolder,
			ParallelOptions options)
		{
			try
			{
				return await RunStepsAsync(ctx, config, url, urlDirectory, workingFolder, options).ConfigureAwait(false);
			}
			finally
			{
				// Nothing uses the dictionaries after the pipeline (maintenance is its last step): let
				// them go, so the app does not hold them once the run is done, however it ended.
				Step_ReleaseDictionaries();
			}
		}

		[RequiresUnreferencedCode("Calls steps that use reflection-based config loading.")]
		private static async Task<AnalysisPipelineResult?> RunStepsAsync(
			CrawlerRunContext ctx,
			Config config,
			string url,
			string urlDirectory,
			string workingFolder,
			ParallelOptions options)
		{
			RunUi.WriteHeader(RunPhases.Analysis);
			RunUi.BeginStep(RunSteps.LoadIndex);
			Step_LoadUrlCache(ctx);

			// Surface ticket-metadata-lookup config mistakes early — empty
			// fields in tickets are otherwise silent at runtime.
			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			RunUi.BeginStep(RunSteps.OversizedFiles);
			Step_WriteOversizedLog(ctx, config);

			List<IssueTracking.IssueRecord> canonicalIssues;
			List<IssueTracking.IssueRecord> pdfIssues;
			List<IssueTracking.IssueRecord> base64Issues;
			List<IssueTracking.IssueRecord> assetIssues;
			using (Logger.QuietConsole())
			{
				RunUi.BeginStep(RunSteps.Sitemap);
				Step_GenerateSitemap(ctx, config);

				// Asset quality before broken links: the broken-links pass over the pages and stylesheets
				// also finds who uses each image flagged for triage (log 38), in the same read.
				if (config.AssetQuality.IsEnabled)
				{
					RunUi.BeginStep(RunSteps.AssetQuality);
				}

				assetIssues = Step_AnalyseAssetQuality(ctx, config);
				RunUi.BeginStep(RunSteps.BrokenLinks);
				Step_Process404s(ctx, url, config, ImagesToPlace(assetIssues));
				RunUi.BeginStep(RunSteps.SeoData);
				await Step_ExtractSeoData(ctx, config);
				RunUi.BeginStep(RunSteps.CanonicalLinks);
				canonicalIssues = Step_AnalyseCanonicals(ctx, config);

				RunUi.BeginStep(RunSteps.PdfQuality);
				pdfIssues = Step_AnalysePdfQuality(ctx);
				if (config.Base64AssetFileExtensions.Count > 0)
				{
					RunUi.BeginStep(RunSteps.Base64Assets);
				}

				base64Issues = Step_ExtractBase64Assets(ctx, config);

				RunUi.BeginStep(RunSteps.ResourceBloat);
				Step_AnalyseResourceBloat(ctx, config);
				RunUi.BeginStep(RunSteps.ResourceBloatBaseline);
				Step_AnalyseResourceBloatBaseline(ctx, config);

				RunUi.BeginStep(RunSteps.BoilerplatePrune);
				Step_BoilerplatePrune(ctx, config);
				RunUi.BeginStep(RunSteps.SelfLinks);
				Step_ScanSelfLinks(ctx, config);
				// The dictionaries load before the content checks: the hyphen check asks them whether a
				// suspended head lost its joiner ("Stadt- Landhaus" — "Stadthaus" is a word).
				RunUi.BeginStep(RunSteps.LoadDictionaries);
				bool dictionariesLoaded;
				using (Logger.QuietConsole())
				{
					dictionariesLoaded = Step_LoadDictionaries(urlDirectory, config);
				}

				// Step_LoadDictionaries can fail config-validation. Bail out rather than
				// continuing with no dictionaries.
				if (!dictionariesLoaded)
				{
					return null;
				}

				RunUi.WriteStepRow("Languages",
					_dictionaryBundles.Count > 0 ? string.Join(" · ", _dictionaryBundles.Keys) : "(none)");
				RunUi.WriteStepRow("Dictionaries", $"{_dictionaryBundles.Count} configured · each loads when first needed");
				RunUi.WriteStepRow("Foreign words", _foreignDictionarySummary);

				RunUi.BeginStep(RunSteps.ContentQuality);
				Step_RunContentQualityChecks(ctx, config, urlDirectory);
			}

			// ── SPELL-CHECK phase ────────────────────────────────────────────
			RunUi.WriteHeader(RunPhases.SpellCheck);
			RunUi.BeginStep(RunSteps.SpellCheck);
			using (Logger.QuietConsole())
			{
				await Step_RunSpellCheck(ctx, config);
			}
			RunUi.WriteStepRow("Spell-check", $"{ctx.LastSpellTickets?.Count ?? 0} finding(s)");
			if (ctx.AcronymsNotChecked is int acronymCount && CrawlerSuite.Core.SpellCheck.AcronymRule.From(config.SpellCheckEngine) is { } rule)
			{
				RunUi.WriteStepRow("Acronyms not checked", $"{acronymCount} distinct, {rule.Describe()}", dimmed: true);
			}

			// Opt-in diagnostic, default OFF: harvest all inline <script> bodies into one blob (log 28)
			// and scan it as a single stream (log 29). Fully isolated from the per-page engine above.
			if (config.SpellCheckEngine.SpellCheckJavaScript.BulkScanPageScript)
			{
				int blocks, findings;
				RunUi.BeginStep(RunSteps.BulkPageScriptScan);
				using (Logger.QuietConsole())
				{
					(blocks, findings) = Step_BulkScanPageScript(ctx, config);
				}
				RunUi.WriteStepRow("Bulk page-script scan", $"{findings} finding(s) · {blocks} block(s)");
			}

			// Opt-in diagnostic, default OFF: spell-check the raw external .js files in the crawl
			// directory (the big bundles), reusing the inline scanner's core. Filename provenance,
			// one log (30). Config validation halts the run if this is on with no dictionaries.
			if (config.SpellCheckEngine.SpellCheckJavaScript.ScanScriptFilesInDownload)
			{
				int files, jsFindings;
				RunUi.BeginStep(RunSteps.JsFileScan);
				using (Logger.QuietConsole())
				{
					(files, jsFindings) = Step_ScanJsFiles(ctx, config);
				}
				RunUi.WriteStepRow("JS-file scan", $"{jsFindings} finding(s) · {files} file(s)");
			}

			Step_WriteSuggestions();

			// The dictionaries this run's checks needed (each loads when first asked for).
			var loaded = _dictionaryBundles.Loaded;
			RunUi.WriteStepRow("Dictionaries loaded",
				$"{loaded.Count} of {_dictionaryBundles.Count}" + (loaded.Count > 0 ? " · " + string.Join(" · ", loaded) : string.Empty),
				dimmed: true);

			// The test levers this run's dictionary checks used (session only, never saved), so
			// every output can be traced to the variant that produced it.
			var levers = Bundle.ActiveLevers;
			RunUi.WriteStepRow("Test levers", levers.Describe(), dimmed: levers.IsDefault);

			// Diagnostic: the slowest dictionary checks of the run. Dictionary checks have no time
			// limit (a verdict must not depend on machine load); this row shows whether any word
			// is pathologically slow. Time, length and language only, never the word.
			var slowest = WordCheckStats.SlowestChecks();
			RunUi.WriteStepRow("Slowest word checks",
				slowest.Count == 0
					? "none"
					: string.Join(" · ", slowest.Select(c => $"{c.Milliseconds:0.0} ms ({c.Language}, {c.Length} letters)"))
						+ $" · {WordCheckStats.Checks:N0} dictionary checks",
				dimmed: true);

			// ── OUTPUT phase ─────────────────────────────────────────────────
			// This run writes every analysis log; it does no triage and writes no
			// ticket text, so IssueTracking.log and TicketText.log stay untouched.
			RunUi.WriteHeader(RunPhases.Output);
			RunUi.BeginStep(RunSteps.SitemapCopy);
			Step_CopySitemapToSiteRoot(ctx, workingFolder, urlDirectory);

			// Dead last: dictionary maintenance does not influence this run's results
			// (it tidies the user/site dictionaries for the next run). Orphans come from
			// the cross-off usage recorded during spell-check above.
			Step_MaintainDictionaries(ctx, config, urlDirectory);

			return new AnalysisPipelineResult(pdfIssues, base64Issues, canonicalIssues, assetIssues);
		}

		/// <summary>Writes log 27, held back by the spell-check (see <see cref="_writeSuggestions"/>); a failure costs only that log.</summary>
		private static void Step_WriteSuggestions()
		{
			var write = _writeSuggestions;
			_writeSuggestions = null;
			if (write is null)
			{
				return;
			}

			try
			{
				using (Logger.QuietConsole())
				{
					write();
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Suppression suggestions (log 27) could not be written: {ex.Message}");
			}
		}

		/// <summary>
		/// Lets go of the run's dictionaries and the checks that reach them. The memory manager gives
		/// the memory back on its own schedule: no collection is forced (no runtime tuning).
		/// </summary>
		private static void Step_ReleaseDictionaries()
		{
			_writeSuggestions = null;
			_dictionaryBundles.Release();
			_dictionaryBundles = Lexicon.BundleSet.Empty;
			CrawlerSuite.Core.SpellCheck.EnrichmentDictionaries.Clear();
		}

		// ── Step 1: Index load ───────────────────────────────────────────────

		private static void Step_LoadUrlCache(CrawlerRunContext ctx)
		{
			RunUi.MarkStep();
			using (Logger.QuietConsole())
			{
				Cache.Load(ctx.CrawlerLogIndexPath);
			}
		}

		// ── Step 1b: Oversized files (log 34) ────────────────────────────────

		/// <summary>Writes log 34 from 01, the samples and their sidecars (replay-safe).</summary>
		private static void Step_WriteOversizedLog(CrawlerRunContext ctx, Config config)
		{
			var count = OversizedFiles.WriteLog(
				ctx.CrawlerLogPath,
				ctx.OversizedDirectory,
				ctx.FileDownloadDirectory,
				config.FilePattern,
				ctx.OversizedLogPath,
				config.MaxSingleFileBytes);

			if (count > 0)
			{
				RunUi.WriteWarning(
					$"{count} file(s) over the {config.MaxSingleFileMB} MB single-file limit were not stored. " +
					$"See {LogFileNames.OversizedFiles}.");
			}

			RunUi.WriteStepRow("Oversized files", count > 0 ? $"{count} not stored" : "none", dimmed: count == 0);
		}

		// ── Step 2: Sitemap ──────────────────────────────────────────────────

		private static void Step_GenerateSitemap(CrawlerRunContext ctx, Config config)
		{
			Logger.LogInfo("Generate sitemap.");
			var tally = new Sitemap.Tally();
			Sitemap.Generate(
				ctx.FileDownloadDirectory,
				ctx.SitemapPath,
				config.SitemapGeneratorExclusions,
				config.SitemapGenerateForcedInclusions,
				config.FilePattern,
				config.ResolvedDegreeOfParallelism,
				tally);
			RunUi.WriteStepRow("Sitemap", $"{CountSitemapEntries(ctx.SitemapPath)} entries");
			RunUi.WriteStepFacts(RunSteps.Sitemap, new StepFacts(
				StepFacts.Count(tally.Entries),
				$"Sitemap: {StepFacts.Count(tally.Entries)} of {StepFacts.Count(tally.Pages)} pages",
				[.. StepFacts.NonZero(
					("not in it — noindex in the page", tally.NoindexInPage),
					("not in it — noindex by header", tally.NoindexByHeader),
					("not in it — found only through the content list", tally.ContentListOnly),
					("not in it — excluded in Settings", tally.Excluded))],
				"Click opens Output files, where the sitemap is."));
		}

		// ── Step 3: 404 processing ───────────────────────────────────────────

		/// <summary>The images whose findings triage takes (Triage.ImageSource): their users are looked up for log 38.</summary>
		internal static List<string> ImagesToPlace(IEnumerable<IssueTracking.IssueRecord> assetIssues) =>
			[.. assetIssues
				.Where(r => Triage.ImageSource.TypesOf(r.Word, r.SourceLabel).Count > 0
					&& Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
				.Select(r => Links.BrokenLinkOrigins.KeyOf(r.Url))
				.Distinct(StringComparer.OrdinalIgnoreCase)];

		private static void Step_Process404s(CrawlerRunContext ctx, string url, Config config, IReadOnlyList<string> images)
		{
			Logger.LogInfo("Processing 404s.");

			// Rows whose status is NotFound, each address once (a URL may hold the word itself).
			var list404 = new List<string>();
			if (File.Exists(ctx.CrawlerLogPath))
			{
				foreach (var line in File.ReadLines(ctx.CrawlerLogPath, System.Text.Encoding.UTF8))
				{
					var parts = line.Split('|');
					if (parts.Length > 2 && parts[2].Trim().Equals("NotFound", StringComparison.OrdinalIgnoreCase))
					{
						list404.Add(Links.BrokenLinkOrigins.KeyOf(parts[1]));
					}
				}
			}

			list404 = [.. list404.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
			FileIo.WriteAllLinesWithRetry(ctx.ErrorLogPath, list404, Path.GetFileName(ctx.ErrorLogPath));

			if (list404.Count == 0)
			{
				// Written with its header only: "no broken links" is a result, not a step that did not run.
				ErrorSourcesLog.Write([], ctx.ErrorSourcesCsvBasePath);
				Logger.LogInfo("No 404s found in crawler log.");
				RunUi.WriteStepRow(RunSteps.BrokenLinks, "none", dimmed: true);
				ImageReferencesLog.Write(images.Count == 0 ? [] : Links.BrokenLinkOrigins.Find(
					ctx.SaveDirectory, [], url, config.FilePattern, config.ModalQueryParameters, config.ExtendedCrawlJsonPathPrefixes, images).ImageRows,
					ctx.ImageReferencesCsvBasePath);
				return;
			}

			var origins = Links.BrokenLinkOrigins.Find(
				ctx.SaveDirectory, list404, url, config.FilePattern, config.ModalQueryParameters, config.ExtendedCrawlJsonPathPrefixes, images);
			ErrorSourcesLog.Write(origins.Rows, ctx.ErrorSourcesCsvBasePath);
			ImageReferencesLog.Write(origins.ImageRows, ctx.ImageReferencesCsvBasePath);
			var canonical = origins.OnlyCanonical > 0 ? $" · {origins.OnlyCanonical} only from a canonical" : string.Empty;
			RunUi.WriteStepRow(
				RunSteps.BrokenLinks,
				$"{origins.Addresses} broken address(es) · {origins.Pages} page(s) · {origins.Files} file(s){canonical}");
			RunUi.WriteStepFacts(RunSteps.BrokenLinks, Links.BrokenLinkOrigins.ReachFacts(origins.Rows));
		}

		// ── Step 4: SEO data extraction ──────────────────────────────────────

		private static async Task Step_ExtractSeoData(CrawlerRunContext ctx, Config config)
		{
			if (!ctx.IsDebugSession || !File.Exists(ctx.SeoDataPath))
			{
				Logger.LogInfo("Extracting SEO relevant data from HTML.");
				var (hrefs, search) = await SEO.HarvestAsync(ctx.FileDownloadDirectory, ctx.SeoDataPath, config.FilePattern);
				ctx.CanonicalHrefsByFile = hrefs;
				RunUi.WriteStepRow("SEO data", $"{CountDataLines(ctx.SeoDataPath)} page(s)");
				RunUi.WriteStepFacts(RunSteps.SeoData, new StepFacts(
					StepFacts.Count(search.Eligible),
					$"Eligible for search: {StepFacts.Count(search.Eligible)} of {StepFacts.Count(search.Pages)} pages",
					[.. StepFacts.NonZero(
						("noindex in the page", search.NoindexInPage),
						("noindex by header", search.NoindexByHeader),
						("canonical to another page", search.CanonicalElsewhere))],
					"Eligible, not indexed: robots.txt is not read, and whether a search engine took a page is not known."));
			}
			else
			{
				Logger.LogInfo("Skipping SEO extraction (debug session, 08-seo-data.csv exists).");
				RunUi.WriteStepRow("SEO data", "skipped (cached)", dimmed: true);
			}
		}

		// ── Step 5: Canonical link analysis ──────────────────────────────────
		// Returns canonical issues for end-of-run merge with the full detection
		// set. See note inside Step_AnalyseCanonicals.

		// Canonical issues are returned to caller (like PDF and
		// Base64 issues) and merged into IssueTracking only at end-of-run.
		// An intermediate Save with Merge(existing, canonicalIssuesOnly) would
		// classify all non-canonical existing records as 'fixed' (they are not
		// in the partial detection set), triggering spurious 'fixed → reopened'
		// transitions when the end-of-run Merge re-detects them with the full
		// set. See Step_AnalysePdfQuality for the same concern.

		private static List<IssueTracking.IssueRecord> Step_AnalyseCanonicals(CrawlerRunContext ctx, Config config)
		{
			Logger.LogInfo("Running canonical link analysis.");
			var canonicalIssues = CanonicalAnalyzer.Analyse(
				ctx.FileDownloadDirectory,
				ctx.CrawlerLogIndexPath,
				ctx.CanonicalCsvBasePath,
				config.Url,
				config.UrlSubdomainsAllowed,
				config.FilePattern,
				ctx.CanonicalHrefsByFile,
				ctx.SeoDataPath,
				ctx.RedirectAnalysisPath);
			ctx.CanonicalHrefsByFile = null; // read once; not kept for the rest of the run
			if (canonicalIssues.Count > 0)
			{
				Logger.LogInfo($"Canonical analysis: {canonicalIssues.Count} issue(s) for end-of-run merge.");
			}
			RunUi.WriteStepRow("Canonical links", $"{canonicalIssues.Count} issue(s)");
			return canonicalIssues;
		}

		// ── Step 6: PDF quality analysis ─────────────────────────────────────

		private static List<IssueTracking.IssueRecord> Step_AnalysePdfQuality(CrawlerRunContext ctx)
		{
			Logger.LogInfo("Running PDF quality analysis.");
			// [KEEP] pdfIssues returned from this step so it's available at end-of-run merge.
			// Do NOT merge into IssueTracking here — the end-of-run Merge handles all
			// issue types together, preventing premature "fixed" status from the main
			// Merge not finding PDFQUALITY issues in its detectedIssues list.
			var pdfIssues = PdfQualityAnalyzer.Analyse(
				ctx.FileDownloadDirectory, ctx.PdfQualityCsvBasePath, ctx.PdfRemediationCsvBasePath);
			Logger.LogInfo($"PDF quality analysis complete: {pdfIssues.Count} issue(s) found.");
			RunUi.WriteStepRow("PDF quality", $"{pdfIssues.Count} issue(s)");
			return pdfIssues;
		}

		// ── Step 6b: Asset quality analysis (images) ─────────────────────────

		private static List<IssueTracking.IssueRecord> Step_AnalyseAssetQuality(
			CrawlerRunContext ctx, Config config)
		{
			// [KEEP] assetIssues returned from this step so it's available at the
			// end-of-run merge — same pattern as pdfIssues. Do NOT merge into
			// IssueTracking here, or the main Merge (not finding ASSET issues in
			// its detectedIssues list) would prematurely mark them fixed.
			if (!config.AssetQuality.IsEnabled)
			{
				Logger.LogInfo("Asset quality analysis disabled — skipping.");
				RunUi.WriteStepRow("Asset quality", "disabled", dimmed: true);
				return [];
			}
			Logger.LogInfo("Running asset quality analysis.");
			var assetIssues = AssetQuality.Analyse(
				ctx.FileDownloadDirectory, ctx.AssetQualityCsvBasePath, config.AssetQuality);
			Logger.LogInfo($"Asset quality analysis complete: {assetIssues.Count} issue(s) found.");
			RunUi.WriteStepRow("Asset quality", $"{assetIssues.Count} finding(s)");
			return assetIssues;
		}

		// ── Step 7: Base64 asset extraction ──────────────────────────────────

		private static List<IssueTracking.IssueRecord> Step_ExtractBase64Assets(
			CrawlerRunContext ctx, Config config)
		{
			// Base64 asset extraction — runs outside the download pipeline.
			// Scans configured file types for embedded data URIs, decodes them,
			// and saves to base64assets/ for manual review. Safe to re-run.
			// [KEEP] base64Issues returned from this step so it survives to the
			// end-of-run IssueTracking merge — same pattern as pdfIssues.
			var base64Issues = new List<IssueTracking.IssueRecord>();
			if (config.Base64AssetFileExtensions.Count > 0)
			{
				Logger.LogInfo("Extracting embedded Base64 assets...");
				base64Issues = Base64AssetExtractor.Extract(
					ctx.FileDownloadDirectory,
					ctx.Base64AssetsDirectory,
					ctx.Base64AssetsLogPath,
					config.Base64AssetFileExtensions,
					config.BloatThresholdBase64Kilobytes * 1024);
				RunUi.WriteStepRow("Base64 assets", $"{base64Issues.Count} over threshold");
			}
			else
			{
				RunUi.WriteStepRow("Base64 assets", "disabled", dimmed: true);
			}
			return base64Issues;
		}

		// ── Step 8: Resource bloat (page-centric) ────────────────────────────

		private static void Step_AnalyseResourceBloat(CrawlerRunContext ctx, Config config)
		{
			// Resource bloat analysis — page-centric view of JS/CSS size,
			// Base64 inlined assets, inlined CSS, and JSON blobs in HTML.
			// Runs after Base64 extraction so log 19 is available.
			Logger.LogInfo("Analysing resource bloat...");
			ResourceBloatAnalyzer.Analyse(
				ctx.FileDownloadDirectory,
				ctx.Base64AssetsLogPath,
				ctx.ResourceBloatLogPath,
				config.Url,
				config.FileExtension,
				config.BloatThresholdBase64Kilobytes * 1024,
				config.BloatThresholdJsKilobytes * 1024,
				config.BloatThresholdCssKilobytes * 1024);
		}

		// ── Step 9: Resource bloat (baseline-adjusted) ───────────────────────

		private static void Step_AnalyseResourceBloatBaseline(CrawlerRunContext ctx, Config config)
		{
			// Resource bloat baseline analysis — reads log 20 and produces
			// log 21 with baseline-adjusted heavy hitters for executives.
			Logger.LogInfo("Analysing resource bloat above baseline...");
			ResourceBloatBaselineAnalyzer.Analyse(
				ctx.FileDownloadDirectory,
				ctx.ResourceBloatLogPath,
				ctx.ResourceBloatBaselineLogPath,
				config.Url,
				config.FileExtension,
				config.BloatThresholdBase64Kilobytes * 1024,
				config.BloatThresholdJsKilobytes * 1024,
				config.BloatThresholdCssKilobytes * 1024,
				config.BloatThresholdAboveBaselineKilobytes * 1024);
		}

		// ── Step 11b: Boilerplate prune (shared pruned tree) ─────────────────

		private static void Step_BoilerplatePrune(CrawlerRunContext ctx, Config config)
		{
			// Writes the shared PRUNED tree: each downloaded page minus its declared boilerplate,
			// kept whole on each group's reference pages. Consumed by the content-quality checks
			// (Step_RunContentQualityChecks).
			Logger.LogInfo("Pruning boilerplate to shared pruned tree.");
			var resolver = new CrawlerSuite.Core.Boilerplate.BoilerplateResolver(config.SpellCheckEngine.BoilerplateGroups);
			CrawlerSuite.Core.Boilerplate.BoilerplateSimplifier.Run(
				ctx.FileDownloadDirectory,
				ctx.FilePrunedDirectory,
				resolver,
				CrawlIndex.LookUpUrlForFile,
				config.FilePattern,
				config.ResolvedDegreeOfParallelism);
			var boilerGroups = config.SpellCheckEngine.BoilerplateGroups;

			// Which groups these pages were pruned with: the Comparison tells two snapshots pruned differently.
			CrawlerSuite.Core.Snapshots.Reprune.WriteStamp(ctx.SaveDirectory, boilerGroups);
			int rules = boilerGroups.Sum(g => g.BoilerplateSelectors.Count);
			RunUi.WriteStepRow("Boilerplate prune",
				$"{CountFiles(ctx.FilePrunedDirectory, config.FilePattern)} page(s) · " +
				$"{boilerGroups.Count} group(s) · {rules} rule(s)");

			long Size(string folder) => Directory.Exists(folder)
				? CrawlerSuite.Core.Host.FolderFiles.Sorted(folder, config.FilePattern).Sum(f => new FileInfo(f).Length)
				: 0;
			long raw = Size(ctx.FileDownloadDirectory);
			long pruned = Size(ctx.FilePrunedDirectory);
			if (raw > 0)
			{
				var lines = new List<string>
				{
					$"raw HTML · {StepFacts.Bytes(raw)}",
					$"after pruning · {StepFacts.Bytes(pruned)}",
					$"boilerplate groups · {boilerGroups.Count} ({rules} rules)",
				};
				var referencePages = boilerGroups.Where(g => g.PagesToCheckBoiler.Count > 0).ToList();
				if (referencePages.Count > 0)
				{
					lines.Add("template checked on:");
					foreach (var group in referencePages)
					{
						var prefix = boilerGroups.Count > 1 ? $"{group.PathPrefix}: " : string.Empty;
						lines.AddRange(group.PagesToCheckBoiler.Select(p => $"  {prefix}{p}"));
					}
				}

				int removed = (int)Math.Round(100.0 * (raw - pruned) / raw);
				RunUi.WriteStepFacts(RunSteps.BoilerplatePrune, new StepFacts(
					$"−{removed}%",
					$"Boilerplate prune  −{removed}%",
					lines,
					"The template's repeated parts — header, navigation, footer — are cut out before the content checks, so they " +
					"see the text the editors write; the template itself is checked once, on the pages above."));
			}
		}

		// ── Step 12: Self-link scan ──────────────────────────────────────────

		private static void Step_ScanSelfLinks(CrawlerRunContext ctx, Config config)
		{
			// Self-link scan runs after the boilerplate prune — it reads the pruned HTML.
			// Pruned (not simplified/raw) on purpose: chrome self-links (nav active-item,
			// breadcrumb-to-self, footer) are legitimate, not the CMS authoring mistake this
			// hunts — editors lacking a real target sometimes link a page to itself to satisfy
			// the mandatory-link rule. Scanning pruned drops chrome as false positives, leaving
			// those content self-links.
			// [KEEP] Must not run before the prune completes or the pruned
			// folder will be empty and the log will contain no results.
			// On a reference page the prune keeps the template (it is checked there once); the self-link
			// scan leaves it out there too, with the same selectors the prune uses elsewhere — the
			// home page's logo leads to the home page by design.
			Logger.LogInfo("Checking for self linking pages.");
			var resolver = new CrawlerSuite.Core.Boilerplate.BoilerplateResolver(config.SpellCheckEngine.BoilerplateGroups);
			SelfLinkScanner.FindSelfLinks(
				ctx.FilePrunedDirectory,
				ctx.SelfLinkAnalysisCsvBasePath,
				config.QueryStringsToIgnoreForSelfLinkDetermination,
				config.FilePattern,
				config.ResolvedDegreeOfParallelism,
				200,
				templatePartsOf: url => resolver.ResolveSelectors(url) is (var selectors, true)
					? CrawlerSuite.Core.Boilerplate.BoilerplateSimplifier.ToXPathRemovals(selectors)
					: [],
				forwardingCsvBasePath: ctx.ForwardingLinksCsvBasePath);
			var forwarding = CountDataLines(ctx.ForwardingLinksCsvBasePath + IssueLogWriter.CsvSemicolonSuffix);
			RunUi.WriteStepRow("Self-links", $"{CountDataLines(ctx.SelfLinkAnalysisCsvBasePath + IssueLogWriter.CsvSemicolonSuffix)} found"
				+ (forwarding > 0 ? $" · {forwarding} forwarding link(s)" : string.Empty));
		}

		// ── Step 13: Content quality checks ──────────────────────────────────

		private static void Step_RunContentQualityChecks(CrawlerRunContext ctx, Config config, string urlDirectory)
		{
			Logger.LogInfo("Running content quality checks.");
			// An institution's name with a space at a hyphen ("Xyzzy- und Hausverband") is kept in
			// the proper names of the configuration; the hyphen-spacing check asks them in every language.
			ctx.ProperNames = config.ContentQuality.CheckHyphenSpacing
				? CrawlerSuite.Core.Quality.ProperNames.From(config)
				: null;
			// Pass 1 (raw checks: unwanted patterns, CMS template defects, malformed
			// HTML/anchors) reads the raw download tree — these must reflect what the
			// server emitted, before any stripping. Pass 2 (structural checks: BareText,
			// MisplacedAnchors, etc.) reads the shared PRUNED tree (each page minus its
			// declared boilerplate) so it isn't re-checking the same boilerplate on every
			// page — inefficient and noisy.
			ctx.ContentQualityIssues = ContentQuality.Analyse(
				ctx.FilePrunedDirectory,
				ctx.FileDownloadDirectory,
				ctx.ContentQualityLogPath,
				config.ContentQuality,
				config.ResolvedDegreeOfParallelism,
				config.FilePattern,
				config.ContentQualityExcludedUrls,
				config.ContentUnwantedPatterns,
				ctx.CmsTemplateDefectsCsvBasePath,
				// The quote detector resolves the declared language SET via the
				// shared resolver. Overrides correct the mislabelled branches and declare
				// multi-language pages; defaultLanguage is left empty (the param default)
				// so an undeclared page yields the empty set — no system anchor, so only
				// the structural quote checks run.
				config.SpellCheckEngine.PageLanguageOverrides,
				hyphenNames: ctx.ProperNames,
				wordCheck: WordCheck,
				crawlStatus: CrawlerSuite.Core.Quality.Css.CrawlLogStatus.From(ctx.CrawlerLogPath));
		}

		/// <summary>
		/// Whether a loaded dictionary accepts <c>word</c> in <c>language</c> ("en-US" asks that flavour, "en" the Primary);
		/// null when no dictionary is loaded for that language. For the content checks.
		/// </summary>
		private static bool? WordCheck(string word, string language)
		{
			if (string.IsNullOrEmpty(language))
			{
				return null;
			}

			if (LanguageKey(language) is not { } key || !_dictionaryBundles.TryGetValue(key, out var bundle))
			{
				return null;
			}

			return bundle.Check(word);
		}

		/// <summary>
		/// The dictionaries a script scan is configured with, as loaded keys: each language or flavour
		/// resolved like a page's ("en" → its Primary), unknown ones left out with a warning.
		/// </summary>
		private static List<string> DictionaryKeys(IReadOnlyList<string>? languages)
		{
			var keys = new List<string>();
			foreach (var language in languages ?? [])
			{
				if (LanguageKey(language) is { } key)
				{
					if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
					{
						keys.Add(key);
					}
				}
				else
				{
					Logger.LogWarning($"Script scan dictionary \"{language}\" matches no loaded dictionary; left out.");
				}
			}

			return keys;
		}

		// ── Step 17: Load dictionaries ───────────────────────────────────────

		/// <summary>
		/// Returns false if dictionary loading fails (typically: CharacterValidator
		/// found suspicious characters in a dictionary file). Caller should abort
		/// the run before spell-check.
		/// </summary>
		private static bool Step_LoadDictionaries(string urlDirectory, Config config)
		{
			Logger.LogInfo("Load dictionaries.");
			// Fresh per run: a language removed from config since the last run must not stay loaded.
			_dictionaryBundles.Release();
			_dictionaryBundles = Lexicon.BundleSet.Empty;
			_writeSuggestions = null;
			_flavours = new([]);
			CrawlerSuite.Core.SpellCheck.EnrichmentDictionaries.Clear();
			// The test levers apply in Debug mode only; otherwise every run checks with the defaults.
			Bundle.ResetRunState(config.DebugMode ? CheckLevers.Session : CheckLevers.Default);
			try
			{
				PreloadDictionaries(urlDirectory, config.CustomDictionaryFile, config.DictionaryBundles,
					config.CustomForeignDictionaryFile);

				// A language named in "Languages by page" without a loaded bundle does not stop the run
				// (a shared configuration may name one not installed here): those pages are checked with
				// the languages that are there, and this says so — never silently.
				var missing = CrawlerSuite.Core.SpellCheck.PageLanguageResolver.MissingLanguages(
					config.SpellCheckEngine.PageLanguageOverrides, code => LanguageKey(code) is not null);
				if (missing.Count > 0)
				{
					foreach (var line in missing)
					{
						Logger.LogWarning("Languages by page: " + line);
					}

					RunUi.WriteWarningBlock("Languages by page name a language without a dictionary", missing);
				}

				return true;
			}
			catch (InvalidOperationException ex)
			{
				// CharacterValidator throws this when suspicious characters are found
				// in a dictionary file; ValidateDictionaryFileHalt has already shown the
				// red detail block. Log, show the stop notice and end the run.
				Logger.LogError(ex.Message);
				RunUi.WriteErrorBlock("Spell-check stopped", ex.Message.Split('\n'));
				return false;
			}
		}

		// ── Step 19: Dictionary maintenance (end of run) ─────────────────────

		/// <summary>
		/// End-of-run maintenance of the user and site dictionaries. Orphans are read
		/// from the cross-off usage recorder populated during spell-check
		/// (<see cref="UsageTracker"/>): any non-pinned user/site entry never
		/// consulted on a crawled page is an orphan. Redundant entries are computed from
		/// the loaded bundles. Pinned ('!') entries are exempt.
		///
		/// Mode (config.DictionaryMaintenance.Mode):
		///   Off         — does nothing.
		///   Report      — writes log 15 only; mutates nothing; runs in silent too.
		///   Interactive — reports like Report; each flagged word is then removed or
		///                 pinned one at a time on the Dictionaries page.
		/// Runs dead last, so it never affects this run's findings — it tidies the
		/// dictionaries for the next run.
		/// </summary>
		private static void Step_MaintainDictionaries(
			CrawlerRunContext ctx,
			Config config,
			string urlDirectory)
		{
			var maint = config.DictionaryMaintenance;
			var mode = (maint.Mode ?? "Off").Trim();

			// The Dictionaries page's "find entries no longer needed" runs maintenance as Report.
			bool report = string.Equals(mode, "Report", StringComparison.OrdinalIgnoreCase)
				|| Host.RunOptions.FindUnneededEntries;
			bool interactive = string.Equals(mode, "Interactive", StringComparison.OrdinalIgnoreCase);

			if (!report && !interactive)
			{
				// Off, empty, or unrecognized → no-op.
				return;
			}

			// Orphans come from the usage recorded during this run's spell pass. If that pass
			// failed, usage is empty or partial and every word would look unused — so the
			// maintenance report is skipped rather than listing the whole dictionary.
			if (ctx.LastSpellTickets is null)
			{
				Logger.LogWarning("Dictionary maintenance skipped — the spell-check pass did not complete, so word usage is unknown.");
				RunUi.WriteStepRow("Dictionary maintenance", "skipped (spell-check incomplete)", dimmed: true);
				return;
			}

			if (!maint.UpdateUserDictionary && !maint.UpdateSiteSpecificDictionary)
			{
				Logger.LogInfo("Dictionary maintenance: neither dictionary selected — nothing to do.");
				return;
			}

			// Dictionary paths — same expressions Step_LoadDictionaries / PreloadDictionaries use.
			var userDictionaryPath = maint.UpdateUserDictionary
				? AppPaths.Dictionary(config.CustomDictionaryFile)
				: string.Empty;
			var siteDictionaryPath = maint.UpdateSiteSpecificDictionary
				? AppPaths.Dictionary($"{urlDirectory}.dic")
				: string.Empty;

			// Snapshot the cross-off usage recorded during spell-check. Taken here, after
			// spell + triage, so the redundancy check's own Check calls below cannot
			// pollute it.
			var usedUser = UsageTracker.SnapshotUser();
			var usedSite = UsageTracker.SnapshotSite();

			var prefixesToStrip = config.SpellCheckWordPrefixesToStrip;
			// Every dictionary: the redundancy check asks all languages (those not loaded yet load here).
			var bundles = _dictionaryBundles.Values.ToList();

			RunUi.WriteHeader(RunPhases.Dictionaries);
			RunUi.BeginStep(RunSteps.DictionaryMaintenance);

			Audit.Analysis userAnalysis;
			Audit.Analysis siteAnalysis;
			using (Logger.QuietConsole())
			{
				var sw = System.Diagnostics.Stopwatch.StartNew();

				userAnalysis = string.IsNullOrEmpty(userDictionaryPath)
					? new Audit.Analysis([], [])
					: Audit.AnalyseFromUsage(userDictionaryPath, usedUser, prefixesToStrip, bundles, CrawlerSuite.Core.SpellCheck.AcronymRule.From(config.SpellCheckEngine));

				siteAnalysis = string.IsNullOrEmpty(siteDictionaryPath)
					? new Audit.Analysis([], [])
					: Audit.AnalyseFromUsage(siteDictionaryPath, usedSite, prefixesToStrip, bundles, CrawlerSuite.Core.SpellCheck.AcronymRule.From(config.SpellCheckEngine));

				Audit.WriteAnalysisReport(
					ctx.UserDictionaryOrphanWordsFilePath,
					userDictionaryPath,
					userAnalysis,
					siteDictionaryPath,
					siteAnalysis);

				sw.Stop();
				Logger.LogInfo(
					$"Dictionary maintenance analysis: {sw.ElapsedMilliseconds} ms " +
					$"(user: {userAnalysis.Orphaned.Count} orphan / {userAnalysis.Redundant.Count} redundant, " +
					$"site: {siteAnalysis.Orphaned.Count} orphan / {siteAnalysis.Redundant.Count} redundant).");
			}

			if (!string.IsNullOrEmpty(userDictionaryPath))
			{
				RunUi.WriteStepRow("User dictionary",
					$"{Audit.CountDictionaryWords(userDictionaryPath)} words · " +
					$"{userAnalysis.Orphaned.Count} orphan(s) · {userAnalysis.Redundant.Count} redundant");
			}
			if (!string.IsNullOrEmpty(siteDictionaryPath))
			{
				RunUi.WriteStepRow("Site dictionary",
					$"{Audit.CountDictionaryWords(siteDictionaryPath)} words · " +
					$"{siteAnalysis.Orphaned.Count} orphan(s) · {siteAnalysis.Redundant.Count} redundant");
			}

			if (Host.RunOptions.FindUnneededEntries)
			{
				Step_FindUnneededEntries(ctx, config, userDictionaryPath, userAnalysis, siteDictionaryPath, siteAnalysis);
			}

			// The run itself only reports: log 15 is written, the dictionaries are untouched.
			// Removing and pinning happen one entry at a time on the Dictionaries page, which
			// reads this report.
			if (interactive)
			{
				Logger.LogInfo("Dictionary maintenance: Mode=Interactive reports like Report (log 15); entries are removed or pinned on the Dictionaries page.");
			}
		}

		/// <summary>
		/// The Dictionaries page's second pass: the spell checks of this run (pages, and inline
		/// scripts and JS files when they are on) once more with the user and site lists switched
		/// off — same system dictionaries, same rules, same suppressions. Its output goes to a
		/// temporary folder outside the snapshot, deleted afterwards; nothing of this run's results
		/// changes. A dictionary entry whose word that pass does not flag is no longer needed:
		/// the app accepts the word without it. Written to log 35 for the Dictionaries page.
		/// </summary>
		private static void Step_FindUnneededEntries(
			CrawlerRunContext ctx,
			Config config,
			string userDictionaryPath,
			Audit.Analysis userAnalysis,
			string siteDictionaryPath,
			Audit.Analysis siteAnalysis)
		{
			RunUi.BeginStep(Host.RunSteps.UnneededEntries);
			var temp = Path.Combine(Path.GetTempPath(), "CrawlerSuite-unneeded-" + Guid.NewGuid().ToString("N"));
			try
			{
				Directory.CreateDirectory(temp);

				// The same bundles without the user and site lists (the foreign allow-list stays:
				// it is not maintained here).
				// Each made when first asked for, from the main set's dictionary (case ignored, as there).
				var bare = _dictionaryBundles.Derive(
					b => new Bundle { System = b.System, Language = b.Language, SharedForeign = b.SharedForeign });

				string T(string name) => Path.Combine(temp, name);
				var files = CrawlerSuite.Core.Host.FolderFiles
					.Sorted(ctx.FileDownloadDirectory, config.FilePattern)
					.Select(f => new CrawlerSuite.Core.SpellCheck.NewSpellEngineRunner.FileInput { Filename = Path.GetFileName(f) });
				CrawlerSuite.Core.SpellCheck.NewSpellEngineRunner.Run(
					files,
					ctx.FileDownloadDirectory,
					T(LogFileNames.SpellErrorsUnique),
					T(LogFileNames.SpellErrorsSources),
					T(LogFileNames.SpellErrorsSourceLocation),
					T(LogFileNames.WordTickets),
					T(LogFileNames.SpellcheckSuppressionSuggestions),
					config.SpellCheckEngine,
					bare,
					(_, doc) => Language.TagFromMeta(doc, config.SpellCheckEngine.DefaultLanguage),
					config.SpellCheckWordPrefixesToStrip,
					config.GermanFugenelemente,
					CrawlIndex.LookUpUrlForFile,
					config.ResolvedDegreeOfParallelism,
					config.SpellCheckEngine.SuppressSpellFindingsCoveredByWordCollision
						? new CrawlerSuite.Core.SpellCheck.WordCollisionMatcher(ctx.ContentQualityIssues)
						: null,
					new CrawlerSuite.Core.Suppressions.AnchorSplitSpellSuppression(ctx.ContentQualityIssues),
					new CrawlerSuite.Core.Suppressions.UnwantedPatternSpellSuppression(
						ctx.ContentQualityIssues,
						config.ContentUnwantedPatterns.SelectMany(p => p.Patterns)),
					new CrawlerSuite.Core.Suppressions.AdjacentAnchorSpellSuppression(ctx.ContentQualityIssues),
					acronyms: CrawlerSuite.Core.SpellCheck.AcronymRule.From(config.SpellCheckEngine),
					properNames: CrawlerSuite.Core.Quality.ProperNames.From(config),
					languageKey: LanguageKey);

				var js = config.SpellCheckEngine.SpellCheckJavaScript;
				if (js.BulkScanPageScript)
				{
					CrawlerSuite.Core.SpellCheck.BulkScriptScanner.Run(
						ctx.FileDownloadDirectory,
						config.FilePattern,
						T(LogFileNames.BulkScriptBlob),
						T(LogFileNames.BulkScriptFindings),
						js.ScriptBulkScanDictionaries,
						bare,
						config.SpellCheckWordPrefixesToStrip,
						config.GermanFugenelemente,
						CrawlIndex.LookUpUrlForFile,
						CrawlerSuite.Core.SpellCheck.AcronymRule.From(config.SpellCheckEngine),
						properNames: CrawlerSuite.Core.Quality.ProperNames.From(config));
				}

				if (js.ScanScriptFilesInDownload)
				{
					CrawlerSuite.Core.SpellCheck.JsFileScanner.Run(
						ctx.FileDownloadDirectory,
						T(LogFileNames.JsFilesSpellCheck),
						T(LogFileNames.JsFilesSpellCheckTrimmed),
						T(LogFileNames.JsFilesSpellCheckUnique),
						T(LogFileNames.JsFilesSpellCheckRouting),
						config.FilePattern,
						js.JsFindingPageReachThreshold,
						CrawlIndex.LookUpUrlForFile,
						js.ScriptFileScanDictionaries,
						bare,
						config.SpellCheckWordPrefixesToStrip,
						config.GermanFugenelemente,
						js.TokensToFilter,
						acronyms: CrawlerSuite.Core.SpellCheck.AcronymRule.From(config.SpellCheckEngine),
					properNames: CrawlerSuite.Core.Quality.ProperNames.From(config));
				}

				var missed = DictionaryReport.MissedWords(
					[T(LogFileNames.SpellErrorsUnique), T(LogFileNames.JsFilesSpellCheckUnique)],
					[T(LogFileNames.BulkScriptFindings)]);
				var user = DictionaryReport.NotNeeded(userDictionaryPath, userAnalysis.Orphaned, missed);
				var site = DictionaryReport.NotNeeded(siteDictionaryPath, siteAnalysis.Orphaned, missed);
				DictionaryReport.WriteNotNeeded(ctx.DictionaryNotNeededFilePath, userDictionaryPath, user, siteDictionaryPath, site, DateTime.Now);
				Logger.LogInfo($"Dictionary entries no longer needed: user {user.Count}, site {site.Count}.");
				RunUi.WriteStepRow(Host.RunSteps.UnneededEntries, $"user {user.Count} · site {site.Count} no longer needed");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
			{
				Logger.LogError($"Finding dictionary entries no longer needed failed: {ex.Message}");
				RunUi.WriteStepRow(Host.RunSteps.UnneededEntries, "failed — see application.log", dimmed: true);
			}
			finally
			{
				try
				{
					Directory.Delete(temp, recursive: true);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					Logger.LogWarning($"Could not delete the temporary folder {temp}: {ex.Message}");
				}
			}
		}

		// ── Step 18: Spell-check ─────────────────────────────────────────────

		private static async Task Step_RunSpellCheck(
			CrawlerRunContext ctx,
			Config config)
		{
			Logger.LogInfo("Running spell-check.");

			// Cross-off dictionary maintenance: clear the usage recorder so it captures
			// only this run's user/site consultations (and so per-site runs in one
			// process do not leak usage between sites). Bundle.Check records
			// hits as the spell scan runs; Step_MaintainDictionaries reads them at the end.
			UsageTracker.Reset();

			// The spell engine owns spell findings: it reads raw bytes from the download dir and
			// emits the ParallelStore views. Its in-memory tickets are the source of truth for
			// triage (stashed on ctx.LastSpellTickets); operator decisions live in IssueTracking.
			try
			{
				// File list comes straight from the download dir — the engine reads raw bytes
				// from there. download/ and simplified/ hold the identical filename set; the list is sorted
				// character by character, so the emitted views are the same on every disk.
				var newEngineFiles = CrawlerSuite.Core.Host.FolderFiles
					.Sorted(ctx.FileDownloadDirectory, config.FilePattern)
					.Select(f => new CrawlerSuite.Core.SpellCheck.NewSpellEngineRunner.FileInput
					{
						Filename = Path.GetFileName(f),
					});

				// Cross-pass dedup input: when enabled, build a matcher from this run's CQ findings so
				// the spell pass can mute the merged twin of a WORD_COLLISION CQ already reports.
				// Null when switched off or when CQ produced nothing — Run then mutes nothing.
				var wordCollisions = config.SpellCheckEngine.SuppressSpellFindingsCoveredByWordCollision
					? new CrawlerSuite.Core.SpellCheck.WordCollisionMatcher(ctx.ContentQualityIssues)
					: null;

				// Cross-pass dedup (always on): mute spelling's twin of an anchor-severed word that
				// content-quality already reports as SPLIT_WORD_ANCHOR. Unconditional because the
				// gate is intrinsically surgical — a one-letter severed tail that rejoins to a real
				// word; see CrawlerSuite.Core.Suppressions.AnchorSplitSpellSuppression for the full rationale.
				var anchorSplit = new CrawlerSuite.Core.Suppressions.AnchorSplitSpellSuppression(ctx.ContentQualityIssues);

				// Cross-pass dedup (always on): mute spelling's twin of a token sitting inside a
				// configured unwanted-pattern delimiter run (e.g. a leaked CMS placeholder). The
				// anchors are the configured pattern strings; the gate is intrinsically surgical (a
				// >=2-special-char delimiter and a whitespace-bounded run) — see
				// CrawlerSuite.Core.Suppressions.UnwantedPatternSpellSuppression for the full rationale.
				var unwantedPattern = new CrawlerSuite.Core.Suppressions.UnwantedPatternSpellSuppression(
					ctx.ContentQualityIssues,
					config.ContentUnwantedPatterns.SelectMany(p => p.Patterns));

				// Cross-pass dedup (always on): mute spelling's twin of a word fractured across ADJACENT
				// anchors — a CMS editor's consecutive-anchor defect that content-quality already reports
				// as ADJACENT_ANCHOR. The gate is intrinsically surgical (two anchor fragments whose
				// verbatim join is a real word); see CrawlerSuite.Core.Suppressions.AdjacentAnchorSpellSuppression.
				var adjacentAnchor = new CrawlerSuite.Core.Suppressions.AdjacentAnchorSpellSuppression(ctx.ContentQualityIssues);

				var usage = new CrawlerSuite.Core.Settings.SettingsUsage(config.SpellCheckEngine);
				var jsOptions = config.SpellCheckEngine.SpellCheckJavaScript;
				var pageScriptSkips = jsOptions.Enabled
					? new CrawlerSuite.Core.Settings.ScriptSkipCounter(jsOptions.TokensToFilter, jsOptions.TokensToFilter.Disabled)
					: null;
				var acronymRule = CrawlerSuite.Core.SpellCheck.AcronymRule.From(config.SpellCheckEngine);
				var knownParts = new CrawlerSuite.Core.SpellCheck.KnownPartTally(config.SpellCheckWordPrefixesToStrip);
				ctx.AcronymsNotChecked = null;
				var newEngineTickets = CrawlerSuite.Core.SpellCheck.NewSpellEngineRunner.Run(
					newEngineFiles,
					ctx.FileDownloadDirectory,
					ctx.UniqueSpellErrorLogPath,
					ctx.SpellErrorLogPath,
					ctx.SpellLocatedLogPath,
					ctx.WordTicketsDiagnosticLogPath,
					ctx.SpellSuppressionSuggestionsLogPath,
					config.SpellCheckEngine,
					_dictionaryBundles,
					(_, doc) => Language.TagFromMeta(doc, config.SpellCheckEngine.DefaultLanguage),
					config.SpellCheckWordPrefixesToStrip,
					config.GermanFugenelemente,
					CrawlIndex.LookUpUrlForFile,
					config.ResolvedDegreeOfParallelism,
					wordCollisions,
					anchorSplit,
					unwantedPattern,
					adjacentAnchor,
					usage,
					pageScriptSkips,
					acronymRule,
					Path.Combine(ctx.SaveDirectory, LogFileNames.Acronyms),
					count => ctx.AcronymsNotChecked = count,
					knownParts,
					properNames: CrawlerSuite.Core.Quality.ProperNames.From(config),
					languageKey: LanguageKey,
					deferSuggestions: write => _writeSuggestions = write);

				// How much the technical XPaths, attributes and boilerplate parts were needed: shown on
				// the settings screen. A failure to write it costs only that display.
				try
				{
					usage.Write(ctx.SaveDirectory);

					// Known word parts: how many words each let pass, and which of them would pass without
					// it (asked without recording dictionary usage).
					var (notNeeded, neededFor) = knownParts.Judge(_dictionaryBundles, config.GermanFugenelemente, acronymRule);
					var switchedOff = knownParts.WouldLetPass(
						config.SpellCheckWordPrefixesToStrip.Disabled,
						SpellWordList.Read(ctx.SaveDirectory),
						_dictionaryBundles,
						config.GermanFugenelemente,
						acronymRule);
					CrawlerSuite.Core.Settings.SettingsUsage.Merge(ctx.SaveDirectory, new Dictionary<string, IReadOnlyDictionary<string, CrawlerSuite.Core.Settings.UsageCount>>
					{
						[CrawlerSuite.Core.SpellCheck.KnownPartTally.Setting] = knownParts.Counts(),
						[CrawlerSuite.Core.SpellCheck.KnownPartTally.NotNeededSetting] = notNeeded,
						[CrawlerSuite.Core.SpellCheck.KnownPartTally.SwitchedOffSetting] = switchedOff,
					});
					CrawlerSuite.Core.Settings.SettingsUsage.MergeExamples(ctx.SaveDirectory, new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
					{
						[CrawlerSuite.Core.SpellCheck.KnownPartTally.Setting] = neededFor,
					});
					if (pageScriptSkips is not null)
					{
						CrawlerSuite.Core.Settings.SettingsUsage.Merge(ctx.SaveDirectory, new Dictionary<string, IReadOnlyDictionary<string, CrawlerSuite.Core.Settings.UsageCount>>
						{
							[CrawlerSuite.Core.Settings.SettingsUsage.ScriptStrings] = pageScriptSkips.Counts(),
						});
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					Logger.LogWarning($"Settings usage could not be written: {ex.Message}");
				}

				// Hand this run's tickets to the spell triage step (in-process source of truth for
				// per-page occurrences). Set only on success — if the harvest threw, this stays
				// null and triage no-ops rather than acting on stale tracking entries.
				ctx.LastSpellTickets = newEngineTickets;

				// Log 36: which parts keep each hyphenated finding flagged, so the Spell findings page
				// can add just those parts. Asks the dictionaries without recording usage, so dictionary
				// maintenance sees only the spell pass's usage. A failure costs only that help.
				try
				{
					CrawlerSuite.Core.SpellCheck.HyphenParts.WriteLog(
						Path.Combine(ctx.SaveDirectory, LogFileNames.HyphenatedParts),
						SpellWordList.Read(ctx.SaveDirectory),
						_dictionaryBundles,
						config.SpellCheckWordPrefixesToStrip,
						config.GermanFugenelemente,
						acronymRule);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					Logger.LogWarning($"Hyphenated word parts (log 36) could not be written: {ex.Message}");
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Spell engine pass failed; spell tickets unavailable this run: {ex}");
				RunUi.WriteError($"Spell-check failed: {(ex is AggregateException ae ? ae.Flatten().InnerExceptions[0].Message : ex.Message)}");
			}
		}

		// ── Step 18b: Bulk inline-<script> scan (diagnostic, opt-in) ─────────

		private static (int Blocks, int Findings) Step_BulkScanPageScript(
			CrawlerRunContext ctx,
			Config config)
		{
			Logger.LogInfo("Running bulk inline-<script> scan (BulkScanPageScript).");
			try
			{
				var keys = DictionaryKeys(config.SpellCheckEngine.SpellCheckJavaScript.ScriptBulkScanDictionaries);
				_dictionaryBundles.Load(keys);
				var result = CrawlerSuite.Core.SpellCheck.BulkScriptScanner.Run(
					ctx.FileDownloadDirectory,
					config.FilePattern,
					ctx.BulkScriptBlobLogPath,
					ctx.BulkScriptFindingsLogPath,
					keys,
					_dictionaryBundles,
					config.SpellCheckWordPrefixesToStrip,
					config.GermanFugenelemente,
					CrawlIndex.LookUpUrlForFile,
					CrawlerSuite.Core.SpellCheck.AcronymRule.From(config.SpellCheckEngine),
					properNames: CrawlerSuite.Core.Quality.ProperNames.From(config));
				return (result.Blocks, result.Findings);
			}
			catch (Exception ex)
			{
				Logger.LogError($"Bulk page-script scan failed: {ex.Message}");
				return (0, 0);
			}
		}

		private static (int Files, int Findings) Step_ScanJsFiles(
			CrawlerRunContext ctx,
			Config config)
		{
			Logger.LogInfo("Running JS-file spell-check scan (ScanScriptFilesInDownload).");
			try
			{
				var jsOptions = config.SpellCheckEngine.SpellCheckJavaScript;
				var keys = DictionaryKeys(jsOptions.ScriptFileScanDictionaries);
				_dictionaryBundles.Load(keys);
				var fileScriptSkips = new CrawlerSuite.Core.Settings.ScriptSkipCounter(jsOptions.TokensToFilter, jsOptions.TokensToFilter.Disabled);
				var result = CrawlerSuite.Core.SpellCheck.JsFileScanner.Run(
					ctx.FileDownloadDirectory,
					ctx.JsFilesSpellCheckLogPath,
					ctx.JsFilesSpellCheckTrimmedLogPath,
					ctx.JsFilesSpellCheckUniqueLogPath,
					ctx.JsFilesSpellCheckRoutingLogPath,
					config.FilePattern,
					config.SpellCheckEngine.SpellCheckJavaScript.JsFindingPageReachThreshold,
					CrawlIndex.LookUpUrlForFile,
					keys,
					_dictionaryBundles,
					config.SpellCheckWordPrefixesToStrip,
					config.GermanFugenelemente,
					config.SpellCheckEngine.SpellCheckJavaScript.TokensToFilter,
					fileScriptSkips,
					CrawlerSuite.Core.SpellCheck.AcronymRule.From(config.SpellCheckEngine),
					properNames: CrawlerSuite.Core.Quality.ProperNames.From(config));

				// How often each script string to skip was needed in the script files (settings screen).
				try
				{
					CrawlerSuite.Core.Settings.SettingsUsage.Merge(ctx.SaveDirectory, new Dictionary<string, IReadOnlyDictionary<string, CrawlerSuite.Core.Settings.UsageCount>>
					{
						[CrawlerSuite.Core.Settings.SettingsUsage.ScriptStringsInFiles] = fileScriptSkips.Counts(),
					});
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					Logger.LogWarning($"Settings usage could not be written: {ex.Message}");
				}

				// Feed the file-scan findings into the SAME triage as page findings by converting
				// them to WordTickets and merging onto LastSpellTickets (which the triage step consumes).
				var scriptTickets = CrawlerSuite.Core.SpellCheck.ScriptSpellingTickets.FromBundleFindings(result.BundleFindings);
				if (scriptTickets.Count > 0)
				{
					var merged = new List<CrawlerSuite.Core.SpellCheck.WordTicket>();
					if (ctx.LastSpellTickets != null)
					{
						merged.AddRange(ctx.LastSpellTickets);
					}
					merged.AddRange(scriptTickets);
					ctx.LastSpellTickets = merged;
				}

				return (result.Files, result.Findings);
			}
			catch (Exception ex)
			{
				Logger.LogError($"JS-file spell-check scan failed: {ex.Message}");
				return (0, 0);
			}
		}

		// ── Step 26: Copy findings to site root ──────────────────────────────

		private static void Step_CopySitemapToSiteRoot(
			CrawlerRunContext ctx, string workingFolder, string urlDirectory)
		{
			// Findings are not copied to the site root: the operator-facing output
			// (IssueTracking.log + TicketText.log) is written there directly. The
			// per-run finding logs stay in the timestamp snapshot; only the sitemap is
			// consumed from the stable site-root path, so it alone is copied up.
			Logger.LogInfo("Copy sitemap to site directory.");
			CopyFilesToDirectory(workingFolder, urlDirectory, ctx.SitemapPath);
			RunUi.WriteStepRow("Sitemap copy", "→ site root");
		}

		private static void CopyFilesToDirectory(string workingFolder, string urlDirectory, params string[] sourceFiles)
		{
			if (string.IsNullOrWhiteSpace(workingFolder))
			{
				throw new ArgumentException(null, nameof(workingFolder));
			}

			if (string.IsNullOrWhiteSpace(urlDirectory))
			{
				throw new ArgumentException(null, nameof(urlDirectory));
			}

			if (sourceFiles == null || sourceFiles.Length == 0)
			{
				return;
			}

			var destDir = Path.Combine(workingFolder, urlDirectory);
			Directory.CreateDirectory(destDir);

			foreach (var src in sourceFiles)
			{
				if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
				{
					continue;
				}
				var destPath = Path.Combine(destDir, Path.GetFileName(src));
				File.Copy(src, destPath, overwrite: true);
			}
		}

		private static void PreloadDictionaries(
			string urlDirectory, string customDictionaryFile, List<DictionaryBundleConfig> bundles,
			string foreignDictionaryFile)
		{
			// The user dictionary holds words right on every site; the site dictionary
			// (dictionaries\<site folder>.dic) words right on this site only. Both count in
			// every language.
			var siteSpecificDictionary = AppPaths.Dictionary($"{urlDirectory}.dic");
			var customDictionaryPath = AppPaths.Dictionary(customDictionaryFile);
			var foreignDictionaryPath = string.IsNullOrWhiteSpace(foreignDictionaryFile)
				? string.Empty
				: AppPaths.Dictionary(foreignDictionaryFile);

			// Make the opt-in foreign dictionary's state visible: off when unset, a word
			// count when loaded, and a NAMED WARNING when configured but not found — so a
			// missing/misnamed file announces itself instead of silently doing nothing.
			if (string.IsNullOrWhiteSpace(foreignDictionaryFile))
			{
				_foreignDictionarySummary = "off (CustomForeignDictionaryFile not set)";
			}
			else if (!File.Exists(foreignDictionaryPath))
			{
				_foreignDictionarySummary = $"NOT FOUND at {foreignDictionaryPath}";
				Logger.LogWarning(
					$"CustomForeignDictionaryFile is set to '{foreignDictionaryFile}' but no file was found " +
					$"at '{foreignDictionaryPath}' — no foreign-language words loaded.");
			}
			else
			{
				var foreignCount = Loader.ReadForeignDictionaryWords(foreignDictionaryPath).Count;
				_foreignDictionarySummary = $"{foreignCount} word(s) from {foreignDictionaryFile}";
				Logger.LogInfo(
					$"Loaded {foreignCount} foreign-language word(s) from {foreignDictionaryPath}.");
			}

			if (bundles.Count == 0)
			{
				Logger.LogWarning("No DictionaryBundles configured — spell-checking will produce no results.");
				return;
			}

			foreach (var bundle in bundles.Where(b => string.IsNullOrWhiteSpace(b.LanguageCode)))
			{
				Logger.LogWarning("DictionaryBundles: skipping entry with empty LanguageCode.");
			}

			var usable = bundles.Where(b => !string.IsNullOrWhiteSpace(b.LanguageCode)).ToList();
			_flavours = Lexicon.DictionaryFlavours.From(usable);
			foreach (var problem in Lexicon.DictionaryFlavours.Problems(usable))
			{
				Logger.LogWarning($"DictionaryBundles: {problem}");
			}

			// A dictionary loads when first asked for: by a page in its language, a script scan
			// configured with it, or a check across all languages; each sets its Language and Primary flag.
			var configOf = new Dictionary<string, DictionaryBundleConfig>(StringComparer.OrdinalIgnoreCase);
			foreach (var bundle in usable)
			{
				configOf.TryAdd(bundle.LanguageCode, bundle);
			}

			var flavours = _flavours;
			Bundle LoadOne(string code)
			{
				var bundle = configOf[code];
				var loaded = Loader.Load(
					AppPaths.Resolve(bundle.DicFile),
					AppPaths.Resolve(bundle.AffFile),
					customDictionaryPath,
					siteSpecificDictionary,
					foreignDictionaryPath,
					false,
					keepCharacterMap: Bundle.ActiveLevers.CharacterMap);
				loaded.Language = bundle.LanguageCode;
				loaded.IsPrimary = string.Equals(flavours.Resolve(Lexicon.LanguageTags.BaseOf(bundle.LanguageCode)),
					bundle.LanguageCode.Trim(), StringComparison.OrdinalIgnoreCase);
				return loaded;
			}

			// For a repeated language code the last entry counts (it overwrites the earlier one).
			var set = new Lexicon.BundleSet(usable.Select(b => b.LanguageCode), LoadOne);
			foreach (var bundle in usable)
			{
				configOf[bundle.LanguageCode] = bundle;
			}

			// The first dictionary loads now, alone: it validates the shared user, site and foreign
			// dictionaries, so a bad shared file halts the run here, before any check.
			if (usable.Count > 0)
			{
				_ = set[usable[0].LanguageCode];
			}

			_dictionaryBundles = set;
			foreach (var code in set.Keys)
			{
				// Expose this dictionary's accept-check to dictionary-dependent enrichers
				// (e.g. the Arabic alif-hamza roundtrip). Presence here is the runtime
				// "is language X configured?" signal — an enricher gates on it; the dictionary
				// itself loads on its first check.
				var key = code;
				CrawlerSuite.Core.SpellCheck.EnrichmentDictionaries.Register(key, word => set[key].Check(word));
			}

			// A language asked for without a flavour ("en") reaches its Primary, as pages do.
			foreach (var (language, primary) in _flavours.MultiFlavour())
			{
				var key = primary;
				CrawlerSuite.Core.SpellCheck.EnrichmentDictionaries.Register(language, word => set[key].Check(word));
			}
		}

		// ── Row-count helpers (artifact-derived; safe on missing/unreadable files) ──

		/// <summary>Non-blank data lines in a log, minus the single header row.</summary>
		private static int CountDataLines(string path)
		{
			try
			{
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
				{
					return 0;
				}
				int lines = File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l));
				return lines > 0 ? lines - 1 : 0; // first non-blank line is the header
			}
			catch
			{
				return 0;
			}
		}

		/// <summary>Number of &lt;loc&gt; entries in a sitemap.xml.</summary>
		private static int CountSitemapEntries(string path)
		{
			try
			{
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
				{
					return 0;
				}
				var text = File.ReadAllText(path);
				int count = 0, idx = 0;
				while ((idx = text.IndexOf("<loc>", idx, StringComparison.Ordinal)) >= 0)
				{
					count++;
					idx += 5;
				}
				return count;
			}
			catch
			{
				return 0;
			}
		}

		/// <summary>Files matching the pattern under a directory (recursive).</summary>
		private static int CountFiles(string directory, string pattern)
		{
			try
			{
				if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
				{
					return 0;
				}
				return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Count();
			}
			catch
			{
				return 0;
			}
		}
	}
}
