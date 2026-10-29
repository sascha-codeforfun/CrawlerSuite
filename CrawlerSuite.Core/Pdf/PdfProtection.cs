using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CrawlerSuite.Core.Pdf
{
	/// <summary>How a PDF is protected, as far as it concerns its readers.</summary>
	public enum PdfProtectionKind
	{
		/// <summary>Not encrypted.</summary>
		None,

		/// <summary>Opens without a password; restrictions (copying, printing …) are set, extraction for accessibility is allowed.</summary>
		Restricted,

		/// <summary>Opens without a password, but extraction for accessibility is not allowed: a screen reader is locked out (PDF/UA-1, 7.16).</summary>
		AccessibilityBlocked,

		/// <summary>Needs a password (or a certificate) to open: nobody can read it without one.</summary>
		PasswordToOpen,
	}

	/// <summary>
	/// Reads a PDF's encryption dictionary — without a PDF library — to tell what its protection
	/// does to readers. A restriction protects nothing (the file opens for everyone; honouring it is
	/// up to the viewer), so what counts is its side effect: whether assistive technology may still
	/// extract the content. Whether a password is needed to open the file is found by trying the
	/// empty one, as every viewer does (standard security handler, revisions 2 to 6).
	/// </summary>
	public static class PdfProtection
	{
		// Permission bits (1-based, PDF 32000-1 table 22).
		private const int CopyBit = 1 << 4; // bit 5: copy or extract text and graphics
		private const int AccessibilityBit = 1 << 9; // bit 10: extract for accessibility (revision 3 and later)

		private static readonly byte[] Padding =
		[
			0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
			0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
		];

		/// <summary>The protection of a PDF read as Latin-1 (<paramref name="latin1"/>; with its object streams unpacked, where an encryption dictionary may not be).</summary>
		public static PdfProtectionKind Of(string latin1)
		{
			var dict = EncryptDictionary(latin1);
			if (dict is null)
			{
				return PdfProtectionKind.None;
			}

			// Another security handler (certificates: /Adobe.PubSec) opens only with its key.
			if (!Regex.IsMatch(dict, @"/Filter\s*/Standard\b"))
			{
				return PdfProtectionKind.PasswordToOpen;
			}

			int revision = Integer(dict, "/R") ?? 2;
			if (!OpensWithoutPassword(dict, revision, FirstId(latin1)))
			{
				return PdfProtectionKind.PasswordToOpen;
			}

			// PDF 2.0 deprecates the accessibility bit: readers treat it as set.
			bool pdf20 = latin1.StartsWith("%PDF-2.", StringComparison.Ordinal) || Regex.IsMatch(latin1, @"/Version\s*/2\.\d");
			int permissions = Integer(dict, "/P") ?? -1;
			bool accessible = pdf20 || (revision <= 2 ? (permissions & CopyBit) != 0 : (permissions & AccessibilityBit) != 0);
			return accessible ? PdfProtectionKind.Restricted : PdfProtectionKind.AccessibilityBlocked;
		}

		/// <summary>The word the PDF quality log writes for a protection.</summary>
		public static string Label(PdfProtectionKind kind) => kind switch
		{
			PdfProtectionKind.None => "none",
			PdfProtectionKind.Restricted => "restricted",
			PdfProtectionKind.AccessibilityBlocked => "accessibility blocked",
			_ => "password to open",
		};

		/// <summary>Whether the file is encrypted at all.</summary>
		public static bool IsEncrypted(string latin1) => EncryptDictionary(latin1) is not null;

		/// <summary>The encryption dictionary the trailer (or a cross-reference stream) names; null when the file is not encrypted.</summary>
		internal static string? EncryptDictionary(string latin1)
		{
			var reference = RegexWalk.Each(latin1, @"/Encrypt\s+(\d+)\s+\d+\s+R\b").LastOrDefault();
			if (reference is not null && int.TryParse(reference.Groups[1].Value, out var number)
				&& PdfObjects.Find(latin1, number) is { } found)
			{
				return found.Body;
			}

			int direct = latin1.LastIndexOf("/Encrypt", StringComparison.Ordinal);
			if (direct >= 0)
			{
				int open = latin1.IndexOf("<<", direct, StringComparison.Ordinal);
				if (open >= 0 && open - direct < 32 && Balanced(latin1, open) is { } body)
				{
					return body;
				}
			}

			return null;
		}

		/// <summary>Whether the empty user password opens the file (algorithms 6 and 11 of ISO 32000, 2.A of ISO 32000-2).</summary>
		internal static bool OpensWithoutPassword(string dict, int revision, byte[] id)
		{
			var owner = StringValue(dict, "/O");
			var user = StringValue(dict, "/U");
			if (owner is null || user is null)
			{
				return true; // cannot be told: the restriction is what can be read
			}

			if (revision >= 5)
			{
				if (user.Length < 48)
				{
					return true;
				}

				var salt = user.AsSpan(32, 8).ToArray();
				var hash = revision == 5 ? SHA256.HashData(salt) : Hash2B([], salt, []);
				return hash.AsSpan(0, 32).SequenceEqual(user.AsSpan(0, 32));
			}

			// The key length: 40 bits for version 1, /Length bits for 2 and 3, 128 bits for 4 (its crypt filters give theirs in bytes).
			int version = Integer(dict, "/V") ?? 0;
			int keyLength = revision == 2 || version <= 1 ? 5
				: version >= 4 ? 16
				: Math.Clamp((Integer(dict, "/Length") ?? 40) / 8, 5, 16);
			int permissions = Integer(dict, "/P") ?? -1;
			bool encryptMetadata = !Regex.IsMatch(dict, @"/EncryptMetadata\s+false\b");

			// Algorithm 2: the file key from the (empty) password.
			var input = new List<byte>(Padding);
			input.AddRange(owner.Take(32));
			input.AddRange(BitConverter.IsLittleEndian ? BitConverter.GetBytes(permissions) : BitConverter.GetBytes(permissions).Reverse());
			input.AddRange(id);
			if (revision >= 4 && !encryptMetadata)
			{
				input.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
			}

			var key = MD5.HashData(input.ToArray());
			if (revision >= 3)
			{
				for (int i = 0; i < 50; i++)
				{
					key = MD5.HashData(key.AsSpan(0, keyLength));
				}
			}

			key = key.AsSpan(0, keyLength).ToArray();
			if (revision == 2)
			{
				// Algorithm 4: the padding encrypted with the key is U.
				return user.Length >= 32 && Rc4(key, Padding).AsSpan().SequenceEqual(user.AsSpan(0, 32));
			}

			// Algorithm 5: MD5 of padding and ID, encrypted 20 times; the first 16 bytes are U's.
			var digest = MD5.HashData([.. Padding, .. id]);
			var value = Rc4(key, digest);
			for (int i = 1; i <= 19; i++)
			{
				value = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), value);
			}

			return user.Length >= 16 && value.AsSpan(0, 16).SequenceEqual(user.AsSpan(0, 16));
		}

		// Algorithm 2.B (ISO 32000-2): the hash of revision 6.
		private static byte[] Hash2B(byte[] password, byte[] salt, byte[] userKey)
		{
			var k = SHA256.HashData([.. password, .. salt, .. userKey]);
			for (int round = 0; ; round++)
			{
				var block = new List<byte>();
				var unit = password.Concat(k).Concat(userKey).ToArray();
				for (int i = 0; i < 64; i++)
				{
					block.AddRange(unit);
				}

				using var aes = Aes.Create();
				aes.Key = k.AsSpan(0, 16).ToArray();
				var e = aes.EncryptCbc(block.ToArray(), k.AsSpan(16, 16), PaddingMode.None);
				int sum = 0;
				for (int i = 0; i < 16; i++)
				{
					sum += e[i];
				}

				k = (sum % 3) switch
				{
					0 => SHA256.HashData(e),
					1 => SHA384.HashData(e),
					_ => SHA512.HashData(e),
				};
				if (round >= 63 && e[^1] <= round - 31)
				{
					return k.AsSpan(0, 32).ToArray();
				}
			}
		}

		internal static byte[] Rc4(byte[] key, byte[] data)
		{
			var s = new byte[256];
			for (int i = 0; i < 256; i++)
			{
				s[i] = (byte)i;
			}

			for (int i = 0, j = 0; i < 256; i++)
			{
				j = (j + s[i] + key[i % key.Length]) & 0xFF;
				(s[i], s[j]) = (s[j], s[i]);
			}

			var output = new byte[data.Length];
			for (int n = 0, i = 0, j = 0; n < data.Length; n++)
			{
				i = (i + 1) & 0xFF;
				j = (j + s[i]) & 0xFF;
				(s[i], s[j]) = (s[j], s[i]);
				output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
			}

			return output;
		}

		/// <summary>The first string of the trailer's /ID (the last trailer wins); empty when there is none.</summary>
		private static byte[] FirstId(string latin1)
		{
			var last = RegexWalk.Each(latin1, @"/ID\s*\[\s*(<[0-9A-Fa-f\s]*>|\((?:\\.|[^\\)])*\))").LastOrDefault();
			return last is null ? [] : StringBytes(last.Groups[1].Value) ?? [];
		}

		/// <summary>A string entry of a dictionary as bytes, hex or literal; null when absent.</summary>
		private static byte[]? StringValue(string dict, string key)
		{
			var m = Regex.Match(dict, $@"{Regex.Escape(key)}\s*(<[0-9A-Fa-f\s]*>|\()");
			if (!m.Success)
			{
				return null;
			}

			if (m.Groups[1].Value.StartsWith('<'))
			{
				return StringBytes(m.Groups[1].Value);
			}

			// A literal string: balanced parentheses, backslash escapes; it may hold any byte.
			int start = m.Index + m.Length;
			int depth = 1;
			for (int i = start; i < dict.Length; i++)
			{
				char c = dict[i];
				if (c == '\\')
				{
					i++;
				}
				else if (c == '(')
				{
					depth++;
				}
				else if (c == ')' && --depth == 0)
				{
					return PdfQualityAnalyzer.LiteralBytes(dict[start..i]);
				}
			}

			return null;
		}

		private static byte[]? StringBytes(string token)
		{
			if (token.StartsWith('('))
			{
				return PdfQualityAnalyzer.LiteralBytes(token[1..^1]);
			}

			var hex = new string(token.Where(Uri.IsHexDigit).ToArray());
			if (hex.Length % 2 == 1)
			{
				hex += "0";
			}

			return Convert.FromHexString(hex);
		}

		private static int? Integer(string dict, string key)
		{
			var m = Regex.Match(dict, $@"{Regex.Escape(key)}\s+(-?\d+)\b(?!\s+\d+\s+R)");
			if (!m.Success || !long.TryParse(m.Groups[1].Value, out var value))
			{
				return null;
			}

			// /P is a 32-bit field; some writers give it unsigned.
			return unchecked((int)value);
		}

		/// <summary>The dictionary that opens at <paramref name="open"/>, up to its matching "&gt;&gt;".</summary>
		private static string? Balanced(string text, int open)
		{
			int depth = 0;
			for (int i = open; i + 1 < text.Length; i++)
			{
				if (text[i] == '<' && text[i + 1] == '<')
				{
					depth++;
					i++;
				}
				else if (text[i] == '>' && text[i + 1] == '>')
				{
					depth--;
					i++;
					if (depth == 0)
					{
						return text[open..(i + 1)];
					}
				}
			}

			return null;
		}
	}
}
