using System.Text;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>
	/// Self-links (log 09) and canonical issues (log 16) in triage: page items with their own
	/// switches, keys that hold across crawls, and what is left out on purpose.
	/// </summary>
	public class PageLinkTriageTests : IDisposable
	{
		private readonly string snapshot = Path.Combine(Path.GetTempPath(), "cs-plt-" + Guid.NewGuid().ToString("N"));

		public PageLinkTriageTests() => Directory.CreateDirectory(Path.Combine(snapshot, "download"));

		public void Dispose()
		{
			try { Directory.Delete(snapshot, recursive: true); } catch (IOException) { }
		}

		private void Write(string name, string content) =>
			File.WriteAllText(Path.Combine(snapshot, name), content, new UTF8Encoding(true));

		[Fact]
		public void SelfLinks_OneItemPerPageAndLink_ExcerptsTogether()
		{
			Write("09-self-link-analysis_comma.csv",
				"File,FileUrl,LinkFound,ContextSnippet\r\n" +
				"water.html,https://site.test/water.html,/water.html,<p>see <a href=\"/water.html\">water</a></p>\r\n" +
				"water.html,https://site.test/water.html,/water.html,<li><a href=\"/water.html\">more water</a></li>\r\n" +
				"ice.html,https://site.test/ice.html,https://site.test/ice.html,<a href=\"https://site.test/ice.html\">ice</a>\r\n");

			var items = SelfLinkSource.Read(snapshot, _ => null);

			Assert.Equal(2, items.Count);
			var water = Assert.Single(items, i => i.Page == "https://site.test/water.html");
			Assert.Equal(SelfLinkSource.Type, water.Type);
			Assert.Equal(SelfLinkSource.Family, water.Family);
			Assert.Equal("/water.html", water.Detail);
			Assert.Equal(2, water.Context.Split('\n').Length);
			Assert.Equal(SelfLinkSource.KeyOf("https://site.test/water.html", "/water.html"), Assert.Single(water.Keys));
		}

		[Fact]
		public void Canonical_ChainConflictRedirectAndNoindex_AreItems_MissingAndExternal_AreLeftOut()
		{
			Write("16-canonical-issues_comma.csv",
				"PageUrl,IssueType,CanonicalUrl,Detail\r\n" +
				"https://site.test/a.html,CANONICAL_CHAIN,https://site.test/b.html,Chain: a → b → c\r\n" +
				"https://site.test/d.html,CANONICAL_CONFLICT,https://site.test/d.html,2 canonical tags found\r\n" +
				"https://site.test/e.html,CANONICAL_404,https://site.test/e.html.html,Canonical target not found\r\n" +
				"https://site.test/f.html,CANONICAL_EXTERNAL,https://vapour.test/f.html,Canonical points outside\r\n" +
				"https://site.test/g.html,CANONICAL_REDIRECT,https://site.test/old.html,Canonical target redirects (Found) to: https://site.test/g.html\r\n" +
				"https://site.test/h.html,CANONICAL_NOINDEX,https://site.test/ice.html,Canonical target is kept out of search (noindex in page)\r\n");

			var items = CanonicalSource.Read(snapshot, _ => null);

			Assert.Equal(4, items.Count);
			Assert.Equal("https://site.test/g.html", Assert.Single(items, i => i.Type == CanonicalSource.Redirect).Page);
			Assert.Equal("https://site.test/h.html", Assert.Single(items, i => i.Type == CanonicalSource.Noindex).Page);
			Assert.All(CanonicalSource.Types, t => Assert.Equal(TriageMode.On, TriageSwitches.Defaults()[t.Type]));
			Assert.All(CanonicalSource.Types, t => Assert.NotNull(FindingsCatalog.BasisFor(t.Type, string.Empty)));
			var chain = Assert.Single(items, i => i.Type == CanonicalSource.Chain);
			Assert.Equal("https://site.test/a.html", chain.Page);
			Assert.Equal(CanonicalSource.Family, chain.Family);
			Assert.Equal("Canonical: https://site.test/b.html", chain.Context);
			Assert.Equal(CanonicalSource.KeyOf(CanonicalSource.Chain, "https://site.test/a.html", "https://site.test/b.html"), Assert.Single(chain.Keys));
			Assert.Single(items, i => i.Type == CanonicalSource.Conflict);
		}

		private void WriteCanonicalsAndSearch()
		{
			Write("16-canonical-issues_comma.csv",
				"PageUrl,IssueType,CanonicalUrl,Detail\r\n" +
				"https://site.test/open.html,CANONICAL_CONFLICT,https://site.test/open.html,2 canonical tags found\r\n" +
				"https://site.test/hidden.html,CANONICAL_CONFLICT,https://site.test/hidden.html,2 canonical tags found\r\n" +
				"https://site.test/header.html,CANONICAL_CHAIN,https://site.test/b.html,Chain\r\n" +
				"https://site.test/moved.html,CANONICAL_CHAIN,https://site.test/b.html,Chain\r\n");
			Write("08-seo-data.csv",
				"url;robots;title;title_length;description;description_length;keywords;source;h1_count;search\r\n" +
				"https://site.test/open.html;;T;1;;0;;discovery;1;in search\r\n" +
				"https://site.test/hidden.html;noindex;T;1;;0;;discovery;1;noindex in page\r\n" +
				"https://site.test/header.html;;T;1;;0;;discovery;1;noindex by header\r\n" +
				"https://site.test/moved.html;;T;1;;0;;discovery;1;canonical elsewhere\r\n");
		}

		[Fact]
		public void Canonicals_OfNoindexPages_AreLeftOutOfTriage_ByDefault()
		{
			WriteCanonicalsAndSearch();

			var pages = TriageSource.Read(snapshot).Where(i => CanonicalSource.IsCanonical(i.Type)).Select(i => i.Page).ToList();

			// A page whose canonical names another stays: its canonical is the very thing in question.
			Assert.Equal(["https://site.test/open.html", "https://site.test/moved.html"], pages);
		}

		[Fact]
		public void Canonicals_OfNoindexPages_AreTriaged_WhenSwitchedOn()
		{
			WriteCanonicalsAndSearch();

			var pages = TriageSource.Read(snapshot, canonicalsOfNoindexPages: true).Where(i => CanonicalSource.IsCanonical(i.Type)).ToList();

			Assert.Equal(4, pages.Count);
		}

		[Fact]
		public void SearchByPage_ReadsTheColumn_AndFallsBackToTheRobots()
		{
			Write("08-seo-data.csv",
				"url;robots;title;title_length;description;description_length;keywords;source;h1_count\r\n" +
				"https://site.test/a.html;index;T;1;;0;;discovery;1\r\n" +
				"https://site.test/b.html;noindex;T;1;;0;;discovery;1\r\n");

			var search = SeoSource.SearchByPage(snapshot);

			Assert.Equal(CrawlerSuite.Core.SEO.Search.In, search["https://site.test/a.html"]);
			Assert.Equal(CrawlerSuite.Core.SEO.Search.NoindexInPage, search["https://site.test/b.html"]);
			Assert.Empty(SeoSource.SearchByPage(Path.Combine(snapshot, "download")));
		}

		[Fact]
		public void WithoutTheLogs_NothingIsRead_AndVerdictsAreKept()
		{
			Assert.Empty(SelfLinkSource.Read(snapshot, _ => null));
			Assert.Empty(CanonicalSource.Read(snapshot, _ => null));
			Assert.False(SelfLinkSource.HasLogs(snapshot));
			Assert.False(CanonicalSource.HasLogs(snapshot));

			Write("09-self-link-analysis_comma.csv", "File,FileUrl,LinkFound,ContextSnippet\r\n");
			Assert.True(SelfLinkSource.HasLogs(snapshot));
			Assert.Empty(SelfLinkSource.Read(snapshot, _ => null)); // looked, none found
		}

		[Fact]
		public void Switches_AreOn_UnderTheirOwnFamilies_AndHaveCards()
		{
			var modes = TriageSwitches.Defaults();
			Assert.Equal(TriageMode.On, modes[SelfLinkSource.Type]);
			Assert.Equal(TriageMode.On, modes[CanonicalSource.Chain]);
			Assert.Equal(TriageMode.On, modes[CanonicalSource.Conflict]);
			Assert.Equal((SelfLinkSource.Family, SelfLinkSource.Label), TriageSwitches.Describe(SelfLinkSource.Type));
			Assert.Equal(CanonicalSource.Family, TriageSwitches.Describe(CanonicalSource.Chain).Family);

			Assert.NotNull(FindingsCatalog.BasisFor(SelfLinkSource.Type, string.Empty));
			Assert.NotNull(FindingsCatalog.BasisFor(CanonicalSource.Chain, string.Empty));
			Assert.NotNull(FindingsCatalog.BasisFor(CanonicalSource.Conflict, string.Empty));
		}

		[Fact]
		public void SelfLink_ExcerptMarksTheAddress()
		{
			const string context = "<p>see <a href=\"/water.html\">water</a></p>";
			var marked = CrawlerSuite.Core.Display.QualityMarks.For(SelfLinkSource.Type, context, "/water.html", null);
			var mark = Assert.Single(marked.Marks);
			Assert.Equal("/water.html", context.Substring(mark.Start, mark.Length));
		}

		[Fact]
		public void TriageSource_ReadsThemAsPageItems_WithTheSavedFile()
		{
			Write("02-crawler-index.log", "water.html|https://site.test/water.html|discovery\r\n");
			File.WriteAllText(Path.Combine(snapshot, "download", "water.html"), "<p>water</p>");
			Write("09-self-link-analysis_comma.csv",
				"File,FileUrl,LinkFound,ContextSnippet\r\nwater.html,https://site.test/water.html,/water.html,<a href=\"/water.html\">water</a>\r\n");

			var item = Assert.Single(TriageSource.Read(snapshot), i => i.Type == SelfLinkSource.Type);
			Assert.Equal(Path.Combine(snapshot, "download", "water.html"), item.LocalFile);
		}
	}
}
