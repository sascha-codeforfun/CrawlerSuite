using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>The triage search: by address, or by a flagged word (whole word, ignoring case).</summary>
	public class PageSearchTests
	{
		private const string Floe = "https://site.test/de/floe.html";
		private const string Frost = "https://site.test/de/frost.html";
		private const string Tide = "https://site.test/de/tide/water.html";

		private static TriageItem Spelling(string page, string word, string langs = "de") =>
			new($"{SpellingSource.Type}|{page}|{word} ({langs})", [$"{SpellingSource.Type}|{page}|{word} ({langs})"], page,
				SpellingSource.Type, SpellingSource.Type, SpellingSource.Family, SpellingSource.Label, $"{word} ({langs})", word, null);

		private static TriageItem Doubled(string page, string detail) =>
			new($"WORD_DOUBLED|{page}|{detail}", [$"WORD_DOUBLED|{page}|{detail}"], page, "WORD_DOUBLED", "WORD_DOUBLED",
				"Words & spacing", "Doubled word", detail, "rain " + detail, null);

		private static IReadOnlyList<PageSummary> Pages() =>
			TriageBoard.Pages(
				[
					Spelling(Floe, "Eisschole"), Spelling(Floe, "Vapor", "en-GB"),
					Spelling(Frost, "Eisschole"), Doubled(Frost, "ice ice"),
					Spelling(Tide, "Wasserr"),
				],
				TriageSwitches.Defaults(), new TriageLedger());

		private static PageSummary PageOf(string url) => Pages().Single(p => p.Url == url);

		[Fact]
		public void AnEmptySearch_FindsEveryPage()
		{
			Assert.All(Pages(), p => Assert.True(PageSearch.Lets(p, "  ")));
		}

		[Fact]
		public void TheAddress_StillFindsAPage_AnywhereInIt()
		{
			Assert.True(PageSearch.Lets(PageOf(Frost), "de/FROST"));
			Assert.False(PageSearch.Lets(PageOf(Floe), "de/frost"));
		}

		[Fact]
		public void AFlaggedWord_FindsEveryPageWithIt_IgnoringCase()
		{
			var found = Pages().Where(p => PageSearch.Lets(p, "eisschole")).Select(p => p.Url).ToList();

			Assert.Equal([Floe, Frost], found.Order(StringComparer.Ordinal));
			Assert.Equal(2, PageSearch.PagesWithWord(Pages(), "EISSCHOLE"));
		}

		[Fact]
		public void TheWord_IsMatchedWhole_NotAsAPart()
		{
			Assert.False(PageSearch.HasWord(PageOf(Floe), "Eis"));
			Assert.False(PageSearch.HasWord(PageOf(Floe), "Eisschole (de)"));
			Assert.Equal(0, PageSearch.PagesWithWord(Pages(), "schole"));
		}

		[Fact]
		public void TheWordIsFound_WhateverItsLanguages()
		{
			Assert.True(PageSearch.HasWord(PageOf(Floe), "vapor"));
		}

		[Fact]
		public void OtherFindings_AreNotWords()
		{
			// "ice ice" is a doubled-word finding's detail, not a flagged word.
			Assert.False(PageSearch.HasWord(PageOf(Frost), "ice ice"));
			Assert.False(PageSearch.Lets(PageOf(Frost), "ice ice"));
		}

		[Fact]
		public void AnAddressPart_AndAWord_BothFind()
		{
			// "tide" is in one address; nothing flags it as a word.
			var found = Pages().Where(p => PageSearch.Lets(p, "tide")).Select(p => p.Url).ToList();

			Assert.Equal([Tide], found);
		}

		[Fact]
		public void WordPages_CountsEachPageOnce_IgnoringCase()
		{
			var pages = TriageBoard.Pages(
				[Spelling(Floe, "Eisschole"), Spelling(Floe, "eisschole", "en-GB"), Spelling(Frost, "EISSCHOLE"), Spelling(Tide, "Wasserr")],
				TriageSwitches.Defaults(), new TriageLedger());

			var counts = PageSearch.WordPages(pages);

			Assert.Equal(2, PageSearch.PagesOf(counts, "Eisschole"));
			Assert.Equal(1, PageSearch.PagesOf(counts, "wasserr"));
			Assert.Equal(0, PageSearch.PagesOf(counts, "Frost"));
			Assert.Equal(PageSearch.PagesWithWord(pages, "eisschole"), PageSearch.PagesOf(counts, "eisschole"));
		}

		[Fact]
		public void AComposedAndADecomposedWord_AreTheSame()
		{
			var page = TriageBoard.Pages([Spelling(Floe, "Fl\u00F6e")], TriageSwitches.Defaults(), new TriageLedger()).Single();

			Assert.True(PageSearch.HasWord(page, "Flo\u0308e"));
		}
	}
}
