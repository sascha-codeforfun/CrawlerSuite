using System.Text;
using System.Text.RegularExpressions;

namespace CrawlerSuite.Core
{
	// ── PdfQualityAnalyzer ────────────────────────────────────────────────────
	//
	// Checks all PDF files in the download directory for metadata quality.
	// Uses pure .NET — no third-party PDF library required.
	//
	// Output: 17-pdf-quality_{semicolon,comma}.csv — one row per PDF, columns:
	//   PageUrl, Title, Description, Keywords, Language, Tags, PdfA,
	//   StructTree, RoleMap, Outlines, AltText, FormFields, PdfUA, Protection
	//
	//   Text fields : actual value or "n/a" if missing.
	//   Flag fields : 1 = present, -1 = absent. 0 reserved for unknown/unparseable.
	//   Protection  : none, restricted, accessibility blocked, password to open
	//                 (PdfProtection). The last column, so a log without it
	//                 reads with it empty; PdfUA stays the last flag column.
	//
	// Output: 18-pdf-remediation_{semicolon,comma}.csv — one row per gap per PDF,
	//   priority-ordered, columns: PageUrl, Priority, Effort, Gap, Action.
	//   Priority 1 = fix first (low effort), 12 = fix last (PDF/UA composite target).
	//   Multiple rows per PDF — filter by Priority or Effort in Excel.
	//
	// IssueTracking: one row per PDF with at least one gap.
	//   Word    = comma-separated gap types (PDF_NO_TITLE, PDF_NO_LANGUAGE, etc.)
	//   Excerpt = document title (makes the ticket identifiable)
	//
	// Format: dual-locale CSV (semicolon + comma variants), BOM, RFC-4180 quoted
	// via IssueLogWriter.WriteCsvPair — so PDF metadata values containing a
	// delimiter or quote are preserved verbatim rather than corrupting the row.
	// ─────────────────────────────────────────────────────────────────────────

	public static class PdfQualityAnalyzer
	{
		private static readonly string?[] HeaderFields =
			["PageUrl", "Title", "Description", "Keywords", "Language", "Tags", "PdfA",
			 "StructTree", "RoleMap", "Outlines", "AltText", "FormFields", "PdfUA", "Protection"];

		private static readonly string?[] RemediationHeaderFields =
			["PageUrl", "Priority", "Effort", "Gap", "Action"];

		// ── Public entry point ────────────────────────────────────────────────

		public static List<IssueTracking.IssueRecord> Analyse(
			string downloadDirectory,
			string pdfQualityCsvBasePath,
			string pdfRemediationCsvBasePath)
		{
			var results = new List<PdfResult>();

			if (!Directory.Exists(downloadDirectory))
			{
				Logger.LogInfo("PdfQualityAnalyzer: download directory not found, skipping.");
				return [];
			}

			var pdfFiles = CrawlerSuite.Core.Host.FolderFiles.Sorted(downloadDirectory, "*.pdf",
				SearchOption.TopDirectoryOnly);

			if (pdfFiles.Length == 0)
			{
				Logger.LogInfo("PdfQualityAnalyzer: no PDF files found.");
				WriteLog(pdfQualityCsvBasePath, results);
				return [];
			}

			Logger.LogInfo($"PdfQualityAnalyzer: checking {pdfFiles.Length} PDF file(s).");

			RunUi.StepTotal(pdfFiles.Length);
			foreach (var file in pdfFiles)
			{
				RunUi.StepAdvance();
				var filename = Path.GetFileName(file);
				var pageUrl = CrawlIndex.LookUpUrlForFile(filename);
				if (string.IsNullOrEmpty(pageUrl) || pageUrl == "error")
				{
					pageUrl = filename;
				}

				try
				{
					results.Add(CheckPdf(file, pageUrl));
				}
				catch (Exception ex)
				{
					Logger.LogWarning($"PdfQualityAnalyzer: could not read {filename}: {ex.Message}");
				}
			}

			WriteLog(pdfQualityCsvBasePath, results);
			WriteRemediationLog(pdfRemediationCsvBasePath, results);

			var issueCount = results.Count(r => r.HasGaps);
			Logger.LogInfo($"PdfQualityAnalyzer: {results.Count} PDF(s) checked, " +
				$"{issueCount} with quality gap(s). See {Path.GetFileName(pdfQualityCsvBasePath)}" +
				$"{IssueLogWriter.CsvSemicolonSuffix} / {Path.GetFileName(pdfQualityCsvBasePath)}{IssueLogWriter.CsvCommaSuffix}.");

			return results
				.Where(r => r.HasGaps)
				.Select(r => new IssueTracking.IssueRecord
				{
					Type = "PDFQUALITY",
					Url = r.PageUrl,
					Word = r.GapSummary,
					SourceLabel = "PDF metadata",
					Excerpt = r.Title == "n/a" ? r.PageUrl : r.Title,
				})
				.ToList();
		}

		// ── Result model ──────────────────────────────────────────────────────

		public record PdfResult(
			string PageUrl,
			string Title,
			string Description,
			string Keywords,
			string Language,
			int Tags,
			int PdfA,
			int StructTree,
			int RoleMap,
			int Outlines,
			int AltText,
			int FormFields,
			int PdfUA,
			Pdf.PdfProtectionKind Protection = Pdf.PdfProtectionKind.None)
		{
			// [KEEP] 0 = present but empty/unparseable — treated as a gap same as -1.
			// 1 = present and valid, -1 = absent, 0 = unknown/malformed.
			public bool HasGaps =>
				Protection is Pdf.PdfProtectionKind.AccessibilityBlocked or Pdf.PdfProtectionKind.PasswordToOpen ||
				Title == "n/a" || Description == "n/a" || Keywords == "n/a" ||
				Language == "n/a" || Tags <= 0 || PdfA <= 0 || PdfUA <= 0 ||
				StructTree <= 0 || Outlines <= 0 || AltText <= 0;

			public string GapSummary
			{
				get
				{
					var gaps = new List<string>();

					// A restriction protects nothing on a public server; what counts is what it
					// breaks: a password locks every reader out, a blocked extraction the screen reader.
					if (Protection == Pdf.PdfProtectionKind.PasswordToOpen)
					{
						gaps.Add("PDF_PASSWORD_TO_OPEN");
					}

					if (Protection == Pdf.PdfProtectionKind.AccessibilityBlocked)
					{
						gaps.Add("PDF_ACCESSIBILITY_BLOCKED");
					}

					if (Title == "n/a")
					{
						gaps.Add("PDF_NO_TITLE");
					}

					if (Description == "n/a")
					{
						gaps.Add("PDF_NO_DESCRIPTION");
					}

					if (Keywords == "n/a")
					{
						gaps.Add("PDF_NO_KEYWORDS");
					}

					if (Language == "n/a")
					{
						gaps.Add("PDF_NO_LANGUAGE");
					}

					if (Tags <= 0)
					{
						gaps.Add("PDF_NO_TAGS");
					}

					if (StructTree <= 0)
					{
						gaps.Add("PDF_NO_STRUCTTREE");
					}

					// A RoleMap is needed only for tag names of a PDF's own; its absence is shown in
					// log 17 but is no gap.
					if (Outlines <= 0)
					{
						gaps.Add("PDF_NO_OUTLINES");
					}

					if (AltText <= 0)
					{
						gaps.Add("PDF_NO_ALTTEXT");
					}

					if (FormFields == 0)
					{
						gaps.Add("PDF_NO_FORMFIELD_NAMES");
					}

					if (PdfA <= 0)
					{
						gaps.Add("PDF_NO_PDFA");
					}

					if (PdfUA <= 0)
					{
						gaps.Add("PDF_NO_PDFUA");
					}

					return string.Join(", ", gaps);
				}
			}

			// Field order mirrors HeaderFields. Ints render via invariant interpolation;
			// RFC-4180 quoting in WriteCsvPair
			// protects any delimiter/quote in the string fields (PDF metadata).
			public string?[] ToFields() =>
				new string?[]
				{
					PageUrl, Title, Description, Keywords, Language, $"{Tags}", $"{PdfA}",
					$"{StructTree}", $"{RoleMap}", $"{Outlines}", $"{AltText}", $"{FormFields}", $"{PdfUA}",
					Pdf.PdfProtection.Label(Protection)
				};
		}

		// ── PDF checker ───────────────────────────────────────────────────────

		internal static PdfResult CheckPdf(string filePath, string pageUrl)
		{
			var bytes = CrawlerSuite.Core.Host.WorkFile.ReadAllBytes(filePath);
			return CheckPdfBytes(bytes, pageUrl);
		}

		internal static PdfResult CheckPdfBytes(byte[] bytes, string pageUrl)
		{
			// [KEEP] The full PDF is read as Latin-1 to safely locate ASCII markers
			// (<?xpacket, /MarkInfo etc.) without corrupting binary data. Latin-1 is
			// a lossless byte-to-char mapping for arbitrary binary content.
			// XMP is always UTF-8 — once we locate the XMP byte boundaries we decode
			// that segment separately as UTF-8 to correctly render non-ASCII chars
			// (e.g. German umlauts stored as multi-byte UTF-8 sequences in XMP).
			var fileText = Encoding.Latin1.GetString(bytes);

			// PDF 1.5+ packs the catalog and the structure tree into compressed object streams; they
			// are unpacked and searched with the rest, or a tagged PDF reads as untagged.
			var latin1Text = fileText + PdfObjects.UnpackObjectStreams(bytes, fileText);

			// The document's own catalog and Info dictionary, as the trailer names them: the first
			// "/Title (" in a file may be a bookmark's, the first XMP packet an embedded image's, the
			// first "/Lang" a paragraph's. When they cannot be found, the plain search stands.
			var catalog = PdfObjects.TrailerReference(latin1Text, "/Root") is { } root ? PdfObjects.Find(latin1Text, root)?.Body : null;
			var info = PdfObjects.TrailerReference(latin1Text, "/Info") is { } infoRef ? PdfObjects.Find(latin1Text, infoRef)?.Body : null;
			var infoText = info ?? latin1Text;

			var xmp = DocumentXmp(bytes, fileText, catalog) ?? ExtractXmpAsUtf8(bytes, fileText);

			var title = SanitizeValue(xmp != null
				? ExtractXmpValue(xmp, "dc:title", "rdf:li", "rdf:Alt") ?? ExtractInfoValue(infoText, "/Title")
				: ExtractInfoValue(infoText, "/Title"));

			var description = SanitizeValue(xmp != null
				? ExtractXmpValue(xmp, "dc:description", "rdf:li", "rdf:Alt") ?? ExtractInfoValue(infoText, "/Subject")
				: ExtractInfoValue(infoText, "/Subject"));

			// Keywords: try dc:subject (XMP), then pdf:Keywords (XMP simple element),
			// then /Keywords from Info dictionary. pdf:Keywords is widely used by
			// Adobe-based generators that omit dc:subject.
			var keywordsRaw = (xmp != null
				? ExtractXmpValue(xmp, "dc:subject", "rdf:li", "rdf:Bag")
					?? ExtractXmpAttribute(xmp, "pdf:Keywords")
				: null)
				?? ExtractInfoValue(infoText, "/Keywords");
			var keywords = SanitizeValue(keywordsRaw);

			// Language: try XMP dc:language first, then always fall back to /Lang
			// in the PDF document catalog (Latin-1 text). Most PDFs store language
			// in the catalog rather than XMP dc:language, so the fallback is critical.
			var languageRaw = (xmp != null
				? ExtractXmpValue(xmp, "dc:language", "rdf:li", "rdf:Bag")
				: null)
				?? (catalog is not null ? ExtractInfoValue(catalog, "/Lang") : null)
				?? ExtractInfoValue(latin1Text, "/Lang");
			var language = SanitizeValue(languageRaw);

			var pdfaConformance = xmp != null ? ExtractXmpAttribute(xmp, "pdfaid:conformance") : null;
			var pdfaPart = xmp != null ? ExtractXmpAttribute(xmp, "pdfaid:part") : null;
			var pdfA = (!string.IsNullOrWhiteSpace(pdfaConformance) ||
						  !string.IsNullOrWhiteSpace(pdfaPart)) ? 1 : -1;

			var pdfuaPart = xmp != null ? ExtractXmpAttribute(xmp, "pdfuaid:part") : null;
			var pdfUA = !string.IsNullOrWhiteSpace(pdfuaPart) ? 1 : -1;

			var tags = CheckTagged(latin1Text) ? 1 : -1;
			var structTree = CheckStructTree(latin1Text) ? 1 : -1;
			var roleMap = CheckRoleMap(latin1Text) ? 1 : -1;
			var outlines = CheckOutlines(latin1Text) ? 1 : -1;
			var altText = CheckAltText(latin1Text) ? 1 : -1;
			var formFields = CheckFormFields(latin1Text);
			var protection = Pdf.PdfProtection.Of(fileText);

			return new PdfResult(
				PageUrl: pageUrl,
				Title: string.IsNullOrWhiteSpace(title) ? "n/a" : title,
				Description: string.IsNullOrWhiteSpace(description) ? "n/a" : description,
				Keywords: string.IsNullOrWhiteSpace(keywords) ? "n/a" : keywords,
				Language: string.IsNullOrWhiteSpace(language) ? "n/a" : language,
				Tags: tags,
				PdfA: pdfA,
				StructTree: structTree,
				RoleMap: roleMap,
				Outlines: outlines,
				AltText: altText,
				FormFields: formFields,
				PdfUA: pdfUA,
				Protection: protection);
		}

		// ── XMP extraction ────────────────────────────────────────────────────

		/// <summary>The XMP the catalog names (/Metadata), decoded as UTF-8; null when there is none or it cannot be read.</summary>
		private static string? DocumentXmp(byte[] bytes, string fileText, string? catalog)
		{
			if (catalog is null || PdfObjects.Reference(catalog, "/Metadata") is not { } metadata
				|| PdfObjects.StreamOf(bytes, fileText, metadata) is not { Length: > 0 } data)
			{
				return null;
			}

			var text = Encoding.UTF8.GetString(data);
			return text.Contains("<x:xmpmeta", StringComparison.Ordinal) || text.Contains("<rdf:RDF", StringComparison.Ordinal) ? text : null;
		}

		/// <summary>
		/// Locates the XMP packet in the raw PDF bytes using ASCII marker strings,
		/// then decodes that byte range as UTF-8 to correctly render non-ASCII chars.
		/// [KEEP] XMP is always UTF-8 per the XMP specification. The surrounding PDF
		/// binary is read as Latin-1 only to find the ASCII packet boundaries.
		/// </summary>
		internal static string? ExtractXmpAsUtf8(byte[] pdfBytes, string latin1Text)
		{
			var start = latin1Text.IndexOf("<?xpacket begin", StringComparison.Ordinal);
			if (start < 0)
			{
				return null;
			}

			var end = latin1Text.IndexOf("<?xpacket end", start, StringComparison.Ordinal);
			if (end < 0)
			{
				return null;
			}

			var endClose = latin1Text.IndexOf("?>", end, StringComparison.Ordinal);
			if (endClose < 0)
			{
				return null;
			}
			// Decode the XMP byte range as UTF-8.
			return Encoding.UTF8.GetString(pdfBytes, start, endClose + 2 - start);
		}

		/// <summary>
		/// Latin-1 string extraction — used in unit tests where byte-level
		/// UTF-8 decoding is not required.
		/// </summary>
		internal static string? ExtractXmp(string pdfText)
		{
			var start = pdfText.IndexOf("<?xpacket begin", StringComparison.Ordinal);
			if (start < 0)
			{
				return null;
			}

			var end = pdfText.IndexOf("<?xpacket end", start, StringComparison.Ordinal);
			if (end < 0)
			{
				return null;
			}

			var endClose = pdfText.IndexOf("?>", end, StringComparison.Ordinal);
			if (endClose < 0)
			{
				return null;
			}

			return pdfText[start..(endClose + 2)];
		}

		/// <summary>
		/// Cleans an extracted metadata string: strips any residual XML tags and
		/// normalizes whitespace; delimiter characters are kept (the CSV writer
		/// quotes them).
		/// Rejects strings that are predominantly non-printable (binary garbage
		/// from accidental pattern matches inside PDF binary streams).
		/// </summary>
		private static string SanitizeValue(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				return string.Empty;
			}
			// Reject binary garbage: require at least 80% printable ASCII characters.
			// PDF binary streams can contain accidental matches for metadata keys.
			var printable = raw.Count(c => c >= 32 && c <= 126);
			if (raw.Length > 0 && (double)printable / raw.Length < 0.8)
			{
				return string.Empty;
			}
			// Strip any XML tags that leaked through.
			var stripped = System.Text.RegularExpressions.Regex.Replace(raw, @"<[^>]+>", " ");
			// Normalize whitespace (collapse newlines and multiple spaces).
			stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+", " ").Trim();
			// Values are RFC-4180-quoted at write time (WriteCsvPair), so a pipe,
			// semicolon, or comma in a value is preserved as-is.
			return stripped.Trim();
		}

		internal static string? ExtractXmpValue(string xmp, string element,
			string childElement, string containerElement)
		{
			var elemTag = $"<{element}>";
			var elemClose = $"</{element}>";
			var elemIdx = xmp.IndexOf(elemTag, StringComparison.OrdinalIgnoreCase);
			if (elemIdx < 0)
			{
				return null;
			}

			var elemEndIdx = xmp.IndexOf(elemClose, elemIdx, StringComparison.OrdinalIgnoreCase);

			// [KEEP] Container/child form: <dc:title><rdf:Alt><rdf:li>value</rdf:li></rdf:Alt></dc:title>
			// Some XMP serialisers emit the container BEFORE the element tag — search the
			// full element span in both directions to be order-independent.
			var containerTag = $"<{containerElement}>";
			var containerIdx = xmp.IndexOf(containerTag, elemIdx, StringComparison.OrdinalIgnoreCase);

			// Container must be inside the element span (or element span is unclosed).
			if (containerIdx >= 0 && (elemEndIdx < 0 || containerIdx < elemEndIdx))
			{
				var liOpenStart = xmp.IndexOf($"<{childElement}", containerIdx,
					StringComparison.OrdinalIgnoreCase);
				if (liOpenStart >= 0)
				{
					var liOpenEnd = xmp.IndexOf('>', liOpenStart);
					if (liOpenEnd >= 0)
					{
						// Self-closing tag <rdf:li .../> has no content — treat as empty.
						if (xmp[liOpenEnd - 1] == '/')
						{
							return null;
						}

						var liClose = xmp.IndexOf($"</{childElement}>", liOpenStart,
							StringComparison.OrdinalIgnoreCase);
						if (liClose > liOpenEnd)
						{
							return xmp[(liOpenEnd + 1)..liClose].Trim();
						}
					}
				}
			}
			else if (elemEndIdx > elemIdx)
			{
				// [KEEP] Simple/inline element form: <dc:title>value</dc:title>
				// Reached when the element exists but has no container (rdf:Alt/rdf:Bag).
				// Unusual in well-formed XMP but occurs in some PDF generators that
				// emit simplified XMP without the full RDF container structure.
				var inner = xmp[(elemIdx + elemTag.Length)..elemEndIdx].Trim();
				// Guard against tag-soup from out-of-order container falling through.
				// If the inner text contains XML tags, it's not a simple value.
				if (inner.Contains('<'))
				{
					return null;
				}

				return inner;
			}

			return null;
		}

		internal static string? ExtractXmpAttribute(string xmp, string attribute)
		{
			var match = Regex.Match(xmp,
				$"{attribute}=[\"']([^\"']+)[\"']",
				RegexOptions.IgnoreCase);
			if (match.Success)
			{
				return match.Groups[1].Value.Trim();
			}

			var tagStart = xmp.IndexOf($"<{attribute}>", StringComparison.OrdinalIgnoreCase);
			if (tagStart >= 0)
			{
				var tagEnd = xmp.IndexOf($"</{attribute}>", tagStart,
					StringComparison.OrdinalIgnoreCase);
				if (tagEnd > tagStart)
				{
					return xmp[(tagStart + attribute.Length + 2)..tagEnd].Trim();
				}
			}

			return null;
		}

		// ── Info dictionary ───────────────────────────────────────────────────

		internal static string? ExtractInfoValue(string pdfText, string key)
		{
			// [KEEP] PDF literal strings use \) to escape a literal ) character.
			// The pattern allows \) inside the string to avoid silent truncation.
			// Capture group replaces \) with ) after matching.
			var literalMatch = Regex.Match(pdfText,
				$@"{Regex.Escape(key)}\s*\(((?:[^\\)\r\n]|\\.)*)\)");
			if (literalMatch.Success)
			{
				return DecodeTextString(LiteralBytes(literalMatch.Groups[1].Value)).Trim();
			}

			var hexMatch = Regex.Match(pdfText,
				$@"{Regex.Escape(key)}\s*<([0-9A-Fa-f]+)>");
			if (hexMatch.Success)
			{
				try
				{
					var hex = hexMatch.Groups[1].Value;
					var bytes = Enumerable.Range(0, hex.Length / 2)
						.Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
						.ToArray();
					return DecodeTextString(bytes).Trim();
				}
				catch { }
			}

			return null;
		}

		/// <summary>
		/// The bytes of a PDF literal string's inside (read as Latin-1, one char per byte), with its
		/// escapes resolved: \n \r \t \b \f \( \) \\, an octal \ddd and a backslash before a line end.
		/// </summary>
		internal static byte[] LiteralBytes(string inner)
		{
			var bytes = new List<byte>(inner.Length);
			for (int i = 0; i < inner.Length; i++)
			{
				char c = inner[i];
				if (c != '\\' || i + 1 >= inner.Length)
				{
					bytes.Add((byte)c);
					continue;
				}

				char e = inner[++i];
				switch (e)
				{
					case 'n': bytes.Add((byte)'\n'); break;
					case 'r': bytes.Add((byte)'\r'); break;
					case 't': bytes.Add((byte)'\t'); break;
					case 'b': bytes.Add((byte)'\b'); break;
					case 'f': bytes.Add((byte)'\f'); break;
					case '\r':
						if (i + 1 < inner.Length && inner[i + 1] == '\n')
						{
							i++;
						}

						break;
					case '\n':
						break;
					case >= '0' and <= '7':
						int value = e - '0';
						for (int k = 0; k < 2 && i + 1 < inner.Length && inner[i + 1] is >= '0' and <= '7'; k++)
						{
							value = value * 8 + (inner[++i] - '0');
						}

						bytes.Add((byte)(value & 0xFF));
						break;
					default:
						bytes.Add((byte)e); // \( \) \\ and any other: the character itself
						break;
				}
			}

			return [.. bytes];
		}

		/// <summary>
		/// A PDF text string as the standard reads it: UTF-16BE after FE FF, UTF-8 after EF BB BF
		/// (PDF 2.0), otherwise PDFDocEncoding — read here as Latin-1, which it matches for letters.
		/// Bytes a producer wrote as UTF-8 without the marker are read the way PDF readers read them.
		/// </summary>
		internal static string DecodeTextString(byte[] bytes)
		{
			if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
			{
				return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
			}

			if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
			{
				return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
			}

			return Encoding.Latin1.GetString(bytes);
		}

		// ── Tagged PDF check ──────────────────────────────────────────────────

		internal static bool CheckTagged(string pdfText)
		{
			var idx = pdfText.IndexOf("/MarkInfo", StringComparison.Ordinal);
			if (idx < 0)
			{
				return false;
			}

			var window = pdfText.Substring(idx, Math.Min(200, pdfText.Length - idx));
			// [KEEP] OrdinalIgnoreCase covers both "true" and "True", so no separate
			// Ordinal check is needed. PDF spec uses /Marked true (lowercase).
			return window.Contains("/Marked true", StringComparison.OrdinalIgnoreCase);
		}

		// ── Structural checks ────────────────────────────────────────────────────

		/// <summary>
		/// Checks for /StructTreeRoot — indicates a logical document structure tree
		/// is present, required for proper reading order and accessibility.
		/// </summary>
		internal static bool CheckStructTree(string pdfText) =>
			pdfText.Contains("/StructTreeRoot", StringComparison.Ordinal);

		/// <summary>
		/// Checks for /RoleMap — maps custom PDF tag names to standard role names.
		/// Required when the document uses non-standard tag names.
		/// </summary>
		internal static bool CheckRoleMap(string pdfText) =>
			pdfText.Contains("/RoleMap", StringComparison.Ordinal);

		/// <summary>
		/// Checks for /Outlines with at least one child entry — PDF bookmarks that
		/// allow users to navigate long documents. /Outlines alone (empty) does not count.
		///
		/// [KEEP] The outline tree root is often an indirect reference (/Outlines N M R),
		/// with /Count and /First stored in the referenced object rather than inline.
		/// We therefore search the full document for /First paired with a positive /Count
		/// anywhere after an /Outlines key — covers both inline and indirect forms.
		/// </summary>
		internal static bool CheckOutlines(string pdfText)
		{
			// Must have /Outlines at all.
			if (!pdfText.Contains("/Outlines", StringComparison.Ordinal))
			{
				return false;
			}

			// [KEEP] Scan for /Count N (positive) with /First nearby — the signature of
			// a non-empty outline tree. Avoids full obj-block regex on large PDFs which
			// can cause catastrophic backtracking. Instead: find each /Count occurrence,
			// check its value is positive, then verify /First appears within 200 chars.
			var countMatches = RegexWalk.Each(pdfText, @"/Count\s+(\d+)");
			foreach (Match cm in countMatches)
			{
				if (!int.TryParse(cm.Groups[1].Value, out var count) || count <= 0)
				{
					continue;
				}

				var start = Math.Max(0, cm.Index - 100);
				var len = Math.Min(300, pdfText.Length - start);
				var vicinity = pdfText.Substring(start, len);
				if (vicinity.Contains("/First", StringComparison.Ordinal))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Checks for at least one /Alt entry — alt text on figure or formula elements.
		/// Presence indicates the document has some alt text; absence means none at all.
		/// [KEEP] Cannot verify completeness (every image has alt text) without full
		/// structure tree traversal — this is a signal, not a guarantee.
		/// </summary>
		internal static bool CheckAltText(string pdfText) => AltEntry.IsMatch(pdfText);

		// "/Alt (…)", "/Alt(…)", "/Alt <…>", "/Alt<…>": PDF allows white space or none before a string.
		private static readonly Regex AltEntry = new(@"/Alt\s*[(<]", RegexOptions.Compiled);
		private static readonly Regex TuEntry = new(@"/TU\s*[(<]", RegexOptions.Compiled);

		/// <summary>
		/// Checks for interactive form fields (/AcroForm) and whether they have
		/// accessible names (/TU = tooltip/user-facing name).
		/// Returns:
		///   -1 = no AcroForm (no forms, no issue)
		///    0 = AcroForm present but no /TU entries (forms lack accessible names)
		///    1 = AcroForm present and at least one /TU entry found
		/// </summary>
		internal static int CheckFormFields(string pdfText)
		{
			if (!pdfText.Contains("/AcroForm", StringComparison.Ordinal))
			{
				return -1;
			}

			return TuEntry.IsMatch(pdfText) ? 1 : 0;
		}

		// ── Log writer ────────────────────────────────────────────────────────

		private static void WriteLog(string csvBasePath, List<PdfResult> results)
		{
			var records = new List<string?[]> { HeaderFields };
			records.AddRange(results.OrderBy(r => r.PageUrl, StringComparer.Ordinal).Select(r => r.ToFields()));
			IssueLogWriter.WriteCsvPair(csvBasePath, records);
		}

		// ── Remediation log writer ────────────────────────────────────────────

		/// <summary>
		/// Writes 18-pdf-remediation_{semicolon,comma}.csv — one row per gap per PDF,
		/// priority-ordered
		/// from simplest fix (metadata fields) to hardest (full PDF/UA compliance).
		/// PDF_NO_PDFUA is always the last row so the compliance target is always
		/// visible and filterable even when all other gaps are resolved.
		/// </summary>
		internal static void WriteRemediationLogPublic(List<PdfResult> results, string csvBasePath) =>
			WriteRemediationLog(csvBasePath, results);

		private static void WriteRemediationLog(string csvBasePath, List<PdfResult> results)
		{
			// Priority | Effort | Gap code | Action
			var ladder = new (int Priority, string Effort, string Gap, string Action)[]
			{
				(0,  "low",    "PDF_PASSWORD_TO_OPEN",   "Needs a password to open, so nobody can read it: probably not meant to be public — take it off the site, or publish it without a password"),
				(0,  "low",    "PDF_ACCESSIBILITY_BLOCKED", "Restricted so that assistive technology may not extract the content (PDF/UA-1, 7.16): remove the restrictions, or allow content copying for accessibility"),
				(1,  "low",    "PDF_NO_LANGUAGE",        "Set document language in PDF properties or export settings"),
				(2,  "low",    "PDF_NO_TITLE",            "Set document title in PDF properties or export settings"),
				(3,  "low",    "PDF_NO_DESCRIPTION",      "Set document description/subject in PDF properties"),
				(4,  "low",    "PDF_NO_KEYWORDS",         "Set document keywords in PDF properties"),
				(5,  "low",    "PDF_NO_OUTLINES",         "Add bookmarks/outline structure for navigation"),
				(6,  "medium", "PDF_NO_TAGS",             "Re-export from source with PDF tagging enabled"),
				(7,  "medium", "PDF_NO_PDFA",             "Re-export with PDF/A conformance enabled"),
				(8,  "high",   "PDF_NO_ALTTEXT",          "Add alt text to all images in the source document"),
				(9,  "high",   "PDF_NO_STRUCTTREE",       "Use an accessible authoring tool or PDF remediation tool to add structure tree"),
				(11, "high",   "PDF_NO_FORMFIELD_NAMES",  "Add accessible names (tooltips) to all interactive form fields"),
				(12, "high",   "PDF_NO_PDFUA",            "Full PDF/UA remediation — resolve all gaps above first"),
			};

			var records = new List<string?[]> { RemediationHeaderFields };
			foreach (var result in results.OrderBy(r => r.PageUrl, StringComparer.Ordinal))
			{
				var gaps = result.GapSummary
					.Split(", ", StringSplitOptions.RemoveEmptyEntries)
					.ToHashSet(StringComparer.OrdinalIgnoreCase);

				if (gaps.Count == 0)
				{
					continue;
				}

				foreach (var (priority, effort, gap, action) in ladder)
				{
					// PDF_NO_PDFUA always appears as the last row when the PDF lacks
					// PDF/UA — even if it is the only gap — so it is always filterable.
					if (!gaps.Contains(gap))
					{
						continue;
					}

					records.Add(new string?[] { result.PageUrl, $"{priority}", effort, gap, action });
				}
			}
			IssueLogWriter.WriteCsvPair(csvBasePath, records);
		}
	}
}
