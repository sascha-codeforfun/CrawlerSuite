namespace CrawlerSuite.Core
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using CrawlerSuite.Core.Host;
	using CrawlerSuite.Core.Snapshots;

	/// <summary>
	/// The preparation decisions taken before a crawl or replay: which snapshot to use,
	/// what to do with an incomplete one, the clean sweep, an empty download folder, an
	/// outdated CMS content list and orphan files. The questions go to the operator
	/// through <see cref="RunUi.Prompts"/>; the consequences (paths rebuilt, files
	/// deleted or renamed) are applied here.
	/// </summary>
	internal static class SnapshotFlows
	{
		/// <summary>
		/// Asks New / Use latest / Abort for the most recent snapshot. On Use latest the
		/// snapshot's integrity is checked, the optional clean sweep is offered and the run
		/// context is pointed at the snapshot. On New the previous snapshot's integrity is
		/// checked (the operator may still abort there).
		/// </summary>
		internal static SnapshotChoice PromptForSnapshotChoice(
			CrawlerRunContext ctx,
			DirectoryInfo mostRecent,
			string workingFolder,
			string urlDirectory,
			string url,
			string siteLabel,
			string ageDescription,
			bool isRecent)
		{
			var choice = RunUi.Prompts.AskSnapshotChoice(
				new SnapshotPrompt(siteLabel, url, mostRecent.Name, ageDescription, isRecent));

			if (choice == SnapshotChoice.UseLatest)
			{
				if (!CheckSnapshotIntegrity(mostRecent, url))
				{
					return SnapshotChoice.Abort;
				}

				Logger.LogInfo($"User chose latest snapshot: {mostRecent.Name}.");
				RunUi.WriteInfo(
					"Replay uses the existing classification from the original crawl; " +
					"file types (HTML vs unverified) are not re-evaluated. " +
					"To apply a changed UnverifiedHtmlPolicy, run a new crawl.");

				var snapshotPath = Path.Combine(workingFolder, urlDirectory, mostRecent.Name);
				if (RunUi.Prompts.AskCleanSweep(snapshotPath))
				{
					CleanSweepSnapshot(snapshotPath);
				}

				ApplyReplayLatestChoice(ctx, mostRecent.Name, snapshotPath);
				return SnapshotChoice.UseLatest;
			}

			if (choice == SnapshotChoice.NewCrawl)
			{
				if (!CheckSnapshotIntegrity(mostRecent, url))
				{
					return SnapshotChoice.Abort;
				}

				var sessionParentDirectory = Path.Combine(workingFolder, urlDirectory);
				var mostRecentAfter = MostRecentSnapshot(sessionParentDirectory);
				Logger.LogInfo($"User chose new crawl. Previous snapshot: {mostRecentAfter?.Name ?? "none"} ({ageDescription} ago).");
				return SnapshotChoice.NewCrawl;
			}

			Logger.LogInfo("Crawl aborted by user.");
			return SnapshotChoice.Abort;
		}

		/// <summary>The newest snapshot folder under the site folder, or null.</summary>
		internal static DirectoryInfo? MostRecentSnapshot(string sessionParentDirectory)
		{
			if (!Directory.Exists(sessionParentDirectory))
			{
				return null;
			}

			return Directory
				.EnumerateDirectories(sessionParentDirectory)
				.Select(d => new DirectoryInfo(d))
				.Where(d => SnapshotFolder.Matches(d.Name))
				.OrderByDescending(d => d.CreationTimeUtc)
				.FirstOrDefault();
		}

		/// <summary>
		/// Offered when a replay finds no downloaded pages. True = a fresh crawl was set up
		/// (new timestamp, paths rebuilt); false = abort.
		/// </summary>
		internal static bool PromptForEmptyDownloadRecovery(
			CrawlerRunContext ctx,
			string workingFolder,
			string urlDirectory)
		{
			if (!RunUi.Prompts.AskEmptyDownloadStartFresh(ctx.FileDownloadDirectory))
			{
				Logger.LogInfo("Aborted — empty download folder.");
				return false;
			}

			ctx.IsDebugSession = false;
			ctx.TimeStamp = SnapshotFolder.NewName();
			ctx.RebuildTimestampPaths(Path.Combine(workingFolder, urlDirectory, ctx.TimeStamp));
			Logger.LogInfo("Empty download folder — switching to fresh crawl.");
			return true;
		}

		/// <summary>
		/// Checks a snapshot for completeness. If incomplete, asks Delete / Keep / Abort.
		/// Returns false if the operator chose to abort.
		/// </summary>
		internal static bool CheckSnapshotIntegrity(DirectoryInfo snapshot, string baseUrl)
		{
			var (isComplete, fileCount, totalMb) = CrawlLog.Summarise(snapshot, baseUrl);
			if (isComplete)
			{
				return true;
			}

			Logger.LogWarning($"Incomplete crawl detected: {snapshot.Name} — {fileCount:N0} files, {totalMb:F1} MB");

			var choice = RunUi.Prompts.AskIncompleteSnapshot(
				new IncompleteSnapshotPrompt(snapshot.FullName, snapshot.Name, fileCount, totalMb));

			if (choice == IncompleteSnapshotChoice.Abort)
			{
				Logger.LogInfo("Aborted by user after incomplete snapshot warning.");
				return false;
			}

			if (choice == IncompleteSnapshotChoice.Delete)
			{
				try
				{
					Directory.Delete(snapshot.FullName, recursive: true);
					Logger.LogWarning($"Deleted incomplete snapshot: {snapshot.Name} ({fileCount:N0} files, {totalMb:F1} MB).");
					RunUi.WriteSuccess("Incomplete snapshot deleted.");
				}
				catch (Exception ex)
				{
					Logger.LogError($"Could not delete snapshot: {ex.Message}");
					RunUi.WriteError($"Could not delete snapshot: {ex.Message}");
				}
			}
			else
			{
				Logger.LogInfo($"User chose to keep incomplete snapshot: {snapshot.Name}.");
			}

			return true;
		}

		/// <summary>
		/// Lets the operator pick which orphan files (no URL in the index) to rename to the
		/// backup extension, then renames them.
		/// </summary>
		internal static void ResolveOrphans(List<string> orphans, string downloadDirectory, string bakExt)
		{
			var chosen = RunUi.Prompts.AskOrphansToRename(orphans, bakExt);
			var chosenSet = new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase);

			int renamed = 0;
			foreach (var filename in orphans)
			{
				if (chosenSet.Contains(filename))
				{
					Downloader.OrphanFiles.Quarantine(downloadDirectory, filename, bakExt);
					renamed++;
					Logger.LogWarning($"  Renamed: {filename} → {filename}.bak");
				}
				else
				{
					Logger.LogInfo($"  Skipped: {filename}");
				}
			}

			if (renamed > 0)
			{
				Logger.LogWarning($"  Integrity check complete: {renamed} file(s) renamed to {bakExt}. Re-crawl to resolve.");
				RunUi.WriteStepRow("Orphans renamed", $"{renamed} of {orphans.Count}");
			}
			else
			{
				Logger.LogInfo("  Integrity check complete: all orphans skipped.");
			}
		}

		/// <summary>
		/// Points the run at the chosen snapshot: replay session, its timestamp, and every
		/// timestamp-dependent path rebuilt.
		/// </summary>
		internal static void ApplyReplayLatestChoice(
			CrawlerRunContext ctx,
			string snapshotName,
			string snapshotPath)
		{
			ctx.IsDebugSession = true;
			ctx.TimeStamp = snapshotName;
			ctx.RebuildTimestampPaths(snapshotPath);
		}

		/// <summary>
		/// Deletes everything in the snapshot folder except the crawl ground truth that cannot
		/// be re-derived without re-crawling: <c>download/</c>, <c>oversized/</c>,
		/// <c>00-crawler.log</c> and <c>01-crawler.log</c>. Per-entry errors are logged and
		/// the sweep continues. Symbolic links and junctions are never followed.
		/// </summary>
		internal static void CleanSweepSnapshot(string snapshotPath)
		{
			if (!Directory.Exists(snapshotPath))
			{
				Logger.LogWarning($"CleanSweepSnapshot: snapshot path does not exist: {snapshotPath}");
				return;
			}

			Logger.LogInfo($"CleanSweepSnapshot: cleaning {snapshotPath}");

			// The sweep runs right after the snapshot choice, before any phase has started; its rows
			// are filed under Prepare (where the replay continues) with their own time.
			RunUi.MarkStep();
			int filesDeleted = 0;
			int dirsDeleted = 0;
			int linksSkipped = 0;
			int filesSkipped = 0;
			int errors = 0;

			foreach (var entry in Directory.EnumerateFileSystemEntries(snapshotPath))
			{
				var name = Path.GetFileName(entry);
				if (ShouldKeepEntry(name))
				{
					Logger.LogInfo($"  Keep: {name}");
					continue;
				}

				try
				{
					var attrs = File.GetAttributes(entry);
					if ((attrs & FileAttributes.ReparsePoint) != 0)
					{
						linksSkipped++;
						Logger.LogWarning($"  Skipped reparse point (symlink/junction): {name} — manual review recommended");
						continue;
					}
				}
				catch (Exception ex)
				{
					errors++;
					Logger.LogWarning($"  Could not inspect attributes of {name}: {ex.Message}");
					continue;
				}

				try
				{
					if (Directory.Exists(entry))
					{
						Directory.Delete(entry, recursive: true);
						dirsDeleted++;
						Logger.LogInfo($"  Deleted directory: {name}");
					}
					else if (FileIo.DeleteWithRetry(entry, name))
					{
						filesDeleted++;
						Logger.LogInfo($"  Deleted file: {name}");
					}
					else
					{
						filesSkipped++;
						Logger.LogWarning($"  Skipped (left open): {name}");
					}
				}
				catch (Exception ex)
				{
					errors++;
					Logger.LogWarning($"  Could not delete {name}: {ex.Message}");
				}
			}

			Logger.LogInfo($"CleanSweepSnapshot: complete — {filesDeleted} file(s), {dirsDeleted} directory(ies) deleted, " +
				$"{linksSkipped} link(s) skipped, {filesSkipped} locked file(s) left, {errors} error(s).");

			RunUi.WriteStepRow(Host.RunPhases.Prepare, "Clean sweep", $"{filesDeleted} file(s) · {dirsDeleted} folder(s) deleted");
			int totalSkipped = linksSkipped + filesSkipped;
			if (totalSkipped > 0 || errors > 0)
			{
				RunUi.WriteStepRow(Host.RunPhases.Prepare, "Clean sweep skipped", $"{totalSkipped} skipped · {errors} error(s)", dimmed: true);
			}
		}

		private static readonly HashSet<string> CleanSweepKeepEntries =
			new(StringComparer.OrdinalIgnoreCase)
			{
				"download",
				OversizedFiles.FolderName,
				LogFileNames.CrawlerRaw,
				LogFileNames.Crawler,
				// What only the crawl counted (the download exclusions): a re-run cannot count it again.
				Settings.SettingsUsage.CrawlFileName,
				// Where the site's names pointed during the crawl: a re-run cannot look again.
				Security.DnsRecord.FileName,
			};

		/// <summary>True if the named snapshot entry is crawl ground truth and survives a clean sweep.</summary>
		internal static bool ShouldKeepEntry(string entryName) => CleanSweepKeepEntries.Contains(entryName);
	}
}
