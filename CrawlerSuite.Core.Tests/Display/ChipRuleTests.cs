using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Display;
using Xunit;

namespace CrawlerSuite.Core.Tests
{
	/// <summary>One rule for every filter chip row: lit shows, lit chips add up, none lit shows all, every row starts with none lit.</summary>
	public class ChipRuleTests
	{
		private static readonly (string, bool)[] NoneLit = [("de", false), ("en-GB", false), ("en-US", false)];

		[Fact]
		public void None_lit_lets_everything_through()
		{
			Assert.True(ChipRule.Lets(NoneLit, "de"));
			Assert.True(ChipRule.Lets(NoneLit, "fr")); // not even a chip of its own
			Assert.False(ChipRule.Dimmed(NoneLit.Select(c => c.Item2), lit: false));
		}

		[Fact]
		public void A_lit_chip_shows_only_its_items_and_dims_the_others()
		{
			(string, bool)[] de = [("de", true), ("en-GB", false), ("en-US", false)];
			Assert.True(ChipRule.Lets(de, "DE"));
			Assert.False(ChipRule.Lets(de, "en-GB"));
			Assert.True(ChipRule.Dimmed(de.Select(c => c.Item2), lit: false));
			Assert.False(ChipRule.Dimmed(de.Select(c => c.Item2), lit: true));
		}

		[Fact]
		public void Lit_chips_add_up()
		{
			(string, bool)[] two = [("de", true), ("en-GB", false), ("en-US", true)];
			Assert.True(ChipRule.Lets(two, "de"));
			Assert.True(ChipRule.Lets(two, "en-US"));
			Assert.False(ChipRule.Lets(two, "en-GB"));
		}

		private static string App(params string[] parts) => File.ReadAllText(Path.Combine([Text.DocumentKeys.Root(), "CrawlerSuite", .. parts]));

		[Fact]
		public void Every_filter_chip_row_starts_with_none_lit()
		{
			foreach (var model in new[] { "TriageModel.cs", "DictionariesModel.cs", "SpellFindingsModel.cs" })
			{
				var source = App("Run", model);
				Assert.DoesNotMatch(new Regex(@"PageSourceRole\.\w+, true,"), source);
			}
		}

		[Fact]
		public void Every_page_draws_its_filter_chips_alike()
		{
			// Lit: a frame; the others of the group dimmed; never "unchecked means hidden".
			foreach (var view in new[] { "ComparisonView.xaml", "TriageView.xaml", "DictionariesView.xaml", "SpellFindingsView.xaml" })
			{
				var xaml = App("Views", view);
				var style = Regex.Match(xaml, @"<Style x:Key=""FilterChip""[\s\S]*?</Style>").Value;
				Assert.Contains(@"<DataTrigger Binding=""{Binding IsDimmed}"" Value=""True"">", style);
				Assert.Contains(@"<Trigger Property=""IsChecked"" Value=""True"">", style);
				Assert.DoesNotContain(@"<Trigger Property=""IsChecked"" Value=""False"">", style);
			}
		}
	}
}
