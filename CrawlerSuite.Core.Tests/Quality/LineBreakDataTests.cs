using System.Globalization;
using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// These tests exist to stop two specific regressions, both of which would be
	/// silent — the check would keep running and simply stop reporting.
	///
	///   1. A Unicode data refresh moves a character between line break classes.
	///      Unicode 17.0 already did this once: en dash, hyphen and figure dash left
	///      class BA for the new class HH. Behaviour was unchanged, but code testing
	///      for "BA" stopped matching an en dash — the single most common case in
	///      real content.
	///   2. Someone "simplifies" the table by deriving it from General_Category.
	///      Dash_Punctuation spans seven line break classes and is wrong in both
	///      directions.
	/// </summary>
	public class LineBreakDataTests
	{
		// ── Glue: the characters an author actually inserts ──────────────

		[Theory]
		[InlineData('\u00A0')]  // NO-BREAK SPACE
		[InlineData('\u202F')]  // NARROW NO-BREAK SPACE
		[InlineData('\u2007')]  // FIGURE SPACE
		[InlineData('\u2011')]  // NON-BREAKING HYPHEN
		public void IsAuthorGlue_AcceptsTheFourInsertableGlueCharacters(char c)
		{
			Assert.True(LineBreakData.IsAuthorGlue(c));
		}

		[Fact]
		public void IsAuthorGlue_RejectsNarrowerGlueThatNobodyTypes()
		{
			// All are class GL, none is an author saying "do not wrap here".
			Assert.False(LineBreakData.IsAuthorGlue('\u035C'));  // COMBINING DOUBLE BREVE BELOW
			Assert.False(LineBreakData.IsAuthorGlue('\u180E'));  // MONGOLIAN VOWEL SEPARATOR
			Assert.False(LineBreakData.IsAuthorGlue('\u0F0C'));  // TIBETAN DELIMITER TSHEG BSTAR
		}

		[Fact]
		public void IsAuthorGlue_RejectsAnOrdinarySpace()
		{
			Assert.False(LineBreakData.IsAuthorGlue(' '));
		}

		// ── The LB12a exception set ──────────────────────────────────────

		[Theory]
		[InlineData('\u0020')]  // SP
		[InlineData('\u002D')]  // HY  hyphen-minus
		[InlineData('\u2013')]  // HH  EN DASH        — was BA before Unicode 17.0
		[InlineData('\u2010')]  // HH  HYPHEN         — was BA before Unicode 17.0
		[InlineData('\u2012')]  // HH  FIGURE DASH    — was BA before Unicode 17.0
		[InlineData('\u00AD')]  // BA  SOFT HYPHEN
		[InlineData('\u0009')]  // BA  TAB
		[InlineData('\u2009')]  // BA  THIN SPACE
		[InlineData('\u2003')]  // BA  EM SPACE
		[InlineData('\u007C')]  // BA  VERTICAL LINE — the common web separator
		public void AllowsBreakBeforeGlue_TrueForTheExceptionSet(char c)
		{
			Assert.True(LineBreakData.AllowsBreakBeforeGlue(c));
		}

		[Theory]
		[InlineData('\u2014')]  // B2  EM DASH            — glue after it IS protective
		[InlineData('\u2E3A')]  // B2  TWO-EM DASH
		[InlineData('\u2015')]  // AI  HORIZONTAL BAR
		[InlineData('\u301C')]  // NS  WAVE DASH
		[InlineData('\u2212')]  // PR  MINUS SIGN
		[InlineData('\u2E1A')]  // AL  HYPHEN WITH DIAERESIS
		[InlineData('\u2011')]  // GL  NON-BREAKING HYPHEN
		[InlineData('\u1806')]  // BB  MONGOLIAN TODO SOFT HYPHEN
		[InlineData('\u3030')]  // ID  WAVY DASH
		[InlineData('\u003D')]  // AL  EQUALS SIGN — AL by default; UAX #14 names it
		                        //     as the standard TAILORING example, so its
		                        //     absence is deliberate
		[InlineData('a')]
		public void AllowsBreakBeforeGlue_FalseForEverythingElse(char c)
		{
			Assert.False(LineBreakData.AllowsBreakBeforeGlue(c));
		}

		/// <summary>
		/// The trap, stated as an executable fact: Dash_Punctuation is not a usable
		/// proxy. If this test ever fails it means someone swapped the table for a
		/// category test, and en dash and em dash are being treated alike.
		/// </summary>
		[Fact]
		public void DashPunctuation_IsNotAProxyForTheExceptionSet()
		{
			// Both are Pd. Only one permits a break before following glue.
			Assert.Equal(UnicodeCategory.DashPunctuation, CharUnicodeInfo.GetUnicodeCategory('\u2013'));
			Assert.Equal(UnicodeCategory.DashPunctuation, CharUnicodeInfo.GetUnicodeCategory('\u2014'));
			Assert.True(LineBreakData.AllowsBreakBeforeGlue('\u2013'));
			Assert.False(LineBreakData.AllowsBreakBeforeGlue('\u2014'));

			// And two that are NOT Pd but must still match.
			Assert.NotEqual(UnicodeCategory.DashPunctuation, CharUnicodeInfo.GetUnicodeCategory('\u00AD'));
			Assert.NotEqual(UnicodeCategory.DashPunctuation, CharUnicodeInfo.GetUnicodeCategory('\u2009'));
			Assert.True(LineBreakData.AllowsBreakBeforeGlue('\u00AD'));
			Assert.True(LineBreakData.AllowsBreakBeforeGlue('\u2009'));
		}

		// ── LB9 transparency ─────────────────────────────────────────────

		[Fact]
		public void IsTransparent_TrueForCombiningMarksAndJoiners()
		{
			Assert.True(LineBreakData.IsTransparent('\u0301'));  // COMBINING ACUTE ACCENT
			Assert.True(LineBreakData.IsTransparent('\u200D'));  // ZERO WIDTH JOINER
			Assert.True(LineBreakData.IsTransparent('\u200E'));  // LEFT-TO-RIGHT MARK
		}

		[Fact]
		public void IsTransparent_FalseForFormatCharactersThatCarryTheirOwnClass()
		{
			// Cf or similar, but each has a real line break class of its own, so
			// stepping over them in a backwards scan would give the wrong answer.
			Assert.False(LineBreakData.IsTransparent('\u00AD'));  // BA
			Assert.False(LineBreakData.IsTransparent('\u2060'));  // WJ
			Assert.False(LineBreakData.IsTransparent('\uFEFF'));  // WJ
			Assert.False(LineBreakData.IsTransparent('\u200B'));  // ZW
			Assert.False(LineBreakData.IsTransparent('\u180E'));  // GL
			Assert.False(LineBreakData.IsTransparent('a'));
		}

		// ── Operator-facing naming ───────────────────────────────────────

		[Fact]
		public void Describe_NamesTheCharacterAndItsCodepoint()
		{
			Assert.Equal("U+00A0 NO-BREAK SPACE", LineBreakData.Describe('\u00A0'));
			Assert.Equal("U+2013 EN DASH", LineBreakData.Describe('\u2013'));
			Assert.Equal("U+202F NARROW NO-BREAK SPACE", LineBreakData.Describe('\u202F'));
		}

		[Fact]
		public void Describe_FallsBackToTheBareCodepoint()
		{
			Assert.Equal("U+0041", LineBreakData.Describe('A'));
		}

		/// <summary>
		/// The scope boundary, as an executable statement. These characters ARE class
		/// BA — they are excluded because they cannot occur in the scripts this tool
		/// is used against, and a dropped character is a silent miss rather than a
		/// misfiled finding. If the tool is ever pointed at these scripts, this test
		/// is the place that says what to add and why.
		/// </summary>
		[Theory]
		[InlineData('\u0964')]  // DEVANAGARI DANDA
		[InlineData('\u0E5A')]  // THAI CHARACTER ANGKHANKHU
		[InlineData('\u17D4')]  // KHMER SIGN KHAN
		[InlineData('\u3000')]  // IDEOGRAPHIC SPACE
		[InlineData('\u0F0B')]  // TIBETAN MARK INTERSYLLABIC TSHEG
		[InlineData('\u2E40')]  // DOUBLE HYPHEN (editorial / Fraktur transcription)
		public void AllowsBreakBeforeGlue_ExcludesScriptsOutOfScope(char c)
		{
			Assert.False(LineBreakData.AllowsBreakBeforeGlue(c));
		}

		[Fact]
		public void UnicodeVersion_IsRecorded()
		{
			Assert.False(string.IsNullOrWhiteSpace(LineBreakData.UnicodeVersion));
		}
	}
}
