using System.IO.Compression;
using System.Text;
using Xunit;

namespace CrawlerSuite.Core.Tests.Assets
{
	/// <summary>
	/// PDF 1.5+ keeps the catalog and the structure tree in compressed object streams; the check
	/// must see them, and take title, XMP and language from the document's own objects. The PDFs
	/// are built here, byte by byte.
	/// </summary>
	public class PdfModernStructureTests
	{
		private static byte[] Deflate(string text) => Deflate(Encoding.Latin1.GetBytes(text));

		private static byte[] Deflate(byte[] bytes)
		{
			using var output = new MemoryStream();
			using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
			{
				zlib.Write(bytes, 0, bytes.Length);
			}

			return output.ToArray();
		}

		private static void Add(MemoryStream pdf, string text) => pdf.Write(Encoding.Latin1.GetBytes(text));

		private static void Stream(MemoryStream pdf, int number, string dict, byte[] data)
		{
			Add(pdf, $"{number} 0 obj\n<< {dict} /Length {data.Length} >>\nstream\n");
			pdf.Write(data);
			Add(pdf, "\nendstream\nendobj\n");
		}

		private const string ImageXmp =
			"<?xpacket begin=\"\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
			"<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Foto vom Zaun</rdf:li></rdf:Alt></dc:title>" +
			"</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

		private const string DocumentXmp =
			"<?xpacket begin=\"\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
			"<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\" pdfuaid:part=\"1\">" +
			"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">Alles über Gartenzäune</rdf:li></rdf:Alt></dc:description>" +
			"</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

		/// <summary>A tagged PDF 1.5 as Word or InDesign writes it: catalog, structure tree and bookmarks packed.</summary>
		private static byte[] Modern(bool metadataCompressed = false)
		{
			using var pdf = new MemoryStream();
			Add(pdf, "%PDF-1.7\n%âãÏÓ\n");

			// An image with its own XMP first in the file.
			Stream(pdf, 8, "/Type /Metadata /Subtype /XML", Encoding.UTF8.GetBytes(ImageXmp));

			// The document's XMP, named by the catalog.
			var xmp = Encoding.UTF8.GetBytes(DocumentXmp);
			if (metadataCompressed)
			{
				Stream(pdf, 6, "/Type /Metadata /Subtype /XML /Filter /FlateDecode", Deflate(xmp));
			}
			else
			{
				Stream(pdf, 6, "/Type /Metadata /Subtype /XML", xmp);
			}

			// Info dictionary in plain view.
			Add(pdf, "7 0 obj\n<< /Producer (Gartenwerk) /Title (Ratgeber Gartenzaun) /Keywords (Zaun, Garten) >>\nendobj\n");

			// Catalog, structure tree, an alt text, outlines and a bookmark titled before the Info title — all packed.
			string[] objects =
			[
				"<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 3 0 R /Lang (de-DE) /Outlines 4 0 R /Metadata 6 0 R >>",
				"<< /Type /Pages /Kids [] /Count 0 >>",
				"<< /Type /StructTreeRoot /K [ << /S /Figure /Alt (Ein Zaun im Garten) /Lang (en) >> ] >>",
				"<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
				"<< /Title (Kapitel eins) /Parent 4 0 R >>",
			];
			int[] numbers = [1, 2, 3, 4, 5];
			var body = new StringBuilder();
			var offsets = new List<int>();
			foreach (var o in objects)
			{
				offsets.Add(body.Length);
				body.Append(o).Append('\n');
			}

			var header = string.Join(' ', numbers.Zip(offsets, (n, off) => $"{n} {off}")) + "\n";
			var packed = Deflate(header + body);
			Stream(pdf, 9, $"/Type /ObjStm /N {objects.Length} /First {header.Length} /Filter /FlateDecode", packed);

			// Cross-reference stream with the trailer entries (its table is not needed for the check).
			Stream(pdf, 10, "/Type /XRef /Size 11 /Root 1 0 R /Info 7 0 R /W [1 2 1]", [0, 0, 0, 0]);
			Add(pdf, "startxref\n0\n%%EOF\n");
			return pdf.ToArray();
		}

		[Fact]
		public void PackedCatalogAndStructure_AreSeen()
		{
			var r = PdfQualityAnalyzer.CheckPdfBytes(Modern(), "https://example.test/a.pdf");
			Assert.Equal(1, r.Tags);
			Assert.Equal(1, r.StructTree);
			Assert.Equal(1, r.Outlines);
			Assert.Equal(1, r.AltText);
			Assert.Equal("de-DE", r.Language);
		}

		[Fact]
		public void TitleAndXmp_AreTheDocumentsOwn()
		{
			var r = PdfQualityAnalyzer.CheckPdfBytes(Modern(), "https://example.test/a.pdf");
			Assert.Equal("Ratgeber Gartenzaun", r.Title);          // the Info title, not the image's XMP nor the bookmark
			Assert.Equal("Alles über Gartenzäune", r.Description); // from the catalog's XMP
			Assert.Equal("Zaun, Garten", r.Keywords);
			Assert.Equal(1, r.PdfUA);
		}

		[Fact]
		public void ACompressedMetadataStream_IsRead()
		{
			var r = PdfQualityAnalyzer.CheckPdfBytes(Modern(metadataCompressed: true), "https://example.test/a.pdf");
			Assert.Equal("Alles über Gartenzäune", r.Description);
			Assert.Equal(1, r.PdfUA);
		}

		[Fact]
		public void AMissingRoleMap_IsNoGap()
		{
			var r = PdfQualityAnalyzer.CheckPdfBytes(Modern(), "https://example.test/a.pdf");
			Assert.Equal(-1, r.RoleMap);
			Assert.DoesNotContain("PDF_NO_ROLEMAP", r.GapSummary);
		}

		[Fact]
		public void ABrokenObjectStream_DoesNotThrow_AndThePlainSearchStands()
		{
			var bytes = Modern();
			var text = Encoding.Latin1.GetString(bytes);
			int at = text.IndexOf("/Type /ObjStm", StringComparison.Ordinal);
			int data = text.IndexOf("stream\n", at, StringComparison.Ordinal) + 7;
			for (int i = data; i < data + 8; i++)
			{
				bytes[i] = 0xFF;   // the packed data cannot inflate
			}

			var r = PdfQualityAnalyzer.CheckPdfBytes(bytes, "https://example.test/a.pdf");
			Assert.Equal(-1, r.Tags);                // what is packed cannot be seen
			Assert.Equal("Zaun, Garten", r.Keywords); // the Info dictionary is in plain view
		}

		[Fact]
		public void AClassicPdf_ReadsAsBefore()
		{
			const string classic = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /MarkInfo << /Marked true >> /StructTreeRoot 2 0 R /Lang (en-GB) >>\nendobj\n" +
				"2 0 obj\n<< /Type /StructTreeRoot /RoleMap << /Box /Div >> >>\nendobj\n" +
				"3 0 obj\n<< /Title (Fence guide) >>\nendobj\ntrailer\n<< /Root 1 0 R /Info 3 0 R >>\n%%EOF";
			var r = PdfQualityAnalyzer.CheckPdfBytes(Encoding.Latin1.GetBytes(classic), "https://example.test/b.pdf");
			Assert.Equal(1, r.Tags);
			Assert.Equal(1, r.StructTree);
			Assert.Equal(1, r.RoleMap);
			Assert.Equal("en-GB", r.Language);
			Assert.Equal("Fence guide", r.Title);
		}
	}
}
