using System;
using System.IO;
using System.Linq;
using System.Text;
using CrawlerSuite.Core.Lexicon;
using Xunit;

namespace CrawlerSuite.Core.Tests.Lexicon
{
	/// <summary>The character-variant table (MAP) is left out of the .aff as loaded; nothing else changes.</summary>
	public class AffixFileTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), "affixfile-" + Guid.NewGuid().ToString("N"));

		public AffixFileTests() => Directory.CreateDirectory(dir);

		public void Dispose()
		{
			try
			{
				Directory.Delete(dir, recursive: true);
			}
			catch (IOException)
			{
			}
		}

		[Fact]
		public void WithoutCharacterMap_DropsOnlyTheMapLines()
		{
			const string aff = "SET UTF-8\r\nTRY abc\r\nMAP 2\r\nMAP aáà\r\n  MAP eéè\r\nMAPPING stays\r\nREP 1\r\nREP a e\r\nMAP";
			var (bytes, leftOut) = AffixFile.WithoutCharacterMap(Encoding.UTF8.GetBytes(aff));
			Assert.Equal(4, leftOut);
			Assert.Equal("SET UTF-8\r\nTRY abc\r\nMAPPING stays\r\nREP 1\r\nREP a e\r\n", Encoding.UTF8.GetString(bytes));
		}

		[Fact]
		public void WithoutCharacterMap_KeepsOtherBytesAsTheyAre()
		{
			// A single-byte encoding: the bytes outside MAP lines pass through untouched.
			byte[] latin = [.. Encoding.Latin1.GetBytes("SET ISO8859-1\nTRY "), 0xE4, 0xF6, 0xFC, .. Encoding.Latin1.GetBytes("\nMAP 1\nMAP a"), 0xE4, (byte)'\n'];
			var (bytes, leftOut) = AffixFile.WithoutCharacterMap(latin);
			Assert.Equal(2, leftOut);
			Assert.Equal([.. Encoding.Latin1.GetBytes("SET ISO8859-1\nTRY "), 0xE4, 0xF6, 0xFC, (byte)'\n'], bytes);
		}

		[Fact]
		public void WithoutCharacterMap_ReadsAMapLineAfterAByteOrderMark()
		{
			byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes("MAP 1\nSET UTF-8\n")];
			var (bytes, leftOut) = AffixFile.WithoutCharacterMap(withBom);
			Assert.Equal(1, leftOut);
			Assert.Equal("SET UTF-8\n", Encoding.ASCII.GetString(bytes));
		}

		private (string Dic, string Aff) Write(string aff, params string[] words)
		{
			var dic = Path.Combine(dir, "xx.dic");
			var affPath = Path.Combine(dir, "xx.aff");
			File.WriteAllText(dic, words.Length + "\n" + string.Join("\n", words) + "\n", new UTF8Encoding(false));
			File.WriteAllText(affPath, aff, new UTF8Encoding(false));
			return (dic, affPath);
		}

		[Fact]
		public void Loader_LeavesTheMapOut_UnlessTheLeverKeepsIt_AndTheFileIsUnchanged()
		{
			var (dic, aff) = Write("SET UTF-8\nTRY mn\nMAP 2\nMAP aáà\nMAP eéè\nCOMPOUNDFLAG X\nCOMPOUNDMIN 3\n", "haus/X", "garten/X", "zaun/X", "hausgartenzaum");
			var before = File.ReadAllBytes(aff);

			var left = Loader.Load(dic, aff, "", "", "", silent: true).System;
			var kept = Loader.Load(dic, aff, "", "", "", silent: true, keepCharacterMap: true).System;

			Assert.True(left.Affix.RelatedCharacterMap.IsEmpty);
			Assert.Equal(2, kept.Affix.RelatedCharacterMap.Count);
			Assert.Equal(before, File.ReadAllBytes(aff));

			// Everything but the variants still decides: the misspelt three-part compound is
			// rejected (one letter away from a dictionary word), the right one accepted.
			var options = CheckLevers.Default.ToOptions();
			foreach (var list in new[] { left, kept })
			{
				Assert.False(list.Check("hausgartenzaun", options));
				Assert.True(list.Check("hausgartenzaum", options));
				Assert.True(list.Check("gartenhaus", options));
			}
		}

		[Fact]
		public void Levers_LeaveTheMapOutByDefault_AndNameIt()
		{
			Assert.False(CheckLevers.Default.CharacterMap);
			Assert.Contains("character variants (MAP) left out", CheckLevers.Default.Describe());
			Assert.Contains("character variants (MAP) kept", (CheckLevers.Default with { CharacterMap = true }).Describe());
			Assert.False((CheckLevers.Default with { CharacterMap = true }).IsDefault);
		}
	}
}
