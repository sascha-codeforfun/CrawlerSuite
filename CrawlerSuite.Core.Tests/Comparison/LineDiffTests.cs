using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>The line diff and the side-by-side rows built from it.</summary>
	public class LineDiffTests
	{
		private static string Ops(string[] a, string[] b) =>
			string.Concat(LineDiff.Diff(a, b).Select(s => s.Op switch { DiffOp.Equal => "=", DiffOp.Delete => "-", _ => "+" }));

		[Fact]
		public void Diff_OneLineChanged_IsADeleteAndAnInsert()
		{
			Assert.Equal("=-+=", Ops(["ice", "water", "vapour"], ["ice", "steam", "vapour"]));
		}

		[Fact]
		public void Diff_InsertAndDelete_AtEitherEnd()
		{
			Assert.Equal("+==", Ops(["ice", "water"], ["frost", "ice", "water"]));
			Assert.Equal("==-", Ops(["ice", "water", "vapour"], ["ice", "water"]));
			Assert.Equal("++", Ops([], ["ice", "water"]));
			Assert.Equal("--", Ops(["ice", "water"], []));
		}

		/// <summary>On small random texts: the steps rebuild both sides, and as few lines change as the longest common subsequence allows.</summary>
		[Fact]
		public void Diff_RandomTexts_RebuildBothSides_WithTheFewestEdits()
		{
			var random = new Random(7);
			for (int round = 0; round < 400; round++)
			{
				var a = Enumerable.Range(0, random.Next(0, 12)).Select(_ => ((char)('a' + random.Next(4))).ToString()).ToArray();
				var b = Enumerable.Range(0, random.Next(0, 12)).Select(_ => ((char)('a' + random.Next(4))).ToString()).ToArray();
				var steps = LineDiff.Diff(a, b);

				Assert.Equal(a, steps.Where(s => s.Op != DiffOp.Insert).Select(s => a[s.A]).ToArray());
				Assert.Equal(b, steps.Where(s => s.Op != DiffOp.Delete).Select(s => b[s.B]).ToArray());
				Assert.All(steps.Where(s => s.Op == DiffOp.Equal), s => Assert.Equal(a[s.A], b[s.B]));
				Assert.Equal(a.Length + b.Length - 2 * Lcs(a, b), steps.Count(s => s.Op != DiffOp.Equal));
			}
		}

		[Fact]
		public void Diff_BeyondTheEditLimit_IsOneReplacedBlock_StillRebuildingBothSides()
		{
			string[] a = ["same", "a1", "a2", "a3", "end"];
			string[] b = ["same", "b1", "b2", "end"];
			var steps = LineDiff.Diff(a, b, maxEdits: 2);
			Assert.Equal("=---++=", string.Concat(steps.Select(s => s.Op switch { DiffOp.Equal => "=", DiffOp.Delete => "-", _ => "+" })));
		}

		[Fact]
		public void Lines_KeepTheLineEnd_SoACrlfChangeIsAChange()
		{
			Assert.Equal(["ice\r", "water"], LineDiff.Lines("ice\r\nwater"));
			Assert.Equal("-+=", Ops(LineDiff.Lines("ice\r\nwater"), LineDiff.Lines("ice\nwater")));
		}

		[Fact]
		public void SideBySide_ChangedLine_MarksOnlyWhatDiffers()
		{
			var view = SideBySide.Build("<body data-x=\"11\">", "<body data-x=\"2\">", allLines: true);
			var row = Assert.Single(view.Rows);
			Assert.Equal(RowKind.Changed, row.Kind);
			Assert.Equal([new Mark(14, 2)], row.MarksA);
			Assert.Equal([new Mark(14, 1)], row.MarksB);
			Assert.Equal(1, view.Changed);
		}

		[Fact]
		public void SideBySide_ALineEndOnly_IsMarked()
		{
			var view = SideBySide.Build("ice\r\nwater", "ice\nwater", allLines: true);
			var row = view.Rows[0];
			Assert.Equal(RowKind.Changed, row.Kind);
			Assert.Equal("ice\r", row.TextA);
			Assert.Equal([new Mark(3, 1)], row.MarksA);
			Assert.Empty(row.MarksB);
		}

		[Fact]
		public void SideBySide_UnpairedLines_StandAlone()
		{
			var view = SideBySide.Build("ice\nwater\nvapour", "ice\nvapour\nsteam", allLines: true);
			Assert.Equal([RowKind.Same, RowKind.OnlyA, RowKind.Same, RowKind.OnlyB], view.Rows.Select(r => r.Kind).ToArray());
			Assert.Equal((1, 1), (view.OnlyA, view.OnlyB));
		}

		[Fact]
		public void SideBySide_Folding_KeepsContextAroundEachDifference()
		{
			var lines = Enumerable.Range(1, 20).Select(i => $"line {i}").ToList();
			var changed = lines.ToList();
			changed[9] = "line ten";
			var view = SideBySide.Build(string.Join('\n', lines), string.Join('\n', changed), allLines: false);

			Assert.Equal(RowKind.Gap, view.Rows[0].Kind);
			Assert.Equal(6, view.Rows[0].Hidden);
			Assert.Equal(["line 7", "line 8", "line 9"], view.Rows.Skip(1).Take(3).Select(r => r.TextA).ToArray());
			Assert.Equal(RowKind.Changed, view.Rows[4].Kind);
			Assert.Equal(RowKind.Gap, view.Rows[^1].Kind);
			Assert.Equal(7, view.Rows[^1].Hidden);
			Assert.Equal([4], view.Differences);

			Assert.Equal(20, SideBySide.Build(string.Join('\n', lines), string.Join('\n', changed), allLines: true).Rows.Count);
		}

		[Fact]
		public void SideBySide_AFileOnOneSide_IsAllThatSidesLines()
		{
			var view = SideBySide.Build("ice\nwater", null, allLines: true);
			Assert.All(view.Rows, r => Assert.Equal(RowKind.OnlyA, r.Kind));
			Assert.Equal(0, view.LinesB);
		}

		[Fact]
		public void SideBySide_ALongLine_IsCutAroundItsMark()
		{
			var head = new string('a', 10000);
			var view = SideBySide.Build(head + "ICE" + "tail", head + "WATER" + "tail", allLines: true);
			var row = view.Rows[0];
			Assert.True(row.CutA);
			Assert.True(row.TextA.Length <= SideBySide.MaxShown);
			Assert.Equal("ICE", row.TextA.Substring(row.MarksA[0].Start, row.MarksA[0].Length));
			Assert.Equal("WATER", row.TextB.Substring(row.MarksB[0].Start, row.MarksB[0].Length));
		}

		private static string[] Marked(string text, IReadOnlyList<Mark> marks) => marks.Select(m => text.Substring(m.Start, m.Length)).ToArray();

		[Fact]
		public void SideBySide_EachChangedPieceOfALine_IsMarkedOnItsOwn()
		{
			var view = SideBySide.Build("<body data-a=\"ice1\" class=\"water\" data-b=\"frost\">", "<body data-a=\"ice2\" class=\"water\" data-b=\"vapour\">", allLines: true);
			var row = Assert.Single(view.Rows);
			Assert.Equal(["ice1", "frost"], Marked(row.TextA, row.MarksA));
			Assert.Equal(["ice2", "vapour"], Marked(row.TextB, row.MarksB));
		}

		[Fact]
		public void SideBySide_AWordIsMarkedWhole_NotFromItsChangedLetter()
		{
			var row = SideBySide.Build("<p>steam rises</p>", "<p>stream rises</p>", allLines: true).Rows[0];
			Assert.Equal(["steam"], Marked(row.TextA, row.MarksA));
			Assert.Equal(["stream"], Marked(row.TextB, row.MarksB));
		}

		[Fact]
		public void SideBySide_ALineThatDiffersOnlyInCacheBusters_IsTheSame_WhenSetAside()
		{
			const string a = "<img src=\"/x/image.img.png/1701234567890.png\" alt=\"ice\">";
			const string b = "<img src=\"/x/image.img.png/1712345678901.png\" alt=\"ice\">";
			Assert.Equal(RowKind.Same, SideBySide.Build(a, b, allLines: true, setCacheBustersAside: true).Rows[0].Kind);

			var row = SideBySide.Build(a, b, allLines: true).Rows[0];
			Assert.Equal(RowKind.Changed, row.Kind);
			Assert.Equal(["1701234567890"], Marked(row.TextA, row.MarksA));
		}

		[Fact]
		public void SideBySide_ACacheBusterBesideARealChange_IsNotMarked()
		{
			const string a = "<img src=\"/x/image.img.jpg/1701234567890.jpg\" alt=\"ice\">";
			const string b = "<img src=\"/x/image.img.jpg/1712345678901.jpg\" alt=\"water\">";
			var row = SideBySide.Build(a, b, allLines: true, setCacheBustersAside: true).Rows[0];
			Assert.Equal(RowKind.Changed, row.Kind);
			Assert.Equal(["ice"], Marked(row.TextA, row.MarksA));
			Assert.Equal(["water"], Marked(row.TextB, row.MarksB));
		}

		private static int Lcs(string[] a, string[] b)
		{
			var t = new int[a.Length + 1, b.Length + 1];
			for (int i = a.Length - 1; i >= 0; i--)
			{
				for (int j = b.Length - 1; j >= 0; j--)
				{
					t[i, j] = a[i] == b[j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
				}
			}

			return t[0, 0];
		}
	}
}
