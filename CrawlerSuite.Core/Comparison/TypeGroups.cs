namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// The type chips of the Comparison's file list: one per file type (the list's Kind column —
	/// "svg", "jpg", "pdf" …), the most frequent first; past <see cref="MaxChips"/> the rarest share
	/// one chip, <see cref="Other"/>, so the row stays short.
	/// </summary>
	public static class TypeGroups
	{
		/// <summary>The most chips the group shows, "other" included.</summary>
		public const int MaxChips = 8;

		/// <summary>The chip for the rarest types when there are more than <see cref="MaxChips"/>.</summary>
		public const string Other = "other";

		/// <summary>The chips for the given kinds (one entry per file): label, the kinds it stands for, how many files.</summary>
		public static IReadOnlyList<(string Label, IReadOnlySet<string> Kinds, int Count)> Plan(IEnumerable<string> kinds, int max = MaxChips)
		{
			var counted = kinds
				.GroupBy(k => k, StringComparer.Ordinal)
				.Select(g => (Kind: g.Key, Count: g.Count()))
				.OrderByDescending(k => k.Count)
				.ThenBy(k => k.Kind, StringComparer.Ordinal)
				.ToList();
			if (counted.Count <= max || max < 2)
			{
				return [.. counted.Select(k => (k.Kind, (IReadOnlySet<string>)new HashSet<string>([k.Kind], StringComparer.Ordinal), k.Count))];
			}

			var shown = counted.Take(max - 1)
				.Select(k => (k.Kind, (IReadOnlySet<string>)new HashSet<string>([k.Kind], StringComparer.Ordinal), k.Count))
				.ToList();
			var rest = counted.Skip(max - 1).ToList();
			shown.Add((Other, new HashSet<string>(rest.Select(k => k.Kind), StringComparer.Ordinal), rest.Sum(k => k.Count)));
			return shown;
		}
	}
}

namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// The file list's chips at work: which rows they let through and what each chip counts. Three
	/// groups — kind (page, file), state (differs, only in A, only in B), type (one per file type).
	/// In each group the lit chips add up and a group with none lit lets everything through; the
	/// groups combine. The state chips count what the kind and type chips let through, the type
	/// chips what the kind and state chips let through. A lit type chip lets no page through; with
	/// <c>typesApply</c> false (the type chips hidden) the type group does not filter.
	/// </summary>
	public static class ChipFilter
	{
		/// <summary>One listed file: page or not, its state chip, its type chip (-1 for a page or a type no chip has).</summary>
		public readonly record struct Row(bool IsPage, int State, int Type);

		public static (bool[] Shown, int[] States, int[] Types) Run(IReadOnlyList<Row> rows, bool[] kindOn, bool[] stateOn, bool[] typeOn, bool typesApply)
		{
			var shown = new bool[rows.Count];
			var states = new int[stateOn.Length];
			var types = new int[typeOn.Length];
			bool anyKind = kindOn.Any(x => x), anyState = stateOn.Any(x => x), anyType = typesApply && typeOn.Any(x => x);
			for (int i = 0; i < rows.Count; i++)
			{
				var row = rows[i];
				if (anyKind && !kindOn[row.IsPage ? 0 : 1])
				{
					continue;
				}

				bool typePasses = !anyType || (row.Type >= 0 && typeOn[row.Type]);
				bool statePasses = !anyState || stateOn[row.State];
				if (typePasses)
				{
					states[row.State]++;
				}

				if (statePasses && row.Type >= 0)
				{
					types[row.Type]++;
				}

				shown[i] = typePasses && statePasses;
			}

			return (shown, states, types);
		}
	}
}
