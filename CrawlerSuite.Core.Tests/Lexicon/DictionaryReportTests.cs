using System.Text;
using CrawlerSuite.Core.Lexicon;
using Xunit;

namespace CrawlerSuite.Core.Tests.Lexicon
{
	/// <summary>The Dictionaries page's reports (logs 15 and 35) and its in-place edits. Invented words only.</summary>
	[Collection("Logger")]
	public class DictionaryReportTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), "st-dicreport-" + Guid.NewGuid().ToString("N"));

		public DictionaryReportTests()
		{
			Directory.CreateDirectory(dir);
			Logger.Initialize(Path.Combine(dir, "test.log"));
		}

		public void Dispose()
		{
			try { Directory.Delete(dir, recursive: true); } catch { }
		}

		private string File(string name, params string[] lines)
		{
			var path = Path.Combine(dir, name);
			System.IO.File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		[Fact]
		public void Read_Log15_SectionsBecomeDictionaryAndReason()
		{
			var log = File("15.log",
				"// User dictionary (user.dic) — not found in any page:",
				"Alphaword",
				"",
				"// User dictionary (user.dic) — redundant (covered by prefix stripping):",
				"Examplix-Thing",
				"",
				"// Site dictionary (site.dic) — not found in any page:",
				"Betaword",
				"",
				"// In BOTH dictionaries — not found in any page (will be removed from both):",
				"Gammaword");
			var entries = DictionaryReport.Read(log);
			Assert.Equal(
				[
					new CleanupEntry("Alphaword", DictionaryKind.User, CleanupReason.NotUsed),
					new CleanupEntry("Examplix-Thing", DictionaryKind.User, CleanupReason.Redundant),
					new CleanupEntry("Betaword", DictionaryKind.Site, CleanupReason.NotUsed),
					new CleanupEntry("Gammaword", DictionaryKind.Both, CleanupReason.NotUsed),
				],
				entries);
		}

		[Fact]
		public void Log35_RoundTrips_WithItsDate()
		{
			var path = Path.Combine(dir, "35.log");
			var when = new DateTime(2026, 10, 12, 14, 30, 0);
			DictionaryReport.WriteNotNeeded(path, "user.dic", ["Deltaword", "Both1"], "site.dic", ["Epsword", "Both1"], when);
			Assert.Equal(when, DictionaryReport.FoundAt(path));
			Assert.Equal(
				[
					new CleanupEntry("Deltaword", DictionaryKind.User, CleanupReason.NotNeeded),
					new CleanupEntry("Epsword", DictionaryKind.Site, CleanupReason.NotNeeded),
					new CleanupEntry("Both1", DictionaryKind.Both, CleanupReason.NotNeeded),
				],
				DictionaryReport.Read(path));
		}

		[Fact]
		public void NotNeeded_KeepsPinnedOrphanedAndMissedEntries()
		{
			var dic = File("user.dic", "Unneeded", "!Pinned", "Orphan", "Stillflagged", "Partword", "Headstem");
			var missed = DictionaryReport.MissedWords(
				[File("11.log", "Stillflagged (de)", "Examplix-Partword (de)", "Headstem- (de)")],
				[File("29.log", "#header", "(no findings)")]);
			var result = DictionaryReport.NotNeeded(dic, new HashSet<string> { "Orphan" }, missed);
			Assert.Equal(new[] { "Unneeded" }, result);
		}

		[Fact]
		public void NotNeeded_LeavesOutNamesWithASpace()
		{
			// "Xyzzy- und Hausverband" is a name for the hyphen check, which a spell pass cannot judge.
			var dic = File("names.dic", "Unneeded", "Xyzzy- und Hausverband");
			var missed = DictionaryReport.MissedWords([File("11b.log", "Other (de)")], []);
			Assert.Equal(new[] { "Unneeded" }, DictionaryReport.NotNeeded(dic, new HashSet<string>(), missed));
		}

		[Fact]
		public void Editor_RemovesAndPins_InPlace_WithOneBackup()
		{
			var dic = File("edit.dic", "Aword", "Bword", "Cword");
			Assert.True(DictionaryEditor.Remove(dic, "Bword"));
			Assert.True(DictionaryEditor.Pin(dic, "Cword"));
			Assert.False(DictionaryEditor.Remove(dic, "Missing"));
			Assert.Equal(new[] { "Aword", "!Cword" }, System.IO.File.ReadAllLines(dic, Encoding.UTF8));
			Assert.True(System.IO.File.Exists(dic + ".1"));
			Assert.False(System.IO.File.Exists(dic + ".2")); // one backup per file per run of the app
		}

		[Fact]
		public void Editor_Adds_AtTheEnd_OnlyWhenNotThere_AndCreatesAMissingFile()
		{
			var dic = File("add.dic", "Aword", "!Pinword");
			Assert.True(DictionaryEditor.Add(dic, "Examplix-Store"));
			Assert.False(DictionaryEditor.Add(dic, "aword"));   // the lists ignore case
			Assert.False(DictionaryEditor.Add(dic, "Pinword")); // pinned counts as there
			Assert.Equal(new[] { "Aword", "!Pinword", "Examplix-Store" }, System.IO.File.ReadAllLines(dic, Encoding.UTF8));

			var fresh = Path.Combine(dir, "fresh.dic");
			Assert.True(DictionaryEditor.Add(fresh, "Examplix"));
			Assert.Equal(new[] { "Examplix" }, System.IO.File.ReadAllLines(fresh, Encoding.UTF8));
			Assert.False(System.IO.File.Exists(fresh + ".1")); // nothing to back up
		}

		[Fact]
		public void Editor_UndoOfAnAdd_RemovesOnlyThatEntry()
		{
			// The Spell findings page undoes a click by removing what Add reported as added
			// (trimmed); an entry that was already there was not added and is never removed.
			var dic = File("undo.dic", "Aword", "Xyzzy");
			var before = System.IO.File.ReadAllLines(dic, Encoding.UTF8);
			var added = new[] { " Qwzpart ", "xyzzy" }.Where(e => DictionaryEditor.Add(dic, e)).Select(e => e.Trim()).ToList();
			Assert.Equal(new[] { "Qwzpart" }, added);

			foreach (var entry in added)
			{
				Assert.True(DictionaryEditor.Remove(dic, entry));
			}

			Assert.Equal(before, System.IO.File.ReadAllLines(dic, Encoding.UTF8));
		}

		[Fact]
		public void SpellWordList_ReadsWordsLanguagesAndPageCounts()
		{
			var snap = Path.Combine(dir, "snap");
			Directory.CreateDirectory(snap);
			System.IO.File.WriteAllLines(Path.Combine(snap, "11-spell-errors-unique.log"),
				["Sicherhiets-Check (de)", "Examplix-Stroe (de, en)", "Leistnugs-Update (de)"], Encoding.UTF8);
			System.IO.File.WriteAllLines(Path.Combine(snap, "12-spell-errors-sources.log"),
				["https://example.test/a|Sicherhiets-Check (de)",
				 "https://example.test/b|Sicherhiets-Check (de) (meta[@name=description])",
				 "https://example.test/b|Sicherhiets-Check (de)",
				 "https://example.test/a|Examplix-Stroe (en)"], Encoding.UTF8);

			var words = SpellWordList.Read(snap);
			Assert.Equal(new[] { "Sicherhiets-Check", "Examplix-Stroe", "Leistnugs-Update" }, words.Select(w => w.Word).ToArray());
			Assert.Equal(new[] { 2, 1, 0 }, words.Select(w => w.Pages).ToArray());
			Assert.Equal(new[] { "de", "en" }, words[1].Languages.ToArray());
		}
	}
}
