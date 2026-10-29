using System.Text;
using CrawlerSuite.Core.Display;
using CrawlerSuite.Core.Lexicon;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests
{
	/// <summary>
	/// No result may depend on how much time the machine gives the app. These pin the places where
	/// a clock could decide: the dictionary's compound check and its suggestion test, and the screen masking.
	/// </summary>
	public class NoClockTests
	{
		// A tiny compounding dictionary with invented generic parts.
		private static WordList CompoundList()
		{
			const string aff = "SET UTF-8\nCOMPOUNDFLAG X\nCOMPOUNDMIN 2\n";
			string[] parts = ["kalt", "hitze", "beständig", "wasser", "fest", "haus", "garten", "zaun", "pfahl", "stein", "mauer"];
			var dic = parts.Length + "\n" + string.Join("\n", parts.Select(p => p + "/X")) + "\n";
			return WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes(dic)),
				new MemoryStream(Encoding.UTF8.GetBytes(aff)));
		}

		private const string LongCompound = "hausgartenzaunpfahlsteinmauer";

		[Fact]
		public void Library_WithAnExpiredClock_RejectsAValidCompound()
		{
			// The hazard itself: when the time limit has run out, a valid compound comes back unknown.
			var list = CompoundList();
			Assert.True(list.Check(LongCompound));
			Assert.False(list.Check(LongCompound, new QueryOptions { TimeLimitCompoundCheck = TimeSpan.Zero }));
		}

		// Three parts make a compound; a dictionary word one letter away makes it a misspelling.
		private static WordList MisspeltCompoundList()
		{
			const string aff = "SET UTF-8\nTRY mn\nCOMPOUNDFLAG X\nCOMPOUNDMIN 3\n";
			string[] words = ["haus/X", "garten/X", "zaun/X", "hausgartenzaum"];
			var dic = words.Length + "\n" + string.Join("\n", words) + "\n";
			return WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes(dic)),
				new MemoryStream(Encoding.UTF8.GetBytes(aff)));
		}

		[Fact]
		public void Library_WithAnExpiredSuggestionClock_AcceptsAMisspeltCompound()
		{
			// The second hazard: a compound of three or more parts is rejected when it is a simple
			// misspelling of a dictionary word. That test has its own clock; when it has run out, the
			// test finds nothing and the misspelling passes.
			var list = MisspeltCompoundList();
			var noClock = CheckLevers.Default.ToOptions();
			Assert.False(list.Check("hausgartenzaun", noClock));
			Assert.True(list.Check("hausgartenzaun", new QueryOptions
			{
				TimeLimitCompoundCheck = Timeout.InfiniteTimeSpan,
				TimeLimitCompoundSuggest = TimeSpan.Zero,
				TimeLimitSuggestStep = Timeout.InfiniteTimeSpan,
				TimeLimitSuggestGlobal = Timeout.InfiniteTimeSpan,
			}));
			Assert.False(new Bundle { System = list }.Check("hausgartenzaun"));
			Assert.True(new Bundle { System = list }.Check("hausgartenzaum"));
		}

		[Fact]
		public void Bundle_ChecksWithoutTimeLimit()
		{
			// Negative = timer off. The default levers switch off all four of the library's clocks.
			var options = CheckLevers.Default.ToOptions();
			Assert.True(options.TimeLimitCompoundCheck < TimeSpan.Zero);
			Assert.True(options.TimeLimitCompoundSuggest < TimeSpan.Zero);
			Assert.True(options.TimeLimitSuggestStep < TimeSpan.Zero);
			Assert.True(options.TimeLimitSuggestGlobal < TimeSpan.Zero);
			Assert.True(Bundle.CheckOptions.TimeLimitCompoundCheck < TimeSpan.Zero);
			Assert.True(Bundle.CheckOptions.TimeLimitCompoundSuggest < TimeSpan.Zero);
			Assert.True(Bundle.CheckOptions.TimeLimitSuggestStep < TimeSpan.Zero);
			Assert.True(Bundle.CheckOptions.TimeLimitSuggestGlobal < TimeSpan.Zero);
			var bundle = new Bundle { System = CompoundList() };
			Assert.True(bundle.Check(LongCompound));
			Assert.True(bundle.Check("hitzebeständig"));
			Assert.False(bundle.Check("hitzebestandig"));
		}

		[Fact]
		public void Bundle_VerdictsUnderFullCpuLoad_AreUnchanged()
		{
			// Every core busy while long compounds are checked by fresh bundles (nothing remembered).
			using var stop = new CancellationTokenSource();
			var burners = Enumerable.Range(0, Environment.ProcessorCount * 2)
				.Select(_ => Task.Run(() =>
				{
					long x = 0;
					while (!stop.IsCancellationRequested)
					{
						x++;
					}

					return x;
				}))
				.ToArray();
			try
			{
				var list = CompoundList();
				for (int i = 0; i < 200; i++)
				{
					var bundle = new Bundle { System = list };
					Assert.True(bundle.Check(LongCompound));
				}
			}
			finally
			{
				stop.Cancel();
				Task.WaitAll(burners);
			}
		}

		[Fact]
		public void LongestRealGermanWord_IsAcceptedWithoutClock_AndATypoIsNot()
		{
			// The proverbial longest German word (63 letters, a former statute's name), built from its
			// six parts. With an expired clock the library gives up and calls it unknown; the bundle
			// asks without a clock and gets the real answer. The word stays under the 100-character
			// rule, which only ever rejects strings that are not real words.
			const string aff = "SET UTF-8\nCOMPOUNDFLAG X\nCOMPOUNDMIN 3\n";
			string[] parts = ["Rindfleisch", "etikettierungs", "überwachungs", "aufgaben", "übertragungs", "gesetz"];
			var dic = parts.Length + "\n" + string.Join("\n", parts.Select(p => p + "/X")) + "\n";
			var list = WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes(dic)),
				new MemoryStream(Encoding.UTF8.GetBytes(aff)));
			const string word = "Rindfleischetikettierungsüberwachungsaufgabenübertragungsgesetz";

			Assert.Equal(63, word.Length);
			Assert.False(list.Check(word, new QueryOptions { TimeLimitCompoundCheck = TimeSpan.Zero }));

			var bundle = new Bundle { System = list };
			Assert.True(bundle.Check(word));
			Assert.False(bundle.Check("Rindfleischetikettierungsüberwachungsaufgabenübertragungsgesez"));
		}

		[Fact]
		public void WordCheckStats_KeepsTheSlowestThree_AndResets()
		{
			Bundle.ResetRunState();
			foreach (var (ticks, len) in new[] { (5L, 1), (1L, 2), (9L, 3), (3L, 4), (7L, 5) })
			{
				WordCheckStats.Record(ticks, len, "xx");
			}

			Assert.Equal(new[] { 3, 5, 1 }, WordCheckStats.SlowestChecks().Select(c => c.Length));
			Assert.Equal(5, WordCheckStats.Checks);

			Bundle.ResetRunState();
			Assert.Empty(WordCheckStats.SlowestChecks());
			Assert.Equal(0, WordCheckStats.Checks);
		}

		[Fact]
		public void Bundle_RecordsItsLanguageInTheStats()
		{
			Bundle.ResetRunState();
			var bundle = new Bundle { System = CompoundList(), Language = "xx" };
			bundle.Check(LongCompound);
			Assert.Contains(WordCheckStats.SlowestChecks(), c => c.Language == "xx" && c.Length == LongCompound.Length);
		}

		[Theory]
		[InlineData("a")]
		[InlineData("a=")]
		[InlineData("C:\\a b")]
		[InlineData("x.")]
		[InlineData("a-")]
		[InlineData("http://a")]
		public void Masking_LongRepetitiveInput_FinishesAndMasks(string unit)
		{
			// Linear-time patterns: a large repetitive cell is masked in one pass, no clock needed.
			var mask = new ScreenMask();
			mask.Learn(new Config { Sites = [new SiteConfig { Name = "S", Url = "https://a.example.test/", IsPrimary = true }] }, null);
			var text = string.Concat(Enumerable.Repeat(unit, 20_000)) + " see a.example.test now";
			var masked = mask.Apply(text, MaskLevel.SchemeOnly);
			Assert.DoesNotContain("a.example.test", masked);
			Assert.EndsWith(" now", masked);
		}
	}
}
