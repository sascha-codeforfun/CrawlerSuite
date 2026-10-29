namespace CrawlerSuite.Core
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.IO;
	using System.Linq;
	using System.Text;
	using CrawlerSuite.Core.Urls;

	/// <summary>
	/// Files over the single-file limit. The crawler stores only their first
	/// <see cref="SampleBytes"/> bytes (<c>&lt;name&gt;.sample</c>) and the header sidecar
	/// (<c>&lt;name&gt;.header</c>) in the snapshot's <c>oversized/</c> folder, outside
	/// <c>download/</c> so no analyzer ever treats a fragment as a page or asset.
	/// Log 34 is derived from disk (the <c>Oversized</c> rows in 01, the samples and the
	/// sidecars), so a replay re-creates it.
	/// </summary>
	internal static class OversizedFiles
	{
		/// <summary>Snapshot subfolder holding the samples and their header sidecars.</summary>
		internal const string FolderName = "oversized";

		/// <summary>Bytes kept from the start of an oversized file.</summary>
		internal const int SampleBytes = 64 * 1024;

		internal const string SampleExtension = ".sample";

		/// <summary>
		/// The sample's file name. The real extension is replaced on purpose: a cut-off
		/// fragment must never open as the real file type.
		/// </summary>
		internal static string SampleName(string downloadFileName) =>
			Path.ChangeExtension(downloadFileName, SampleExtension.TrimStart('.'));

		/// <summary>
		/// The sample name the crawler used for <paramref name="url"/>: a page's (and an asset's whose
		/// ending is kept), or a quarantined asset's ("x.asset.sample") when only that one is in
		/// <paramref name="oversizedDirectory"/>. Without a directory, the page's.
		/// </summary>
		internal static string SampleNameForUrl(string url, string? oversizedDirectory = null)
		{
			var pageSample = SampleName(Naming.GenerateFileName(url));
			if (oversizedDirectory is null || File.Exists(Path.Combine(oversizedDirectory, pageSample)))
			{
				return pageSample;
			}

			var assetSample = SampleName(Naming.GenerateAssetFileName(url));
			return File.Exists(Path.Combine(oversizedDirectory, assetSample)) ? assetSample : pageSample;
		}

		/// <summary>Names the file type from its first bytes, or "unknown".</summary>
		internal static string Sniff(ReadOnlySpan<byte> head)
		{
			static bool At(ReadOnlySpan<byte> h, int offset, string ascii)
			{
				if (h.Length < offset + ascii.Length)
				{
					return false;
				}

				for (int i = 0; i < ascii.Length; i++)
				{
					if (h[offset + i] != (byte)ascii[i])
					{
						return false;
					}
				}

				return true;
			}

			static bool Bytes(ReadOnlySpan<byte> h, params byte[] magic) =>
				h.Length >= magic.Length && h[..magic.Length].SequenceEqual(magic);

			if (At(head, 0, "%PDF-")) return "PDF";
			if (Bytes(head, 0x50, 0x4B, 0x03, 0x04) || Bytes(head, 0x50, 0x4B, 0x05, 0x06)) return "ZIP (also DOCX/XLSX/PPTX)";
			if (Bytes(head, 0x1F, 0x8B)) return "GZIP";
			if (Bytes(head, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return "7-Zip";
			if (At(head, 0, "Rar!")) return "RAR";
			if (At(head, 0, "MZ")) return "Windows executable";
			if (Bytes(head, 0x7F, 0x45, 0x4C, 0x46)) return "ELF executable";
			if (At(head, 4, "ftyp")) return "MP4 / QuickTime video";
			if (Bytes(head, 0x1A, 0x45, 0xDF, 0xA3)) return "WebM / Matroska video";
			if (At(head, 0, "RIFF") && At(head, 8, "AVI ")) return "AVI video";
			if (At(head, 0, "RIFF") && At(head, 8, "WAVE")) return "WAV audio";
			if (At(head, 0, "RIFF") && At(head, 8, "WEBP")) return "WebP image";
			if (At(head, 0, "OggS")) return "Ogg audio/video";
			if (At(head, 0, "ID3") || Bytes(head, 0xFF, 0xFB)) return "MP3 audio";
			if (At(head, 0, "fLaC")) return "FLAC audio";
			if (Bytes(head, 0x89, 0x50, 0x4E, 0x47)) return "PNG image";
			if (Bytes(head, 0xFF, 0xD8, 0xFF)) return "JPEG image";
			if (At(head, 0, "GIF8")) return "GIF image";
			if (Bytes(head, 0x49, 0x49, 0x2A, 0x00) || Bytes(head, 0x4D, 0x4D, 0x00, 0x2A)) return "TIFF image";
			if (Bytes(head, 0xD0, 0xCF, 0x11, 0xE0)) return "Office 97-2003 document";
			if (At(head, 0, "SQLite format 3")) return "SQLite database";
			if (FileTypeClassifier.LooksLikeHtml(head.ToArray())) return "HTML";
			return "unknown";
		}

		/// <summary>
		/// Writes log 34 from the <c>Oversized</c> rows of 01, the samples and their sidecars.
		/// Linking pages are found by searching the downloaded pages for the URL's path.
		/// Always written, so its presence shows the check ran (header-only = none).
		/// </summary>
		internal static int WriteLog(
			string crawlerLogPath,
			string oversizedDirectory,
			string downloadDirectory,
			string filePattern,
			string logPath,
			long limitBytes)
		{
			var rows = new List<(string Timestamp, string Url)>();
			if (File.Exists(crawlerLogPath))
			{
				foreach (var line in File.ReadLines(crawlerLogPath, Encoding.UTF8))
				{
					var parts = line.Split('|', StringSplitOptions.TrimEntries);
					if (parts.Length >= 3 && parts[2].Equals(CrawlLogMessages.OversizedStatus, StringComparison.Ordinal))
					{
						rows.Add((parts[0], parts[1]));
					}
				}
			}

			var records = new List<string?[]>
			{
				new string?[] { "timestamp", "url", "size", "contentType", "detectedType", "contentDisposition", "sample", "linkedFrom" },
			};

			if (rows.Count > 0)
			{
				var linkedFrom = FindLinkingPages(rows.Select(r => r.Url).Distinct().ToList(), downloadDirectory, filePattern);

				foreach (var (timestamp, url) in rows)
				{
					var sampleName = SampleNameForUrl(url, oversizedDirectory);
					var samplePath = Path.Combine(oversizedDirectory, sampleName);
					var headerPath = Path.ChangeExtension(samplePath, HeaderSidecar.HeaderSidecarExtension.TrimStart('.'));
					var headers = ReadResponseHeaders(headerPath);

					headers.TryGetValue("Content-Length", out var announced);
					var size = long.TryParse(announced, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
						? $"{n} bytes (announced)"
						: $"more than {limitBytes} bytes (no size announced)";

					string detected = "unknown";
					try
					{
						if (File.Exists(samplePath))
						{
							detected = Sniff(CrawlerSuite.Core.Host.WorkFile.ReadAllBytes(samplePath));
						}
					}
					catch (IOException)
					{
					}

					headers.TryGetValue("Content-Type", out var contentType);
					headers.TryGetValue("Content-Disposition", out var disposition);
					linkedFrom.TryGetValue(url, out var pages);

					records.Add(new string?[]
					{
						timestamp,
						url,
						size,
						contentType ?? "n/a",
						detected,
						disposition ?? "n/a",
						File.Exists(samplePath) ? $"{FolderName}/{sampleName}" : "n/a",
						pages is { Count: > 0 } ? string.Join(" ", pages) : "n/a",
					});
				}
			}

			IssueLogWriter.Write(logPath, '|', records);
			return rows.Count;
		}

		/// <summary>Reads the RESPONSE section of a header sidecar into name → first value.</summary>
		private static Dictionary<string, string> ReadResponseHeaders(string headerPath)
		{
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (!File.Exists(headerPath))
			{
				return result;
			}

			try
			{
				bool inResponse = false;
				foreach (var line in File.ReadLines(headerPath, Encoding.UTF8))
				{
					if (line.StartsWith("=== RESPONSE ===", StringComparison.Ordinal))
					{
						inResponse = true;
						continue;
					}

					if (!inResponse)
					{
						continue;
					}

					var colon = line.IndexOf(':');
					if (colon <= 0)
					{
						continue;
					}

					var name = line[..colon].Trim();
					var value = line[(colon + 1)..].Trim();
					result.TryAdd(name, value);
				}
			}
			catch (IOException)
			{
			}

			return result;
		}

		/// <summary>URL → URLs of every downloaded page that contains the URL or its path.</summary>
		private static Dictionary<string, List<string>> FindLinkingPages(
			List<string> urls, string downloadDirectory, string filePattern)
		{
			var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			if (!Directory.Exists(downloadDirectory))
			{
				return result;
			}

			var needles = urls.Select(u =>
			{
				var path = Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.PathAndQuery : u;
				return (Url: u, Path: path);
			}).ToList();

			foreach (var file in CrawlerSuite.Core.Host.FolderFiles.Sorted(downloadDirectory, filePattern))
			{
				string content;
				try
				{
					content = CrawlerSuite.Core.Host.WorkFile.ReadAllText(file, Encoding.UTF8);
				}
				catch (IOException)
				{
					continue;
				}

				foreach (var (url, path) in needles)
				{
					if (content.Contains(path, StringComparison.OrdinalIgnoreCase))
					{
						if (!result.TryGetValue(url, out var list))
						{
							list = [];
							result[url] = list;
						}

						var pageUrl = CrawlIndex.LookUpUrlForFile(Path.GetFileName(file));
						list.Add(pageUrl == "error" ? Path.GetFileName(file) : pageUrl);
					}
				}
			}

			return result;
		}
	}
}
