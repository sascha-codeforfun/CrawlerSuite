namespace CrawlerSuite.Core.Snapshots
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Security.Cryptography;
	using System.Text;
	using System.Text.Json;
	using System.Threading;
	using CrawlerSuite.Core.Boilerplate;

	/// <summary>Which boilerplate groups a snapshot's pruned pages were written with.</summary>
	/// <param name="Fingerprint">The groups' fingerprint (<see cref="Reprune.Fingerprint"/>).</param>
	/// <param name="Groups">Groups in effect.</param>
	/// <param name="Rules">Parts switched on, over all groups.</param>
	/// <param name="PrunedAt">When the pages were pruned (UTC).</param>
	public sealed record PruneStamp(string Fingerprint, int Groups, int Rules, DateTime PrunedAt);

	/// <summary>What a re-prune of one snapshot did.</summary>
	/// <param name="Pages">Pages written to pruned/.</param>
	/// <param name="Skipped">Why the snapshot was left as it was (its crawl did not finish); null when it was pruned.</param>
	public sealed record RepruneOutcome(string Snapshot, int Pages, string? Skipped);

	/// <summary>
	/// Prunes a snapshot's saved pages again with the current boilerplate groups — the same
	/// prune the analysis runs, reference pages whole — so snapshots pruned at different times can
	/// be compared alike. Writes the snapshot's <c>pruned/</c> folder and its prune stamp and
	/// nothing else: no analysis step, no findings, and never the triage ledger (which lives
	/// beside the snapshots, not in them). The new pages are written to a folder of their own
	/// first and take the old folder's place only when complete.
	/// </summary>
	public static class Reprune
	{
		/// <summary>The stamp in a snapshot's folder: which groups its pruned pages were written with.</summary>
		public const string StampFileName = "prune-stamp.json";

		private const string PrunedFolder = "pruned";
		private const string NewFolder = "pruned.new";
		private const string OldFolder = "pruned.old";

		private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

		/// <summary>
		/// How the prune applies the groups, part of every fingerprint: a pruned folder written by an
		/// older prune then reads as pruned with other rules, so the Comparison asks for a re-prune.
		/// 2: attribute parts apply on reference pages too.
		/// </summary>
		internal const int PruneVersion = 2;

		/// <summary>
		/// A fingerprint of what decides a prune: each group's path prefix, its reference pages and the
		/// parts switched on (type and value). Order does not matter; switched-off parts do not count.
		/// </summary>
		public static string Fingerprint(IReadOnlyList<BoilerplateGroupConfig> groups)
		{
			var lines = groups
				.Select(g => string.Join('\u001F',
					[
						g.PathPrefix.Trim(),
						string.Join('\u001E', g.PagesToCheckBoiler.Select(p => p.Trim()).Order(StringComparer.Ordinal)),
						string.Join('\u001E', g.BoilerplateSelectors.Where(s => s.Enabled)
							.Select(s => $"{s.Type.Trim().ToLowerInvariant()}={s.Value.Trim()}").Order(StringComparer.Ordinal)),
					]))
				.Order(StringComparer.Ordinal);
			var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"prune {PruneVersion}\n" + string.Join('\n', lines)));
			return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
		}

		/// <summary>Writes the stamp of a prune with <paramref name="groups"/> into <paramref name="snapshot"/>.</summary>
		public static void WriteStamp(string snapshot, IReadOnlyList<BoilerplateGroupConfig> groups)
		{
			var stamp = new PruneStamp(Fingerprint(groups), groups.Count,
				groups.Sum(g => g.BoilerplateSelectors.Count(s => s.Enabled)), DateTime.UtcNow);
			try
			{
				File.WriteAllText(Path.Combine(snapshot, StampFileName), JsonSerializer.Serialize(stamp, Indented), new UTF8Encoding(false));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Logger.LogWarning($"Prune stamp could not be written — {ex.Message}");
			}
		}

		/// <summary>A snapshot's stamp; null when it has none (its prune wrote no stamp) or it cannot be read.</summary>
		public static PruneStamp? ReadStamp(string snapshot)
		{
			var path = Path.Combine(snapshot, StampFileName);
			try
			{
				return File.Exists(path) ? JsonSerializer.Deserialize<PruneStamp>(File.ReadAllText(path, Encoding.UTF8)) : null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				return null;
			}
		}

		/// <summary>
		/// Re-prunes one snapshot with <paramref name="config"/>'s boilerplate groups. A snapshot
		/// whose crawl did not finish (no crawler log) is skipped, as everywhere else.
		/// <paramref name="onPage"/> is called once per page done.
		/// </summary>
		public static RepruneOutcome Run(string snapshot, Config config, Action? onPage = null, CancellationToken cancel = default)
		{
			var download = Path.Combine(snapshot, "download");
			var log = Path.Combine(snapshot, LogFileNames.Crawler);
			if (!Directory.Exists(download) || !File.Exists(log))
			{
				return new RepruneOutcome(snapshot, 0, "its crawl did not finish (no crawler log)");
			}

			cancel.ThrowIfCancellationRequested();
			var index = CrawlIndex.BuildLogIndex(log);
			var groups = config.SpellCheckEngine.BoilerplateGroups;
			var fresh = Path.Combine(snapshot, NewFolder);
			if (Directory.Exists(fresh))
			{
				Directory.Delete(fresh, recursive: true);
			}

			Directory.CreateDirectory(fresh);
			try
			{
				BoilerplateSimplifier.Run(download, fresh, new BoilerplateResolver(groups),
					file => index.TryGetValue(file, out var url) ? url : file,
					config.FilePattern, config.ResolvedDegreeOfParallelism, () =>
					{
						cancel.ThrowIfCancellationRequested();
						onPage?.Invoke();
					});
			}
			catch (Exception ex) when (ex is OperationCanceledException
				|| (ex is AggregateException agg && agg.InnerExceptions.All(e => e is OperationCanceledException)))
			{
				// Stopped: the snapshot keeps its old pruned pages and stamp.
				Directory.Delete(fresh, recursive: true);
				throw new OperationCanceledException(cancel);
			}

			// Complete: the new pages take the old folder's place.
			var pruned = Path.Combine(snapshot, PrunedFolder);
			var old = Path.Combine(snapshot, OldFolder);
			if (Directory.Exists(old))
			{
				Directory.Delete(old, recursive: true);
			}

			if (Directory.Exists(pruned))
			{
				Directory.Move(pruned, old);
			}

			Directory.Move(fresh, pruned);
			if (Directory.Exists(old))
			{
				Directory.Delete(old, recursive: true);
			}

			WriteStamp(snapshot, groups);
			int pages = Directory.EnumerateFiles(pruned).Count();
			Logger.LogInfo($"Re-prune: {Path.GetFileName(snapshot)} — {pages} page(s), {groups.Count} boilerplate group(s).");
			return new RepruneOutcome(snapshot, pages, null);
		}

		/// <summary>The pages a re-prune of <paramref name="snapshot"/> goes through (for a progress total).</summary>
		public static int PagesOf(string snapshot, string filePattern)
		{
			var download = Path.Combine(snapshot, "download");
			return Directory.Exists(download) ? Directory.EnumerateFiles(download, filePattern).Count() : 0;
		}

		/// <summary>
		/// What the stamps of A and B say about comparing them, given the groups in effect now;
		/// empty when both were pruned with the current groups.
		/// </summary>
		public static string Hint(PruneStamp? a, PruneStamp? b, string currentFingerprint)
		{
			if (a is null || b is null)
			{
				var which = a is null && b is null ? "A and B were" : a is null ? "A was" : "B was";
				return $"{which} pruned before prune stamps existed, so whether the boilerplate groups match is not known. Re-prune A and B to be sure.";
			}

			if (a.Fingerprint != b.Fingerprint)
			{
				return "A and B were pruned with different boilerplate groups: differences may come from that. Re-prune A and B.";
			}

			return a.Fingerprint != currentFingerprint
				? "A and B were pruned alike, but not with the boilerplate groups in effect now. Re-prune A and B to apply them."
				: string.Empty;
		}
	}
}
