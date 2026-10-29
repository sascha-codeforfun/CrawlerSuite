using CrawlerSuite.Core.Urls;
using Xunit;

namespace CrawlerSuite.Core.Tests.Urls
{
	/// <summary>A stylesheet or script named by its file: path and cache buster cut, nothing else touched.</summary>
	public class FileKindTests
	{
		[Theory]
		[InlineData("https://site.test/lib/water.min.css", FileKind.Stylesheet)]
		[InlineData("https://site.test/lib/WATER.CSS?v=2", FileKind.Stylesheet)]
		[InlineData("https://site.test/lib/rain.js", FileKind.Script)]
		[InlineData("https://site.test/lib/rain.mjs#top", FileKind.Script)]
		[InlineData("https://site.test/lib/ice.png", null)]
		[InlineData("https://site.test/water.html", null)]
		[InlineData("https://site.test/css/", null)]
		[InlineData("", null)]
		public void Of_ReadsTheExtensionOfThePath(string url, string? kind) =>
			Assert.Equal(kind, FileKind.Of(url));

		[Theory]
		[InlineData("https://site.test/etc/lib/water.min.f00dfeed0123456789abcdef01234567.css", "water.min.css")]
		[InlineData("https://site.test/lib/rain.c0ffee00112233445566778899aabbcc.js", "rain.js")]
		[InlineData("https://site.test/lib/rain.js?v=0b216b5682d9cf0e", "rain.js")]
		[InlineData("https://site.test/lib/ice.deadbeef.css", "ice.deadbeef.css")]
		[InlineData("https://site.test/lib/vapour-2026.css", "vapour-2026.css")]
		public void ShortName_CutsThePathAndTheCacheBuster(string url, string name) =>
			Assert.Equal(name, FileKind.ShortName(url));
	}
}
