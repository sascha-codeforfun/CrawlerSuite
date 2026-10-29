using System.Text;
using System.Text.Json;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.Settings;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests.Lexicon
{
	/// <summary>The test levers: all clocks off by default, the library's limits when on, never saved.</summary>
	public class CheckLeversTests
	{
		[Fact]
		public void Default_IsAllClocksOff_AndNoTurns()
		{
			var levers = CheckLevers.Default;
			Assert.False(levers.CompoundCheckClock || levers.CompoundSuggestClock || levers.SuggestStepClock || levers.SuggestGlobalClock);
			Assert.Equal(TakeTurns.None, levers.TakeTurns);
			Assert.True(levers.IsDefault);
			Assert.EndsWith("(defaults)", levers.Describe());
		}

		[Fact]
		public void ClocksOn_UseTheLibrarysOwnLimits()
		{
			var library = new QueryOptions();
			var options = new CheckLevers(true, true, true, true, TakeTurns.None).ToOptions();
			Assert.Equal(library.TimeLimitCompoundCheck, options.TimeLimitCompoundCheck);
			Assert.Equal(library.TimeLimitCompoundSuggest, options.TimeLimitCompoundSuggest);
			Assert.Equal(library.TimeLimitSuggestStep, options.TimeLimitSuggestStep);
			Assert.Equal(library.TimeLimitSuggestGlobal, options.TimeLimitSuggestGlobal);
		}

		[Fact]
		public void Describe_NamesEveryLever()
		{
			var text = new CheckLevers(false, true, false, false, TakeTurns.WholeProgram).Describe();
			Assert.Contains("compound check clock off", text);
			Assert.Contains($"compound suggestion clock {(int)new QueryOptions().TimeLimitCompoundSuggest.TotalMilliseconds} ms", text);
			Assert.Contains("suggestion step clock off", text);
			Assert.Contains("overall suggestion clock off", text);
			Assert.Contains("take turns: whole program", text);
			Assert.EndsWith("(changed for this session)", text);
		}

		[Fact]
		public void RunState_WithoutLevers_UsesTheDefaults()
		{
			Bundle.ResetRunState();
			Assert.True(Bundle.ActiveLevers.IsDefault);
		}

		[Fact]
		public void Levers_AreNotSettings_AndNeverReachTheConfiguration()
		{
			string[] names = ["Clock", "TakeTurns", "Lever"];
			Assert.DoesNotContain(SettingsCatalog.All, s => names.Any(n => s.Path.Contains(n, StringComparison.OrdinalIgnoreCase)));
			var json = JsonSerializer.Serialize(new Config());
			Assert.DoesNotContain(names, n => json.Contains(n, StringComparison.OrdinalIgnoreCase));
		}

		[Theory]
		[InlineData(TakeTurns.PerLanguage)]
		[InlineData(TakeTurns.WholeProgram)]
		public void TakingTurns_GivesTheSameVerdicts(TakeTurns turns)
		{
			// Only the turn-taking changes (the clocks stay off), so other tests running meanwhile
			// are not affected.
			const string aff = "SET UTF-8\nCOMPOUNDFLAG X\nCOMPOUNDMIN 2\n";
			string[] parts = ["kalt", "hitze", "beständig", "wasser", "fest", "haus", "garten", "zaun"];
			var dic = parts.Length + "\n" + string.Join("\n", parts.Select(p => p + "/X")) + "\n";
			var list = WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes(dic)),
				new MemoryStream(Encoding.UTF8.GetBytes(aff)));
			try
			{
				Bundle.ResetRunState(CheckLevers.Default with { TakeTurns = turns });
				Assert.Equal(turns, Bundle.ActiveLevers.TakeTurns);
				var results = new bool[200];
				Parallel.For(0, results.Length, i =>
				{
					var bundle = new Bundle { System = list, Language = "xx" };
					results[i] = bundle.Check(i % 2 == 0 ? "hausgartenzaun" : "hitzebestandig");
				});
				Assert.All(results.Where((_, i) => i % 2 == 0), Assert.True);
				Assert.All(results.Where((_, i) => i % 2 == 1), Assert.False);
			}
			finally
			{
				Bundle.ResetRunState();
			}
		}
	}
}
