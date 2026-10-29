using Xunit;
using CrawlerSuite.Core.SpellCheck;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Tests for pure text-manipulation helpers in Tools: StripWordPrefix.
	///
	/// StripWordPrefix is a pure, internal function; the tests in this file
	/// are what its exposure to the test assembly is for.
	/// </summary>
	[Collection("Logger")]
	public class WordPrefixTests
	{

		// ── StripWordPrefix ─────────────────────────────────────────────────

		[Fact]
		public void StripWordPrefix_NoHyphenReturnsWordUnchanged()
		{
			// The fast-path: if the word has no '-', no prefix can match.
			var result = WordPrefix.Strip("Suite", new[] { "BRAND" });

			Assert.Equal("Suite", result);
		}

		[Fact]
		public void StripWordPrefix_EmptyPrefixesReturnsWordUnchanged()
		{
			var result = WordPrefix.Strip("BRAND-Suite", Array.Empty<string>());

			Assert.Equal("BRAND-Suite", result);
		}

		[Fact]
		public void StripWordPrefix_StripsMatchingPrefix()
		{
			var result = WordPrefix.Strip("BRAND-Suite", new[] { "BRAND" });

			Assert.Equal("Suite", result);
		}

		[Fact]
		public void StripWordPrefix_PrefixMatchIsCaseInsensitive()
		{
			var result = WordPrefix.Strip("brand-Suite", new[] { "BRAND" });

			Assert.Equal("Suite", result);
		}

		[Fact]
		public void StripWordPrefix_AnEntryStandingAlone_IsAccepted()
		{
			// A known word part alone ("We at Wonder …") is the entry itself: accepted like
			// a word made of entries. Any case, as everywhere in the list.
			Assert.Null(WordPrefix.Strip("BRAND", new[] { "BRAND" }));
			Assert.Null(WordPrefix.Strip("brand", new[] { "BRAND" }));
			Assert.Equal("BRANDS", WordPrefix.Strip("BRANDS", new[] { "BRAND" }));
			Assert.Null(WordPrefix.Strip("S-Xyzzy", new[] { "S", "Xyzzy" })); // the last piece alone, after recursion
		}

		[Fact]
		public void StripWordPrefix_ExactMatchOfHyphenatedPrefixReturnsNull()
		{
			// "BRAND-Suite" exactly equals a prefix → null (nothing left to spell-check).
			var result = WordPrefix.Strip("BRAND-Suite", new[] { "BRAND-Suite" });

			Assert.Null(result);
		}

		[Fact]
		public void StripWordPrefix_PrefixWithTrailingHyphenAndNothingAfter_ReturnsNull()
		{
			// "BRAND-" matches "BRAND" + "-" with empty remainder → null.
			var result = WordPrefix.Strip("BRAND-", new[] { "BRAND" });

			Assert.Null(result);
		}

		[Fact]
		public void StripWordPrefix_PrefersLongerPrefixFirst()
		{
			// Sort-by-length-desc means "BRAND-Suite" should be tried before
			// "BRAND" alone. Both match the input; the longer one wins.
			var result = WordPrefix.Strip(
				"BRAND-Suite-Component",
				new[] { "BRAND", "BRAND-Suite" });

			Assert.Equal("Component", result);
		}

		[Fact]
		public void StripWordPrefix_RecursivelyStripsChainedPrefixes()
		{
			// After stripping "BRAND", the remainder "Suite-Component" is
			// re-checked against the prefix list. "Suite" matches → "Component".
			var result = WordPrefix.Strip(
				"BRAND-Suite-Component",
				new[] { "BRAND", "Suite" });

			Assert.Equal("Component", result);
		}

		[Fact]
		public void StripWordPrefix_NonMatchingHyphenatedWordReturnsUnchanged()
		{
			var result = WordPrefix.Strip("foo-bar", new[] { "BRAND" });

			Assert.Equal("foo-bar", result);
		}

		[Fact]
		public void StripWordPrefix_SkipsEmptyPrefixesInList()
		{
			// Empty/whitespace prefixes are filtered out before sorting — they
			// would otherwise match every input and produce nonsense.
			var result = WordPrefix.Strip(
				"BRAND-Suite",
				new[] { "", "  ", "BRAND" });

			Assert.Equal("Suite", result);
		}
	}
}
