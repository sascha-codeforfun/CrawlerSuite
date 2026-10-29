namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Text;
	using System.Text.RegularExpressions;

	/// <summary>
	/// An embedded font program as the PDF comparison sees it: the random subset tag a generator
	/// gives a font subset ("ABCDEF+") set aside wherever the program carries it, and in a TrueType
	/// or OpenType program the checksums that follow from it. A PDF written out again then carries
	/// the same font when its glyphs are the same. Glyphs, metrics and every other table still count.
	/// </summary>
	internal static class FontSubsets
	{
		// In a dictionary or a Type 1 / CFF program: the tag stands on its own before the font's name.
		private static readonly Regex Tag = new(@"(?<![A-Za-z0-9])[A-Z]{6}\+(?=[A-Za-z0-9])", RegexOptions.Compiled);

		// In a TrueType name table the strings stand back to back, so the tag may follow a letter; one byte or two (UTF-16BE) per sign.
		private static readonly Regex NameTag = new(@"[A-Z]{6}\+(?=[A-Za-z0-9])|(?:\x00[A-Z]){6}\x00\+(?=\x00[A-Za-z0-9])", RegexOptions.Compiled);

		private const string Neutral = "AAAAAA+";

		private const string WideNeutral = "\0A\0A\0A\0A\0A\0A\0+";

		/// <summary>The font program with its subset tags and the checksums that depend on them neutralised; the same length.</summary>
		internal static byte[] Neutralise(byte[] data)
		{
			if (IsSfnt(data) && Sfnt(data) is { } neutral)
			{
				return neutral;
			}

			if (data.AsSpan().IndexOf((byte)'+') < 0)
			{
				return data;
			}

			return Encoding.Latin1.GetBytes(Tag.Replace(Encoding.Latin1.GetString(data), Neutral));
		}

		/// <summary>A TrueType (0x00010000, "true") or OpenType ("OTTO") program.</summary>
		private static bool IsSfnt(byte[] data) =>
			data.Length >= 12
			&& ((data[0] == 0 && data[1] == 1 && data[2] == 0 && data[3] == 0)
				|| (data[0] == 't' && data[1] == 'r' && data[2] == 'u' && data[3] == 'e')
				|| (data[0] == 'O' && data[1] == 'T' && data[2] == 'T' && data[3] == 'O'));

		/// <summary>
		/// The program with the tags in its name table neutralised, every table checksum in the
		/// table directory and head's checkSumAdjustment zeroed; null when the directory does not
		/// read (the program is then compared as it is, tags aside).
		/// </summary>
		private static byte[]? Sfnt(byte[] data)
		{
			int tables = (data[4] << 8) | data[5];
			if (12 + (16 * tables) > data.Length)
			{
				return null;
			}

			var copy = (byte[])data.Clone();
			for (int i = 0; i < tables; i++)
			{
				int entry = 12 + (16 * i);
				string tag = Encoding.ASCII.GetString(copy, entry, 4);
				long offset = ReadUInt32(copy, entry + 8);
				long length = ReadUInt32(copy, entry + 12);
				copy.AsSpan(entry + 4, 4).Clear(); // the table's checksum
				if (offset + length > copy.Length)
				{
					continue;
				}

				if (tag == "head" && length >= 12)
				{
					copy.AsSpan((int)offset + 8, 4).Clear(); // checkSumAdjustment
				}
				else if (tag == "name")
				{
					var text = Encoding.Latin1.GetString(copy, (int)offset, (int)length);
					var replaced = NameTag.Replace(text, m => m.Value[0] == '\0' ? WideNeutral : Neutral);
					Encoding.Latin1.GetBytes(replaced).CopyTo(copy, (int)offset);
				}
			}

			return copy;
		}

		private static long ReadUInt32(byte[] data, int at) =>
			((long)data[at] << 24) | ((long)data[at + 1] << 16) | ((long)data[at + 2] << 8) | data[at + 3];
	}
}
