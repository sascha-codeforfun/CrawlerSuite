using CrawlerSuite.Core.Host;
using Xunit;

namespace CrawlerSuite.Core.Tests.Host
{
	/// <summary>The work figures are counted, add up across threads, and read as written.</summary>
	public class WorkCountersTests
	{
		[Fact]
		public void Mark_AddsUpEveryThread()
		{
			var tally = new WorkTally(); // its own tally: other tests count into the run's at the same time
			System.Threading.Tasks.Parallel.For(0, 1000, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
			{
				tally.Parsed();
				tally.Words(3);
				tally.Lookup(remembered: i % 2 == 0);
			});

			var work = tally.Mark();
			Assert.Equal(1000, work.Parses);
			Assert.Equal(3000, work.Words);
			Assert.Equal(500, work.Lookups);
			Assert.Equal(500, work.Remembered);
			Assert.True(new WorkTally().Mark().IsEmpty);
		}

		[Fact]
		public void Since_IsTheDifference()
		{
			var start = new StepWork(1, 100, 2, 3, 4, 5, 6);
			var end = new StepWork(3, 400, 2, 13, 24, 35, 46);
			Assert.Equal(new StepWork(2, 300, 0, 10, 20, 30, 40), end.Since(start));
		}

		[Fact]
		public void Compact_ShowsOnlyWhatTheStepDid()
		{
			Assert.Equal(string.Empty, WorkText.Compact(null));
			Assert.Equal(string.Empty, WorkText.Compact(StepWork.None));
			Assert.Equal("1,978 files · 120 MB · 1,978 parses",
				WorkText.Compact(new StepWork(1978, 120L << 20, 1978, 0, 0, 0, 0)));
			Assert.Equal("1 text run · 4.2 M words · 4.1 M lookups",
				WorkText.Compact(new StepWork(0, 0, 0, 1, 4_200_000, 100_000, 4_000_000)));
		}

		[Fact]
		public void Summary_ReadsAsOneLine()
		{
			Assert.Equal(
				"Read 1.5 GB in 15,512 files · 12,034 page parses · 2.1 M text runs · 6.0 M words checked · 9.1 M dictionary lookups (1.2 M asked of the dictionaries, 7.9 M answered from memory)",
				WorkText.Summary(new StepWork(15_512, 3L << 29, 12_034, 2_100_000, 6_000_000, 1_200_000, 7_900_000)));
		}

		[Theory]
		[InlineData(0L, "0 KB")]
		[InlineData(600L, "1 KB")]
		[InlineData(1L << 20, "1 MB")]
		[InlineData(5L << 30, "5.0 GB")]
		public void Bytes_PicksTheUnit(long bytes, string expected)
		{
			Assert.Equal(expected, WorkText.Bytes(bytes));
		}
	}
}
