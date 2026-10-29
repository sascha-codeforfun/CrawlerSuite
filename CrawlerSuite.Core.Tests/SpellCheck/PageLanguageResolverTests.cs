namespace CrawlerSuite.Core.Tests.SpellCheck
{
	using System;
	using CrawlerSuite.Core.Lexicon;
	using System.Collections.Generic;
	using System.Linq;
	using CrawlerSuite.Core.SpellCheck;
	using Xunit;

	public class PageLanguageResolverTests
	{
		private static Dictionary<string, List<string>> Overrides() => new()
		{
			["/de/home/international/"] = new() { "de", "en" },
			["/de/home/service/examplix-guide-en"] = new() { "de", "en" },
			["/de/home/welcome"] = new() { "de", "en", "fr" },
		};

		[Fact]
		public void NoOverrides_ReturnsBranchLanguageOnly()
		{
			var langs = PageLanguageResolver.Resolve("https://site.test/de/home/foo.html", "de", null);
			Assert.Equal(new[] { "de" }, langs);
		}

		[Fact]
		public void NoMatch_ReturnsBranchLanguageOnly()
		{
			var langs = PageLanguageResolver.Resolve("https://site.test/de/home/other.html", "de", Overrides());
			Assert.Equal(new[] { "de" }, langs);
		}

		[Fact]
		public void PrefixMatch_StartsWith_CatchesPageAndSubpages()
		{
			// The example: one prefix catches both the page and its popup subpages.
			var a = PageLanguageResolver.Resolve(
				"https://site.test/de/home/service/examplix-guide-en.html", "de", Overrides());
			var b = PageLanguageResolver.Resolve(
				"https://site.test/de/home/service/examplix-guide-en/popups/page.html", "de", Overrides());

			Assert.Equal(new[] { "de", "en" }, a);
			Assert.Equal(new[] { "de", "en" }, b);
		}

		[Fact]
		public void DomainStripped_MatchesOnPathOnly()
		{
			var langs = PageLanguageResolver.Resolve(
				"https://www.any-host.example/de/home/welcome/start.html", "de", Overrides());
			Assert.Equal(new[] { "de", "en", "fr" }, langs);
		}

		[Fact]
		public void LongestPrefixWins_LexSpecialis()
		{
			var ov = new Dictionary<string, List<string>>
			{
				["/de/home/"] = new() { "de" },
				["/de/home/international/"] = new() { "de", "en" },
			};
			var langs = PageLanguageResolver.Resolve(
				"https://site.test/de/home/international/page.html", "de", ov);
			Assert.Equal(new[] { "de", "en" }, langs); // specific beats general regardless of order
		}

		[Fact]
		public void Match_IsCaseInsensitive_BothSides()
		{
			var langs = PageLanguageResolver.Resolve(
				"https://site.test/DE/Home/International/x.html", "de", Overrides());
			Assert.Equal(new[] { "de", "en" }, langs);
		}

		[Fact]
		public void QueryAndFragment_DoNotBreakMatch()
		{
			var langs = PageLanguageResolver.Resolve(
				"https://site.test/de/home/welcome?stref=languageswitch#top", "de", Overrides());
			Assert.Equal(new[] { "de", "en", "fr" }, langs);
		}

		[Fact]
		public void MissingBundles_AllPresent_IsEmpty()
		{
			var bundles = new Dictionary<string, Bundle>
			{
				["de"] = new Bundle(),
				["en"] = new Bundle(),
				["fr"] = new Bundle(),
			};
			Assert.Empty(PageLanguageResolver.MissingBundles(Overrides(), bundles));
		}

		[Fact]
		public void MissingBundles_NamesLanguageAndPaths_WithoutStopping()
		{
			var bundles = new Dictionary<string, Bundle>
			{
				["de"] = new Bundle(),
				["en"] = new Bundle(),
				// fr deliberately absent: a shared configuration may name a language not installed here
			};
			var line = Assert.Single(PageLanguageResolver.MissingBundles(Overrides(), bundles));
			Assert.StartsWith("fr has no dictionary bundle", line, StringComparison.Ordinal);
			Assert.Contains("/de/home/welcome", line, StringComparison.Ordinal);
		}

		[Fact]
		public void MissingBundles_NoOverrides_IsEmpty()
		{
			var bundles = new Dictionary<string, Bundle>();
			Assert.Empty(PageLanguageResolver.MissingBundles(null, bundles));
			Assert.Empty(PageLanguageResolver.MissingBundles(new Dictionary<string, List<string>>(), bundles));
		}

		[Fact]
		public void OverrideFor_EmptyList_IsADeclaration_LongestPathWins()
		{
			var overrides = new Dictionary<string, List<string>>
			{
				["/de/"] = new() { "de" },
				["/de/archive/"] = new(),          // not spell-checked
				["/de/archive/kept/"] = new() { "de", "en" },
			};
			Assert.Equal(new[] { "de" }, CrawlerSuite.Core.Html.PageLanguageSet.OverrideFor("https://site.test/de/page.html", overrides));
			Assert.Empty(CrawlerSuite.Core.Html.PageLanguageSet.OverrideFor("https://site.test/de/archive/old.html", overrides)!);
			Assert.Equal(new[] { "de", "en" }, CrawlerSuite.Core.Html.PageLanguageSet.OverrideFor("https://site.test/de/archive/kept/a.html", overrides));
			Assert.Null(CrawlerSuite.Core.Html.PageLanguageSet.OverrideFor("https://site.test/en/page.html", overrides));
		}
	}
}
