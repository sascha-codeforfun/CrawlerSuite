using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Quality;
using CrawlerSuite.Core.Triage;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// Bare text reports prose that lost its paragraph structure, not "text in a div"; headings that
	/// are only styled are their own template defect. All markup here is invented.
	/// </summary>
	public class StructureDefectTests
	{
		private static ContentQualityConfig Config(params string[] extraHeadingClasses)
		{
			var config = new ContentQualityConfig();
			config.ContentQualityHeadingClasses.AddRange(extraHeadingClasses);
			return config;
		}

		private static HtmlDocument Doc(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		private static List<QualityIssue> Bare(string html, ContentQualityConfig? config = null) =>
			DefectBareText.CheckBareText("f.html", Doc(html), config ?? Config()).ToList();

		// ── Bare text: silent where HTML is fine ─────────────────────────────

		[Theory]
		[InlineData("<div>Phone: <a href=\"tel:1\">1</a></div>")]          // a label before a value
		[InlineData("<div class=\"tip\">?</div>")]                         // a glyph, no letter
		[InlineData("<div>12:30</div>")]                                   // digits only
		[InlineData("<div>Your session ends in 5 minutes.</div>")]         // a widget message alone
		[InlineData("<div>One line<br>Next line</div>")]                   // a single break is a line, not a paragraph
		[InlineData("<div><div>Nested text</div><p>Paragraph.</p></div>")] // the text is not a direct child
		public void BareText_Silent(string html)
		{
			Assert.Empty(Bare(html));
		}

		// ── Bare text: the two shapes of a lost paragraph ────────────────────

		[Theory]
		[InlineData("<div><h3>Title</h3>Text right under the heading.</div>")]
		[InlineData("<section><p>One.</p>Two without a paragraph.<p>Three.</p></section>")]
		[InlineData("<div>Lead-in sentence.<ul><li>Item</li></ul></div>")]
		public void BareText_BesideParagraphs_Reported(string html)
		{
			var issue = Assert.Single(Bare(html));
			Assert.Equal(DefectBareText.BesideParagraphsDetail, issue.Detail);
		}

		[Theory]
		[InlineData("<div>First thought.<br><br>Second thought.</div>")]
		[InlineData("<div>First thought.<br>\n  <br>\nSecond thought.</div>")]
		[InlineData("<div>First <b>thought</b>.<br><br><br>Second thought.</div>")]
		public void BareText_BreakParagraphs_ReportedOncePerContainer(string html)
		{
			var issue = Assert.Single(Bare(html));
			Assert.Equal(DefectBareText.BreakParagraphsDetail, issue.Detail);
		}

		[Fact]
		public void BareText_BreaksWithoutProseOnBothSides_Silent()
		{
			Assert.Empty(Bare("<div>Text<br><br><a href=\"#\">link only</a></div>"));
			Assert.Empty(Bare("<div><br><br>Text</div>"));
		}

		[Fact]
		public void BareText_InsideAStyledHeading_Silent()
		{
			// The heading-styled element is reported as STYLED_HEADING; its text is not bare text again.
			Assert.Empty(Bare("<div class=\"card-title\"><p>x</p>Our services</div>", Config("card-title")));
			Assert.Single(Bare("<div class=\"card-body\"><p>x</p>Our services</div>", Config("card-title")));
		}

		// ── Styled headings ──────────────────────────────────────────────────

		private static List<QualityIssue> Styled(string html, ContentQualityConfig? config = null) =>
			StyledHeadings.Check("f.html", Doc(html), (config ?? Config()).ContentQualityHeadingClasses, 40).ToList();

		[Fact]
		public void StyledHeading_DefaultClasses_GroupedPerPattern()
		{
			var issues = Styled("<div class=\"h2 bold\">One</div><div class=\"h2\">Two</div><span class=\"h3\">Three</span>");
			Assert.Equal(2, issues.Count);
			Assert.Equal("<div class=\"h2\">", issues[0].Detail);
			// Every heading text, and the class attributes as written when they hold more than the heading class.
			Assert.Equal("as found: class=\"h2 bold\" / class=\"h2\" ¦ One ¦ Two", issues[0].Context);
			Assert.Equal("Three", issues[1].Context);
			Assert.Equal("<span class=\"h3\">", issues[1].Detail);
			Assert.All(issues, i => Assert.Equal(StyledHeadings.IssueType, i.IssueType));
		}

		[Fact]
		public void StyledHeading_ListsEveryText_ARepeatedOneOnce()
		{
			var issues = Styled("<div class=\"xq-block h3\">Haus</div><div class=\"xq-block h3\">Garten</div><div class=\"xq-block h3\">Haus</div>");
			var issue = Assert.Single(issues);
			Assert.Equal("<div class=\"h3\">", issue.Detail);
			Assert.Equal(["as found: class=\"xq-block h3\"", "Haus ×2", "Garten"], StyledHeadings.Lines(issue.Context));
		}

		[Fact]
		public void StyledHeading_LabellingANestedList_IsNotReported()
		{
			// The nesting carries the grouping; only the label without its own list is reported.
			var issues = Styled("<ul><li><div class=\"h4\">Gruppe</div>\n<ul><li><a href=\"#\">Eins</a></li></ul></li>" +
				"<li><div class=\"h4\">Allein</div><p>Text</p></li></ul><div class=\"h4\">Frei</div>");
			var issue = Assert.Single(issues);
			Assert.Equal(["Allein", "Frei"], StyledHeadings.Lines(issue.Context));
		}

		[Fact]
		public void StyledHeading_ConfiguredClass_IsWholeTokenAndCaseSensitive()
		{
			var config = Config("card-title");
			Assert.Single(Styled("<div class=\"card-title\">News</div>", config));
			Assert.Empty(Styled("<div class=\"card-title-wrap\">News</div>", config));
			Assert.Empty(Styled("<div class=\"Card-Title\">News</div>", config));
			Assert.Empty(Styled("<div class=\"card-title\">News</div>")); // not configured
		}

		[Theory]
		[InlineData("<h2 class=\"h2\">Real heading</h2>")]
		[InlineData("<div class=\"h2\" role=\"heading\" aria-level=\"2\">ARIA heading</div>")]
		[InlineData("<div class=\"h2\" role=\"heading\">ARIA heading, level 2 by default</div>")]
		[InlineData("<div class=\"h2\" role=\" Heading \">ARIA heading, any case and spacing</div>")]
		[InlineData("<div class=\"h2\" hidden>Hidden</div>")]
		[InlineData("<div aria-hidden=\"true\"><div class=\"h2\">Hidden</div></div>")]
		[InlineData("<div class=\"h2\"> </div>")]
		[InlineData("<script><div class=\"h2\">Template text</div></script>")]
		public void StyledHeading_NotReported(string html)
		{
			Assert.Empty(Styled(html));
		}

		[Fact]
		public void StyledHeading_ARoleOtherThanHeadingFirst_IsReported() =>
			Assert.Single(Styled("<div class=\"h2\" role=\"presentation heading\">Not a heading for assistive technology</div>"));

		[Fact]
		public void StyledHeading_EmptyList_CheckOff()
		{
			var config = new ContentQualityConfig { ContentQualityHeadingClasses = [] };
			Assert.Empty(Styled("<div class=\"h2\">Text</div>", config));
		}

		[Fact]
		public void Results_GroupStyledHeadingsByPattern_MostPagesFirst()
		{
			var rows = new List<FindingRow>
			{
				new(["https://x.test/a", "<div class=\"h3\">", "A"], null),
				new(["https://x.test/a", "<div class=\"h2\">", "B"], null),
				new(["https://x.test/b", "<div class=\"h2\">", "C"], null),
			};
			var grouped = FindingsCatalog.StyledHeadingsByPattern(rows);
			Assert.Equal("<div class=\"h2\">", grouped[0].Cells[0]);
			Assert.Equal("2", grouped[0].Cells[1]);
			Assert.Equal("<div class=\"h3\">", grouped[1].Cells[0]);
		}

		[Fact]
		public void StyledHeadings_ShowOneLinePerHeading_InTriageAndResults()
		{
			const string context = "as found: class=\"xq-block h3\" ¦ A ¦ B ×2 ¦ C ¦ D ¦ E ¦ F";
			var item = Assert.Single(CrawlerSuite.Core.Triage.TriageSource.Group(
				[(StyledHeadings.IssueType, "https://x.test/a", "<div class=\"h3\">", context, null)]));
			Assert.Equal("as found: class=\"xq-block h3\"\nA\nB ×2\nC\nD\nE\nF", item.Context);

			var grouped = FindingsCatalog.StyledHeadingsByPattern([new(["https://x.test/a", "<div class=\"h3\">", context], null)]);
			Assert.Equal("as found: class=\"xq-block h3\"\nA\nB ×2\nC\nD\n… and 2 more", grouped[0].Cells[3]);
		}

		[Fact]
		public void Triage_StyledHeadingsOnByDefault_BareTextRidesAlong()
		{
			// Styled headings arrive grouped, one entry per pattern on several pages: on lists those
			// entries, not an item on every page. Bare text still rides along.
			Assert.Equal(TriageMode.On, TriageSwitches.Defaults()[StyledHeadings.IssueType]);
			Assert.Equal(TriageMode.RideAlong, TriageSwitches.Defaults()["BARE_TEXT_IN_CONTAINER"]);
		}
	}
}
