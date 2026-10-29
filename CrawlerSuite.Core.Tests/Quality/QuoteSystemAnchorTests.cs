using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// Language-anchored QUOTE_SYSTEM_MIX offender selection. On a single-language
	/// page the offender is the opener whose system isn't valid for that language,
	/// regardless of textual order (so on a German page an English pair appearing first
	/// is not crowned "correct" with the German pair flagged). Unanchored (multi/undeclared)
	/// keeps the first-divergent rule — nothing is suppressed.
	/// </summary>
	public class QuoteSystemAnchorTests
	{
		// English pair first, then the correct German pair.
		[Fact]
		public void Anchored_De_EnglishFirstThenGerman_FlagsEnglishOpener()
		{
			// “Ueber” (English U+201C…U+201D) then „Foto“ (German U+201E…U+201C)
			var text = "im Bereich \u201CUeber\u201D die \u201EFoto\u201C aus";
			var englishOpen = text.IndexOf('\u201C');
			Assert.Equal(englishOpen, Quotes.LocateSystemMixMismatch(text, "de"));
		}

		// Order independence: flip the pairs; the English one is still the offender.
		[Fact]
		public void Anchored_De_GermanFirstThenEnglish_StillFlagsEnglishOpener()
		{
			var text = "die \u201EFoto\u201C im \u201CUeber\u201D aus";
			var firstU201C = text.IndexOf('\u201C');                  // German closer of „Foto“
			var englishOpen = text.IndexOf('\u201C', firstU201C + 1); // English opener
			Assert.Equal(englishOpen, Quotes.LocateSystemMixMismatch(text, "de"));
		}

		[Fact]
		public void Anchored_De_CleanGerman_ReturnsMinusOne()
		{
			var text = "Der \u201EBegriff\u201C hier.";   // „Begriff“ — correct German, no mix
			Assert.Equal(-1, Quotes.LocateSystemMixMismatch(text, "de"));
		}

		[Fact]
		public void Anchored_De_GuillemetMix_FlagsGuillemet()
		{
			// Correct German „X“ plus French guillemets «Y» → on a de page the guillemet is wrong.
			var text = "\u201EX\u201C und \u00ABY\u00BB";
			var guillemet = text.IndexOf('\u00AB');
			Assert.Equal(guillemet, Quotes.LocateSystemMixMismatch(text, "de"));
		}

		[Fact]
		public void Anchored_De_TwoWrongSystems_NotSuppressed()
		{
			// English “…” plus guillemets «…» — neither valid for de, but still a genuine
			// mix. Must surface (fall through to first-divergent), never suppress.
			var text = "\u201CX\u201D und \u00ABY\u00BB";
			Assert.True(Quotes.LocateSystemMixMismatch(text, "de") >= 0);
		}

		[Fact]
		public void Unanchored_KeepsFirstDivergent_FlagsGermanOpener()
		{
			// Same shape but no anchor (multi-language/undeclared): the
			// first-divergent rule applies → the German (second) opener returns.
			var text = "im Bereich \u201CUeber\u201D die \u201EFoto\u201C aus";
			var germanOpen = text.IndexOf('\u201E');
			Assert.Equal(germanOpen, Quotes.LocateSystemMixMismatch(text, null));
		}

		// The U+201E opener is shared by German-double and Slavic-double; the
		// page language decides which. On a Slavic-double page, „…” is the page's own
		// system and a French guillemet is the offender; on a de page the same „…”
		// block resolves to German-double and is clean (closer correctness is the
		// pairing walk's job, not the mix locator's).
		[Fact]
		public void Anchored_Pl_SlavicPlusGuillemet_FlagsGuillemet()
		{
			var text = "\u201EWyraz\u201D oraz \u00ABInne\u00BB";
			var guillemet = text.IndexOf('\u00AB');
			Assert.Equal(guillemet, Quotes.LocateSystemMixMismatch(text, "pl"));
		}

		[Fact]
		public void Anchored_Pl_SingleSlavicSystem_ReturnsMinusOne()
		{
			// One system only (Slavic-double, valid for pl) → no mix.
			var text = "To jest \u201EWyraz\u201D tutaj.";
			Assert.Equal(-1, Quotes.LocateSystemMixMismatch(text, "pl"));
		}

		[Fact]
		public void Anchored_De_SharedOpenerBlock_ResolvesGermanNoMix()
		{
			// „Foto” on a de page resolves the shared U+201E opener to German-double,
			// so only one system is present → the mix locator returns -1 (the wrong
			// CLOSER is reported by the pairing walk, not here).
			var text = "die \u201EFoto\u201D hier.";
			Assert.Equal(-1, Quotes.LocateSystemMixMismatch(text, "de"));
		}
	
		// ── The page language travels with the finding, and the screen blames by it ──

		private static ContentQualityConfig MixOnly() => new()
		{
			CheckQuoteSystemMixing = true,
			CheckQuotePairing = false,
			CheckLigatures = false,
			CheckLanguageMismatch = false,
			CheckSplitWordAnchors = false,
		};

		private static QualityIssue MixOn(string html)
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml(html);
			return Assert.Single(Quotes.Check("f.html", doc, MixOnly()), i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		[Fact]
		public void TheFinding_NamesThePageLanguageItWasJudgedBy()
		{
			var issue = MixOn("<html lang=\"de\"><body><li>im Bereich \u201CWasser\u201D die \u201EEis\u201C aus</li></body></html>");
			Assert.Equal("de", Quotes.PageLanguageIn(issue.Detail));
			Assert.StartsWith("Multiple quote systems: ", issue.Detail);
		}

		[Fact]
		public void WithoutADeclaredLanguage_TheFindingNamesNone()
		{
			var issue = MixOn("<html><body><li>im Bereich \u201CWasser\u201D die \u201EEis\u201C aus</li></body></html>");
			Assert.Null(Quotes.PageLanguageIn(issue.Detail));
		}

		[Fact]
		public void TheScreen_BlamesTheQuotationThatDoesNotFitThePageLanguage_WhereverItStands()
		{
			var issue = MixOn("<html lang=\"de\"><body><li>im Bereich \u201CWasser\u201D die \u201EEis\u201C aus</li></body></html>");
			var marked = CrawlerSuite.Core.Display.QualityMarks.For(issue.IssueType, issue.Context, issue.Detail, null);
			var offender = Assert.Single(marked.Marks, m => m.Role == CrawlerSuite.Core.Display.MarkRole.Offender);
			Assert.Equal(issue.Context.IndexOf('\u201C'), offender.Start); // the English opener, not the German „
		}

		[Fact]
		public void WithoutAPageLanguage_TheScreenBlamesNothing_AndSaysWhy()
		{
			var context = "im Bereich \u201CWasser\u201D die \u201EEis\u201C aus";
			var marked = CrawlerSuite.Core.Display.QualityMarks.For("QUOTE_SYSTEM_MIX", context, "Multiple quote systems: English-double, German-double", null);
			Assert.DoesNotContain(marked.Marks, m => m.Role == CrawlerSuite.Core.Display.MarkRole.Offender);
			Assert.Equal(4, marked.Marks.Count(m => m.Role == CrawlerSuite.Core.Display.MarkRole.Partner));
			Assert.Equal(CrawlerSuite.Core.Display.QualityMarks.NoPageLanguage, marked.Note);
		}

		[Fact]
		public void TheLedgerKey_IgnoresThePageLanguageNote() =>
			Assert.Equal(
				CrawlerSuite.Core.Triage.TriageSource.KeyOf("QUOTE_SYSTEM_MIX", "https://site.test/a.html", "Multiple quote systems: English-double, German-double"),
				CrawlerSuite.Core.Triage.TriageSource.KeyOf("QUOTE_SYSTEM_MIX", "https://site.test/a.html", "Multiple quote systems: English-double, German-double (page language: de)"));
	}
}
