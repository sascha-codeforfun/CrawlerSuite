using System.Collections.Generic;
using Xunit;

namespace CrawlerSuite.Core.Tests.Html
{
	/// <summary>
	/// The centralized page-language-set resolver (CrawlerSuite.Core.Html.PageLanguageSet).
	/// Pins the resolution chain (override → &lt;html lang&gt; → &lt;meta language&gt; →
	/// default), longest-prefix override precedence, and the set semantics the quote
	/// system check relies on: exactly one declared language is an anchor; zero (empty
	/// default) or many is "no anchor" (empty / multi-element set).
	/// </summary>
	public class PageLanguageSetTests
	{
		private static Dictionary<string, List<string>> Overrides() => new()
		{
			["/nl/home/"] = new() { "de" },                                   // mislabelled branch → corrected
			["/de/home/service.html"] = new() { "de", "en" },
			["/de/home/service/examplix-guide.html"] = new() { "de", "en", "cs" },
		};

		private static HtmlAgilityPack.HtmlDocument Doc(string html)
		{
			var d = new HtmlAgilityPack.HtmlDocument();
			d.LoadHtml(html);
			return d;
		}

		// ── core overload (pre-resolved branch language) ─────────────────────

		[Fact]
		public void Core_NoOverride_NonEmptyBranch_ReturnsBranchSingleton()
		{
			var set = CrawlerSuite.Core.Html.PageLanguageSet.Resolve("https://x/de/home/p.html", "de", null);
			Assert.Equal(new[] { "de" }, set);
		}

		[Fact]
		public void Core_NoOverride_EmptyBranch_ReturnsEmptySet()
		{
			// The agnostic lever: nothing declared + empty default ⇒ no anchor.
			var set = CrawlerSuite.Core.Html.PageLanguageSet.Resolve("https://x/de/home/p.html", "", null);
			Assert.Empty(set);
		}

		[Fact]
		public void Core_OverrideWins_LongestPrefix()
		{
			var set = CrawlerSuite.Core.Html.PageLanguageSet.Resolve(
				"https://x/de/home/service/examplix-guide.html", "de", Overrides());
			Assert.Equal(new[] { "de", "en", "cs" }, set);
		}

		[Fact]
		public void Core_OverrideWins_OverDeclaredBranch()
		{
			// /nl/home/ page is really German; the override forces de regardless of branch.
			var set = CrawlerSuite.Core.Html.PageLanguageSet.Resolve(
				"https://x/nl/home/foo.html", "nl", Overrides());
			Assert.Equal(new[] { "de" }, set);
		}

		// ── doc overload (branch resolved via <html lang> / <meta language>) ─

		[Fact]
		public void Doc_HtmlLangWins_OverMeta()
		{
			var doc = Doc("<html lang=\"de\"><head><meta name=\"language\" content=\"en\"></head><body></body></html>");
			var set = CrawlerSuite.Core.Html.PageLanguageSet.Resolve("https://x/p.html", doc, null, "");
			Assert.Equal(new[] { "de" }, set);
		}

		[Fact]
		public void Doc_MetaUsed_WhenNoHtmlLang()
		{
			var doc = Doc("<html><head><meta name=\"language\" content=\"en\"></head><body></body></html>");
			var set = CrawlerSuite.Core.Html.PageLanguageSet.Resolve("https://x/p.html", doc, null, "");
			Assert.Equal(new[] { "en" }, set);
		}

		[Fact]
		public void Doc_Undeclared_EmptyDefault_ReturnsEmptySet()
		{
			var doc = Doc("<html><body><p>no language declared</p></body></html>");
			var set = CrawlerSuite.Core.Html.PageLanguageSet.Resolve("https://x/p.html", doc, null, "");
			Assert.Empty(set);
		}

		[Fact]
		public void Doc_Undeclared_NonEmptyDefault_ReturnsDefaultSingleton()
		{
			// Spelling passes a real default, so it always gets at least one language.
			var doc = Doc("<html><body><p>no language declared</p></body></html>");
			var set = CrawlerSuite.Core.Html.PageLanguageSet.Resolve("https://x/p.html", doc, null, "de");
			Assert.Equal(new[] { "de" }, set);
		}

		[Fact]
		public void Doc_OverrideWins_OverHonestHtmlLang()
		{
			var doc = Doc("<html lang=\"nl\"><body></body></html>");
			var set = CrawlerSuite.Core.Html.PageLanguageSet.Resolve(
				"https://x/nl/home/foo.html", doc, Overrides(), "");
			Assert.Equal(new[] { "de" }, set);
		}

		[Fact]
		public void Doc_MultiLanguageOverride_YieldsNoSingleAnchor()
		{
			// de+en page → set size 2 → the quote system check must NOT anchor on one.
			var doc = Doc("<html lang=\"de\"><body></body></html>");
			var set = CrawlerSuite.Core.Html.PageLanguageSet.Resolve(
				"https://x/de/home/service.html", doc, Overrides(), "");
			Assert.Equal(new[] { "de", "en" }, set);
			Assert.NotEqual(1, set.Count);
		}

		// ── HasOverride ───────────────────────────────────────
		// True exactly when Resolve would return an override set: a matching prefix with a
		// non-empty language list (longest key wins, as in Resolve).

		[Fact]
		public void HasOverride_MatchingPrefix_True()
		{
			Assert.True(CrawlerSuite.Core.Html.PageLanguageSet.HasOverride(
				"https://x/de/home/service.html", Overrides()));
		}

		[Fact]
		public void HasOverride_NoMatchingPrefix_False()
		{
			Assert.False(CrawlerSuite.Core.Html.PageLanguageSet.HasOverride(
				"https://x/en/home/other.html", Overrides()));
		}

		[Fact]
		public void HasOverride_NoOverrides_False()
		{
			Assert.False(CrawlerSuite.Core.Html.PageLanguageSet.HasOverride(
				"https://x/de/home/service.html", null));
		}
	}
}
