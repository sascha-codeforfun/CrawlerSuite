namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Collections.Generic;

	/// <summary>One step of a line diff.</summary>
	public enum DiffOp
	{
		/// <summary>The line is in both: A's line <c>A</c> is B's line <c>B</c>.</summary>
		Equal,

		/// <summary>A's line <c>A</c> is not in B.</summary>
		Delete,

		/// <summary>B's line <c>B</c> is not in A.</summary>
		Insert,
	}

	/// <summary>One step: the operation and the line numbers (0-based; -1 for the side it does not touch).</summary>
	public readonly record struct DiffStep(DiffOp Op, int A, int B);

	/// <summary>
	/// The shortest line diff of two texts (Myers' algorithm), lines compared exactly — a line end
	/// or a blank is a difference. The common start and end are taken off first, so a small change
	/// in a long file costs little. When the texts differ in more than <see cref="MaxEdits"/> lines
	/// the middle is shown as one replaced block instead of searched further.
	/// </summary>
	public static class LineDiff
	{
		/// <summary>The most changed lines searched for a shortest diff.</summary>
		public const int MaxEdits = 2000;

		/// <summary>
		/// The lines of a text, split after each '\n'. A '\r' stays with its line, so a changed line
		/// end shows as a changed line; a text ending in '\n' has an empty last line.
		/// </summary>
		public static string[] Lines(string? text) => (text ?? string.Empty).Split('\n');

		/// <summary>
		/// The steps that turn <paramref name="a"/> into <paramref name="b"/>, in order. Two items are
		/// the same when their <paramref name="key"/>s are (by default the items themselves); an Equal
		/// step may then join two items that differ only in what the key sets aside.
		/// </summary>
		public static List<DiffStep> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxEdits = MaxEdits, Func<string, string>? key = null)
		{
			if (key is not null)
			{
				var keysA = new string[a.Count];
				var keysB = new string[b.Count];
				for (int i = 0; i < a.Count; i++)
				{
					keysA[i] = key(a[i]);
				}

				for (int i = 0; i < b.Count; i++)
				{
					keysB[i] = key(b[i]);
				}

				return Diff(keysA, keysB, maxEdits);
			}

			var steps = new List<DiffStep>(Math.Max(a.Count, b.Count) + 4);
			int start = 0;
			while (start < a.Count && start < b.Count && string.Equals(a[start], b[start], StringComparison.Ordinal))
			{
				steps.Add(new DiffStep(DiffOp.Equal, start, start));
				start++;
			}

			int endA = a.Count, endB = b.Count;
			while (endA > start && endB > start && string.Equals(a[endA - 1], b[endB - 1], StringComparison.Ordinal))
			{
				endA--;
				endB--;
			}

			// Lines as numbers: equal lines get equal numbers, so the search compares ints.
			var ids = new Dictionary<string, int>(StringComparer.Ordinal);
			int[] Ids(IReadOnlyList<string> lines, int from, int to)
			{
				var result = new int[to - from];
				for (int i = from; i < to; i++)
				{
					if (!ids.TryGetValue(lines[i], out var id))
					{
						id = ids.Count;
						ids[lines[i]] = id;
					}

					result[i - from] = id;
				}

				return result;
			}

			var x = Ids(a, start, endA);
			var y = Ids(b, start, endB);
			var middle = Middle(x, y, maxEdits);
			foreach (var step in middle)
			{
				steps.Add(new DiffStep(step.Op, step.A < 0 ? -1 : step.A + start, step.B < 0 ? -1 : step.B + start));
			}

			for (int i = endA, j = endB; i < a.Count; i++, j++)
			{
				steps.Add(new DiffStep(DiffOp.Equal, i, j));
			}

			return steps;
		}

		/// <summary>Myers' greedy search, the frontier saved before every round for the way back.</summary>
		private static List<DiffStep> Middle(int[] a, int[] b, int maxEdits)
		{
			int n = a.Length, m = b.Length;
			if (n == 0 || m == 0)
			{
				return Replaced(n, m);
			}

			int max = Math.Min(n + m, maxEdits);
			int offset = max + 1;
			var v = new int[2 * max + 3];
			var trace = new List<int[]>();
			int found = -1;
			for (int d = 0; d <= max && found < 0; d++)
			{
				// The frontier as round d starts (round d-1's ends), diagonals -d..d.
				var saved = new int[2 * d + 1];
				Array.Copy(v, offset - d, saved, 0, 2 * d + 1);
				trace.Add(saved);

				for (int k = -d; k <= d; k += 2)
				{
					int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
						? v[offset + k + 1]
						: v[offset + k - 1] + 1;
					int y = x - k;
					while (x < n && y < m && a[x] == b[y])
					{
						x++;
						y++;
					}

					v[offset + k] = x;
					if (x >= n && y >= m)
					{
						found = d;
						break;
					}
				}
			}

			if (found < 0)
			{
				return Replaced(n, m);
			}

			var reversed = new List<DiffStep>(n + m);
			int cx = n, cy = m;
			for (int d = found; d > 0; d--)
			{
				var saved = trace[d];
				int At(int k) => saved[k + d];
				int k0 = cx - cy;
				int prevK = k0 == -d || (k0 != d && At(k0 - 1) < At(k0 + 1)) ? k0 + 1 : k0 - 1;
				int prevX = At(prevK);
				int prevY = prevX - prevK;
				while (cx > prevX && cy > prevY)
				{
					cx--;
					cy--;
					reversed.Add(new DiffStep(DiffOp.Equal, cx, cy));
				}

				if (prevK == k0 + 1)
				{
					reversed.Add(new DiffStep(DiffOp.Insert, -1, prevY));
				}
				else
				{
					reversed.Add(new DiffStep(DiffOp.Delete, prevX, -1));
				}

				cx = prevX;
				cy = prevY;
			}

			while (cx > 0 && cy > 0)
			{
				cx--;
				cy--;
				reversed.Add(new DiffStep(DiffOp.Equal, cx, cy));
			}

			reversed.Reverse();
			return reversed;
		}

		/// <summary>Every line of A replaced by every line of B.</summary>
		private static List<DiffStep> Replaced(int n, int m)
		{
			var result = new List<DiffStep>(n + m);
			for (int i = 0; i < n; i++)
			{
				result.Add(new DiffStep(DiffOp.Delete, i, -1));
			}

			for (int j = 0; j < m; j++)
			{
				result.Add(new DiffStep(DiffOp.Insert, -1, j));
			}

			return result;
		}
	}
}
