namespace CrawlerSuite.Core
{
	using System.Text;

	/// <summary>
	/// Resolves ticket metadata (Location, Package, CmsLink, SpecialInfo) for a
	/// page URL by matching against the configured CmsContentList CSV, and emits
	/// startup diagnostics for that lookup. Self-contained, depends only on
	/// <see cref="Config"/> and shared utilities.
	/// </summary>
	public static class SpellMetadataLookup
	{
		// ── Metadata lookup from CmsContentList ───────────────────────────────────────

		// ── Ticket metadata record ──────────────────────────────────────────
		// Returned by BuildMetadataLookup — carries all CSV-derived and
		// language-derived fields resolved per page URL.
		public record TicketMetadata(
			string Location,
			string Package,
			string CmsLink,
			string SpecialInfo);

		/// <summary>
		/// Builds a lookup function that resolves all ticket metadata for a given page URL
		/// by matching against CmsContentList. Returns empty strings when not configured
		/// or no match is found.
		/// </summary>
		public static Func<string, TicketMetadata> BuildMetadataLookup(
			Config config,
			bool silent = false)
		{
			var tg = config.TicketGeneration;

			// Cache CmsContentList as a non-null local. When it's missing, we
			// short-circuit below with empty metadata anyway, so a fallback
			// instance is fine here — all reads through `list.X` then resolve
			// to declared defaults. This avoids the ?. operator-precedence
			// hazard (`a < b?.X ?? 0` doesn't parse the way readers expect).
			var list = config.CmsContentList ?? new CmsContentListConfig();

			// Feature disabled — return empty resolver.
			var emptyMetadata = new TicketMetadata("", "", "", "");
			var csvPath = list.Path;
			if (string.IsNullOrEmpty(csvPath)
				|| !File.Exists(csvPath)
				|| (string.IsNullOrEmpty(tg.UrlSourceColumn) && string.IsNullOrEmpty(tg.PackageColumn)
					&& string.IsNullOrEmpty(tg.CmsEditorBaseUrl) && tg.SpecialInfoMappings.Count == 0))
			{
				return _ => emptyMetadata;
			}

			// Load CSV into a column-name → value lookup per row.
			var delimiter = string.IsNullOrEmpty(list.ColumnDelimiter)
				? ";"
				: list.ColumnDelimiter;

			if (!FileIo.TryReadCsvLines(csvPath, silent, out var csvLines,
				Encoding.UTF8))
			{
				Logger.LogWarning("Skipping ticket metadata lookup — CSV unavailable.");
				return _ => emptyMetadata;
			}

			var rows = new List<Dictionary<string, string>>();
			string[]? headers = null;
			int skipped = 0;

			foreach (var raw in csvLines)
			{
				var line = raw.Trim();

				// Skip preamble rows before the header. Blank rows count — this
				// matches Power Query's Table.Skip(table, N) semantic, where the
				// user-counted row number in any editor (including Excel/Power
				// Query) determines what to skip. A blank line at row 4 still
				// occupies row 4 and consumes one skip slot.
				if (headers == null && skipped < list.SkipRows)
				{
					skipped++;
					continue;
				}

				// After preamble: filter blank lines (e.g. trailing empty rows
				// from CSV export).
				if (string.IsNullOrEmpty(line))
				{
					continue;
				}

				var cols = line.Split(delimiter);

				if (headers == null)
				{
					headers = [.. cols.Select(c => c.Trim())];
					continue;
				}

				var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < headers.Length && i < cols.Length; i++)
				{
					row[headers[i]] = cols[i].Trim();
				}

				rows.Add(row);
			}

			if (headers == null || rows.Count == 0)
			{
				return _ => emptyMetadata;
			}

			// Build a path→row index for fast lookup.
			var pathIndex = new Dictionary<string, Dictionary<string, string>>(
				StringComparer.OrdinalIgnoreCase);

			// Path column for row matching — defaults to first CSV column.
			string pathCol = string.IsNullOrEmpty(tg.PathColumn)
				? (headers.FirstOrDefault() ?? "")
				: tg.PathColumn;

			// CMS path column — used to build {CmsLink}.
			// Column name configured in crawlersuite.config.json via CmsPathColumn.
			string cmsPathCol = tg.CmsPathColumn;

			foreach (var row in rows)
			{
				if (row.TryGetValue(pathCol, out var path) && !string.IsNullOrEmpty(path))
				{
					pathIndex.TryAdd(path, row);
				}
			}

			// The lookup function itself — resolves all ticket metadata in one pass.
			return (pageUrl) =>
			{
				// Derive CSV path from public URL.
				// Empty PathStripPrefix falls back to config.Url — the
				// canonical site base URL configured for the crawl. Explicit value
				// overrides for CMSes whose export uses a different host/scheme than
				// the public site (rare). Mirrors the pattern used by
				// Content.CompareCrawlAndContent for 05-not-directly-crawlable.log.
				var stripPrefix = string.IsNullOrEmpty(list.PathStripPrefix)
					? config.Url
					: list.PathStripPrefix;
				var relativePath = string.IsNullOrEmpty(stripPrefix)
					? pageUrl
					: pageUrl.Replace(stripPrefix, "", StringComparison.OrdinalIgnoreCase);

				// Build CSV path — prefix with RowFilter but do NOT append the
				// ValueSuffix. The CMS path column in the export never has
				// a file extension — the suffix is only used for URL construction
				// in Content.Listing, not for metadata lookup.
				string csvPath;
				if (list.ValuePrefixReplace && !string.IsNullOrEmpty(list.RowFilter))
				{
					// Strip ValueSuffix from relative path before prefixing
					// so that /de/home/page.html matches the path value /content/.../de/home/page
					var pathForLookup = !string.IsNullOrEmpty(list.ValueSuffix)
						&& relativePath.EndsWith(list.ValueSuffix, StringComparison.OrdinalIgnoreCase)
						? relativePath[..^list.ValueSuffix.Length]
						: relativePath;
					csvPath = list.RowFilter + pathForLookup;
				}
				else
				{
					csvPath = relativePath;
				}

				if (!pathIndex.TryGetValue(csvPath, out var matchedRow))
				{
					return emptyMetadata;
				}

				// Location — from UrlSourceColumn.
				string location = "";
				if (!string.IsNullOrEmpty(tg.UrlSourceColumn)
					&& matchedRow.TryGetValue(tg.UrlSourceColumn, out var sourceVal))
				{
					if (!string.IsNullOrEmpty(tg.UrlSourceLocalName)
						&& sourceVal.Equals(tg.UrlSourceLocalName, StringComparison.OrdinalIgnoreCase))
					{
						location = tg.UrlSourceLocalName;
					}
					else if (!string.IsNullOrEmpty(tg.UrlSourceExternalName)
						&& sourceVal.Equals(tg.UrlSourceExternalName, StringComparison.OrdinalIgnoreCase))
					{
						location = tg.UrlSourceExternalName;
					}
					else
					{
						location = sourceVal;
					}
				}

				// Package — from PackageColumn.
				string package = "";
				if (!string.IsNullOrEmpty(tg.PackageColumn)
					&& matchedRow.TryGetValue(tg.PackageColumn, out var packageVal))
				{
					package = packageVal;
				}

				// CmsLink — CmsEditorBaseUrl + path column value + CmsEditorBaseUrlSuffix.
				string cmsLink = "";
				if (!string.IsNullOrEmpty(tg.CmsEditorBaseUrl)
					&& matchedRow.TryGetValue(cmsPathCol, out var cmsPath)
					&& !string.IsNullOrEmpty(cmsPath))
				{
					cmsLink = tg.CmsEditorBaseUrl.TrimEnd('/') + cmsPath + tg.CmsEditorBaseUrlSuffix;
				}

				// SpecialInfo — first matching SpecialInfoMapping wins.
				// Pattern supports * wildcard (case-insensitive).
				string specialInfo = "";
				foreach (var mapping in tg.SpecialInfoMappings)
				{
					if (string.IsNullOrEmpty(mapping.Column) || string.IsNullOrEmpty(mapping.Pattern))
					{
						continue;
					}

					if (!matchedRow.TryGetValue(mapping.Column, out var colVal))
					{
						continue;
					}

					if (MatchesWildcard(colVal, mapping.Pattern))
					{
						specialInfo = mapping.Label;
						break;
					}
				}

				return new TicketMetadata(location, package, cmsLink, specialInfo);
			};
		}

		// Converts a wildcard pattern (* = any sequence) to a regex and matches.
		internal static bool MatchesWildcard(string value, string pattern)
		{
			var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
				.Replace("\\*", ".*") + "$";
			return System.Text.RegularExpressions.Regex.IsMatch(
				value, regex,
				System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		}

		// ── Startup diagnostics for ticket metadata lookup ──────────────────────
		//
		// Emits human-friendly diagnostics so config mistakes that would otherwise
		// produce silently-empty ticket fields are visible at startup. Catches
		// these failure modes:
		//   * CmsContentList.Path empty or file not found
		//   * CmsContentList.SkipRows misaligned with the actual header row
		//   * Configured column names not present in the captured header
		//   * Template producing unexpected layout
		//
		// Pure side-effects (Logger output) — does not affect pipeline state.
		// Parallel implementation of the CSV-loading logic in BuildMetadataLookup;
		// any change to row-skip / header / lookup semantics must be reflected here.

		/// <summary>
		/// Writes a startup diagnostic summary for ticket metadata lookup configuration.
		/// Emits INFO/WARNING via Logger so config mistakes are visible at startup
		/// rather than silently producing empty ticket fields. Called once per run
		/// from AnalysisPipeline at startup.
		/// </summary>
		public static void LogMetadataLookupDiagnostics(Config config)
		{
			var tg = config.TicketGeneration;

			// File-only diagnostic: the full dump is written to application.log for
			// post-mortem inspection. The leading log line below is the in-file header.
			using (Logger.QuietConsole())
			{
				Logger.LogInfo("Ticket metadata lookup diagnostics:");
				LogMetadataLookupDiagnosticsBody(config, tg);
			}
		}

		// Body of the diagnostic — kept separate so the outer method can wrap it
		// in a single logging scope without scattering early-return cleanup
		// across every branch.
		private static void LogMetadataLookupDiagnosticsBody(Config config, TicketGenerationConfig tg)
		{
			// Cache CmsContentList as a non-null local — see BuildMetadataLookup
			// for the rationale (?. operator precedence + readability).
			var list = config.CmsContentList ?? new CmsContentListConfig();

			// 1. CmsContentList presence.
			var csvPath = list.Path;
			if (string.IsNullOrEmpty(csvPath))
			{
				Logger.LogInfo("  CmsContentList: <not configured> — ticket metadata lookup disabled.");
				return;
			}

			Logger.LogInfo($"  CmsContentList: {csvPath}");

			if (!File.Exists(csvPath))
			{
				Logger.LogWarning(
					$"  Ticket metadata lookup: file not found at '{csvPath}' — "
					+ "tickets will have empty Location, Package, and CmsLink fields.");
				return;
			}

			var fileSize = new FileInfo(csvPath).Length;
			Logger.LogInfo($"  File present: yes ({fileSize:N0} bytes)");

			// CSV age — surfaced unconditionally so the operator sees freshness
			// at startup. The stale-CSV gate inside Step_PerformPostCrawlPass
			// only fires when CmsContentList.PostCrawlPass=true, but the file feeds ticket
			// metadata regardless. Showing age here keeps the operator informed.
			var freshness = CmsContentListFreshnessCheck.Evaluate(config.CmsContentList);
			if (freshness.FileDate.HasValue)
			{
				if (freshness.CheckDisabled)
				{
					Logger.LogInfo(
						$"  File date: {freshness.FileDate?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} "
						+ $"({freshness.AgeDays} day(s) old; age check disabled, MaxAgeDays={freshness.MaxAgeDays})");
				}
				else if (freshness.IsStale)
				{
					Logger.LogWarning(
						$"  File date: {freshness.FileDate?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} "
						+ $"({freshness.AgeDays} day(s) old; exceeds MaxAgeDays={freshness.MaxAgeDays})");
					if (list.PostCrawlPass)
					{
						Logger.LogWarning(
							"  CmsContentList.PostCrawlPass=true — operator will be prompted (or post-crawl pass skipped in silent mode).");
					}
				}
				else
				{
					Logger.LogInfo(
						$"  File date: {freshness.FileDate?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} "
						+ $"({freshness.AgeDays} day(s) old; within MaxAgeDays={freshness.MaxAgeDays})");
				}
			}

			Logger.LogInfo($"  SkipRows: {list.SkipRows} (counted from row 1 — blank rows count, matching Power Query Table.Skip)");

			// 2. Read CSV using same logic as BuildMetadataLookup.
			if (!FileIo.TryReadCsvLines(csvPath, silent: true, out var csvLines, Encoding.UTF8))
			{
				Logger.LogWarning("  Could not read CSV file — tickets will have empty fields.");
				return;
			}

			var delimiter = string.IsNullOrEmpty(list.ColumnDelimiter)
				? ";" : list.ColumnDelimiter;

			string[]? headers = null;
			string[]? firstData = null;
			int skipped = 0;

			foreach (var raw in csvLines)
			{
				var line = raw.Trim();

				if (headers == null && skipped < list.SkipRows)
				{
					skipped++;
					continue;
				}

				if (string.IsNullOrEmpty(line))
				{
					continue;
				}

				var cols = line.Split(delimiter);

				if (headers == null)
				{
					headers = [.. cols.Select(c => c.Trim())];
					continue;
				}

				firstData = [.. cols.Select(c => c.Trim())];
				break;
			}

			if (headers == null)
			{
				Logger.LogWarning("  Header row not found — check CmsContentList.SkipRows.");
				return;
			}

			Logger.LogInfo($"  Header captured ({headers.Length} columns):");
			Logger.LogInfo($"    {string.Join(" | ", headers)}");

			// 3. Sanity-check: each configured column name present in captured header?
			var headerSet = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
			void CheckColumn(string label, string columnName)
			{
				if (string.IsNullOrEmpty(columnName))
				{
					return;
				}

				if (!headerSet.Contains(columnName))
				{
					Logger.LogWarning(
						$"  TicketGeneration.{label} = \"{columnName}\" — not found in captured header. "
						+ "Tickets for that field will be empty. Check CmsContentList.SkipRows and column spelling.");
				}
			}
			CheckColumn(nameof(tg.UrlSourceColumn), tg.UrlSourceColumn);
			CheckColumn(nameof(tg.PackageColumn), tg.PackageColumn);
			CheckColumn(nameof(tg.CmsPathColumn), tg.CmsPathColumn);
			CheckColumn(nameof(tg.PathColumn), tg.PathColumn);

			// 4. Sample ticket from the first data row.
			if (firstData == null)
			{
				Logger.LogInfo("  No data rows found below the header — no sample ticket to render.");
				return;
			}

			// Map first-data-row values back to columns.
			var firstRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < headers.Length && i < firstData.Length; i++)
			{
				firstRow[headers[i]] = firstData[i];
			}

			// Derive public URL from the CMS path value: reverse the path-construction in
			// BuildMetadataLookup. Strip RowFilter from it, prefix with
			// config.Url, append ValueSuffix. This mirrors what a real page's
			// URL would look like for the metadata lookup to succeed.
			//
			// Last path segment is replaced with "sample" so the URL is visibly
			// synthetic — operators reading the diagnostic must not confuse it
			// with a real page that exists at the first CSV row's path value.
			string sampleUrl = "<unable to derive sample URL — check CmsPathColumn>";
			string sampleCmsPath = "";
			if (!string.IsNullOrEmpty(tg.CmsPathColumn)
				&& firstRow.TryGetValue(tg.CmsPathColumn, out var cmsPath)
				&& !string.IsNullOrEmpty(cmsPath))
			{
				sampleCmsPath = ReplaceLastSegmentWithSample(cmsPath);

				var pathPart = sampleCmsPath;
				if (!string.IsNullOrEmpty(list.RowFilter)
					&& pathPart.StartsWith(list.RowFilter, StringComparison.OrdinalIgnoreCase))
				{
					pathPart = pathPart[list.RowFilter.Length..];
				}

				var baseUrl = !string.IsNullOrEmpty(list.PathStripPrefix)
					? list.PathStripPrefix
					: config.Url;

				sampleUrl = (baseUrl ?? "") + pathPart + (list.ValueSuffix ?? "");
			}

			// Build sample TicketMetadata directly from the first row.
			string location = "";
			if (!string.IsNullOrEmpty(tg.UrlSourceColumn)
				&& firstRow.TryGetValue(tg.UrlSourceColumn, out var srcVal))
			{
				if (!string.IsNullOrEmpty(tg.UrlSourceLocalName)
					&& srcVal.Equals(tg.UrlSourceLocalName, StringComparison.OrdinalIgnoreCase))
				{
					location = tg.UrlSourceLocalName;
				}
				else if (!string.IsNullOrEmpty(tg.UrlSourceExternalName)
					&& srcVal.Equals(tg.UrlSourceExternalName, StringComparison.OrdinalIgnoreCase))
				{
					location = tg.UrlSourceExternalName;
				}
				else
				{
					location = srcVal;
				}
			}
			string package = "";
			if (!string.IsNullOrEmpty(tg.PackageColumn)
				&& firstRow.TryGetValue(tg.PackageColumn, out var pkgVal))
			{
				package = pkgVal;
			}

			string cmsLink = "";
			if (!string.IsNullOrEmpty(tg.CmsEditorBaseUrl)
				&& !string.IsNullOrEmpty(sampleCmsPath))
			{
				cmsLink = tg.CmsEditorBaseUrl.TrimEnd('/') + sampleCmsPath + tg.CmsEditorBaseUrlSuffix;
			}

			Logger.LogInfo($"  Sample from the first data row: {sampleUrl}");
			Logger.LogInfo($"    Source: {(location.Length > 0 ? location : "(none)")} · package: {(package.Length > 0 ? package : "(none)")} · CMS link: {(cmsLink.Length > 0 ? cmsLink : "(none)")}");
		}

		// Replaces the final path segment of a slash-separated path with the
		// literal "sample". Used by the startup diagnostic to make the sample
		// ticket's URL visibly synthetic — the path structure is preserved so
		// the operator can verify URL derivation works, but the final segment
		// signals "this is a preview, not a real page."
		// Examples:
		//   "/cms/path/alpha/page-one" → "/cms/path/alpha/sample"
		//   "/foo"                     → "/sample"
		//   ""                         → ""
		private static string ReplaceLastSegmentWithSample(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return path;
			}

			var idx = path.LastIndexOf('/');
			if (idx < 0)
			{
				return "sample";
			}

			return path[..(idx + 1)] + "sample";
		}
	}
}
