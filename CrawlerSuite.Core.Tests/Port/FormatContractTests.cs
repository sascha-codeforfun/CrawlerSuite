using System.Reflection;
using System.Text;
using CrawlerSuite.Core.Formats;
using CrawlerSuite.Core.Logging;
using CrawlerSuite.Core.Snapshots;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>
	/// The log format contract: the registry matches the file names the engine writes, log 34
	/// matches its registry entry, and an <c>Oversized</c> row is ignored by every log reader
	/// (index, 404 list, completion, redirects).
	/// </summary>
	[Collection("Logger")]
	public class FormatContractTests : IDisposable
	{
		private readonly string dir;

		public FormatContractTests()
		{
			dir = Path.Combine(Path.GetTempPath(), "cs-format-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			Logger.Initialize(Path.Combine(dir, "test.log"));
			RunUi.Attach(null);
		}

		public void Dispose()
		{
			try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
		}

		[Fact]
		public void Registry_NumbersAreUnique_AndEveryEntryIsDescribed()
		{
			var numbered = OutputFormats.All.Where(f => f.Number.Length > 0).ToList();
			Assert.Equal(numbered.Count, numbered.Select(f => f.Number).Distinct().Count());
			Assert.All(OutputFormats.All, f =>
			{
				Assert.False(string.IsNullOrWhiteSpace(f.Title));
				Assert.False(string.IsNullOrWhiteSpace(f.Purpose));
				Assert.NotEmpty(f.Columns);
			});
		}

		[Fact]
		public void Registry_FileNames_MatchTheNamesTheEngineWrites()
		{
			var names = typeof(LogFileNames)
				.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				.Where(f => f.IsLiteral && f.FieldType == typeof(string))
				.Select(f => (string)f.GetRawConstantValue()!)
				.Where(n => n.Length > 3 && char.IsDigit(n[0]) && char.IsDigit(n[1]) && n[2] == '-')
				.ToList();

			foreach (var name in names)
			{
				var number = name[..2];
				var entry = OutputFormats.ByNumber(number);
				Assert.True(entry is not null, $"No registry entry for {name}");
				var expected = name.EndsWith(".log") || name.EndsWith(".csv") ? name : name + IssueLogWriter.CsvCommaSuffix;
				Assert.Equal(expected, entry!.PrimaryFileName);
			}
		}

		[Fact]
		public void Log34_HeaderMatchesRegistry()
		{
			var crawlerLog = Path.Combine(dir, "01-crawler.log");
			File.WriteAllText(crawlerLog, "");
			var log = Path.Combine(dir, LogFileNames.OversizedFiles);

			OversizedFiles.WriteLog(crawlerLog, Path.Combine(dir, "oversized"), Path.Combine(dir, "download"), "*.html", log, 150L * 1024 * 1024);

			var header = File.ReadAllLines(log, Encoding.UTF8).First().Split('|');
			var expected = OutputFormats.ByNumber("34")!.Columns.Select(c => c.Name).ToArray();
			Assert.Equal(expected, header);
		}

		[Fact]
		public void OversizedMessage_AvoidsEveryWordTheLogReadersMatch()
		{
			var message = CrawlLogMessages.Oversized(150L * 1024 * 1024, "abc.sample") + " " + CrawlLogMessages.OversizedStatus
				+ " " + CrawlLogMessages.Stalled(TimeSpan.FromSeconds(30)) + " " + CrawlLogMessages.NoAnswer(TimeSpan.FromSeconds(100));
			foreach (var word in new[] { "saved", "completed", "crawled", "found" })
			{
				Assert.DoesNotContain(word, message, StringComparison.OrdinalIgnoreCase);
			}
		}

		[Fact]
		public void OversizedRow_IsIgnoredByTheLogReaders()
		{
			var log = Path.Combine(dir, "01-crawler.log");
			const string url = "https://site.test";
			CrawlLogWriter.Write(url, "crawled", "info", log);
			CrawlLogWriter.Write(url + "/a.html", "crawled", "info", log);
			File.AppendAllText(log, $"2026-10-10 10:00:01 | {url}/a.html | saved | aaa.html | discovery{FileFormat.NewLine}");
			CrawlLogWriter.Write(url + "/big.zip", CrawlLogMessages.OversizedStatus, CrawlLogMessages.Oversized(150L * 1024 * 1024, "bbb.sample"), log);
			CrawlLogWriter.Write(url, "completed", "info", log);

			// Index: only the saved page.
			var index = Path.Combine(dir, "02-crawler-index.log");
			CrawlIndex.CreateLookupFile(log, index);
			var entries = File.ReadAllLines(index).Where(l => l.Trim().Length > 0).ToList();
			Assert.Single(entries);
			Assert.DoesNotContain(entries, e => e.Contains("big.zip"));

			// 404 list: none.
			Assert.Empty(CrawlIndex.GetLinesContainingKey(log, "NotFound"));

			// Completion and duration: unaffected.
			var (complete, duration, _) = CrawlLog.Analyse(log, url);
			Assert.True(complete);
			Assert.NotNull(duration);

			// Redirect analysis: no row.
			var redirects = Path.Combine(dir, "03-redirect-analysis.log");
			RedirectAnalyzer.AnalyzeRedirects(log, redirects);
			Assert.DoesNotContain(File.ReadAllLines(redirects), l => l.Contains("big.zip"));
		}

		[Fact]
		public void OutputFiles_UseCrlfAndUtf8Bom()
		{
			var path = Path.Combine(dir, "x.log");
			FileFormat.WriteAllLines(path, ["a", "b"]);
			var bytes = File.ReadAllBytes(path);
			Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', 13, 10, (byte)'b', 13, 10 }, bytes);
		}
	}
}
