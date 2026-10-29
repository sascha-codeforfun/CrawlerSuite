using System.Text;
using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>Pictures a line carries as base64: found, typed by their bytes, folded in the shown text, and compared by pixels.</summary>
	public class EmbeddedPicturesTests
	{
		private static readonly byte[] PngHead = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
		private static readonly byte[] JpegHead = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];

		private static string Uri(string type, byte[] bytes, string extra = "") => $"data:image/{type}{extra};base64,{Convert.ToBase64String(bytes)}";

		[Fact]
		public void Every_embedded_picture_of_a_line_is_found_in_order_with_its_bytes()
		{
			var line = $"<img src=\"{Uri("png", PngHead)}\" alt=\"ice\"><i style=\"background:url({Uri("jpeg", JpegHead)})\"></i>";
			var found = EmbeddedPictures.In(line);
			Assert.Equal(["PNG", "JPEG"], found.Select(f => f.Kind).ToArray());
			Assert.Equal(PngHead, found[0].Bytes);
			Assert.Equal(Convert.ToBase64String(PngHead), line.Substring(found[0].Start, found[0].Length));
		}

		[Fact]
		public void Parameters_before_base64_are_allowed()
		{
			Assert.Single(EmbeddedPictures.In($"x {Uri("png", PngHead, ";charset=utf-8")} y"));
		}

		[Fact]
		public void The_type_comes_from_the_bytes_and_a_wrong_label_is_told()
		{
			var found = EmbeddedPictures.In(Uri("png", JpegHead)).Single();
			Assert.Equal("JPEG", found.Kind);
			Assert.True(found.Mislabelled);
			Assert.False(EmbeddedPictures.In(Uri("jpeg", JpegHead)).Single().Mislabelled);
		}

		[Fact]
		public void An_embedded_svg_is_text()
		{
			var svg = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"><circle r=\"4\"/></svg>");
			var found = EmbeddedPictures.In(Uri("svg+xml", svg)).Single();
			Assert.True(found.IsSvg);
			Assert.False(found.Mislabelled);
		}

		[Theory]
		[InlineData("GIF89a", "GIF")]
		[InlineData("BM......", "BMP")]
		[InlineData("plain words", "unknown")]
		public void Other_types_are_told_by_their_first_bytes(string head, string kind)
		{
			Assert.Equal(kind, EmbeddedPictures.Sniff(Encoding.Latin1.GetBytes(head)));
		}

		[Fact]
		public void A_line_without_base64_has_no_pictures_and_broken_base64_is_told()
		{
			Assert.Empty(EmbeddedPictures.In("<img src=\"/i/ice.png\">"));
			var broken = EmbeddedPictures.In("data:image/png;base64,A").Single();
			Assert.Null(broken.Bytes);
			Assert.Equal("unreadable", broken.Kind);
		}

		[Fact]
		public void A_long_base64_run_folds_to_one_piece_marked_when_it_differs_inside()
		{
			var run = new string('Q', 300);
			var text = "<img src=\"data:image/png;base64," + run + "\"> ice";
			int start = text.IndexOf(run, StringComparison.Ordinal);
			var folds = EmbeddedPictures.Folds(text);
			Assert.Equal([(start, 300)], folds);

			var plain = EmbeddedPictures.Pieces(text, [], folds);
			Assert.Equal(3, plain.Count);
			Assert.True(plain[1].Fold);
			Assert.False(plain[1].Hit);
			Assert.Contains("300 characters", plain[1].Text);

			// A mark inside the run marks the folded piece; a mark outside stays where it is.
			var marked = EmbeddedPictures.Pieces(text, [(start + 10, 2), (text.Length - 3, 3)], folds);
			Assert.True(marked.Single(p => p.Fold).Hit);
			Assert.Equal(("ice", true, false), marked[^1]);
		}

		[Fact]
		public void Short_base64_is_not_folded()
		{
			Assert.Empty(EmbeddedPictures.Folds("data:image/png;base64," + new string('Q', EmbeddedPictures.FoldFrom - 1)));
		}

		[Fact]
		public void Pixels_are_counted_four_bytes_each()
		{
			byte[] a = [1, 2, 3, 255, 9, 9, 9, 255, 0, 0, 0, 0];
			byte[] b = [1, 2, 3, 255, 9, 9, 8, 255, 0, 0, 0, 1];
			Assert.Equal(2, EmbeddedPictures.DifferingPixels(a, b));
			Assert.Equal(0, EmbeddedPictures.DifferingPixels(a, a));
		}
	}
}
