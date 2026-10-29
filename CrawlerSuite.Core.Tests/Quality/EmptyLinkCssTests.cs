using CrawlerSuite.Core.Display;
using CrawlerSuite.Core.Quality.Css;
using CrawlerSuite.Core.Text;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// A link without a name, checked against the page's stylesheets like the hidden-focusable check:
	/// hidden by a rule that always applies is cleared (no marker, off in triage), hidden only under a
	/// media query is conditional, a stylesheet missing from the snapshot leaves it undetermined, and
	/// a shown one stays the finding. Invented markup and CSS.
	/// </summary>
	[Collection("Logger")]
	public sealed class EmptyLinkCssTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), $"cssl-{Guid.NewGuid():N}");
		private readonly string page = $"page_{Guid.NewGuid():N}.html";

		public EmptyLinkCssTests()
		{
			Directory.CreateDirectory(dir);
			var lookup = Path.Combine(dir, "lookup.lku");
			File.WriteAllLines(lookup, [$"{page}|https://example.test/a/page.html"]);
			CrawlerSuite.Core.Urls.Cache.Load(lookup);
			File.WriteAllText(Path.Combine(dir, CrawlerSuite.Core.Urls.Naming.GenerateFileName("https://example.test/s.css")),
				".off{display:none}\n@media (min-width: 900px){.wide{display:none}}");
		}

		public void Dispose() => Directory.Delete(dir, recursive: true);

		private CrawlerSuite.Core.Quality.QualityIssue Verdict(string head, string body)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml($"<html><head>{head}</head><body>{body}</body></html>");
			var issues = CrawlerSuite.Core.Quality.MisplacedAnchors.Check(page, doc, new ContentQualityConfig(),
				() => doc, new StyleSheets(dir, _ => null)).ToList();
			return Assert.Single(issues, i => i.IssueType.StartsWith("MISPLACED_ANCHOR_EMPTY", StringComparison.Ordinal));
		}

		private const string Sheet = "<link rel=\"stylesheet\" href=\"/s.css\">";

		[Fact]
		public void HiddenByTheStylesheet_IsCleared_WithTheRule_AndTheElementLast()
		{
			var issue = Verdict(Sheet, "<div class=\"off\"><a href=\"/floe\"></a></div>");

			Assert.Equal("MISPLACED_ANCHOR_EMPTY_CLEARED", issue.IssueType);
			Assert.StartsWith("Hidden by CSS (", issue.Detail);
			Assert.Contains(".off", issue.Detail);
			Assert.EndsWith("— <a href=\"/floe\">", issue.Detail);
		}

		[Fact]
		public void HiddenOnlyUnderAMediaQuery_IsConditional()
		{
			var issue = Verdict(Sheet, "<div class=\"wide\"><a href=\"/floe\"><img src=\"ice.png\" alt=\"\"></a></div>");

			Assert.Equal("MISPLACED_ANCHOR_EMPTY_CONDITIONAL", issue.IssueType);
			Assert.Contains("min-width: 900px", issue.Detail);
			Assert.Contains("Image link:", issue.Detail);
		}

		[Fact]
		public void AStylesheetMissingFromTheSnapshot_LeavesItUndetermined()
		{
			var issue = Verdict(Sheet + "<link rel=\"stylesheet\" href=\"/absent.css\">", "<a href=\"/floe\"></a>");

			Assert.Equal("MISPLACED_ANCHOR_EMPTY_UNDETERMINED", issue.IssueType);
			Assert.Contains("absent.css", issue.Detail);
		}

		[Fact]
		public void Shown_StaysTheFinding_WithItsDetailUnchanged()
		{
			var issue = Verdict(Sheet, "<a href=\"/floe\"></a>");

			Assert.Equal("MISPLACED_ANCHOR_EMPTY", issue.IssueType);
			Assert.StartsWith("No accessible name — ", issue.Detail);
		}

		[Fact]
		public void TheMarker_SaysWhatIsMissing_ExceptOnACleared_Link()
		{
			const string Context = "<p><a href=\"/floe\"></a></p>";
			var shown = QualityMarks.For("MISPLACED_ANCHOR_EMPTY_CONDITIONAL", Context,
				"Shown except under @media (min-width: 900px): s.css:2:27 .wide: No accessible name — <a href=\"/floe\">", null);
			var cleared = QualityMarks.For("MISPLACED_ANCHOR_EMPTY_CLEARED", Context,
				"Hidden by CSS (s.css:1:1 .off): No accessible name — <a href=\"/floe\">", null);

			Assert.Contains("[Missing link text]", shown.Text);
			Assert.Equal(Context, cleared.Text);
			Assert.Empty(cleared.Marks);
		}

		[Theory]
		[InlineData("Hidden by CSS (s.css:1:1 .off): No accessible name — <a href=\"/floe\">",
			"Per CSS verborgen (s.css:1:1 .off): Kein zugänglicher Name – <a href=\"/floe\">")]
		[InlineData("Shown except under @media (min-width: 900px): s.css:2:27 .wide: Image link: the image is marked decorative (alt=\"\") — <a href=\"/floe\">",
			"Angezeigt außer under @media (min-width: 900px): s.css:2:27 .wide: Bildlink: Das Bild ist als dekorativ markiert (alt=\"\") – <a href=\"/floe\">")]
		[InlineData("Whether it is shown cannot be told (stylesheet not in the snapshot: absent.css (not fetched)): No accessible name — <a href=\"/floe\">",
			"Ob er angezeigt wird, lässt sich nicht sagen (Stylesheet nicht im Snapshot: absent.css (not fetched)): Kein zugänglicher Name – <a href=\"/floe\">")]
		public void TheDetails_InGerman(string english, string german) =>
			Assert.Equal(german, FindingDetails.In(DocumentLanguage.German, "MISPLACED_ANCHOR_EMPTY_CONDITIONAL", english));
	}
}
