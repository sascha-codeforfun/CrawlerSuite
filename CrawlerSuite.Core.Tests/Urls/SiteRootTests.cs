using System.Text;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Urls;
using Xunit;

namespace CrawlerSuite.Core.Tests.Urls
{
	/// <summary>
	/// The root a list of addresses shares, and an address shown from its path on it — other hosts,
	/// schemes and look-alike hosts stay whole.
	/// </summary>
	public class SiteRootTests : IDisposable
	{
		private readonly string snapshot = Path.Combine(Path.GetTempPath(), "cs-root-" + Guid.NewGuid().ToString("N"));

		public SiteRootTests() => Directory.CreateDirectory(snapshot);

		public void Dispose()
		{
			try { Directory.Delete(snapshot, recursive: true); } catch (IOException) { }
		}

		[Fact]
		public void Of_TakesTheRootMostAddressesShare()
		{
			var root = SiteRoot.Of(
			[
				"https://water.test/a.html", "https://water.test/b/c.html", "https://ice.water.test/x.html", "not an address", "mailto:rain@water.test",
			]);
			Assert.Equal("https://water.test", root);
		}

		[Fact]
		public void Of_ATieGoesToTheFirstInOrdinalOrder_AndNothingGivesEmpty()
		{
			Assert.Equal("https://ice.test", SiteRoot.Of(["https://vapour.test/a", "https://ice.test/b"]));
			Assert.Equal(string.Empty, SiteRoot.Of(["", "relative/path.html"]));
		}

		[Fact]
		public void RootOf_KeepsAPortThatIsNotTheDefault()
		{
			Assert.Equal("https://water.test", SiteRoot.RootOf("https://water.test:443/a"));
			Assert.Equal("http://water.test:8080", SiteRoot.RootOf("http://water.test:8080/a"));
		}

		[Theory]
		[InlineData("https://water.test/de/home/a.html", "/de/home/a.html")]
		[InlineData("https://WATER.test/a?x=1", "/a?x=1")]
		[InlineData("https://water.test", "/")]
		[InlineData("https://water.test?x=1", "/?x=1")]
		[InlineData("https://water.test#top", "/#top")]
		[InlineData("https://ice.water.test/a.html", null)]
		[InlineData("https://water.test.example/a.html", null)]
		[InlineData("http://water.test/a.html", null)]
		[InlineData("", null)]
		public void PathOn_ShortensOnlyAddressesOnTheRoot(string url, string? expected) =>
			Assert.Equal(expected, SiteRoot.PathOn(url, "https://water.test"));

		[Fact]
		public void PathOn_WithoutARoot_ShortensNothing() =>
			Assert.Null(SiteRoot.PathOn("https://water.test/a.html", string.Empty));

		[Fact]
		public void FromSnapshot_ReadsTheCrawlerIndex()
		{
			File.WriteAllText(Path.Combine(snapshot, LogFileNames.CrawlerIndex),
				"a.html|https://water.test/a.html|discovery\r\nb.html|https://water.test/b.html|discovery\r\nc.png|https://ice.test/c.png|discovery\r\n",
				new UTF8Encoding(false));
			Assert.Equal("https://water.test", SiteRoot.FromSnapshot(snapshot));
		}

		[Fact]
		public void FromSnapshot_WithoutAnIndex_IsEmpty() =>
			Assert.Equal(string.Empty, SiteRoot.FromSnapshot(snapshot));

		[Fact]
		public void TheStyledHeadingsCard_TakesTheLevelFromTheOutline_NotFromTheClass()
		{
			var text = FindingsCatalog.BasisFor("STYLED_HEADING", string.Empty)!.Value.Basis.Text;
			Assert.Contains("Checked in the delivered HTML: a role or heading added by a script at runtime is not seen.", text);
			Assert.Contains("The class shows the look, not the level", text);
			Assert.Contains("<h3><button>…</button></h3>", text);
			Assert.DoesNotContain("the h-element the class names", text);
		}
	}
}
