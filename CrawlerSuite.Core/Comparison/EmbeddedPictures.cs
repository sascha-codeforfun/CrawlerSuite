namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Collections.Generic;
	using System.Text;
	using System.Text.RegularExpressions;

	/// <summary>
	/// Pictures a page carries in its source as base64 text — <c>data:image/…;base64,…</c> — found
	/// in a line, so the Comparison's File tab can show them as pictures beside the text: an icon,
	/// a photo, a QR code; the tool does not know which and does not guess. The type is read from
	/// the bytes, not only from the label the source gives it.
	/// </summary>
	public static class EmbeddedPictures
	{
		/// <summary>Base64 runs shorter than this are not folded: the text is short enough to read.</summary>
		public const int FoldFrom = 120;

		private static readonly Regex DataUri = new(
			@"data:image/(?<type>[A-Za-z0-9.+\-]+)(?:;[A-Za-z0-9\-]+=[^;,""'\s]*)*;base64,(?<data>[A-Za-z0-9+/]+={0,2})",
			RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

		private static readonly Regex Base64Run = new(@"[A-Za-z0-9+/]{" + FoldFrom + @",}={0,2}", RegexOptions.Compiled);

		/// <summary>One picture in a line: the type its label gives, where its base64 text stands, and its bytes (null when the text does not decode).</summary>
		public sealed record Found(string Labelled, int Start, int Length, byte[]? Bytes)
		{
			/// <summary>The type the bytes are ("PNG", "JPEG", "SVG" …); "unknown" when they say nothing known.</summary>
			public string Kind => Bytes is null ? "unreadable" : Sniff(Bytes);

			/// <summary>An SVG: text, shown as its text.</summary>
			public bool IsSvg => Kind == "SVG";

			/// <summary>The label and the bytes disagree (a JPEG labelled image/png).</summary>
			public bool Mislabelled => Bytes is not null && Kind != "unknown" && !Matches(Labelled, Kind);
		}

		/// <summary>The pictures in a line, in order.</summary>
		public static IReadOnlyList<Found> In(string? line, long maxBytes = SnapshotComparison.MaxShownBytes)
		{
			var list = new List<Found>();
			if (string.IsNullOrEmpty(line) || line.IndexOf("base64,", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return list;
			}

			foreach (Match m in DataUri.Each(line))
			{
				var data = m.Groups["data"];
				byte[]? bytes = null;
				if ((long)data.Length * 3 / 4 <= maxBytes)
				{
					var buffer = new byte[(data.Length * 3 / 4) + 3];
					var text = data.Value.Length % 4 == 0 ? data.Value : data.Value.PadRight(data.Value.Length + (4 - (data.Value.Length % 4)), '=');
					if (Convert.TryFromBase64String(text, buffer, out int written))
					{
						bytes = buffer.AsSpan(0, written).ToArray();
					}
				}

				list.Add(new Found("image/" + m.Groups["type"].Value.ToLowerInvariant(), data.Index, data.Length, bytes));
			}

			return list;
		}

		/// <summary>Where a shown line's long base64 runs stand: folded away while the picture is shown instead.</summary>
		public static IReadOnlyList<(int Start, int Length)> Folds(string shown)
		{
			var list = new List<(int, int)>();
			foreach (Match m in Base64Run.Each(shown))
			{
				list.Add((m.Index, m.Length));
			}

			return list;
		}

		/// <summary>The picture type its first bytes show.</summary>
		public static string Sniff(ReadOnlySpan<byte> b)
		{
			if (b.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
			{
				return "PNG";
			}

			if (b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
			{
				return "JPEG";
			}

			if (b.StartsWith("GIF8"u8))
			{
				return "GIF";
			}

			if (b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8))
			{
				return "WebP";
			}

			if (b.StartsWith("BM"u8))
			{
				return "BMP";
			}

			if (b.StartsWith((ReadOnlySpan<byte>)[0, 0, 1, 0]))
			{
				return "ICO";
			}

			if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8) && (b[8..12].SequenceEqual("avif"u8) || b[8..12].SequenceEqual("avis"u8)))
			{
				return "AVIF";
			}

			var head = Encoding.UTF8.GetString(b[..Math.Min(b.Length, 512)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
			if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
				|| (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
			{
				return "SVG";
			}

			return "unknown";
		}

		private static bool Matches(string labelled, string kind) => kind switch
		{
			"PNG" => labelled is "image/png" or "image/apng",
			"JPEG" => labelled is "image/jpeg" or "image/jpg" or "image/pjpeg",
			"GIF" => labelled == "image/gif",
			"WebP" => labelled == "image/webp",
			"BMP" => labelled is "image/bmp" or "image/x-ms-bmp",
			"ICO" => labelled is "image/x-icon" or "image/vnd.microsoft.icon" or "image/ico",
			"AVIF" => labelled == "image/avif",
			"SVG" => labelled == "image/svg+xml",
			_ => true,
		};

		/// <summary>How many pixels differ between two pictures of the same size, given as 32-bit pixels (4 bytes each).</summary>
		public static int DifferingPixels(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
		{
			int differing = 0;
			int n = Math.Min(a.Length, b.Length) / 4;
			for (int i = 0; i < n; i++)
			{
				if (!a.Slice(i * 4, 4).SequenceEqual(b.Slice(i * 4, 4)))
				{
					differing++;
				}
			}

			return differing;
		}

		/// <summary>
		/// A shown line cut into pieces around its marks and its folds: plain or marked text, and
		/// each fold as one piece standing for the base64 it hides — marked when anything in it differs.
		/// </summary>
		public static IReadOnlyList<(string Text, bool Hit, bool Fold)> Pieces(string text, IReadOnlyList<(int Start, int Length)> marks,
			IReadOnlyList<(int Start, int Length)> folds)
		{
			var hit = new bool[text.Length];
			foreach (var (start, length) in marks)
			{
				for (int i = Math.Max(0, start); i < Math.Min(text.Length, start + length); i++)
				{
					hit[i] = true;
				}
			}

			var fold = new int[text.Length];
			for (int f = 0; f < folds.Count; f++)
			{
				for (int i = Math.Max(0, folds[f].Start); i < Math.Min(text.Length, folds[f].Start + folds[f].Length); i++)
				{
					fold[i] = f + 1;
				}
			}

			var list = new List<(string, bool, bool)>();
			int at = 0;
			while (at < text.Length)
			{
				if (fold[at] > 0)
				{
					int f = fold[at];
					int end = at;
					bool any = false;
					while (end < text.Length && fold[end] == f)
					{
						any |= hit[end];
						end++;
					}

					list.Add(($"⟨base64, {CrawlerSuite.Core.Display.NumberText.Group(end - at)} characters — shown below⟩", any, true));
					at = end;
					continue;
				}

				int stop = at;
				while (stop < text.Length && fold[stop] == 0 && hit[stop] == hit[at])
				{
					stop++;
				}

				list.Add((text[at..stop], hit[at], false));
				at = stop;
			}

			return list;
		}
	}
}
