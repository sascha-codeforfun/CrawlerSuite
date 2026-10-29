using System.Text;
using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>Comparing two snapshots: the pair guard, the states, pages as pruned, and both sides of a file.</summary>
	[Collection("Logger")]
	public class SnapshotComparisonTests : IDisposable
	{
		private readonly string root = Path.Combine(Path.GetTempPath(), "comparison-" + Guid.NewGuid().ToString("N"));
		private readonly string a;
		private readonly string b;

		public SnapshotComparisonTests()
		{
			a = Path.Combine(root, "https___site_test", "2026-01-01-10-00-00");
			b = Path.Combine(root, "https___site_test", "2026-01-02-10-00-00");
			foreach (var snapshot in new[] { a, b })
			{
				Directory.CreateDirectory(Path.Combine(snapshot, SnapshotComparison.DownloadFolder));
				Directory.CreateDirectory(Path.Combine(snapshot, SnapshotComparison.PrunedFolder));
			}
		}

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

		private static void Save(string snapshot, string name, string text) => Save(snapshot, name, Encoding.UTF8.GetBytes(text));

		private static void Save(string snapshot, string name, byte[] bytes)
		{
			var path = Path.Combine(snapshot, SnapshotComparison.DownloadFolder, name);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, bytes);
		}

		/// <summary>A page as saved and as its run pruned it.</summary>
		private static void Page(string snapshot, string name, string saved, string pruned)
		{
			Save(snapshot, name, saved);
			File.WriteAllText(Path.Combine(snapshot, SnapshotComparison.PrunedFolder, name), pruned, new UTF8Encoding(false));
		}

		private ComparisonResult Run() => SnapshotComparison.Run(a, b);

		private static CompareState StateOf(ComparisonResult result, string name) =>
			result.Listed.FirstOrDefault(f => f.Name == name)?.State ?? CompareState.Identical;

		[Fact]
		public void CheckPair_RefusesTheSameSnapshot_AlsoSpelledAnotherWay()
		{
			Assert.Null(SnapshotComparison.CheckPair(a, b));
			Assert.Equal("Choose a snapshot for A and one for B.", SnapshotComparison.CheckPair(a, null));
			Assert.Equal("A and B are the same snapshot. Choose two different ones.", SnapshotComparison.CheckPair(a, a));
			Assert.Equal("A and B are the same snapshot. Choose two different ones.",
				SnapshotComparison.CheckPair(a, a + Path.DirectorySeparatorChar));
			Assert.Equal("A and B are the same snapshot. Choose two different ones.",
				SnapshotComparison.CheckPair(a, Path.Combine(a, "..", Path.GetFileName(a))));
			Assert.Throws<InvalidOperationException>(() => SnapshotComparison.Run(a, a));
		}

		/// <summary>A one-page PDF with its stamps and the word on the page.</summary>
		private static byte[] PdfOf(string word, string modified, int first)
		{
			var content = $"BT ({word}) Tj ET";
			var text = $"%PDF-1.7\n{first} 0 obj\n<< /Type /Catalog /Pages {first + 1} 0 R >>\nendobj\n"
				+ $"{first + 1} 0 obj\n<< /Type /Pages /Kids [{first + 2} 0 R] /Count 1 >>\nendobj\n"
				+ $"{first + 2} 0 obj\n<< /Type /Page /Parent {first + 1} 0 R /Contents {first + 3} 0 R >>\nendobj\n"
				+ $"{first + 3} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"
				+ $"{first + 4} 0 obj\n<< /Title (Ice) /ModDate ({modified}) >>\nendobj\n"
				+ $"trailer\n<< /Root {first} 0 R /Info {first + 4} 0 R >>\n%%EOF\n";
			return Encoding.Latin1.GetBytes(text);
		}

		[Fact]
		public void Run_APdfWrittenOutAgainWithTheSameContent_IsIdentical_AndCounted()
		{
			Save(a, "doc1.pdf", PdfOf("ice", "D:20260101000000Z", 1));
			Save(b, "doc1.pdf", PdfOf("ice", "D:20260102000000Z", 20));
			Save(a, "doc2.pdf", PdfOf("ice", "D:20260101000000Z", 1));
			Save(b, "doc2.pdf", PdfOf("water", "D:20260102000000Z", 1));

			var result = Run();
			Assert.Equal(CompareState.Identical, StateOf(result, "doc1.pdf"));
			Assert.Equal(CompareState.Differs, StateOf(result, "doc2.pdf"));
			Assert.Equal(1, result.RegeneratedPdfs);
		}

		[Fact]
		public void Run_APageThatDiffersOnlyInPerDeliveryTokens_IsIdentical_AndCounted()
		{
			Page(a, "login.html", "<html>saved</html>", "<form name=\"3fa9c0b17de24e65\"><input name=\"QwErTyIcEpLaNkSa\"></form><p>ice</p>");
			Page(b, "login.html", "<html>saved</html>", "<form name=\"8e01d5c2b94af37a\"><input name=\"FrOsTyMoRnInGvAp\"></form><p>ice</p>");
			Page(a, "edited.html", "<html>saved</html>", "<form name=\"3fa9c0b17de24e65\"></form><p>ice</p>");
			Page(b, "edited.html", "<html>saved</html>", "<form name=\"8e01d5c2b94af37a\"></form><p>water</p>");

			var result = Run();
			Assert.Equal(CompareState.Identical, StateOf(result, "login.html"));
			Assert.Equal(CompareState.Differs, StateOf(result, "edited.html"));
			Assert.Equal(1, result.IdenticalOnlyTokens);
		}

		[Fact]
		public void Run_APageThatDiffersOnlyInBlankLines_IsIdentical_AndCounted()
		{
			// A removed part left a tabs-only line in A, and one blank line fewer in B.
			Page(a, "box.html", "<html>saved A</html>", "<div class=\"box\">\n\n\t\t\n<i hidden></i>\n\n</div>\n");
			Page(b, "box.html", "<html>saved B</html>", "<div class=\"box\">\n\n<i hidden></i>\n</div>\n");
			Page(a, "edited.html", "<html>saved</html>", "<p>ice</p>\n\t\t\n<p>water</p>\n");
			Page(b, "edited.html", "<html>saved</html>", "<p>ice</p>\n<p>vapour</p>\n");

			var result = Run();
			Assert.Equal(CompareState.Identical, StateOf(result, "box.html"));
			Assert.Equal(CompareState.Differs, StateOf(result, "edited.html"));
			Assert.Equal(1, result.IdenticalOnlyBlankLines);
			Assert.Equal(0, result.IdenticalOnlyPruned);
		}

		[Fact]
		public void Run_BlankLinesAndCacheBusters_AreCountedUnderCacheBusters()
		{
			Page(a, "ice.html", "x", "<img src=\"/i/image.img.png/1701234567890.png\" alt=\"ice\">\n\t\n");
			Page(b, "ice.html", "x", "<img src=\"/i/image.img.png/1712345678901.png\" alt=\"ice\">\n");

			var result = Run();
			Assert.Equal(CompareState.Identical, StateOf(result, "ice.html"));
			Assert.Equal((0, 1), (result.IdenticalOnlyBlankLines, result.IdenticalOnlyCacheBusters));
		}

		[Fact]
		public void Run_BlankLinesInAFileThatIsNotAPage_StillCount()
		{
			Save(a, "style.css", "p { color: blue; }\n\n");
			Save(b, "style.css", "p { color: blue; }\n");

			Assert.Equal(CompareState.Differs, StateOf(Run(), "style.css"));
		}

		[Fact]
		public void CheckPair_ASnapshotWithoutSavedFilesOrPrunedPages_IsRefused()
		{
			var empty = Path.Combine(root, "https___site_test", "2026-01-03-10-00-00");
			Directory.CreateDirectory(empty);
			Assert.Equal("Snapshot B (2026-01-03-10-00-00) has no saved files (no download folder).", SnapshotComparison.CheckPair(a, empty));

			Directory.CreateDirectory(Path.Combine(empty, SnapshotComparison.DownloadFolder));
			Assert.StartsWith("Snapshot B (2026-01-03-10-00-00) has no pruned pages", SnapshotComparison.CheckPair(a, empty));
		}

		[Fact]
		public void SnapshotsOf_ListsSnapshotFoldersOnly_OldestFirst()
		{
			Directory.CreateDirectory(Path.Combine(root, "https___site_test", "notes"));
			Assert.Equal([a, b], SnapshotComparison.SnapshotsOf(Path.Combine(root, "https___site_test")));
			Assert.Empty(SnapshotComparison.SnapshotsOf(Path.Combine(root, "missing")));
		}

		[Fact]
		public void Run_StatesOfTheSet_IdenticalCountedNotListed()
		{
			Page(a, "ice.html", "<p>ice</p>", "<p>ice</p>");
			Page(b, "ice.html", "<p>ice</p>", "<p>ice</p>");
			Page(a, "water.html", "<p>water</p>", "<p>water</p>");
			Page(b, "water.html", "<p>steam</p>", "<p>steam</p>");
			Save(a, "frost.svg", "<svg/>");
			Save(b, "vapour.png", [1, 2, 3]);
			Save(a, "logo.png", [1, 2, 3]);
			Save(b, "logo.png", [1, 2, 3]);

			var result = Run();
			Assert.Equal(["frost.svg", "vapour.png", "water.html"], result.Listed.Select(f => f.Name).ToArray());
			Assert.Equal(CompareState.Differs, StateOf(result, "water.html"));
			Assert.Equal(CompareState.OnlyInA, StateOf(result, "frost.svg"));
			Assert.Equal(CompareState.OnlyInB, StateOf(result, "vapour.png"));
			Assert.Equal((1, 1), (result.IdenticalPages, result.IdenticalAssets));
			Assert.Equal((4, 4), (result.FilesA, result.FilesB));
			Assert.True(result.Listed.Single(f => f.Name == "water.html").IsPage);
			Assert.False(result.Listed.Single(f => f.Name == "frost.svg").IsPage);
		}

		[Fact]
		public void Run_PagesAreComparedAsPruned_BoilerplateDifferencesDoNotCount()
		{
			// The saved pages differ in their navigation (cut by the prune) and a salt (an attribute part).
			Page(a, "ice.html", "<nav>one</nav><body data-salt=\"1\"><p>ice</p></body>", "<body><p>ice</p></body>");
			Page(b, "ice.html", "<nav>one two</nav><body data-salt=\"2\"><p>ice</p></body>", "<body><p>ice</p></body>");
			Page(a, "water.html", "<p>water</p>", "<p>water</p>");
			Page(b, "water.html", "<p>water</p>", "<p>water</p>");

			var result = Run();
			Assert.Empty(result.Listed);
			Assert.Equal((2, 1), (result.IdenticalPages, result.IdenticalOnlyPruned));
		}

		[Fact]
		public void Run_EveryDifferenceOfThePrunedPagesCounts_BlanksAndLineEndsIncluded()
		{
			Page(a, "blank.html", "x", "<p>ice</p>\n");
			Page(b, "blank.html", "x", "<p>ice</p> \n");
			Page(a, "crlf.html", "x", "<p>ice</p>\r\n");
			Page(b, "crlf.html", "x", "<p>ice</p>\n");

			var result = Run();
			Assert.Equal(CompareState.Differs, StateOf(result, "blank.html"));
			Assert.Equal(CompareState.Differs, StateOf(result, "crlf.html"));
		}

		[Fact]
		public void Run_APagePrunedInOneSnapshotOnly_Differs_AndSaysSo()
		{
			Page(a, "ice.html", "<p>ice</p>", "<p>ice</p>");
			Save(b, "ice.html", "<p>ice</p>");

			var file = Assert.Single(Run().Listed);
			Assert.Equal(CompareState.Differs, file.State);
			Assert.True(file.IsPage);
			Assert.StartsWith("Pruned in A only", file.Note);
		}

		[Fact]
		public void Run_HeaderSidecars_AreLeftOutOfTheSet()
		{
			Page(a, "ice.html", "<p>ice</p>", "<p>ice</p>");
			Page(b, "ice.html", "<p>ice</p>", "<p>ice</p>");
			Save(a, "ice.html" + HeaderSidecar.HeaderSidecarExtension, "Date: one");
			Save(b, "ice.html" + HeaderSidecar.HeaderSidecarExtension, "Date: two");
			Save(b, "only.png" + HeaderSidecar.HeaderSidecarExtension, "Date: three");

			var result = Run();
			Assert.Empty(result.Listed);
			Assert.Equal(3, result.Sidecars);
			Assert.Equal((1, 1), (result.FilesA, result.FilesB));
		}

		[Fact]
		public void Run_NamesTheAddress_FromEitherCrawlerIndex()
		{
			Save(a, "frost.svg", "<svg/>");
			Save(b, "vapour.svg", "<svg/>");
			File.WriteAllLines(Path.Combine(a, LogFileNames.CrawlerIndex), ["frost.svg|https://site.test/frost.svg|discovery"]);
			File.WriteAllLines(Path.Combine(b, LogFileNames.CrawlerIndex), ["vapour.svg|https://site.test/vapour.svg|discovery"]);

			var result = Run();
			Assert.Equal("https://site.test/frost.svg", result.Listed.Single(f => f.Name == "frost.svg").Url);
			Assert.Equal("https://site.test/vapour.svg", result.Listed.Single(f => f.Name == "vapour.svg").Url);
		}

		[Fact]
		public void Run_ReadsOnly_NothingInEitherSnapshotChanges()
		{
			Page(a, "ice.html", "<body data-x=\"1\">ice</body>", "<body>ice</body>");
			Page(b, "ice.html", "<body data-x=\"2\">ice</body>", "<body>ice!</body>");
			string Listing() => string.Join("\n", new[] { a, b }.SelectMany(s => Directory.EnumerateFileSystemEntries(s, "*", SearchOption.AllDirectories))
				.Order(StringComparer.Ordinal).Select(p => p + ":" + (File.Exists(p) ? Convert.ToHexString(File.ReadAllBytes(p)) : "dir")));
			var before = Listing();
			Run();
			Assert.Equal(before, Listing());
		}

		[Fact]
		public void Run_ReportsProgress_UpToTheWholeSet()
		{
			Save(a, "ice.svg", "<svg/>");
			Save(b, "water.svg", "<svg/>");
			var reports = new List<(int Done, int Total)>();
			SnapshotComparison.Run(a, b, new SyncProgress(reports));
			Assert.Equal((0, 2), reports[0]);
			Assert.Equal((2, 2), reports[^1]);
		}

		[Fact]
		public void Load_APage_AsPruned_OrAsSaved()
		{
			Page(a, "ice.html", "<nav>n</nav><body data-x=\"1\">ice</body>", "<body>ice</body>");
			Page(b, "ice.html", "<nav>m</nav><body data-x=\"2\">ice!</body>", "<body>ice!</body>");
			var file = Run().Listed.Single();

			var pruned = SnapshotComparison.Load(a, b, file, asSaved: false);
			Assert.True(pruned.IsText);
			Assert.Equal(("<body>ice</body>", "<body>ice!</body>"), (pruned.TextA, pruned.TextB));

			var saved = SnapshotComparison.Load(a, b, file, asSaved: true);
			Assert.Equal("<nav>n</nav><body data-x=\"1\">ice</body>", saved.TextA);
		}

		[Fact]
		public void Load_ABinaryFile_IsShownBySizeAndChecksum()
		{
			Save(a, "logo.png", [137, 80, 0, 1]);
			Save(b, "logo.png", [137, 80, 0, 2]);
			var file = Run().Listed.Single();

			var sides = SnapshotComparison.Load(a, b, file, asSaved: false);
			Assert.False(sides.IsText);
			Assert.Equal((4L, 4L), (sides.BytesA, sides.BytesB));
			Assert.NotEqual(sides.HashA, sides.HashB);
			Assert.Equal(64, sides.HashA!.Length);
		}

		[Fact]
		public void Load_AFileOnOneSide_HasNoTextOnTheOther()
		{
			Save(a, "frost.svg", "<svg/>");
			var file = Run().Listed.Single();
			var sides = SnapshotComparison.Load(a, b, file, asSaved: false);
			Assert.True(sides.IsText);
			Assert.Equal("<svg/>", sides.TextA);
			Assert.Null(sides.TextB);
		}

		[Fact]
		public void Run_APageDifferingOnlyInCacheBusters_IsIdentical_AndCounted()
		{
			Page(a, "ice.html", "x", "<img src=\"/i/image.img.png/1701234567890.png\" alt=\"ice\">");
			Page(b, "ice.html", "x", "<img src=\"/i/image.img.png/1712345678901.png\" alt=\"ice\">");
			Page(a, "water.html", "x", "<img src=\"/i/image.img.png/1701234567890.png\" alt=\"ice\">");
			Page(b, "water.html", "x", "<img src=\"/i/image.img.png/1712345678901.png\" alt=\"water\">");

			var result = Run();
			Assert.Equal(["water.html"], result.Listed.Select(f => f.Name).ToArray());
			Assert.Equal((1, 1), (result.IdenticalPages, result.IdenticalOnlyCacheBusters));
		}

		[Fact]
		public void Run_AFileSavedAgainUnderANewCacheBuster_IsPaired_SameBytesIdentical()
		{
			Save(a, "old.png", [1, 2, 3]);
			Save(b, "new.png", [1, 2, 3]);
			Save(a, "oldlib.js", "var ice;");
			Save(b, "newlib.js", "var water;");
			File.WriteAllLines(Path.Combine(a, LogFileNames.CrawlerIndex), [
				"old.png|https://site.test/i/image.img.png/1701234567890.png|discovery",
				"oldlib.js|https://site.test/lib/ice.min.0123456789abcdef0123.js|discovery"]);
			File.WriteAllLines(Path.Combine(b, LogFileNames.CrawlerIndex), [
				"new.png|https://site.test/i/image.img.png/1712345678901.png|discovery",
				"newlib.js|https://site.test/lib/ice.min.fedcba9876543210fedc.js|discovery"]);

			var result = Run();
			Assert.Equal(1, result.Resaved);
			var script = Assert.Single(result.Listed);
			Assert.Equal((CompareState.Differs, "newlib.js", "oldlib.js"), (script.State, script.Name, script.FileA));
			Assert.StartsWith("Saved again under a new cache buster; in A: https://site.test/lib/ice.min.0123456789abcdef0123.js", script.Note);

			var sides = SnapshotComparison.Load(a, b, script, asSaved: false);
			Assert.Equal(("var ice;", "var water;"), (sides.TextA, sides.TextB));
		}

		[Fact]
		public void Run_AnAmbiguousCacheBusterPair_StaysUnpaired()
		{
			Save(a, "one.png", [1]);
			Save(a, "two.png", [1]);
			Save(b, "three.png", [1]);
			File.WriteAllLines(Path.Combine(a, LogFileNames.CrawlerIndex), [
				"one.png|https://site.test/i/1701234567890.png|discovery",
				"two.png|https://site.test/i/1701234567891.png|discovery"]);
			File.WriteAllLines(Path.Combine(b, LogFileNames.CrawlerIndex), ["three.png|https://site.test/i/1712345678901.png|discovery"]);

			var result = Run();
			Assert.Equal(0, result.Resaved);
			Assert.Equal((2, 1), (result.Count(CompareState.OnlyInA), result.Count(CompareState.OnlyInB)));
		}

		/// <summary>Progress reported on the calling thread (Progress&lt;T&gt; would post to the thread pool).</summary>
		private sealed class SyncProgress(List<(int, int)> into) : IProgress<(int Done, int Total)>
		{
			public void Report((int Done, int Total) value) => into.Add(value);
		}
	}
}
