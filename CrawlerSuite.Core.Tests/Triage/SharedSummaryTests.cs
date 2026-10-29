using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>
	/// The head of a shared entry's ticket: where on the site it appears, the texts that repeat, a
	/// few pages to open, every page by host — and the card's own sentences for what and how.
	/// </summary>
	public class SharedSummaryTests
	{
		private static TriageItem Shared(IReadOnlyList<string> pages, Dictionary<string, string>? notes = null) =>
			new("k", ["k"], SharedEntries.EntryOf("STYLED_HEADING", "<span class=\"h1\">", string.Empty), "STYLED_HEADING", "STYLED_HEADING",
				"Template defects", "Styled headings", "<span class=\"h1\">", "context", null, Pages: pages, PageNotes: notes);

		[Fact]
		public void Sections_LeaveOutAFolderEveryPageShares_AndNameAnotherHost()
		{
			string[] pages =
			[
				"https://site.test/en/home/water/rain.html",
				"https://site.test/en/home/water/tide.html",
				"https://site.test/en/home/ice/hail.html",
				"https://site.test/fr/home/water/pluie.html",
				"https://site.test/en/home/index.html",
				"https://help.site.test/en/home/water/wave.html",
			];

			var summary = SharedSummary.Of(Shared(pages));

			Assert.Equal(6, summary.Pages);
			Assert.Equal(2, summary.Hosts);
			Assert.Equal(
				[("en/water", 2), ("en", 1), ("en/ice", 1), ("fr/water", 1), ("help.site.test › en/water", 1)],
				summary.Sections);
			Assert.Equal(5, summary.SectionCount);
			Assert.Equal(("https://site.test", 5), (summary.ByHost[0].Host, summary.ByHost[0].Paths.Count));
			Assert.Equal("/en/home/ice/hail.html", summary.ByHost[0].Paths[0]);
		}

		[Fact]
		public void Frequent_TextsOnThreePagesOrMore_CountsAside()
		{
			var notes = new Dictionary<string, string>
			{
				["https://site.test/a.html"] = "Rain ×2 · Tide",
				["https://site.test/b.html"] = "Rain · Hail",
				["https://site.test/c.html"] = "Rain · Tide",
				["https://site.test/d.html"] = "Tide ×3",
			};

			var summary = SharedSummary.Of(Shared([.. notes.Keys], notes));

			Assert.Equal([("Rain", 3), ("Tide", 3)], summary.Frequent);
		}

		[Fact]
		public void Examples_OnePerLargestSection_WithWhatWasFound()
		{
			var notes = new Dictionary<string, string>
			{
				["https://site.test/water/a.html"] = "Rain",
				["https://site.test/water/b.html"] = "Tide",
				["https://site.test/ice/c.html"] = "Hail",
				["https://site.test/mist/d.html"] = string.Empty,
				["https://site.test/fog/e.html"] = "Haze",
			};

			var summary = SharedSummary.Of(Shared([.. notes.Keys], notes));

			Assert.Equal(SharedSummary.ExamplesShown, summary.Examples.Count);
			Assert.Equal(("https://site.test/water/a.html", "Rain"), summary.Examples[0]);
			Assert.Equal(3, summary.Examples.Select(e => e.Page.Split('/')[3]).Distinct().Count());
		}

		[Fact]
		public void WhatAndFix_AreTheCardsOwnSentences()
		{
			const string verdict = "Likely a defect, fixed in the template.";
			const string card = verdict + " An element looks like a heading, e.g. <div class=\"h2\">. Sighted readers see a heading. " +
				"Fix once in the template: make it a heading. Rides along by default: dealt with later.";

			Assert.Equal("An element looks like a heading, e.g. <div class=\"h2\">.", SharedSummary.WhatOf(card, verdict));
			Assert.Equal("Fix once in the template: make it a heading.", SharedSummary.FixOf(card));
			Assert.Null(SharedSummary.FixOf("Search engines. A note without a remedy."));
		}

		[Fact]
		public void ARealCard_HasItsFix()
		{
			var basis = CrawlerSuite.Core.Findings.FindingsCatalog.BasisFor("STYLED_HEADING", "<div class=\"h2\">")!.Value.Basis;
			Assert.StartsWith("Fix once in the template", SharedSummary.FixOf(basis.Text));
			Assert.DoesNotContain("Fix", SharedSummary.WhatOf(basis.Text, basis.Verdict));
		}

		[Fact]
		public void TheEntryKeepsWhatWasFound_ApartFromItsPageList()
		{
			var raw = new List<(string Type, string Page, string Detail, string Context, string? Local)>
			{
				("WORD_DOUBLED", "https://site.test/a.html", "the the", "rain on the the water", null),
				("WORD_DOUBLED", "https://site.test/b.html", "the the", "rain on the the water", null),
			};

			var entry = Assert.Single(SharedEntries.Apply(TriageSource.Group(raw)), i => i.IsShared);
			Assert.Equal("rain on the the water", entry.Found);
			Assert.Contains("The same on 2 pages", entry.Context);
		}
	}
}
