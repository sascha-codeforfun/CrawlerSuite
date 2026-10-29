namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.IO;
	using System.Linq;
	using System.Security.Cryptography;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>How a file of the compared set stands.</summary>
	public enum CompareState
	{
		/// <summary>The same in A and B (for pages: as their runs pruned them).</summary>
		Identical,

		/// <summary>In both, not the same.</summary>
		Differs,

		/// <summary>Saved in A only.</summary>
		OnlyInA,

		/// <summary>Saved in B only.</summary>
		OnlyInB,
	}

	/// <summary>One file of the set that is not identical: its name in download/, what it is, and how it stands.</summary>
	/// <param name="Name">The path inside download/, '/' between folders.</param>
	/// <param name="IsPage">A page: compared as its run pruned it.</param>
	/// <param name="Url">The address it was saved from, from either snapshot's crawler index; null when neither has it.</param>
	/// <param name="Note">What else is known (pruned in one snapshot only, could not be read); empty for nothing.</param>
	/// <param name="NameA">A's file when it has another name than B's: the same address but for a cache buster.</param>
	public sealed record ComparedFile(string Name, bool IsPage, CompareState State, long BytesA, long BytesB, string? Url, string Note, string? NameA = null)
	{
		/// <summary>A's file name: <see cref="NameA"/> when the file was paired across a cache buster, else <see cref="Name"/>.</summary>
		public string FileA => NameA ?? Name;
	}

	/// <summary>The outcome of comparing snapshot A with snapshot B.</summary>
	/// <param name="Listed">The files that are not identical, by name.</param>
	/// <param name="IdenticalOnlyPruned">Pages identical as pruned whose saved files differ: what differs is boilerplate.</param>
	/// <param name="IdenticalOnlyCacheBusters">Pages identical once their cache busters are set aside.</param>
	/// <param name="Resaved">Files saved again under an address that differs only in its cache buster, with the same bytes.</param>
	/// <param name="Sidecars">Header sidecars left out of the set (both sides).</param>
	/// <param name="IdenticalOnlyTokens">Pages identical once their per-delivery tokens are set aside too (<see cref="DeliveryTokens"/>).</param>
	/// <param name="IdenticalOnlyBlankLines">Pages identical as pruned once their blank lines are set aside (<see cref="BlankLines"/>): what a removed boilerplate part left behind.</param>
	/// <param name="IdenticalOnlyPatterns">PDFs identical once the site's patterns are set aside in their text (<see cref="PdfSetAside"/>).</param>
	/// <param name="PatternHits">For each pattern, by name, how many of those PDFs it took.</param>
	/// <param name="RegeneratedPdfs">PDFs whose bytes differ but whose content does not: written out again (<see cref="PdfContent"/>); counted with the identical files.</param>
	public sealed record ComparisonResult(
		string SnapshotA,
		string SnapshotB,
		IReadOnlyList<ComparedFile> Listed,
		int FilesA,
		int FilesB,
		int IdenticalPages,
		int IdenticalAssets,
		int IdenticalOnlyPruned,
		int IdenticalOnlyCacheBusters,
		int Resaved,
		int Sidecars,
		TimeSpan Elapsed,
		int RegeneratedPdfs = 0,
		int IdenticalOnlyTokens = 0,
		int IdenticalOnlyBlankLines = 0,
		int IdenticalOnlyPatterns = 0,
		IReadOnlyDictionary<string, int>? PatternHits = null)
	{
		public int Identical => IdenticalPages + IdenticalAssets + Resaved;

		public int Count(CompareState state) => Listed.Count(f => f.State == state);
	}

	/// <summary>Both sides of one file, ready to be shown.</summary>
	/// <param name="IsText">Shown as text; otherwise size and checksum only.</param>
	/// <param name="TextA">A's text (null when A has no such file or it is not shown as text).</param>
	/// <param name="HashA">A's SHA-256, for files not shown as text.</param>
	/// <param name="Note">Why a file is not shown as text, or what else is known.</param>
	public sealed record FileSides(bool IsText, string? TextA, string? TextB, long BytesA, long BytesB, string? HashA, string? HashB, string Note);

	/// <summary>
	/// Compares the saved files of two snapshots of a site: every file in A's and B's download
	/// folder, paired by name, header sidecars left out. A page is compared as its run pruned it
	/// (the snapshot's pruned folder: the boilerplate groups applied, reference pages whole), its blank
	/// lines (<see cref="BlankLines"/>), cache busters (<see cref="CacheBusters"/>) and per-delivery
	/// tokens (<see cref="DeliveryTokens"/>) set aside; every other file byte for byte. A file saved
	/// only in A and one saved only in B whose addresses differ only in a cache buster are paired and
	/// compared. Any other difference counts — whitespace within a line and line endings included.
	/// Read-only: nothing in either snapshot is written. Pairs are compared in parallel (the
	/// analysis' parallel-work setting), each worker holding one pair of files at a time; a file is
	/// read in blocks, never whole.
	/// </summary>
	public static class SnapshotComparison
	{
		/// <summary>The folder of a snapshot that holds the saved files.</summary>
		public const string DownloadFolder = "download";

		/// <summary>The folder of a snapshot that holds its pages as its last run pruned them.</summary>
		public const string PrunedFolder = "pruned";

		/// <summary>Files larger than this are compared, but shown by size and checksum only.</summary>
		public const long MaxShownBytes = 16L * 1024 * 1024;

		/// <summary>The snapshot folders of a site folder, by name (oldest first); empty when there are none.</summary>
		public static IReadOnlyList<string> SnapshotsOf(string siteFolder)
		{
			try
			{
				return !Directory.Exists(siteFolder) ? []
					: Directory.EnumerateDirectories(siteFolder)
						.Where(d => CrawlerSuite.Core.Snapshots.SnapshotFolder.Matches(Path.GetFileName(d)))
						.OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
						.ToList();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Logger.LogWarning($"Comparison: could not list the snapshots — {ex.Message}");
				return [];
			}
		}

		/// <summary>
		/// Null when A and B can be compared; otherwise why not: one is missing, both are the same
		/// snapshot (also through another spelling of the path or a link), or one has no saved files.
		/// </summary>
		public static string? CheckPair(string? a, string? b)
		{
			if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
			{
				return "Choose a snapshot for A and one for B.";
			}

			string fullA, fullB;
			try
			{
				fullA = Resolved(a);
				fullB = Resolved(b);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				return $"A snapshot folder cannot be read: {ex.Message}";
			}

			if (string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase))
			{
				return "A and B are the same snapshot. Choose two different ones.";
			}

			foreach (var (side, path) in new[] { ("A", fullA), ("B", fullB) })
			{
				if (!Directory.Exists(Path.Combine(path, DownloadFolder)))
				{
					return $"Snapshot {side} ({Path.GetFileName(path)}) has no saved files (no {DownloadFolder} folder).";
				}

				if (!Directory.Exists(Path.Combine(path, PrunedFolder)))
				{
					return $"Snapshot {side} ({Path.GetFileName(path)}) has no pruned pages: run its analysis (Re-run latest when it is the latest), so the comparison can read them.";
				}
			}

			return null;
		}

		/// <summary>
		/// Compares A with B. Call <see cref="CheckPair"/> first; this throws
		/// <see cref="InvalidOperationException"/> on a pair it refuses. <paramref name="progress"/>
		/// gets (files done, files in the set).
		/// </summary>
		/// <param name="maxDegreeOfParallelism">Pairs compared at once: the analysis' "Parallel work" setting; 0 = every processor.</param>
		/// <param name="pdfSetAside">The site's patterns set aside in the text of PDFs (<see cref="Config.ComparisonPdfSetAside"/>); null sets nothing aside.</param>
		public static ComparisonResult Run(string a, string b,
			IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default, int maxDegreeOfParallelism = 0,
			PdfSetAside? pdfSetAside = null)
		{
			if (CheckPair(a, b) is { } refusal)
			{
				throw new InvalidOperationException(refusal);
			}

			var clock = Stopwatch.StartNew();
			var (filesA, sidecarsA) = FilesOf(Path.Combine(a, DownloadFolder));
			var (filesB, sidecarsB) = FilesOf(Path.Combine(b, DownloadFolder));
			var prunedA = PrunedOf(a);
			var prunedB = PrunedOf(b);
			var urls = UrlsOf(b, UrlsOf(a, null));

			// Windows file names do not tell case apart, so neither does the pairing.
			var names = new SortedSet<string>(filesA.Keys, StringComparer.OrdinalIgnoreCase);
			names.UnionWith(filesB.Keys);

			var ordered = names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
			var outcomes = new (CompareState State, bool Page, bool OnlyPruned, bool OnlyBusters, string Note, bool Regenerated, bool OnlyTokens, bool OnlyBlank, IReadOnlyCollection<string>? Patterns)[ordered.Length];
			int done = 0, total = ordered.Length;
			var gate = new object();
			progress?.Report((0, total));

			var options = new ParallelOptions
			{
				MaxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount,
				CancellationToken = cancel,
			};
			try
			{
				Parallel.For(0, ordered.Length, options, i => CompareOne(i));
			}
			catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
			{
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
			}

			void CompareOne(int i)
			{
				var name = ordered[i];
				filesA.TryGetValue(name, out var pathA);
				filesB.TryGetValue(name, out var pathB);
				prunedA.TryGetValue(LeafName(name), out var pageA);
				prunedB.TryGetValue(LeafName(name), out var pageB);
				bool page = pageA is not null || pageB is not null;
				if (pathA is null || pathB is null)
				{
					outcomes[i] = (pathA is null ? CompareState.OnlyInB : CompareState.OnlyInA, page, false, false, string.Empty, false, false, false, null);
				}
				else if (pageA is not null && pageB is not null)
				{
					// A page as its run pruned it; when the pruned pages match, whether the saved ones do too.
					var same = SameBytes(pageA, pageB);
					var noise = same == false ? SameButNoise(pageA, pageB) : Noise.None;
					outcomes[i] = same switch
					{
						true => (CompareState.Identical, true, SameBytes(pathA, pathB) != true, false, string.Empty, false, false, false, null),
						false when noise == Noise.BlankLines => (CompareState.Identical, true, false, false, string.Empty, false, false, true, null),
						false when noise == Noise.CacheBusters => (CompareState.Identical, true, false, true, string.Empty, false, false, false, null),
						false when noise == Noise.Tokens => (CompareState.Identical, true, false, false, string.Empty, false, true, false, null),
						false => (CompareState.Differs, true, false, false, string.Empty, false, false, false, null),
						null => (CompareState.Differs, true, false, false, "Could not be read.", false, false, false, null),
					};
				}
				else if (page)
				{
					outcomes[i] = (CompareState.Differs, true, false, false,
						$"Pruned in {(pageA is not null ? "A" : "B")} only: run the other snapshot's analysis to compare the page as pruned.", false, false, false, null);
				}
				else
				{
					var same = SameBytes(pathA, pathB);

					// A PDF written out again carries the same content in other bytes.
					bool regenerated = same == false && PdfContent.SameContent(pathA, pathB) == true;

					// Printed anew on each regeneration: the site's patterns set aside in the PDF's text.
					var patterns = same == false && !regenerated && pdfSetAside is not null
						&& PdfContent.SameContentBut(pathA, pathB, pdfSetAside) is { Same: true } but ? but.Hits : null;
					outcomes[i] = (same == true || regenerated || patterns is not null ? CompareState.Identical : CompareState.Differs, false, false, false,
						same is null ? "Could not be read." : string.Empty, regenerated, false, false, patterns);
				}

				lock (gate)
				{
					done++;
					progress?.Report((done, total));
				}
			}

			// A file only in A and one only in B whose addresses differ only in a cache buster are the same
			// file saved again: paired when exactly one of each shares the address with its cache buster
			// set aside, and compared by their bytes.
			var pairedTo = PairAcrossCacheBusters(ordered, outcomes.Select(o => o.State).ToArray(), urls);
			var resavedSame = new Dictionary<int, bool?>();
			foreach (var (indexA, indexB) in pairedTo)
			{
				filesA.TryGetValue(ordered[indexA], out var pathA);
				filesB.TryGetValue(ordered[indexB], out var pathB);
				resavedSame[indexB] = pathA is null || pathB is null ? null : SameBytes(pathA, pathB);
			}

			var pairedA = new HashSet<int>(pairedTo.Select(p => p.A));
			var partnerOfB = pairedTo.ToDictionary(p => p.B, p => p.A);

			// Added up in name order, so the result does not depend on which thread was first.
			var listed = new List<ComparedFile>();
			int identicalPages = 0, identicalAssets = 0, onlyPruned = 0, onlyBusters = 0, resaved = 0, regeneratedPdfs = 0, onlyTokens = 0, onlyBlank = 0, onlyPatterns = 0;
			var patternHits = new SortedDictionary<string, int>(StringComparer.Ordinal);
			for (int i = 0; i < ordered.Length; i++)
			{
				var (state, page, prunedOnly, bustersOnly, note, regeneratedPdf, tokensOnly, blankOnly, patternsTaken) = outcomes[i];
				if (state == CompareState.Identical)
				{
					if (patternsTaken is not null)
					{
						onlyPatterns++;
						foreach (var pattern in patternsTaken)
						{
							patternHits[pattern] = patternHits.GetValueOrDefault(pattern) + 1;
						}
					}

					onlyBlank += blankOnly ? 1 : 0;
					onlyTokens += tokensOnly ? 1 : 0;
					regeneratedPdfs += regeneratedPdf ? 1 : 0;
					identicalPages += page ? 1 : 0;
					identicalAssets += page ? 0 : 1;
					onlyPruned += prunedOnly ? 1 : 0;
					onlyBusters += bustersOnly ? 1 : 0;
					continue;
				}

				if (pairedA.Contains(i))
				{
					continue; // listed (or counted) with its partner from B
				}

				var name = ordered[i];
				var url = urls.TryGetValue(LeafName(name), out var u) ? u : null;
				if (partnerOfB.TryGetValue(i, out var indexA))
				{
					var nameA = ordered[indexA];
					filesA.TryGetValue(nameA, out var partnerA);
					filesB.TryGetValue(name, out var pathB2);
					var same = resavedSame[i];
					if (same == true)
					{
						resaved++;
						continue;
					}

					var urlA = urls.TryGetValue(LeafName(nameA), out var ua) ? ua : nameA;
					listed.Add(new ComparedFile(name, page, CompareState.Differs, SizeOf(partnerA), SizeOf(pathB2), url,
						same is null ? "Could not be read." : $"Saved again under a new cache buster; in A: {urlA}", nameA));
					continue;
				}

				filesA.TryGetValue(name, out var pathA);
				filesB.TryGetValue(name, out var pathB);
				listed.Add(new ComparedFile(name, page, state, SizeOf(pathA), SizeOf(pathB), url, note));
			}

			var result = new ComparisonResult(a, b, listed, filesA.Count, filesB.Count, identicalPages, identicalAssets, onlyPruned,
				onlyBusters, resaved, sidecarsA + sidecarsB, clock.Elapsed, regeneratedPdfs, onlyTokens, onlyBlank, onlyPatterns, patternHits);

			// Counts only: the application log names no file and no address.
			Logger.LogInfo($"Comparison {Path.GetFileName(a)} → {Path.GetFileName(b)}: {result.Identical} identical "
				+ $"({onlyPruned} pages only as pruned, {onlyBlank} only but for blank lines, {onlyBusters} only but for cache busters, {resaved} files saved again, "
				+ $"{onlyTokens} only but for per-delivery tokens, {regeneratedPdfs} PDFs written out again, "
				+ $"{onlyPatterns} PDFs only but for configured patterns{(patternHits.Count > 0 ? " — " + string.Join(", ", patternHits.Select(h => $"{h.Key}: {h.Value}")) : string.Empty)}), "
				+ $"{result.Count(CompareState.Differs)} differ, "
				+ $"{result.Count(CompareState.OnlyInA)} only in A, {result.Count(CompareState.OnlyInB)} only in B; "
				+ $"{clock.Elapsed.TotalSeconds:0.0} s.");
			return result;
		}

		/// <summary>
		/// Both sides of one file for the side-by-side view: a page as its run pruned it, or with
		/// <paramref name="asSaved"/> as it was saved.
		/// </summary>
		public static FileSides Load(string a, string b, ComparedFile file, bool asSaved)
		{
			string? PathOf(string snapshot, string name)
			{
				if (file.IsPage && !asSaved && PrunedPath(snapshot, name) is { } pruned)
				{
					return pruned;
				}

				return PathIn(snapshot, name);
			}

			var pathA = PathOf(a, file.FileA);
			var pathB = PathOf(b, file.Name);
			long bytesA = SizeOf(pathA), bytesB = SizeOf(pathB);
			if (bytesA > MaxShownBytes || bytesB > MaxShownBytes)
			{
				return new FileSides(false, null, null, bytesA, bytesB, HashOf(pathA), HashOf(pathB),
					$"Larger than {MaxShownBytes / 1048576} MB: compared, but shown by size and checksum only.");
			}

			try
			{
				var contentA = pathA is null ? null : File.ReadAllBytes(pathA);
				var contentB = pathB is null ? null : File.ReadAllBytes(pathB);
				if (!file.IsPage && (LooksBinary(contentA) || LooksBinary(contentB)))
				{
					return new FileSides(false, null, null, bytesA, bytesB, HashOf(contentA), HashOf(contentB), "Not a text file: shown by size and checksum.");
				}

				// A pruned page is written as UTF-8; a saved file is read as the analysis reads it.
				string? Text(byte[]? content, string? path) => content is null ? null
					: path is not null && IsPrunedPath(path) ? new UTF8Encoding(false).GetString(content)
					: Decode(content);
				return new FileSides(true, Text(contentA, pathA), Text(contentB, pathB), bytesA, bytesB, null, null, file.Note);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return new FileSides(false, null, null, bytesA, bytesB, null, null, $"Could not be read: {ex.Message}");
			}
		}

		/// <summary>The text of a saved file, decoded as the analysis decodes pages.</summary>
		internal static string Decode(byte[] bytes) => DetectEncoding.FromBytes(bytes).GetString(bytes);

		/// <summary>The same bytes, read in blocks: a large file is never held whole.</summary>
		private static bool? SameBytes(string pathA, string pathB)
		{
			try
			{
				using var streamA = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
				using var streamB = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
				if (streamA.Length != streamB.Length)
				{
					return false;
				}

				var bufferA = new byte[1 << 16];
				var bufferB = new byte[1 << 16];
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
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Logger.LogWarning($"Comparison: a file could not be read — {ex.Message}");
				return null;
			}
		}

		/// <summary>The files under a download folder by their path inside it, and the sidecars left out.</summary>
		private static (Dictionary<string, string> Files, int Sidecars) FilesOf(string download)
		{
			var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			int sidecars = 0;
			foreach (var path in Directory.EnumerateFiles(download, "*", SearchOption.AllDirectories))
			{
				if (path.EndsWith(HeaderSidecar.HeaderSidecarExtension, StringComparison.OrdinalIgnoreCase))
				{
					sidecars++;
					continue;
				}

				files[Path.GetRelativePath(download, path).Replace('\\', '/')] = path;
			}

			return (files, sidecars);
		}

		/// <summary>The saved files' addresses from a snapshot's crawler index, added to <paramref name="into"/>.</summary>
		private static Dictionary<string, string> UrlsOf(string snapshot, Dictionary<string, string>? into)
		{
			var urls = into ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var index = Path.Combine(snapshot, LogFileNames.CrawlerIndex);
			if (!File.Exists(index))
			{
				return urls;
			}

			try
			{
				foreach (var line in File.ReadLines(index, Encoding.UTF8))
				{
					var parts = line.Split('|', StringSplitOptions.TrimEntries);
					if (parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0)
					{
						urls[parts[0].TrimStart('﻿')] = parts[1];
					}
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Logger.LogWarning($"Comparison: could not read a crawler index — {ex.Message}");
			}

			return urls;
		}

		private enum Noise { None, BlankLines, CacheBusters, Tokens }

		/// <summary>
		/// Whether two pruned pages (both written as UTF-8 by the prune) differ only in blank lines
		/// (<see cref="BlankLines"/>), only in those and cache busters, or only in those, cache busters
		/// and per-delivery tokens; <see cref="Noise.None"/> otherwise. Each step sets aside what the
		/// one before did, and the page is counted under the first that makes the two the same.
		/// </summary>
		private static Noise SameButNoise(string pageA, string pageB)
		{
			try
			{
				var blankA = BlankLines.Neutral(File.ReadAllText(pageA, Encoding.UTF8));
				var blankB = BlankLines.Neutral(File.ReadAllText(pageB, Encoding.UTF8));
				if (string.Equals(blankA, blankB, StringComparison.Ordinal))
				{
					return Noise.BlankLines;
				}

				var textA = CacheBusters.Neutral(blankA);
				var textB = CacheBusters.Neutral(blankB);
				return string.Equals(textA, textB, StringComparison.Ordinal) ? Noise.CacheBusters
					: string.Equals(DeliveryTokens.Neutral(textA), DeliveryTokens.Neutral(textB), StringComparison.Ordinal) ? Noise.Tokens
					: Noise.None;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return Noise.None;
			}
		}

		/// <summary>
		/// Pairs of (index only in A, index only in B) whose addresses are equal with their cache
		/// busters set aside — only where exactly one file on each side shares that address.
		/// </summary>
		private static List<(int A, int B)> PairAcrossCacheBusters(string[] names, CompareState[] states, Dictionary<string, string> urls)
		{
			var onlyA = new Dictionary<string, List<int>>(StringComparer.Ordinal);
			var onlyB = new Dictionary<string, List<int>>(StringComparer.Ordinal);
			for (int i = 0; i < names.Length; i++)
			{
				if (states[i] is not (CompareState.OnlyInA or CompareState.OnlyInB)
					|| !urls.TryGetValue(LeafName(names[i]), out var url))
				{
					continue;
				}

				var key = CacheBusters.Neutral(url);
				if (string.Equals(key, url, StringComparison.Ordinal))
				{
					continue; // no cache buster: nothing to pair across
				}

				var side = states[i] == CompareState.OnlyInA ? onlyA : onlyB;
				if (!side.TryGetValue(key, out var list))
				{
					side[key] = list = [];
				}

				list.Add(i);
			}

			var pairs = new List<(int, int)>();
			foreach (var (key, inA) in onlyA)
			{
				if (inA.Count == 1 && onlyB.TryGetValue(key, out var inB) && inB.Count == 1)
				{
					pairs.Add((inA[0], inB[0]));
				}
			}

			return pairs;
		}

		/// <summary>A snapshot's pruned pages by file name (the prune writes them flat, under their saved names).</summary>
		private static Dictionary<string, string> PrunedOf(string snapshot)
		{
			var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var path in Directory.EnumerateFiles(Path.Combine(snapshot, PrunedFolder)))
			{
				pages[Path.GetFileName(path)] = path;
			}

			return pages;
		}

		private static string? PrunedPath(string snapshot, string name)
		{
			var path = Path.Combine(snapshot, PrunedFolder, LeafName(name));
			return File.Exists(path) ? path : null;
		}

		private static bool IsPrunedPath(string path) =>
			string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), PrunedFolder, StringComparison.OrdinalIgnoreCase);

		private static string LeafName(string name)
		{
			int slash = name.LastIndexOf('/');
			return slash >= 0 ? name[(slash + 1)..] : name;
		}

		private static string? PathIn(string snapshot, string name)
		{
			var path = Path.Combine(snapshot, DownloadFolder, name.Replace('/', Path.DirectorySeparatorChar));
			return File.Exists(path) ? path : null;
		}

		private static long SizeOf(string? path)
		{
			try
			{
				return path is null ? 0 : new FileInfo(path).Length;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return 0;
			}
		}

		/// <summary>A NUL byte in the first 8 KB: not text.</summary>
		internal static bool LooksBinary(byte[]? content) =>
			content is not null && content.AsSpan(0, Math.Min(content.Length, 8192)).IndexOf((byte)0) >= 0;

		private static string? HashOf(byte[]? content) => content is null ? null : Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

		private static string? HashOf(string? path)
		{
			if (path is null)
			{
				return null;
			}

			try
			{
				using var stream = File.OpenRead(path);
				return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}

		/// <summary>The folder itself, a link followed to where it points.</summary>
		private static string Resolved(string path)
		{
			var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var info = new DirectoryInfo(full);
			if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
			{
				full = Path.GetFullPath(target.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			}

			return full;
		}
	}
}
