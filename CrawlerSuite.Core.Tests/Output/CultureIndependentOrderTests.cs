using System.Globalization;
using Xunit;

namespace CrawlerSuite.Core.Tests.Output
{
	/// <summary>
	/// Logs sort their rows the same way on every machine. Under Danish rules "aa" counts as a
	/// letter after "z", so a culture-sensitive sort would put it last; the ordinal sort keeps it first.
	/// </summary>
	public class CultureIndependentOrderTests
	{
		[Fact]
		public void IssueTracking_Save_SortsOrdinallyUnderAnyCulture()
		{
			var path = Path.Combine(Path.GetTempPath(), $"order-{Guid.NewGuid():N}.log");
			var saved = CultureInfo.CurrentCulture;
			try
			{
				CultureInfo.CurrentCulture = new CultureInfo("da-DK");
				var records = new List<IssueTracking.IssueRecord>
				{
					new() { Type = "QUALITY", Url = "https://example.test/zeta" },
					new() { Type = "QUALITY", Url = "https://example.test/aaron" },
				};
				IssueTracking.Save(path, records);
			}
			finally
			{
				CultureInfo.CurrentCulture = saved;
			}

			var lines = File.ReadAllLines(path).Skip(1).ToList();
			File.Delete(path);
			Assert.Equal(2, lines.Count);
			Assert.Contains("/aaron", lines[0]);
			Assert.Contains("/zeta", lines[1]);
		}

		[Fact]
		public void DanishCulture_ReallyOrdersDifferently_SoTheTestAboveMeansSomething()
		{
			// Skipped in effect where ICU data is missing (invariant globalization): then both agree.
			var da = new CultureInfo("da-DK").CompareInfo;
			if (CultureInfo.InvariantCulture.CompareInfo.Compare("a", "b") == da.Compare("aa", "z")) return;
			Assert.True(da.Compare("aaron", "zeta") > 0);
			Assert.True(string.CompareOrdinal("aaron", "zeta") < 0);
		}
	}
}
