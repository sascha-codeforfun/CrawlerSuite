namespace CrawlerSuite.Core.SpellCheck
{
	using System;
	using CrawlerSuite.Core.Lexicon;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;
	using HtmlAgilityPack;
	using CrawlerSuite.Core.Boilerplate;

	/// <summary>
	/// Drives the spell pipeline over a crawl, emitting the three ParallelStore views. This
	/// is the primary spell path and owns spell findings (see AnalysisPipeline.Step_RunSpellCheck).
	/// Inputs (dictionaries, language overrides, prefixes, fugenelemente, per-file cross-language
	/// words) are passed in from the spell step — sourced via the shared dictionary/config
	/// machinery.
	///
	/// This runner does NO dictionary loading and NO language-list invention: the caller supplies
	/// the already-loaded bundles and config-derived lists. The only language logic mirrored here
	/// is the per-file override match + HTML-lang fallback;
	/// AllDictionaries mode is delegated to the same Tools entry point.
	/// </summary>
	public static class NewSpellEngineRunner
	{
		/// <summary>
		/// Per-file inputs the runner needs.
		/// </summary>
		public sealed class FileInput
		{
			public string Filename { get; init; } = string.Empty;
		}

		/// <summary>
		/// Runs the engine over the given files and writes the three views. Returns nothing the
		/// pipeline depends on — the views are its output.
		/// </summary>
		/// <param name="deferSuggestions">Given the writing of log 27 instead of it being written here (null: written here).</param>
		public static IReadOnlyList<WordTicket> Run(
			IEnumerable<FileInput> files,
			string downloadDirectory,
			string uniqueViewPath,
			string sourcesViewPath,
			string locatedViewPath,
			string wordTicketsDiagnosticPath,
			string suppressionSuggestionsPath,
			SpellCheckEngineConfig engineConfig,
			IReadOnlyDictionary<string, Bundle> dictionaryBundles,
			Func<string, HtmlDocument, string> resolveLanguage,
			IReadOnlyList<string> prefixesToStrip,
			IReadOnlyList<string> fugenelemente,
			Func<string, string> lookUpUrlForFile,
			int maxDegreeOfParallelism,
			WordCollisionMatcher? wordCollisions = null,
			CrawlerSuite.Core.Suppressions.AnchorSplitSpellSuppression? anchorSplit = null,
			CrawlerSuite.Core.Suppressions.UnwantedPatternSpellSuppression? unwantedPattern = null,
			CrawlerSuite.Core.Suppressions.AdjacentAnchorSpellSuppression? adjacentAnchor = null,
			CrawlerSuite.Core.Settings.SettingsUsage? usage = null,
			CrawlerSuite.Core.Settings.ScriptSkipCounter? scriptSkips = null,
			AcronymRule? acronyms = null,
			string? acronymViewPath = null,
			Action<int>? acronymsListed = null,
			KnownPartTally? knownParts = null,
			CrawlerSuite.Core.Quality.ProperNames? properNames = null,
			Func<string, string?>? languageKey = null,
			Action<Action>? deferSuggestions = null)
		{
			var resolver = new BoilerplateResolver(engineConfig.BoilerplateGroups);

			// A language tag ("en", "en-us", "de") to the key of the dictionary that checks it: the
			// flavour it names, else its language's dictionary or Primary. Without a resolver, the
			// key is the tag itself when such a dictionary is loaded.
			string? Key(string tag) => languageKey is not null
				? languageKey(tag)
				: dictionaryBundles.ContainsKey(tag) ? tag : null;

			// Materialize once so pages can be slotted by their original position; flattening the
			// slots in index order reproduces the sequential document order EXACTLY, so the emitted
			// views are byte-identical regardless of how many threads ran (only the slot a page
			// lands in is fixed; which thread filled it is irrelevant). This is what lets the run go
			// parallel without sacrificing the linear diffability of the logs.
			var fileList = files as IReadOnlyList<FileInput> ?? files.ToList();
			var perFileTickets = new IReadOnlyList<WordTicket>?[fileList.Count];

			// Slot 27: per-file script-source suppression inputs, slotted in document order like the
			// tickets above so the merged stream is deterministic regardless of thread scheduling.
			var perFileScriptInputs = new List<ScriptSuppressionInput>?[fileList.Count];

			// Log 37: per-page acronym collectors, slotted in document order like the tickets, so the
			// list is the same however the pages were scheduled.
			var perFileAcronyms = new (string Page, AcronymCollector? Collector)[fileList.Count];

			// Known word parts: per-page counters, added up after the pages (sums and unions, so the
			// same however the pages were scheduled).
			var perFileKnownParts = new KnownPartCounter?[fileList.Count];

			// Read-only shared collaborators (verified safe for concurrent reads): the bundle lookup,
			// the boilerplate resolver (all state built in its ctor), globalIgnore/knownDefects
			// matchers, and SpellChecker.Check itself. Bundles are hoisted out of the loop since they
			// never change per page.
			// As given, not copied: a copy would load every dictionary (BundleSet loads each when first asked for).
			var bundleMap = dictionaryBundles;

			// Skip-set = the non-prose attributes (operator override if declared, else the built-in
			// class/id/style default) PLUS the site-specific technical attributes. Case-insensitive.
			IEnumerable<string> nonProse =
				(IEnumerable<string>?)engineConfig.GlobalNonProseHtmlAttributesThatWillBeIgnored
				?? DomTraverser.DefaultNonProseAttributes;
			var skipAttributeNames = new HashSet<string>(nonProse, StringComparer.OrdinalIgnoreCase);
			// Boolean attributes (override or built-in default) — their values are non-prose by
			// definition; union into the same skip lookup. Separate concern, separate override.
			IEnumerable<string> boolAttrs =
				(IEnumerable<string>?)engineConfig.GlobalBooleanHtmlAttributesThatWillBeIgnored
				?? DomTraverser.DefaultBooleanAttributes;
			foreach (var name in boolAttrs)
			{
				skipAttributeNames.Add(name);
			}

			foreach (var name in engineConfig.GlobalAttributesToIgnore ?? Enumerable.Empty<string>())
			{
				skipAttributeNames.Add(name);
			}

			// Meta allowlist: declared list (even empty) REPLACES the default; absent (null) → built-in
			// default {description, keywords}. "Declare it, declare it right" — [] means check no meta.
			IReadOnlySet<string> metaContentNames = engineConfig.MetaContentNamesToSpellCheck is { } declared
				? new HashSet<string>(declared, StringComparer.OrdinalIgnoreCase)
				: DomTraverser.DefaultMetaContentNames;

			// HTML element types whose subtree is removed before checking (svg/object/input/select/…).
			// Plain config list (absent = none), case-insensitive.
			var htmlTagsToIgnore = new HashSet<string>(
				engineConfig.GlobalHtmlTagsToIgnore ?? Enumerable.Empty<string>(),
				StringComparer.OrdinalIgnoreCase);

			// Site-specific non-prose script literals to filter (config: SpellCheckJavaScript.
			// TokensToFilter). Whole-literal, case-insensitive; only consulted for Script runs.
			var scriptTokensToFilter = new HashSet<string>(
				engineConfig.SpellCheckJavaScript.TokensToFilter ?? Enumerable.Empty<string>(),
				StringComparer.OrdinalIgnoreCase);

			// Optional script-only fallback dictionaries (config: SpellCheckJavaScript.ScriptFallbackDictionaries),
			// each a language tag resolved to a loaded dictionary (e.g. "en-GB", "en-US"). An empty or
			// unknown entry is ignored. Passed to RunChecker, which applies them to Script runs only.
			IReadOnlyList<string> scriptFallbackLanguages = (engineConfig.SpellCheckJavaScript.ScriptFallbackDictionaries ?? [])
				.Where(l => !string.IsNullOrWhiteSpace(l))
				.Select(Key)
				.OfType<string>()
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			// Known chrome/template language defects to mute (declared per source-path + text).
			var knownDefects = new KnownDefectMatcher(engineConfig.KnownChromeLanguageDefects);

			// Global technical-ignore: xpath selectors for non-content plumbing (tracking pixels etc.)
			// pruned on EVERY page including reference pages. Built once as an xpath-only BoilerplateMatcher
			// (reusing its node-or-ancestor xpath resolution) — orthogonal to per-group boilerplate.
			BoilerplateMatcher? globalIgnore = engineConfig.GlobalXpathToIgnore is { Count: > 0 } xpaths
				? new BoilerplateMatcher(xpaths.Select(x => new BoilerplateSelector("xpath", x)))
				: null;

			int total = 0, unreadable = 0, noBundle = 0, notChecked = 0, pagesWithFindings = 0;
			Logger.LogInfo(
				$"New spell engine: starting validation pass (boilerplate groups: {engineConfig.BoilerplateGroups.Count}).");

			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount,
			};

			RunUi.StepTotal(fileList.Count);
			Parallel.For(0, fileList.Count, parallelOptions, i =>
			{
				RunUi.StepAdvance();
				try
				{
					var file = fileList[i];
					Interlocked.Increment(ref total);
					string url = lookUpUrlForFile(file.Filename) ?? file.Filename;

					byte[] bytes;
					try
					{
						bytes = CrawlerSuite.Core.Host.WorkFile.ReadAllBytes(Path.Combine(downloadDirectory, file.Filename));
					}
					catch (Exception readEx)
					{
						Interlocked.Increment(ref unreadable);
						Logger.LogWarning($"Spell-check: could not read {file.Filename}: {readEx.Message}");
						return;
					}

					var doc = DomTraverser.Parse(bytes);
					usage?.CountPage(doc, url);
					string branchLanguage = resolveLanguage(file.Filename, doc);

					// Resolve the language SET: a "Languages by page" path match (longest wins) yields the
					// declared set, of which only the languages with a loaded bundle are used (the missing
					// ones were reported when the dictionaries loaded). A declared set that leaves nothing
					// — no language, or none installed — means the page is not spell-checked. Without a
					// declaration the single branch language is used, and a page whose branch language has
					// no bundle is skipped.
					IReadOnlyList<string> languages;
					var declared = CrawlerSuite.Core.Html.PageLanguageSet.OverrideFor(url, engineConfig.PageLanguageOverrides);
					if (declared is not null)
					{
						languages = declared.Select(Key).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
						if (languages.Count == 0)
						{
							Interlocked.Increment(ref notChecked);
							return;
						}
					}
					else
					{
						var keys = PageLanguageResolver.Resolve(url, branchLanguage, null).Select(Key).ToList();
						languages = keys.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
						if (languages.Count == 0 || keys.Any(k => k is null))
						{
							Interlocked.Increment(ref noBundle);
							return; // no bundle for the branch language
						}
					}

					var (matcher, isReferencePage) = resolver.Resolve(url);

					// Per-element lang: when NO override governs this URL, each run is checked against the
					// dictionary for the language declared closest to it (nearest <… lang> ancestor, the
					// page language as floor) — so a <div lang="ar"> island is checked as Arabic, not as
					// the page's English. An override stays ground truth for the whole page. An island whose
					// declared language has no loaded bundle falls back to the page set (its words surface as
					// normal findings, signalling the operator to load that dictionary). No run is ever checked
					// against a broader set than the page's; dictionary-backed islands get a narrower, correct one.
					bool overrideActive = declared is not null;
					IReadOnlyList<string> RunLanguagesFor(TextRun run)
					{
						if (overrideActive)
						{
							return languages;
						}

						string island = CrawlerSuite.Core.Html.Language.NearestElementTag(run.LanguageNode ?? run.Node, branchLanguage);
						return Key(island) is { } islandKey ? new[] { islandKey } : languages;
					}

					// Primary bundle follows the language argument inside the checker, so any of the set's
					// languages is checkable from this one instance (see ToolsSpellChecker.Check). A fresh
					// checker per page keeps all per-page state thread-local.
					var acronymCollector = acronyms is not null ? new AcronymCollector() : null;
					perFileAcronyms[i] = (url, acronymCollector);
					var knownUse = knownParts is not null ? new KnownPartCounter() : null;
					perFileKnownParts[i] = knownUse;
					var checker = new ToolsSpellChecker(
						bundleMap[languages[0]],
						bundleMap,
						prefixesToStrip,
						fugenelemente,
						acronyms,
						acronymCollector,
						knownUse,
						properNames);

					// Keep each finding paired with the run it came from: the aggregator below needs only
					// the findings, while
					// slot 27 needs the run's RawText — the whole decoded literal, the TokensToFilter unit —
					// for Script findings. Pairing here avoids carrying the literal through the rest of the
					// pipeline on the finding.
					var scriptSkipScope = scriptSkips?.For(file.Filename);
					var pairs = DomTraverser
						.Traverse(doc, matcher, isReferencePage, skipAttributeNames, engineConfig.SpellCheckJavaScript.Enabled, metaContentNames, globalIgnore, htmlTagsToIgnore)
						.SelectMany(run => RunChecker.Check(run, RunLanguagesFor(run), checker.Check, knownDefects, engineConfig.HeuristicNonProseDataAttributeSuppression, wordCollisions?.WordsForFile(file.Filename), scriptTokensToFilter, scriptFallbackLanguages, anchorSplitTails: anchorSplit?.ForFile(file.Filename), unwantedPatternWords: unwantedPattern?.WordsForFile(file.Filename), adjacentAnchorJoins: adjacentAnchor?.ForFile(file.Filename), scriptSkips: scriptSkipScope)
							.Select(finding => (run, finding)))
						.ToList();

					var findings = pairs.Select(p => p.finding).ToList();

					if (findings.Count == 0)
					{
						return;
					}

					// Slot 27: project the Script-source findings into suppression inputs (only when JS
					// checking is on — there are no Script findings otherwise).
					if (engineConfig.SpellCheckJavaScript.Enabled)
					{
						var scriptInputs = pairs
							.Where(p => p.run.Source == RunSource.Script)
							.Select(p => new ScriptSuppressionInput(url, p.finding.Word, p.run.RawText, p.finding.ExcerptText ?? string.Empty))
							.ToList();
						if (scriptInputs.Count > 0)
						{
							perFileScriptInputs[i] = scriptInputs;
						}
					}

					Interlocked.Increment(ref pagesWithFindings);
					perFileTickets[i] = FindingAggregator.Aggregate(url, findings, ExcerptBuilder.Build);
				}
				catch (Exception ex)
				{
					// One failing page skips that page only; the pass and its other findings survive.
					Interlocked.Increment(ref unreadable);
					Logger.LogWarning($"Spell-check: page skipped — {fileList[i].Filename}: {ex.GetType().Name}: {ex.Message}");
				}
			});

			// Flatten the per-page slots in document order — identical to the sequential walk.
			var allTickets = new List<WordTicket>();
			foreach (var slot in perFileTickets)
			{
				if (slot != null)
				{
					allTickets.AddRange(slot);
				}
			}

			Logger.LogInfo(
				$"New spell engine: {total} file(s) processed, {pagesWithFindings} with findings, "
				+ $"{allTickets.Count} ticket(s). Skipped: {unreadable} unreadable, {noBundle} no-language-bundle, "
				+ $"{notChecked} not spell-checked by Languages by page.");

			WriteViews(uniqueViewPath, sourcesViewPath, locatedViewPath, wordTicketsDiagnosticPath, allTickets,
				ValidIn(dictionaryBundles, Key(engineConfig.DefaultLanguage)));

			if (knownParts is not null)
			{
				foreach (var counter in perFileKnownParts)
				{
					knownParts.Add(counter);
				}
			}

			if (acronyms is not null && !string.IsNullOrEmpty(acronymViewPath))
			{
				var lines = AcronymList.Lines(perFileAcronyms.Where(p => p.Page is not null), acronyms);
				FileFormat.WriteAllLines(acronymViewPath, lines);
				acronymsListed?.Invoke(lines.Count(l => !l.StartsWith('#')));
			}

			// Slot 27: merge the per-file script suppression inputs in document order and write the
			// suggestion log — only when JS checking is on (else nothing is script-sourced and no file
			// is produced).
			if (engineConfig.SpellCheckJavaScript.Enabled)
			{
				var allScriptInputs = new List<ScriptSuppressionInput>();
				foreach (var slot in perFileScriptInputs)
				{
					if (slot != null)
					{
						allScriptInputs.AddRange(slot);
					}
				}

				// Slot 27 A/B signal. The checker surfaces no correction candidates (its "suggestions"
				// field carries the failed language, not near-words), so the real-word probe is done
				// here against the dictionaries the pipeline already loaded: a single-token literal
				// resembles a real word when its CAPITALIZED form is accepted by some bundle (e.g.
				// "eisberg" → "Eisberg"). Consulted only for single-token literals, on this one thread.
				// The probe asks every dictionary, so it loads every one: with deferSuggestions the log is
				// written when the caller says (after the script scans, which load them anyway), not here.
				IReadOnlyList<Bundle>? all = null;
				Func<string, bool> resemblesRealWord = word =>
					!string.IsNullOrEmpty(word)
					&& Bundle.CheckAny(char.ToUpperInvariant(word[0]) + word.Substring(1), all ??= dictionaryBundles.Values.ToList());

				void Write() => WriteView(suppressionSuggestionsPath, SuppressionSuggestionAnalyzer.Compose(allScriptInputs, resemblesRealWord));
				if (deferSuggestions is null)
				{
					Write();
				}
				else
				{
					deferSuggestions(Write);
				}
			}

			return allTickets;
		}

		/// <summary>
		/// Which reference dictionaries know a flagged word — information for the Spell findings
		/// page, the word stays flagged. The references are every English flavour and the default
		/// language's dictionary, less the ones the word was flagged in: on an English page the other
		/// flavours and the default language, on a default-language page English, on any other page
		/// both. A word with a capital only at the start is also asked in lowercase ("Meeting" →
		/// "meeting"): German capitalises every noun, a borrowed one too.
		/// </summary>
		internal static Func<string, string, IReadOnlyList<string>> ValidIn(IReadOnlyDictionary<string, Bundle> bundles, string? defaultKey)
		{
			var references = bundles.Keys
				.Where(k => LanguageTags.BaseOf(k) == "en")
				.ToList();
			if (defaultKey is not null && !references.Contains(defaultKey, StringComparer.OrdinalIgnoreCase))
			{
				references.Add(defaultKey);
			}

			return (word, languages) =>
			{
				var flagged = languages.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
				var lower = word.Length > 1 && char.IsUpper(word[0]) && word.Skip(1).Any(char.IsLower) && !word.Skip(1).Any(char.IsUpper)
					? char.ToLowerInvariant(word[0]) + word[1..]
					: null;
				return references
					.Where(key => !flagged.Contains(key, StringComparer.OrdinalIgnoreCase)
						&& bundles.TryGetValue(key, out var bundle)
						&& (bundle.KnowsInSystem(word) || (lower is not null && bundle.KnowsInSystem(lower))))
					.ToList();
			};
		}

		private static void WriteViews(
			string uniqueViewPath,
			string sourcesViewPath,
			string locatedViewPath,
			string wordTicketsDiagnosticPath,
			IReadOnlyList<WordTicket> tickets,
			Func<string, string, IReadOnlyList<string>>? validIn = null)
		{
			// Written directly to the numbered run-folder logs (11/12/13/14), alongside the other
			// numbered logs.
			// 14 is the verbatim word-tickets diagnostic: what the engine flagged and where,
			// written every harvest run regardless of triage.
			WriteView(uniqueViewPath, ParallelStoreWriter.UniqueView(tickets, validIn));
			WriteView(sourcesViewPath, ParallelStoreWriter.SourcesView(tickets));
			WriteView(locatedViewPath, ParallelStoreWriter.LocatedView(tickets));
			WriteView(wordTicketsDiagnosticPath, WordTicketsDiagnostic.Compose(tickets));
		}

		private static void WriteView(string path, string content)
		{
			// Split the pre-composed view into lines and write via the shared lock-aware helper —
			// same write path, encoding (UTF-8 BOM) and line endings (CRLF via FileFormat.NewLine)
			// as the other numbered logs, and the codebase's
			// standard retry-on-lock (so a held handle doesn't fail the write or the next sweep).
			string[] lines = content.Length == 0
				? []
				: content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

			string fileName = Path.GetFileName(path);
			bool ok = FileIo.WriteAllLinesWithRetry(path, lines, fileName);
			if (ok)
			{
				Logger.LogInfo($"New spell engine: wrote {lines.Length} line(s) to {path}.");
			}
		}
	}
}
