namespace CrawlerSuite.Core
{
	// ── CrawlerRunContext ─────────────────────────────────────────────────────
	//
	// Per-run mutable state, created by CrawlerApp.RunAsync and threaded into
	// the snapshot, orchestration and analysis classes that need to read or
	// mutate it (the replay choice sets IsDebugSession; the empty-download
	// recovery clears it and recomputes the timestamp and all paths).
	//
	// It holds no process-level state: it lives for one run only and is
	// re-created for the next run.
	//
	// Why a class (not a record): several fields are mutated during a run
	// (IsDebugSession, TimeStamp, and all the path strings recomputed by the
	// replay and empty-download branches). A `with`-expression on a record
	// per mutation would be noisy and obscure intent. A mutable class makes
	// the mutation honest.
	// ─────────────────────────────────────────────────────────────────────────

	internal sealed class CrawlerRunContext
	{
		// ── Run mode flags ────────────────────────────────────────────────────

		/// <summary>
		/// True when the analysis pipeline runs on an existing snapshot without
		/// re-downloading. False at startup; set by the snapshot choice "use the
		/// latest" (or Re-run latest) and back to false by the empty-download
		/// recovery.
		/// </summary>
		internal bool IsDebugSession { get; set; }

		/// <summary>A quick re-run of the latest snapshot: no connectivity check (a replay needs no network).</summary>
		internal bool SkipConnectivity { get; set; }

		// True when this run analyses the LATEST snapshot — a fresh crawl (N) or a
		// replay of the most-recent snapshot; runs never pick an older snapshot, so it is
		// always true. Site-level state (the triage ledger) is written only for the
		// latest snapshot, never by a replay of an older one.
		internal bool IsLatestSnapshotPath { get; set; }

		/// <summary>
		/// Set by Step_CheckCmsContentListFreshness when silent mode + stale
		/// CmsContentList: tells Step_PerformPostCrawlPass to skip the post-crawl
		/// download pass for this run. Interactive mode never sets this — the
		/// operator's abort choice ends the whole run before the main crawl
		/// runs, so the pass is never reached.
		/// </summary>
		internal bool SuppressPostCrawlPass { get; set; }

		/// <summary>
		/// Set when the crawl was stopped at the total download limit and the operator chose
		/// to delete the snapshot and start a fresh crawl.
		/// </summary>
		internal bool RecrawlRequested { get; set; }

		// ── Network identity (resolved proxy credentials) ─────────────────────

		// [KEEP-COMMENT] These hold the proxy sign-in this run actually uses, settled
		// by the connectivity check (CrawlerApp.RunOnceAsync, ConnectivityCheck.SignInAsync):
		// empty for the Windows sign-in, or what the operator typed after the proxy refused
		// it. Read thereafter by the main crawl and the post-crawl pass. The
		// configuration holds no credentials; they go with the run: this context, the
		// run's ProxySignIn and the session (ended in CrawlerApp.RunAsync's finally) are the
		// only holders, so nothing outlives the job. One source for every HTTP path, so the
		// preflight and the crawl never authenticate with different identities. Deliberately
		// NOT rebuilt by RebuildTimestampPaths: they are not timestamp-derived, and a replay
		// or empty-download re-point must not wipe resolved credentials.
		internal string ProxyUser { get; set; } = string.Empty;
		internal string ProxyPassword { get; set; } = string.Empty;

		// ── Timestamp and root directories ────────────────────────────────────

		internal string TimeStamp { get; set; } = string.Empty;
		internal string SaveDirectory { get; set; } = string.Empty;

		internal string FileDownloadDirectory { get; set; } = string.Empty;
		internal string FilePrunedDirectory { get; set; } = string.Empty;

		// ── Log paths ─────────────────────────────────────────────────────────

		internal string CrawlerLogPath { get; set; } = string.Empty;
		internal string CrawlerRawLogPath { get; set; } = string.Empty;
		internal string CrawlerLogIndexPath { get; set; } = string.Empty;
		internal string ErrorLogPath { get; set; } = string.Empty;
		internal string ErrorSourcesCsvBasePath { get; set; } = string.Empty;
		internal string SpellErrorLogPath { get; set; } = string.Empty;
		internal string UniqueSpellErrorLogPath { get; set; } = string.Empty;

		/// <summary>Distinct acronyms the page spell-check left unchecked (log 37); null when the rule is off or the pass failed.</summary>
		internal int? AcronymsNotChecked { get; set; }

		/// <summary>Names with a space at a hyphen from the user and site dictionaries, read by the hyphen-spacing check; its use is recorded with the spell check's.</summary>
		internal CrawlerSuite.Core.Quality.ProperNames? ProperNames { get; set; }
		internal string SpellLocatedLogPath { get; set; } = string.Empty;
		internal string WordTicketsDiagnosticLogPath { get; set; } = string.Empty;
		internal string SpellSuppressionSuggestionsLogPath { get; set; } = string.Empty;
		internal string BulkScriptBlobLogPath { get; set; } = string.Empty;
		internal string BulkScriptFindingsLogPath { get; set; } = string.Empty;
		internal string JsFilesSpellCheckLogPath { get; set; } = string.Empty;
		// Trimmed/triage companion to log 30: only findings from literals that passed the
		// prose-ratio gate. Log 30 stays the full debug source; this is the list a human triages.
		internal string JsFilesSpellCheckTrimmedLogPath { get; set; } = string.Empty;
		// Flat, sorted, de-duplicated list of the distinct kept-finding words (no context, no
		// dictionaries). The "what do I go after" view, mirroring page-spelling log 11's unique view.
		internal string JsFilesSpellCheckUniqueLogPath { get; set; } = string.Empty;
		// Routing PREVIEW: per bundle, its reach (pages that load it) and the CLEAR/BULK decision.
		// Verification artifact; nothing here is written to the ticket ledger.
		internal string JsFilesSpellCheckRoutingLogPath { get; set; } = string.Empty;

		// In-memory spell-engine tickets from this run's harvest, handed to the spell triage step
		// for per-page occurrence display. Null when the harvest did not run or threw.
		internal System.Collections.Generic.IReadOnlyList<CrawlerSuite.Core.SpellCheck.WordTicket>? LastSpellTickets { get; set; }

		// Editor-class content-quality issues from this run (post-suppression), handed to the spell
		// step so it can dedup a spell finding against a WORD_COLLISION CQ already reports. Null when
		// content-quality did not run.
		internal System.Collections.Generic.IReadOnlyList<CrawlerSuite.Core.Quality.QualityIssue>? ContentQualityIssues { get; set; }
		internal string FullContentLogPath { get; set; } = string.Empty;
		internal string NotOnlineLogPath { get; set; } = string.Empty;

		/// <summary>Where the site's names pointed in this crawl; null on a replay.</summary>
		internal Security.DnsRecord? Dns { get; set; }

		/// <summary>What each download exclusion kept out in this crawl, both phases; null until a crawl session starts.</summary>
		internal Settings.DownloadExclusionUsage? ExclusionUsage { get; set; }
		internal string ContentCrawlCompareLogFile { get; set; } = string.Empty;
		internal string SitemapPath { get; set; } = string.Empty;
		internal string SeoDataPath { get; set; } = string.Empty;

		/// <summary>
		/// Raw canonical hrefs per page file, read by the SEO step from its parse; the canonical-link
		/// step uses them instead of parsing every page again. Null when the SEO step did not parse.
		/// </summary>
		internal IReadOnlyDictionary<string, string[]>? CanonicalHrefsByFile { get; set; }
		internal string UserDictionaryOrphanWordsFilePath { get; set; } = string.Empty;
		internal string DictionaryNotNeededFilePath { get; set; } = string.Empty;
		internal string RedirectAnalysisPath { get; set; } = string.Empty;
		internal string SelfLinkAnalysisCsvBasePath { get; set; } = string.Empty;
		internal string ForwardingLinksCsvBasePath { get; set; } = string.Empty;
		internal string CanonicalCsvBasePath { get; set; } = string.Empty;
		internal string PdfQualityCsvBasePath { get; set; } = string.Empty;
		internal string PdfRemediationCsvBasePath { get; set; } = string.Empty;
		internal string AssetQualityCsvBasePath { get; set; } = string.Empty;
		internal string ImageReferencesCsvBasePath { get; set; } = string.Empty;
		internal string Base64AssetsLogPath { get; set; } = string.Empty;
		internal string Base64AssetsDirectory { get; set; } = string.Empty;
		internal string ResourceBloatLogPath { get; set; } = string.Empty;
		internal string ResourceBloatBaselineLogPath { get; set; } = string.Empty;
		internal string ContentQualityLogPath { get; set; } = string.Empty;
		internal string CmsTemplateDefectsCsvBasePath { get; set; } = string.Empty;
		internal string ContentTypeMismatchLogPath { get; set; } = string.Empty;
		internal string PdfContentTypeMismatchLogPath { get; set; } = string.Empty;
		internal string ImageContentTypeMismatchLogPath { get; set; } = string.Empty;
		internal string OversizedLogPath { get; set; } = string.Empty;
		internal string OversizedDirectory { get; set; } = string.Empty;

		// ── Site-root tracking files (no timestamp, fixed Power Query paths) ──

		internal string IssueTrackingPath { get; set; } = string.Empty;
		internal string TicketTextPath { get; set; } = string.Empty;

		/// <summary>
		/// Rebuilds every timestamp-dependent path from a new saveDirectory.
		/// Called by the replay branch (replay an existing snapshot) and
		/// the empty-download recovery branch (fresh crawl with new
		/// timestamp). Centralised here so all path-recompute sites stay in
		/// sync — adding a new log path means one edit, not three.
		/// </summary>
		internal void RebuildTimestampPaths(string saveDirectory)
		{
			SaveDirectory = saveDirectory;
			FileDownloadDirectory = Path.Combine(saveDirectory, "download");
			FilePrunedDirectory = Path.Combine(saveDirectory, "pruned");
			CrawlerRawLogPath = Path.Combine(saveDirectory, LogFileNames.CrawlerRaw);
			CrawlerLogPath = Path.Combine(saveDirectory, LogFileNames.Crawler);
			CrawlerLogIndexPath = Path.Combine(saveDirectory, LogFileNames.CrawlerIndex);
			RedirectAnalysisPath = Path.Combine(saveDirectory, LogFileNames.RedirectAnalysis);
			FullContentLogPath = Path.Combine(saveDirectory, LogFileNames.FullContent);
			NotOnlineLogPath = Path.Combine(saveDirectory, LogFileNames.ContentListNotOnline);
			ContentCrawlCompareLogFile = Path.Combine(saveDirectory, LogFileNames.NotDirectlyCrawlable);
			ErrorLogPath = Path.Combine(saveDirectory, LogFileNames.Errors404);
			ErrorSourcesCsvBasePath = Path.Combine(saveDirectory, LogFileNames.Error404SourcesCsvBase);
			SeoDataPath = Path.Combine(saveDirectory, LogFileNames.SeoData);
			SelfLinkAnalysisCsvBasePath = Path.Combine(saveDirectory, LogFileNames.SelfLinkAnalysisCsvBase);
			ForwardingLinksCsvBasePath = Path.Combine(saveDirectory, LogFileNames.ForwardingLinksCsvBase);
			ContentQualityLogPath = Path.Combine(saveDirectory, LogFileNames.ContentQualityIssues);
			UniqueSpellErrorLogPath = Path.Combine(saveDirectory, LogFileNames.SpellErrorsUnique);
			SpellErrorLogPath = Path.Combine(saveDirectory, LogFileNames.SpellErrorsSources);
			SpellLocatedLogPath = Path.Combine(saveDirectory, LogFileNames.SpellErrorsSourceLocation);
			// Spell-engine verbatim diagnostic — what was flagged and where, every harvest run.
			WordTicketsDiagnosticLogPath = Path.Combine(saveDirectory, LogFileNames.WordTickets);
			// Script-source suppression suggestions for SpellCheckJavaScript.TokensToFilter (supplements
			// the JS spell-check feature).
			SpellSuppressionSuggestionsLogPath = Path.Combine(saveDirectory, LogFileNames.SpellcheckSuppressionSuggestions);
			BulkScriptBlobLogPath = Path.Combine(saveDirectory, LogFileNames.BulkScriptBlob);
			BulkScriptFindingsLogPath = Path.Combine(saveDirectory, LogFileNames.BulkScriptFindings);
			JsFilesSpellCheckLogPath = Path.Combine(saveDirectory, LogFileNames.JsFilesSpellCheck);
			JsFilesSpellCheckTrimmedLogPath = Path.Combine(saveDirectory, LogFileNames.JsFilesSpellCheckTrimmed);
			JsFilesSpellCheckUniqueLogPath = Path.Combine(saveDirectory, LogFileNames.JsFilesSpellCheckUnique);
			JsFilesSpellCheckRoutingLogPath = Path.Combine(saveDirectory, LogFileNames.JsFilesSpellCheckRouting);
			UserDictionaryOrphanWordsFilePath = Path.Combine(saveDirectory, LogFileNames.UserDictionaryOrphanWords);
			DictionaryNotNeededFilePath = Path.Combine(saveDirectory, LogFileNames.DictionaryNotNeeded);
			CanonicalCsvBasePath = Path.Combine(saveDirectory, LogFileNames.CanonicalIssuesCsvBase);
			PdfQualityCsvBasePath = Path.Combine(saveDirectory, LogFileNames.PdfQualityCsvBase);
			PdfRemediationCsvBasePath = Path.Combine(saveDirectory, LogFileNames.PdfRemediationCsvBase);
			AssetQualityCsvBasePath = Path.Combine(saveDirectory, LogFileNames.AssetQualityCsvBase);
			ImageReferencesCsvBasePath = Path.Combine(saveDirectory, LogFileNames.ImageReferencesCsvBase);
			Base64AssetsLogPath = Path.Combine(saveDirectory, LogFileNames.Base64Assets);
			Base64AssetsDirectory = Path.Combine(saveDirectory, "base64assets");
			ResourceBloatLogPath = Path.Combine(saveDirectory, LogFileNames.ResourceBloat);
			ResourceBloatBaselineLogPath = Path.Combine(saveDirectory, LogFileNames.ResourceBloatAboveBaseline);
			CmsTemplateDefectsCsvBasePath = Path.Combine(saveDirectory, LogFileNames.CmsTemplateAuthoringDefectsCsvBase);
			ContentTypeMismatchLogPath = Path.Combine(saveDirectory, LogFileNames.ContentTypeExtensionMismatch);
			PdfContentTypeMismatchLogPath = Path.Combine(saveDirectory, LogFileNames.PdfContentTypeExtensionMismatch);
			ImageContentTypeMismatchLogPath = Path.Combine(saveDirectory, LogFileNames.ImageContentTypeExtensionMismatch);
			OversizedLogPath = Path.Combine(saveDirectory, LogFileNames.OversizedFiles);
			OversizedDirectory = Path.Combine(saveDirectory, OversizedFiles.FolderName);
			SitemapPath = Path.Combine(saveDirectory, "sitemap.xml");
		}
	}
}
