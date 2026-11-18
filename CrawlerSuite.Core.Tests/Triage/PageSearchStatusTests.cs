using System.Text;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>
	/// The "In search" / "Not in search" status of a page in triage: taken from log 08, shown as a pill
	/// with its reason, used by the filter chips, and listing pages in search before the others
	/// (after security and bidi). Invented addresses only.
	/// </summary>
	public class PageSearchStatusTests : IDisposable
	{
		private readonly string snapshot = Path.Combine(Path.GetTempPath(), "cs-pss-" + Guid.NewGuid().ToString("N"));

		public PageSearchStatusTests() => Directory.CreateDirectory(Path.Combine(snapshot, "download"));

		public void Dispose()
		{
			try { Directory.Delete(snapshot, recursive: true); } catch (IOException) { }
		}

		private static TriageItem Item(string page, string type = "WORD_DOUBLED") =>
			new(type + "|" + page, [type + "|" + page], page, type, type, "Text", "Doubled word", "x x", "x x", null);

		[Theory]
		[InlineData("in search", "In search", PageSearchStatus.InKey)]
		[InlineData("noindex in page", "Not in search", PageSearchStatus.OutKey)]
		[InlineData("noindex by header", "Not in search", PageSearchStatus.OutKey)]
		[InlineData("canonical elsewhere", "Not in search", PageSearchStatus.OutKey)]
		[InlineData("redirects to https://site.test/new.html", "Not in search", PageSearchStatus.OutKey)]
		[InlineData(PageSearchStatus.PdfIn, "In search", PageSearchStatus.InKey)]
		[InlineData(PageSearchStatus.Unknown, "Search unknown", PageSearchStatus.UnknownKey)]
		[InlineData(null, "", PageSearchStatus.NoneKey)]
		public void Label_AndChipKey(string? search, string label, string key)
		{
			Assert.Equal(label, PageSearchStatus.LabelOf(search));
			Assert.Equal(key, PageSearchStatus.KeyOf(search));
		}

		[Fact]
		public void Reason_NamesWhy()
		{
			Assert.Contains("robots meta tag", PageSearchStatus.ReasonOf("noindex in page"));
			Assert.Contains("X-Robots-Tag", PageSearchStatus.ReasonOf("noindex by header"));
			Assert.Contains("canonical names another page", PageSearchStatus.ReasonOf("canonical elsewhere"));
			Assert.Contains("robots.txt is not read", PageSearchStatus.ReasonOf("in search"));
			Assert.Equal(string.Empty, PageSearchStatus.ReasonOf(null));
			Assert.Contains("redirects to https://site.test/new.html", PageSearchStatus.ReasonOf("redirects to https://site.test/new.html"));
			Assert.Contains("X-Robots-Tag", PageSearchStatus.ReasonOf(PageSearchStatus.PdfIn));
			Assert.StartsWith("Search unknown", PageSearchStatus.ReasonOf(PageSearchStatus.Unknown));
		}

		[Fact]
		public void ForPages_TakesLog08_ThenRedirects_ThenThePdfsHeader_ElseUnknown()
		{
			File.WriteAllText(Path.Combine(snapshot, "08-seo-data.csv"),
				"url;robots;title;title_length;description;description_length;keywords;source;h1_count;search\r\n" +
				"https://site.test/page.html;;T;1;;0;;discovery;1;in search\r\n", new UTF8Encoding(true));
			File.WriteAllText(Path.Combine(snapshot, LogFileNames.RedirectAnalysis),
				"https://site.test/old.html | 301 | https://site.test/gone.html > 404\n", new UTF8Encoding(false));
			File.WriteAllText(Path.Combine(snapshot, LogFileNames.CrawlerIndex),
				"a.pdf|https://site.test/a.pdf|discovery\nb.pdf|https://site.test/b.pdf|discovery\n", new UTF8Encoding(false));
			File.WriteAllBytes(Path.Combine(snapshot, "download", "a.pdf"), [0x25, 0x50, 0x44, 0x46]);
			File.WriteAllBytes(Path.Combine(snapshot, "download", "b.pdf"), [0x25, 0x50, 0x44, 0x46]);
			File.WriteAllText(Path.Combine(snapshot, "download", "b" + HeaderSidecar.HeaderSidecarExtension),
				"=== RESPONSE ===\nX-Robots-Tag: noindex\n", new UTF8Encoding(false));

			var map = PageSearchStatus.ForPages(snapshot,
			[
				"https://site.test/page.html", "https://site.test/old.html", "https://site.test/a.pdf", "https://site.test/b.pdf",
				"https://site.test/lost.html", "https://site.test/css/site.css", SharedEntries.EntryOf("X", "y", "z"),
			]);

			Assert.Equal("in search", map["https://site.test/page.html"]);
			Assert.Equal("redirects to https://site.test/gone.html", map["https://site.test/old.html"]);
			Assert.Equal(PageSearchStatus.PdfIn, map["https://site.test/a.pdf"]);
			Assert.Equal("noindex by header", map["https://site.test/b.pdf"]);
			Assert.Equal(PageSearchStatus.Unknown, map["https://site.test/lost.html"]);
			Assert.Equal(5, map.Count); // the stylesheet and the shared entry are no pages
		}

		[Fact]
		public void PagesInSearch_ComeFirst_AfterNothingElseDecides()
		{
			var items = new[] { Item("https://site.test/a.html"), Item("https://site.test/b.html"), Item("https://site.test/c.html") };
			var search = new Dictionary<string, string>
			{
				["https://site.test/a.html"] = "noindex in page",
				["https://site.test/c.html"] = "in search",
			};

			var pages = TriageBoard.Pages(items, TriageSwitches.Defaults(), new TriageLedger(), search);

			Assert.Equal(["https://site.test/c.html", "https://site.test/a.html", "https://site.test/b.html"], pages.Select(p => p.Url));
			Assert.Equal("in search", pages[0].Search);
			Assert.True(pages[0].InSearch);
			Assert.Null(pages[2].Search);
			Assert.False(pages[2].InSearch);
		}

		[Fact]
		public void WithoutStatuses_TheOrderIsAsBefore()
		{
			var items = new[] { Item("https://site.test/b.html"), Item("https://site.test/a.html") };
			Assert.Equal(["https://site.test/a.html", "https://site.test/b.html"],
				TriageBoard.Pages(items, TriageSwitches.Defaults(), new TriageLedger()).Select(p => p.Url));
		}

		[Fact]
		public void CanonicalVerdicts_OfNoindexPages_AreKept_WhileLeftOut()
		{
			File.WriteAllText(Path.Combine(snapshot, "16-canonical-issues_comma.csv"),
				"PageUrl,IssueType,CanonicalUrl,Detail\r\n" +
				"https://site.test/hidden.html,CANONICAL_CONFLICT,https://site.test/hidden.html,2 canonical tags found\r\n" +
				"https://site.test/open.html,CANONICAL_CONFLICT,https://site.test/open.html,2 canonical tags found\r\n", new UTF8Encoding(true));
			File.WriteAllText(Path.Combine(snapshot, "08-seo-data.csv"),
				"url;robots;title;title_length;description;description_length;keywords;source;h1_count;search\r\n" +
				"https://site.test/hidden.html;noindex;T;1;;0;;discovery;1;noindex in page\r\n" +
				"https://site.test/open.html;;T;1;;0;;discovery;1;in search\r\n", new UTF8Encoding(true));

			var kept = CanonicalSource.KeptForNoindexPages(snapshot, canonicalsOfNoindexPages: false).ToList();

			Assert.Equal([CanonicalSource.KeyOf(CanonicalSource.Conflict, "https://site.test/hidden.html", "https://site.test/hidden.html")], kept);
			Assert.Empty(CanonicalSource.KeptForNoindexPages(snapshot, canonicalsOfNoindexPages: true));
		}
	}
}
