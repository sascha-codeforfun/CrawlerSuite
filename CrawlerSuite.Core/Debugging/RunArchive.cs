namespace CrawlerSuite.Core.Debugging
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.IO;
	using System.Linq;
	using System.Security.Cryptography;
	using System.Text;
	using System.Text.RegularExpressions;

	/// <summary>
	/// Debug mode's run archive: every completed run's output files are copied to
	/// <c>debug\&lt;site folder&gt;\&lt;snapshot&gt;\&lt;version&gt;-&lt;number&gt;</c> next to the program and
	/// compared with the previous run of the same snapshot and with a run marked as baseline.
	///
	/// The crawled pages are never copied (the snapshot's download and oversized folders): only
	/// what the run wrote. Names carry no time: the number counts the runs of a snapshot. Nothing
	/// here is ever deleted.
	/// </summary>
	public static class RunArchive
	{
		/// <summary>The archive folder's name, next to the program.</summary>
		public const string FolderName = "debug";

		/// <summary>The archive's own files in a run's folder; they are never compared.</summary>
		public const string RunInfoFile = "run-info.txt";
		public const string RunStepsFile = "run-steps.txt";
		public const string RunOverviewFile = "run-overview.txt";
		public const string CompareFile = "compare.txt";
		public const string CompareBaselineFile = "compare-baseline.txt";

		/// <summary>
		/// A run's files with size and SHA-256, written when the run is archived (or on first compare
		/// of an older run): two runs compare by their lists, and only files whose hash differs are
		/// opened. Opening thousands of files is slow — on Windows every open is also a virus
		/// scan.
		/// </summary>
		public const string ManifestFile = "files.sha256";

		/// <summary>In a snapshot's archive folder: the name of the run marked as baseline.</summary>
		public const string BaselineMarker = "baseline.txt";

		/// <summary>The most differing lines listed per file and side in a compare file.</summary>
		internal const int MaxListedLines = 2000;

		private static readonly HashSet<string> ArchiveOwnFiles = new(StringComparer.OrdinalIgnoreCase)
		{
			RunInfoFile, RunStepsFile, RunOverviewFile, CompareFile, CompareBaselineFile, ManifestFile,
		};

		/// <summary>Snapshot folders that hold the crawl itself, not the run's results.</summary>
		private static readonly HashSet<string> CrawlFolders = new(StringComparer.OrdinalIgnoreCase)
		{
			"download",
			OversizedFiles.FolderName,
		};

		/// <summary>
		/// Lines that differ from run to run by design and are left out of the compare: the time
		/// a "no longer needed" report was made (log 35's first line), and the time the pages were
		/// pruned (prune-stamp.json). Matched at the line's start, indentation aside.
		/// </summary>
		private static readonly (string File, string LinePrefix)[] IgnoredLines =
		[
			(LogFileNames.DictionaryNotNeeded, "# 35 — "),
			(Snapshots.Reprune.StampFileName, "\"PrunedAt\":"),
		];

		private static readonly Regex RunName = new(@"^(?<version>.+)-(?<number>\d{3,})$", RegexOptions.CultureInvariant);

		/// <summary>The archive folder next to the program.</summary>
		public static string Root => Path.Combine(AppPaths.BaseDirectory, FolderName);

		/// <summary>The archive folder of one snapshot: site folder, then the snapshot's own name.</summary>
		public static string SnapshotFolder(string root, string snapshotDirectory)
		{
			var full = Path.GetFullPath(snapshotDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var snapshot = Path.GetFileName(full);
			var site = Path.GetFileName(Path.GetDirectoryName(full) ?? string.Empty);
			return site.Length == 0 ? Path.Combine(root, snapshot) : Path.Combine(root, site, snapshot);
		}

		/// <summary>The archived runs of a snapshot, oldest first (by their number).</summary>
		public static IReadOnlyList<string> Runs(string snapshotArchive) =>
			!Directory.Exists(snapshotArchive)
				? []
				: Directory.EnumerateDirectories(snapshotArchive)
					.Select(Path.GetFileName)
					.OfType<string>()
					.Select(name => (name, match: RunName.Match(name)))
					.Where(r => r.match.Success)
					.OrderBy(r => long.Parse(r.match.Groups["number"].Value, CultureInfo.InvariantCulture))
					.Select(r => r.name)
					.ToList();

		/// <summary>The name of the next run of a snapshot: the version and the next number (001, 002 …).</summary>
		public static string NextRunName(string snapshotArchive, string version)
		{
			long highest = Runs(snapshotArchive)
				.Select(name => long.Parse(RunName.Match(name).Groups["number"].Value, CultureInfo.InvariantCulture))
				.DefaultIfEmpty(0)
				.Max();
			return $"{version}-{highest + 1:000}";
		}

		/// <summary>The run marked as baseline for a snapshot, or null (none, or its folder is gone).</summary>
		public static string? Baseline(string snapshotArchive)
		{
			var marker = Path.Combine(snapshotArchive, BaselineMarker);
			if (!File.Exists(marker))
			{
				return null;
			}

			var name = File.ReadAllText(marker).Trim();
			return name.Length > 0 && Directory.Exists(Path.Combine(snapshotArchive, name)) ? name : null;
		}

		/// <summary>Marks an archived run as the snapshot's baseline (replacing an earlier mark).</summary>
		public static void MarkBaseline(string snapshotArchive, string runName)
		{
			if (!Directory.Exists(Path.Combine(snapshotArchive, runName)))
			{
				throw new DirectoryNotFoundException($"No archived run {runName}.");
			}

			File.WriteAllText(Path.Combine(snapshotArchive, BaselineMarker), runName + Environment.NewLine);
		}

		/// <summary>
		/// Copies what a run wrote to its snapshot into <paramref name="target"/>: every file and
		/// folder except the crawl itself. Links (symlinks, junctions) are not followed.
		/// </summary>
		/// <returns>The number of files and bytes copied.</returns>
		/// <remarks>Writes the run's <see cref="ManifestFile"/> once the files are there.</remarks>
		public static (int Files, long Bytes) CopyRunOutput(string snapshotDirectory, string target)
		{
			Directory.CreateDirectory(target);
			int files = 0;
			long bytes = 0;
			foreach (var entry in Directory.EnumerateFileSystemEntries(snapshotDirectory))
			{
				var name = Path.GetFileName(entry);
				if (IsLink(entry))
				{
					continue;
				}

				if (Directory.Exists(entry))
				{
					if (!CrawlFolders.Contains(name))
					{
						var (f, b) = CopyFolder(entry, Path.Combine(target, name));
						files += f;
						bytes += b;
					}
				}
				else
				{
					File.Copy(entry, Path.Combine(target, name), overwrite: false);
					files++;
					bytes += new FileInfo(entry).Length;
				}
			}

			// The list the next compare reads instead of opening every file; the files were just written.
			Manifest(target, RelativeFiles(target));
			return (files, bytes);
		}

		private static (int Files, long Bytes) CopyFolder(string source, string target)
		{
			Directory.CreateDirectory(target);
			int files = 0;
			long bytes = 0;
			foreach (var entry in Directory.EnumerateFileSystemEntries(source))
			{
				if (IsLink(entry))
				{
					continue;
				}

				var destination = Path.Combine(target, Path.GetFileName(entry));
				if (Directory.Exists(entry))
				{
					var (f, b) = CopyFolder(entry, destination);
					files += f;
					bytes += b;
				}
				else
				{
					File.Copy(entry, destination, overwrite: false);
					files++;
					bytes += new FileInfo(entry).Length;
				}
			}

			return (files, bytes);
		}

		private static bool IsLink(string path) =>
			(File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

		/// <summary>SHA-256 of a file in lowercase hex, or "none" when there is no such file.</summary>
		public static string Checksum(string path)
		{
			if (!File.Exists(path))
			{
				return "none";
			}

			using var stream = File.OpenRead(path);
			return Convert.ToHexStringLower(SHA256.HashData(stream));
		}

		/// <summary>The total size of a folder's files (0 when it does not exist).</summary>
		public static long SizeOf(string folder)
		{
			if (!Directory.Exists(folder))
			{
				return 0;
			}

			long total = 0;
			foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
			{
				try
				{
					total += new FileInfo(file).Length;
				}
				catch (IOException)
				{
					// A file that went away meanwhile counts nothing.
				}
			}

			return total;
		}

		// ── Compare ─────────────────────────────────────────────────────────────

		/// <summary>How one file of two runs compares.</summary>
		public enum FileState
		{
			/// <summary>Byte for byte the same (after the ignored lines).</summary>
			Identical,

			/// <summary>The same lines, in a different order.</summary>
			OrderOnly,

			/// <summary>Lines only in one of the runs.</summary>
			Differs,

			/// <summary>Only the earlier run has the file.</summary>
			OnlyInA,

			/// <summary>Only the later run has the file.</summary>
			OnlyInB,
		}

		/// <summary>One file of a compare: its state and the lines found only in A and only in B.</summary>
		public sealed record FileCompare(string Name, FileState State, IReadOnlyList<string> OnlyInA, IReadOnlyList<string> OnlyInB);

		/// <summary>
		/// Compares two archived runs file by file (the archive's own files left out). A file is
		/// identical when its bytes are; otherwise its lines are compared as a multiset, so a line
		/// that occurs more often in one run counts as often as it is extra.
		/// </summary>
		public static IReadOnlyList<FileCompare> Compare(string runA, string runB)
		{
			var a = RelativeFiles(runA);
			var b = RelativeFiles(runB);
			var hashesA = Manifest(runA, a);
			var hashesB = Manifest(runB, b);
			var result = new List<FileCompare>();
			foreach (var name in a.Union(b, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
			{
				bool inA = a.Contains(name);
				bool inB = b.Contains(name);
				if (!inB)
				{
					result.Add(new FileCompare(name, FileState.OnlyInA, [], []));
					continue;
				}

				if (!inA)
				{
					result.Add(new FileCompare(name, FileState.OnlyInB, [], []));
					continue;
				}

				// Same size and hash, and no line of it left out: identical without opening either file.
				if (!HasIgnoredLines(name) && hashesA.TryGetValue(name, out var ha) && hashesB.TryGetValue(name, out var hb) && ha == hb)
				{
					result.Add(new FileCompare(name, FileState.Identical, [], []));
					continue;
				}

				result.Add(CompareOne(name, Path.Combine(runA, name), Path.Combine(runB, name)));
			}

			return result;
		}

		private static bool HasIgnoredLines(string name) =>
			IgnoredLines.Any(r => string.Equals(Path.GetFileName(name), r.File, StringComparison.OrdinalIgnoreCase));

		/// <summary>
		/// The run's files as "size hash" by name: from its manifest when that lists exactly the files
		/// there with their sizes (sizes are read without opening a file), else worked out and written
		/// (an older run, or one changed by hand). A manifest that cannot be written is only used.
		/// </summary>
		internal static Dictionary<string, string> Manifest(string run, IReadOnlySet<string> files)
		{
			var path = Path.Combine(run, ManifestFile);
			var sizes = files.ToDictionary(f => f, f => new FileInfo(Path.Combine(run, f)).Length, StringComparer.OrdinalIgnoreCase);
			if (File.Exists(path))
			{
				var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				foreach (var line in File.ReadLines(path, Encoding.UTF8))
				{
					var parts = line.Split(' ', 3);
					if (parts.Length == 3 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
					{
						listed[parts[2]] = $"{size} {parts[1]}";
					}
				}

				bool current = listed.Count == sizes.Count && sizes.All(f =>
					listed.TryGetValue(f.Key, out var entry) && entry.StartsWith(f.Value.ToString(CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal));
				if (current)
				{
					return listed;
				}
			}

			var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var (name, size) in sizes)
			{
				made[name] = $"{size} {Checksum(Path.Combine(run, name))}";
			}

			try
			{
				File.WriteAllLines(path, made.OrderBy(m => m.Key, StringComparer.Ordinal)
					.Select(m => { var sh = m.Value.Split(' ', 2); return $"{sh[0]} {sh[1]} {m.Key}"; }), new UTF8Encoding(false));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Used for this compare only.
			}

			return made;
		}

		private static HashSet<string> RelativeFiles(string run) =>
			Directory.Exists(run)
				? Directory.EnumerateFiles(run, "*", SearchOption.AllDirectories)
					.Select(f => Path.GetRelativePath(run, f))
					.Where(f => !ArchiveOwnFiles.Contains(f))
					.ToHashSet(StringComparer.OrdinalIgnoreCase)
				: new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private static FileCompare CompareOne(string name, string pathA, string pathB)
		{
			var ignored = IgnoredLines.Where(r => string.Equals(Path.GetFileName(name), r.File, StringComparison.OrdinalIgnoreCase))
				.Select(r => r.LinePrefix)
				.ToList();
			if (ignored.Count == 0 && SameBytes(pathA, pathB))
			{
				return new FileCompare(name, FileState.Identical, [], []);
			}

			var linesA = ReadLines(pathA, ignored);
			var linesB = ReadLines(pathB, ignored);
			if (linesA.SequenceEqual(linesB, StringComparer.Ordinal))
			{
				return new FileCompare(name, FileState.Identical, [], []);
			}

			var counts = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var line in linesA)
			{
				counts[line] = counts.GetValueOrDefault(line) + 1;
			}

			var onlyB = new List<string>();
			foreach (var line in linesB)
			{
				int n = counts.GetValueOrDefault(line);
				if (n > 0)
				{
					counts[line] = n - 1;
				}
				else
				{
					onlyB.Add(line);
				}
			}

			var onlyA = new List<string>();
			foreach (var line in linesA)
			{
				if (counts.TryGetValue(line, out int left) && left > 0)
				{
					onlyA.Add(line);
					counts[line] = left - 1;
				}
			}

			return new FileCompare(name, onlyA.Count == 0 && onlyB.Count == 0 ? FileState.OrderOnly : FileState.Differs, onlyA, onlyB);
		}

		private static bool SameBytes(string pathA, string pathB)
		{
			var infoA = new FileInfo(pathA);
			var infoB = new FileInfo(pathB);
			if (infoA.Length != infoB.Length)
			{
				return false;
			}

			using var streamA = infoA.OpenRead();
			using var streamB = infoB.OpenRead();
			var bufferA = new byte[81920];
			var bufferB = new byte[81920];
			while (true)
			{
				int readA = streamA.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
				int readB = streamB.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
				if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
				{
					return false;
				}

				if (readA == 0)
				{
					return true;
				}
			}
		}

		private static List<string> ReadLines(string path, IReadOnlyList<string> ignoredPrefixes) =>
			File.ReadLines(path, Encoding.UTF8)
				.Where(line => !ignoredPrefixes.Any(p => line.TrimStart().StartsWith(p, StringComparison.Ordinal)))
				.ToList();

		/// <summary>
		/// One line for the run steps: the files and counts only, never a line's content
		/// ("12 identical · 3 differ: 10, 11, 12").
		/// </summary>
		public static string Summary(IReadOnlyList<FileCompare> files)
		{
			int identical = files.Count(f => f.State == FileState.Identical);
			var differ = files.Where(f => f.State is FileState.Differs or FileState.OrderOnly).ToList();
			var onlyA = files.Where(f => f.State == FileState.OnlyInA).ToList();
			var onlyB = files.Where(f => f.State == FileState.OnlyInB).ToList();
			if (differ.Count == 0 && onlyA.Count == 0 && onlyB.Count == 0)
			{
				return $"all {identical} files identical";
			}

			var parts = new List<string> { $"{identical} identical" };
			if (differ.Count > 0)
			{
				parts.Add($"{differ.Count} differ: {string.Join(", ", differ.Select(f => ShortName(f.Name)))}");
			}

			if (onlyA.Count > 0)
			{
				parts.Add($"{onlyA.Count} gone: {string.Join(", ", onlyA.Select(f => ShortName(f.Name)))}");
			}

			if (onlyB.Count > 0)
			{
				parts.Add($"{onlyB.Count} new: {string.Join(", ", onlyB.Select(f => ShortName(f.Name)))}");
			}

			return string.Join(" · ", parts);
		}

		/// <summary>A numbered log by its number ("11"), any other file by its name.</summary>
		internal static string ShortName(string name)
		{
			var file = Path.GetFileName(name);
			int dash = file.IndexOf('-');
			return dash == 2 && char.IsAsciiDigit(file[0]) && char.IsAsciiDigit(file[1]) && file == name ? file[..2] : name;
		}

		/// <summary>The compare written to a run's folder: every file's state and the differing lines.</summary>
		public static string Report(string nameA, string nameB, IReadOnlyList<FileCompare> files)
		{
			var nl = Environment.NewLine;
			var sb = new StringBuilder();
			sb.Append($"A (earlier): {nameA}").Append(nl);
			sb.Append($"B (this run): {nameB}").Append(nl);
			sb.Append($"Result: {Summary(files)}").Append(nl);
			sb.Append("Lines are compared as a multiset: order is ignored, and a line that occurs more often in one run is listed as often as it is extra.").Append(nl);
			foreach (var (file, prefix) in IgnoredLines)
			{
				sb.Append($"Left out: lines of {file} starting with \"{prefix}\" (they carry the time of the run).").Append(nl);
			}

			sb.Append(nl);
			foreach (var f in files)
			{
				switch (f.State)
				{
					case FileState.Identical:
						sb.Append($"=  {f.Name}").Append(nl);
						break;
					case FileState.OrderOnly:
						sb.Append($"~  {f.Name}: the same lines in a different order").Append(nl);
						break;
					case FileState.OnlyInA:
						sb.Append($"-  {f.Name}: only in A").Append(nl);
						break;
					case FileState.OnlyInB:
						sb.Append($"+  {f.Name}: only in B").Append(nl);
						break;
					default:
						sb.Append($"≠  {f.Name}: {f.OnlyInA.Count} line(s) only in A, {f.OnlyInB.Count} only in B").Append(nl);
						AppendLines(sb, "    - ", f.OnlyInA, nl);
						AppendLines(sb, "    + ", f.OnlyInB, nl);
						break;
				}
			}

			return sb.ToString();
		}

		private static void AppendLines(StringBuilder sb, string marker, IReadOnlyList<string> lines, string nl)
		{
			foreach (var line in lines.Take(MaxListedLines))
			{
				sb.Append(marker).Append(line).Append(nl);
			}

			if (lines.Count > MaxListedLines)
			{
				sb.Append($"    … {lines.Count - MaxListedLines} more").Append(nl);
			}
		}
	}
}
