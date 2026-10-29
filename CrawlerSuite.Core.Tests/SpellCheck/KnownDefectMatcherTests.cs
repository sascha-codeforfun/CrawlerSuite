namespace CrawlerSuite.Core.Tests.SpellCheck
{
	using System.Collections.Generic;
	using CrawlerSuite.Core.SpellCheck;
	using Xunit;

	public class KnownDefectMatcherTests
	{
		private static KnownDefectMatcher Matcher() => new(new Dictionary<string, List<string>>
		{
			["div[@data-floe-label]"] = new() { "Treibeis" },            // exact
			["div[@data-floe-title]"] = new() { "Weiter zu*" },                 // prefix
		});

		[Fact]
		public void Empty_WhenNoConfig()
		{
			Assert.True(new KnownDefectMatcher(null).IsEmpty);
			Assert.True(new KnownDefectMatcher(new Dictionary<string, List<string>>()).IsEmpty);
		}

		[Fact]
		public void Exact_MutesWholeValueWords()
		{
			var m = Matcher();
			Assert.True(m.IsKnownDefect("div[@data-floe-label]", "Treibeis", "Treibeis"));
		}

		[Fact]
		public void Exact_DoesNotMatch_WhenValueDiffers()
		{
			var m = Matcher();
			// value is not exactly "Treibeis" → no mute
			Assert.False(m.IsKnownDefect("div[@data-floe-label]", "Treibeis extra", "Treibeis"));
		}

		[Fact]
		public void Prefix_MutesLiteralWords_ButKeepsVaryingTail()
		{
			var m = Matcher();
			const string value = "Weiter zu Schollenkunde";

			Assert.True(m.IsKnownDefect("div[@data-floe-title]", value, "Weiter")); // literal → mute
			Assert.True(m.IsKnownDefect("div[@data-floe-title]", value, "zu"));      // literal → mute
			Assert.False(m.IsKnownDefect("div[@data-floe-title]", value, "Schollenkunde")); // tail → still checked
		}

		[Fact]
		public void Prefix_DoesNotMatch_WhenValueDoesNotStartWithLiteral()
		{
			var m = Matcher();
			Assert.False(m.IsKnownDefect("div[@data-floe-title]", "Etwas anderes", "Weiter"));
		}

		[Fact]
		public void WrongPath_NotMuted()
		{
			var m = Matcher();
			// "Treibeis" elsewhere (e.g. real body text) is NOT muted — only from the declared path.
			Assert.False(m.IsKnownDefect("p[#text]", "Treibeis", "Treibeis"));
		}

		// ---- The construction guard branches.

		[Fact]
		public void EmptyConfig_IsEmpty_AndMutesNothing()
		{
			var m = new KnownDefectMatcher(new Dictionary<string, List<string>>());
			Assert.True(m.IsEmpty);
			Assert.False(m.IsKnownDefect("div[@x]", "anything", "anything"));
		}

		[Fact]
		public void NullPatternList_EntrySkipped_NoThrow()
		{
			// A config entry whose pattern list is null is skipped, not dereferenced.
			var m = new KnownDefectMatcher(new Dictionary<string, List<string>> { ["div[@x]"] = null! });
			Assert.True(m.IsEmpty);
			Assert.False(m.IsKnownDefect("div[@x]", "anything", "anything"));
		}

		[Fact]
		public void EmptyPatternString_Skipped_RealPatternStillWorks()
		{
			// An empty pattern string is skipped; a real sibling pattern still compiles and matches.
			var m = new KnownDefectMatcher(new Dictionary<string, List<string>>
			{
				["div[@x]"] = new() { "", "Mustertext" },
			});
			Assert.True(m.IsKnownDefect("div[@x]", "Mustertext", "Mustertext"));
		}
	}
}
