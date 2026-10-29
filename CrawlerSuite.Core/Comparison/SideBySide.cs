namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>What a row of the side-by-side view shows.</summary>
	public enum RowKind
	{
		/// <summary>The same line on both sides (for pages: perhaps but for a cache buster).</summary>
		Same,

		/// <summary>A line of A and a line of B that differ.</summary>
		Changed,

		/// <summary>A line only A has.</summary>
		OnlyA,

		/// <summary>A line only B has.</summary>
		OnlyB,

		/// <summary>Unchanged lines folded away (<see cref="SideRow.Hidden"/> of them).</summary>
		Gap,
	}

	/// <summary>A part of a cell's text that differs from the other side.</summary>
	public readonly record struct Mark(int Start, int Length);

	/// <summary>
	/// One row: A's line on the left, B's on the right (numbers 1-based, null for no line). On a
	/// changed row each part that differs is marked on its side. A line too long to show whole is
	/// cut around its first mark; <see cref="CutA"/> / <see cref="CutB"/> say so.
	/// </summary>
	public sealed record SideRow(RowKind Kind, int? LineA, string TextA, IReadOnlyList<Mark> MarksA, bool CutA,
		int? LineB, string TextB, IReadOnlyList<Mark> MarksB, bool CutB, int Hidden = 0);

	/// <summary>The rows of a side-by-side view and what they add up to.</summary>
	public sealed record SideBySideView(IReadOnlyList<SideRow> Rows, int Changed, int OnlyA, int OnlyB, int LinesA, int LinesB)
	{
		/// <summary>The rows that carry a difference, for moving from one to the next.</summary>
		public IReadOnlyList<int> Differences
		{
			get
			{
				var list = new List<int>();
				for (int i = 0; i < Rows.Count; i++)
				{
					if (Rows[i].Kind is RowKind.Changed or RowKind.OnlyA or RowKind.OnlyB
						&& (i == 0 || Rows[i - 1].Kind is RowKind.Same or RowKind.Gap))
					{
						list.Add(i);
					}
				}

				return list;
			}
		}
	}

	/// <summary>
	/// Builds the side-by-side rows of two texts from their line diff: deleted and inserted lines
	/// that stand together are paired as changed rows, and within a changed row each differing piece
	/// is marked (words and single signs compared in order). Unchanged stretches are folded to a few
	/// lines of context unless every line is wanted. With <c>setCacheBustersAside</c> two lines that
	/// differ only in cache busters or per-delivery tokens (<see cref="DeliveryTokens"/>) are the
	/// same, and neither is ever marked.
	/// </summary>
	public static class SideBySide
	{
		/// <summary>Unchanged lines kept on each side of a difference when folding.</summary>
		public const int Context = 3;

		/// <summary>The most characters a cell shows of one line.</summary>
		public const int MaxShown = 4000;

		/// <summary>Characters kept before the first mark when a long line is cut.</summary>
		private const int Lead = 200;

		/// <summary>The longest word a mark is widened over to start and end on whole words.</summary>
		private const int MaxWiden = 64;

		/// <summary>The most changed pieces searched for within one line; beyond, the changed middle is one mark.</summary>
		private const int MaxPieceEdits = 400;

		private static readonly IReadOnlyList<Mark> NoMarks = [];

		/// <summary>The view of A against B. Null text: that side has no such file (every line is the other side's own).</summary>
		public static SideBySideView Build(string? textA, string? textB, bool allLines, bool setCacheBustersAside = false)
		{
			var a = textA is null ? [] : LineDiff.Lines(textA);
			var b = textB is null ? [] : LineDiff.Lines(textB);
			var steps = LineDiff.Diff(a, b, key: setCacheBustersAside ? SetAside : null);
			var rows = new List<SideRow>(steps.Count);
			int changed = 0, onlyA = 0, onlyB = 0;

			int s = 0;
			while (s < steps.Count)
			{
				if (steps[s].Op == DiffOp.Equal)
				{
					rows.Add(SameRow(a, b, steps[s]));
					s++;
					continue;
				}

				// A block of deletes and inserts between two equal lines: pair them in order.
				var deleted = new List<int>();
				var inserted = new List<int>();
				while (s < steps.Count && steps[s].Op != DiffOp.Equal)
				{
					if (steps[s].Op == DiffOp.Delete)
					{
						deleted.Add(steps[s].A);
					}
					else
					{
						inserted.Add(steps[s].B);
					}

					s++;
				}

				int pairs = Math.Min(deleted.Count, inserted.Count);
				for (int i = 0; i < pairs; i++)
				{
					rows.Add(ChangedRow(a[deleted[i]], deleted[i], b[inserted[i]], inserted[i], setCacheBustersAside));
					changed++;
				}

				for (int i = pairs; i < deleted.Count; i++)
				{
					var (text, cut) = Shown(a[deleted[i]], keepLineEnd: true);
					rows.Add(new SideRow(RowKind.OnlyA, deleted[i] + 1, text, NoMarks, cut, null, string.Empty, NoMarks, false));
					onlyA++;
				}

				for (int i = pairs; i < inserted.Count; i++)
				{
					var (text, cut) = Shown(b[inserted[i]], keepLineEnd: true);
					rows.Add(new SideRow(RowKind.OnlyB, null, string.Empty, NoMarks, false, inserted[i] + 1, text, NoMarks, cut));
					onlyB++;
				}
			}

			return new SideBySideView(allLines ? rows : Fold(rows), changed, onlyA, onlyB, a.Length, b.Length);
		}

		/// <summary>
		/// The pieces a line is compared in: a run of letters and digits is one piece, every other
		/// character a piece of its own. Start offsets in the line.
		/// </summary>
		internal static List<(int Start, string Text)> Pieces(string line)
		{
			var pieces = new List<(int, string)>();
			int i = 0;
			while (i < line.Length)
			{
				int start = i;
				if (char.IsLetterOrDigit(line[i]))
				{
					while (i < line.Length && char.IsLetterOrDigit(line[i]))
					{
						i++;
					}
				}
				else
				{
					i++;
				}

				pieces.Add((start, line[start..i]));
			}

			return pieces;
		}

		/// <summary>A line with its cache busters and per-delivery tokens set aside: what two lines are compared by.</summary>
		internal static string SetAside(string line) => DeliveryTokens.Neutral(CacheBusters.Neutral(line));

		/// <summary>
		/// The marks of two differing lines: what they share at the start and the end stays plain;
		/// in between, the pieces are compared in order and each run of differing pieces is marked
		/// on its side. A cache buster set aside is never marked.
		/// </summary>
		internal static (List<Mark> A, List<Mark> B) Marks(string lineA, string lineB, bool setCacheBustersAside)
		{
			int prefix = 0;
			int limit = Math.Min(lineA.Length, lineB.Length);
			while (prefix < limit && lineA[prefix] == lineB[prefix])
			{
				prefix++;
			}

			int suffix = 0;
			while (suffix < limit - prefix && lineA[lineA.Length - 1 - suffix] == lineB[lineB.Length - 1 - suffix])
			{
				suffix++;
			}

			// Widen the middle to whole pieces, so a word is compared as a word.
			// (The start is shared by both lines, and so is the end: widening reads either.) A run
			// longer than a word is not widened over: a page squeezed onto one line would become one piece.
			int from = prefix;
			while (from > 0 && prefix - from < MaxWiden && char.IsLetterOrDigit(lineA[from - 1]))
			{
				from--;
			}

			bool wholeWords = !(from > 0 && char.IsLetterOrDigit(lineA[from - 1]));
			if (!wholeWords)
			{
				from = prefix;
			}

			int endA = lineA.Length - suffix, endB = lineB.Length - suffix;
			int widen = 0;
			while (wholeWords && endA + widen < lineA.Length && widen < MaxWiden && char.IsLetterOrDigit(lineA[endA + widen]))
			{
				widen++;
			}

			if (wholeWords && (endA + widen == lineA.Length || !char.IsLetterOrDigit(lineA[endA + widen])))
			{
				endA += widen;
				endB += widen;
			}

			var middleA = Pieces(lineA[from..endA]);
			var middleB = Pieces(lineB[from..endB]);
			string Key(List<(int Start, string Text)> pieces, int i, string line) =>
				setCacheBustersAside
				&& CacheBusters.IsOne(pieces[i].Text, from + pieces[i].Start > 0 ? line[from + pieces[i].Start - 1] : '\0', line[(from + pieces[i].Start + pieces[i].Text.Length)..Math.Min(line.Length, from + pieces[i].Start + pieces[i].Text.Length + 8)])
					? "\u0001" + CacheBusters.Placeholder
					: setCacheBustersAside && DeliveryTokens.IsOne(pieces[i].Text)
						? "\u0001" + DeliveryTokens.Placeholder
						: pieces[i].Text;

			var keysA = middleA.Select((_, i) => Key(middleA, i, lineA)).ToList();
			var keysB = middleB.Select((_, i) => Key(middleB, i, lineB)).ToList();
			var steps = LineDiff.Diff(keysA, keysB, MaxPieceEdits);

			var marksA = new List<Mark>();
			var marksB = new List<Mark>();
			void Extend(List<Mark> marks, int start, int length)
			{
				if (marks.Count > 0 && marks[^1].Start + marks[^1].Length == start)
				{
					marks[^1] = new Mark(marks[^1].Start, marks[^1].Length + length);
				}
				else
				{
					marks.Add(new Mark(start, length));
				}
			}

			foreach (var step in steps)
			{
				if (step.Op == DiffOp.Delete)
				{
					Extend(marksA, from + middleA[step.A].Start, middleA[step.A].Text.Length);
				}
				else if (step.Op == DiffOp.Insert)
				{
					Extend(marksB, from + middleB[step.B].Start, middleB[step.B].Text.Length);
				}
			}

			// Nothing but cache busters, or a change of length zero on one side: mark where it stands.
			if (marksA.Count == 0 && marksB.Count == 0 && !setCacheBustersAside)
			{
				marksA.Add(new Mark(prefix, lineA.Length - prefix - suffix));
				marksB.Add(new Mark(prefix, lineB.Length - prefix - suffix));
			}

			return (marksA, marksB);
		}

		private static SideRow SameRow(string[] a, string[] b, DiffStep step)
		{
			var (textA, cutA) = Shown(a[step.A], keepLineEnd: false);
			var (textB, cutB) = Shown(b[step.B], keepLineEnd: false);
			return new SideRow(RowKind.Same, step.A + 1, textA, NoMarks, cutA, step.B + 1, textB, NoMarks, cutB);
		}

		private static SideRow ChangedRow(string lineA, int indexA, string lineB, int indexB, bool setCacheBustersAside)
		{
			var (marksA, marksB) = Marks(lineA, lineB, setCacheBustersAside);
			var (textA, shownA, cutA) = ShownMarked(lineA, marksA);
			var (textB, shownB, cutB) = ShownMarked(lineB, marksB);
			return new SideRow(RowKind.Changed, indexA + 1, textA, shownA, cutA, indexB + 1, textB, shownB, cutB);
		}

		/// <summary>
		/// A line as a cell shows it. A line end ('\r') is dropped on unchanged rows and kept on
		/// changed ones, where it may be the difference. Too long a line is cut.
		/// </summary>
		private static (string Text, bool Cut) Shown(string line, bool keepLineEnd)
		{
			if (!keepLineEnd && line.EndsWith('\r'))
			{
				line = line[..^1];
			}

			return line.Length <= MaxShown ? (line, false) : (line[..MaxShown], true);
		}

		/// <summary>A changed line, cut around its first mark when it is too long, with the marks moved and clipped to match.</summary>
		private static (string Text, IReadOnlyList<Mark> Marks, bool Cut) ShownMarked(string line, List<Mark> marks)
		{
			if (line.Length <= MaxShown)
			{
				return (line, marks, false);
			}

			int from = marks.Count == 0 ? 0 : Math.Max(0, marks[0].Start - Lead);
			int length = Math.Min(MaxShown, line.Length - from);
			var shown = new List<Mark>();
			foreach (var mark in marks)
			{
				int start = Math.Max(mark.Start, from);
				int end = Math.Min(mark.Start + mark.Length, from + length);
				if (end > start)
				{
					shown.Add(new Mark(start - from, end - start));
				}
			}

			return (line.Substring(from, length), shown, true);
		}

		/// <summary>Unchanged stretches longer than twice the context fold to a gap row between their ends.</summary>
		private static List<SideRow> Fold(List<SideRow> rows)
		{
			var folded = new List<SideRow>(rows.Count);
			int i = 0;
			while (i < rows.Count)
			{
				if (rows[i].Kind != RowKind.Same)
				{
					folded.Add(rows[i]);
					i++;
					continue;
				}

				int end = i;
				while (end < rows.Count && rows[end].Kind == RowKind.Same)
				{
					end++;
				}

				bool atStart = i == 0, atEnd = end == rows.Count;
				int keepBefore = atStart ? 0 : Context;   // after a difference
				int keepAfter = atEnd ? 0 : Context;      // before the next one
				int run = end - i;
				if (run <= keepBefore + keepAfter + 1)
				{
					folded.AddRange(rows.GetRange(i, run));
				}
				else
				{
					folded.AddRange(rows.GetRange(i, keepBefore));
					folded.Add(new SideRow(RowKind.Gap, null, string.Empty, NoMarks, false, null, string.Empty, NoMarks, false,
						run - keepBefore - keepAfter));
					folded.AddRange(rows.GetRange(end - keepAfter, keepAfter));
				}

				i = end;
			}

			return folded;
		}
	}
}
