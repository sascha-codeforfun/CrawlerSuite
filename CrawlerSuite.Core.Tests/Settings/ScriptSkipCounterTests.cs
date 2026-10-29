using CrawlerSuite.Core.Settings;
using CrawlerSuite.Core.SpellCheck;
using Xunit;

namespace CrawlerSuite.Core.Tests.Settings
{
	/// <summary>Usage of the script strings to skip: only real skips count, switched-off entries too.</summary>
	public class ScriptSkipCounterTests
	{
		private static void See(ScriptSkipScope scope, string literal, IReadOnlySet<string> active) =>
			scope.Observe(literal, ValueClassifier.ClassifyScriptLiteral(literal, active));

		[Fact]
		public void CountsRealSkips_PerSource_CaseInsensitive()
		{
			var active = new HashSet<string>(["Examplix", "keydown"], StringComparer.OrdinalIgnoreCase);
			var counter = new ScriptSkipCounter(active);
			See(counter.For("a.html"), "Examplix", active);
			See(counter.For("a.html"), " examplix ", active);
			See(counter.For("b.html"), "Examplix", active);
			See(counter.For("b.html"), "keydown", active); // the code vocabulary skips it anyway
			See(counter.For("b.html"), "Examplix ist hier", active); // whole literal only

			var counts = counter.Counts();
			Assert.Equal(new UsageCount(2, 3), counts["Examplix"]);
			Assert.Equal(new UsageCount(0, 0), counts["keydown"]);
		}

		[Fact]
		public void SwitchedOffEntry_CountsWhatItWouldSkip_WithoutSkipping()
		{
			var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var counter = new ScriptSkipCounter([], ["Gletscherwissen"]);
			var verdict = ValueClassifier.ClassifyScriptLiteral("Gletscherwissen", active);
			Assert.True(verdict.ShouldCheck); // still checked
			counter.For("p.html").Observe("Gletscherwissen", verdict);
			Assert.Equal(new UsageCount(1, 1), counter.Counts()["Gletscherwissen"]);
		}

		[Fact]
		public void SwitchableList_ReadsSwitchedOffEntries()
		{
			var options = System.Text.Json.JsonSerializer.Deserialize<JavaScriptSpellCheckOptions>(
				"{ \"TokensToFilter\": [ \"alpha\", { \"Value\": \"beta\", \"Enabled\": false } ] }")!;
			Assert.Equal(new[] { "alpha" }, options.TokensToFilter);
			Assert.Equal(new[] { "beta" }, options.TokensToFilter.Disabled);
		}
	}
}
