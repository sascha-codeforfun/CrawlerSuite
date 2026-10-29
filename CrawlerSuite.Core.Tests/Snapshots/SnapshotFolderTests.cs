using System.Text.RegularExpressions;
using Xunit;
using CrawlerSuite.Core.Snapshots;

namespace CrawlerSuite.Core.Tests.Snapshots
{
	/// <summary>
	/// Tests for SnapshotFolder.NewName and SnapshotFolder.Matches.
	/// These drive snapshot folder naming for the whole crawler — any change
	/// in format silently breaks snapshot history. Lock the contract down.
	/// </summary>
	[Collection("Logger")]
	public class SnapshotFolderTests : IDisposable
	{
		private readonly string _tempDir;

		public SnapshotFolderTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"ts-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"));
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		// ── Matches ───────────────────────────────────────────

		[Theory]
		[InlineData("2026-05-14-04-58-32", true)]
		[InlineData("2024-01-01-00-00-00", true)]
		[InlineData("9999-12-31-23-59-59", true)]
		public void Matches_AcceptsValidFormat(string name, bool expected)
		{
			Assert.Equal(expected, SnapshotFolder.Matches(name));
		}

		[Theory]
		[InlineData("2026-05-14", false)]                  // wrong length
		[InlineData("2026-05-14-04-58-32-extra", false)]   // wrong length
		[InlineData("2026/05/14/04/58/32", false)]         // wrong separators
		[InlineData("2026_05_14_04_58_32", false)]         // wrong separators
		[InlineData("abcd-ef-gh-ij-kl-mn", false)]         // non-digit positions
		[InlineData("2026a05-14-04-58-32", false)]         // dash positions wrong
		[InlineData("", false)]                            // empty
		[InlineData("not-a-timestamp-at-all", false)]      // wrong shape
		public void Matches_RejectsInvalidFormats(string name, bool expected)
		{
			Assert.Equal(expected, SnapshotFolder.Matches(name));
		}

		[Fact]
		public void Matches_RejectsLengthMismatchOf19WithNonDigits()
		{
			// Exactly 19 chars but dashes in the right positions and non-digits
			// elsewhere → should be rejected by the All(char.IsDigit) check.
			Assert.False(SnapshotFolder.Matches("aaaa-aa-aa-aa-aa-aa"));
		}

		// ── NewName ─────────────────────────────────────────────────────

		[Fact]
		public void NewName_IsTheCurrentTime_InTheSnapshotFormat()
		{
			var name = SnapshotFolder.NewName();
			Assert.True(SnapshotFolder.Matches(name));
		}
	}
}
