using System.Globalization;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Logging;
using CrawlerSuite.Core.Snapshots;
using Xunit;

namespace CrawlerSuite.Core.Tests.Logging
{
	/// <summary>
	/// Dates are written the same on every machine. A Thai setting uses the Buddhist calendar
	/// (year + 543); some settings use "." as the time separator. Neither may reach the logs,
	/// which the app reads back with a fixed format.
	/// </summary>
	public class DatesInAnyCultureTests
	{
		private static readonly Regex Stamp = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \|", RegexOptions.CultureInvariant);

		private static string WriteUnder(string culture, Action<string> write)
		{
			var path = Path.Combine(Path.GetTempPath(), $"dates-{Guid.NewGuid():N}.log");
			var saved = CultureInfo.CurrentCulture;
			try
			{
				CultureInfo.CurrentCulture = new CultureInfo(culture);
				write(path);
			}
			finally
			{
				CultureInfo.CurrentCulture = saved;
			}
			var line = File.ReadAllLines(path)[0];
			File.Delete(path);
			return line;
		}

		[Theory]
		[InlineData("th-TH")]
		[InlineData("fi-FI")]
		[InlineData("ar-SA")]
		public void CrawlLog_Row_HasWesternYearAndColons(string culture)
		{
			var line = WriteUnder(culture, p => CrawlLogWriter.Write("https://example.test/", "200", "crawled", p));
			Assert.Matches(Stamp, line);
			var parsed = CrawlLog.ParseTimestamp(line);
			Assert.NotNull(parsed);
			Assert.InRange(parsed!.Value.Year, DateTime.Now.Year - 1, DateTime.Now.Year + 1);
		}

		[Fact]
		public void CrawlLog_SavedRow_HasWesternYearAndColons()
		{
			var line = WriteUnder("th-TH", p => CrawlLogWriter.WriteSaved("https://example.test/a.html", "a.html", p, "discovery", "text/html"));
			Assert.Matches(Stamp, line);
		}

		[Fact]
		public void SnapshotFolderName_HasWesternYear()
		{
			var saved = CultureInfo.CurrentCulture;
			string name;
			try
			{
				CultureInfo.CurrentCulture = new CultureInfo("th-TH");
				name = SnapshotFolder.NewName();
			}
			finally
			{
				CultureInfo.CurrentCulture = saved;
			}
			Assert.Matches(@"^\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}$", name);
			Assert.InRange(int.Parse(name[..4], CultureInfo.InvariantCulture), DateTime.Now.Year - 1, DateTime.Now.Year + 1);
		}
	}
}
