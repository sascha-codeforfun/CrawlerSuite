using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>The type chips of the Comparison's file list, and the three chip groups combined.</summary>
	public class TypeGroupsTests
	{
		[Fact]
		public void One_chip_per_type_the_most_frequent_first_ties_by_name()
		{
			var plan = TypeGroups.Plan(["svg", "jpg", "svg", "png", "jpg", "svg", "css"]);
			Assert.Equal(["svg", "jpg", "css", "png"], plan.Select(p => p.Label).ToArray());
			Assert.Equal([3, 2, 1, 1], plan.Select(p => p.Count).ToArray());
		}

		[Fact]
		public void Past_the_limit_the_rarest_share_one_other_chip()
		{
			var kinds = new[] { "a", "a", "a", "b", "b", "c", "d", "e" };
			var plan = TypeGroups.Plan(kinds, max: 3);
			Assert.Equal(["a", "b", TypeGroups.Other], plan.Select(p => p.Label).ToArray());
			Assert.Equal(3, plan[2].Count);
			Assert.True(plan[2].Kinds.SetEquals(["c", "d", "e"]));
		}

		[Fact]
		public void Exactly_at_the_limit_there_is_no_other_chip()
		{
			Assert.DoesNotContain(TypeGroups.Plan(["a", "b", "c"], max: 3), p => p.Label == TypeGroups.Other);
			Assert.Empty(TypeGroups.Plan([]));
		}

		// Rows: a page that differs, an svg only in B, a jpg only in B, an svg only in A.
		private static readonly ChipFilter.Row[] Rows = [new(true, 0, -1), new(false, 2, 0), new(false, 2, 1), new(false, 1, 0)];

		private static (bool[] Shown, int[] States, int[] Types) Run(bool[]? kind = null, bool[]? state = null, bool[]? type = null, bool typesApply = true) =>
			ChipFilter.Run(Rows, kind ?? [false, false], state ?? [false, false, false], type ?? [false, false], typesApply);

		[Fact]
		public void Nothing_lit_shows_everything_and_counts_everything()
		{
			var (shown, states, types) = Run();
			Assert.All(shown, Assert.True);
			Assert.Equal([1, 1, 2], states);
			Assert.Equal([2, 1], types);
		}

		[Fact]
		public void A_lit_type_shows_only_files_of_that_type_and_the_state_chips_count_them()
		{
			var (shown, states, types) = Run(type: [true, false]);
			Assert.Equal([false, true, false, true], shown);
			Assert.Equal([0, 1, 1], states);
			Assert.Equal([2, 1], types); // the type chips count by kind and state only
		}

		[Fact]
		public void State_and_type_combine_and_the_type_chips_follow_the_state()
		{
			var (shown, _, types) = Run(state: [false, false, true], type: [true, false]);
			Assert.Equal([false, true, false, false], shown);
			Assert.Equal([1, 1], types);
		}

		[Fact]
		public void Two_lit_types_add_up()
		{
			Assert.Equal([false, true, true, true], Run(type: [true, true]).Shown);
		}

		[Fact]
		public void Hidden_type_chips_do_not_filter()
		{
			// Only Pages lit: the type chips are hidden; one left lit must not hide the page.
			Assert.Equal([true, false, false, false], Run(kind: [true, false], type: [true, false], typesApply: false).Shown);
		}
	}
}
