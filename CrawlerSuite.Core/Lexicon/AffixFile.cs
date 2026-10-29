namespace CrawlerSuite.Core.Lexicon
{
	using System;
	using System.Collections.Generic;
	using System.IO;

	/// <summary>
	/// How an .aff file is handed to the spell-check library. The file on disk is never changed
	/// (its checksum stays what it is); only the copy read into memory can leave out the
	/// character-variant table.
	///
	/// The MAP table lists characters that are easily confused (e → é è ê ë …). Hunspell uses it
	/// for suggestions only. The app never asks for suggestions, but the library runs one
	/// suggestion test while checking: a compound of three or more parts is rejected when it is a
	/// simple misspelling of a dictionary word. In that test the MAP step tries every
	/// combination of variants, so its work grows with the variants per letter raised to the
	/// length of the word. With a large table (dozens of groups covering nearly every letter) a
	/// single ten-letter word can take minutes. A clock that cut it off would make the verdict
	/// depend on machine load; with the clocks off it runs on. Left out, the
	/// test still tries all other kinds of misspelling (replacement table, wrong, missing, extra
	/// and swapped letters) and every word takes milliseconds. The one thing lost: a compound of
	/// three or more parts that differs from a dictionary word only by such a variant passes.
	/// </summary>
	public static class AffixFile
	{
		/// <summary>
		/// The .aff file's bytes, without its MAP lines when <paramref name="keepCharacterMap"/> is
		/// false. Works on bytes, line by line: the keyword is ASCII at the start of a line in every
		/// encoding an .aff file uses, so the other lines pass through unchanged.
		/// </summary>
		/// <returns>The bytes to load and the number of lines left out.</returns>
		public static (byte[] Bytes, int LeftOut) Read(string path, bool keepCharacterMap)
		{
			var bytes = File.ReadAllBytes(path);
			return keepCharacterMap ? (bytes, 0) : WithoutCharacterMap(bytes);
		}

		/// <summary>Leaves out every line whose first word is MAP (the count line and the groups).</summary>
		public static (byte[] Bytes, int LeftOut) WithoutCharacterMap(byte[] bytes)
		{
			var kept = new List<byte>(bytes.Length);
			int leftOut = 0;
			int start = 0;
			while (start < bytes.Length)
			{
				int end = Array.IndexOf(bytes, (byte)'\n', start);
				int next = end < 0 ? bytes.Length : end + 1;
				if (IsMapLine(bytes.AsSpan(start, next - start), atFileStart: start == 0))
				{
					leftOut++;
				}
				else
				{
					kept.AddRange(bytes.AsSpan(start, next - start).ToArray());
				}

				start = next;
			}

			return (kept.ToArray(), leftOut);
		}

		private static bool IsMapLine(ReadOnlySpan<byte> line, bool atFileStart)
		{
			if (atFileStart && line.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
			{
				line = line[3..]; // UTF-8 byte-order mark
			}

			int i = 0;
			while (i < line.Length && (line[i] == (byte)' ' || line[i] == (byte)'\t'))
			{
				i++;
			}

			line = line[i..];
			return line.Length >= 3
				&& line[0] == (byte)'M' && line[1] == (byte)'A' && line[2] == (byte)'P'
				&& (line.Length == 3 || line[3] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n');
		}
	}
}
