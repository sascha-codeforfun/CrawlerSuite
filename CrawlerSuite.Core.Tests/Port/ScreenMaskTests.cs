using CrawlerSuite.Core.Display;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>The screen mask: URL levels, site labels, configured paths, hosts and the proxy account.</summary>
	public class ScreenMaskTests
	{
		private const string Url = "https://www.mysite.test/en/products/widget.html";

		private static ScreenMask Learnt()
		{
			var config = new Config
			{
				Sites =
				[
					new SiteConfig { Name = "Other Shop", Url = "https://shop.example.test", IsPrimary = false },
					new SiteConfig { Name = "MySite", Url = "https://www.mysite.test", IsPrimary = true },
					new SiteConfig { Name = "Third", Url = "https://third.example.test", IsPrimary = false },
				],
				BaseDirectory = @"D:\Crawls",
				ProxyUrl = "proxy.corp.test:8080",
			};
			var mask = new ScreenMask();
			mask.Learn(config, @"C:\Tools\CrawlerSuite\crawlersuite.config.json");
			return mask;
		}

		[Theory]
		[InlineData(@"D:\Crawls\https___www_mysite_test\20261010-1412", @"•••\•••\20261010-1412")]
		[InlineData(@"E:\Elsewhere\https___www_mysite_test\20261010-1412\download", @"•••\download")]
		[InlineData(@"E:\Elsewhere\https___www_mysite_test", @"•••\•••")]
		[InlineData(@"C:\Tools\CrawlerSuite\Dictionaries\https___www_mysite_test.dic", @"•••\Dictionaries\•••.dic")]
		[InlineData(@"Snapshot folder https___shop_example_test opened", @"Snapshot folder ••• opened")]
		public void SiteFolderName_IsHiddenInPaths(string text, string expected)
		{
			foreach (var level in new[] { MaskLevel.Host, MaskLevel.HostAndPath, MaskLevel.SchemeOnly })
			{
				Assert.Equal(expected, Learnt().Apply(text, level));
			}
		}

		[Fact]
		public void SiteFolderName_WithTrailingSlashUrl_IsHidden()
		{
			var mask = new ScreenMask();
			mask.Learn(new Config { Sites = [new SiteConfig { Name = "S", Url = "https://a.example.test/", IsPrimary = true }] }, null);

			Assert.Equal(@"•••\x", mask.Apply(@"F:\https___a_example_test_\x", MaskLevel.Host));
			Assert.Equal(@"•••\x", mask.Apply(@"F:\https___a_example_test\x", MaskLevel.Host));
		}

		[Fact]
		public void SiteFolderName_NotInsideALongerName()
		{
			Assert.Equal("https___www_mysite_test_shop", Learnt().Apply("https___www_mysite_test_shop", MaskLevel.Host));
		}

		[Fact]
		public void Clear_ChangesNothing()
		{
			var text = $"MySite · {Url} · D:\\Crawls\\MySite";
			Assert.Equal(text, Learnt().Apply(text, MaskLevel.Clear));
		}

		[Theory]
		[InlineData(MaskLevel.Host, "https://•••••/en/products/widget.html")]
		[InlineData(MaskLevel.HostAndPath, "https://•••••/•••/•••/widget.html")]
		[InlineData(MaskLevel.SchemeOnly, "https://•••••")]
		public void Url_IsMaskedToTheLevel(MaskLevel level, string expected)
		{
			Assert.Equal(expected, new ScreenMask().Apply(Url, level));
		}

		[Fact]
		public void HostAndPath_HidesQueryAndFragment_KeepsTrailingPunctuation()
		{
			var masked = new ScreenMask().Apply("See https://a.test/x/page.html?id=7#top.", MaskLevel.HostAndPath);
			Assert.Equal("See https://•••••/•••/page.html?•••#•••.", masked);
		}

		[Fact]
		public void UrlInLogRow_StopsAtTheSeparator()
		{
			var masked = new ScreenMask().Apply("2026-10-10 | http://localhost:5000/a.html|saved", MaskLevel.SchemeOnly);
			Assert.Equal("2026-10-10 | http://•••••|saved", masked);
		}

		[Fact]
		public void SiteNames_BecomeLabels_InConfigOrder()
		{
			var mask = Learnt();
			Assert.Equal("Primary site", mask.SiteLabel("MySite"));
			Assert.Equal("Site 2", mask.SiteLabel("Other Shop"));
			Assert.Equal("Site 3", mask.SiteLabel("Third"));
			Assert.Equal("Running · Primary site", mask.Apply("Running · mysite", MaskLevel.Host));
		}

		[Fact]
		public void SiteName_InsideAWord_IsLeftAlone()
		{
			Assert.Equal("MySiteMap", Learnt().Apply("MySiteMap", MaskLevel.Host));
		}

		[Fact]
		public void SnapshotPath_KeepsLabelAndTimestamp()
		{
			var masked = Learnt().Apply(@"D:\Crawls\MySite\2026-10-10-09-00-00", MaskLevel.Host);
			Assert.Equal(@"•••\Primary site\2026-10-10-09-00-00", masked);
		}

		[Fact]
		public void ProgramFolder_IsHidden()
		{
			var masked = Learnt().Apply(@"C:\Tools\CrawlerSuite\application.log", MaskLevel.Host);
			Assert.Equal(@"•••\application.log", masked);
		}

		[Fact]
		public void UnknownAbsolutePath_KeepsOnlyTheFileName()
		{
			var masked = new ScreenMask().Apply(@"Cannot write E:\Work\secret-client\report.csv now", MaskLevel.Host);
			Assert.Equal(@"Cannot write •••\report.csv now", masked);
		}

		[Theory]
		[InlineData(@"Missing: E:\Client Work\Acme Corp\report.csv", @"Missing: •••\report.csv")]
		[InlineData(@"Folder E:\Client Work\Acme is gone", @"Folder •••\Acme is gone")]
		public void UnknownPathWithSpaces_HidesEveryFolder(string text, string expected)
		{
			Assert.Equal(expected, new ScreenMask().Apply(text, MaskLevel.Host));
		}

		[Fact]
		public void ProxyHostWithoutADot_LeavesTheWordAlone()
		{
			var mask = new ScreenMask();
			mask.Learn(new Config { ProxyUrl = "http://proxy:8080" }, null);
			Assert.Equal("Configure a proxy; http://•••••", mask.Apply("Configure a proxy; http://proxy:8080", MaskLevel.SchemeOnly));
		}

		[Fact]
		public void ConfiguredRootDoesNotEatALongerFolderName()
		{
			var masked = Learnt().Apply(@"D:\CrawlsOld\x.txt", MaskLevel.Host);
			Assert.Equal(@"•••\x.txt", masked); // the fallback, not "•••Old\x.txt"
		}

		[Fact]
		public void BareHostsAndProxyAccount_AreHidden()
		{
			// The account is the Windows user's: the proxy sign-in is filled in with it (no account is configured).
			var masked = Learnt().Apply($"Proxy proxy.corp.test:8080 as {System.Environment.UserName}; site mysite.test and www.mysite.test.", MaskLevel.Host);
			Assert.Equal("Proxy •••••:8080 as •••; site ••••• and •••••.", masked);
		}

		[Fact]
		public void SiteRelativePath_FollowsTheLevel()
		{
			var mask = new ScreenMask();
			Assert.Equal("/en/products/", mask.ApplyToUrlPath("/en/products/", MaskLevel.Host));
			Assert.Equal("/•••/•••/", mask.ApplyToUrlPath("/en/products/", MaskLevel.HostAndPath));
			Assert.Equal("/•••", mask.ApplyToUrlPath("/en/products/", MaskLevel.SchemeOnly));
		}

		[Fact]
		public void Learning_IsAdditive()
		{
			var mask = Learnt();
			mask.Learn(new Config { Sites = [new SiteConfig { Name = "Renamed", Url = "https://new.test", IsPrimary = true }] }, null);
			Assert.Equal("Primary site and Primary site", mask.Apply("MySite and Renamed", MaskLevel.Host));
		}
	
		// An address names the site as surely as its host; the class next to it stays.
		[Theory]
		[InlineData("www.site.test: new 203.0.113.7 (public); gone 198.51.100.2", "www.site.test: new ••• (public); gone •••")]
		[InlineData("connected to 2001:db8::5 and fe80::1%3", "connected to ••• and •••%3")]
		[InlineData("::ffff:192.0.2.1 (public)", "••• (public)")]
		[InlineData("started 21:38:00, build 2.0.179, 4 of 12", "started 21:38:00, build 2.0.179, 4 of 12")]
		public void IpAddresses_AreHidden_TimesAndVersionsStay(string text, string expected) =>
			Assert.Equal(expected, new ScreenMask().Apply(text, MaskLevel.Host));

		[Fact]
		public void IpAddresses_ShowInClear() =>
			Assert.Equal("203.0.113.7", new ScreenMask().Apply("203.0.113.7", MaskLevel.Clear));
	}
}
