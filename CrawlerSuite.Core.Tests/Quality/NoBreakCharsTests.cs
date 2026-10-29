using System.Linq;
using CrawlerSuite.Core.Quality;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	public class NoBreakCharsTests
	{
		private const char NBSP = '\u00A0';
		private const char NNBSP = '\u202F';
		private const char EN = '\u2013';
		private const char EM = '\u2014';

		private static ContentQualityConfig DefaultConfig() => new()
		{
			ContentQualityExcerptRadius = 120,
			ContentQualityMaxExcerpt = 400,
		};

		private static List<QualityIssue> ScanText(string text) =>
			NoBreakChars.Scan("f.html", text, "text", DefaultConfig()).ToList();

		private static List<QualityIssue> ScanHtml(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return NoBreakChars.Check("f.html", doc, DefaultConfig()).ToList();
		}

		// ── Arm one: LB12a ───────────────────────────────────────────────

		[Fact]
		public void Scan_GlueAfterEnDash_IsReported()
		{
			var issues = ScanText($"Alpha{NBSP}{EN}{NBSP}Beta 3");
			Assert.Single(issues);
			Assert.Equal(NoBreakChars.InertIssueType, issues[0].IssueType);
		}

		/// <summary>
		/// The leading glue character in the common "glue dash glue" shape is DOING
		/// ITS JOB — it stops the dash starting a line, which is the one rule German
		/// typography agrees on. Reporting it would tell an author to delete the only
		/// part that works. Exactly one finding, and it is the trailing one.
		/// </summary>
		[Fact]
		public void Scan_GlueBeforeEnDash_IsNotReported()
		{
			var issues = ScanText($"Alpha{NBSP}{EN}{NBSP}Beta 3");
			Assert.Single(issues);
			Assert.Contains("directly after", issues[0].Detail);
			Assert.Contains("U+2013 EN DASH", issues[0].Detail);
		}

		/// <summary>
		/// The check is not about U+00A0. Scoping it to that character would miss the
		/// narrow no-break space, which is what some editors and locale autocorrect
		/// insert. A check scoped to U+00A0 alone is blind to it.
		/// </summary>
		[Fact]
		public void Scan_NarrowNoBreakSpaceIsTreatedTheSame()
		{
			var issues = ScanText($"Alpha{NNBSP}{EN}{NNBSP}Beta");
			Assert.Single(issues);
			Assert.Contains("U+202F NARROW NO-BREAK SPACE", issues[0].Detail);
		}

		[Theory]
		[InlineData('\u002D')]  // HYPHEN-MINUS
		[InlineData('\u2010')]  // HYPHEN
		[InlineData('\u00AD')]  // SOFT HYPHEN
		[InlineData('\u0020')]  // SPACE
		[InlineData('\u2009')]  // THIN SPACE
		public void Scan_GlueAfterAnyExceptionClassCharacter_IsReported(char preceding)
		{
			Assert.Single(ScanText($"a{preceding}{NBSP}b"));
		}

		[Fact]
		public void Scan_GlueAfterEmDash_IsNotReported()
		{
			// EM DASH is class B2, outside the LB12a exception set, so the glue
			// character genuinely protects here.
			Assert.Empty(ScanText($"Alpha{NBSP}{EM}{NBSP}Beta"));
		}

		[Fact]
		public void Scan_TransparentCharacterBetweenIsSteppedOver()
		{
			// A combining mark inherits the behaviour of the character before it
			// (LB9). A naive lookback reads the mark and returns the wrong verdict.
			var issues = ScanText($"a{EN}\u0301{NBSP}b");
			Assert.Single(issues);
			Assert.Contains("U+2013 EN DASH", issues[0].Detail);
		}

		// ── Arm two: LB18 ────────────────────────────────────────────────

		[Fact]
		public void Scan_GlueFollowedByOrdinarySpace_IsReported()
		{
			var issues = ScanText($"year 2014{NNBSP} average value");
			Assert.Single(issues);
			Assert.Equal(NoBreakChars.DoubleSpaceIssueType, issues[0].IssueType);
		}

		[Theory]
		[InlineData('\u0020')]  // SPACE
		[InlineData('\u2009')]  // THIN SPACE
		[InlineData('\u0009')]  // TAB
		public void Scan_GlueAfterASpaceShapedCharacter_IsTheDoubleSpaceDefect(char preceding)
		{
			// Split by symptom, not by rule: a space in front means the reader sees a
			// double space. A dash in front means a protection that silently fails.
			var issues = ScanText($"a{preceding}{NBSP}b");
			Assert.Single(issues);
			Assert.Equal(NoBreakChars.DoubleSpaceIssueType, issues[0].IssueType);
		}

		[Theory]
		[InlineData('\u2013')]  // EN DASH
		[InlineData('\u002D')]  // HYPHEN-MINUS
		[InlineData('\u2010')]  // HYPHEN
		[InlineData('\u00AD')]  // SOFT HYPHEN
		public void Scan_GlueAfterAHyphenShapedCharacter_IsTheInertDefect(char preceding)
		{
			var issues = ScanText($"a{preceding}{NBSP}b");
			Assert.Single(issues);
			Assert.Equal(NoBreakChars.InertIssueType, issues[0].IssueType);
		}

		[Fact]
		public void Scan_GlueBothInertAndBeforeSpace_IsReportedOnce()
		{
			var issues = ScanText($"a{EN}{NBSP} b");
			Assert.Single(issues);
			Assert.Equal(NoBreakChars.InertIssueType, issues[0].IssueType);
		}

		// ── Silence where the glue works ─────────────────────────────────

		[Theory]
		[InlineData("Dr.\u00A0Becker")]   // the example given in UAX #14 itself
		[InlineData("8.7\u00A0percent")]
		[InlineData("Note:\u00A0content")]
		[InlineData("Part\u00A03")]
		public void Scan_ProtectiveGlue_IsNotReported(string text)
		{
			Assert.Empty(ScanText(text));
		}

		[Fact]
		public void Scan_ConsecutiveGlue_IsNotReported()
		{
			// Glue before glue is still glue — no break opportunity, nothing to say.
			Assert.Empty(ScanText($"a{NBSP}{NBSP}b"));
		}

		[Fact]
		public void Scan_EmptyText_IsSilent()
		{
			Assert.Empty(ScanText(string.Empty));
		}

		// ── Document traversal ───────────────────────────────────────────

		/// <summary>
		/// Why scanning is per node. Joining text nodes with a
		/// space — which the surrounding ContentQuality pass does for its own
		/// purposes — manufactures a space before this glue character and reports a
		/// defect that does not exist. In the real DOM the preceding character is the
		/// colon, and the glue is protective.
		/// </summary>
		[Fact]
		public void Check_GlueAfterAnElementBoundary_IsNotReported()
		{
			Assert.Empty(ScanHtml("<p><b>Note:</b>&nbsp;content</p>"));
		}

		[Fact]
		public void Check_TextNodeGlue_IsReported()
		{
			var issues = ScanHtml("<p>Alpha&nbsp;&#8211;&nbsp;Beta</p>");
			Assert.Single(issues);
			Assert.Equal(NoBreakChars.InertIssueType, issues[0].IssueType);
		}

		[Fact]
		public void Check_ScriptAndStyleAreSkipped()
		{
			Assert.Empty(ScanHtml("<style>a{content:\"x\u2013\u00A0y\"}</style>"));
			Assert.Empty(ScanHtml("<script>var s = \"x\u2013\u00A0y\";</script>"));
		}

		// ── Attributes are out of scope ──────────────────────────────────

		/// <summary>
		/// Not a gap — a decision, pinned here so it cannot be "fixed" by accident.
		///
		/// What an attribute holding a serialized HTML fragment finally becomes is
		/// decided by JavaScript this tool never executes. The script may inject the
		/// value verbatim, concatenate it with other fragments, or rewrite it first.
		/// A fragment that is well-formed alone can be malformed once combined, and
		/// one that looks malformed alone can be a legitimate half the script closes.
		/// Any finding drawn from it would be a claim about a string whose final form
		/// was assumed.
		///
		/// The honest finding about such content is structural — that it exists and
		/// is outside what static analysis can verify — and belongs to its own check.
		/// </summary>
		[Fact]
		public void Check_DoubleEncodedFragmentAttribute_IsNotScanned()
		{
			Assert.Empty(ScanHtml(
				"<div data-description=\"&lt;p&gt;A&amp;#8211;&amp;nbsp;B&lt;/p&gt;\"></div>"));
		}

		[Fact]
		public void Check_NoAttributeIsScanned()
		{
			Assert.Empty(ScanHtml($"<a title=\"A{EN}{NBSP}B\" href=\"#\">x</a>"));
			Assert.Empty(ScanHtml($"<img alt=\"A{EN}{NBSP}B\" src=\"x.png\" />"));
			Assert.Empty(ScanHtml($"<a href=\"/a{EN}{NBSP}b\">x</a>"));
		}

		[Fact]
		public void Check_TextInsideAnElementCarryingSuchAnAttributeIsStillScanned()
		{
			// The attribute is ignored; the element's own text is not.
			var issues = ScanHtml(
				$"<div data-description=\"&lt;p&gt;x&lt;/p&gt;\">A{EN}{NBSP}B</div>");
			Assert.Single(issues);
			Assert.Equal(NoBreakChars.InertIssueType, issues[0].IssueType);
		}

		// ── Finding shape ────────────────────────────────────────────────

		[Fact]
		public void Detail_NamesBothCharactersSoAnInvisibleOneCanBeIdentified()
		{
			var issues = ScanText($"a{EN}{NBSP}b");
			Assert.Contains("U+00A0 NO-BREAK SPACE", issues[0].Detail);
			Assert.Contains("U+2013 EN DASH", issues[0].Detail);
		}

		// ── Markup context ───────────────────────────────────────────────

		/// <summary>
		/// A text excerpt states the defect but cannot LOCATE it. The same few words
		/// often recur many times on one page, so an operator reading a short text
		/// window has no way to tell which occurrence is meant. The enclosing
		/// element's tag and attributes are what make it findable in a source view —
		/// which is exactly where the enrichment sends the author.
		/// </summary>
		[Fact]
		public void Check_ContextIsTheEnclosingMarkup_NotJustTheText()
		{
			var issues = ScanHtml($"<p class=\"c1\">Alpha{EN}{NBSP}Beta</p>");
			Assert.Single(issues);
			Assert.Contains("<p class=\"c1\">", issues[0].Context);
			Assert.Contains("[NO-BREAK SPACE U+00A0]", issues[0].Context);
		}

		[Fact]
		public void Scan_WithoutMarkup_FallsBackToTheTextExcerpt()
		{
			// Unit callers hold only text; the finding must still render.
			var issues = ScanText($"Alpha{EN}{NBSP}Beta");
			Assert.Single(issues);
			Assert.DoesNotContain("<p", issues[0].Context);
			Assert.Contains("[NO-BREAK SPACE U+00A0]", issues[0].Context);
		}

		/// <summary>
		/// Two identical offender pairs in one element must not both centre on the
		/// first occurrence, or the second finding points at the wrong character.
		/// </summary>
		[Fact]
		public void Check_RepeatedPairsInOneElement_CentreOnTheirOwnOccurrence()
		{
			// The element must EXCEED ContentQualityMaxExcerpt for centring to be
			// observable: when the whole element fits in the window, Centred
			// correctly returns all of it and both findings share one context.
			var pad = new string('x', 250);
			var issues = ScanHtml($"<p>{pad}{EN}{NBSP}FIRST{pad}{EN}{NBSP}SECOND{pad}</p>");
			Assert.Equal(2, issues.Count);
			Assert.NotEqual(issues[0].Context, issues[1].Context);
			Assert.Contains("FIRST", issues[0].Context);
			Assert.Contains("SECOND", issues[1].Context);
		}

		[Fact]
		public void Check_ElementShorterThanTheWindow_IsShownWhole()
		{
			// The companion case, stated so the behaviour above is not mistaken for
			// a bug later: a short element is rendered in full, so two findings in
			// it legitimately carry the same context.
			var issues = ScanHtml($"<p>Alpha{EN}{NBSP}Beta{EN}{NBSP}Gamma</p>");
			Assert.Equal(2, issues.Count);
			Assert.Equal(issues[0].Context, issues[1].Context);
			Assert.Contains("Alpha", issues[0].Context);
			Assert.Contains("Gamma", issues[0].Context);
		}

		/// <summary>
		/// Markers must be rendered BEFORE the window is taken. Centring raw markup
		/// and marking afterwards lets the expansion (22 characters per no-break
		/// space) push the reported defect out of the trailing cut.
		/// </summary>
		[Fact]
		public void Check_MarkerExpansionCannotPushTheDefectOutOfTheWindow()
		{
			var filler = new string('x', 60);
			var many = string.Concat(Enumerable.Repeat($"{NBSP}a", 12));
			var issues = ScanHtml($"<p>{filler}{many}{filler}{EN}{NBSP}TARGET</p>");
			var last = issues[^1];
			Assert.Contains("TARGET", last.Context);
		}

		/// <summary>
		/// TicketText.log is plain text with no colour and no markup, so the excerpt
		/// has to identify the character by itself or the finding cannot be acted on.
		/// </summary>
		[Fact]
		public void Context_RendersTheGlueCharacterVisibly()
		{
			var issues = ScanText($"Alpha{EN}{NBSP}Beta");
			Assert.Contains("[NO-BREAK SPACE U+00A0]", issues[0].Context);
		}

		// ── Line edges and runs ──────────────────────────────────────────

		private static List<QualityIssue> DoubleSpaces(string html) =>
			ScanHtml(html).Where(i => i.IssueType == NoBreakChars.DoubleSpaceIssueType).ToList();

		[Theory]
		// A control whose only content is a no-break space between source line breaks.
		[InlineData("<button>\r\n\t\t&nbsp;\r\n\t</button>")]
		// A no-break space opening a box, before an image.
		[InlineData("<div>&nbsp;\r\n\t<img alt=\"\">\r\n</div>")]
		// Trailing spacing at the end of a paragraph.
		[InlineData("<p>Last words.&nbsp; &nbsp;</p>")]
		// Spacing before a line break, through an inline parent.
		[InlineData("<p><span>Line one&nbsp; </span><br>Line two</p>")]
		// Spacing opening a paragraph, inside an inline parent.
		[InlineData("<p><span> &nbsp;Opening words</span></p>")]
		public void SpacingWithNoWordOnOneSide_IsNotADoubleSpace(string html) =>
			Assert.Empty(DoubleSpaces(html));

		[Theory]
		[InlineData("<p>One sentence.&nbsp; Next sentence.</p>")]
		[InlineData("<p>Some words &nbsp;more words</p>")]
		// The following words are in a sibling element: still the same line.
		[InlineData("<p>Some words&nbsp; <b>bold words</b></p>")]
		// The preceding words are in the parent, before the inline element.
		[InlineData("<p>Some words<span> &nbsp;more</span></p>")]
		public void SpacingBetweenWords_IsADoubleSpace(string html) =>
			Assert.Single(DoubleSpaces(html));

		[Fact]
		public void ARunOfSpaces_IsOneFinding()
		{
			var issue = Assert.Single(DoubleSpaces("<td>Name of a fund&nbsp; &nbsp; &nbsp; &nbsp; Class A</td>"));
			Assert.Contains("run of 8 spaces, 4 of them U+00A0 NO-BREAK SPACE", issue.Detail);
		}

		[Fact]
		public void ARunPerBulletLine_EachIsOneFinding()
		{
			var issues = DoubleSpaces("<p>•&nbsp;&nbsp;&nbsp; first point,<br>\n•&nbsp;&nbsp;&nbsp; second point</p>");
			Assert.Equal(2, issues.Count);
			Assert.All(issues, i => Assert.Contains("run of 4 spaces, 3 of them", i.Detail));
		}

		[Fact]
		public void NoBreakSpacesWithoutAnOrdinarySpace_AreNoRun() =>
			Assert.Empty(DoubleSpaces("<p>Indent&nbsp;&nbsp;&nbsp;words</p>"));

		[Theory]
		// Suspended compound: the no-break hyphen keeps the hyphen on its word.
		[InlineData("Input\u2011 and output")]
		// The hyphen standing for the shared first part, after a space.
		[InlineData("Upload and \u2011download")]
		public void ANoBreakHyphenNextToASpace_IsNoFinding(string text) =>
			Assert.Empty(ScanText(text));

		[Fact]
		public void AGlueWrittenAsAnEntity_IsMarkedInTheExcerpt()
		{
			var issue = Assert.Single(ScanHtml("<p>You can pay &nbsp;small amounts without a PIN.</p>"),
				i => i.IssueType == NoBreakChars.DoubleSpaceIssueType);
			Assert.Contains("[NO-BREAK SPACE U+00A0]", issue.Context);
			var marked = CrawlerSuite.Core.Display.QualityMarks.For(issue.IssueType, issue.Context, issue.Detail, DefaultConfig());
			Assert.NotEmpty(marked.Marks);
		}
	}
}
