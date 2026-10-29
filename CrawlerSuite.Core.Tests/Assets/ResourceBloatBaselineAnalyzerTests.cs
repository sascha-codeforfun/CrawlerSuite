using Xunit;

namespace CrawlerSuite.Core.Tests.Assets
{
	/// <summary>
	/// Tests for ResourceBloatBaselineAnalyzer.ShortenUrl helper.
	/// </summary>
	public class ResourceBloatBaselineAnalyzerTests
	{
		// ── ShortenUrl ────────────────────────────────────────────────────────

		[Fact]
		public void ShortenUrl_FloelibWithContentHash_StripsHash()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl(
				"https://www.example.com/ice/floelibs/site/sitebundle.min.5f0c3e8a1d7b4926e3a0c5d8b7f14e62.css");
			Assert.Equal("sitebundle.min.css", result);
		}

		[Fact]
		public void ShortenUrl_JsFileWithHash_StripsHash()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl(
				"https://www.example.com/ice/floelibs/site/sitebundle.min.lc-9e4d2b7f1a3c58e0-lc.js");
			// No 32-char hex hash — returns filename as-is
			Assert.Equal("sitebundle.min.lc-9e4d2b7f1a3c58e0-lc.js", result);
		}

		[Fact]
		public void ShortenUrl_Exactly32HexHash_Stripped()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl(
				"https://www.example.com/libs/script.min.abcdef1234567890abcdef1234567890.js");
			Assert.Equal("script.min.js", result);
		}

		[Fact]
		public void ShortenUrl_NoHash_ReturnsFilename()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl(
				"https://www.example.com/libs/tidewaveagent_ABCDEFGHIJKL_20481150917104422.js");
			Assert.Equal("tidewaveagent_ABCDEFGHIJKL_20481150917104422.js", result);
		}

		[Fact]
		public void ShortenUrl_EmptyString_ReturnsEmpty()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl("");
			Assert.Equal("", result);
		}

		[Fact]
		public void ShortenUrl_UrlWithNoPath_ReturnsEmptySegment()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl(
				"https://www.example.com/");
			// Last segment after split('/') is ""
			Assert.Equal("", result);
		}
	}
}
