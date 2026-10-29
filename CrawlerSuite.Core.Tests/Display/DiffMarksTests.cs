using System.IO;
using Xunit;

namespace CrawlerSuite.Core.Tests
{
	/// <summary>
	/// Inside a changed line of the Comparison, the pieces that really differ stand out from the
	/// row's soft red or green: A's in the solid red triage paints its offenders with, B's in a
	/// solid green of the same depth, both with white text.
	/// </summary>
	public class DiffMarksTests
	{
		private static string App(params string[] parts) => File.ReadAllText(Path.Combine([Text.DocumentKeys.Root(), "CrawlerSuite", .. parts]));

		[Fact]
		public void The_two_sides_mark_their_pieces_red_and_green()
		{
			var view = App("Views", "ComparisonView.xaml");
			Assert.Contains(@"views:Highlight.RemovedPieces=""{Binding PiecesA}""", view);
			Assert.Contains(@"views:Highlight.AddedPieces=""{Binding PiecesB}""", view);

			var highlight = App("Views", "Highlight.cs");
			Assert.Contains(@"""Brush.ErrorSolid"", ""Brush.OnError""", highlight);
			Assert.Contains(@"""Brush.OkSolid"", ""Brush.OnOk""", highlight);
		}

		[Fact]
		public void The_theme_has_the_solid_green_and_its_text()
		{
			var theme = App("Theme", "HueTheme.xaml");
			Assert.Contains(@"<Color x:Key=""Ok.Solid"">#15803D</Color>", theme);
			Assert.Contains(@"x:Key=""Brush.OkSolid""", theme);
			Assert.Contains(@"x:Key=""Brush.OnOk""", theme);
		}
	}
}
