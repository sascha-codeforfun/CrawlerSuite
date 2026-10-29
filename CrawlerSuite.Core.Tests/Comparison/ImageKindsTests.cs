using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>Which saved files the Comparison shows as pictures.</summary>
	public class ImageKindsTests
	{
		[Theory]
		[InlineData("ab12cd.png")]
		[InlineData("ab12cd.JPG")]
		[InlineData("ab12cd.jpeg")]
		[InlineData("ab12cd.gif")]
		[InlineData("ab12cd.webp")]
		[InlineData("ab12cd.ico")]
		[InlineData("sub/ab12cd.tiff")]
		public void RasterFormats_ArePictures(string name) => Assert.True(ImageKinds.IsImage(name));

		[Theory]
		[InlineData("ab12cd.svg")]
		[InlineData("ab12cd.html")]
		[InlineData("ab12cd.png.header")]
		[InlineData("ab12cd")]
		[InlineData("png")]
		public void TextAndOtherFiles_AreNot(string name) => Assert.False(ImageKinds.IsImage(name));
	}
}
