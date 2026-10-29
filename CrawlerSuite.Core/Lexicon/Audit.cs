namespace CrawlerSuite.Core.Lexicon
{
	using System.Collections.Concurrent;
		using CrawlerSuite.Core.SpellCheck;
	using System.Text;
	using System.Text.RegularExpressions;

	public static class Audit
	{
		public record Analysis(
			HashSet<string> Orphaned,
			HashSet<string> Redundant);

		/// <summary>
		/// Analyses both the user dictionary and the site-specific dictionary independently,
		/// writes a combined orphan log, and returns each analysis separately so the caller
		/// can clean each file with only its own flagged entries.
		/// Words present in both dictionaries are flagged in each analysis independently
		/// and noted in the log so the operator is aware of the duplication.
		/// </summary>
		public static (Analysis User, Analysis Site) AnalyseDictionaries(
			string userDictionaryPath,
			string siteDictionaryPath,
			string folderPath,
			string outputFilePath,
			IReadOnlyList<string>? prefixesToStrip = null,
			IReadOnlyCollection<Bundle>? loadedBundles = null,
			int degreeOfParallelism = 0)
		{
			var userAnalysis = File.Exists(userDictionaryPath)
				? AnalyseDictionary(userDictionaryPath, folderPath, prefixesToStrip, loadedBundles, degreeOfParallelism)
				: new Analysis([], []);

			var siteAnalysis = File.Exists(siteDictionaryPath)
				? AnalyseDictionary(siteDictionaryPath, folderPath, prefixesToStrip, loadedBundles, degreeOfParallelism)
				: new Analysis([], []);

			// Words flagged in both dictionaries — noted in the log for transparency.
			var inBothOrphaned = new HashSet<string>(userAnalysis.Orphaned, StringComparer.Ordinal);
			var inBothRedundant = new HashSet<string>(userAnalysis.Redundant, StringComparer.Ordinal);
			inBothOrphaned.IntersectWith(siteAnalysis.Orphaned);
			inBothRedundant.IntersectWith(siteAnalysis.Redundant);

			WriteAnalysisLog(
				outputFilePath,
				userDictionaryPath,
				userAnalysis,
				siteDictionaryPath,
				siteAnalysis,
				inBothOrphaned,
				inBothRedundant);

			return (userAnalysis, siteAnalysis);
		}

		/// <summary>
		/// Analyses a single dictionary file against the normalized text corpus.
		/// Returns orphaned words (not found in any page) and redundant words
		/// (prefix-stripped remainder passes system dictionary without this entry).
		///
		/// Performance: the file-scan loop runs in parallel
		/// using <paramref name="degreeOfParallelism"/>. The <c>notFound</c> set
		/// is held in a ConcurrentDictionary so concurrent removal is safe;
		/// removal is commutative so order does not affect the final set. The
		/// inner prefix-Contains fallback precomputes <c>prefix + "-"</c> outside
		/// the per-word loop to avoid string concatenation in the hot path.
		/// </summary>
		internal static Analysis AnalyseDictionary(
			string dictionaryPath,
			string folderPath,
			IReadOnlyList<string>? prefixesToStrip,
			IReadOnlyCollection<Bundle>? loadedBundles,
			int degreeOfParallelism = 0)
		{
			var orphaned = new HashSet<string>(StringComparer.Ordinal);
			var redundant = new HashSet<string>(StringComparer.Ordinal);

			var words = File
				.ReadAllLines(dictionaryPath)
				.Select(l => l.Trim())
				.Where(l => l.Length > 0 && !l.Contains('/'))
				.Distinct(StringComparer.Ordinal)
				.ToList();

			// Words prefixed with ! are pinned — excluded from orphan/redundancy analysis.
			// The ! is stripped when loading into Bundle so spell-check is unaffected.
			var pinnedWords = words
				.Where(w => w.StartsWith('!'))
				.Select(w => w.TrimStart('!'))
				.ToHashSet(StringComparer.Ordinal);

			var analysisWords = words
				.Where(w => !w.StartsWith('!'))
				.Select(w => w.TrimStart('!'))
				.ToList();

			if (analysisWords.Count == 0)
			{
				return new Analysis(orphaned, redundant);
			}

			// ConcurrentDictionary<string, byte> — used as a thread-safe set.
			// Value is ignored; only key membership matters. Initial population
			// holds every analysis word; the parallel scan below removes any
			// word found in the corpus.
			var notFound = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
			foreach (var w in analysisWords)
			{
				notFound[w] = 0;
			}

			// Build one regex that matches any whole word from the analysis list directly.
			var alternates = analysisWords.OrderByDescending(w => w.Length).Select(Regex.Escape);
			var combinedPattern = @"\b(?:" + string.Join("|", alternates) + @")\b";
			var combinedRegex = new Regex(combinedPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

			// Precompute "prefix-" once for the fallback Contains scan — avoids
			// allocating $"{prefix}-{word}" inside the per-file hot loop.
			string[] prefixHyphens = prefixesToStrip is { Count: > 0 }
				? prefixesToStrip.Select(p => p + "-").ToArray()
				: [];

			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = degreeOfParallelism <= 0
					? Environment.ProcessorCount
					: degreeOfParallelism
			};

			var files = CrawlerSuite.Core.Host.FolderFiles.Sorted(folderPath, "*.txt", SearchOption.TopDirectoryOnly).ToList();

			Parallel.ForEach(files, parallelOptions, (file, loopState) =>
			{
				// Cheap early-out: another worker may have already cleared the set.
				if (notFound.IsEmpty) { loopState.Stop(); return; }

				var text = File.ReadAllText(file, Encoding.UTF8);

				foreach (Match m in combinedRegex.Each(text))
				{
					notFound.TryRemove(m.Value, out _);
					if (notFound.IsEmpty) { loopState.Stop(); return; }
				}

				// Step 2: only check prefix+word for words still not found.
				// Use direct string search instead of regex to avoid catastrophic
				// backtracking on large alternation patterns.
				if (prefixHyphens.Length > 0 && !notFound.IsEmpty)
				{
					// Snapshot keys at this moment — concurrent removal during enumeration
					// is permitted by ConcurrentDictionary but creating a list keeps the
					// inner loop simple and avoids re-checking words other workers cleared.
					var stillNotFound = notFound.Keys.ToList();
					foreach (var word in stillNotFound)
					{
						// Skip if another worker already removed this word.
						if (!notFound.ContainsKey(word))
						{
							continue;
						}

						foreach (var prefixHyphen in prefixHyphens)
						{
							// Build "prefix-word" via concat (one allocation per check,
							// vs interpolation which also allocates a StringBuilder-like
							// intermediate). The string.Concat fast path for two strings
							// is the lightest option available.
							var prefixedToken = string.Concat(prefixHyphen, word);
							if (text.Contains(prefixedToken, StringComparison.OrdinalIgnoreCase))
							{
								notFound.TryRemove(word, out _);
								break;
							}
						}
						if (notFound.IsEmpty) { loopState.Stop(); return; }
					}
				}
			});

			// Anything still in notFound after the parallel scan is orphaned.
			orphaned.UnionWith(notFound.Keys);

			// Redundancy check: prefix-stripped remainder passes system dictionary.
			// Only run against non-pinned words. This loop is serial — analysisWords
			// is small (hundreds-to-thousands), each iteration is cheap, and
			// Bundle.CheckAny is not thread-safe.
			if (prefixesToStrip is { Count: > 0 } && loadedBundles is { Count: > 0 })
			{
				foreach (var word in analysisWords)
				{
					if (orphaned.Contains(word))
					{
						continue;
					}

					var remainder = WordPrefix.Strip(word, prefixesToStrip);
					if (remainder == null || remainder == word)
					{
						continue;
					}

					if (Bundle.CheckAny(remainder, loadedBundles))
					{
						redundant.Add(word);
					}
				}
			}

			return new Analysis(orphaned, redundant);
		}

		/// <summary>
		/// Cross-off variant of <see cref="AnalyseDictionary"/>: orphans are the
		/// dictionary entries (excluding '!'-pinned) that never appear in
		/// <paramref name="usedWords"/> — the set of user/site words actually consulted
		/// during spell-check (collected by <see cref="UsageTracker"/>).
		/// No file or corpus scan: a word is "in use" iff the spell engine looked it up
		/// on some page, so this is immune to the markup/entity/tag-split false-orphan
		/// problems a raw-HTML scan would have.
		///
		/// Redundancy is computed exactly as in <see cref="AnalyseDictionary"/>
		/// (prefix-stripped remainder accepted by the system dictionary).
		/// <paramref name="usedWords"/> is matched case-insensitively against the
		/// dictionary entries.
		/// </summary>
		public static Analysis AnalyseFromUsage(
			string dictionaryPath,
			IReadOnlyCollection<string> usedWords,
			IReadOnlyList<string>? prefixesToStrip,
			IReadOnlyCollection<Bundle>? loadedBundles,
			CrawlerSuite.Core.SpellCheck.AcronymRule? acronyms = null)
		{
			var orphaned = new HashSet<string>(StringComparer.Ordinal);
			var redundant = new HashSet<string>(StringComparer.Ordinal);

			if (!File.Exists(dictionaryPath))
			{
				return new Analysis(orphaned, redundant);
			}

			var words = File
				.ReadAllLines(dictionaryPath)
				.Select(l => l.Trim())
				.Where(l => l.Length > 0 && !l.Contains('/'))
				.Distinct(StringComparer.Ordinal)
				.ToList();

			// Words prefixed with ! are pinned — exempt from orphan/redundancy analysis.
			var analysisWords = words
				.Where(w => !w.StartsWith('!'))
				.Select(w => w.TrimStart('!'))
				.ToList();

			if (analysisWords.Count == 0)
			{
				return new Analysis(orphaned, redundant);
			}

			// Orphan = never consulted during spell-check. Case-insensitive membership
			// so a token hit crosses off the entry.
			var used = new HashSet<string>(usedWords, StringComparer.OrdinalIgnoreCase);

			foreach (var word in analysisWords)
			{
				// A word made only of Known word parts is accepted before any dictionary is asked,
				// so its entry is never consulted: it is redundant (covered by those parts), not an
				// orphan — the word may well be on the pages.
				if (prefixesToStrip is { Count: > 0 } && WordPrefix.Strip(word, prefixesToStrip) is null)
				{
					redundant.Add(word);
					continue;
				}

				if (used.Contains(word))
				{
					continue;
				}

				// Never asked during the run, and shaped like an acronym the rule leaves unchecked:
				// covered by that rule, not unused. Only when never asked — the entry also accepts
				// other spellings ("abc" for "ABC", since the lists ignore case), which are checked
				// like any word, so an entry a run did ask for is still needed.
				if (acronyms is not null && acronyms.Matches(word))
				{
					redundant.Add(word);
					continue;
				}

				orphaned.Add(word);
			}

			// Redundancy check: prefix-stripped remainder passes the system dictionary.
			// Identical to AnalyseDictionary; only non-orphan words are considered.
			if (prefixesToStrip is { Count: > 0 } && loadedBundles is { Count: > 0 })
			{
				foreach (var word in analysisWords)
				{
					if (orphaned.Contains(word))
					{
						continue;
					}

					var remainder = WordPrefix.Strip(word, prefixesToStrip);
					if (remainder == null || remainder == word)
					{
						continue;
					}

					if (Bundle.CheckAny(remainder, loadedBundles))
					{
						redundant.Add(word);
					}
				}
			}

			return new Analysis(orphaned, redundant);
		}

		private static void WriteAnalysisLog(
			string outputFilePath,
			string userPath,
			Analysis user,
			string sitePath,
			Analysis site,
			HashSet<string> inBothOrphaned,
			HashSet<string> inBothRedundant)
		{
			using var w = new StreamWriter(outputFilePath, false, Encoding.UTF8) { NewLine = FileFormat.NewLine };

			bool anything = false;

			void Section(string header, IEnumerable<string> words)
			{
				var list = words.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
				if (list.Count == 0)
				{
					return;
				}

				if (anything)
				{
					w.WriteLine();
				}

				w.WriteLine($"// {header}");
				foreach (var word in list)
				{
					w.WriteLine(word);
				}

				anything = true;
			}

			Section($"User dictionary ({Path.GetFileName(userPath)}) — not found in any page:",
				user.Orphaned.Except(inBothOrphaned));

			Section($"User dictionary ({Path.GetFileName(userPath)}) — redundant (covered by Known word parts or the acronym rule):",
				user.Redundant.Except(inBothRedundant));

			Section($"Site dictionary ({Path.GetFileName(sitePath)}) — not found in any page:",
				site.Orphaned.Except(inBothOrphaned));

			Section($"Site dictionary ({Path.GetFileName(sitePath)}) — redundant (covered by Known word parts or the acronym rule):",
				site.Redundant.Except(inBothRedundant));

			// Entries present in both dictionaries — flagged once for clarity.
			Section("In BOTH dictionaries — not found in any page (will be removed from both):",
				inBothOrphaned);

			Section("In BOTH dictionaries — redundant (will be removed from both):",
				inBothRedundant);
		}

		/// <summary>
		/// Public entry point to write the combined orphan/redundancy report (log 15)
		/// from pre-computed analyses — used by the cross-off maintenance path, which
		/// derives orphans from spell-check usage rather than a file scan. Computes the
		/// "in both dictionaries" intersections and delegates to the shared log writer.
		/// </summary>
		public static void WriteAnalysisReport(
			string outputFilePath,
			string userPath,
			Analysis user,
			string sitePath,
			Analysis site)
		{
			var inBothOrphaned = new HashSet<string>(user.Orphaned, StringComparer.Ordinal);
			var inBothRedundant = new HashSet<string>(user.Redundant, StringComparer.Ordinal);
			inBothOrphaned.IntersectWith(site.Orphaned);
			inBothRedundant.IntersectWith(site.Redundant);

			WriteAnalysisLog(
				outputFilePath,
				userPath,
				user,
				sitePath,
				site,
				inBothOrphaned,
				inBothRedundant);
		}

		/// <summary>
		/// Sorts a dictionary file alphabetically (case-insensitive, ! pin marker
		/// stripped for sort key but preserved in output). Comments and Hunspell flag
		/// lines are written first, words follow in sorted order.
		/// Only rewrites the file if the order actually changed — no unnecessary writes.
		/// </summary>
		public static void SortDictionary(string dictionaryPath, string cultureName = "en-US")
		{
			if (!File.Exists(dictionaryPath))
			{
				return;
			}

			var lines = File.ReadAllLines(dictionaryPath, Encoding.UTF8);
			List<string> comments = [];
			List<string> words = [];

			foreach (var line in lines)
			{
				var trimmed = line.Trim();
				if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//") || trimmed.Contains('/'))
				{
					comments.Add(line);
				}
				else
				{
					words.Add(trimmed);
				}
			}

			// Use current culture for sort so umlauts and accented characters sort
			// correctly for the machine's locale. Falls back gracefully for any language.
			var cultureComparer = StringComparer.Create(
				new System.Globalization.CultureInfo(cultureName), ignoreCase: true);

			var sorted = words
				.OrderBy(w => w.TrimStart('!'), cultureComparer)
				.ToList();

			// Only rewrite if order actually changed under the culture comparer.
			if (sorted.SequenceEqual(words, StringComparer.Create(
				new System.Globalization.CultureInfo(cultureName), ignoreCase: false)))
			{
				return;
			}

			BackupDictionary(dictionaryPath);
			var output = comments.Concat(sorted);
			File.WriteAllLines(dictionaryPath, output, Encoding.UTF8);
			Logger.LogInfo($"Dictionary sorted: {Path.GetFileName(dictionaryPath)}");
		}
		/// <summary>
		/// Copies a dictionary file to a numbered backup before it is changed.
		/// Backs up to filename.dic.1, .dic.2, etc., always incrementing.
		/// </summary>
		public static void BackupDictionary(string dictionaryPath)
		{
			if (!File.Exists(dictionaryPath))
			{
				return;
			}

			int number = 1;
			string backupPath;
			do
			{
				backupPath = $"{dictionaryPath}.{number}";
				number++;
			}
			while (File.Exists(backupPath));

			File.Copy(dictionaryPath, backupPath);
			Logger.LogInfo($"Dictionary backup created: {backupPath}");
		}

		/// <summary>
		/// Removes flagged entries from a dictionary file in-place, preserving all other
		/// lines including blank lines, comments, and Hunspell flag lines exactly as-is.
		/// Creates a numbered backup before writing.
		/// </summary>
		public static void CleanDictionary(string dictionaryPath, Dictionary<string, string> toRemove)
		{
			if (!File.Exists(dictionaryPath))
			{
				Logger.LogError($"CleanDictionary: file not found: {dictionaryPath}");
				return;
			}

			if (toRemove.Count == 0)
			{
				Logger.LogInfo($"CleanDictionary: nothing to remove from {dictionaryPath}");
				return;
			}

			BackupDictionary(dictionaryPath);

			var lines = File.ReadAllLines(dictionaryPath, Encoding.UTF8);
			List<string> kept = [];
			var seen = new HashSet<string>(StringComparer.Ordinal); // case-sensitive: Floe-Tide and floe-tide are distinct entries
			int removed = 0;

			// Separate comments/flags from word entries for sorting.
			List<string> comments = [];
			List<string> words = [];

			foreach (var line in lines)
			{
				var trimmed = line.Trim();
				var normalized = trimmed.TrimStart('!'); // strip pin marker for comparison

				// Preserve blank lines, comments, and Hunspell flag lines.
				if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//") || trimmed.Contains('/'))
				{
					comments.Add(line);
					continue;
				}

				if (toRemove.TryGetValue(normalized, out var reason))
				{
					removed++;
					Logger.LogInfo($"CleanDictionary: removed '{trimmed}' from {Path.GetFileName(dictionaryPath)} — {reason}");
					continue;
				}

				if (!seen.Add(normalized))
				{
					removed++;
					Logger.LogInfo($"CleanDictionary: removed '{trimmed}' from {Path.GetFileName(dictionaryPath)} — exact duplicate");
					continue;
				}

				words.Add(trimmed); // preserve original line including ! if present
			}

			// Write in original order — SortDictionary runs after CleanDictionary
			// and handles sorting with the correct culture comparer.
			kept.AddRange(comments);
			kept.AddRange(words);

			File.WriteAllLines(dictionaryPath, kept, Encoding.UTF8);
			Logger.LogInfo($"CleanDictionary: {removed} entries removed from {dictionaryPath}");
		}
		/// <summary>
		/// Prepends the '!' pin marker to specified words in a dictionary file,
		/// exempting them from future orphan and redundancy analysis.
		/// Words already pinned are left untouched. Creates a numbered backup before writing.
		/// </summary>
		public static void PinWords(string dictionaryPath, IReadOnlyCollection<string> wordsToPIN)
		{
			if (!File.Exists(dictionaryPath))
			{
				Logger.LogError($"PinWords: file not found: {dictionaryPath}");
				return;
			}

			if (wordsToPIN.Count == 0)
			{
				Logger.LogInfo($"PinWords: nothing to pin in {dictionaryPath}");
				return;
			}

			var pinSet = new HashSet<string>(wordsToPIN, StringComparer.Ordinal);
			var lines = File.ReadAllLines(dictionaryPath, Encoding.UTF8);
			List<string> result = [];
			int pinned = 0;

			foreach (var line in lines)
			{
				var trimmed = line.Trim();

				// Leave comments, blank lines, and Hunspell flag lines untouched.
				if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//") || trimmed.Contains('/'))
				{
					result.Add(line);
					continue;
				}

				// Already pinned — leave as-is.
				if (trimmed.StartsWith('!'))
				{
					result.Add(line);
					continue;
				}

				if (pinSet.Contains(trimmed))
				{
					result.Add('!' + trimmed);
					pinned++;
					Logger.LogInfo($"PinWords: pinned '{trimmed}' in {Path.GetFileName(dictionaryPath)}");
				}
				else
				{
					result.Add(line);
				}
			}

			if (pinned == 0)
			{
				Logger.LogInfo($"PinWords: no matching words found to pin in {dictionaryPath}");
				return;
			}

			BackupDictionary(dictionaryPath);
			File.WriteAllLines(dictionaryPath, result, Encoding.UTF8);
			Logger.LogInfo($"PinWords: {pinned} word(s) pinned in {dictionaryPath}");
		}

		/// <summary>
		/// Counts the spell-check entries in a dictionary file: non-blank lines, minus a
		/// leading Hunspell-style bare integer count line if present. Returns 0 when the
		/// file is missing or unreadable.
		/// </summary>
		internal static int CountDictionaryWords(string dictionaryPath)
		{
			try
			{
				if (string.IsNullOrEmpty(dictionaryPath) || !File.Exists(dictionaryPath))
				{
					return 0;
				}

				var lines = File.ReadAllLines(dictionaryPath)
					.Where(l => !string.IsNullOrWhiteSpace(l))
					.ToList();

				// Hunspell .dic files open with a bare entry count on line 1 — exclude it.
				if (lines.Count > 0 && int.TryParse(lines[0].Trim(), out _))
				{
					lines.RemoveAt(0);
				}

				return lines.Count;
			}
			catch
			{
				return 0;
			}
		}
	}
}
