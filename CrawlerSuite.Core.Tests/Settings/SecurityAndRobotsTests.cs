using CrawlerSuite.Core.Quality;
using CrawlerSuite.Core.Settings;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Settings
{
	/// <summary>Security-relevant pattern groups, the fixed search rule, and the quality usage counts.</summary>
	[Collection("SecurityPatterns")] // the registry is process-wide
	public class SecurityAndRobotsTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), "st-secrob-" + Guid.NewGuid().ToString("N"));

		public SecurityAndRobotsTests() => Directory.CreateDirectory(dir);

		public void Dispose()
		{
			SecurityPatterns.Learn(null);
			try { Directory.Delete(dir, recursive: true); } catch { }
		}

		[Fact]
		public void Security_DecidedByTheGroupSwitch_NotTheCategory()
		{
			SecurityPatterns.Learn(
			[
				new ContentUnwantedPattern { Category = "Anything", Name = "Leak", SecurityRelevant = true, Patterns = ["x"] },
				new ContentUnwantedPattern { Category = "Security", Name = "Tidy", Patterns = ["y"] },
			]);
			Assert.True(SecurityPatterns.IsSecurity("UNWANTED_PATTERN", "Anything: Leak — pattern: x"));
			Assert.False(SecurityPatterns.IsSecurity("UNWANTED_PATTERN", "Security: Tidy — pattern: y"));
			Assert.False(SecurityPatterns.IsSecurity("WORD_COLLISION", "Anything: Leak — pattern: x"));
			Assert.Equal("Leak", SecurityPatterns.GroupOf("Anything: Leak — open placeholder, missing closing ')'"));
		}

		[Fact]
		public void Triage_SecurityPagesComeFirst()
		{
			SecurityPatterns.Learn([new ContentUnwantedPattern { Name = "Leak", SecurityRelevant = true, Patterns = ["x"] }]);
			TriageItem Item(string page, string detail) =>
				new(page + detail, [detail], page, "UNWANTED_PATTERN", "UNWANTED_PATTERN", "Your patterns", "Unwanted patterns", detail, "", null);
			var items = new[]
			{
				Item("https://x/a", "C: Tidy — pattern: y"),
				Item("https://x/a", "C: Tidy — pattern: z"),
				Item("https://x/b", "C: Leak — pattern: x"),
			};
			var modes = new Dictionary<string, TriageMode> { ["UNWANTED_PATTERN"] = TriageMode.On };
			var pages = TriageBoard.Pages(items, modes, new TriageLedger());
			Assert.Equal("https://x/b", pages[0].Url);
			Assert.True(pages[0].HasSecurity);
		}

		[Theory]
		[InlineData("noindex", true)]
		[InlineData("none", true)]
		[InlineData("max-snippet:20, noindex", true)]
		[InlineData("googlebot: noindex", false)]
		[InlineData("googlebot: nofollow, noindex", false)]
		[InlineData("noindex, otherbot: nofollow", true)]
		[InlineData("unavailable_after: 2026-12-31", false)]
		public void Header_GeneralDirectivesOnly(string header, bool outOfSearch) =>
			Assert.Equal(outOfSearch, SearchRobots.IsOutOfSearch(null, [header]));

		[Fact]
		public void Header_ReadFromTheSidecar_EveryOccurrence()
		{
			var page = Path.Combine(dir, "abc.html");
			File.WriteAllText(page, "<html></html>");
			File.WriteAllText(Path.Combine(dir, "abc.header"),
				"=== REQUEST ===\nGET https://x/\nX-Robots-Tag: noindex\n=== RESPONSE ===\nHTTP 200\nX-Robots-Tag: googlebot: noarchive\nX-Robots-Tag: none\n");
			var values = SearchRobots.HeaderValuesOf(page);
			Assert.Equal(new[] { "googlebot: noarchive", "none" }, values.ToArray()); // request headers are not response headers
			Assert.True(SearchRobots.IsOutOfSearch("index", values));
		}

		[Fact]
		public void Usage_Merge_KeepsTheOtherWritersCounts()
		{
			SettingsUsage.Merge(dir, new Dictionary<string, IReadOnlyDictionary<string, UsageCount>>
			{
				[SettingsUsage.Suppressions] = new Dictionary<string, UsageCount> { [SettingsUsage.RuleKey("WORD_COLLISION", "")] = new(3, 9) },
			});
			new SettingsUsage(new CrawlerSuite.Core.SpellCheck.SpellCheckEngineConfig { GlobalXpathToIgnore = ["//a"] }).Write(dir);

			var report = SettingsUsage.Read(dir)!;
			Assert.Equal(new UsageCount(3, 9), report.Of(SettingsUsage.Suppressions, SettingsUsage.RuleKey("WORD_COLLISION", "")));
			Assert.Equal(new UsageCount(0, 0), report.Of(SettingsUsage.Xpaths, "//a"));
		}

		[Fact]
		public void Suppression_CountsPagesPerRule()
		{
			var issues = new[]
			{
				new QualityIssue("a.html", "WORD_COLLISION", "d", "c"),
				new QualityIssue("a.html", "WORD_COLLISION", "d", "c2"),
				new QualityIssue("b.html", "WORD_COLLISION", "d", "c"),
			};
			var result = IssueSuppressions.Apply(issues, [new IssueSuppressionRule { Type = "WORD_COLLISION", Comment = "why" }]);
			Assert.Equal(3, result.RuleHits[0]);
			Assert.Equal(2, result.RulePages[0]);
		}
	}

	[CollectionDefinition("SecurityPatterns", DisableParallelization = true)]
	public class SecurityPatternsCollection
	{
	}
}
