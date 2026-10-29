using System.Text;
using Xunit;

namespace CrawlerSuite.Core.Tests.Links
{
	/// <summary>
	/// Tests for RedirectAnalyzer.AnalyzeRedirects — parses a pipe-delimited
	/// crawl log, follows "Found"/"MovedPermanently" chains up to depth 3, and
	/// writes one output line per redirecting record.
	///
	/// The analyzer is file-in / file-out, so each test writes a temporary log,
	/// runs the analyzer, and reads back the output. The temp directory is
	/// cleaned up in Dispose. Placed in the Logger collection because the
	/// analyzer routes some failures through the static Logger.
	/// </summary>
	[Collection("Logger")]
	public class RedirectAnalyzerTests : IDisposable
	{
		private readonly string _tempDir;

		public RedirectAnalyzerTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"redirect-analyzer-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		private (string logPath, string outPath) WriteLog(params string[] lines)
		{
			var logPath = Path.Combine(_tempDir, $"in-{Guid.NewGuid():N}.log");
			var outPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.log");
			File.WriteAllLines(logPath, lines, Encoding.UTF8);
			return (logPath, outPath);
		}

		private string[] RunAndRead(params string[] inputLines)
		{
			var (logPath, outPath) = WriteLog(inputLines);
			RedirectAnalyzer.AnalyzeRedirects(logPath, outPath);
			return File.Exists(outPath)
				? File.ReadAllLines(outPath, Encoding.UTF8)
				: [];
		}

		// ── Basic single redirect ─────────────────────────────────────────────

		[Fact]
		public void SingleRedirect_EmitsOneLine_WithSourceAndTarget()
		{
			var output = RunAndRead(
				"2026-05-23 | https://other.test/old | Found | https://other.test/new",
				"2026-05-23 | https://other.test/new | OK | -");

			Assert.Single(output);
			Assert.Contains("https://other.test/old", output[0]);
			Assert.Contains("https://other.test/new", output[0]);
		}

		[Fact]
		public void MovedPermanently_IsTreatedAsRedirect()
		{
			var output = RunAndRead(
				"2026-05-23 | https://other.test/old | MovedPermanently | https://other.test/new",
				"2026-05-23 | https://other.test/new | OK | -");

			Assert.Single(output);
			Assert.Contains("MovedPermanently", output[0]);
		}

		[Fact]
		public void NonRedirectStatus_ProducesNoOutput()
		{
			var output = RunAndRead(
				"2026-05-23 | https://other.test/page | OK | -",
				"2026-05-23 | https://other.test/other | NotFound | -");

			Assert.Empty(output);
		}

		// ── Chain following ───────────────────────────────────────────────────

		[Fact]
		public void TwoHopChain_FollowsBothHops()
		{
			var output = RunAndRead(
				"2026-05-23 | https://other.test/a | Found | https://other.test/b",
				"2026-05-23 | https://other.test/b | Found | https://other.test/c",
				"2026-05-23 | https://other.test/c | OK | -");

			// The record for /a should show the full chain a -> b -> c.
			var lineForA = output.Single(l => l.StartsWith("https://other.test/a"));
			Assert.Contains("https://other.test/b", lineForA);
			Assert.Contains("https://other.test/c", lineForA);
		}

		[Fact]
		public void RedirectLoop_IsDetectedAndLabelled()
		{
			var output = RunAndRead(
				"2026-05-23 | https://other.test/a | Found | https://other.test/b",
				"2026-05-23 | https://other.test/b | Found | https://other.test/a");

			Assert.Contains(output, l => l.Contains("(Loop)"));
		}

		[Fact]
		public void RedirectTargetNotInLog_IsLabelledNotFound()
		{
			var output = RunAndRead(
				"2026-05-23 | https://other.test/a | Found | https://other.test/missing");

			var line = output.Single();
			Assert.Contains("(NotFound)", line);
		}

		[Fact]
		public void ChainDepth_IsCappedAtThree()
		{
			// a -> b -> c -> d -> e ; depth cap is 3 hops so 'e' is never reached.
			var output = RunAndRead(
				"2026-05-23 | https://other.test/a | Found | https://other.test/b",
				"2026-05-23 | https://other.test/b | Found | https://other.test/c",
				"2026-05-23 | https://other.test/c | Found | https://other.test/d",
				"2026-05-23 | https://other.test/d | Found | https://other.test/e",
				"2026-05-23 | https://other.test/e | OK | -");

			var lineForA = output.Single(l => l.StartsWith("https://other.test/a"));
			Assert.DoesNotContain("https://other.test/e", lineForA);
		}

		// ── Normalization & parsing edge cases ────────────────────────────────

		[Fact]
		public void UrlNormalization_IgnoresQueryAndTrailingSlash()
		{
			// Target carries a query string and trailing slash; the matching
			// record is stored without them — normalization must still link them.
			var output = RunAndRead(
				"2026-05-23 | https://other.test/a | Found | https://other.test/b/?utm=1",
				"2026-05-23 | https://other.test/b | OK | -");

			var line = output.Single();
			Assert.DoesNotContain("(NotFound)", line);
		}

		[Fact]
		public void IgnoredVariableInfo_SkipsRecord()
		{
			// "Response status code does not indicate success" is the ignored marker.
			var output = RunAndRead(
				"2026-05-23 | https://other.test/a | Found | Response status code does not indicate success: 404 (Not Found)");

			Assert.Empty(output);
		}

		[Fact]
		public void BlankAndMalformedLines_AreSkipped()
		{
			var output = RunAndRead(
				"",
				"   ",
				"only | three | parts",
				"2026-05-23 | https://other.test/a | Found | https://other.test/b",
				"2026-05-23 | https://other.test/b | OK | -");

			Assert.Single(output);
		}

		[Fact]
		public void EmptyInputFile_ProducesEmptyOutput()
		{
			var output = RunAndRead();
			Assert.Empty(output);
		}
	}
}
