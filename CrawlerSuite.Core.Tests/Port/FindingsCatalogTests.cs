using System.Text;
using CrawlerSuite.Core.Findings;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>The findings views read every file shape the run writes.</summary>
	public class FindingsCatalogTests : IDisposable
	{
		private readonly string dir;

		public FindingsCatalogTests()
		{
			dir = Path.Combine(Path.GetTempPath(), "cs-findings-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path.Combine(dir, "download"));
			File.WriteAllText(Path.Combine(dir, "download", "aaapage.html"), "<html></html>");
			Write("02-crawler-index.log",
				"aaapage.html|https://site.test/page.html|discovery",
				"bbbother.html|https://site.test/other.html|discovery");
		}

		public void Dispose()
		{
			try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
		}

		private void Write(string name, params string[] lines) =>
			File.WriteAllText(Path.Combine(dir, name), "﻿" + string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));

		private FindingNode Tab(string title) => FindingsCatalog.Read(dir).Single(n => n.Title == title);

		private static FindingNode Child(FindingNode node, string title) => node.Children.Single(c => c.Title == title);

		[Fact]
		public void ContentQuality_GroupsByFamilyAndType_FromBothLogs()
		{
			Write("10-content-quality-issues.log",
				"Filename|IssueType|Detail|Context",
				"aaapage.html|LIGATURE|Ligature ﬁ|the ﬁrst",
				"aaapage.html|MALFORMED_HTML:12|Unclosed tag|<div>",
				"aaapage.html|QUOTE_UNMATCHED|„ without closer|„Hello");
			Write("22-cms-template-authoring-defects_comma.csv",
				"Filename,Url,IssueType,Detail,Context",
				"bbbother.html,https://site.test/other.html,EMBEDDED_BOM_IN_BODY,BOM at 120,\"a, b\"");

			var cq = Tab("Content quality");

			Assert.Equal(4, cq.Count);
			var ligatures = Child(Child(cq, "Characters"), "Ligatures");
			var row = Assert.Single(ligatures.Table!.Rows);
			Assert.Equal("https://site.test/page.html", row.Cells[0]);
			Assert.EndsWith("aaapage.html", row.LocalFile);
			Assert.Equal(1, Child(Child(cq, "Markup"), "Malformed HTML").Count);
			Assert.Equal("a, b", Child(Child(cq, "Template defects"), "Embedded BOM").Table!.Rows[0].Cells[3]);
			Assert.Equal(0, Child(cq, "Anchors").Count);
			Assert.DoesNotContain(cq.Children, c => c.Title == "Other");
		}

		[Fact]
		public void InvisibleCharacters_SplitByCause_WithExplanations()
		{
			Write("22-cms-template-authoring-defects_comma.csv",
				"Filename,Url,IssueType,Detail,Context",
				"aaapage.html,https://site.test/page.html,INVISIBLE_CHAR_IN_BODY,ZWSP (U+200B) found at 3 position(s),x",
				"bbbother.html,https://site.test/other.html,INVISIBLE_CHAR_IN_BODY,ZWSP (U+200B) found at 1 position(s),x",
				"aaapage.html,https://site.test/page.html,INVISIBLE_CHAR_IN_BODY,bidi control (U+202E) found at 1 position(s),x",
				"aaapage.html,https://site.test/page.html,INVISIBLE_CHAR_IN_BODY,invisible (U+00AD) found at 2 position(s),x",
				"aaapage.html,https://site.test/page.html,INVISIBLE_CHAR_IN_BODY,something without a code point,x");

			var invisible = Child(Child(Tab("Content quality"), "Template defects"), "Invisible characters in body");

			Assert.Equal(5, invisible.Count);
			Assert.Equal(2, Child(invisible, "Paste debris").Count);
			Assert.Equal(1, Child(invisible, "Bidi controls").Count);
			Assert.Equal(1, Child(invisible, "Soft hyphens").Count);
			Assert.Equal(1, Child(invisible, "Other").Count);
			Assert.Equal(0, Child(invisible, "Joiners").Count);
			Assert.True(Child(invisible, "Bidi controls").Basis!.IsViolation);
			Assert.All(invisible.Children, c => Assert.NotNull(c.Basis));
			Assert.Equal("Bidi controls", invisible.Children[0].Title);
		}

		[Fact]
		public void AdjacentAnchors_OneRowPerPage_Renumbered()
		{
			Write("10-content-quality-issues.log",
				"Filename|IssueType|Detail|Context",
				"aaapage.html|ADJACENT_ANCHOR|[299] „“ + „roid“|ctx",
				"aaapage.html|ADJACENT_ANCHOR|[153] „And“ + „“|ctx",
				"bbbother.html|ADJACENT_ANCHOR|[10] „a“ + „b“|other");

			var adjacent = Child(Child(Tab("Content quality"), "Anchors"), "Adjacent anchors");

			Assert.Equal(2, adjacent.Count);
			var page = adjacent.Table!.Rows.Single(r => r.Cells[0] == "https://site.test/page.html");
			Assert.Equal("[01] „And“ + „“\n[02] „“ + „roid“", page.Cells[1]);
			Assert.Equal("ctx", page.Cells[2]);
			Assert.Equal("„a“ + „b“", adjacent.Table.Rows.Single(r => r.Cells[0] == "https://site.test/other.html").Cells[1]);
		}

		[Fact]
		public void EmbeddedBom_OneRowPerMergedInFile()
		{
			Write("22-cms-template-authoring-defects_comma.csv",
				"Filename,Url,IssueType,Detail,Context",
				"aaapage.html,https://site.test/page.html,EMBEDDED_BOM_IN_BODY,BOM,\"offset 10: …x\"\">[BOM U+FEFF]<div></div><link…\"",
				"bbbother.html,https://site.test/other.html,EMBEDDED_BOM_IN_BODY,BOM,\"offset 99: …y\"\">[BOM U+FEFF]<div></div><link…\"",
				"bbbother.html,https://site.test/other.html,EMBEDDED_BOM_IN_BODY,BOM,\"offset 5: …[BOM U+FEFF]<script>…\"");

			var bom = Child(Child(Tab("Content quality"), "Template defects"), "Embedded BOM");

			Assert.Equal(2, bom.Count);
			var first = bom.Table!.Rows[0];
			Assert.Equal("<div></div><link", first.Cells[0]);
			Assert.Equal("2", first.Cells[1]);
			Assert.Contains("https://site.test/other.html", first.Cells[2]);
		}

		[Fact]
		public void InvisibleCharacters_NoUnknowns_NoOtherTab()
		{
			Write("22-cms-template-authoring-defects_comma.csv",
				"Filename,Url,IssueType,Detail,Context",
				"aaapage.html,https://site.test/page.html,INVISIBLE_CHAR_IN_BODY,ZWJ (U+200D) found at 1 position(s),x");

			var invisible = Child(Child(Tab("Content quality"), "Template defects"), "Invisible characters in body");

			Assert.DoesNotContain(invisible.Children, c => c.Title == "Other");
			Assert.Equal(1, Child(invisible, "Joiners").Count);
		}

		[Fact]
		public void InvisibleCharacters_EveryCharacterTheDetectorFlags_HasACause()
		{
			for (int cp = 0; cp <= 0xFFFF; cp++)
			{
				if (CrawlerSuite.Core.Quality.DefectDetectionHelpers.IsArchitectClassInvisible((char)cp))
				{
					Assert.True(FindingsCatalog.CauseOf(cp) != FindingsCatalog.InvisibleCause.Other, $"U+{cp:X4}");
				}
			}
		}

		[Fact]
		public void ContentQuality_UnknownType_LandsUnderOther()
		{
			Write("10-content-quality-issues.log", "Filename|IssueType|Detail|Context", "x.html|POTENTIAL_TRANSLATION|d|c");
			var other = Child(Tab("Content quality"), "Other");
			Assert.Equal("POTENTIAL_TRANSLATION", Assert.Single(other.Children).Title);
		}

		[Fact]
		public void ContentQuality_NotWritten_HasNoTables()
		{
			var cq = Tab("Content quality");
			Assert.False(cq.IsWritten);
			Assert.Null(Child(Child(cq, "Quotes"), "Unmatched").Table);
		}

		[Fact]
		public void Spelling_ReadsUniqueByPageAndLocatedBlocks()
		{
			Write("11-spell-errors-unique.log", "Teh (en)", "Wrold (en, de) · valid fr");
			Write("12-spell-errors-sources.log", "https://site.test/page.html|Teh (en)", "https://site.test/page.html|Desc (de) (meta[@name=description])");
			Write("13-spell-errors-source-location.log",
				"Teh (en) @ https://site.test/page.html",
				"    p  … Teh quick …",
				"    title  Teh title",
				"Wrold (en, de) @ https://site.test/other.html",
				"    li  Hello Wrold");
			Write("15-user-dictionary-orphan-words.log", "// User dictionary: not found in any page", "Foo", "", "// Site dictionary: redundant", "Bar");

			var spelling = Tab("Spelling");

			Assert.Equal(2, spelling.Count); // headline: unique misspellings
			Assert.Equal(string.Join("|", new string[] { "Wrold", "en, de", "fr" }), string.Join("|", Child(spelling, "Unique misspellings").Table!.Rows[1].Cells));
			var meta = Child(spelling, "By page").Table!.Rows[1];
			Assert.Equal(string.Join("|", new string[] { "https://site.test/page.html", "Desc", "de", "meta[@name=description]" }), string.Join("|", meta.Cells));
			Assert.NotNull(meta.LocalFile);
			var located = Child(spelling, "Exact location").Table!.Rows;
			Assert.Equal(3, located.Count);
			Assert.Equal(string.Join("|", new string[] { "Teh", "en", "https://site.test/page.html", "title", "Teh title" }), string.Join("|", located[1].Cells));
			var orphans = Child(spelling, "Dictionary orphans").Table!.Rows;
			Assert.Equal(string.Join("|", new string[] { "Site dictionary: redundant", "Bar" }), string.Join("|", orphans[1].Cells));
		}

		[Fact]
		public void Scripts_ReadTabbedFindingsAndSuggestions()
		{
			Write("29-bulk-script-findings.log", "# 29 — bulk", "# columns: word <TAB> page <TAB> context", "", "Wrold\thttps://site.test/page.html\tvar a = 'Wrold'");
			Write("27-spellcheck-suppression-suggestions.log",
				"# 27 — header", "",
				"## SUGGEST — 1 literal(s)",
				"     12×  btnPrimary                      el.className = 'btnPrimary'",
				"",
				"## REVIEW (low confidence — resembles a real word) — 0 literal(s)",
				"   (none)",
				"## Ready to paste — SUGGEST into TokensToFilter (high confidence; prune first):",
				"\"TokensToFilter\": [", "  \"btnPrimary\"", "]");

			var scripts = Tab("Scripts");

			Assert.Equal(string.Join("|", new string[] { "Wrold", "https://site.test/page.html", "var a = 'Wrold'" }), string.Join("|", Child(scripts, "Inline scripts").Table!.Rows.Single().Cells));
			Assert.Equal(string.Join("|", new string[] { "Suggest", "12", "btnPrimary", "el.className = 'btnPrimary'" }), string.Join("|", Child(scripts, "Filter suggestions").Table!.Rows.Single().Cells));
		}

		[Fact]
		public void Links_ReadListsCsvAndRedirects()
		{
			Write("06-404.log", "https://site.test/gone.html");
			Write("07-404-sources_semicolon.csv", "BrokenUrl;Kind;Referrer;AsWritten;Grade;Evidence",
				"https://site.test/gone.html;link;https://site.test/page.html;gone.html;;",
				"https://site.test/gone.html;link;https://site.test/other.html;/gone.html;;",
				"https://site.test/page.html.html;canonical;https://site.test/page.html;page.html.html;canonical;see the canonical");
			Write("03-redirect-analysis.log", "https://site.test/a | TemporaryRedirect | 301 https://site.test/b | 200 https://site.test/c");

			var links = Tab("Links");

			Assert.Equal(1, links.Count); // one address; the canonical-only one is the canonical's finding
			var broken = Child(links, "Broken links");
			Assert.Equal(1, broken.Count);
			Assert.Equal(3, broken.Table!.Rows.Count); // one row per reference
			Assert.EndsWith("aaapage.html", broken.Table.Rows[0].LocalFile);
			Assert.Equal("301 https://site.test/b | 200 https://site.test/c", Child(links, "Redirect chains").Table!.Rows.Single().Cells[2]);
		}

		[Fact]
		public void Csv_FormulaGuardIsRemovedForDisplay()
		{
			Write("17-pdf-quality_comma.csv", "PageUrl,Title,Description,Keywords,Language,Tags,PdfA,StructTree,RoleMap,Outlines,AltText,FormFields,PdfUA",
				"https://site.test/a.pdf,T,,,,'-1,'-1,0,0,0,0,0,0");

			var row = Child(Tab("Assets"), "PDF quality").Table!.Rows.Single();

			Assert.Equal("-1", row.Cells[5]);
		}

		[Fact]
		public void AtTables_KeepPipesInsideTheLastColumns()
		{
			Write("20-resource-bloat.log",
				"PageUrl@@@JSCSSFileCount@@@JSCSSTotalBytes@@@JSFileSizeBytes@@@CSSFileSizeBytes@@@Base64AssetCount@@@Base64TotalDecodedBytes@@@Base64LargeAssets@@@InlinedCSSBytes@@@JSONBlobCount@@@JSONBlobTotalBytes@@@Issues",
				"https://site.test/page.html@@@3@@@1000@@@600@@@400@@@1@@@50@@@a.png|b.png@@@0@@@0@@@0@@@BIG_JS|MANY_FILES");

			var row = Child(Tab("Assets"), "Resource bloat").Table!.Rows.Single();

			Assert.Equal("a.png|b.png", row.Cells[7]);
			Assert.Equal("BIG_JS|MANY_FILES", row.Cells[11]);
		}

		[Fact]
		public void FailedDownloads_KeepOnlyFailures_WithOriginAndLinkedFrom()
		{
			Write("01-crawler.log",
				"2026-10-10 10:00:00 | https://site.test | crawled | info",
				"2026-10-10 10:00:01 | https://site.test/page.html | saved | aaapage.html | discovery",
				"2026-10-10 10:00:02 | https://site.test/old | Found | https://site.test/new",
				"2026-10-10 10:00:03 | https://site.test/gone.html | NotFound | Response status code does not indicate success: 404 (Not Found).",
				"2026-10-10 10:00:04 | https://site.test/slow.html | N/A | No data received for 30 s — download aborted (stall timeout)",
				"2026-10-10 10:00:05 | https://site.test/big.zip | Oversized | exceeds the 150 MB single-file limit, not stored; sample kept as oversized/x.sample",
				"2026-10-10 10:00:06 | https://site.test/listed.html | InternalServerError | Response status code does not indicate success: 500 (Internal Server Error).",
				"2026-10-10 10:00:07 | https://site.test | completed | info");
			Write("05-not-directly-crawlable.log", "/listed.html");
			Write("07-404-sources_comma.csv", "BrokenUrl,Kind,Referrer,AsWritten,Grade,Evidence", "https://site.test/gone.html,link,https://site.test/page.html,gone.html,,");

			var failed = Child(Tab("Crawl"), "Failed downloads").Table!.Rows;

			Assert.Equal(3, failed.Count);
			Assert.Equal("https://site.test/gone.html|NotFound||main crawl|https://site.test/page.html", string.Join("|", failed[0].Cells));
			Assert.EndsWith("aaapage.html", failed[0].LocalFile);
			Assert.Contains("stall timeout", failed[1].Cells[2]);
			Assert.Equal("list 05", failed[2].Cells[3]);
		}

		[Fact]
		public void MissingFile_IsALeafWithoutTable()
		{
			var oversized = Child(Tab("Crawl"), "Oversized");
			Assert.Null(oversized.Table);
			Assert.False(oversized.IsWritten);
			Assert.Equal(0, oversized.Count);
		}
	}
}
