using CrawlerSuite.Core.Quality.Css;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// The page's CSS decides whether an aria-hidden element is in the Tab order: selectors, the
	/// cascade, inheritance, media queries and states, stylesheets missing from the snapshot.
	/// Invented markup and CSS; stylesheets are written to a temporary download folder under the
	/// names the crawler gives them.
	/// </summary>
	public sealed class HiddenFocusableCssTests : IDisposable
	{
		private const string Page = "https://example.test/dir/page.html";
		private readonly string dir = Path.Combine(Path.GetTempPath(), $"css-{Guid.NewGuid():N}");
		private readonly Dictionary<string, string> crawlLog = new(StringComparer.Ordinal);

		public HiddenFocusableCssTests() => Directory.CreateDirectory(dir);

		public void Dispose() => Directory.Delete(dir, recursive: true);

		private void Sheet(string url, string css) =>
			File.WriteAllText(Path.Combine(dir, CrawlerSuite.Core.Urls.Naming.GenerateFileName(url)), css);

		private (CssVerdict Verdict, string Evidence, PageStyles Styles) Resolve(string head, string body, string id = "t")
		{
			var doc = new HtmlDocument();
			doc.LoadHtml($"<html><head>{head}</head><body>{body}</body></html>");
			var styles = new PageStyles(doc, Page, new StyleSheets(dir, u => crawlLog.TryGetValue(u, out var s) ? s : null));
			var element = doc.GetElementbyId(id);
			var (verdict, evidence) = styles.Resolve(element);
			return (verdict, evidence, styles);
		}

		private const string Box = "<input id=\"t\" type=\"checkbox\" aria-hidden=\"true\">";

		[Theory]
		[InlineData("#t{display:none}")]
		[InlineData("input[type=checkbox]{display:none}")]
		[InlineData("input[type=\"CHECKBOX\" i]{display:none}")]
		[InlineData(".wrap > .inner input{display:none}")]
		[InlineData(".wrap .inner > :last-child{display:none}")]
		[InlineData("span + input{display:none}")]
		[InlineData("b ~ input{display:none}")]
		[InlineData(".inner > :nth-child(3){display:none}")]
		[InlineData("input:not(.shown){display:none}")]
		[InlineData(":is(.x, .inner) input{display:none}")]
		[InlineData(".wrap{visibility:hidden}")]
		[InlineData(".wrap{content-visibility:hidden}")]
		public void HiddenByAStylesheet_NamesTheRule(string css)
		{
			Sheet("https://example.test/css/site.css", "\n\n" + css);
			var (verdict, evidence, _) = Resolve(
				"<link rel=\"stylesheet\" href=\"/css/site.css\">",
				$"<div class=\"wrap\"><div class=\"inner\"><b>x</b><span>y</span>{Box}</div></div>");
			Assert.Equal(CssVerdict.Hidden, verdict);
			Assert.Contains(":3:1", evidence);   // line 3, column 1 of the sheet
		}

		[Fact]
		public void TheCascade_ImportantSpecificityOrderAndInline()
		{
			// A later, equally specific rule shows it again.
			Assert.Equal(CssVerdict.Shown, Resolve("<style>#t{display:none} #t{display:inline-block}</style>", Box).Verdict);
			// More specific wins over later.
			Assert.Equal(CssVerdict.Hidden, Resolve("<style>body #t{display:none} #t{display:block}</style>", Box).Verdict);
			// !important wins over specificity.
			Assert.Equal(CssVerdict.Hidden, Resolve("<style>input{display:none !important} body #t{display:block}</style>", Box).Verdict);
			// Inline style wins over a normal rule …
			Assert.Equal(CssVerdict.Shown, Resolve("<style>#t{display:none}</style>",
				"<input id=\"t\" type=\"checkbox\" aria-hidden=\"true\" style=\"display:block\">").Verdict);
			// … but not over an !important one.
			Assert.Equal(CssVerdict.Hidden, Resolve("<style>#t{display:none !important}</style>",
				"<input id=\"t\" type=\"checkbox\" aria-hidden=\"true\" style=\"display:block\">").Verdict);
		}

		[Fact]
		public void Inheritance_DisplayNoneCannotBeUndone_VisibilityCan()
		{
			const string tree = "<div class=\"outer\"><div class=\"inner\">" + Box + "</div></div>";
			Assert.Equal(CssVerdict.Hidden, Resolve("<style>.outer{display:none} #t{display:block}</style>", tree).Verdict);
			Assert.Equal(CssVerdict.Shown, Resolve("<style>.outer{visibility:hidden} .inner{visibility:visible}</style>", tree).Verdict);
			Assert.Equal(CssVerdict.Hidden, Resolve("<style>.outer{visibility:hidden}</style>", tree).Verdict);
			// content-visibility:hidden hides an element's contents, not the element itself.
			Assert.Equal(CssVerdict.Shown, Resolve("<style>#t{content-visibility:hidden}</style>", Box).Verdict);
		}

		[Fact]
		public void MediaQueriesAndStates_AreConditional()
		{
			var media = Resolve("<style>@media (max-width: 600px) { #t{display:none} }</style>", Box);
			Assert.Equal(CssVerdict.Conditional, media.Verdict);
			Assert.Contains("(max-width: 600px)", media.Evidence);

			var linkMedia = Resolve("<style media=\"(max-width: 300px)\">#t{display:none}</style>", Box);
			Assert.Equal(CssVerdict.Conditional, linkMedia.Verdict);

			var state = Resolve("<style>.wrap:not(:hover) #t{display:none}</style>", "<div class=\"wrap\">" + Box + "</div>");
			Assert.Equal(CssVerdict.Conditional, state.Verdict);
			Assert.Contains("state", state.Evidence);
		}

		[Theory]
		[InlineData("<style>@media print { #t{display:none} }</style>")]
		[InlineData("<style>@media only print and (orientation: portrait) { #t{display:none} }</style>")]
		[InlineData("<style>@media print, PRINT { body.floe_open #t{display:none} }</style>")]
		[InlineData("<style media=\"print\">#t{display:none}</style>")]
		[InlineData("<style>@media screen and (min-width: 1px) { @media print { #t{display:none} } }</style>")]
		public void PrintOnlyRules_HideNothingOnScreen(string head) =>
			Assert.Equal(CssVerdict.Shown, Resolve(head, Box).Verdict);

		[Theory]
		[InlineData("<style>@media not print { #t{display:none} }</style>")]
		[InlineData("<style>@media print, screen and (max-width: 600px) { #t{display:none} }</style>")]
		[InlineData("<style>@media all and (max-width: 600px) { #t{display:none} }</style>")]
		public void QueriesThatApplyOnScreen_StillCount(string head) =>
			Assert.Equal(CssVerdict.Conditional, Resolve(head, Box).Verdict);

		[Theory]
		[InlineData("@media print", true)]
		[InlineData("@media only print", true)]
		[InlineData("@media print and (orientation: portrait)", true)]
		[InlineData("@media screen and (max-width:649px) and @media print", true)]
		[InlineData("@media not print", false)]
		[InlineData("@media screen, print", false)]
		[InlineData("@media (max-width: 600px)", false)]
		[InlineData("@container card (min-width: 400px)", false)]
		[InlineData(null, false)]
		public void PrintOnly_ReadsTheMediaTypes(string? condition, bool printOnly) =>
			Assert.Equal(printOnly, PageStyles.PrintOnly(condition));

		[Fact]
		public void APrintStylesheet_IsNotRead_SoItCannotBeMissing()
		{
			var (verdict, _, styles) = Resolve("<link rel=\"stylesheet\" href=\"/css/print-only.css\" media=\"print\">", Box);

			Assert.Equal(CssVerdict.Shown, verdict);
			Assert.Empty(styles.Gaps);
		}

		[Fact]
		public void ImportsAreFollowed_WithTheirMedia()
		{
			Sheet("https://example.test/css/main.css", "@import url(\"parts/forms.css\");\n@import 'small.css' (max-width: 400px);");
			Sheet("https://example.test/css/parts/forms.css", ".gone{display:none}");
			Sheet("https://example.test/css/small.css", "#t{display:none}");
			var head = "<link rel=\"stylesheet\" href=\"https://example.test/css/main.css\">";
			Assert.Equal(CssVerdict.Hidden, Resolve(head, "<div class=\"gone\">" + Box + "</div>").Verdict);
			Assert.Equal(CssVerdict.Conditional, Resolve(head, Box).Verdict);
		}

		[Fact]
		public void VisuallyHidden_StaysShown()
		{
			var css = ".visually-hidden{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);clip-path:inset(50%);opacity:0;white-space:nowrap}";
			var (verdict, _, _) = Resolve($"<style>{css}</style>",
				"<input id=\"t\" type=\"checkbox\" aria-hidden=\"true\" class=\"visually-hidden\">");
			Assert.Equal(CssVerdict.Shown, verdict);
		}

		[Fact]
		public void PseudoElementsAndAlternateSheets_DoNotHideTheElement()
		{
			Assert.Equal(CssVerdict.Shown, Resolve("<style>#t::before{display:none}</style>", Box).Verdict);
			Sheet("https://example.test/alt.css", "#t{display:none}");
			Assert.Equal(CssVerdict.Shown, Resolve("<link rel=\"alternate stylesheet\" href=\"/alt.css\">", Box).Verdict);
		}

		[Fact]
		public void AMissingSheet_IsAGap_UnlessItIsNotThereForAnyone()
		{
			crawlLog["https://example.test/css/gone.css"] = "404";
			var gone = Resolve("<link rel=\"stylesheet\" href=\"/css/gone.css\">", Box).Styles;
			Assert.Empty(gone.Gaps);
			Assert.Single(gone.NotThere);

			var never = Resolve("<link rel=\"stylesheet\" href=\"/css/never.css\">", Box).Styles;
			var gap = Assert.Single(never.Gaps);
			Assert.Contains("never fetched", gap.Reason);

			crawlLog["https://example.test/css/broke.css"] = "N/A";
			var broke = Resolve("<link rel=\"stylesheet\" href=\"/css/broke.css\">", Box).Styles;
			Assert.Contains("fetch failed", Assert.Single(broke.Gaps).Reason);
		}

		[Fact]
		public void UnreadableSelectors_AreCounted_NeverGuessed()
		{
			var (verdict, _, styles) = Resolve("<style>body:has(#t){display:none} & .x{display:none}</style>", Box);
			Assert.Equal(CssVerdict.Shown, verdict);
			Assert.Equal(2, styles.UnsupportedRules);
		}

		[Fact]
		public void Comments_StringsAndAtRules_DoNotConfuseTheParser()
		{
			var css = "/* #t{display:none} */ @font-face{font-family:x;src:url('a;b{c}.woff')} @keyframes k{from{display:none}} " +
				"a[title=\"}{\"]{color:red} @supports (display:grid){ #t{display:none} }";
			Assert.Equal(CssVerdict.Hidden, Resolve($"<style>{css}</style>", Box).Verdict);
			var sheet = CssSheet.Parse(css, "x.css");
			Assert.Single(sheet.Rules);   // only the @supports rule sets display
		}
	}

	/// <summary>
	/// The verdicts the check writes once the page's CSS is read: cleared with the rule as
	/// evidence, conditional, undetermined when a stylesheet is missing from the snapshot, and a
	/// violation when nothing takes the element out. Invented markup and CSS.
	/// </summary>
	[Collection("Logger")]
	public sealed class HiddenFocusableVerdictTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), $"cssv-{Guid.NewGuid():N}");
		private readonly string page = $"page_{Guid.NewGuid():N}.html";

		public HiddenFocusableVerdictTests()
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
			return Assert.Single(issues, i => i.IssueType.StartsWith("ARIA_HIDDEN_FOCUSABLE", StringComparison.Ordinal));
		}

		private const string Sheet = "<link rel=\"stylesheet\" href=\"/s.css\">";

		[Fact]
		public void HiddenByTheStylesheet_IsClearedWithTheRule()
		{
			var issue = Verdict(Sheet, "<div class=\"off\"><input type=\"checkbox\" aria-hidden=\"true\"></div>");
			Assert.Equal("ARIA_HIDDEN_FOCUSABLE_CLEARED", issue.IssueType);
			Assert.Contains("hidden by CSS", issue.Detail);
			Assert.Contains(".off", issue.Detail);
		}

		[Fact]
		public void HiddenOnlyUnderAMediaQuery_IsConditional()
		{
			var issue = Verdict(Sheet, "<div class=\"wide\"><button aria-hidden=\"true\">Go</button></div>");
			Assert.Equal("ARIA_HIDDEN_FOCUSABLE_CONDITIONAL", issue.IssueType);
			Assert.Contains("min-width: 900px", issue.Detail);
		}

		[Fact]
		public void AStylesheetMissingFromTheSnapshot_LeavesItUndetermined()
		{
			var issue = Verdict(Sheet + "<link rel=\"stylesheet\" href=\"/absent.css\">", "<button aria-hidden=\"true\">Go</button>");
			Assert.Equal("ARIA_HIDDEN_FOCUSABLE_UNDETERMINED", issue.IssueType);
			Assert.Contains("absent.css", issue.Detail);
		}

		[Fact]
		public void NothingTakesItOut_IsAViolation() =>
			Assert.Equal("ARIA_HIDDEN_FOCUSABLE", Verdict(Sheet, "<button aria-hidden=\"true\" class=\"visually-hidden\">Go</button>").IssueType);
	}
}
