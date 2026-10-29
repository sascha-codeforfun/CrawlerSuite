using System;
using CrawlerSuite.Core.Lexicon;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HtmlAgilityPack;

namespace CrawlerSuite.Core.SpellCheck
{
	/// <summary>
	/// The per-bundle findings emitted out of the file scan, grouped so the eventual
	/// SCRIPT_SPELLING tickets can be routed. Reach decides routing: CLEAR (<see cref="IsBulk"/> false,
	/// reach ≤ threshold) → tickets carry the reached page(s); BULK (reach &gt; threshold) → tickets
	/// carry the stable, hash-stripped bundle key so they survive a re-deploy. The words are the distinct
	/// flagged tokens for this bundle (first excerpt kept). This is the payload; turning it into
	/// tickets for the W→U triage loop is done by <c>ScriptSpellingTickets</c>.
	/// Namespace-level so it is shared across the scanner classes and tests.
	/// </summary>
	public readonly record struct ScriptBundleFindings(
		string BundlePath,
		string StableKey,
		string BundleUrl,
		int Reach,
		bool IsBulk,
		IReadOnlyList<string> Pages,
		IReadOnlyList<ScriptWordHit> Words);

	/// <summary>
	/// Opt-in diagnostic (ScanScriptFilesInDownload, default OFF): spell-checks the raw external .js
	/// files in the crawl directory — the big site bundles — reusing the inline scanner's core
	/// (<see cref="BulkScriptScanner.ScanText"/>). Unlike the inline pass there is no harvest/blob:
	/// each file IS its own source, so provenance is the file's path (relative to the crawl directory),
	/// not an injected header. Same dictionaries, same gates ON, pruning OFF. TokensToFilter
	/// applies here too (whole-literal, single-token-safe, so it can never hide a misspelling); it stays
	/// OFF only on the bulk-PAGE audit pass, whose job is to run raw and surface blind spots.
	///
	/// Encoding: JS has no reliable charset signal and bundlers concatenate mixed-encoding sources, so
	/// files are read UTF-8-with-replacement and any literal containing U+FFFD is skipped whole (see
	/// <see cref="BulkScriptScanner.ScanText"/>) — gaps are visible in the manifest, never mojibake.
	///
	/// Minified vendor bundles are mostly machine tokens, so the raw scan is a firehose. The
	/// per-literal PROSE-RATIO GATE (<see cref="ScriptProseRatioTau"/>) demotes any literal whose word
	/// tokens are mostly non-words (ids, events, glyph tables, kebab keys, framework dev-strings,
	/// runtime-split token lists) while keeping real sentences — so a real <c>title:</c> / <c>text:</c>
	/// / <c>infoText:</c> typo still surfaces. Output is TWO logs:
	///   • log 30 (full / debug) — every scanned file, every surviving finding, AND a one-line note for
	///     each gated literal ("# gated (ratio …) · file · literal"). The audit source of truth.
	///   • log 31 (trimmed / triage) — the same manifest, but only the surviving findings. The list a
	///     human triages. Log 30 is the debug source from which log 31 is distilled.
	/// Both are written in the SAME single pass (no second scan).
	/// </summary>
	public static class JsFileScanner
	{
		public readonly record struct Result(int Files, int Findings, int SkippedUndecodable, int FilesTooLarge, int GatedLiterals, int SuppressedFindings, IReadOnlyList<ScriptBundleFindings> BundleFindings);

		// Assemble one bundle's emitted findings: dedupe raw hits to distinct words (first excerpt
		// kept, original order), and bake the reach-based routing decision. Pure and side-effect-free so
		// the routing/dedupe contract is unit-testable without a spell checker or a crawl directory.
		internal static ScriptBundleFindings BuildBundleFindings(
			string bundlePath, string stableKey, string bundleUrl, int reach, int reachThreshold,
			IReadOnlyList<string> pages, IEnumerable<ScriptWordHit> rawHits)
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var words = new List<ScriptWordHit>();
			foreach (var hit in rawHits)
			{
				if (seen.Add(hit.Word))
				{
					words.Add(hit);
				}
			}

			bool isBulk = reach > reachThreshold;
			return new ScriptBundleFindings(bundlePath, stableKey, bundleUrl, reach, isBulk, pages, words);
		}

		// Pathological-file guard for the raw read. The site bundles we target are single-digit MB; this
		// only fences off a runaway file from OOMing the run. [REVIEW] promote to config if ever needed.
		private const long MaxFileBytes = 32L * 1024 * 1024;

		// Prose-ratio gate threshold: a literal is demoted when (union-miss word tokens / total
		// word tokens) >= this. 0.4 sits between machine-token literals, which score high, and
		// real prose carrying a typo, which scores low. [REVIEW] promote to a
		// config knob (e.g. SpellCheckJavaScript.ScriptProseRatioTau) if a site ever needs to retune.
		private const double ScriptProseRatioTau = 0.4;

		public static Result Run(
			string downloadDirectory,
			string findingsPath,
			string trimmedFindingsPath,
			string uniqueWordsPath,
			string routingPreviewPath,
			string pagePattern,
			int reachThreshold,
			Func<string, string> fileToUrl,
			IReadOnlyList<string> dictionaries,
			IReadOnlyDictionary<string, Bundle> bundles,
			IReadOnlyList<string> prefixesToStrip,
			IReadOnlyList<string> fugenelemente,
			IReadOnlyList<string> tokensToFilter,
			CrawlerSuite.Core.Settings.ScriptSkipCounter? scriptSkips = null,
			AcronymRule? acronyms = null,
			CrawlerSuite.Core.Quality.ProperNames? properNames = null)
		{
			using var outw = new StreamWriter(findingsPath, append: false, IssueLogWriter.Utf8WithBom) { NewLine = FileFormat.NewLine };
			using var trimw = new StreamWriter(trimmedFindingsPath, append: false, IssueLogWriter.Utf8WithBom) { NewLine = FileFormat.NewLine };

			outw.WriteLine("# 30 — JS file spell-check (ScanScriptFilesInDownload) · FULL / debug");
			trimw.WriteLine("# 31 — JS file spell-check (ScanScriptFilesInDownload) · TRIMMED / triage");

			if (dictionaries == null || dictionaries.Count == 0 || !dictionaries.All(bundles.ContainsKey))
			{
				// Config validation halts the run before here on enabled+empty, so this is only reachable
				// if a named dictionary is not loaded. Note and no-op rather than crash.
				const string note = "# No usable ScriptBulkScanDictionaries configured (a named dictionary is not loaded) — nothing scanned.";
				outw.WriteLine(note);
				trimw.WriteLine(note);
				WriteUniqueWords(uniqueWordsPath, new HashSet<string>(StringComparer.Ordinal));
				WriteRoutingPreview(routingPreviewPath, new List<RoutingEntry>(), reachThreshold, 0, 0);
				return new Result(0, 0, 0, 0, 0, 0, System.Array.Empty<ScriptBundleFindings>());
			}

			string tauText = ScriptProseRatioTau.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
			// TokensToFilter applies to the file scan: the file scan has no boilerplate pruning, and the
			// filter is whole-literal/single-token-safe, so it can never hide a misspelling. The bulk
			// PAGE audit pass stays raw by design.
			var siteTokensToFilter = new HashSet<string>(
				tokensToFilter ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
			string filterText = siteTokensToFilter.Count > 0 ? $"ON ({siteTokensToFilter.Count})" : "OFF";
			string header = $"# dictionaries: {string.Join(", ", dictionaries)} · gates ON · pruning OFF · TokensToFilter {filterText} · UTF-8 (replacement; undecodable literals skipped whole) · prose-ratio gate τ={tauText}{(acronyms is null ? string.Empty : " · acronyms not checked (" + acronyms.Describe() + ")")}";
			outw.WriteLine(header);
			outw.WriteLine("# A '# gated (...)' line marks a literal demoted by the prose-ratio gate (its findings are suppressed here and absent from log 31).");
			outw.WriteLine("# A literal of one word (beside punctuation or digits at most) is dropped before any dictionary and gets no line: it can never yield a finding.");
			outw.WriteLine("# columns: word <TAB> file <TAB> context");
			outw.WriteLine();

			trimw.WriteLine(header);
			trimw.WriteLine($"# Trimmed view: only findings from literals that PASSED the prose-ratio gate. Full output incl. gated literals: {Path.GetFileName(findingsPath)}");
			trimw.WriteLine("# columns: word <TAB> file <TAB> context");
			trimw.WriteLine();

			var files = EnumerateJsFiles(downloadDirectory);

			// As given: a copy would load every dictionary (BundleSet loads each when first asked for).
			var bundleMap = bundles;
			int totalFindings = 0;
			int totalSkipped = 0;
			int tooLarge = 0;
			int totalGated = 0;
			int totalSuppressed = 0;

			// Flat unique-token view (log 32). Accumulated across every file from the SAME kept-
			// findings stream that feeds log 31, deduped exact (Ordinal — "Ausstatung" ≠ "ausstatung"),
			// written sorted at the end. No second scan; no influence on what is flagged.
			var uniqueWords = new HashSet<string>(StringComparer.Ordinal);

			// Reverse index (bundle → pages) so each file's findings can carry their REACH. Built
			// once from the saved page HTML. The routing decision below keys on reach alone — small
			// reach is locally fixable, large reach is a site-wide priority call. It deliberately does
			// NOT infer cross-copy DRIFT (same component diverging across page copies) from any
			// spelling signal: that is a separate similarity/diff problem and unsound to read from
			// spell findings (see ScriptPageIndex). Log 33 previews the routing; the bundle findings
			// below carry it into the tickets.
			var pageIndex = ScriptPageIndex.BuildFromDownload(
				downloadDirectory, pagePattern, fileToUrl, out int pagesIndexed, out int pagesUnresolved);
			var routing = new List<RoutingEntry>();
			var bundleFindings = new List<ScriptBundleFindings>(); // per-bundle emitted findings

			// Files are scanned in parallel, each into its own buffers; the buffers are then written
			// in file order, so logs 30–33 are the same as a one-by-one pass. The spell bundles are
			// read-only and already shared by the parallel page spell-check.
			// The largest files start first and each thread takes the next file as it frees up, so a
			// big bundle never starts last and leaves one core working alone at the end.
			var scanned = new FileScan[files.Count];
			RunUi.StepTotal(files.Count);
			var largestFirst = LargestFirst(files);
			Parallel.ForEach(Partitioner.Create(largestFirst, EnumerablePartitionerOptions.NoBuffering), i =>
			{
				scanned[i] = ScanFile(files[i], downloadDirectory, dictionaries, bundleMap, prefixesToStrip, fugenelemente, siteTokensToFilter, scriptSkips, acronyms, properNames);
				RunUi.StepAdvance();
			});

			for (int i = 0; i < files.Count; i++)
			{
				var scan = scanned[i];
				outw.Write(scan.Full);
				trimw.Write(scan.Trimmed);
				uniqueWords.UnionWith(scan.UniqueWords);
				if (scan.TooLarge)
				{
					tooLarge++;
				}

				if (scan.Outcome is not { } outcome)
				{
					continue;
				}

				totalFindings += outcome.Findings;
				totalSkipped += outcome.Skipped;
				totalGated += outcome.GatedLiterals;
				totalSuppressed += outcome.SuppressedFindings;

				// Only files that produced kept findings can become tickets, so only they need a
				// routing decision. Resolve the bundle's own URL → stable key → reach; reach decides
				// CLEAR (≤ threshold, per-page) vs BULK (> threshold, per-bundle). Captured for the
				// preview log (routing) and for the tickets (bundle findings).
				if (outcome.Findings > 0)
				{
					string url = SafeResolve(fileToUrl, Path.GetFileName(files[i]));
					string key = ScriptUrlKey.StableKey(url);
					int reach = key.Length == 0 ? 0 : pageIndex.Reach(key);
					var pages = pageIndex.Pages(key);
					routing.Add(new RoutingEntry(scan.Rel, key, url, outcome.Findings, reach, pages));
					bundleFindings.Add(BuildBundleFindings(scan.Rel, key, url, reach, reachThreshold, pages, scan.Hits));
				}
			}

			string summary =
				$"# total: {totalFindings} kept finding(s) across {files.Count} file(s) · "
				+ $"{totalGated} literal(s) gated ({totalSuppressed} non-word occurrence(s) suppressed) · "
				+ $"{totalSkipped} undecodable literal(s) skipped · {tooLarge} file(s) skipped (too large)";

			outw.WriteLine();
			outw.WriteLine(summary);
			trimw.WriteLine();
			trimw.WriteLine(summary);

			WriteUniqueWords(uniqueWordsPath, uniqueWords);
			WriteRoutingPreview(routingPreviewPath, routing, reachThreshold, pagesIndexed, pagesUnresolved);

			return new Result(files.Count, totalFindings, totalSkipped, tooLarge, totalGated, totalSuppressed, bundleFindings);
		}

		/// <summary>
		/// The positions of <paramref name="files"/>, largest file first (equal sizes in list order).
		/// Only the order of work; results are still written in list order.
		/// </summary>
		internal static int[] LargestFirst(IReadOnlyList<string> files)
		{
			var sizes = new long[files.Count];
			for (int i = 0; i < files.Count; i++)
			{
				try
				{
					sizes[i] = new FileInfo(files[i]).Length;
				}
				catch (IOException)
				{
					sizes[i] = 0;
				}
				catch (UnauthorizedAccessException)
				{
					sizes[i] = 0;
				}
			}

			return Enumerable.Range(0, files.Count).OrderByDescending(i => sizes[i]).ThenBy(i => i).ToArray();
		}

		/// <summary>What scanning one file produced: its lines for logs 30 and 31, and its findings.</summary>
		private sealed record FileScan(string Rel, string Full, string Trimmed, BulkScriptScanner.ScanOutcome? Outcome, bool TooLarge,
			IReadOnlyCollection<string> UniqueWords, IReadOnlyList<ScriptWordHit> Hits);

		/// <summary>Scans one file into its own buffers (exactly the lines the one-by-one pass wrote for it).</summary>
		private static FileScan ScanFile(
			string file, string downloadDirectory, IReadOnlyList<string> dictionaries, IReadOnlyDictionary<string, Bundle> bundleMap,
			IReadOnlyList<string> prefixesToStrip, IReadOnlyList<string> fugenelemente, HashSet<string> siteTokensToFilter,
			CrawlerSuite.Core.Settings.ScriptSkipCounter? scriptSkips,
			AcronymRule? acronyms,
			CrawlerSuite.Core.Quality.ProperNames? properNames)
		{
			using var outw = new StringWriter(System.Globalization.CultureInfo.InvariantCulture) { NewLine = FileFormat.NewLine };
			using var trimw = new StringWriter(System.Globalization.CultureInfo.InvariantCulture) { NewLine = FileFormat.NewLine };
			var uniqueWords = new HashSet<string>(StringComparer.Ordinal);
			var fileHits = new List<ScriptWordHit>(); // this bundle's emitted findings
			string rel = Relative(downloadDirectory, file);
			FileScan Done(BulkScriptScanner.ScanOutcome? outcome, bool tooLarge = false) =>
				new(rel, outw.ToString(), trimw.ToString(), outcome, tooLarge, uniqueWords, fileHits);

			long size;
			try
			{
				size = new FileInfo(file).Length;
			}
			catch (Exception ex)
			{
				// Manifest stays parallel across both logs so either can prove the file was seen.
				WriteBoth(outw, trimw, $"# ── {rel} · UNREADABLE ({ex.Message}) ──");
				return Done(null);
			}

			if (size > MaxFileBytes)
			{
				WriteBoth(outw, trimw, $"# ── {rel} · {FormatSize(size)} · SKIPPED (exceeds {FormatSize(MaxFileBytes)} guard) ──");
				return Done(null, tooLarge: true);
			}

			WriteBoth(outw, trimw, $"# ── {rel} · {FormatSize(size)} ──");

			string content;
			try
			{
				content = ReadJsFile(file);
			}
			catch (Exception ex)
			{
				WriteBoth(outw, trimw, $"#    (could not read: {ex.Message})");
				return Done(null);
			}

			// One checker and one carrier node per file: nothing mutable is shared between threads.
			// Acronyms are left unchecked here as in the page text (the same setting).
			var checker = new ToolsSpellChecker(bundleMap[dictionaries[0]], bundleMap, prefixesToStrip, fugenelemente, acronyms, properNames: properNames);
			var node = HtmlNode.CreateNode("<script></script>");
			var outcome = BulkScriptScanner.ScanText(
				content,
				_ => rel,
				dictionaries,
				checker,
				node,
				outw,
				trimmedWriter: trimw,
				proseRatioGate: true,
				proseRatioTau: ScriptProseRatioTau,
				uniqueWords: uniqueWords,
				siteTokensToFilter: siteTokensToFilter,
				findingSink: fileHits,
				scriptSkips: scriptSkips?.For(rel));

			if (outcome.Skipped > 0)
			{
				WriteBoth(outw, trimw, $"#    ({outcome.Skipped} undecodable literal(s) skipped)");
			}

			return Done(outcome);
		}

		// Log 32 — a plain, flat list of the unique kept-finding words, one per line, sorted
		// case-insensitively (mirrors log 11's unique view; no dictionary/lang column, which would be
		// noise on a many-dictionary bulk scan). Dedup is exact (Ordinal); display order is culture-aware
		// case-insensitive so the list reads naturally for triage.
		private static void WriteUniqueWords(string path, HashSet<string> words)
		{
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			var ordered = words.ToList();
			ordered.Sort(WordListOrder.Comparer);

			using var w = new StreamWriter(path, append: false, IssueLogWriter.Utf8WithBom) { NewLine = FileFormat.NewLine };
			w.WriteLine("# 32 — JS file spell-check (ScanScriptFilesInDownload) · UNIQUE tokens");
			w.WriteLine($"# Flat, sorted, de-duplicated list of every distinct kept-finding word ({ordered.Count}). One per line, no context.");
			w.WriteLine();
			foreach (var word in ordered)
			{
				w.WriteLine(word);
			}
		}

		// One bundle that produced findings, with its resolved URL, stable key, and reach.
		private readonly record struct RoutingEntry(
			string Rel, string Key, string Url, int Findings, int Reach, IReadOnlyList<string> Pages);

		// Resolve a disk filename to its source URL via the injected authoritative lookup, swallowing
		// any failure to an empty string so an unresolved file shows as a visible gap, not a crash.
		private static string SafeResolve(Func<string, string> fileToUrl, string filename)
		{
			try
			{
				return fileToUrl(filename) ?? string.Empty;
			}
			catch
			{
				return string.Empty;
			}
		}

		// Log 33 — routing PREVIEW. For every bundle that produced kept findings, shows the reach
		// (how many pages load it) and the resulting routing — CLEAR (reach ≤ threshold → would become
		// per-page findings) vs BULK (reach > threshold → would become one per-bundle finding). This is
		// the verification artifact: it makes the join and the reach visible in one place, so the
		// numbers can be checked against the crawl. A bundle whose
		// URL did not resolve (reach 0, no key) is flagged as UNRESOLVED — a gap to investigate, never
		// silently dropped.
		private static void WriteRoutingPreview(
			string path, List<RoutingEntry> routing, int reachThreshold, int pagesIndexed, int pagesUnresolved)
		{
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			using var w = new StreamWriter(path, append: false, IssueLogWriter.Utf8WithBom) { NewLine = FileFormat.NewLine };
			w.WriteLine("# 33 — JS file spell-check (ScanScriptFilesInDownload) · routing PREVIEW");
			w.WriteLine($"# Reverse index built from {pagesIndexed} page(s); {pagesUnresolved} page(s) could not be resolved to a URL.");
			w.WriteLine($"# reach ≤ {reachThreshold} → CLEAR (per-page findings); reach > {reachThreshold} → BULK (one per-bundle finding).");
			w.WriteLine("# Preview only — nothing here is written to the ticket ledger yet.");
			w.WriteLine();

			foreach (var e in routing.OrderByDescending(r => r.Reach).ThenBy(r => r.Rel, StringComparer.OrdinalIgnoreCase))
			{
				if (e.Key.Length == 0 || e.Reach == 0)
				{
					w.WriteLine($"UNRESOLVED  findings={e.Findings}  file={e.Rel}  (url='{e.Url}')");
					continue;
				}

				string route = e.Reach <= reachThreshold ? "CLEAR" : "BULK";
				w.WriteLine($"{route,-5}  reach={e.Reach}  findings={e.Findings}  {e.Key}");
				if (route == "CLEAR")
				{
					foreach (var page in e.Pages)
					{
						w.WriteLine($"        page: {page}");
					}
				}
			}

			if (routing.Count == 0)
			{
				w.WriteLine("# (no bundles produced findings)");
			}
		}

		// Write one line to both logs, keeping their per-file manifests identical so either log alone
		// proves a clean run actually scanned every file.
		private static void WriteBoth(TextWriter a, TextWriter b, string line)
		{
			a.WriteLine(line);
			b.WriteLine(line);
		}

		// .js under the crawl directory, EXCLUDING the base64assets/ subtree (those are stripped copies
		// the bloat analyzer may emit — scanning both would double-count). The "*.js" mask can also match
		// .json via legacy 8.3 short names, so the real extension is re-checked. Sorted for stable logs.
		internal static List<string> EnumerateJsFiles(string downloadDirectory)
		{
			if (string.IsNullOrEmpty(downloadDirectory) || !Directory.Exists(downloadDirectory))
			{
				return new List<string>();
			}

			return Directory.EnumerateFiles(downloadDirectory, "*.js", SearchOption.AllDirectories)
				.Where(p => Path.GetExtension(p).Equals(".js", StringComparison.OrdinalIgnoreCase))
				.Where(p => !PathHasSegment(p, "base64assets"))
				.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		internal static bool PathHasSegment(string path, string segment) =>
			path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				.Any(s => s.Equals(segment, StringComparison.OrdinalIgnoreCase));

		private static string Relative(string root, string file)
		{
			try
			{
				return Path.GetRelativePath(root, file);
			}
			catch
			{
				return Path.GetFileName(file);
			}
		}

		// [REVIEW] JS files carry no reliable charset signal and bundlers concatenate mixed-encoding
		// sources into one file. Decode the WHOLE file as UTF-8 with replacement: clean UTF-8 (the common
		// case) decodes fully; bytes from a non-UTF-8 segment (Windows-1252 / ANSI) become U+FFFD rather
		// than plausible mojibake letters, and ScanText skips any literal carrying U+FFFD. Encoding.UTF8
		// (the shared instance) uses replacement, not exception, fallback and never throws; the reader is
		// BOM-aware. If real per-segment detection is ever needed, change only this seam.
		internal static string ReadJsFile(string path) => CrawlerSuite.Core.Host.WorkFile.ReadAllText(path, Encoding.UTF8);

		internal static string FormatSize(long bytes)
		{
			if (bytes >= 1024L * 1024)
			{
				return FormattableString.Invariant($"{bytes / (1024.0 * 1024.0):0.0} MB");
			}

			if (bytes >= 1024)
			{
				return FormattableString.Invariant($"{bytes / 1024.0:0.0} KB");
			}

			return FormattableString.Invariant($"{bytes} B");
		}
	}
}
