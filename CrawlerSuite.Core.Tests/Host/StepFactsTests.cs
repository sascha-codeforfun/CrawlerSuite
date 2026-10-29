using System.Text;
using CrawlerSuite.Core.Host;
using CrawlerSuite.Core.Links;
using Xunit;

namespace CrawlerSuite.Core.Tests.Host
{
	/// <summary>The overview's facts: neutral chips with a small overlay; lines that would read 0 are left out.</summary>
	public class StepFactsTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), "cs-facts-" + Guid.NewGuid().ToString("N"));

		public StepFactsTests() => Directory.CreateDirectory(dir);

		public void Dispose()
		{
			try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
		}

		[Fact]
		public void CountsAndSizes_ReadTheSameEverywhere()
		{
			Assert.Equal("1,978", StepFacts.Count(1978));
			Assert.Equal("670.2 MB", StepFacts.Bytes(702_754_406));
			Assert.Equal(new[] { "a · 2" }, StepFacts.NonZero(("a", 2), ("b", 0)).ToArray());
		}

		[Fact]
		public void Redirects_AreCountedByKind_AsInformation()
		{
			var log = Path.Combine(dir, "03.log");
			File.WriteAllLines(log,
			[
				"https://site.test/a | Found | 302 https://site.test/b",
				"https://site.test/c | Found | 200 https://site.test/d",
				"https://site.test/e | MovedPermanently | 200 https://site.test/f",
			], new UTF8Encoding(true));

			var facts = RedirectAnalyzer.Facts(log)!;

			Assert.Equal("3", facts.Chip);
			Assert.Equal(new[] { "301 permanent · 1", "302 temporary · 2" }, facts.Lines.ToArray());
		}

		[Theory]
		[InlineData("https://site.test/a.html", "/a.html", false)]
		[InlineData("https://site.test/a/", "https://SITE.test/a", false)]   // trailing '/' and the host's case do not count
		[InlineData("https://site.test/a.html", "/b.html", true)]
		[InlineData("https://site.test/a.html", "", false)]
		public void ACanonical_NamingAnotherPage_TakesThePageOutOfEligible(string page, string canonical, bool elsewhere) =>
			Assert.Equal(elsewhere, SEO.CanonicalElsewhere(page, canonical.Length == 0 ? [] : [canonical]));

		[Fact]
		public void OnlyPagesWhoseLinksResolveToABrokenAddress_AreParsed()
		{
			var page = new Uri("https://site.test/shop/a.html");
			var broken = new HashSet<string> { "https://site.test/shop/water.html", "https://site.test/%C3%A4rger.png" };
			bool IsBroken(string url) => broken.Contains(url);

			Assert.True(BrokenLinkOrigins.NamesBroken("<a href=\"water.html\">H2O</a>", page, IsBroken));
			Assert.True(BrokenLinkOrigins.NamesBroken("<img src='/ärger.png'>", page, IsBroken));
			Assert.True(BrokenLinkOrigins.NamesBroken("<a href=water.html>H2O</a>", page, IsBroken));
			Assert.False(BrokenLinkOrigins.NamesBroken("<a href=\"/water.html\">the water page at the top</a><p>water.html</p>", page, IsBroken));
			Assert.False(BrokenLinkOrigins.NamesBroken("<a href=\"/shop/waterfall.html\">Falls</a>", page, IsBroken));
		}

	}
}
