using CrawlerSuite.Core.Settings;
using Xunit;

namespace CrawlerSuite.Core.Tests.Settings
{
	/// <summary>Example texts per entry (the words that need a Known word part) next to the counts.</summary>
	public class SettingsUsageExamplesTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), "st-usage-ex-" + Guid.NewGuid().ToString("N"), "2026-10-13-09-30-00");

		public SettingsUsageExamplesTests() => Directory.CreateDirectory(dir);

		public void Dispose()
		{
			try { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); } catch { }
		}

		[Fact]
		public void Examples_RoundTrip_AndKeepTheCounts()
		{
			SettingsUsage.Merge(dir, new Dictionary<string, IReadOnlyDictionary<string, UsageCount>>
			{
				["A"] = new Dictionary<string, UsageCount> { ["x"] = new(2, 5) },
			});
			SettingsUsage.MergeExamples(dir, new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
			{
				["A"] = new Dictionary<string, IReadOnlyList<string>> { ["x"] = ["Xyzzy-Haus (de)", "xyzzy (de, en)"] },
			});
			SettingsUsage.Merge(dir, new Dictionary<string, IReadOnlyDictionary<string, UsageCount>>
			{
				["B"] = new Dictionary<string, UsageCount> { ["y"] = new(1, 1) },
			});

			var report = SettingsUsage.Read(dir)!;
			Assert.Equal(new UsageCount(2, 5), report.Of("A", "x"));
			Assert.Equal(["Xyzzy-Haus (de)", "xyzzy (de, en)"], report.ExamplesOf("A", "x"));
			Assert.Empty(report.ExamplesOf("A", "other"));
			Assert.Empty(report.ExamplesOf("B", "y"));
		}
	}
}
