using System.Text;
using CrawlerSuite.Core.Display;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>Spelling in triage: one item per word per page, decided after the page's content quality.</summary>
	public class SpellingTriageTests : IDisposable
	{
		private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
		private readonly string site;
		private readonly string snapshot;

		public SpellingTriageTests()
		{
			site = Path.Combine(Path.GetTempPath(), "cs-spelltriage-" + Guid.NewGuid().ToString("N"));
			snapshot = Path.Combine(site, "2026-10-17-10-00-00");
			Directory.CreateDirectory(Path.Combine(snapshot, "download"));
			File.WriteAllText(Path.Combine(snapshot, "download", "a.html"), "<html></html>");
			Write("02-crawler-index.log", "a.html|https://site.test/a.html|discovery", "b.html|https://site.test/b.html|discovery");
			Write("10-content-quality-issues.log", "Filename|IssueType|Detail|Context", "a.html|LIGATURE|Ligature ﬁ|the ﬁrst");
			Write("22-cms-template-authoring-defects_comma.csv", "Filename,Url,IssueType,Detail,Context");
		}

		public void Dispose()
		{
			try { Directory.Delete(site, recursive: true); } catch (IOException) { }
		}

		private void Write(string name, params string[] lines) =>
			File.WriteAllText(Path.Combine(snapshot, name), "﻿" + string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));

		private void WriteSpelling()
		{
			Write("11-spell-errors-unique.log", "Wassre (de)", "water (de) · valid en-GB, en-US");
			Write("12-spell-errors-sources.log",
				"https://site.test/a.html|Wassre (de)",
				"https://site.test/a.html|water (de)",
				"https://site.test/a.html|water (de) (meta[@name=description])",
				"https://site.test/b.html|Wassre (de)");
			Write("13-spell-errors-source-location.log",
				"Wassre (de) @ https://site.test/a.html",
				"    p[#text]  Ein Molekül Wassre ist H2O.",
				"water (de) @ https://site.test/a.html",
				"    li[#text]  Formel für water: H2O");
		}

		[Fact]
		public void EachWord_IsOneItemPerPage_WithExcerptsAndValidIn()
		{
			WriteSpelling();
			var spelling = TriageSource.Read(snapshot).Where(i => i.Type == SpellingSource.Type).ToList();

			Assert.Equal(3, spelling.Count);
			var water = Assert.Single(spelling, i => i.Detail == "water (de)");
			Assert.Equal("https://site.test/a.html", water.Page);
			Assert.Equal("Formel für water: H2O\nDescription", water.Context); // body excerpt and meta field: one item, the field named
			Assert.Equal("Formel für water: H2O\nDescription", water.ContextDe);
			Assert.Equal("en-GB, en-US", water.ValidIn);
			Assert.Equal("SPELLING|https://site.test/a.html|water (de)", Assert.Single(water.Keys));
			Assert.NotNull(water.LocalFile);
			Assert.Equal("Spelling", water.Family);

			var onB = Assert.Single(spelling, i => i.Page == "https://site.test/b.html");
			Assert.Equal(string.Empty, onB.Context); // no excerpt in log 13 for that page
			Assert.Equal(string.Empty, onB.ValidIn);
		}

		[Fact]
		public void AWordADictionaryHoldsByNow_IsNotTriaged()
		{
			WriteSpelling();
			var items = TriageSource.Read(snapshot, word => word == "water");
			Assert.DoesNotContain(items, i => i.Detail.StartsWith("water", StringComparison.Ordinal));
			Assert.Equal(2, items.Count(i => i.Type == SpellingSource.Type));
		}

		[Fact]
		public void Spelling_ListsAPageByItself_AndAllowsLocale()
		{
			WriteSpelling();
			var pages = TriageBoard.Pages(TriageSource.Read(snapshot), TriageSwitches.Defaults(), new TriageLedger());
			Assert.Contains(pages, p => p.Url == "https://site.test/b.html"); // only a typo there
			Assert.Equal(TriageMode.On, TriageSwitches.All.Single(s => s.Id == SpellingSource.Type).Default);
			Assert.True(TriageSwitches.AllowsLocale(SpellingSource.Type));
		}

		[Fact]
		public void WithoutSpellLogs_SpellingVerdictsStay()
		{
			WriteSpelling();
			var ledger = new TriageLedger();
			var typo = TriageSource.Read(snapshot).First(i => i.Type == SpellingSource.Type);
			ledger.Decide(typo, Verdict.Ticket, string.Empty, Now);

			File.Delete(Path.Combine(snapshot, "12-spell-errors-sources.log"));
			var keys = TriageSource.Read(snapshot).SelectMany(i => i.Keys)
				.Concat(SpellingSource.KeptWithoutLogs(snapshot, ledger)).ToList();

			Assert.Equal(0, ledger.Reconcile("2026-10-17-10-00-00", keys));
			Assert.NotNull(ledger.VerdictOf(typo));
		}

		[Fact]
		public void WithSpellLogs_AFixedTypo_IsGone()
		{
			WriteSpelling();
			var ledger = new TriageLedger();
			var typo = TriageSource.Read(snapshot).First(i => i.Detail == "Wassre (de)");
			ledger.Decide(typo, Verdict.Ticket, string.Empty, Now);

			Write("12-spell-errors-sources.log", "https://site.test/a.html|water (de)");
			var keys = TriageSource.Read(snapshot).SelectMany(i => i.Keys)
				.Concat(SpellingSource.KeptWithoutLogs(snapshot, ledger)).ToList();

			Assert.Equal(1, ledger.Reconcile("2026-10-17-10-00-00", keys));
		}

		[Fact]
		public void TheWord_IsMarkedWhereItStandsAlone()
		{
			var marked = QualityMarks.For(SpellingSource.Type, "Wassre und Wassreleitung, Wassre.", "Wassre (de)", null);
			Assert.Equal(new[] { 0, 26 }, marked.Marks.Select(m => m.Start).ToArray());
			Assert.All(marked.Marks, m => Assert.Equal(MarkRole.Offender, m.Role));
		}

		[Fact]
		public void AHyphenatedWord_IsCovered_OnceEachFlaggedPartIsInADictionary()
		{
			var part = new PartFinding("Wassre");
			part.Add("de", HyphenPartStatus.Unknown);
			var parts = new[] { part, new PartFinding("Molekül") };
			var user = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var siteDic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wassre" };

			Assert.True(DictionaryCoverage.Covers("Molekül-Wassre", parts, user, siteDic));
			Assert.False(DictionaryCoverage.Covers("Molekül-Wassre", parts, user, new HashSet<string>()));
			Assert.True(DictionaryCoverage.Covers("H2O", [], new HashSet<string> { "H2O" }, siteDic));
		}
	}
}
