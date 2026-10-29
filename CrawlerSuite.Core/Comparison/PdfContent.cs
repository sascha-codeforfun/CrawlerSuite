namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.IO.Compression;
	using System.Linq;
	using System.Security.Cryptography;
	using System.Text;
	using System.Text.RegularExpressions;
	using CrawlerSuite.Core.Pdf;

	/// <summary>
	/// What a PDF carries, apart from how it is laid out in the file: every object and every
	/// stream (page content, fonts, pictures), unpacked and hashed, as a multiset. A PDF written
	/// out again by its generator moves object numbers, the cross-reference table, the subset tags
	/// of its fonts ("ABCDEF+") and its stamps; none of that is in the fingerprint, so a file
	/// regenerated with the same content has the same one.
	/// </summary>
	/// <param name="Encrypted">Encrypted: its content cannot be unpacked, so it has no parts.</param>
	/// <param name="Pages">Page objects found.</param>
	/// <param name="Parts">Hash of each normalised object and stream, with how often it occurs.</param>
	/// <param name="Stamps">The stamps set aside (creation and modification date, producer, creator, the file ID, the XMP packet), by name, for the report.</param>
	/// <param name="Described">What each part is, by its hash: for telling the parts that differ apart.</param>
	/// <param name="SetAsideHits">Read with the site's patterns (<see cref="PdfSetAside"/>): the names of those that matched in its page content.</param>
	public sealed record PdfFingerprint(bool Encrypted, int Pages, IReadOnlyDictionary<string, int> Parts, IReadOnlyDictionary<string, string> Stamps,
		IReadOnlyDictionary<string, PdfPart>? Described = null, IReadOnlyCollection<string>? SetAsideHits = null);

	/// <summary>One part of a PDF, described: its kind, its size once unpacked, and its dictionary as text (shortened).</summary>
	public sealed record PdfPart(string Kind, long Size, string Text);

	/// <summary>A row of the File tab's PDF table: what, its value in A and in B, and whether they differ.</summary>
	public sealed record PdfRow(string Label, string A, string B, bool Differs, string? TipA = null, string? TipB = null);

	/// <summary>How two PDFs' fingerprints compare.</summary>
	/// <param name="SameContent">The same parts and pages on both sides: the file was written again with the same content.</param>
	/// <param name="ChangedStamps">The stamps that differ, by name.</param>
	/// <param name="PartsOnlyA">The parts only A has, described (at most <see cref="PdfContent.MaxPartsShown"/>).</param>
	public sealed record PdfDifference(bool SameContent, bool EncryptedA, bool EncryptedB, int PagesA, int PagesB,
		int Same, int OnlyA, int OnlyB, IReadOnlyList<string> ChangedStamps,
		IReadOnlyDictionary<string, string>? StampsA = null, IReadOnlyDictionary<string, string>? StampsB = null,
		IReadOnlyList<PdfPart>? PartsOnlyA = null, IReadOnlyList<PdfPart>? PartsOnlyB = null)
	{
		private static readonly (string Key, string Label)[] StampRows =
		[
			("/CreationDate", "Created"), ("/ModDate", "Modified"), ("/Producer", "Producer"), ("/Creator", "Creator"), ("/ID", "File ID"),
			("XMP xmp:CreateDate", "XMP created"), ("XMP xmp:ModifyDate", "XMP modified"), ("XMP xmp:MetadataDate", "XMP metadata date"),
			("XMP pdf:Producer", "XMP producer"), ("XMP xmp:CreatorTool", "XMP creator tool"),
			("XMP xmpMM:DocumentID", "XMP document ID"), ("XMP xmpMM:InstanceID", "XMP instance ID"),
		];

		/// <summary>
		/// The table: pages, content parts, then every stamp either side has — dates readable,
		/// IDs shortened (in full as the tip). Empty for an encrypted file.
		/// </summary>
		public IReadOnlyList<PdfRow> Rows()
		{
			var rows = new List<PdfRow>();
			if (EncryptedA || EncryptedB)
			{
				return rows;
			}

			rows.Add(new PdfRow("Pages", PagesA.ToString(System.Globalization.CultureInfo.InvariantCulture),
				PagesB.ToString(System.Globalization.CultureInfo.InvariantCulture), PagesA != PagesB));
			rows.Add(new PdfRow("Content parts", $"{Same + OnlyA:N0}", SameContent ? $"{Same + OnlyB:N0} (the same)" : $"{Same + OnlyB:N0} ({Same:N0} the same)", !SameContent));
			var a = StampsA ?? new Dictionary<string, string>();
			var b = StampsB ?? new Dictionary<string, string>();
			foreach (var (key, label) in StampRows)
			{
				var rawA = a.GetValueOrDefault(key);
				var rawB = b.GetValueOrDefault(key);
				if (rawA is null && rawB is null)
				{
					continue;
				}

				var (shownA, tipA) = PdfContent.Readable(key, rawA);
				var (shownB, tipB) = PdfContent.Readable(key, rawB);
				rows.Add(new PdfRow(label, shownA, shownB, !string.Equals(rawA, rawB, StringComparison.Ordinal),
					tipA.Length > 0 ? tipA : null, tipB.Length > 0 ? tipB : null));
			}

			return rows;
		}

		/// <summary>The difference in a few lines, for the File tab.</summary>
		public IReadOnlyList<string> Lines()
		{
			var lines = new List<string>();
			if (EncryptedA || EncryptedB)
			{
				var side = EncryptedA && EncryptedB ? "Both are" : EncryptedA ? "A is" : "B is";
				lines.Add($"{side} encrypted: the content cannot be unpacked here, so the file is compared by its bytes only.");
				return lines;
			}

			lines.Add($"Pages: {PagesA} in A, {PagesB} in B.");
			lines.Add(SameContent
				? "Content: the same — page content, fonts and pictures unchanged; the file was written out again."
				: $"Content parts (objects and streams): {Same} the same, {OnlyA} only in A, {OnlyB} only in B.");
			lines.Add(ChangedStamps.Count == 0 ? "Stamps: unchanged." : $"Stamps that changed: {string.Join(", ", ChangedStamps)}.");
			return lines;
		}
	}

	/// <summary>Reads and compares PDFs by what they carry (<see cref="PdfFingerprint"/>), without a PDF library.</summary>
	public static class PdfContent
	{
		/// <summary>Larger PDFs are compared by their bytes only.</summary>
		public const long MaxBytes = 32L * 1024 * 1024;

		/// <summary>The most one stream may unpack to; beyond it, its packed bytes stand for it.</summary>
		private const int MaxUnpackedBytes = 64 * 1024 * 1024;

		// The stamps a generator writes anew each time; set aside, reported.
		private static readonly string[] StampKeys = ["/CreationDate", "/ModDate", "/Producer", "/Creator"];

		private static readonly Regex ObjectStart = new(@"(?<![0-9])(\d+)\s+(\d+)\s+obj\b", RegexOptions.Compiled);
		private static readonly Regex LengthEntry = new(@"/(?:Length|DL)\s+\d+(?:\s+\d+\s+R\b)?", RegexOptions.Compiled);
		private static readonly Regex ReferenceEntry = new(@"(?<![0-9])\d+\s+\d+\s+R\b", RegexOptions.Compiled);
		private static readonly Regex SubsetTag = new(@"(?<![A-Za-z0-9])[A-Z]{6}\+(?=[A-Za-z0-9])", RegexOptions.Compiled);
		private static readonly Regex Blanks = new(@"\s+", RegexOptions.Compiled);
		private static readonly Regex PageType = new(@"/Type\s*/Page(?![A-Za-z0-9])", RegexOptions.Compiled);
		private static readonly Regex SkippedType = new(@"/Type\s*/(?:ObjStm|XRef|Metadata)(?![A-Za-z0-9])", RegexOptions.Compiled);
		private static readonly Regex FontProgram = new(@"/Length1\b|/Subtype\s*/(?:Type1C|CIDFontType0C|OpenType)\b", RegexOptions.Compiled);
		private static readonly Regex FlateOnly = new(@"/Filter\s*(?:/(?:FlateDecode|Fl)\b|\[\s*/(?:FlateDecode|Fl)\s*\])", RegexOptions.Compiled);
		private static readonly Regex FontLengths = new(@"/Length[123]\s+\d+", RegexOptions.Compiled);
		private static readonly Regex NotPageContent = new(@"/Subtype\s*/Image\b|/Type\s*/EmbeddedFile\b|/Type\s*/XRef\b|/Alternate\b", RegexOptions.Compiled);

		/// <summary>A stream that is page content (or a drawing a page uses): not a picture, a font, an attached file or a colour profile.</summary>
		private static bool IsPageContent(string dict) => !FontProgram.IsMatch(dict) && !NotPageContent.IsMatch(dict);

		private static readonly Regex FileId = new(@"/ID\s*\[\s*(<[0-9A-Fa-f\s]*>|\((?:\\.|[^\\)])*\))", RegexOptions.Compiled);

		/// <summary>Whether the bytes are a PDF: its header within the first kilobyte, as readers accept it.</summary>
		public static bool IsPdf(ReadOnlySpan<byte> head) => head[..Math.Min(head.Length, 1024)].IndexOf("%PDF-"u8) >= 0;

		/// <summary>Whether two saved PDFs carry the same content; null when either is no PDF, too large, encrypted or unreadable.</summary>
		public static bool? SameContent(string pathA, string pathB) =>
			Read(pathA) is { Encrypted: false } a && Read(pathB) is { Encrypted: false } b ? Compare(a, b).SameContent : null;

		/// <summary>
		/// Whether two saved PDFs carry the same content once the site's patterns are set aside in
		/// their page content (<see cref="PdfSetAside"/>), and which patterns that took. Their font
		/// programs are then not compared by their bytes: a font subset holds the glyphs its text
		/// uses, so a set-aside digit changes it too. True only when a pattern matched on either side
		/// and every other part — every other line of page content included — is the same; null when
		/// either is no PDF, too large, encrypted or unreadable.
		/// </summary>
		public static (bool Same, IReadOnlyCollection<string> Hits)? SameContentBut(string pathA, string pathB, PdfSetAside setAside)
		{
			if (Read(pathA, setAside) is not { Encrypted: false } a || Read(pathB, setAside) is not { Encrypted: false } b)
			{
				return null;
			}

			var hits = new SortedSet<string>(a.SetAsideHits ?? [], StringComparer.Ordinal);
			hits.UnionWith(b.SetAsideHits ?? []);
			return (hits.Count > 0 && Compare(a, b).SameContent, hits);
		}

		/// <summary>How two saved PDFs differ; null when either is missing, no PDF or too large.</summary>
		public static PdfDifference? Compare(string? pathA, string? pathB) =>
			pathA is not null && pathB is not null && Read(pathA) is { } a && Read(pathB) is { } b ? Compare(a, b) : null;

		public static PdfDifference Compare(PdfFingerprint a, PdfFingerprint b)
		{
			int same = 0;
			foreach (var (hash, count) in a.Parts)
			{
				same += b.Parts.TryGetValue(hash, out var other) ? Math.Min(count, other) : 0;
			}

			int totalA = a.Parts.Values.Sum(), totalB = b.Parts.Values.Sum();
			var changed = a.Stamps.Keys.Union(b.Stamps.Keys)
				.Where(k => !string.Equals(a.Stamps.GetValueOrDefault(k), b.Stamps.GetValueOrDefault(k), StringComparison.Ordinal))
				.OrderBy(k => Array.IndexOf(StampOrder, k) is var i && i >= 0 ? i : int.MaxValue)
				.ToList();
			bool sameContent = !a.Encrypted && !b.Encrypted && totalA > 0 && same == totalA && same == totalB && a.Pages == b.Pages;
			return new PdfDifference(sameContent, a.Encrypted, b.Encrypted, a.Pages, b.Pages, same, totalA - same, totalB - same, changed,
				a.Stamps, b.Stamps, OnlyIn(a, b), OnlyIn(b, a));
		}

		/// <summary>The most parts listed per side as differing.</summary>
		public const int MaxPartsShown = 40;

		/// <summary>The parts <paramref name="side"/> has more often than <paramref name="other"/>, described.</summary>
		private static List<PdfPart> OnlyIn(PdfFingerprint side, PdfFingerprint other)
		{
			var list = new List<PdfPart>();
			foreach (var (hash, count) in side.Parts)
			{
				int extra = count - other.Parts.GetValueOrDefault(hash);
				for (int i = 0; i < extra && list.Count < MaxPartsShown; i++)
				{
					list.Add(side.Described?.GetValueOrDefault(hash) ?? new PdfPart("object", 0, string.Empty));
				}
			}

			return [.. list.OrderBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.Text, StringComparer.Ordinal)];
		}

		/// <summary>A stamp's value as the table shows it, and the full value as its tip: dates readable, IDs shortened, strings decoded.</summary>
		internal static (string Shown, string Tip) Readable(string key, string? raw)
		{
			if (raw is null)
			{
				return ("–", string.Empty);
			}

			var value = key.StartsWith("XMP ", StringComparison.Ordinal) ? raw : TextOf(raw);
			if (key is "/CreationDate" or "/ModDate" && PdfDate.Match(value) is { Success: true } d)
			{
				var zone = d.Groups["z"].Value switch
				{
					"Z" => " UTC",
					"" => string.Empty,
					var sign => $" {sign}{Or(d.Groups["zh"].Value, "00")}:{Or(d.Groups["zm"].Value, "00")}",
				};
				return ($"{d.Groups["y"].Value}-{Or(d.Groups["mo"].Value, "01")}-{Or(d.Groups["d"].Value, "01")} "
					+ $"{Or(d.Groups["h"].Value, "00")}:{Or(d.Groups["mi"].Value, "00")}:{Or(d.Groups["s"].Value, "00")}{zone}", value);
			}

			if (key == "/ID")
			{
				var hex = new string(raw.Where(Uri.IsHexDigit).ToArray());
				return (hex.Length > 12 ? hex[..12].ToLowerInvariant() + "…" : hex.ToLowerInvariant(), hex.ToLowerInvariant());
			}

			if (key.EndsWith("ID", StringComparison.Ordinal) && value.Length > 24)
			{
				return (value[..24] + "…", value);
			}

			if (key.Contains("Date", StringComparison.Ordinal) && value.Length >= 19 && value[10] == 'T')
			{
				return (value[..10] + " " + value[11..19] + (value.Length > 19 ? " " + value[19..] : string.Empty), value);
			}

			return (value, value.Length > 60 ? value : string.Empty);

			static string Or(string text, string fallback) => text.Length > 0 ? text : fallback;
		}

		private static readonly Regex PdfDate = new(
			@"^(?:D:)?(?<y>\d{4})(?<mo>\d{2})?(?<d>\d{2})?(?<h>\d{2})?(?<mi>\d{2})?(?<s>\d{2})?(?<z>[Zz+\-])?(?<zh>\d{2})?'?(?<zm>\d{2})?'?",
			RegexOptions.Compiled);

		/// <summary>A PDF string value as text: a literal (…) or a hex &lt;…&gt; string, decoded as PDF text strings are.</summary>
		private static string TextOf(string raw)
		{
			var v = raw.Trim();
			try
			{
				if (v.Length >= 2 && v[0] == '(' && v[^1] == ')')
				{
					return PdfQualityAnalyzer.DecodeTextString(PdfQualityAnalyzer.LiteralBytes(v[1..^1])).Trim();
				}

				if (v.Length >= 2 && v[0] == '<' && v[^1] == '>')
				{
					var hex = new string(v.Where(Uri.IsHexDigit).ToArray());
					return PdfQualityAnalyzer.DecodeTextString(Convert.FromHexString(hex.Length % 2 == 0 ? hex : hex + "0")).Trim();
				}
			}
			catch (FormatException)
			{
			}

			return v.TrimStart('/');
		}

		private static readonly string[] StampOrder = ["/CreationDate", "/ModDate", "/Producer", "/Creator", "/ID", "XMP metadata"];

		/// <summary>The fingerprint of a saved file; null when it is no PDF, larger than <see cref="MaxBytes"/> or cannot be read.</summary>
		public static PdfFingerprint? Read(string path) => Read(path, null);

		private static PdfFingerprint? Read(string path, PdfSetAside? setAside)
		{
			try
			{
				var info = new FileInfo(path);
				if (!info.Exists || info.Length > MaxBytes)
				{
					return null;
				}

				var bytes = File.ReadAllBytes(path);
				return IsPdf(bytes) ? Read(bytes, setAside) : null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}

		/// <summary>The fingerprint of a PDF's bytes; with <paramref name="setAside"/>, the site's patterns set aside in its page content and its font programs taken by their dictionaries only.</summary>
		public static PdfFingerprint Read(byte[] bytes, PdfSetAside? setAside = null)
		{
			var hits = new SortedSet<string>(StringComparer.Ordinal);
			var text = Encoding.Latin1.GetString(bytes);
			var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
			if (FileId.Each(text).LastOrDefault() is { } id)
			{
				stamps["/ID"] = id.Groups[1].Value;
			}

			if (PdfProtection.IsEncrypted(text))
			{
				return new PdfFingerprint(true, 0, new Dictionary<string, int>(), stamps);
			}

			var parts = new Dictionary<string, int>(StringComparer.Ordinal);
			var described = new Dictionary<string, PdfPart>(StringComparer.Ordinal);
			int pages = 0;

			void Add(string kind, string normalised, byte[]? data)
			{
				using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
				sha.AppendData(Encoding.Latin1.GetBytes(kind + normalised + "\0"));
				if (data is not null)
				{
					sha.AppendData(data);
				}

				var key = Convert.ToHexString(sha.GetHashAndReset());
				parts[key] = parts.GetValueOrDefault(key) + 1;
				described.TryAdd(key, Describe(kind, normalised, data));
			}

			void AddObject(string body)
			{
				var normalised = Normalise(body, stamps);
				pages += PageType.IsMatch(normalised) ? 1 : 0;
				Add("D", normalised, null);
			}

			int pos = 0;
			Match start;
			while ((start = ObjectStart.Match(text, pos)).Success)
			{
				int dictStart = start.Index + start.Length;
				int endObj = text.IndexOf("endobj", dictStart, StringComparison.Ordinal);
				int limit = endObj < 0 ? text.Length : endObj;
				int streamAt = StreamKeyword(text, dictStart, limit);
				if (streamAt < 0)
				{
					AddObject(text[dictStart..limit]);
					pos = Math.Min(text.Length, limit + "endobj".Length);
					continue;
				}

				var dict = text[dictStart..streamAt];
				var (dataStart, dataEnd) = DataRange(text, dict, streamAt, bytes.Length);
				int endStream = text.IndexOf("endstream", dataEnd, StringComparison.Ordinal);
				int after = endStream < 0 ? dataEnd : endStream + "endstream".Length;
				int objEnd = text.IndexOf("endobj", after, StringComparison.Ordinal);
				pos = objEnd < 0 ? text.Length : objEnd + "endobj".Length;

				if (SkippedType.IsMatch(dict))
				{
					if (dict.Contains("/Metadata", StringComparison.Ordinal))
					{
						// The XMP packet carries dates and document IDs: set aside, reported.
						stamps["XMP metadata"] = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(dataStart, dataEnd - dataStart)));
						ReadXmp(Decode(dict, bytes.AsSpan(dataStart, dataEnd - dataStart).ToArray()), stamps);
					}

					continue; // object streams are read below; cross-reference streams are layout
				}

				var data = Decode(dict, bytes.AsSpan(dataStart, dataEnd - dataStart).ToArray());
				bool fontProgram = FontProgram.IsMatch(dict);
				if (fontProgram)
				{
					data = FontSubsets.Neutralise(data);
				}

				var normalised = Normalise(dict, stamps);
				if (setAside is not null)
				{
					if (fontProgram)
					{
						// The glyphs follow from the text, which is compared: the program counts by its dictionary only.
						Add("F", FontLengths.Replace(normalised, string.Empty), null);
						continue;
					}

					if (IsPageContent(dict))
					{
						data = Encoding.Latin1.GetBytes(setAside.Apply(Encoding.Latin1.GetString(data), hits));
					}
				}

				Add("S", normalised, data);
			}

			// The objects packed into object streams (PDF 1.5+), as plain objects.
			var unpacked = PdfObjects.UnpackObjectStreams(bytes, text);
			foreach (var (from, end) in PdfObjects.Bodies(unpacked))
			{
				AddObject(unpacked[from..end]);
			}

			return new PdfFingerprint(false, pages, parts, stamps, described, setAside is null ? null : hits);
		}

		private static readonly string[] XmpFields =
			["xmp:CreateDate", "xmp:ModifyDate", "xmp:MetadataDate", "pdf:Producer", "xmp:CreatorTool", "xmpMM:DocumentID", "xmpMM:InstanceID"];

		/// <summary>The dates and IDs of an XMP packet, as stamps ("XMP xmp:ModifyDate"): element or attribute form.</summary>
		private static void ReadXmp(byte[] data, Dictionary<string, string> stamps)
		{
			var xmp = Encoding.UTF8.GetString(data);
			foreach (var field in XmpFields)
			{
				var name = Regex.Escape(field);
				var m = Regex.Match(xmp, $@"<{name}>\s*([^<]*?)\s*</{name}>");
				if (!m.Success)
				{
					m = Regex.Match(xmp, $@"\b{name}\s*=\s*[""']([^""']*)[""']");
				}

				if (m.Success && m.Groups[1].Value.Length > 0)
				{
					stamps["XMP " + field] = m.Groups[1].Value;
				}
			}
		}

		private static readonly Regex Printable = new(@"[^\x20-\x7E\u00A0-\u00FF]+", RegexOptions.Compiled);

		/// <summary>What a part is, from its dictionary: for the list of parts that differ.</summary>
		private static PdfPart Describe(string kind, string dict, byte[]? data)
		{
			bool Has(string pattern) => Regex.IsMatch(dict, pattern);
			string what = kind == "S"
				? Has(@"/Subtype\s*/Image\b") ? "picture"
					: Has(@"/Length1\b|/Subtype\s*/(?:Type1C|CIDFontType0C|OpenType)\b") ? "font program"
					: Has(@"/Subtype\s*/Form\b") ? "drawing (form)"
					: Has(@"/Type\s*/EmbeddedFile\b") ? "attached file"
					: Has(@"/N\s+[134]\b") && Has(@"/Alternate\b") ? "colour profile"
					: "page content"
				: Has(@"/Type\s*/Page(?![A-Za-z])") ? "page"
					: Has(@"/Type\s*/Pages\b") ? "page tree"
					: Has(@"/Type\s*/(?:Font|FontDescriptor)\b") ? "font"
					: Has(@"/Type\s*/Annot\b|/Subtype\s*/(?:Link|Widget)\b") ? "annotation"
					: Has(@"/Type\s*/Catalog\b") ? "catalog"
					: Has(@"/Type\s*/StructElem\b|/Type\s*/StructTreeRoot\b") ? "structure"
					: Has(@"/Title\b|/Author\b|/Subject\b") ? "document info"
					: "object";
			var text = dict.Length > 300 ? dict[..300] + " …" : dict;
			if (what == "page content" && data is { Length: > 0 })
			{
				var sample = Printable.Replace(Encoding.Latin1.GetString(data, 0, Math.Min(data.Length, 400)), " ").Trim();
				text = sample.Length > 200 ? sample[..200] + " …" : sample;
			}

			return new PdfPart(what, data?.LongLength ?? dict.Length, text);
		}

		/// <summary>
		/// An object's dictionary as the comparison sees it: lengths, references (renumbered on each
		/// write), subset tags and stamps taken out, blanks collapsed.
		/// </summary>
		internal static string Normalise(string body, Dictionary<string, string> stamps)
		{
			var text = TakeStamps(body, stamps);
			text = LengthEntry.Replace(text, string.Empty);
			text = ReferenceEntry.Replace(text, "R");
			text = SubsetTag.Replace(text, "+");
			return Blanks.Replace(text, " ").Trim();
		}

		// Takes the stamp entries out of a dictionary, noting their values.
		private static string TakeStamps(string body, Dictionary<string, string> stamps)
		{
			foreach (var key in StampKeys)
			{
				int at;
				int from = 0;
				while ((at = body.IndexOf(key, from, StringComparison.Ordinal)) >= 0)
				{
					int valueStart = at + key.Length;
					if (valueStart < body.Length && (char.IsAsciiLetterOrDigit(body[valueStart]) || body[valueStart] == '_'))
					{
						from = valueStart; // a longer name ("/Creator2")
						continue;
					}

					int valueEnd = ValueEnd(body, valueStart);
					stamps[key] = body[valueStart..valueEnd].Trim();
					body = body.Remove(at, valueEnd - at);
					from = at;
				}
			}

			return body;
		}

		// Where the value after a key ends: a literal string (balanced, escapes), a hex string, or one token.
		private static int ValueEnd(string text, int i)
		{
			while (i < text.Length && char.IsWhiteSpace(text[i]))
			{
				i++;
			}

			if (i >= text.Length)
			{
				return i;
			}

			if (text[i] == '(')
			{
				int depth = 0;
				for (; i < text.Length; i++)
				{
					if (text[i] == '\\')
					{
						i++;
					}
					else if (text[i] == '(')
					{
						depth++;
					}
					else if (text[i] == ')' && --depth == 0)
					{
						return i + 1;
					}
				}

				return text.Length;
			}

			if (text[i] == '<' && (i + 1 >= text.Length || text[i + 1] != '<'))
			{
				int close = text.IndexOf('>', i);
				return close < 0 ? text.Length : close + 1;
			}

			if (text[i] == '/')
			{
				i++;
			}

			while (i < text.Length && !char.IsWhiteSpace(text[i]) && "/<>[]()".IndexOf(text[i]) < 0)
			{
				i++;
			}

			return i;
		}

		// The "stream" keyword of an object (not "endstream"), between its dictionary and "endobj".
		private static int StreamKeyword(string text, int from, int limit)
		{
			int at = from;
			while ((at = text.IndexOf("stream", at, limit - at, StringComparison.Ordinal)) >= 0)
			{
				if (at < 3 || !text.AsSpan(at - 3, 3).SequenceEqual("end"))
				{
					return at;
				}

				at += "stream".Length;
				if (at >= limit)
				{
					break;
				}
			}

			return -1;
		}

		// Where a stream's data starts and ends: by its /Length when that is direct and fits, else up to "endstream".
		private static (int Start, int End) DataRange(string text, string dict, int streamAt, int fileLength)
		{
			int dataStart = streamAt + "stream".Length;
			if (dataStart < text.Length && text[dataStart] == '\r')
			{
				dataStart++;
			}

			if (dataStart < text.Length && text[dataStart] == '\n')
			{
				dataStart++;
			}

			var length = Regex.Match(dict, @"/Length\s+(\d+)\b(?!\s+\d+\s+R)");
			if (length.Success && int.TryParse(length.Groups[1].Value, out var n) && dataStart + n <= fileLength
				&& text.AsSpan(dataStart + n, Math.Min(32, fileLength - dataStart - n)).IndexOf("endstream") >= 0)
			{
				return (dataStart, dataStart + n);
			}

			int end = text.IndexOf("endstream", dataStart, StringComparison.Ordinal);
			if (end < 0)
			{
				return (dataStart, dataStart);
			}

			// The line end before "endstream" is not data.
			int dataEnd = end;
			if (dataEnd > dataStart && text[dataEnd - 1] == '\n')
			{
				dataEnd--;
			}

			if (dataEnd > dataStart && text[dataEnd - 1] == '\r')
			{
				dataEnd--;
			}

			return (dataStart, dataEnd);
		}

		// A stream's content: unpacked when Flate is its only filter (a predictor left as it is),
		// else its bytes as they are — a JPEG is compared as the JPEG it is.
		private static byte[] Decode(string dict, byte[] raw)
		{
			if (!FlateOnly.IsMatch(dict))
			{
				return raw;
			}

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
					if (output.Length > MaxUnpackedBytes)
					{
						return raw;
					}
				}

				return output.ToArray();
			}
			catch (InvalidDataException)
			{
				return raw;
			}
		}
	}
}
