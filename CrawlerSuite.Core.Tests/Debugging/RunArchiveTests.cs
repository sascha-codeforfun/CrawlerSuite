using System;
using System.IO;
using System.Linq;
using CrawlerSuite.Core.Debugging;
using CrawlerSuite.Core.Host;
using Xunit;

namespace CrawlerSuite.Core.Tests.Debugging
{
	/// <summary>Debug mode's run archive: naming, what is copied, the compare and the baseline.</summary>
	public class RunArchiveTests : IDisposable
	{
		private readonly string root = Path.Combine(Path.GetTempPath(), "runarchive-" + Guid.NewGuid().ToString("N"));

		public RunArchiveTests() => Directory.CreateDirectory(root);

		public void Dispose()
		{
			try
			{
				Directory.Delete(root, recursive: true);
			}
			catch (IOException)
			{
			}
		}

		private string Dir(params string[] parts)
		{
			var path = Path.Combine([root, .. parts]);
			Directory.CreateDirectory(path);
			return path;
		}

		private static void Write(string folder, string name, params string[] lines)
		{
			var path = Path.Combine(folder, name);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllLines(path, lines);
		}

		[Fact]
		public void NextRunName_CountsUpPerSnapshot_AcrossVersions()
		{
			var snapshot = Dir("site", "snap");
			Assert.Equal("2.0.41-001", RunArchive.NextRunName(snapshot, "2.0.41"));
			Dir("site", "snap", "2.0.40-001");
			Dir("site", "snap", "2.0.41-002");
			Dir("site", "snap", "notes");
			Assert.Equal("2.0.41-003", RunArchive.NextRunName(snapshot, "2.0.41"));
			Assert.Equal(["2.0.40-001", "2.0.41-002"], RunArchive.Runs(snapshot));
		}

		[Fact]
		public void Runs_AreOrderedByNumber_NotByName()
		{
			var snapshot = Dir("snap");
			Dir("snap", "2.0.9-010");
			Dir("snap", "2.0.41-002");
			Assert.Equal(["2.0.41-002", "2.0.9-010"], RunArchive.Runs(snapshot));
		}

		[Fact]
		public void SnapshotFolder_IsSiteThenSnapshot()
		{
			var folder = RunArchive.SnapshotFolder(Path.Combine(root, "debug"), Path.Combine(root, "crawls", "site-folder", "snapshot-1"));
			Assert.Equal(Path.Combine(root, "debug", "site-folder", "snapshot-1"), folder);
		}

		[Fact]
		public void CopyRunOutput_LeavesTheCrawlOut()
		{
			var snapshot = Dir("crawl");
			Write(snapshot, "11-spell-errors-unique.log", "a");
			Write(snapshot, "settings-usage.json", "{}");
			Write(Path.Combine(snapshot, "download"), "page.html", "<p>");
			Write(Path.Combine(snapshot, "oversized"), "big.bin", "x");
			Write(Path.Combine(snapshot, "derived"), "part.log", "b");

			var target = Path.Combine(root, "archive", "2.0.41-001");
			var (files, bytes) = RunArchive.CopyRunOutput(snapshot, target);

			Assert.Equal(3, files);
			Assert.True(bytes > 0);
			Assert.True(File.Exists(Path.Combine(target, "11-spell-errors-unique.log")));
			Assert.True(File.Exists(Path.Combine(target, "derived", "part.log")));
			Assert.False(Directory.Exists(Path.Combine(target, "download")));
			Assert.False(Directory.Exists(Path.Combine(target, "oversized")));
		}

		[Fact]
		public void Compare_TellsIdenticalDifferingOrderAndMissingApart()
		{
			var a = Dir("a");
			var b = Dir("b");
			Write(a, "01-same.log", "x", "y");
			Write(b, "01-same.log", "x", "y");
			Write(a, "11-words.log", "one", "two", "two");
			Write(b, "11-words.log", "two", "three", "one", "three");
			Write(a, "12-order.log", "p", "q");
			Write(b, "12-order.log", "q", "p");
			Write(a, "13-gone.log", "g");
			Write(b, "14-new.log", "n");
			Write(a, RunArchive.RunInfoFile, "run a");
			Write(b, RunArchive.RunInfoFile, "run b");
			Write(b, RunArchive.RunStepsFile, "steps");

			var result = RunArchive.Compare(a, b).ToDictionary(f => f.Name);

			Assert.Equal(5, result.Count); // the archive's own files are not compared
			Assert.Equal(RunArchive.FileState.Identical, result["01-same.log"].State);
			var words = result["11-words.log"];
			Assert.Equal(RunArchive.FileState.Differs, words.State);
			Assert.Equal(["two"], words.OnlyInA);
			Assert.Equal(["three", "three"], words.OnlyInB);
			Assert.Equal(RunArchive.FileState.OrderOnly, result["12-order.log"].State);
			Assert.Equal(RunArchive.FileState.OnlyInA, result["13-gone.log"].State);
			Assert.Equal(RunArchive.FileState.OnlyInB, result["14-new.log"].State);

			var summary = RunArchive.Summary(result.Values.OrderBy(f => f.Name).ToList());
			Assert.Equal("1 identical · 2 differ: 11, 12 · 1 gone: 13 · 1 new: 14", summary);
		}

		[Fact]
		public void Compare_ReadsTheManifests_AndOpensOnlyFilesWhoseHashDiffers()
		{
			var a = Dir("ma");
			var b = Dir("mb");
			Write(a, "11-x.log", "w");
			Write(b, "11-x.log", "w");
			Write(a, "12-y.log", "v");
			Write(b, "12-y.log", "u");

			Assert.Equal("1 identical · 1 differ: 12", RunArchive.Summary(RunArchive.Compare(a, b)));
			Assert.True(File.Exists(Path.Combine(a, RunArchive.ManifestFile)));
			Assert.Contains(File.ReadAllLines(Path.Combine(a, RunArchive.ManifestFile)), l => l.EndsWith(" 11-x.log", StringComparison.Ordinal));

			// The manifest decides: a file it lists with the same size and hash is not opened. Changing
			// a file's bytes without its size, then claiming the old hash, shows the list was used …
			var listed = File.ReadAllLines(Path.Combine(b, RunArchive.ManifestFile));
			File.WriteAllLines(Path.Combine(b, "11-x.log"), ["z"]);
			File.WriteAllLines(Path.Combine(b, RunArchive.ManifestFile), listed);
			Assert.Equal(RunArchive.FileState.Identical, RunArchive.Compare(a, b).Single(f => f.Name == "11-x.log").State);

			// … and a size that no longer matches makes the list be worked out again.
			File.WriteAllLines(Path.Combine(b, "11-x.log"), ["longer"]);
			Assert.Equal(RunArchive.FileState.Differs, RunArchive.Compare(a, b).Single(f => f.Name == "11-x.log").State);
		}

		[Fact]
		public void Compare_LeavesTheTimeOfAPruneOut()
		{
			var a = Dir("pa");
			var b = Dir("pb");
			Write(a, "prune-stamp.json", "{", "  \"Fingerprint\": \"f\",", "  \"PrunedAt\": \"2026-10-26T11:58:40Z\"", "}");
			Write(b, "prune-stamp.json", "{", "  \"Fingerprint\": \"f\",", "  \"PrunedAt\": \"2026-10-26T12:33:46Z\"", "}");

			Assert.Equal("all 1 files identical", RunArchive.Summary(RunArchive.Compare(a, b)));
		}

		[Fact]
		public void Compare_AllIdentical_SaysSo()
		{
			var a = Dir("a");
			var b = Dir("b");
			Write(a, "11-x.log", "w");
			Write(b, "11-x.log", "w");
			Write(Path.Combine(a, "sub"), "y.log", "v");
			Write(Path.Combine(b, "sub"), "y.log", "v");
			Assert.Equal("all 2 files identical", RunArchive.Summary(RunArchive.Compare(a, b)));
		}

		[Fact]
		public void Compare_LeavesTheTimeOfANoLongerNeededReportOut()
		{
			var a = Dir("a");
			var b = Dir("b");
			Write(a, LogFileNames.DictionaryNotNeeded, "# 35 — dictionary entries no longer needed · found 2026-01-01 10:00 by …", "entry");
			Write(b, LogFileNames.DictionaryNotNeeded, "# 35 — dictionary entries no longer needed · found 2026-01-02 11:30 by …", "entry");
			Assert.Equal(RunArchive.FileState.Identical, RunArchive.Compare(a, b).Single().State);
		}

		[Fact]
		public void Report_ListsTheDifferingLines_UpToTheLimit()
		{
			var a = Dir("a");
			var b = Dir("b");
			Write(a, "11-x.log", "kept", "old");
			Write(b, "11-x.log", "kept", "new");
			var report = RunArchive.Report("2.0.41-001", "2.0.41-002", RunArchive.Compare(a, b));
			Assert.Contains("A (earlier): 2.0.41-001", report);
			Assert.Contains("    - old", report);
			Assert.Contains("    + new", report);
			Assert.DoesNotContain(report.Split('\n'), l => l.StartsWith("    ", StringComparison.Ordinal) && l.Contains("kept", StringComparison.Ordinal));

			var many = Enumerable.Range(0, RunArchive.MaxListedLines + 5).Select(i => $"line {i}").ToArray();
			Write(b, "12-many.log", many);
			Write(a, "12-many.log");
			var big = RunArchive.Report("a", "b", RunArchive.Compare(a, b));
			Assert.Contains("    … 5 more", big);
		}

		[Fact]
		public void Baseline_IsMarkedReadAndReplaced()
		{
			var snapshot = Dir("snap");
			Assert.Null(RunArchive.Baseline(snapshot));
			Dir("snap", "2.0.41-001");
			Dir("snap", "2.0.41-002");
			RunArchive.MarkBaseline(snapshot, "2.0.41-001");
			Assert.Equal("2.0.41-001", RunArchive.Baseline(snapshot));
			RunArchive.MarkBaseline(snapshot, "2.0.41-002");
			Assert.Equal("2.0.41-002", RunArchive.Baseline(snapshot));
			Assert.Throws<DirectoryNotFoundException>(() => RunArchive.MarkBaseline(snapshot, "2.0.41-009"));

			// A baseline whose folder was cleared away by hand is no baseline.
			Directory.Delete(Path.Combine(snapshot, "2.0.41-002"));
			Assert.Null(RunArchive.Baseline(snapshot));
		}

		[Fact]
		public void Baseline_IsNotARun()
		{
			var snapshot = Dir("snap");
			Dir("snap", "2.0.41-001");
			RunArchive.MarkBaseline(snapshot, "2.0.41-001");
			Assert.Equal(["2.0.41-001"], RunArchive.Runs(snapshot));
			Assert.Equal("2.0.41-002", RunArchive.NextRunName(snapshot, "2.0.41"));
		}

		[Fact]
		public void Checksum_OfAMissingFile_IsNone()
		{
			Assert.Equal("none", RunArchive.Checksum(Path.Combine(root, "missing")));
			Write(root, "f.txt", "abc");
			Assert.Equal(64, RunArchive.Checksum(Path.Combine(root, "f.txt")).Length);
		}

		[Fact]
		public void RunPlan_HasADebugPhase_OnlyInDebugMode()
		{
			var off = RunPlan.Build(new Config { CmsContentList = null }, replay: true);
			Assert.DoesNotContain(off, p => p.Title == RunPhases.Debug);

			var on = RunPlan.Build(new Config { CmsContentList = null, DebugMode = true }, replay: true);
			var titles = on.Select(p => p.Title).ToList();
			Assert.Equal(titles.IndexOf(RunPhases.Complete) - 1, titles.IndexOf(RunPhases.Debug));
			Assert.Equal([RunSteps.RunArchive, RunSteps.RunCompare], on.Single(p => p.Title == RunPhases.Debug).Steps);
		}

		[Fact]
		public void DebugMode_IsASettingOnTheDebugTab_OffByDefault()
		{
			var info = CrawlerSuite.Core.Settings.SettingsCatalog.Find("DebugMode");
			Assert.NotNull(info);
			Assert.Equal("Debug", info.Tab);
			Assert.Equal(CrawlerSuite.Core.Settings.SettingsCatalog.LeversTab, info.Tab);
			Assert.False(new Config().DebugMode);
		}
	}
}
