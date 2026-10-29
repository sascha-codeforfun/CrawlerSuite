using System.Text;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>Content-quality triage: items, switches, the ledger and the page list.</summary>
	public class TriageTests : IDisposable
	{
		private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
		private readonly string site;
		private readonly string snapshot;

		public TriageTests()
		{
			site = Path.Combine(Path.GetTempPath(), "cs-triage-" + Guid.NewGuid().ToString("N"));
			snapshot = Path.Combine(site, "2026-10-11-10-00-00");
			Directory.CreateDirectory(Path.Combine(snapshot, "download"));
			File.WriteAllText(Path.Combine(snapshot, "download", "a.html"), "<html></html>");
			Write("02-crawler-index.log", "a.html|https://site.test/a.html|discovery", "b.html|https://site.test/b.html|discovery");
		}

		public void Dispose()
		{
			try { Directory.Delete(site, recursive: true); } catch (IOException) { }
		}

		private void Write(string name, params string[] lines) =>
			File.WriteAllText(Path.Combine(snapshot, name), "﻿" + string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));

		private void WriteFindings()
		{
			Write("10-content-quality-issues.log",
				"Filename|IssueType|Detail|Context",
				"a.html|QUOTE_UNMATCHED|„ without closer|„Hallo",
				"a.html|ADJACENT_ANCHOR|[299] „“ + „roid“|<a>And</a><a></a><a>roid</a>",
				"a.html|ADJACENT_ANCHOR|[153] „And“ + „“|<a>And</a><a></a><a>roid</a>",
				"b.html|LIGATURE|Ligature ﬁ|the ﬁrst");
			Write("22-cms-template-authoring-defects_comma.csv",
				"Filename,Url,IssueType,Detail,Context",
				"a.html,https://site.test/a.html,INVISIBLE_CHAR_IN_BODY,ZWSP (U+200B) found at 2 position(s),x",
				"a.html,https://site.test/a.html,INVISIBLE_CHAR_IN_BODY,invisible (U+00AD) found at 9 position(s),x",
				// Another context than on a.html: the same finding identical on both pages would be one shared entry.
				"b.html,https://site.test/b.html,INVISIBLE_CHAR_IN_BODY,ZWSP (U+200B) found at 1 position(s),y",
				"b.html,https://site.test/b.html,INVISIBLE_CHAR_IN_BODY,bidi control (U+202E) found at 1 position(s),x");
		}

		// ── Items ────────────────────────────────────────────────────────────────

		[Fact]
		public void Items_AdjacentAnchorsAreOneItemPerPage_InSourceOrder()
		{
			WriteFindings();
			var adjacent = Assert.Single(TriageSource.Read(snapshot), i => i.Type == "ADJACENT_ANCHOR");

			Assert.Equal(2, adjacent.Keys.Count);
			Assert.StartsWith("[01] „And“", adjacent.Detail);
			Assert.Contains("[02] „“ + „roid“", adjacent.Detail);
			Assert.Equal("https://site.test/a.html", adjacent.Page);
		}

		[Fact]
		public void Items_InvisibleCharactersFallUnderTheirCause()
		{
			WriteFindings();
			var items = TriageSource.Read(snapshot);

			Assert.Equal(2, items.Count(i => i.SwitchId == TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.PasteDebris)));
			Assert.Single(items, i => i.SwitchId == TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.SoftHyphens));
			Assert.Single(items, i => TriageSwitches.IsBidi(i.SwitchId));
		}

		[Fact]
		public void Key_IgnoresPositionsAndCounts()
		{
			Assert.Equal(
				TriageSource.KeyOf("INVISIBLE_CHAR_IN_BODY", "https://s.test/a", "SHY found at 3 position(s)"),
				TriageSource.KeyOf("INVISIBLE_CHAR_IN_BODY", "https://s.test/a", "SHY found at 12 position(s)"));
			Assert.Equal(
				TriageSource.KeyOf("ADJACENT_ANCHOR", "https://s.test/a", "[153] „And“ + „“"),
				TriageSource.KeyOf("ADJACENT_ANCHOR", "https://s.test/a", "[160] „And“ + „“"));
		}

		// ── Switches ─────────────────────────────────────────────────────────────

		[Fact]
		public void Switches_Defaults()
		{
			var modes = TriageSwitches.Defaults();

			Assert.Equal(TriageMode.Off, modes[TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.SoftHyphens)]);
			Assert.Equal(TriageMode.RideAlong, modes[TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.PasteDebris)]);
			Assert.Equal(TriageMode.On, modes["QUOTE_UNMATCHED"]);
			Assert.True(TriageSwitches.All.Single(s => TriageSwitches.IsBidi(s.Id)).Locked);
		}

		[Fact]
		public void Switches_Defaults_EditorFixableOn_RestRidesAlong()
		{
			var modes = TriageSwitches.Defaults();
			string[] on =
			[
				"QUOTE_UNMATCHED", "QUOTE_WRONG_OPEN", "QUOTE_WRONG_CLOSE", "QUOTE_WRONG_APOSTROPHE", "LIGATURE",
				"WORD_COLLISION", "WORD_DOUBLED", "WORD_SPLIT_BY_FORMATTING", "SPACE_AFTER_HYPHEN", "SPACE_BEFORE_HYPHEN", "SPLIT_WORD_ANCHOR", "ADJACENT_ANCHOR",
				"LANGUAGE_MISMATCH", "UNWANTED_PATTERN", SpellingSource.Type,
				TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.Bidi),
				TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.DirectionMarks),
				TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.Joiners),
				TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.Other),
				.. new[] { CrawlerSuite.Core.Links.BrokenLinkReach.PageView, CrawlerSuite.Core.Links.BrokenLinkReach.Click,
					CrawlerSuite.Core.Links.BrokenLinkReach.Elsewhere, CrawlerSuite.Core.Links.BrokenLinkReach.Unknown }.Select(BrokenLinkSource.SwitchIdOf),
				SelfLinkSource.Type, ForwardingLinkSource.External, ForwardingLinkSource.SameHost, CanonicalSource.Chain, CanonicalSource.Conflict, CanonicalSource.Redirect, CanonicalSource.Noindex,
				ImageSource.Truncated, ImageSource.FileOversize, ImageSource.DimensionsOversize, ImageSource.LocationOrDevice,
				"EMBEDDED_BOM_IN_BODY", "STYLED_HEADING",
			];
			string[] rideAlong =
			[
				"QUOTE_MIXED_KIND", "QUOTE_SYSTEM_MIX", "CONTROL_CHARS_IN_CONTENT", "DECOMPOSED_TEXT_IN_CONTENT",
				"MIXED_NORMALIZATION_IN_CONTENT", "LINE_BREAK_GLUE_INERT", "LINE_BREAK_GLUE_DOUBLE_SPACE",
				"BARE_TEXT_IN_CONTAINER", "MISPLACED_ANCHOR_EMPTY", "ARIA_HIDDEN_FOCUSABLE", "ARIA_HIDDEN_FOCUSABLE_CONDITIONAL",
				"ARIA_HIDDEN_FOCUSABLE_UNDETERMINED", "ARIA_REFERENCE_BROKEN",
				"MISPLACED_ANCHOR_EMPTY_CONDITIONAL", "MISPLACED_ANCHOR_EMPTY_UNDETERMINED",
				"MALFORMED_HTML", "CONTENT_BEFORE_DOCTYPE", "PUNCTUATION_WEAK",
				TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.PasteDebris),
				TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.Controls),
				.. SeoSource.Types.Select(t => t.Type), SeoSource.H1Missing, SeoSource.H1Several,
				.. new[] { CrawlerSuite.Core.Links.BrokenLinkReach.Script, CrawlerSuite.Core.Links.BrokenLinkReach.Undecided,
					CrawlerSuite.Core.Links.BrokenLinkReach.Never }.Select(BrokenLinkSource.SwitchIdOf),
			];

			Assert.All(on, id => Assert.Equal(TriageMode.On, modes[id]));
			Assert.All(rideAlong, id => Assert.Equal(TriageMode.RideAlong, modes[id]));
			Assert.Equal(TriageMode.Off, modes[TriageSwitches.SoftHyphens]);
			Assert.Equal(TriageMode.Off, modes["ARIA_HIDDEN_FOCUSABLE_CLEARED"]);
			Assert.Equal(TriageMode.Off, modes["MISPLACED_ANCHOR_EMPTY_CLEARED"]);
			Assert.Equal(TriageMode.Off, modes[ImageSource.Tiny]);
			Assert.Equal(TriageMode.Off, modes[ImageSource.People]);
			Assert.Equal(TriageMode.Off, modes[SeoSource.H1Repeated]);
			Assert.Equal(on.Length + rideAlong.Length + 6, modes.Count); // every switch has a stated default
		}

		[Fact]
		public void Switches_BidiCannotBeSwitchedOff()
		{
			var modes = TriageSwitches.Defaults();
			var bidi = TriageSwitches.IdOf(FindingsCatalog.InvisibleCause.Bidi);
			modes[bidi] = TriageMode.Off;

			Assert.Equal(TriageMode.On, TriageSwitches.ModeOf(modes, bidi));
		}

		[Fact]
		public void Switches_MirrorResultsOrder()
		{
			Assert.Equal("QUOTE_UNMATCHED", TriageSwitches.All[0].Id);
			Assert.DoesNotContain(TriageSwitches.All, s => s.Id == TriageSwitches.Invisible);
		}

		// ── Page list ────────────────────────────────────────────────────────────

		[Fact]
		public void Pages_BidiFirst_RideAlongNeverListsAPage_OffIsLeftOut()
		{
			WriteFindings();
			var pages = TriageBoard.Pages(TriageSource.Read(snapshot), TriageSwitches.Defaults(), new TriageLedger());

			Assert.Equal("https://site.test/b.html", pages[0].Url); // bidi
			Assert.True(pages[0].HasBidi);
			var a = pages.Single(p => p.Url == "https://site.test/a.html");
			Assert.Equal(2, a.Triaged.Count);                   // quote + adjacent
			Assert.Single(a.RideAlong);                         // paste debris
			Assert.DoesNotContain(a.All, i => i.SwitchId.EndsWith("SoftHyphens", StringComparison.Ordinal));
			Assert.Equal(PageStatus.Open, a.Status);
		}

		[Fact]
		public void Pages_OnlyRideAlong_NotListed()
		{
			Write("22-cms-template-authoring-defects_comma.csv",
				"Filename,Url,IssueType,Detail,Context",
				"a.html,https://site.test/a.html,INVISIBLE_CHAR_IN_BODY,ZWSP (U+200B) found at 2 position(s),x");

			Assert.Empty(TriageBoard.Pages(TriageSource.Read(snapshot), TriageSwitches.Defaults(), new TriageLedger()));
		}

		// ── Ledger ───────────────────────────────────────────────────────────────

		private (TriageLedger Ledger, PageSummary Page) PageA()
		{
			WriteFindings();
			var ledger = new TriageLedger();
			var page = TriageBoard.Pages(TriageSource.Read(snapshot), TriageSwitches.Defaults(), ledger).Single(p => p.Url.EndsWith("a.html"));
			return (ledger, page);
		}

		private PageSummary Again(TriageLedger ledger) =>
			TriageBoard.Pages(TriageSource.Read(snapshot), TriageSwitches.Defaults(), ledger).Single(p => p.Url.EndsWith("a.html"));

		[Fact]
		public void Ledger_StatusFollowsTheVerdicts()
		{
			var (ledger, page) = PageA();
			var all = page.All.ToList();

			ledger.Decide(all[0], Verdict.Ticket, string.Empty, Now);
			Assert.Equal(PageStatus.PartlyDecided, Again(ledger).Status);

			foreach (var item in all.Skip(1))
			{
				ledger.Decide(item, Verdict.Wontfix, "template", Now);
			}

			Assert.Equal(PageStatus.ReadyToFile, Again(ledger).Status);

			var filing = ledger.FilePage(page.Url, all, "new", string.Empty, Now);
			Assert.Equal(PageStatus.Filed, Again(ledger).Status);
			Assert.Single(filing.TicketKeys);
		}

		[Fact]
		public void Ledger_FilingWithoutAVerdict_Throws()
		{
			var (ledger, page) = PageA();
			Assert.Throws<InvalidOperationException>(() => ledger.FilePage(page.Url, page.All.ToList(), "new", string.Empty, Now));
		}

		[Fact]
		public void Ledger_SavesAndLoads()
		{
			var (ledger, page) = PageA();
			var item = page.All.First();
			ledger.Decide(item, Verdict.Wontfix, " not ours ", Now);
			ledger.PageOf(page.Url).Tickets.Add("INC-1234");
			var path = TriageLedger.PathFor(snapshot);
			ledger.Save(path);

			var loaded = TriageLedger.Load(path);

			Assert.Equal(Path.Combine(site, TriageLedger.FileName), path);
			Assert.Equal("not ours", loaded.VerdictOf(item)!.Comment);
			Assert.Equal(Verdict.Wontfix, loaded.VerdictOf(item)!.Verdict);
			Assert.Equal(["INC-1234"], loaded.Pages["HTTPS://SITE.TEST/A.HTML"].Tickets);
		}

		[Fact]
		public void Ledger_KeepsDatesOnly_AndRemovesOldTimesOnLoad()
		{
			var (ledger, page) = PageA();
			var item = page.All.First();
			ledger.Decide(item, Verdict.Ticket, string.Empty, Now);
			ledger.FilePage(page.Url, [item], "new", string.Empty, Now);
			var path = TriageLedger.PathFor(snapshot);
			ledger.Save(path);

			var saved = File.ReadAllText(path);
			Assert.False(LedgerDate.HasTimeOfDay(saved));
			Assert.Contains("\"2026-10-11\"", saved, StringComparison.Ordinal);

			// A ledger written before: full timestamps. Read as dates, and saved once without the times.
			File.WriteAllText(path, saved.Replace("\"2026-10-11\"", "\"2026-10-11T14:36:54.8003513Z\"", StringComparison.Ordinal));
			var loaded = TriageLedger.Load(path);
			var stamp = new DateTime(2026, 10, 11, 14, 36, 54, DateTimeKind.Utc);
			Assert.Equal(DateOnly.FromDateTime(stamp.ToLocalTime()), loaded.VerdictOf(item)!.Decided); // the local day of the old time
			Assert.False(LedgerDate.HasTimeOfDay(File.ReadAllText(path)));
		}

		[Fact]
		public void Ledger_GoneIsGone_DraftsAndFiledAlike()
		{
			var (ledger, page) = PageA();
			foreach (var item in page.All)
			{
				ledger.Decide(item, Verdict.Ticket, string.Empty, Now);
			}

			ledger.FilePage(page.Url, page.All.ToList(), "new", string.Empty, Now);
			var quote = page.All.Single(i => i.Type == "QUOTE_UNMATCHED");

			// The next crawl: the quote is fixed.
			var remaining = page.All.Where(i => i != quote).SelectMany(i => i.Keys);
			int removed = ledger.Reconcile("2026-10-12-10-00-00", remaining);

			Assert.Equal(1, removed);
			Assert.Null(ledger.VerdictOf(quote));
			Assert.Equal("2026-10-12-10-00-00", ledger.Snapshot);
		}

		[Fact]
		public void Ledger_NewFindingOnAFiledPage_Reopens()
		{
			var (ledger, page) = PageA();
			var all = page.All.ToList();
			var quote = all.Single(i => i.Type == "QUOTE_UNMATCHED");
			var before = all.Where(i => i != quote).ToList();
			foreach (var item in before)
			{
				ledger.Decide(item, Verdict.Ticket, string.Empty, Now);
			}

			ledger.FilePage(page.Url, before, "new", string.Empty, Now);

			// The quote is "new" since that filing: the page is reopened with only it open.
			Assert.Equal(PageStatus.Reopened, Again(ledger).Status);
			Assert.Equal(3, ledger.PreviouslyTicketed(page.Url).Count); // adjacent (2 junctions) + paste debris
		}

		[Fact]
		public void Ledger_OlderSnapshot_IsNotAccepted()
		{
			var ledger = new TriageLedger();
			ledger.Reconcile("2026-10-12-10-00-00", []);

			Assert.False(ledger.AcceptsSnapshot("2026-10-11-10-00-00"));
			Assert.True(ledger.AcceptsSnapshot("2026-10-12-10-00-00"));
			Assert.True(ledger.AcceptsSnapshot("2026-10-13-10-00-00"));
		}

		[Fact]
		public void Ledger_ChangingAFiledVerdict_MakesItADraftAgain()
		{
			var (ledger, page) = PageA();
			var all = page.All.ToList();
			foreach (var item in all)
			{
				ledger.Decide(item, Verdict.Ticket, string.Empty, Now);
			}

			ledger.FilePage(page.Url, all, "new", string.Empty, Now);
			ledger.Decide(all[0], Verdict.Wontfix, string.Empty, Now);

			Assert.Equal(PageStatus.ReadyToFile, Again(ledger).Status);
		}

		[Fact]
		public void Ledger_PageOfKey()
		{
			Assert.Equal("https://s.test/a", TriageLedger.PageOfKey("TYPE|https://s.test/a|detail | with bar"));
		}
	}
}
