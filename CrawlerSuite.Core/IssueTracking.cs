using System.Text;
using CrawlerSuite.Core.Urls;
using CrawlerSuite.Core.Security;

namespace CrawlerSuite.Core
{
	// ── IssueTracking ─────────────────────────────────────────────────────────
	//
	// Maintains a persistent IssueTracking.log at site level (no timestamp folder).
	// Every run: promoted issues from the five source logs are merged with the
	// existing log, statuses are updated, and the file is rewritten.
	//
	// Column order (Source first — easy to filter in Excel/Power Query):
	//   Source|Ticket|DateReported|Type|Url|Status|DateFound|DateLastSeen|DateExpiry|
	//   Word|Comment|Language|SourceLabel|Excerpt|CrawlSource
	//
	// Issue types:  REDIRECT | 404 | SELFLINK | QUALITY | SPELLING | SEO
	// Status values: new | pending | overdue | fixed | reopened | wontfix | config
	//
	// Identity key (prevents duplicate rows):
	//   SPELLING / QUALITY : Type + Url + Word
	//   404 / SELFLINK / REDIRECT : Type + Url
	// ─────────────────────────────────────────────────────────────────────────

	public static class IssueTracking
	{
		// ── Record ────────────────────────────────────────────────────────────

		public record IssueRecord
		{
			/// <summary>
			/// How this record was created. First column — easy to filter in Excel/Power Query.
			/// "auto"   = promoted at end of run by IssueTracking.Merge (no human review)
			/// "triage" = a human decided in triage (deliberate decision)
			/// "manual" = row edited directly in IssueTracking.log
			/// </summary>
			public string Source { get; init; } = "auto";
			public string Ticket { get; init; } = string.Empty;
			public string DateReported { get; init; } = string.Empty;
			public string Type { get; init; } = string.Empty;
			public string Url { get; init; } = string.Empty;
			public string Status { get; set; } = "new";
			public string DateFound { get; init; } = Today();
			public string DateLastSeen { get; set; } = Today();
			public string DateExpiry { get; init; } = string.Empty;
			public string Word { get; init; } = string.Empty;
			public string Comment { get; init; } = string.Empty;
			public string Language { get; init; } = string.Empty;
			public string SourceLabel { get; init; } = string.Empty;
			public string Excerpt { get; init; } = string.Empty;
			/// <summary>
			/// Crawl source of the page: "discovery" (found via link following) or
			/// "list" (post-crawl pass from CMS content list). Empty for issue types
			/// where a file mapping is not available (e.g. REDIRECT). The last column,
			/// so a log without this column loads cleanly with an empty value.
			/// </summary>
			public string CrawlSource { get; init; } = string.Empty;

			/// <summary>
			/// Identity key — used to match an existing record against a newly
			/// detected issue. Word distinguishes SPELLING/QUALITY sub-issues.
			/// </summary>
			public string Key => string.IsNullOrEmpty(Word)
				? $"{Type}|{Url}"
				: $"{Type}|{Url}|{Word}";

			public static IssueRecord Parse(string line)
			{
				var p = line.Split('|');
				// Written through ComposeLine, so a cell may carry the formula guard.
				string F(int i) => i < p.Length ? CsvInjectionGuard.Denormalize(p[i].Trim()) : string.Empty;
				return new IssueRecord
				{
					Source = F(0),
					Ticket = F(1),
					DateReported = F(2),
					Type = F(3),
					Url = F(4),
					Status = F(5),
					DateFound = F(6),
					DateLastSeen = F(7),
					DateExpiry = F(8),
					Word = F(9),
					Comment = F(10),
					Language = F(11),
					SourceLabel = F(12),
					Excerpt = F(13),
					CrawlSource = F(14),
				};
			}

			public string Serialize() =>
				IssueLogWriter.ComposeLine(IssueLogWriter.PipeDelimiter,
					Source, Ticket, DateReported, Type, Url, Status, DateFound,
					DateLastSeen, DateExpiry, Word, Comment, Language, SourceLabel,
					Excerpt, CrawlSource);
		}

		private static readonly string?[] HeaderFields =
		[
			"Source", "Ticket", "DateReported", "Type", "Url", "Status",
			"DateFound", "DateLastSeen", "DateExpiry", "Word", "Comment",
			"Language", "SourceLabel", "Excerpt", "CrawlSource"
		];

		private static string Today() =>
			DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

		// ── Load / Save ───────────────────────────────────────────────────────

		/// <summary>
		/// Loads IssueTracking.log. Returns empty list if file does not exist.
		/// Skips the header line and blank lines. Also drops any record whose Url is
		/// the unresolved-lookup sentinel "error" (see Cache.UrlFor), since it
		/// can't be tied to a real page.
		/// </summary>
		public static List<IssueRecord> Load(string filePath)
		{
			if (!File.Exists(filePath))
			{
				return [];
			}

			return File.ReadAllLines(filePath, Encoding.UTF8)
				.Where(l => l.Length > 0
					&& !l.StartsWith("Source|", StringComparison.OrdinalIgnoreCase)
					&& l.Contains('|'))
				.Select(IssueRecord.Parse)
				.Where(r => !string.IsNullOrEmpty(r.Type)
					&& !string.IsNullOrEmpty(r.Url)
					&& !string.Equals(r.Url, "error", StringComparison.Ordinal))
				.ToList();
		}

		/// <summary>
		/// Saves the issue list to IssueTracking.log with header.
		/// Sorts by Type then Url for stable output.
		/// [KEEP] Routed through IssueLogWriter — each field is sanitized so
		/// crawled content (Word, SourceLabel, Excerpt, Comment) can never
		/// inject newlines or delimiters into the persistent tracking log.
		/// </summary>
		public static void Save(string filePath, List<IssueRecord> records)
		{
			var rows = new List<string?[]> { HeaderFields };
			rows.AddRange(records
				.OrderBy(r => r.Type, StringComparer.Ordinal)
				.ThenBy(r => r.Url, StringComparer.Ordinal)
				.ThenBy(r => r.Word, StringComparer.Ordinal)
				.Select(r => new string?[]
				{
					r.Source, r.Ticket, r.DateReported, r.Type, r.Url, r.Status,
					r.DateFound, r.DateLastSeen, r.DateExpiry, r.Word, r.Comment,
					r.Language, r.SourceLabel, r.Excerpt, r.CrawlSource
				}));
			IssueLogWriter.Write(filePath, IssueLogWriter.PipeDelimiter, rows);
		}

		// ── Merge ─────────────────────────────────────────────────────────────

		/// <summary>
		/// Merges newly detected issues into the existing issue list, gone-is-gone: a
		/// still-detected record is kept as it is, a newly detected issue is added with
		/// status "new", and a record no longer detected is dropped, whatever its status.
		/// </summary>
		public static List<IssueRecord> Merge(
			List<IssueRecord> existing,
			List<IssueRecord> detected)
		{
			// Gone-is-gone: the ledger holds ONLY issues present in the current
			// detected set. A still-detected existing record is kept VERBATIM —
			// preserving its disposition (Status/Ticket/Comment), the Excerpt frozen
			// at promotion, and DateFound. A newly-detected issue is added. An
			// existing record NOT in the detected set is DROPPED outright — no
			// "fixed"/"reopened" status, no retention. If it reappears in a later run
			// it re-enters as new and is re-triaged. This holds for EVERY disposition,
			// including wontfix/config: once the finding is gone the row goes with it
			// (a disposition is cross-run state only while the finding is still present).
			var detectedMap = detected
				.Where(r => !string.IsNullOrEmpty(r.Type) && !string.IsNullOrEmpty(r.Url))
				.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
			var existingMap = existing
				.Where(r => !string.IsNullOrEmpty(r.Type) && !string.IsNullOrEmpty(r.Url))
				.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

			List<IssueRecord> result = [];

			// Keep existing records that are still detected (verbatim); drop the rest.
			foreach (var record in existing)
			{
				if (detectedMap.ContainsKey(record.Key))
				{
					result.Add(record);
				}
			}

			// Add newly-detected issues not already present.
			foreach (var issue in detected)
			{
				if (!existingMap.ContainsKey(issue.Key))
				{
					result.Add(issue);
				}
			}

			return result;
		}

		/// <summary>
		/// Gone-is-gone Merge that EXEMPTS one record type. Existing rows of
		/// <paramref name="exemptType"/> are passed through verbatim — never dropped,
		/// never reconciled against <paramref name="detected"/> — while every other
		/// row follows the normal <see cref="Merge"/> rules against the detected set.
		/// Used for SPELLING: those rows are operator decisions written and reconciled
		/// by spell triage against the in-memory tickets, and they are deliberately
		/// absent from the end-of-run detected set, so a plain Merge would drop them all.
		/// </summary>
		public static List<IssueRecord> MergeExempt(
			List<IssueRecord> existing,
			List<IssueRecord> detected,
			string exemptType)
		{
			var exempt = existing
				.Where(r => r.Type.Equals(exemptType, StringComparison.OrdinalIgnoreCase))
				.ToList();
			var rest = existing
				.Where(r => !r.Type.Equals(exemptType, StringComparison.OrdinalIgnoreCase))
				.ToList();

			var merged = Merge(rest, detected);
			merged.AddRange(exempt);
			return merged;
		}

		/// <summary>
		/// Removes <c>fixed</c> records past their retention window. Other
		/// statuses are never purged — wontfix/config encode deliberate
		/// operator decisions ("do not flag again") and must persist; new/
		/// pending/overdue/reopened are still-actionable.
		///
		/// Retention semantics (<paramref name="retentionDays"/>):
		///   0   → disabled, keep all fixed forever (default).
		///   &gt;0 → purge fixed whose DateLastSeen is older than N days. A
		///          fixed record's DateLastSeen is frozen at the last run the
		///          issue was actually detected (Merge stops updating it once
		///          the issue disappears), so it approximates "fixed since".
		///   -1  → purge ALL fixed unconditionally (dev/reset). Any negative
		///          value is treated this way.
		///
		/// A fixed record with an unparseable/empty DateLastSeen is kept under
		/// a positive window (cannot age it) but purged under -1 (purge-all).
		/// Pure: no I/O, returns a new list.
		/// </summary>
		public static List<IssueRecord> PurgeExpiredFixed(
			List<IssueRecord> records, int retentionDays)
		{
			if (retentionDays == 0)
			{
				return records;   // disabled — keep all
			}

			bool purgeAll = retentionDays < 0;
			var cutoff = purgeAll
				? default
				: DateTime.Now.Date.AddDays(-retentionDays);

			var kept = new List<IssueRecord>(records.Count);
			foreach (var r in records)
			{
				bool isFixed = r.Status.Equals("fixed", StringComparison.OrdinalIgnoreCase);
				if (!isFixed)
				{
					kept.Add(r);
					continue;
				}

				if (purgeAll)
				{
					continue;   // -1 → drop every fixed
				}

				// Positive window: drop only if DateLastSeen is parseable AND
				// older than the cutoff. Unparseable/empty → keep (can't age).
				//
				// Parse with the exact format Today() writes ("yyyy-MM-dd") under
				// the invariant culture, rather than a loose TryParse. This makes
				// the producer/consumer date contract explicit and immune to a
				// future locale surprise — anything not in the canonical written
				// form falls into the same safe "unparseable → kept" bucket.
				if (DateTime.TryParseExact(
						r.DateLastSeen,
						"yyyy-MM-dd",
						System.Globalization.CultureInfo.InvariantCulture,
						System.Globalization.DateTimeStyles.None,
						out var seen)
					&& seen.Date < cutoff)
				{
					continue;   // expired — drop
				}

				kept.Add(r);
			}

			return kept;
		}

		/// <summary>
		/// Applies operator triage decisions to an existing issue list,
		/// touching ONLY the records the operator decided on. All other
		/// existing records are passed through unchanged.
		///
		/// This is the correct semantics for triage Save. Merge(existing,
		/// partialDecisions) would be wrong here: it would treat every
		/// non-triaged existing record as gone, because it is not in the
		/// partial detection set.
		///
		/// Rules:
		///   - decision with Key matching existing record → overwrite
		///     Status, Comment, DateLastSeen, DateReported; preserve DateFound,
		///     DateExpiry, Source. Other fields take the decision's value.
		///   - decision with no matching Key → add as a new record
		///   - existing records with no matching decision → passed through
		///     completely untouched
		/// </summary>
		public static List<IssueRecord> ApplyTriageDecisions(
			List<IssueRecord> existing,
			List<IssueRecord> decisions)
		{
			var today = Today();
			var decisionsByKey = decisions
				.Where(d => !string.IsNullOrEmpty(d.Type) && !string.IsNullOrEmpty(d.Url))
				.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

			List<IssueRecord> result = [];

			// Walk existing records. Overwrite when a triage decision targets
			// the same Key; otherwise pass through unchanged.
			var keysUpdated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var record in existing)
			{
				if (decisionsByKey.TryGetValue(record.Key, out var decision))
				{
					keysUpdated.Add(record.Key);
					result.Add(record with
					{
						Status = decision.Status,
						Comment = decision.Comment,
						DateLastSeen = today,
						DateReported = string.IsNullOrEmpty(record.DateReported)
							? today
							: record.DateReported,
						// Triage often supplies a richer Excerpt and SourceLabel
						// from the current run — prefer the decision's value
						// when present, fall back to existing.
						Excerpt = string.IsNullOrEmpty(decision.Excerpt)
							? record.Excerpt
							: decision.Excerpt,
						SourceLabel = string.IsNullOrEmpty(decision.SourceLabel)
							? record.SourceLabel
							: decision.SourceLabel,
					});
				}
				else
				{
					// Untouched — explicitly NOT marked 'fixed' just because
					// it isn't in the triage decisions list.
					result.Add(record);
				}
			}

			// Add new triage decisions that didn't match an existing record.
			foreach (var decision in decisions)
			{
				if (string.IsNullOrEmpty(decision.Type) || string.IsNullOrEmpty(decision.Url))
				{
					continue;
				}

				if (!keysUpdated.Contains(decision.Key))
				{
					result.Add(decision);
				}
			}

			return result;
		}

		// ── Parsers ───────────────────────────────────────────────────────────

		/// <summary>
		/// Parses the 07-404-sources dual-locale CSV pair (the _semicolon.csv is the
		/// canonical machine-read side; quote-aware via ParseCsvLine).
		/// Columns: BrokenUrl, Kind, Referrer, AsWritten, Grade, Evidence (header row skipped).
		/// Url = BrokenUrl, SourceLabel = Referrer; a reference that is only a canonical is the
		/// canonical's finding and left out.
		/// </summary>
		public static List<IssueRecord> PromoteFrom404(string csvBasePath)
		{
			var semicolonPath = csvBasePath + IssueLogWriter.CsvSemicolonSuffix;
			if (!File.Exists(semicolonPath))
			{
				return [];
			}

			return File.ReadAllLines(semicolonPath, Encoding.UTF8)
				.Where(l => l.Length > 0)
				.Select(l => IssueLogWriter.ParseCsvLine(l, ';'))
				.Where(p => p.Length > 0
					&& !string.Equals(p[0], "BrokenUrl", StringComparison.OrdinalIgnoreCase)   // header row
					&& !(p.Length > 4 && p[4].Trim() == Links.BrokenLinkOrigins.CanonicalOnly))
				.Select(p =>
				{
					string F(int i) => i < p.Length ? p[i].Trim() : string.Empty;
					return new IssueRecord
					{
						Type = "404",
						Url = F(0),
						SourceLabel = F(2),
					};
				})
				.Where(r => !string.IsNullOrEmpty(r.Url))
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Parses the 09-self-link-analysis dual-locale CSV pair (the _semicolon.csv
		/// is the canonical machine-read side; quote-aware via ParseCsvLine).
		/// Columns: File, FileUrl, LinkFound, ContextSnippet (header row skipped).
		/// Url = FileUrl, Excerpt = ContextSnippet.
		/// Every row is an issue — a page linking to itself.
		/// </summary>
		public static List<IssueRecord> PromoteFromSelfLink(string csvBasePath)
		{
			var semicolonPath = csvBasePath + IssueLogWriter.CsvSemicolonSuffix;
			if (!File.Exists(semicolonPath))
			{
				return [];
			}

			return File.ReadAllLines(semicolonPath, Encoding.UTF8)
				.Where(l => l.Length > 0)
				.Select(l => IssueLogWriter.ParseCsvLine(l, ';'))
				.Where(p => p.Length > 0
					&& !string.Equals(p[0], "File", StringComparison.OrdinalIgnoreCase)) // header row
				.Select(p =>
				{
					string F(int i) => i < p.Length ? p[i].Trim() : string.Empty;
					var filename = F(0);
					return new IssueRecord
					{
						Type = "SELFLINK",
						Url = F(1),
						// F(3) ContextSnippet is the one read-back content field that can
						// carry a write-side formula-injection neutralizer; strip it so the
						// excerpt round-trips to its original text (see CsvInjectionGuard).
						Excerpt = CsvInjectionGuard.Denormalize(F(3)),
						CrawlSource = CrawlIndex.LookUpSourceForFile(filename),
					};
				})
				.Where(r => !string.IsNullOrEmpty(r.Url))
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Parses 03-redirect-analysis.log.
		/// Format: {url} | {status} | {redirectTarget} > {chain}
		/// Issue rule: line contains "Found" more than once (double redirect).
		/// NotFound at end = out-of-scope external URL, ignored.
		/// Url = first URL in the line, Excerpt = full chain.
		/// </summary>
		public static List<IssueRecord> PromoteFromRedirect(string logPath)
		{
			if (!File.Exists(logPath))
			{
				return [];
			}

			List<IssueRecord> issues = [];

			foreach (var line in File.ReadAllLines(logPath, Encoding.UTF8))
			{
				if (line.Length == 0)
				{
					continue;
				}

				// Count "Found" occurrences excluding "NotFound" — double redirect = issue.
				int foundCount = CountOccurrences(line, "Found", StringComparison.OrdinalIgnoreCase);
				int notFoundCount = CountOccurrences(line, "NotFound", StringComparison.OrdinalIgnoreCase);
				int netFound = foundCount - notFoundCount;
				if (netFound < 2)
				{
					continue;
				}

				// First segment before " | " is the originating URL.
				var firstPipe = line.IndexOf(" | ", StringComparison.Ordinal);
				var url = firstPipe >= 0 ? line[..firstPipe].Trim() : line.Trim();

				issues.Add(new IssueRecord
				{
					Type = "REDIRECT",
					Url = url,
					Excerpt = line.Trim(),
				});
			}

			return issues
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Parses 10-content-quality-issues.log.
		/// Format: Filename|IssueType|Detail|Context (header line skipped)
		/// Type = QUALITY, Url resolved from filename via Cache,
		/// Word = IssueType (LIGATURE, QUOTE_UNMATCHED etc.), Excerpt = Context.
		///
		/// [KEEP] Reader-side defence: lines whose F(0) doesn't look like a
		/// filename are skipped with a warning. Writers route through
		/// IssueLogWriter which sanitises content, but this defence protects
		/// against out-of-tree log files that were not written through it.
		/// </summary>
		public static List<IssueRecord> PromoteFromQuality(string logPath)
		{
			if (!File.Exists(logPath))
			{
				return [];
			}

			return File.ReadAllLines(logPath, Encoding.UTF8)
				.Where(l => l.Length > 0
					&& !l.StartsWith("Filename|", StringComparison.OrdinalIgnoreCase))
				.Select(line =>
				{
					var p = line.Split('|');
					// Log 10 is written through ComposeLine: a cell may carry the formula guard.
					string F(int i) => i < p.Length ? CsvInjectionGuard.Denormalize(p[i].Trim()) : string.Empty;
					var filename = F(0);

					if (!LooksLikeFilename(filename))
					{
						Logger.LogWarning(
							$"PromoteFromQuality: skipping line with non-filename F(0): " +
							$"'{Truncate(filename, 80)}'. " +
							$"This usually indicates a malformed log line (e.g. embedded newline " +
							$"in content); review the writer for the relevant log.");
						return (IssueRecord?)null;
					}

					var url = CrawlIndex.LookUpUrlForFile(filename);
					var issueType = F(1);
					// Reject records where the IssueType field
					// is empty (malformed-line residue). Also reject the
					// pathological combination of a failed URL lookup AND empty
					// IssueType — that shape would persist as an anonymous
					// "auto|||QUALITY|error|..." record.
					// A legitimate quality issue always has a non-empty
					// IssueType (LIGATURE, QUOTE_*, etc.). A non-empty
					// IssueType with a failed URL lookup is preserved as-is
					// (Url falls back to filename per the existing logic).
					if (string.IsNullOrEmpty(issueType))
					{
						return (IssueRecord?)null;
					}

					var detail = F(2);

					// For IssueTypes whose Detail carries a
					// meaningful sub-category, build a composite Word that includes
					// the Detail field (e.g. "UNWANTED_PATTERN:Category: Name —
					// pattern: example", or "MALFORMED_HTML:CONTENT_BEFORE_DOCTYPE").
					// This matches the composite Word shape produced on the triage /
					// detection side, so auto-promoted and triage-promoted records
					// share the same Key for the same underlying issue. Without this,
					// triage decisions create parallel records instead of updating the
					// auto-promoted ones.
					//
					// Composite shape is used where the IssueType has meaningful
					// sub-categories in Detail:
					//   UNWANTED_PATTERN  — Detail is "Category: Name — pattern(s): …"
					//   MALFORMED_HTML    — Detail is the sub-defect (CONTENT_BEFORE_DOCTYPE);
					//                       a server-side defect, auto-promoted only.
					//   CONTROL_CHARS_IN_CONTENT — Detail carries a stable identity payload
					//                       after the ControlChars identity separator: a
					//                       location token plus the full marker-encoded
					//                       element text. The triage path keys on that
					//                       payload (so each affected element on a page gets
					//                       its own Key); this side MUST key on the identical
					//                       payload or the auto-promoted (detected) record and
					//                       the ticketed record get different Keys — the
					//                       ticketed pending row would then fail Merge's
					//                       gone-is-gone match and be dropped, and the finding
					//                       would re-present every run. The split mirrors the
					//                       triage builder exactly; a line with no separator
					//                       falls back to the whole Detail,
					//                       matching the triage fallback.
					// Other QUALITY IssueTypes (LIGATURE, BARE_TEXT_*, QUOTE_*, etc.)
					// use the bare IssueType Word because their triage paths do too,
					// or because composing with Detail would over-discriminate the
					// Key space.
					string word;
					if (issueType == "UNWANTED_PATTERN")
					{
						// A url-tagged occurrence (Detail carries the ExcludeUrl marker) keys
						// on the pattern prose LEFT of the marker as a UNWANTED_PATTERN_URL
						// summary, so every url occurrence on a page collapses to one Key (the
						// trailing DistinctBy(Key) folds them) — matching the triage summary
						// Word byte-for-byte, or the ticketed summary and the detected set get
						// different Keys and Merge drops it. An untagged UNWANTED_PATTERN keeps
						// the composite Word (per-occurrence).
						var marker = Quality.UnwantedPatterns.UrlMarker;
						var markerIndex = detail.IndexOf(marker, StringComparison.Ordinal);
						if (markerIndex >= 0)
						{
							var prose = detail[..markerIndex];
							word = $"UNWANTED_PATTERN_URL:{prose}";
						}
						else
						{
							word = string.IsNullOrEmpty(detail) ? issueType : $"{issueType}:{detail}";
						}
					}
					else if (issueType == "MALFORMED_HTML")
					{
						word = string.IsNullOrEmpty(detail) ? issueType : $"{issueType}:{detail}";
					}
					else if (issueType == "CONTROL_CHARS_IN_CONTENT")
					{
						var sep = Quality.ControlChars.IdentitySeparator;
						var sepIndex = detail.IndexOf(sep, StringComparison.Ordinal);
						var identityPayload = sepIndex >= 0
							? detail[(sepIndex + sep.Length)..]
							: detail;
						word = $"{issueType}:{identityPayload}";
					}
					else
					{
						word = issueType;
					}

					return new IssueRecord
					{
						Type = "QUALITY",
						Url = string.IsNullOrEmpty(url) || url == "error" ? filename : url,
						Word = word,
						SourceLabel = detail,
						Excerpt = F(3),         // Context
						CrawlSource = CrawlIndex.LookUpSourceForFile(filename),
					};
				})
				.Where(r => r != null && !string.IsNullOrEmpty(r!.Url))
				.Cast<IssueRecord>()
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Parses 11-spell-error-sources.log.
		/// Format: {pageUrl}|{filename}|{word (lang)}|{word (lang)}...
		/// One record per word per page.
		/// Type = SPELLING, Url = pageUrl, Word = word, Language = lang code.
		/// </summary>
		public static List<IssueRecord> PromoteFromSpelling(string logPath)
		{
			if (!File.Exists(logPath))
			{
				return [];
			}

			List<IssueRecord> issues = [];

			foreach (var line in File.ReadAllLines(logPath, Encoding.UTF8))
			{
				if (line.Length == 0)
				{
					continue;
				}

				var parts = line.Split('|');
				if (parts.Length < 3)
				{
					continue;
				}

				var pageUrl = parts[0].Trim();

				// parts[1] = filename (optional when showLocalSourceInLog is false)
				// parts[2..] = word entries formatted as "word (lang)" or "word (lang) (meta[...])"
				int wordStart = parts.Length > 2 ? 2 : 1;
				for (int i = wordStart; i < parts.Length; i++)
				{
					var entry = parts[i].Trim();
					if (entry.Length == 0)
					{
						continue;
					}

					// Parse "word (lang)" — word is everything before the first " ("
					var parenIdx = entry.IndexOf(" (", StringComparison.Ordinal);
					var word = parenIdx >= 0 ? entry[..parenIdx].Trim() : entry;
					var lang = string.Empty;

					if (parenIdx >= 0)
					{
						// Extract lang code from first parenthetical "(lang)"
						var closeIdx = entry.IndexOf(')', parenIdx);
						if (closeIdx > parenIdx + 2)
						{
							lang = entry[(parenIdx + 2)..closeIdx].Trim();
						}
					}

					if (string.IsNullOrEmpty(word))
					{
						continue;
					}

					var spellFilename = Cache.FilenameFor(pageUrl);
					issues.Add(new IssueRecord
					{
						Type = "SPELLING",
						Url = pageUrl,
						Word = word,
						Language = lang,
						CrawlSource = spellFilename is not null
							? CrawlIndex.LookUpSourceForFile(spellFilename)
							: string.Empty,
					});
				}
			}

			return issues
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		// ── Helper ────────────────────────────────────────────────────────────

		/// <summary>
		/// Cheap sanity check: does this look like a filename rather than
		/// crawled content text? Used as a reader-side defence in PromoteFrom*
		/// methods — if a log line's F(0) doesn't look like a filename, the
		/// line is most likely a corrupted entry (e.g. embedded newline in
		/// content split one record across multiple lines).
		///
		/// Rejection criteria (any one is enough): contains whitespace,
		/// starts with a dash, longer than 260 chars (Windows MAX_PATH),
		/// contains a control character, or contains zero non-letter / non-
		/// digit characters across its entire length.
		/// </summary>
		internal static bool LooksLikeFilename(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return false;
			}

			if (s.Length > 260)
			{
				return false;
			}

			if (s[0] == '-' || s[0] == ' ')
			{
				return false;
			}

			foreach (var ch in s)
			{
				if (ch == ' ' || ch == '\t')
				{
					return false;
				}

				if (ch < 0x20)
				{
					return false;
				}
			}

			return true;
		}

		private static string Truncate(string s, int max) =>
			s.Length <= max ? s : s[..max] + "…";

		private static int CountOccurrences(string text, string pattern,
			StringComparison comparison)
		{
			int count = 0;
			int idx = 0;
			while ((idx = text.IndexOf(pattern, idx, comparison)) >= 0)
			{
				count++;
				idx += pattern.Length;
			}
			return count;
		}

		/// <summary>
		/// Parses 10-content-quality-issues.log for SPLIT_WORD_ANCHOR entries and
		/// extracts the artifact word (fragment inside the anchor tag) per filename.
		/// These words are spelling artifacts caused by the CMS authoring error —
		/// they should be suppressed from spell-check results.
		///
		/// Extraction: the context column contains "...text>Fragment</a>char..."
		/// The fragment is the text between the last '>' and '</a>'.
		/// </summary>
		public static Dictionary<string, HashSet<string>> BuildSplitAnchorArtifacts(
			string qualityLogPath)
		{
			var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

			if (!File.Exists(qualityLogPath))
			{
				return result;
			}

			var anchorPattern = new System.Text.RegularExpressions.Regex(
				@">([^<>]+)</a>",
				System.Text.RegularExpressions.RegexOptions.Compiled);

			foreach (var line in File.ReadAllLines(qualityLogPath, Encoding.UTF8))
			{
				if (line.Length == 0)
				{
					continue;
				}

				var p = line.Split('|');
				if (p.Length < 2)
				{
					continue;
				}

				var issueType = p.Length > 1 ? p[1].Trim() : string.Empty;
				if (!issueType.Equals("SPLIT_WORD_ANCHOR", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var filename = p[0].Trim();
				var context = p.Length > 3 ? CsvInjectionGuard.Denormalize(p[3].Trim()) : string.Empty;

				var match = anchorPattern.Match(context);
				if (!match.Success)
				{
					continue;
				}

				var artifact = match.Groups[1].Value.Trim();
				if (string.IsNullOrEmpty(artifact))
				{
					continue;
				}

				if (!result.TryGetValue(filename, out var words))
				{
					words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					result[filename] = words;
				}
				words.Add(artifact);
			}

			return result;
		}
	}
}
