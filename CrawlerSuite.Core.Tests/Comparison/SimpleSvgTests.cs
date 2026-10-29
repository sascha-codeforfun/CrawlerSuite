using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>Simple SVGs read into shapes the app draws without a library; anything else not drawn, with the reason.</summary>
	public class SimpleSvgTests
	{
		private const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

		[Fact]
		public void An_icon_of_paths_reads_with_its_view_box_and_open_colour()
		{
			var (drawing, problem) = SimpleSvg.Read($"<?xml version=\"1.0\"?><svg {Ns} viewBox=\"0 0 24 24\"><path d=\"M21 21.341h-9a1 1 0 0 1 0-2h9a1 1 0 0 1 0 2z\"/><path d=\"M3 4l1-4z\"/></svg>");
			Assert.Equal(string.Empty, problem);
			Assert.NotNull(drawing);
			Assert.Equal((0d, 0d, 24d, 24d), (drawing!.MinX, drawing.MinY, drawing.Width, drawing.Height));
			Assert.Equal(2, drawing.Shapes.Count);
			Assert.True(drawing.UsesCurrentColour);
			Assert.Equal(SimpleSvg.Current, drawing.Shapes[0].Fill);
			Assert.Null(drawing.Shapes[0].Stroke);
		}

		[Theory]
		[InlineData("M21 21.341h-9a1 1 0 0 1 0-2z", "M 21 21.341 h -9 a 1 1 0 0 1 0 -2 Z")]
		[InlineData("M1.15.15L2-3", "M 1.15 0.15 L 2 -3")]                      // packed numbers
		[InlineData("M0 0a1 1 0 011 1", "M 0 0 a 1 1 0 0 1 1 1")]                // packed arc flags
		[InlineData("M0 0 10 10 20 0", "M 0 0 L 10 10 L 20 0")]                  // pairs after a move are lines
		[InlineData("m0 0 10 10", "m 0 0 l 10 10")]
		[InlineData("M0,0 C1,1 2,2 3,3 4,4 5,5 6,6", "M 0 0 C 1 1 2 2 3 3 C 4 4 5 5 6 6")]
		public void Path_data_is_rewritten_with_a_letter_per_command_and_numbers_apart(string d, string expected)
		{
			Assert.Equal(expected, SimpleSvg.NormalisePath(d));
		}

		[Theory]
		[InlineData("M0 0 X1 1")]
		[InlineData("10 10")]
		[InlineData("M0 0a1 1 0 2 1 1 1")]
		public void Path_data_that_does_not_read_is_refused(string d)
		{
			Assert.Null(SimpleSvg.NormalisePath(d));
		}

		[Fact]
		public void Shapes_become_paths_and_paint_is_inherited_from_groups()
		{
			var (drawing, _) = SimpleSvg.Read($"<svg {Ns} width=\"40\" height=\"20\"><g fill=\"#f00\" stroke=\"blue\" stroke-width=\"2\">"
				+ "<rect x=\"1\" y=\"2\" width=\"3\" height=\"4\"/><circle cx=\"10\" cy=\"10\" r=\"5\" fill=\"none\"/><polygon points=\"0,0 4,0 2,3\"/></g></svg>");
			Assert.NotNull(drawing);
			Assert.Equal((40d, 20d), (drawing!.Width, drawing.Height));
			Assert.Equal("M 1 2 H 4 V 6 H 1 Z", drawing.Shapes[0].Path);
			Assert.Equal("#FFFF0000", drawing.Shapes[0].Fill);
			Assert.Equal("blue", drawing.Shapes[0].Stroke);
			Assert.Equal(2, drawing.Shapes[0].StrokeWidth);
			Assert.Null(drawing.Shapes[1].Fill); // the circle: stroke only
			Assert.EndsWith("Z", drawing.Shapes[2].Path);
		}

		[Theory]
		[InlineData("#0f08", "#8800FF00")]
		[InlineData("#11223380", "#80112233")]
		[InlineData("rgb(255, 0, 10)", "#FFFF000A")]
		[InlineData("rgba(0,0,0,0.5)", "#80000000")]
		public void Colours_are_normalised(string css, string expected)
		{
			Assert.Equal(expected, SimpleSvg.Normalise(css, new SortedSet<string>()));
		}

		[Fact]
		public void Style_declarations_and_transforms_are_read()
		{
			var (drawing, _) = SimpleSvg.Read($"<svg {Ns} viewBox=\"0 0 10 10\"><g transform=\"translate(2 3)\"><path style=\"fill:#000;fill-rule:evenodd\" transform=\"scale(2)\" d=\"M0 0h1v1z\"/></g></svg>");
			var shape = drawing!.Shapes.Single();
			Assert.True(shape.EvenOdd);
			Assert.Equal("#FF000000", shape.Fill);
			Assert.Equal([2d, 0, 0, 2, 2, 3], shape.Transform);
		}

		[Theory]
		[InlineData("<text>ice</text>", "text")]
		[InlineData("<defs><linearGradient id=\"g\"/></defs><rect width=\"1\" height=\"1\" fill=\"url(#g)\"/>", "definitions (defs)")]
		[InlineData("<style>path{fill:red}</style><path d=\"M0 0h1v1z\"/>", "CSS")]
		[InlineData("<use href=\"#a\"/>", "use")]
		[InlineData("<image href=\"x.png\"/>", "embedded images")]
		public void Anything_else_makes_the_whole_svg_not_drawn_with_the_reason(string inner, string reason)
		{
			var (drawing, problem) = SimpleSvg.Read($"<svg {Ns} viewBox=\"0 0 10 10\"><path d=\"M0 0h1v1z\"/>{inner}</svg>");
			Assert.Null(drawing);
			Assert.Contains(reason, problem);
		}

		[Fact]
		public void Without_a_size_or_as_broken_xml_it_is_not_drawn()
		{
			Assert.Contains("neither a viewBox", SimpleSvg.Read($"<svg {Ns}><path d=\"M0 0h1v1z\"/></svg>").Problem);
			Assert.Contains("does not read as XML", SimpleSvg.Read("<svg><path></svg>").Problem);
		}
	}
}
