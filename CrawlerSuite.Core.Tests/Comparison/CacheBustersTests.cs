using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>The cache busters the comparison sets aside: the two forms seen, nothing broader.</summary>
	public class CacheBustersTests
	{
		[Theory]
		[InlineData("/c/image.img.png/1712345678901.png", "/c/image.img.png/#.png")]
		[InlineData("/c/image.img.jpg/1712345678901.jpg", "/c/image.img.jpg/#.jpg")]
		[InlineData("/c/icon/1712345678901.svg\"", "/c/icon/#.svg\"")]
		[InlineData("/c/pic/1712345678901.webp?w=1", "/c/pic/#.webp?w=1")]
		[InlineData("/lib/water.min.a1b2c3d4e5f60718293a4b5c6d7e8f90.js", "/lib/water.min.#.js")]
		[InlineData("/lib/0123456789abcdef0123/ice.css", "/lib/#/ice.css")]
		public void Neutral_SetsTheFormsAside(string text, string expected) => Assert.Equal(expected, CacheBusters.Neutral(text));

		[Theory]
		[InlineData("/c/pic/171234567890.png")]     // 12 digits
		[InlineData("/c/pic/17123456789012.png")]   // 14 digits
		[InlineData("/c/pic/x1712345678901.png")]   // not the whole file name
		[InlineData("lastModification=1700000000999|")]
		[InlineData("/c/pic/1712345678901")]        // no extension
		[InlineData("/lib/water.min.a1b2c3d4e5f6.js")]  // 12 hex digits
		[InlineData("agent_12345678901234567.js")]  // digits after an underscore
		public void Neutral_LeavesEverythingElse(string text) => Assert.Equal(text, CacheBusters.Neutral(text));
	}
}
