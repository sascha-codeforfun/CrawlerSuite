using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// No-break characters occupy width and look exactly like an ordinary space. On
	/// the console that is survivable; in TicketText.log, which is plain text with no
	/// colour and no markup, it means a finding about a no-break space cannot point
	/// at its own subject. The marker vocabulary is what makes the excerpt
	/// self-identifying on every surface.
	/// </summary>
	public class LogExcerptGlueMarkerTests
	{
		[Theory]
		[InlineData('\u00A0', "[NO-BREAK SPACE U+00A0]")]
		[InlineData('\u202F', "[NARROW NO-BREAK SPACE U+202F]")]
		[InlineData('\u2007', "[FIGURE SPACE U+2007]")]
		[InlineData('\u2011', "[NON-BREAKING HYPHEN U+2011]")]
		public void Truncate_RendersGlueCharactersVisibly(char c, string marker)
		{
			Assert.Contains(marker, LogExcerpt.Truncate($"a{c}b"));
		}

		[Fact]
		public void Truncate_LeavesOrdinarySpacesAlone()
		{
			Assert.Equal("a b", LogExcerpt.Truncate("a b"));
		}

		[Fact]
		public void Truncate_MarkersAreAsciiOnly()
		{
			// The tool flags exotic non-ASCII in content; using it in the tool's own
			// diagnostic output would be self-defeating.
			var rendered = LogExcerpt.Truncate("a\u00A0b\u202Fc\u2007d\u2011e");
			foreach (var ch in rendered)
			{
				Assert.True(ch < 128, $"non-ASCII U+{(int)ch:X4} in marker output");
			}
		}

		[Fact]
		public void Truncate_DistinguishesTheGlueCharactersFromEachOther()
		{
			// The remediation differs by character, so collapsing them to one marker
			// would leave the author unable to act.
			var rendered = LogExcerpt.Truncate("a\u00A0b\u202Fc");
			Assert.Contains("[NO-BREAK SPACE U+00A0]", rendered);
			Assert.Contains("[NARROW NO-BREAK SPACE U+202F]", rendered);
		}

		/// <summary>
		/// The accepted consequence, pinned so it is a decision rather than a
		/// surprise: these markers also enter the identity rendering, so the tracking
		/// key of a content-quality finding whose text contains a no-break character
		/// includes the markers, so such a finding resurfaces once as undecided when they first apply.
		/// Spelling decisions are keyed SPELLING|url|word and are unaffected.
		/// </summary>
		[Fact]
		public void Truncate_GlueNoLongerRendersAsABareSpace()
		{
			var rendered = LogExcerpt.Truncate("a\u00A0b");
			Assert.DoesNotContain("a b", rendered);
			Assert.False(rendered.Contains('\u00A0'), "the raw glue character must not survive rendering");
		}
	}
}
