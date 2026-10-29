using System.Text;
using Xunit;

namespace CrawlerSuite.Core.Tests.Output
{
	/// <summary>
	/// Tests verifying that all log writers emit the UTF-8 BOM at file start.
	///
	/// The standard: every log file produced by the crawler must start with the
	/// byte sequence EF BB BF (UTF-8 BOM signature), so that downstream consumers
	/// (PowerQuery, Excel, log readers) decode the file as UTF-8 rather than
	/// applying locale-default decoding.
	///
	/// The encoding policy lives in one place, <see cref="IssueLogWriter.Utf8WithBom"/>,
	/// which the writers of 08-seo-data.log, 09-self-link-analysis.log and
	/// 11-spell-error-sources.log reference.
	/// </summary>
	public class LogWriterBomTests
	{
		private const byte BomByte0 = 0xEF;
		private const byte BomByte1 = 0xBB;
		private const byte BomByte2 = 0xBF;

		private static void AssertFileStartsWithUtf8Bom(string path)
		{
			var bytes = File.ReadAllBytes(path);
			Assert.True(bytes.Length >= 3,
				$"File '{path}' is shorter than 3 bytes — cannot contain a BOM.");
			Assert.Equal(BomByte0, bytes[0]);
			Assert.Equal(BomByte1, bytes[1]);
			Assert.Equal(BomByte2, bytes[2]);
		}

		// ── Central constant ─────────────────────────────────────────────

		[Fact]
		public void Utf8WithBom_HasBomEmissionEnabled()
		{
			Assert.Equal(0xEF, IssueLogWriter.Utf8WithBom.GetPreamble()[0]);
			Assert.Equal(0xBB, IssueLogWriter.Utf8WithBom.GetPreamble()[1]);
			Assert.Equal(0xBF, IssueLogWriter.Utf8WithBom.GetPreamble()[2]);
		}

		// ── StreamingWriter ──────────────────────────────────────────────
		// Backs 11-spell-error-sources.log.

		[Fact]
		public void StreamingWriter_ProducesBomPrefixedFile()
		{
			var tempPath = Path.GetTempFileName();
			try
			{
				using (var writer = IssueLogWriter.OpenStreaming(tempPath, IssueLogWriter.PipeDelimiter, append: false))
				{
					writer.WriteRecord("col1", "col2", "col3");
				}

				AssertFileStartsWithUtf8Bom(tempPath);
			}
			finally
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
		}

		// ── Non-streaming helpers (Encoding.UTF8) ───────────────────────────────
		// Locks the contract — these are the writers behind
		// 10, 16, 17, 18, 19, 20, 21, 22 and any future log added through the
		// same paths.

		[Fact]
		public void Write_PipeDelimited_ProducesBomPrefixedFile()
		{
			var tempPath = Path.GetTempFileName();
			try
			{
				IssueLogWriter.Write(tempPath, IssueLogWriter.PipeDelimiter,
					[["a", "b", "c"]]);

				AssertFileStartsWithUtf8Bom(tempPath);
			}
			finally
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
		}

		[Fact]
		public void Write_SeoDelimited_ProducesBomPrefixedFile()
		{
			var tempPath = Path.GetTempFileName();
			try
			{
				IssueLogWriter.Write(tempPath, IssueLogWriter.SeoDelimiter,
					[["a", "b", "c"]]);

				AssertFileStartsWithUtf8Bom(tempPath);
			}
			finally
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
		}

		[Fact]
		public void Append_ProducesBomPrefixedFile()
		{
			var tempPath = Path.GetTempFileName();
			// Pre-create as empty so Append behaves as expected.
			File.WriteAllText(tempPath, "");
			try
			{
				IssueLogWriter.Append(tempPath, IssueLogWriter.PipeDelimiter, "x", "y");

				AssertFileStartsWithUtf8Bom(tempPath);
			}
			finally
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
		}

		[Fact]
		public void AppendMany_ProducesBomPrefixedFile()
		{
			var tempPath = Path.GetTempFileName();
			File.WriteAllText(tempPath, "");
			try
			{
				IssueLogWriter.AppendMany(tempPath, IssueLogWriter.PipeDelimiter,
					[["a", "b"], ["c", "d"]]);

				AssertFileStartsWithUtf8Bom(tempPath);
			}
			finally
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
		}

		// ── End-to-end shape: file actually parseable as UTF-8 ────────────
		// Sanity: BOM-prefixed file with non-ASCII content round-trips correctly.

		[Fact]
		public void StreamingWriter_NonAsciiContent_RoundtripsAsUtf8()
		{
			var tempPath = Path.GetTempFileName();
			try
			{
				using (var writer = IssueLogWriter.OpenStreaming(tempPath, IssueLogWriter.PipeDelimiter, append: false))
				{
					writer.WriteRecord("Anpassen", "Schließen", "Größe");
				}

				var content = File.ReadAllText(tempPath, Encoding.UTF8);
				Assert.Contains("Anpassen", content);
				Assert.Contains("Schließen", content);
				Assert.Contains("Größe", content);

				AssertFileStartsWithUtf8Bom(tempPath);
			}
			finally
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
		}

		// ── WriteList (Content.Listing → 04-full-content.log) ─────────────
		// A default `new StreamWriter(path)` writes BOM-less UTF-8; 04-full-content.log
		// must carry the BOM like every sibling log.

		[Fact]
		public void WriteList_ProducesBomPrefixedFile()
		{
			var tempPath = Path.GetTempFileName();
			try
			{
				FileIo.WriteList(tempPath, ["alpha", "beta"]);

				AssertFileStartsWithUtf8Bom(tempPath);
			}
			finally
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
		}
	}
}
