using CrawlerSuite.Core.Findings;
using Xunit;

namespace CrawlerSuite.Core.Tests.Findings
{
	/// <summary>The explanation cards triage shows are the Results' cards, keyed per type (and per cause for invisibles).</summary>
	public class FindingBasisTests
	{
		[Fact]
		public void BasisFor_IsTheResultsCard()
		{
			var found = FindingsCatalog.BasisFor("ARIA_HIDDEN_FOCUSABLE", "anything");
			Assert.NotNull(found);
			Assert.Same(FindingsCatalog.Bases["ARIA_HIDDEN_FOCUSABLE"], found.Value.Basis);
			Assert.Equal("ARIA_HIDDEN_FOCUSABLE", found.Value.Key);
		}

		[Fact]
		public void BasisFor_InvisibleCharacters_ByCause()
		{
			var bidi = FindingsCatalog.BasisFor("INVISIBLE_CHAR_IN_BODY", "RLO (U+202E) found");
			var debris = FindingsCatalog.BasisFor("INVISIBLE_CHAR_IN_BODY", "ZWSP (U+200B) found");
			Assert.NotEqual(bidi!.Value.Key, debris!.Value.Key);
			Assert.True(bidi.Value.Basis.IsViolation);
		}

		[Fact]
		public void BasisFor_TypeWithoutCard_IsNull()
		{
			Assert.Null(FindingsCatalog.BasisFor("NO_SUCH_TYPE", "x"));
		}

		[Fact]
		public void EveryContentQualityType_HasACard_WithWorkingLinks()
		{
			foreach (var (_, _, types) in FindingsCatalog.ContentQualityFamilies)
			{
				foreach (var (type, _) in types)
				{
					var detail = type == "INVISIBLE_CHAR_IN_BODY" ? "ZWSP (U+200B) found" : "x";
					var found = FindingsCatalog.BasisFor(type, detail);
					Assert.True(found is not null, type);
					Assert.NotNull(found.Value.Basis.Links);
					Assert.All(found.Value.Basis.Links, l => Assert.StartsWith("https://", l.Url));
					Assert.EndsWith(".", found.Value.Basis.Verdict);
				}
			}
		}

		[Fact]
		public void ControlCharacters_AreExplainedByTheirCharacter()
		{
			var shy = FindingsCatalog.BasisFor("CONTROL_CHARS_IN_CONTENT", "Found SHY (U+00AD) in <title> text")!.Value;
			var zwsp = FindingsCatalog.BasisFor("CONTROL_CHARS_IN_CONTENT", "Found ZWSP (U+200B) in <p> text")!.Value;
			var unknown = FindingsCatalog.BasisFor("CONTROL_CHARS_IN_CONTENT", "no code point")!.Value;
			Assert.NotEqual(shy.Key, zwsp.Key);
			Assert.Contains("title", shy.Basis.Text, StringComparison.Ordinal);
			Assert.Same(FindingsCatalog.Bases["CONTROL_CHARS_IN_CONTENT"], unknown.Basis);
		}

		[Fact]
		public void LineBreaksAndTabs_InTitleOrMeta_GetTheirOwnCard()
		{
			var lf = FindingsCatalog.BasisFor("CONTROL_CHARS_IN_CONTENT", "Found LF (U+000A) in meta[@name=\"description\"] content")!.Value;
			var cr = FindingsCatalog.BasisFor("CONTROL_CHARS_IN_CONTENT", "Found CR (U+000D) in <title> text")!.Value;
			var tab = FindingsCatalog.BasisFor("CONTROL_CHARS_IN_CONTENT", "Found TAB (U+0009) in <title> text")!.Value;
			var c1 = FindingsCatalog.BasisFor("CONTROL_CHARS_IN_CONTENT", "Found C1 control (U+0085) in <title> text")!.Value;

			Assert.Equal(lf.Key, cr.Key);
			Assert.Equal(lf.Key, tab.Key);
			Assert.NotEqual(lf.Key, c1.Key);
			Assert.Contains("plain space", lf.Basis.Text, StringComparison.Ordinal);
			Assert.Contains("PDF", lf.Basis.Text, StringComparison.Ordinal);
			Assert.DoesNotContain("not counted", lf.Basis.Text, StringComparison.Ordinal);
		}

		[Fact]
		public void ContentBeforeDoctype_IsExplainedThoughWrittenAsMalformedHtml()
		{
			Assert.Equal("CONTENT_BEFORE_DOCTYPE", FindingsCatalog.BasisFor("MALFORMED_HTML", "CONTENT_BEFORE_DOCTYPE")!.Value.Key);
			Assert.Equal("MALFORMED_HTML", FindingsCatalog.BasisFor("MALFORMED_HTML", "TagNotClosed")!.Value.Key);
		}

		[Fact]
		public void WcagCards_CarryTheLegalBenchmark_OthersDoNot()
		{
			foreach (var type in new[] { "MISPLACED_ANCHOR_EMPTY", "ARIA_HIDDEN_FOCUSABLE", "STYLED_HEADING" })
			{
				var basis = FindingsCatalog.Bases[type];
				Assert.True(basis.IsWcag, type);
				Assert.EndsWith(FindingBasis.LegalNote, basis.FullText, StringComparison.Ordinal);
				Assert.Contains(FindingBasis.LegalLinks[0], basis.AllLinks);
			}

			var markup = FindingsCatalog.Bases["ARIA_REFERENCE_BROKEN"];
			Assert.False(markup.IsWcag);
			Assert.Equal(markup.Text, markup.FullText);
			Assert.Equal(markup.Links.Count, markup.AllLinks.Count);
		}

		[Fact]
		public void LegalNote_GivesDatesOnlyForFacts()
		{
			Assert.Contains("28 June 2025", FindingBasis.LegalNote, StringComparison.Ordinal);
			Assert.Contains("WCAG 2.1 Level AA", FindingBasis.LegalNote, StringComparison.Ordinal);
			Assert.Contains("expected to become the harmonised reference", FindingBasis.LegalNote, StringComparison.Ordinal);
			Assert.Contains("applicable since", FindingBasis.LegalNote, StringComparison.Ordinal); // in force earlier, applies from then
			Assert.DoesNotContain("requires", FindingBasis.LegalNote, StringComparison.Ordinal);  // a standard requires nothing by itself
			Assert.DoesNotContain("November", FindingBasis.LegalNote, StringComparison.Ordinal);  // the citation date is not a fact yet
			Assert.DoesNotContain("2027", FindingBasis.LegalNote, StringComparison.Ordinal);
		}

		[Fact]
		public void LanguageOfParts_IsAWcagCard()
		{
			Assert.True(FindingsCatalog.LanguageOfParts.IsWcag);
			Assert.Contains(FindingsCatalog.LanguageOfParts.Links, l => l.Url.EndsWith("language-of-parts.html", StringComparison.Ordinal));
		}

		[Fact]
		public void ALikelyWcagIssue_OpensWithThePerson_AndEndsWithTheRequirement()
		{
			var cards = FindingsCatalog.Bases.Values.Append(FindingsCatalog.LanguageOfParts).Where(b => b.IsWcag).ToList();
			Assert.Equal(7, cards.Count);
			Assert.All(cards, b =>
			{
				Assert.DoesNotContain("WCAG", b.Text, StringComparison.Ordinal); // the standard is named at the end, not argued in the text
				Assert.DoesNotContain("violation", b.Text, StringComparison.OrdinalIgnoreCase);
				Assert.StartsWith("WCAG 2.2 · ", b.Requirement);
				Assert.Contains($"\n\n{FindingBasis.RequirementLabel}: {b.Requirement}.\n\n{FindingBasis.LegalNote}", b.FullText, StringComparison.Ordinal);
				Assert.Equal(FindingBasis.WcagLabel, b.Headline);
			});
			Assert.Equal("Markup issue.", new FindingBasis("Markup issue. More.", [], false).Headline);
		}

		[Fact]
		public void Verdict_IsTheFirstSentence()
		{
			Assert.Equal("Keyboard users land on something a screen reader is told isn't there: focus goes somewhere silent.",
				FindingsCatalog.Bases["ARIA_HIDDEN_FOCUSABLE"].Verdict);
			Assert.Equal("likely WCAG issue", FindingsCatalog.Bases["STYLED_HEADING"].Headline);
			Assert.Equal("No full stop", new FindingBasis("No full stop", [], false).Verdict);
		}
	}
}
