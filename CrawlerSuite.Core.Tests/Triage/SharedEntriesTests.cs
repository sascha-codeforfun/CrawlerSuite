using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>
	/// One defect repeated on several pages: one shared entry, taken off its pages. Only exact
	/// repetition counts; a styled heading is matched per heading text.
	/// </summary>
	public class SharedEntriesTests
	{
		private const string Heading = "STYLED_HEADING";
		private const string Pattern = "<div class=\"tide\">";

		private static TriageItem Item(string type, string page, string detail, string context) =>
			TriageSource.Group([(type, page, detail, context, null)]).Single();

		private static TriageItem Styled(string page, params string[] lines) =>
			Item(Heading, page, Pattern, string.Join(" ¦ ", lines));

		[Fact]
		public void AHeadingPatternOnTwoPages_IsOneSharedItem_WhateverTheTexts()
		{
			var items = SharedEntries.Apply(
			[
				Styled("https://site.test/water.html", "as found: class=\"tide wave\"", "Rising tide ×8", "Rain box"),
				Styled("https://site.test/ice.html", "Rising tide", "Hail box"),
			]);

			var shared = Assert.Single(items);
			Assert.True(shared.IsShared);
			Assert.Equal(SharedEntries.EntryOf(Heading, Pattern, string.Empty), shared.Page);
			Assert.Equal(SharedEntries.KeyOf(Heading, Pattern, string.Empty), Assert.Single(shared.Keys));
			Assert.Equal(["https://site.test/ice.html", "https://site.test/water.html"], shared.Pages);
			Assert.Equal("Rising tide ×8 · Rain box", shared.PageNotes!["https://site.test/water.html"]);
			Assert.Equal("Rising tide · Hail box", shared.PageNotes!["https://site.test/ice.html"]);
			Assert.StartsWith("as found: class=\"tide wave\"", shared.Context);
			Assert.Contains("  https://site.test/ice.html — Rising tide · Hail box", shared.Context);
			Assert.Contains("The same on 2 pages", shared.Context);
		}

		[Fact]
		public void APatternOnOnePageOnly_StaysWithThatPage()
		{
			var lone = Item(Heading, "https://site.test/water.html", "<span class=\"wave\">", "Rain box");
			var items = SharedEntries.Apply(
			[
				lone,
				Styled("https://site.test/water.html", "Rising tide"),
				Styled("https://site.test/ice.html", "Hail box"),
			]);

			Assert.Equal(2, items.Count);
			Assert.Contains(lone, items);
			Assert.Single(items, i => i.IsShared);
		}

		[Fact]
		public void TheSameFindingOnTwoPages_IsShared_NumbersAside_ADifferentOneStays()
		{
			var items = SharedEntries.Apply(
			[
				Item("MALFORMED_HTML", "https://site.test/a.html", "unclosed div at 3", "<div class=\"vapour\">"),
				Item("MALFORMED_HTML", "https://site.test/b.html", "unclosed div at 4", "<div class=\"vapour\">"),
				Item("MALFORMED_HTML", "https://site.test/c.html", "unclosed div at 5", "<div class=\"steam\">"),
			]);

			Assert.Equal(2, items.Count);
			var shared = Assert.Single(items, i => i.IsShared);
			Assert.Equal(2, shared.Pages!.Count);
			Assert.Single(items, i => i.Page == "https://site.test/c.html");
		}

		[Fact]
		public void OnOnePage_NothingIsShared()
		{
			TriageItem[] items =
			[
				Styled("https://site.test/water.html", "Rising tide"),
				Item("WORD_DOUBLED", "https://site.test/water.html", "the the", "and the the sea"),
			];
			Assert.Same(items, SharedEntries.Apply(items));
		}

		[Fact]
		public void SpellingBrokenLinksSelfLinks_AreNeverShared()
		{
			TriageItem Of(string type, string page) => new(type + page, [type + page], page, type, type, "f", "l", "d", "c", null);
			TriageItem[] items =
			[
				Of(SpellingSource.Type, "https://site.test/a.html"), Of(SpellingSource.Type, "https://site.test/b.html"),
				Of(SelfLinkSource.Type, "https://site.test/a.html"), Of(SelfLinkSource.Type, "https://site.test/b.html"),
			];
			Assert.Same(items, SharedEntries.Apply(items));
		}

		[Fact]
		public void ManyPages_TheItemNamesTen_TheRestCounted()
		{
			var pages = Enumerable.Range(1, 12).Select(n => Styled($"https://site.test/p{n:00}.html", "Rising tide")).ToList();
			var shared = Assert.Single(SharedEntries.Apply(pages));
			Assert.Equal(12, shared.Pages!.Count);
			Assert.Contains("… and 2 more (all in the ticket)", shared.Context);
			Assert.DoesNotContain("p12.html", shared.Context);
		}

		[Fact]
		public void Title_NamesTheDefectAndWhatIdentifiesIt()
		{
			var title = SharedEntries.TitleOf(SharedEntries.EntryOf(Heading, Pattern, string.Empty));
			Assert.EndsWith("— " + Pattern, title);
			Assert.Equal("https://site.test/a.html", SharedEntries.TitleOf("https://site.test/a.html"));
		}

		[Fact]
		public void Board_ListsASharedEntryWhenItsSwitchIsOn_AfterThePages()
		{
			var items = SharedEntries.Apply(
			[
				Styled("https://site.test/water.html", "Rising tide"),
				Styled("https://site.test/ice.html", "Rising tide"),
				Item("WORD_DOUBLED", "https://site.test/water.html", "the the", "and the the sea"),
			]);

			var pages = TriageBoard.Pages(items, TriageSwitches.Defaults(), new TriageLedger()); // styled headings on by default

			Assert.Equal(2, pages.Count);
			Assert.Equal("https://site.test/water.html", pages[0].Url);
			Assert.True(SharedEntries.IsShared(pages[1].Url));
			Assert.Single(pages[1].Triaged);
		}

		[Fact]
		public void Board_ASharedEntryWhoseSwitchRidesAlong_Waits()
		{
			var items = SharedEntries.Apply(
			[
				Item("LINE_BREAK_GLUE_INERT", "https://site.test/water.html", "glue", "<p>salt&nbsp;</p>"),
				Item("LINE_BREAK_GLUE_INERT", "https://site.test/ice.html", "glue", "<p>salt&nbsp;</p>"),
			]);

			Assert.Single(items, i => i.IsShared);
			Assert.Empty(TriageBoard.Pages(items, TriageSwitches.Defaults(), new TriageLedger())); // rides along by default
		}

		[Fact]
		public void Modes_FromTheConfiguration_OverTheDefaults()
		{
			var modes = TriageSwitches.ModesFrom(new Dictionary<string, string>
			{
				["LINE_BREAK_GLUE_INERT"] = "on",
				["STYLED_HEADING"] = "RideAlong",
				["NO_SUCH_SWITCH"] = "On",
				["WORD_DOUBLED"] = "sideways",
				[TriageSwitches.IdOf(Core.Findings.FindingsCatalog.InvisibleCause.Bidi)] = "Off",
			});

			Assert.Equal(TriageMode.On, modes["LINE_BREAK_GLUE_INERT"]);
			Assert.Equal(TriageMode.RideAlong, modes["STYLED_HEADING"]);
			Assert.Equal(TriageMode.On, modes["WORD_DOUBLED"]); // unknown value: default
			Assert.False(modes.ContainsKey("NO_SUCH_SWITCH"));
			Assert.Equal(TriageMode.On, modes[TriageSwitches.IdOf(Core.Findings.FindingsCatalog.InvisibleCause.Bidi)]); // locked

			var kept = TriageSwitches.ToConfigured(modes);
			Assert.Equal(2, kept.Count);
			Assert.Equal("On", kept["LINE_BREAK_GLUE_INERT"]);
			Assert.Equal("RideAlong", kept["STYLED_HEADING"]);
			Assert.Equal(TriageSwitches.Defaults(), TriageSwitches.ModesFrom(null));
		}
	}
}
