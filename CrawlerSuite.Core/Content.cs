namespace CrawlerSuite.Core
{
	public class Content
	{
		/// <summary>
		/// The content list's online filter, all of it from the configuration: a row counts as online
		/// when the value in <see cref="Column"/> (a header name) matches <see cref="Pattern"/> (* matches
		/// anything, case ignored). The app knows no column or value of its own. Empty column or pattern:
		/// no filter.
		/// </summary>
		/// <param name="SkipRows">Rows above the header, as for the ticket metadata lookup.</param>
		/// <param name="NotOnlineFile">Where the rows left out are listed (log 39); null writes none.</param>
		public sealed record OnlineFilter(string Column, string Pattern, int SkipRows, string? NotOnlineFile)
		{
			public bool IsOn => Column.Trim().Length > 0 && Pattern.Trim().Length > 0;
		}

		/// <summary>
		/// What <see cref="Listing"/> did: the paths listed, those left out as not online, why a filter
		/// that is set did not apply; and for the settings screen the rows read (past the row filters,
		/// with a path) and, per path to leave out (trimmed), how many of them it matched — switched-off
		/// entries and entries behind another one counted too.
		/// </summary>
		public sealed record ListingResult(int Listed, int NotOnline, string? FilterOff)
		{
			public int RowsRead { get; init; }

			public IReadOnlyDictionary<string, int> PathsLeftOut { get; init; } = new Dictionary<string, int>();
		}

		public static ListingResult Listing(
			string outputFile,
			string filePath,
			string rowMustContain,
			string columnDelimiter,
			int columnPosition,
			bool replacePrefix,
			string addSuffix,
			List<string> exclusions,
			string rowNegativeFilter,
			bool silent = false,
			OnlineFilter? online = null,
			IReadOnlyList<string>? switchedOffExclusions = null
		)
		{
			List<string> filteredRows = [];
			List<string> notOnline = [];

			// Every entry is counted, also one that matched nothing (that is the point).
			var counted = exclusions.Concat(switchedOffExclusions ?? []).Where(e => !string.IsNullOrEmpty(e)).ToList();
			var leftOut = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var entry in counted)
			{
				leftOut.TryAdd(entry.Trim(), 0);
			}

			int rowsRead = 0;

			if (columnPosition < 0)
			{
				Logger.LogError($"Invalid column position configured in CmsContentList.ColumnIndex in crawlersuite.config.json.");
				return new ListingResult(0, 0, null);
			}

			// Split the negative filter pattern once before the loop instead of
			// re-allocating the array on every line read.
			string[] negativeFilters = rowNegativeFilter.Split(['|'], StringSplitOptions.None);
			var delimiter = string.IsNullOrEmpty(columnDelimiter) ? ";" : columnDelimiter;

			if (!FileIo.TryReadCsvLines(filePath, silent, out var csvLines))
			{
				Logger.LogWarning($"Skipping content listing — CSV unavailable: {filePath}");
				return new ListingResult(0, 0, null);
			}

			var lines = csvLines as IReadOnlyList<string> ?? [.. csvLines];
			int onlineColumn = -1, header = -1;
			string? filterOff = null;
			if (online is { IsOn: true })
			{
				(onlineColumn, header, filterOff) = OnlineColumnOf(lines, online, delimiter);
				if (filterOff is not null)
				{
					Logger.LogWarning($"Content list: the online filter is off — {filterOff}");
				}
			}

			for (int n = 0; n < lines.Count; n++)
			{
				var line = lines[n];
				// The header row is no page, whatever the row filter lets through.
				if (n == header || string.IsNullOrEmpty(line) || !line.Contains(rowMustContain))
				{
					continue;
				}

				bool containsNegativeFilter = negativeFilters.Any(filter => line.Contains(filter));
				if (containsNegativeFilter)
				{
					continue;
				}

				string[] columns = line.Split([delimiter], StringSplitOptions.None);

				if (columns.Length > columnPosition)
				{
					var entry = columns[columnPosition];
					if (replacePrefix)
					{
						entry = entry.Replace(rowMustContain, string.Empty) + addSuffix;
					}

					if (!string.IsNullOrEmpty(entry) && !entry.Equals(addSuffix))
					{
						rowsRead++;
						foreach (var exclusion in counted.Where(e => entry.Contains(e)).Select(e => e.Trim()).Distinct(StringComparer.Ordinal))
						{
							leftOut[exclusion]++;
						}
					}

					if (!exclusions.Any(exclusion => entry.Contains(exclusion)))
					{
						if (!string.IsNullOrEmpty(entry) && !entry.Equals(addSuffix))
						{
							if (onlineColumn >= 0 && !IsOnline(columns, onlineColumn, online!.Pattern))
							{
								notOnline.Add(entry);
							}
							else
							{
								filteredRows.Add(entry);
							}
						}
					}
				}
			}

			FileIo.WriteList(outputFile, filteredRows);
			if (online?.NotOnlineFile is { } notOnlineFile)
			{
				// Written whenever the filter is set, so an empty list says "none", not "not looked".
				if (onlineColumn >= 0)
				{
					FileIo.WriteList(notOnlineFile, notOnline);
				}
				else if (File.Exists(notOnlineFile))
				{
					File.Delete(notOnlineFile);
				}
			}

			if (onlineColumn >= 0)
			{
				Logger.LogInfo($"Content list: {filteredRows.Count} page(s) online, {notOnline.Count} left out as not online (column \"{online!.Column.Trim()}\").");
			}

			return new ListingResult(filteredRows.Count, notOnline.Count, filterOff) { RowsRead = rowsRead, PathsLeftOut = leftOut };
		}

		/// <summary>The value in the online column matches the pattern; a row too short to have it does not.</summary>
		private static bool IsOnline(string[] columns, int column, string pattern) =>
			column < columns.Length && SpellMetadataLookup.MatchesWildcard(columns[column].Trim(), pattern.Trim());

		/// <summary>
		/// The online column's position in the header row: the first non-blank row after
		/// <see cref="OnlineFilter.SkipRows"/> rows (blank rows count, as for the ticket metadata lookup),
		/// its names compared trimmed and case ignored. -1 with the reason when there is none.
		/// </summary>
		internal static (int Column, int Header, string? Off) OnlineColumnOf(IReadOnlyList<string> lines, OnlineFilter online, string delimiter)
		{
			int at = Math.Max(0, online.SkipRows);
			while (at < lines.Count && string.IsNullOrWhiteSpace(lines[at]))
			{
				at++;
			}

			if (at >= lines.Count)
			{
				return (-1, -1, "the file has no header row (check CmsContentList.SkipRows).");
			}

			var names = lines[at].Split([delimiter], StringSplitOptions.None).Select(n => n.Trim().Trim('\uFEFF')).ToList();
			int column = names.FindIndex(n => n.Equals(online.Column.Trim(), StringComparison.OrdinalIgnoreCase));
			return column >= 0
				? (column, at, null)
				: (-1, -1, $"the header row has no column \"{online.Column.Trim()}\" (check CmsContentList.OnlineColumn and SkipRows).");
		}

		public static void CompareCrawlAndContent(string contentList, string compareList, string outputLog, string prefixToStrip)
		{
			HashSet<string> crawlEntries = [];

			using (StreamReader reader = new(contentList))
			{
				while (!reader.EndOfStream)
				{
					string line = reader.ReadLine() ?? string.Empty;
					if (!string.IsNullOrEmpty(line))
					{
						var parts = line.Split('|');
						// [KEEP] Index format: filename|url|source.
						// Use >= 2 so both 2-column (no source) and 3-column index files are handled.
						if (parts.Length >= 2)
						{
							string remoteFile = parts[1];
							if (remoteFile.StartsWith(prefixToStrip))
							{
								remoteFile = remoteFile[prefixToStrip.Length..];
							}
							crawlEntries.Add(remoteFile);
						}
					}
				}
			}

			List<string> missingPaths = [];

			using (StreamReader reader = new(compareList))
			{
				while (!reader.EndOfStream)
				{
					string relativePath = reader.ReadLine() ?? string.Empty;
					if (!string.IsNullOrEmpty(relativePath) && !crawlEntries.Contains(relativePath))
					{
						missingPaths.Add(relativePath);
					}
				}
			}

			FileIo.WriteAllLinesWithRetry(outputLog, missingPaths, Path.GetFileName(outputLog));
		}
	}
}
