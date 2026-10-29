using System.Text;
using CrawlerSuite.Core.Findings;
using Xunit;

namespace CrawlerSuite.Core.Tests.Findings
{
	/// <summary>
	/// The page source shown on the Results: the content list's source value and mapping label as
	/// they are, and the CMS link — the ticket lookup's answers, never interpreted. All column
	/// names and values here are invented.
	/// </summary>
	[Collection("Logger")]
	public class PageSourcesTests : IDisposable
	{
		private readonly string dir;

		public PageSourcesTests()
		{
			dir = Path.Combine(Path.GetTempPath(), $"st-source-{Guid.NewGuid():N}");
			Directory.CreateDirectory(dir);
			Logger.Initialize(Path.Combine(dir, "test.log"));
		}

		public void Dispose()
		{
			try { Directory.Delete(dir, recursive: true); } catch { }
		}

		private Config Config(bool withList = true)
		{
			var csv = Path.Combine(dir, "list.csv");
			File.WriteAllLines(csv,
			[
				"Path;Origin;LockedBy;EditorPath",
				"https://x.test/a;origin-one;;/edit/a",
				"https://x.test/b;origin-two;robot-7;/edit/b",
				"https://x.test/c;something-else;;",
			], Encoding.UTF8);
			return new Config
			{
				CmsContentList = withList ? new CmsContentListConfig { Path = csv, ColumnDelimiter = ";" } : null,
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					UrlSourceColumn = "Origin",
					UrlSourceLocalName = "origin-one",
					UrlSourceExternalName = "origin-two",
					CmsPathColumn = "EditorPath",
					CmsEditorBaseUrl = "https://cms.x.test/editor",
					SpecialInfoMappings = [new SpecialInfoMapping { Column = "LockedBy", Pattern = "robot*", Label = "(label-x)" }],
				},
			};
		}

		[Fact]
		public void Source_IsTheValueAndLabelAsTheyAre()
		{
			var lookup = PageSources.Build(Config())!;
			Assert.Equal(new PageSource("origin-one", "", "https://cms.x.test/editor/edit/a", PageSourceRole.Local), lookup("https://x.test/a"));
			Assert.Equal("origin-two (label-x)", lookup("https://x.test/b")!.Text);
			Assert.Equal("https://cms.x.test/editor/edit/b", lookup("https://x.test/b")!.CmsLink);
		}

		[Fact]
		public void Role_IsWhichConfiguredKeyMatched_LabelFirst()
		{
			var lookup = PageSources.Build(Config())!;
			Assert.Equal(PageSourceRole.Local, lookup("https://x.test/a")!.Role);
			Assert.Equal(PageSourceRole.Mapped, lookup("https://x.test/b")!.Role); // value is the external name, but a label matched
			Assert.Equal(PageSourceRole.Other, lookup("https://x.test/c")!.Role);
			var names = new TicketGenerationConfig { UrlSourceLocalName = "origin-one", UrlSourceExternalName = "origin-two" };
			Assert.Equal(PageSourceRole.External, PageSources.RoleOf("ORIGIN-TWO", "", names));
			Assert.Equal(PageSourceRole.Other, PageSources.RoleOf("", "", new TicketGenerationConfig()));
		}

		[Fact]
		public void Source_AnyOtherValue_IsShownUnchanged_WithoutALink()
		{
			var source = PageSources.Build(Config())!("https://x.test/c")!;
			Assert.Equal("something-else", source.Text);
			Assert.Equal(string.Empty, source.CmsLink);
		}

		[Fact]
		public void Source_PageNotInTheList_IsNull()
		{
			Assert.Null(PageSources.Build(Config())!("https://x.test/missing"));
			Assert.Null(PageSources.Build(Config())!(""));
		}

		[Fact]
		public void Source_NoContentList_NoLookup()
		{
			Assert.Null(PageSources.Build(Config(withList: false)));
			var config = Config();
			config.CmsContentList!.Path = Path.Combine(dir, "absent.csv");
			Assert.Null(PageSources.Build(config));
		}

		[Fact]
		public void SiteOf_FindsTheSiteByItsSnapshotFolder()
		{
			var config = Config();
			config.BaseDirectory = dir;
			config.Sites = [new SiteConfig { Name = "one", Url = "https://x.test" }, new SiteConfig { Name = "two", Url = "https://y.test" }];
			var snapshot = Path.Combine(SnapshotLocator.SiteFolder(config, "https://y.test"), "2026-01-02-03-04-05");
			Assert.Equal("two", PageSources.SiteOf(config, snapshot)?.Name);
			Assert.Null(PageSources.SiteOf(config, Path.Combine(dir, "elsewhere", "2026-01-02-03-04-05")));
		}

		[Fact]
		public void NeutralNames_NeverCarryTheValues()
		{
			PageSource?[] rows =
			[
				new PageSource("origin-two", "(label-x)", "", PageSourceRole.Mapped),
				new PageSource("origin-one", "", "", PageSourceRole.Local),
				new PageSource("origin-one", "", "", PageSourceRole.Local),
				null,
				new PageSource("", "(label-x)", ""),
			];
			var names = new NeutralSourceNames(rows);
			var counts = names.Count(rows);
			Assert.Equal(("source value 1 (local)", 2), counts[0]);
			Assert.Contains(("source value 2 + label 1", 1), counts);
			Assert.Contains(("no source value + label 1", 1), counts);
			Assert.Contains(("no source", 1), counts);
			Assert.DoesNotContain(counts, c => c.Name.Contains("origin", StringComparison.Ordinal) || c.Name.Contains("label-x", StringComparison.Ordinal));
		}
	}
}
