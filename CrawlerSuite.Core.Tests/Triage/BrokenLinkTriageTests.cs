using System.Text;
using CrawlerSuite.Core.Formats;
using CrawlerSuite.Core.Links;
using CrawlerSuite.Core.Logging;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>
	/// Broken links in triage: the reach of each reference, where the fix is made (stylesheet,
	/// shared template, page), and ledger keys that survive a cache buster.
	/// </summary>
	public class BrokenLinkTriageTests : IDisposable
	{
		private readonly string snapshot = Path.Combine(Path.GetTempPath(), "cs-404t-" + Guid.NewGuid().ToString("N"));

		public BrokenLinkTriageTests() => Directory.CreateDirectory(Path.Combine(snapshot, "download"));

		public void Dispose()
		{
			try { Directory.Delete(snapshot, recursive: true); } catch (IOException) { }
		}

		[Theory]
		[InlineData("script", "", "", BrokenLinkReach.PageView)]
		[InlineData("image", "", "", BrokenLinkReach.PageView)]
		[InlineData("link element", "", "stylesheet", BrokenLinkReach.PageView)]
		[InlineData("link element", "", "preload", BrokenLinkReach.PageView)]
		[InlineData("link element", "", "alternate", BrokenLinkReach.Elsewhere)]
		[InlineData("link element", "", "next", BrokenLinkReach.Never)]
		[InlineData("link", BrokenLinkOrigins.RelativeLink, "", BrokenLinkReach.Click)]
		[InlineData("form action", "", "", BrokenLinkReach.Click)]
		[InlineData("redirect", "", "", BrokenLinkReach.Click)]
		[InlineData("data attribute", "", "", BrokenLinkReach.Script)]
		[InlineData("inline data", "", "", BrokenLinkReach.Script)]
		[InlineData("stylesheet url()", BrokenLinkOrigins.OnLoad, "", BrokenLinkReach.PageView)]
		[InlineData("stylesheet url()", BrokenLinkOrigins.OnDevices, "", BrokenLinkReach.PageView)]
		[InlineData("stylesheet url()", BrokenLinkOrigins.InState, "", BrokenLinkReach.Click)]
		[InlineData("stylesheet url()", BrokenLinkOrigins.WhenPrinted, "", BrokenLinkReach.Click)]
		[InlineData("stylesheet url()", BrokenLinkOrigins.WhenScriptRuns, "", BrokenLinkReach.Script)]
		[InlineData("stylesheet url()", BrokenLinkOrigins.Undecided, "", BrokenLinkReach.Undecided)]
		[InlineData("stylesheet url()", BrokenLinkOrigins.Dead, "", BrokenLinkReach.Never)]
		[InlineData("canonical", BrokenLinkOrigins.CanonicalOnly, "", BrokenLinkReach.Elsewhere)]
		[InlineData("content list", "", "", BrokenLinkReach.Elsewhere)]
		[InlineData(BrokenLinkOrigins.NoReference, BrokenLinkOrigins.Undecided, "", BrokenLinkReach.Unknown)]
		public void Reach_FollowsKindGradeAndRel(string kind, string grade, string rel, string reach) =>
			Assert.Equal(reach, BrokenLinkReach.Of(kind, grade, rel));

		[Fact]
		public void Reach_RanksMostSeriousFirst()
		{
			Assert.True(BrokenLinkReach.Rank(BrokenLinkReach.PageView) < BrokenLinkReach.Rank(BrokenLinkReach.Click));
			Assert.True(BrokenLinkReach.Rank(BrokenLinkReach.Elsewhere) < BrokenLinkReach.Rank(BrokenLinkReach.Unknown));
			Assert.True(BrokenLinkReach.Rank(BrokenLinkReach.Script) < BrokenLinkReach.Rank(BrokenLinkReach.Undecided));
			Assert.True(BrokenLinkReach.Rank(BrokenLinkReach.Undecided) < BrokenLinkReach.Rank(BrokenLinkReach.Never));
			Assert.Equal(7, BrokenLinkReach.All.Count);
		}

		[Fact]
		public void CacheBuster_IsNeutral_TheRestOfTheNameStays()
		{
			var before = "https://site.test/etc/lib/water.min.f00dfeed0123456789abcdef01234567.css";
			var after = "https://site.test/etc/lib/water.min.9f3c01aa5682d9cf0eecde318efe94211.css";
			Assert.Equal(BrokenLinkSource.CacheNeutral(before), BrokenLinkSource.CacheNeutral(after));
			Assert.Equal("https://site.test/etc/lib/water.min.#.css", BrokenLinkSource.CacheNeutral(before));

			// Another file, short hex and words stay apart.
			Assert.NotEqual(BrokenLinkSource.CacheNeutral(before), BrokenLinkSource.CacheNeutral(before.Replace("water", "ice")));
			Assert.Equal("https://site.test/img/cafe.png", BrokenLinkSource.CacheNeutral("https://site.test/img/cafe.png"));
			Assert.Equal("https://site.test/a/deadbeef.js", BrokenLinkSource.CacheNeutral("https://site.test/a/deadbeef.js"));
			Assert.Equal(
				BrokenLinkSource.KeyOf("https://site.test/p.html", before),
				BrokenLinkSource.KeyOf("https://site.test/p.html", after));
		}

		private static BrokenLinkSource.Row Ref(string broken, string kind, string referrer, string asWritten, string grade = "") =>
			new(broken, kind, referrer, asWritten, grade, string.Empty, BrokenLinkReach.Of(kind, grade));

		[Fact]
		public void Entries_Stylesheet_SharedTemplate_AndPage()
		{
			var rows = new List<BrokenLinkSource.Row>
			{
				Ref("https://site.test/lib/img/ice.png", "stylesheet url()", "https://site.test/lib/water.css", "img/ice.png", BrokenLinkOrigins.Dead),
				Ref("https://site.test/lib/img/steam.png", "stylesheet url()", "https://site.test/lib/water.css", "img/steam.png", BrokenLinkOrigins.OnLoad),
				Ref("https://site.test/etc.lib/rain.js", "script", "https://site.test/a.html", "/etc.lib/rain.js"),
				Ref("https://site.test/etc.lib/rain.js", "script", "https://site.test/b.html", "/etc.lib/rain.js"),
				Ref("https://site.test/shop/snow.html", "link", "https://site.test/shop/a.html", "snow.html", BrokenLinkOrigins.RelativeLink),
				Ref("https://site.test/ghost.html", BrokenLinkOrigins.NoReference, string.Empty, string.Empty, BrokenLinkOrigins.Undecided),
			};

			var items = BrokenLinkSource.Items(rows, url => url.EndsWith(".html", StringComparison.Ordinal) ? "local.html" : null);

			Assert.Equal(5, items.Count);
			Assert.Equal(2, items.Count(i => i.Page == "https://site.test/lib/water.css")); // the stylesheet: one item per address

			var shared = Assert.Single(items, i => i.Detail == "https://site.test/etc.lib/rain.js");
			Assert.Equal("https://site.test/etc.lib/rain.js", shared.Page); // one entry for the template, not one per page
			Assert.Contains("on 2 page(s)", shared.Context);
			Assert.Contains("https://site.test/b.html", shared.Context);
			Assert.Null(shared.LocalFile);
			Assert.Equal(BrokenLinkReach.PageView, shared.Label);

			var page = Assert.Single(items, i => i.Detail == "https://site.test/shop/snow.html");
			Assert.Equal("https://site.test/shop/a.html", page.Page);
			Assert.Equal("local.html", page.LocalFile);
			Assert.Equal(BrokenLinkSource.SwitchIdOf(BrokenLinkReach.Click), page.SwitchId);
			Assert.Equal(BrokenLinkSource.Family, page.Family);
			Assert.Contains("Check: Open the link", page.Context);

			var ghost = Assert.Single(items, i => i.Detail == "https://site.test/ghost.html");
			Assert.Equal("https://site.test/ghost.html", ghost.Page);
			Assert.Equal(BrokenLinkReach.Unknown, ghost.Label);
		}

		[Fact]
		public void AnItem_TakesItsMostSeriousReach()
		{
			var rows = new List<BrokenLinkSource.Row>
			{
				Ref("https://site.test/drop.png", "data attribute", "https://site.test/a.html", "/drop.png"),
				Ref("https://site.test/drop.png", "image", "https://site.test/a.html", "drop.png"),
			};

			var item = Assert.Single(BrokenLinkSource.Items(rows, _ => null));
			Assert.Equal(BrokenLinkReach.PageView, item.Label);
			Assert.Contains("Referenced as: image — drop.png", item.Context);
			Assert.Contains("Referenced as: data attribute — /drop.png", item.Context);
		}

		[Fact]
		public void ALinkToAPageTheCmsHasButNotOnline_SaysSo()
		{
			var rows = new List<BrokenLinkSource.Row>
			{
				Ref("https://site.test/floe/tide.html", "link", "https://site.test/a.html", "/floe/tide.html"),
				Ref("https://site.test/gone.html", "link", "https://site.test/a.html", "/gone.html"),
			};
			var notOnline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/floe/tide.html" };

			var items = BrokenLinkSource.Items(rows, _ => null, notOnline).ToDictionary(i => i.Detail);

			Assert.Contains("not as online (log 39)", items["https://site.test/floe/tide.html"].Context);
			Assert.Contains("nicht als online (Log 39)", items["https://site.test/floe/tide.html"].ContextDe);
			Assert.DoesNotContain("log 39", items["https://site.test/gone.html"].Context);
		}

		[Fact]
		public void Read_FromLog07_AndAnOldLogWithoutReach()
		{
			var basePath = Path.Combine(snapshot, "07-404-sources");
			ErrorSourcesLog.Write(
			[
				new BrokenReference("https://site.test/img/ice.png", "image", "https://site.test/a.html", "img/ice.png", string.Empty, string.Empty, BrokenLinkReach.PageView),
			], basePath);
			var item = Assert.Single(BrokenLinkSource.Read(snapshot, _ => null));
			Assert.Equal(BrokenLinkReach.PageView, item.Label);
			Assert.True(BrokenLinkSource.HasLogs(snapshot));

			// A six-column log: the reach is worked out from kind and grade.
			var comma = Path.Combine(snapshot, OutputFormats.ByNumber("07")!.PrimaryFileName);
			File.WriteAllText(comma, "BrokenUrl,Kind,Referrer,AsWritten,Grade,Evidence\r\nhttps://site.test/x.html,link,https://site.test/a.html,x.html,,\r\n", new UTF8Encoding(true));
			Assert.Equal(BrokenLinkReach.Click, Assert.Single(BrokenLinkSource.Read(snapshot, _ => null)).Label);
		}

		[Fact]
		public void WithoutLog07_BrokenLinkVerdictsAreKept()
		{
			var ledger = new TriageLedger();
			var item = new TriageItem("k", ["k"], "https://site.test/a.html", BrokenLinkSource.Type, BrokenLinkSource.SwitchIdOf(BrokenLinkReach.Click),
				BrokenLinkSource.Family, BrokenLinkReach.Click, "https://site.test/x.html", string.Empty, null);
			ledger.Decide(item, Verdict.Wontfix, string.Empty, DateTime.Now);

			Assert.False(BrokenLinkSource.HasLogs(snapshot));
			Assert.Equal(["k"], BrokenLinkSource.KeptWithoutLogs(snapshot, ledger));
		}

		[Fact]
		public void RunOverview_CountsEachAddressOnce_UnderItsMostSeriousReach()
		{
			var facts = BrokenLinkOrigins.ReachFacts(
			[
				new BrokenReference("https://site.test/a.png", "image", "https://site.test/p.html", "a.png", "", "", BrokenLinkReach.PageView),
				new BrokenReference("https://site.test/a.png", "data attribute", "https://site.test/p.html", "/a.png", "", "", BrokenLinkReach.Script),
				new BrokenReference("https://site.test/b.html", "link", "https://site.test/p.html", "b.html", "", "", BrokenLinkReach.Click),
			]);

			Assert.True(facts.IsFinding);
			Assert.Equal("2", facts.Chip);
			Assert.Equal([$"{BrokenLinkReach.PageView} · 1", $"{BrokenLinkReach.Click} · 1"], facts.Lines);
		}

		// One page that loads water.css, with an element of class "tap"; the sheet references img/wave.png.
		private BrokenReference GradeOf(string css)
		{
			File.WriteAllText(Path.Combine(snapshot, "02-crawler-index.log"),
				"p.html|https://site.test/p.html|discovery\r\nwater.css|https://site.test/lib/water.css|discovery\r\n", new UTF8Encoding(false));
			File.WriteAllText(Path.Combine(snapshot, "download", "p.html"),
				"<html><head><link rel=\"stylesheet\" href=\"/lib/water.css\"></head><body><a class=\"tap\" href=\"/\">H2O</a></body></html>", new UTF8Encoding(false));
			File.WriteAllText(Path.Combine(snapshot, "download", "water.css"), css, new UTF8Encoding(false));

			var result = BrokenLinkOrigins.Find(snapshot, ["https://site.test/lib/img/wave.png"], "https://site.test/", "*.html", [], null);
			return Assert.Single(result.Rows);
		}

		[Fact]
		public void ARuleInAStateOnAnElementOfThePage_FiresInAState_OnAVisitorsAction()
		{
			var row = GradeOf(".tap:hover{background:url(img/wave.png)}");
			Assert.Equal(BrokenLinkOrigins.InState, row.Grade);
			Assert.Equal(BrokenLinkReach.Click, row.Reach);
			Assert.Contains("(:hover)", row.Evidence);
		}

		[Fact]
		public void ARuleUnderAScreenSizeQuery_FiresOnMatchingDevices_OnEveryPageView()
		{
			var row = GradeOf("@media (min-width: 48em){.tap{background:url(img/wave.png)}}");
			Assert.Equal(BrokenLinkOrigins.OnDevices, row.Grade);
			Assert.Equal(BrokenLinkReach.PageView, row.Reach);
			Assert.Contains("under @media (min-width: 48em)", row.Evidence);
		}

		[Fact]
		public void ARuleUnderAContainerQuery_FiresOnMatchingDevices()
		{
			var row = GradeOf("@container (min-width: 20em){.tap{background:url(img/wave.png)}}");
			Assert.Equal(BrokenLinkOrigins.OnDevices, row.Grade);
			Assert.Equal(BrokenLinkReach.PageView, row.Reach);
		}

		[Fact]
		public void ARuleUnderPrint_FiresWhenPrinted_OnAVisitorsAction()
		{
			var row = GradeOf("@media print{.tap{background:url(img/wave.png)}}");
			Assert.Equal(BrokenLinkOrigins.WhenPrinted, row.Grade);
			Assert.Equal(BrokenLinkReach.Click, row.Reach);
			Assert.Contains("only when printed (@media print)", row.Evidence);
		}

		[Fact]
		public void AStateUnderAScreenQuery_TheStateWins()
		{
			var row = GradeOf("@media (min-width: 48em){.tap:focus{background:url(img/wave.png)}}");
			Assert.Equal(BrokenLinkOrigins.InState, row.Grade);
			Assert.Equal(BrokenLinkReach.Click, row.Reach);
			Assert.Contains("(:focus, under @media (min-width: 48em))", row.Evidence);
		}

		[Fact]
		public void AnUnjudgeableRule_HasAnUndecidedReach_AndSaysWhy()
		{
			var row = GradeOf("@font-face{font-family:Drop;src:url(img/wave.png)}");
			Assert.Equal(BrokenLinkOrigins.Undecided, row.Grade);
			Assert.Equal(BrokenLinkReach.Undecided, row.Reach);
			Assert.Contains("@font-face", row.Evidence);
		}

		[Theory]
		[InlineData("@media print", true)]
		[InlineData("@media only print", true)]
		[InlineData("@media print and (orientation: portrait)", true)]
		[InlineData("@media screen and (min-width: 48em) and @media print", true)]
		[InlineData("@media print, screen", false)]
		[InlineData("@media not print", false)]
		[InlineData("@media (min-width: 48em)", false)]
		[InlineData("@media screen", false)]
		[InlineData("@container (min-width: 20em)", false)]
		public void OnlyWhenPrinted_ReadsTheMediaTypes(string condition, bool printed) =>
			Assert.Equal(printed, BrokenLinkOrigins.OnlyWhenPrinted(condition));

		[Fact]
		public void StatesIn_NamesTheStatesNotThePseudoElements() =>
			Assert.Equal([":hover", ":checked"], CrawlerSuite.Core.Quality.Css.CssSelector.StatesIn(".tap:hover::before + input:checked:hover ~ .ice:first-child"));

		[Fact]
		public void RedirectsToOneAddress_AreNamedAsRedirects_NotAsATemplate()
		{
			var rows = new List<BrokenLinkSource.Row>
			{
				Ref("https://site.test/rain.html", "redirect", "https://site.test/old-rain.html", "/rain.html"),
				Ref("https://site.test/rain.html", "redirect", "https://site.test/older-rain.html", "/rain.html"),
			};

			var item = Assert.Single(BrokenLinkSource.Items(rows, _ => null));
			Assert.Contains("2 address(es) redirect here", item.Context);
			Assert.DoesNotContain("template", item.Context);
		}

		[Fact]
		public void ErrorElsewhere_TheCheckNamesWhereItIsWritten()
		{
			Assert.Contains("canonical", BrokenLinkReach.BrowserCheck(BrokenLinkReach.Elsewhere, "https://site.test/x.html", "canonical"));
			Assert.DoesNotContain("content list", BrokenLinkReach.BrowserCheck(BrokenLinkReach.Elsewhere, "https://site.test/x.html", "canonical"));
			Assert.Contains("content list", BrokenLinkReach.BrowserCheck(BrokenLinkReach.Elsewhere, "https://site.test/x.html", "content list"));
			Assert.DoesNotContain("canonical", BrokenLinkReach.BrowserCheck(BrokenLinkReach.Elsewhere, "https://site.test/x.html", "content list"));
			Assert.Contains("alternate", BrokenLinkReach.BrowserCheck(BrokenLinkReach.Elsewhere, "https://site.test/x.html", "link element"));
		}

		[Fact]
		public void NeverRequested_StatesTheFinding_AndLeavesTheVerdict()
		{
			var check = BrokenLinkReach.BrowserCheck(BrokenLinkReach.Never, "https://site.test/x.png");
			Assert.Contains("Nothing on the crawled pages requests it", check);
			Assert.Contains("for you to decide", check);
			Assert.DoesNotContain("clutter", check);
		}

		[Fact]
		public void RunOverview_LeavesOutAnAddressOnlyACanonicalPointsTo_AsTheChipDoes()
		{
			var facts = BrokenLinkOrigins.ReachFacts(
			[
				new BrokenReference("https://site.test/a.png", "image", "https://site.test/p.html", "a.png", "", "", BrokenLinkReach.PageView),
				new BrokenReference("https://site.test/c.html", "canonical", "https://site.test/p.html", "c.html", BrokenLinkOrigins.CanonicalOnly, "", BrokenLinkReach.Elsewhere),
			]);

			Assert.Equal("1", facts.Chip);
			Assert.Equal([$"{BrokenLinkReach.PageView} · 1"], facts.Lines);
			Assert.Contains("Not counted: 1 referenced only by a canonical", facts.Note);
		}

		[Fact]
		public void TheBrokenLinksCard_ExplainsATriageItem() =>
			Assert.Equal(BrokenLinkSource.Type, CrawlerSuite.Core.Findings.FindingsCatalog.BasisFor(BrokenLinkSource.Type, "https://site.test/x.html")?.Key);
	}
}
