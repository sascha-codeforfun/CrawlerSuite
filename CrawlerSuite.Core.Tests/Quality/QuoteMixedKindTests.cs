using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// Straight/typographic kind consistency. A double-quote PAIR must not mix
	/// kinds: a typographic opener closed by a straight ASCII quote (or vice-versa) is
	/// QUOTE_MIXED_KIND. A CONSISTENT straight pair ("…") is deliberately never flagged
	/// — the tool can't know the surrounding text isn't a context where ASCII quotes are
	/// correct, so only the inconsistency, which is wrong regardless of intent, fires.
	/// </summary>
	public class QuoteMixedKindTests
	{
		private static ContentQualityConfig Config() => new()
		{
			ContentQualityExcerptRadius   = 120,
			ContentQualityMaxExcerpt = 400,
		};

		[Fact]
		public void TypographicOpener_StraightCloser_IsMixedKind_NotUnmatched()
		{
			// „Online-Shop" — German opener, straight ASCII closer.
			var text = "die App \u201EOnline-Shop\u0022 noch nicht";
			var issues = Quotes.CheckPairing("f.html", text, Config(), "de").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_MIXED_KIND");
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_UNMATCHED");
		}

		[Fact]
		public void StraightOpener_TypographicCloser_IsMixedKind()
		{
			// "Wort” — straight opener, English typographic closer (U+201D, closer-only).
			var text = "ein \u0022Wort\u201D hier";
			var issues = Quotes.CheckPairing("f.html", text, Config(), "de").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_MIXED_KIND");
		}

		[Fact]
		public void ConsistentStraightPair_IsNotFlagged()
		{
			// "Wort" — straight open + straight close. Undecidable intent → never flagged.
			var text = "ein \u0022Wort\u0022 hier";
			var issues = Quotes.CheckPairing("f.html", text, Config(), "de").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void MixedKind_Flagged_NeverAmbiguous()
		{
			// „Online-Shop" — German opener + straight closer. MIXED_KIND is
			// unambiguous by construction; it is always a finding, never ambiguous.
			var cfg = new ContentQualityConfig
			{
				ContentQualityExcerptRadius   = 120,
				ContentQualityMaxExcerpt = 400,
			};
			var text = "die App \u201EOnline-Shop\u0022 noch nicht";
			var issues = Quotes.CheckPairing("f.html", text, cfg, "de").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_MIXED_KIND");
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_AMBIGUOUS");
		}
	}
}
