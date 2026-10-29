using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Display;
using Xunit;

namespace CrawlerSuite.Core.Tests
{
	/// <summary>
	/// A toolbar in a narrow window: its actions move to their own line instead of being cut off, and
	/// a text meant for one line stays one line (a wrapping style would otherwise break it letter by
	/// letter in a narrow column and stretch the whole row).
	/// </summary>
	public class ToolbarFitTests
	{
		[Fact]
		public void Actions_stay_on_the_line_while_the_status_keeps_its_room()
		{
			Assert.False(ToolbarFit.Stack(available: 1600, fixedPart: 700, actions: 600));
			Assert.False(ToolbarFit.Stack(available: 1460, fixedPart: 700, actions: 600)); // exactly the status's least width left
		}

		[Fact]
		public void Actions_go_below_when_the_status_would_be_squeezed()
		{
			Assert.True(ToolbarFit.Stack(available: 1459, fixedPart: 700, actions: 600));
			Assert.True(ToolbarFit.Stack(available: 1100, fixedPart: 700, actions: 600)); // the actions would be cut off
		}

		[Fact]
		public void Nothing_laid_out_yet_keeps_the_actions_on_the_line()
		{
			Assert.False(ToolbarFit.Stack(available: 0, fixedPart: 700, actions: 600));
		}

		[Fact]
		public void A_wider_set_of_actions_goes_below_sooner()
		{
			// Stop appears while a comparison runs: the same window may then need the second line.
			Assert.False(ToolbarFit.Stack(available: 1500, fixedPart: 700, actions: 600));
			Assert.True(ToolbarFit.Stack(available: 1500, fixedPart: 700, actions: 680));
		}

		// Text.Caption and Text.Body wrap; a TextBlock that trims with "…" is meant for one line and must say NoWrap,
		// or the style's wrapping wins and a narrow column breaks it into a tall stack of letters.
		[Fact]
		public void Every_trimmed_text_with_a_wrapping_style_says_no_wrap()
		{
			var views = Path.Combine(Text.DocumentKeys.Root(), "CrawlerSuite", "Views");
			var offenders = Directory.GetFiles(views, "*.xaml")
				.SelectMany(file => Regex.Matches(File.ReadAllText(file), @"<TextBlock\b[^>]*>")
					.Select(m => (File: Path.GetFileName(file), Tag: m.Value)))
				.Where(t => t.Tag.Contains("TextTrimming=")
					&& Regex.IsMatch(t.Tag, @"Style=""\{StaticResource Text\.(Caption|Body)\}""")
					&& !t.Tag.Contains(@"TextWrapping=""NoWrap"""))
				.Select(t => t.File + ": " + Regex.Replace(t.Tag, @"\s+", " "))
				.ToList();
			Assert.True(offenders.Count == 0, string.Join("\n", offenders));
		}

		[Fact]
		public void The_comparison_toolbar_can_move_its_actions_below()
		{
			var xaml = File.ReadAllText(Path.Combine(Text.DocumentKeys.Root(), "CrawlerSuite", "Views", "ComparisonView.xaml"));
			Assert.Contains(@"x:Name=""PairActions""", xaml);
			Assert.Contains(@"x:Name=""ActionsInline""", xaml);
			Assert.Contains(@"x:Name=""ActionsBelow""", xaml);
			var status = Regex.Match(xaml, @"<TextBlock x:Name=""PairStatus""[^>]*>").Value;
			Assert.Contains(@"TextWrapping=""NoWrap""", status);
		}
	}
}
