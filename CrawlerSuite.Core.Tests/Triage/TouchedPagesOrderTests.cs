using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>
	/// The order of a new round: pages never ticketed first, then pages ticketed before — the one
	/// ticketed longest ago first — so a page whose editor just had a turn waits.
	/// </summary>
	public class TouchedPagesOrderTests
	{
		private static TriageItem Item(string page, string detail) =>
			new($"WORD_DOUBLED|{page}|{detail}", [$"WORD_DOUBLED|{page}|{detail}"], page, "WORD_DOUBLED", "WORD_DOUBLED",
				"Words & spacing", "Doubled word", detail, "rain " + detail, null);

		[Fact]
		public void NeverTicketedFirst_ThenTheOneTicketedLongestAgo()
		{
			const string Fresh = "https://site.test/fresh.html";
			const string Recent = "https://site.test/recent.html";
			const string Old = "https://site.test/old.html";
			var ledger = new TriageLedger();

			// Each touched page was filed once and has a new finding now.
			var recentFiled = Item(Recent, "ice ice");
			ledger.Decide(recentFiled, Verdict.Ticket, string.Empty, new DateTime(2026, 10, 1));
			ledger.FilePage(Recent, [recentFiled], "new", string.Empty, new DateTime(2026, 10, 1));
			var oldFiled = Item(Old, "tide tide");
			ledger.Decide(oldFiled, Verdict.Ticket, string.Empty, new DateTime(2026, 3, 1));
			ledger.FilePage(Old, [oldFiled], "new", string.Empty, new DateTime(2026, 3, 1));

			TriageItem[] items =
			[
				recentFiled, Item(Recent, "rain rain"), Item(Recent, "hail hail"), Item(Recent, "mist mist"), // most defects
				oldFiled, Item(Old, "wave wave"),
				Item(Fresh, "fog fog"),
			];

			var pages = TriageBoard.Pages(items, TriageSwitches.Defaults(), ledger);

			Assert.Equal([Fresh, Old, Recent], pages.Select(p => p.Url));
			Assert.Null(pages[0].LastFiled);
			Assert.Equal(new DateOnly(2026, 3, 1), pages[1].LastFiled);
		}

		[Fact]
		public void BrokenLinks_AWeakReachRidesAlong()
		{
			var modes = TriageSwitches.Defaults();
			Assert.Equal(TriageMode.On, modes[BrokenLinkSource.SwitchIdOf(CrawlerSuite.Core.Links.BrokenLinkReach.PageView)]);
			Assert.Equal(TriageMode.On, modes[BrokenLinkSource.SwitchIdOf(CrawlerSuite.Core.Links.BrokenLinkReach.Click)]);
			Assert.Equal(TriageMode.On, modes[BrokenLinkSource.SwitchIdOf(CrawlerSuite.Core.Links.BrokenLinkReach.Elsewhere)]);
			Assert.Equal(TriageMode.On, modes[BrokenLinkSource.SwitchIdOf(CrawlerSuite.Core.Links.BrokenLinkReach.Unknown)]);
			Assert.Equal(TriageMode.RideAlong, modes[BrokenLinkSource.SwitchIdOf(CrawlerSuite.Core.Links.BrokenLinkReach.Script)]);
			Assert.Equal(TriageMode.RideAlong, modes[BrokenLinkSource.SwitchIdOf(CrawlerSuite.Core.Links.BrokenLinkReach.Undecided)]);
			Assert.Equal(TriageMode.RideAlong, modes[BrokenLinkSource.SwitchIdOf(CrawlerSuite.Core.Links.BrokenLinkReach.Never)]);
		}
	}
}
