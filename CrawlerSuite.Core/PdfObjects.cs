using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace CrawlerSuite.Core
{
	/// <summary>
	/// Just enough of a PDF's object structure for the quality check, without a PDF library:
	/// the objects packed into compressed object streams (PDF 1.5+, where Word, InDesign and Acrobat
	/// keep the catalog and the structure tree), the catalog and the Info dictionary named by the
	/// trailer, and a stream's content. Everything works on the file read as Latin-1, where one
	/// character is one byte, so positions in the text are positions in the file.
	/// Nothing here throws on a broken or encrypted file: what cannot be read is left out.
	/// </summary>
	internal static class PdfObjects
	{
		/// <summary>The most text all object streams of one file may unpack to; beyond it the rest is left packed.</summary>
		internal const int MaxUnpackedChars = 64 * 1024 * 1024;

		// "12 0 obj" at the start of an object.
		private static readonly Regex ObjectStart = new(@"(?<![0-9])(\d+)\s+(\d+)\s+obj\b", RegexOptions.Compiled);

		/// <summary>
		/// The objects inside the file's compressed object streams, written out as
		/// "N 0 obj … endobj" so a plain search sees them like any other object. Empty when there
		/// are none or none can be read (encrypted, damaged, a filter other than Flate).
		/// </summary>
		internal static string UnpackObjectStreams(byte[] bytes, string latin1) => UnpackObjectStreams(bytes, latin1, out _);

		/// <param name="work">The characters of dictionaries looked at, for the tests: at most the file's length.</param>
		internal static string UnpackObjectStreams(byte[] bytes, string latin1, out long work)
		{
			var output = new StringBuilder();
			work = 0;
			// The next "endobj" and "stream" at or after the current object, found once and reused
			// until an object starts past them: one pass over the file, however many objects lack an
			// "endobj" (else a file made of bare headers would make this quadratic). -1: none from here on.
			int nextEndObj = -2, nextStream = -2;
			for (Match start = ObjectStart.Match(latin1), next; start.Success; start = next)
			{
				next = start.NextMatch();
				if (output.Length >= MaxUnpackedChars)
				{
					break;
				}

				// Only the object's own dictionary is looked at: up to its "stream", before the next
				// "endobj" and before the next object's header — without that bound, bare headers and
				// one "stream" after them would make every object's dictionary run to that one "stream".
				int dictStart = start.Index + start.Length;
				int nextHeader = next.Success ? next.Index : latin1.Length;
				nextEndObj = NextFrom(latin1, "endobj", dictStart, nextEndObj);
				nextStream = NextFrom(latin1, "stream", dictStart, nextStream);
				int limit = Math.Min(nextEndObj < 0 ? latin1.Length : nextEndObj, nextHeader);
				int streamAt = nextStream >= 0 && nextStream + "stream".Length <= limit ? nextStream : -1;
				if (streamAt < 0)
				{
					continue;
				}

				work += streamAt - dictStart;
				var dict = latin1[dictStart..streamAt];
				if (!dict.Contains("/ObjStm", StringComparison.Ordinal) || !Regex.IsMatch(dict, @"/Type\s*/ObjStm\b"))
				{
					continue;
				}

				if (Data(bytes, latin1, dict, streamAt) is not { } data
					|| Number(dict, "/N") is not { } count || Number(dict, "/First") is not { } first
					|| first > data.Length)
				{
					continue;
				}

				Unpack(data, count, first, output);
			}

			return output.ToString();
		}

		/// <summary>
		/// Each object's body in text made of whole objects (the unpacked object streams): from its
		/// header to its "endobj", and never past the next header — an object full of "N 0 obj"
		/// cannot make every one of them run to the same "endobj". One pass.
		/// </summary>
		internal static IEnumerable<(int From, int End)> Bodies(string text)
		{
			int nextEndObj = -2;
			for (Match start = ObjectStart.Match(text), next; start.Success; start = next)
			{
				// Only the match in hand and the one after it.
				next = start.NextMatch();
				int from = start.Index + start.Length;
				int nextHeader = next.Success ? next.Index : text.Length;
				nextEndObj = NextFrom(text, "endobj", from, nextEndObj);
				yield return (from, Math.Min(nextEndObj < 0 ? text.Length : nextEndObj, nextHeader));
			}
		}

		// The first occurrence of value at or after from: the one found before while it is still
		// ahead (or there was none), else searched again from here.
		// The objects come in file order, so "none ahead" stays true. -2: not searched yet.
		internal static int NextFrom(string text, string value, int from, int known) =>
			known == -1 || known >= from ? known : text.IndexOf(value, from, StringComparison.Ordinal);

		/// <summary>The object number the trailer names for <paramref name="key"/> ("/Root", "/Info"): the last one, as later updates win.</summary>
		internal static int? TrailerReference(string text, string key)
		{
			var last = RegexWalk.Each(text, $@"{Regex.Escape(key)}\s+(\d+)\s+\d+\s+R\b").LastOrDefault();
			return last is not null && int.TryParse(last.Groups[1].Value, out var n) ? n : null;
		}

		/// <summary>
		/// Where object <paramref name="number"/> starts in <paramref name="text"/> and its body up to
		/// "endobj" (or "stream"); the last definition wins, as in an updated file. Null when absent.
		/// </summary>
		internal static (int Start, string Body)? Find(string text, int number)
		{
			if (RegexWalk.Each(text, $@"(?<![0-9]){number}\s+\d+\s+obj\b").LastOrDefault() is not { } m)
			{
				return null;
			}

			int from = m.Index + m.Length;
			int endObj = text.IndexOf("endobj", from, StringComparison.Ordinal);
			int stream = text.IndexOf("stream", from, StringComparison.Ordinal);
			int end = endObj < 0 ? text.Length : endObj;
			if (stream >= 0 && stream < end)
			{
				end = stream;
			}

			return (m.Index, text[from..end]);
		}

		/// <summary>The object number a dictionary entry points to ("/Metadata 7 0 R"), null when it is direct or absent.</summary>
		internal static int? Reference(string body, string key)
		{
			var m = Regex.Match(body, $@"{Regex.Escape(key)}\s+(\d+)\s+\d+\s+R\b");
			return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
		}

		/// <summary>The decoded content of the stream of object <paramref name="number"/> in the file itself (not in an object stream); null when absent or unreadable.</summary>
		internal static byte[]? StreamOf(byte[] bytes, string latin1, int number)
		{
			if (Find(latin1, number) is not { } found)
			{
				return null;
			}

			int streamAt = latin1.IndexOf("stream", found.Start, StringComparison.Ordinal);
			int endObj = latin1.IndexOf("endobj", found.Start, StringComparison.Ordinal);
			if (streamAt < 0 || (endObj >= 0 && endObj < streamAt))
			{
				return null;
			}

			var dict = latin1[found.Start..streamAt];
			return Data(bytes, latin1, dict, streamAt);
		}

		// The bytes of a stream, inflated when Flate is its only filter; null for any other filter,
		// a predictor, or data that does not inflate.
		private static byte[]? Data(byte[] bytes, string latin1, string dict, int streamAt)
		{
			int dataStart = streamAt + "stream".Length;
			if (dataStart < latin1.Length && latin1[dataStart] == '\r')
			{
				dataStart++;
			}

			if (dataStart < latin1.Length && latin1[dataStart] == '\n')
			{
				dataStart++;
			}

			// In long: a /Length near int.MaxValue must not wrap round and pass.
			int dataEnd = Number(dict, "/Length") is { } length && length >= 0 && (long)dataStart + length <= bytes.Length
				&& !Regex.IsMatch(dict, @"/Length\s+\d+\s+\d+\s+R\b")
				? dataStart + length
				: latin1.IndexOf("endstream", dataStart, StringComparison.Ordinal);
			if (dataEnd < dataStart)
			{
				return null;
			}

			var raw = bytes.AsSpan(dataStart, dataEnd - dataStart).ToArray();
			bool flate = Regex.IsMatch(dict, @"/Filter\s*(?:\[\s*)?/(?:FlateDecode|Fl)\b");
			bool otherFilter = Regex.IsMatch(dict, @"/Filter") && !flate
				|| Regex.IsMatch(dict, @"/Filter\s*\[\s*/\w+\s*/\w+");
			if (otherFilter || dict.Contains("/Predictor", StringComparison.Ordinal))
			{
				return null;
			}

			return flate ? Inflate(raw) : raw;
		}

		private static byte[]? Inflate(byte[] raw)
		{
			try
			{
				using var input = new MemoryStream(raw);
				using var zlib = new ZLibStream(input, CompressionMode.Decompress);
				using var output = new MemoryStream();
				var buffer = new byte[81920];
				int read;
				while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
				{
					output.Write(buffer, 0, read);
					if (output.Length > MaxUnpackedChars)
					{
						return null;
					}
				}

				return output.ToArray();
			}
			catch (InvalidDataException)
			{
				return null;
			}
		}

		// The objects of one object stream: a header of "number offset" pairs, then the objects.
		private static void Unpack(byte[] data, int count, int first, StringBuilder output)
		{
			var text = Encoding.Latin1.GetString(data);
			var header = text[..first].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			var entries = new List<(int Number, int Offset)>();
			for (int i = 0; i + 1 < header.Length && entries.Count < count; i += 2)
			{
				if (int.TryParse(header[i], out var number) && int.TryParse(header[i + 1], out var offset)
					&& offset >= 0 && (long)first + offset <= text.Length)
				{
					entries.Add((number, first + offset));
				}
			}

			for (int i = 0; i < entries.Count && output.Length < MaxUnpackedChars; i++)
			{
				int from = entries[i].Offset;
				int to = i + 1 < entries.Count && entries[i + 1].Offset >= from ? entries[i + 1].Offset : text.Length;
				output.Append('\n').Append(entries[i].Number).Append(" 0 obj\n")
					.Append(text, from, to - from).Append("\nendobj\n");
			}
		}

		private static int? Number(string dict, string key)
		{
			var m = Regex.Match(dict, $@"{Regex.Escape(key)}\s+(\d+)\b(?!\s+\d+\s+R)");
			return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
		}
	}
}
