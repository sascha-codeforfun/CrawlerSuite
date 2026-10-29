using CrawlerSuite.Core.Quality;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// A space on the wrong side of a hyphen inside a word (SPACE_AFTER_HYPHEN, SPACE_BEFORE_HYPHEN),
	/// and the correct suspended heads and tails it must leave alone. Invented words, plus two
	/// sample sentences.
	/// </summary>
	public class HyphenSpacingTests
	{
		private static readonly ContentQualityConfig Config = new();

		private static IReadOnlySet<string> German => HyphenSpacing.Joiners(Config, ["de"]);

		private static List<(string Type, string Detail)> Scan(string text, IReadOnlySet<string>? joiners = null) =>
			HyphenSpacing.Scan("f.html", text, "p", joiners ?? German, Config).Select(i => (i.IssueType, i.Detail)).ToList();

		// A sample sentence: a suspended head before "und", a suspended tail after a slash,
		// a spaced dash, a missing full stop (not this check's business) and a deliberate typo.
		// Typed, the slash takes no spaces ("Hauseingänge/-ausgänge"): that is its one finding.
		[Theory]
		[InlineData("Der Hauseingang ist verschlossen Hauseingänge / -ausgänge sollten immer verschlossen sein - auch an Sonn- und Fiertagen.")]
		[InlineData("Der Hauseingang ist verschlossen Hauseingänge / -ausgänge sollten immer verschlossen sein - auch an Sonn- und Feiertagen.")]
		public void TheOperatorsSentences_HaveOnlyTheSpacedSlash(string text)
		{
			var issue = Assert.Single(Scan(text));
			Assert.Equal(("SPACE_BEFORE_HYPHEN", "p: space at the slash in “Hauseingänge / -ausgänge”"), issue);
			Assert.Empty(Scan(text.Replace("Hauseingänge / -ausgänge", "Hauseingänge/-ausgänge", StringComparison.Ordinal)));
		}

		// The correct forms: a slash without spaces, "und", "oder".
		[Theory]
		[InlineData("Stadt-/Landhaus")]
		[InlineData("Stadt- und Landhaus")]
		[InlineData("Stadt- oder Landhaus")]
		public void TheCorrectWaysToJoin_HaveNoFinding(string text)
		{
			Assert.Empty(Scan(text));
		}

		[Theory]
		[InlineData("Stadt- / Landhaus", "“Stadt- / Landhaus”")]
		[InlineData("Stadt- /Landhaus", "“Stadt- /Landhaus”")]
		[InlineData("Stadt-/ Landhaus", "“Stadt-/ Landhaus”")]
		public void ASpacedSlash_AtASuspendedHead_IsReported(string text, string shown)
		{
			var issue = Assert.Single(Scan(text));
			Assert.Equal("SPACE_AFTER_HYPHEN", issue.Type);
			Assert.Equal("p: space at the slash in " + shown, issue.Detail);
			Assert.Null(HyphenSpacing.NameFrom(issue.Type, issue.Detail)); // nothing to keep as a name
		}

		// A stand-in for the German dictionary: the words these tests need, nothing else.
		private static readonly HashSet<string> Words = new(StringComparer.Ordinal) { "Stadthaus", "Hausausgänge" };

		private static List<(string Type, string Detail)> Diagnosed(string text) =>
			HyphenSpacing.Scan("f.html", text, "p", German, Config, accepts: Words.Contains).Select(i => (i.IssueType, i.Detail)).ToList();

		[Theory]
		[InlineData("Stadt- Landhaus", "SPACE_AFTER_HYPHEN", "p: “/” or a joining word missing after the hyphen in “Stadt- Landhaus”")]
		[InlineData("Hauseingänge -ausgänge", "SPACE_BEFORE_HYPHEN", "p: “/” or a joining word missing before the hyphen in “Hauseingänge -ausgänge”")]
		[InlineData("ein Xyzzy- Smartgerät", "SPACE_AFTER_HYPHEN", "p: space after the hyphen in “Xyzzy- Smartgerät”")]  // no word comes out: both fixes stand
		[InlineData("nach 7- jährigen Fristen", "SPACE_AFTER_HYPHEN", "p: stray space after the hyphen in “7- jährigen Fristen”")]
		public void TheDictionary_TellsALeftOutJoinerFromASpace(string text, string type, string detail)
		{
			Assert.Equal((type, detail), Assert.Single(Diagnosed(text)));
		}

		[Fact]
		public void ThePage_AsksTheDictionaryOfItsLanguage_AndMarksTheSlash()
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html lang=\"de\"><body><p>Ein Stadt- Landhaus</p><p>Ein Stadt- / Landhaus</p></body></html>");
			bool? German(string word, string language) => language.StartsWith("de", StringComparison.Ordinal) ? Words.Contains(word) : null;
			var issues = HyphenSpacing.Check("f.html", doc, Config, wordCheck: German).ToList();
			Assert.Equal(2, issues.Count);
			Assert.Contains("joining word missing", issues[0].Detail);
			Assert.Equal("<p>Ein Stadt- / Landhaus</p>", issues[1].Context);

			var gap = Assert.Single(CrawlerSuite.Core.Display.QualityMarks.For(issues[1].IssueType, issues[1].Context, issues[1].Detail, null).Marks,
				m => m.Role == CrawlerSuite.Core.Display.MarkRole.Offender);
			Assert.Equal("- / ", issues[1].Context.Substring(gap.Start, gap.Length));

			// A page in a language without a dictionary: the finding stays open.
			doc.LoadHtml("<html lang=\"fr\"><body><p>Ein Stadt- Landhaus</p></body></html>");
			Assert.Contains("space after the hyphen in", Assert.Single(HyphenSpacing.Check("f.html", doc, Config, wordCheck: German)).Detail);
		}

		[Theory]
		[InlineData("<p>Ein Gr&uuml;nland-/ Haus</p>")]
		[InlineData("<p>Ein Gr&#252;nland-/ Haus</p>")]
		[InlineData("<p>Ein Gr&#xfc;nland-/ Haus</p>")]
		[InlineData("<p>Ein Grünland-/ Haus</p>")]
		public void TheSpot_IsFound_WhereTheSourceWritesALetterAsAnEntity(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html lang=\"de\"><body>" + html + "</body></html>");
			var issue = Assert.Single(HyphenSpacing.Check("f.html", doc, Config));
			Assert.Equal(html, issue.Context); // the HTML, not the plain-text fallback
		}

		[Fact]
		public void MergedExcerpts_EachLineShowsItsSpot()
		{
			// Triage merges identical findings of a page, one excerpt per line: every line is marked.
			const string context = "<p>Der Xyzzy- und Hausverband</p>\n<p>Beim Xyzzy- und Hausverband</p>";
			var marks = CrawlerSuite.Core.Display.QualityMarks.For("SPACE_AFTER_HYPHEN", context,
				"p: space after the hyphen in “Xyzzy- und Hausverband”", null).Marks;
			var gaps = marks.Where(m => m.Role == CrawlerSuite.Core.Display.MarkRole.Offender).ToList();
			Assert.Equal(2, gaps.Count);
			Assert.All(gaps, g => Assert.Equal("- ", context.Substring(g.Start, g.Length)));
		}

		[Fact]
		public void WithoutADictionary_TheFindingStaysOpen()
		{
			Assert.Equal("p: space after the hyphen in “Stadt- Landhaus”", Assert.Single(Scan("Stadt- Landhaus")).Detail);
		}

		[Theory]
		[InlineData("Haus- und Gartenpflege")]
		[InlineData("Haus- u. Gartenpflege")]
		[InlineData("zwei- bis dreimal")]
		[InlineData("Haus- bzw. Gartenpflege")]
		[InlineData("Haus-, Hof- und Gartenpflege")]
		[InlineData("Ein-/Ausgang")]
		[InlineData("Bau- & Gartenmarkt")]
		[InlineData("Textilgroß- und -einzelhandel")]
		[InlineData("Hauseingänge bzw. -ausgänge")]
		[InlineData("Text - mehr Text")]
		[InlineData("Haus-Garten-Xyzzy")]
		[InlineData("bei -5 Grad")]
		[InlineData("- ein Punkt")]
		[InlineData("Xyzzy-")]
		public void CorrectForms_AreLeftAlone(string text)
		{
			Assert.Empty(Scan(text));
		}

		[Theory]
		[InlineData("Dieser Xyzzy- Haus-Effekt beschreibt", "SPACE_AFTER_HYPHEN", "Xyzzy- Haus-Effekt")]
		[InlineData("eine neue E- Mail", "SPACE_AFTER_HYPHEN", "E- Mail")]
		[InlineData("Xyzzy-\n  Haus", "SPACE_AFTER_HYPHEN", "Xyzzy- Haus")]  // a line break in the source is a space on the page
		[InlineData("Xyzzy- Haus", "SPACE_AFTER_HYPHEN", "Xyzzy- Haus")]
		[InlineData("das Haus -Garten Fest", "SPACE_BEFORE_HYPHEN", "Haus -Garten")]
		[InlineData("Garten -Nummer", "SPACE_BEFORE_HYPHEN", "Garten -Nummer")]
		public void AStraySpace_IsReported(string text, string type, string shown)
		{
			var issue = Assert.Single(Scan(text));
			Assert.Equal(type, issue.Type);
			Assert.Contains(shown, issue.Detail);
		}

		[Theory]
		[InlineData("Xyzzy- beziehungs\u00ADweise Hauslauf\u00ADzeit")] // a soft hyphen shows as nothing
		[InlineData("Verwechseln Sie Haus- mit Gartenpflege")]
		public void SoftHyphensAndMit_AreRead(string text)
		{
			Assert.Empty(Scan(text));
		}

		[Fact]
		public void AProperName_CountsInEveryLanguage()
		{
			// On an English page "und" is no joining word; the name kept as a proper name is.
			const string text = "the arbitration body of the Xyzzy- und Hausverband e.V. decides";
			var english = HyphenSpacing.Joiners(Config, ["en"]);
			Assert.Single(HyphenSpacing.Scan("f.html", text, "p", english, Config));

			var names = new ProperNames([new ProperName { Name = "Xyzzy- und Hausverband" }]);
			Assert.Empty(HyphenSpacing.Scan("f.html", text, "p", english, Config, names));

			// A longer name holding the spot counts too, any case.
			var longer = new ProperNames([new ProperName { Name = "XYZZY- UND HAUSVERBAND E.V." }]);
			Assert.Empty(HyphenSpacing.Scan("f.html", text, "p", english, Config, longer));

			// Only where the whole name stands: the same head and joiner in another phrase is still a finding.
			Assert.Single(HyphenSpacing.Scan("f.html", "the Xyzzy- und Gartenamt decides", "p", english, Config, names));

			// A tail: "Qwz -Haus" kept as written.
			var tail = new ProperNames([new ProperName { Name = "Qwz -Haus" }]);
			Assert.Empty(HyphenSpacing.Scan("f.html", "die Qwz -Haus Gruppe", "p", German, Config, tail));
		}

		[Theory]
		[InlineData("des Xyzzy- und Hausverband e.V.", "SPACE_AFTER_HYPHEN", "“Xyzzy- und Hausverband”")]
		[InlineData("ein Xyzzy- Haus, dann", "SPACE_AFTER_HYPHEN", "“Xyzzy- Haus”")]          // punctuation: no third word
		[InlineData("die Qwz -Haus Gruppe", "SPACE_BEFORE_HYPHEN", "“die Qwz -Haus”")]
		[InlineData("Qwz -Haus", "SPACE_BEFORE_HYPHEN", "“Qwz -Haus”")]                      // nothing before: no third word
		public void TheFinding_NamesTheWordNextToTheTwo(string text, string type, string shown)
		{
			var issue = Assert.Single(HyphenSpacing.Scan("f.html", text, "p", HyphenSpacing.Joiners(Config, ["en"]), Config));
			Assert.Equal(type, issue.IssueType);
			Assert.EndsWith(shown, issue.Detail);
		}

		[Fact]
		public void TheName_IsReadFromTheFinding()
		{
			var after = HyphenSpacing.NameFrom("SPACE_AFTER_HYPHEN", "p: space after the hyphen in “Xyzzy- und Hausverband”");
			Assert.Equal(new HyphenNameCandidate("Xyzzy- und", "Hausverband", false), after);
			Assert.Equal("Xyzzy- und Hausverband", after!.With(true));
			Assert.Equal("Xyzzy- und", after.With(false));

			var before = HyphenSpacing.NameFrom("SPACE_BEFORE_HYPHEN", "p: space before the hyphen in “die Qwz -Haus”");
			Assert.Equal("die Qwz -Haus", before!.With(true));
			Assert.Equal("Qwz -Haus", before.With(false));

			Assert.Equal("Xyzzy- Haus", HyphenSpacing.NameFrom("SPACE_AFTER_HYPHEN", "p: space after the hyphen in “Xyzzy- Haus”")!.With(true));
			Assert.Null(HyphenSpacing.NameFrom("WORD_COLLISION", "p: “Xyzzy- und”"));
		}

		[Fact]
		public void TheExcerpt_IsThePagesHtmlAroundTheSpot()
		{
			// A missing slash ("Xyzzy- / Hausgeld") reads only from the markup and the sentence after it.
			var doc = new HtmlDocument();
			doc.LoadHtml("<html lang=\"de\"><body><ul><li><b>Neues Xyzzy- Haus&shy;geld:<br /> </b>" +
				"Haus&shy;geld für jeden Tag.<br /> </li></ul><p>Garten&shy;haus- Qwz</p></body></html>");
			var issues = HyphenSpacing.Check("f.html", doc, Config).ToList();
			Assert.Equal(2, issues.Count);
			Assert.EndsWith("“Xyzzy- Hausgeld”", issues[0].Detail);
			Assert.Equal("<li><b>Neues Xyzzy- Haus&shy;geld:<br> </b>Haus&shy;geld für jeden Tag.<br> </li>", issues[0].Context);
			Assert.Equal("<p>Garten&shy;haus- Qwz</p>", issues[1].Context); // soft hyphens inside the word at the hyphen

			var marks = CrawlerSuite.Core.Display.QualityMarks.For(issues[0].IssueType, issues[0].Context, issues[0].Detail, null).Marks;
			var gap = Assert.Single(marks, m => m.Role == CrawlerSuite.Core.Display.MarkRole.Offender);
			Assert.Equal("- ", issues[0].Context.Substring(gap.Start, gap.Length));
			var word = Assert.Single(marks, m => m.Role == CrawlerSuite.Core.Display.MarkRole.Highlight);
			Assert.Equal("Xyzzy", issues[0].Context.Substring(word.Start, word.Length));
		}

		[Fact]
		public void TheJoiningWords_AreThePagesLanguage()
		{
			const string text = "pre- and post-war";
			Assert.Empty(Scan(text, HyphenSpacing.Joiners(Config, ["en-GB"])));
			Assert.Single(Scan(text, German));                  // "and" is no German joining word
			Assert.Single(Scan(text, HyphenSpacing.Joiners(Config, ["fr"]))); // no list: punctuation only
		}

		[Fact]
		public void TheDocument_IsReadInItsLanguage_AndCodeIsSkipped()
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html lang=\"de\"><body><p>Haus- und Garten</p><p lang=\"en\">pre- and post-war</p>" +
				"<p>Xyzzy- Haus</p><code>run -verbose now</code><pre>a- b</pre></body></html>");
			var issues = HyphenSpacing.Check("f.html", doc, Config).ToList();
			var issue = Assert.Single(issues);
			Assert.Equal("SPACE_AFTER_HYPHEN", issue.IssueType);
			Assert.Contains("Xyzzy- Haus", issue.Detail);
		}

		[Fact]
		public void AHyphenAtTheEndOfATextNode_IsNotJudgedByTheNextNode()
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html lang=\"de\"><body><p>Xyzzy-<b> Haus</b></p></body></html>");
			Assert.Empty(HyphenSpacing.Check("f.html", doc, Config));
		}
	}
}
