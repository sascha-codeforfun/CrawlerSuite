using System.IO.Compression;
using System.Text;
using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>PDFs compared by what they carry, not by how the file lays it out.</summary>
	public class PdfContentTests
	{
		private sealed record Obj(int Number, string Dict, string? Stream = null, int Level = 6);

		private static byte[] Deflate(string text, int level)
		{
			using var output = new MemoryStream();
			using (var z = new ZLibStream(output, level >= 9 ? CompressionLevel.SmallestSize : level <= 1 ? CompressionLevel.Fastest : CompressionLevel.Optimal))
			{
				z.Write(Encoding.Latin1.GetBytes(text));
			}

			return output.ToArray();
		}

		/// <summary>A small PDF: the objects in the order given, streams deflated at their level, then the trailer.</summary>
		private static byte[] Pdf(string trailer, params Obj[] objects)
		{
			using var output = new MemoryStream();
			void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));
			Write("%PDF-1.7\n");
			foreach (var o in objects)
			{
				if (o.Stream is null)
				{
					Write($"{o.Number} 0 obj\n{o.Dict}\nendobj\n");
					continue;
				}

				var data = Deflate(o.Stream, o.Level);
				Write($"{o.Number} 0 obj\n<< {o.Dict} /Filter /FlateDecode /Length {data.Length} >>\nstream\n");
				output.Write(data);
				Write("\nendstream\nendobj\n");
			}

			Write($"trailer\n{trailer}\n%%EOF\n");
			return output.ToArray();
		}

		private const string Content = "BT /F1 12 Tf 10 50 Td (ice) Tj ET";

		/// <summary>The same page, its objects numbered from <paramref name="first"/>.</summary>
		private static byte[] Page(int first = 1, string content = Content, int level = 6, string tag = "ABCDEF", string mod = "D:20260101000000Z",
			string producer = "Writer 1.0", string id = "00112233445566778899AABBCCDDEEFF", int pages = 1)
		{
			int catalog = first, tree = first + 1, page = first + 2, stream = first + 3, font = first + 4, info = first + 5;
			var kids = string.Join(" ", Enumerable.Repeat($"{page} 0 R", pages));
			var objects = new List<Obj>
			{
				new(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>"),
				new(tree, $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>"),
			};
			for (int i = 0; i < pages; i++)
			{
				objects.Add(new(page, $"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 200 100] /Contents {stream} 0 R /Resources << /Font << /F1 {font} 0 R >> >> >>"));
			}

			objects.Add(new(stream, string.Empty, content, level));
			objects.Add(new(font, $"<< /Type /Font /Subtype /TrueType /BaseFont /{tag}+Frost >>"));
			objects.Add(new(info, $"<< /Title (Ice) /ModDate ({mod}) /Producer ({producer}) /CreationDate (D:20250101000000Z) >>"));
			return Pdf($"<< /Root {catalog} 0 R /Info {info} 0 R /ID [<{id}><{id}>] >>", [.. objects]);
		}

		private static PdfDifference Compare(byte[] a, byte[] b) => PdfContent.Compare(PdfContent.Read(a), PdfContent.Read(b));

		[Fact]
		public void TheSameFile_IsTheSameContent()
		{
			var d = Compare(Page(), Page());
			Assert.True(d.SameContent);
			Assert.Empty(d.ChangedStamps);
			Assert.Equal((1, 1), (d.PagesA, d.PagesB));
		}

		[Fact]
		public void WrittenAgain_NewStampsNumbersTagsAndCompression_IsTheSameContent()
		{
			var a = Page();
			var b = Page(first: 40, level: 9, tag: "QWERTY", mod: "D:20260214093000Z", producer: "Writer 1.1", id: "FFEEDDCCBBAA99887766554433221100");
			Assert.NotEqual(a.Length, b.Length);

			var d = Compare(a, b);
			Assert.True(d.SameContent);
			Assert.Equal(["/ModDate", "/Producer", "/ID"], d.ChangedStamps);
		}

		[Fact]
		public void AChangedWord_IsNotTheSameContent()
		{
			var d = Compare(Page(), Page(content: "BT /F1 12 Tf 10 50 Td (water) Tj ET"));
			Assert.False(d.SameContent);
			Assert.Equal((1, 1), (d.OnlyA, d.OnlyB));
		}

		[Fact]
		public void AnotherPageCount_IsNotTheSameContent()
		{
			var d = Compare(Page(), Page(pages: 2));
			Assert.False(d.SameContent);
			Assert.Equal((1, 2), (d.PagesA, d.PagesB));
		}

		[Fact]
		public void ObjectsPackedIntoAnObjectStream_AreReadAsPlainObjects()
		{
			var plain = Page();

			// The same objects, but the catalog, page tree, page, font and info packed into an object stream.
			string[] packed =
			[
				"<< /Type /Catalog /Pages 2 0 R >>",
				"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
				"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
				"<< /Type /Font /Subtype /TrueType /BaseFont /ZZZZZZ+Frost >>",
				"<< /Title (Ice) /ModDate (D:20260214000000Z) /Producer (Writer 2) /CreationDate (D:20250101000000Z) >>",
			];
			int[] numbers = [1, 2, 3, 5, 6];
			var body = new StringBuilder();
			var header = new StringBuilder();
			for (int i = 0; i < packed.Length; i++)
			{
				header.Append(numbers[i]).Append(' ').Append(body.Length).Append(' ');
				body.Append(packed[i]).Append('\n');
			}

			var objStm = header.ToString() + body;
			var file = Pdf("<< /Type /XRef /Root 1 0 R /Info 6 0 R >>",
				new Obj(4, string.Empty, Content),
				new Obj(7, $"/Type /ObjStm /N {packed.Length} /First {header.Length}", objStm));

			var d = Compare(plain, file);
			Assert.True(d.SameContent);
			Assert.Contains("/ModDate", d.ChangedStamps);
		}

		[Fact]
		public void AnEncryptedPdf_IsNeverTheSameContent()
		{
			var encrypted = Pdf("<< /Root 1 0 R /Encrypt 9 0 R >>",
				new Obj(1, "<< /Type /Catalog >>"),
				new Obj(9, "<< /Filter /Standard /V 1 /R 2 /O <00> /U <00> /P -4 >>"));
			var d = Compare(encrypted, encrypted);
			Assert.False(d.SameContent);
			Assert.True(d.EncryptedA);
			Assert.Contains("encrypted", d.Lines()[0]);
		}

		[Fact]
		public void IsPdf_AcceptsTheHeaderInTheFirstKilobyte()
		{
			Assert.True(PdfContent.IsPdf("%PDF-1.4\n"u8));
			Assert.True(PdfContent.IsPdf(Encoding.Latin1.GetBytes(new string(' ', 100) + "%PDF-1.7")));
			Assert.False(PdfContent.IsPdf("<html>ice</html>"u8));
		}

		[Fact]
		public void Rows_ShowPagesPartsAndStamps_DatesReadable_ChangesMarked()
		{
			var rows = Compare(Page(), Page(mod: "D:20260214093000+01'00'", producer: "Writer 1.1")).Rows();
			var byLabel = rows.ToDictionary(r => r.Label);
			Assert.False(byLabel["Pages"].Differs);
			Assert.False(byLabel["Content parts"].Differs);
			Assert.True(byLabel["Modified"].Differs);
			Assert.Equal("2026-01-01 00:00:00 UTC", byLabel["Modified"].A);
			Assert.Equal("2026-02-14 09:30:00 +01:00", byLabel["Modified"].B);
			Assert.Equal(("Writer 1.0", "Writer 1.1"), (byLabel["Producer"].A, byLabel["Producer"].B));
			Assert.False(byLabel["Created"].Differs);
		}

		[Fact]
		public void PartsOnlyOneSideHas_AreDescribed()
		{
			var d = Compare(Page(), Page(content: "BT /F1 12 Tf 10 50 Td (water) Tj ET"));
			var onlyB = Assert.Single(d.PartsOnlyB!);
			Assert.Equal("page content", onlyB.Kind);
			Assert.Contains("(water) Tj", onlyB.Text);
			Assert.Equal("page content", Assert.Single(d.PartsOnlyA!).Kind);
		}

		[Fact]
		public void XmpDatesAndIds_AreRows()
		{
			string Xmp(string date, string id) =>
				$"<x:xmpmeta><rdf:RDF><rdf:Description xmp:ModifyDate=\"{date}\"><xmpMM:InstanceID>uuid:{id}</xmpMM:InstanceID></rdf:Description></rdf:RDF></x:xmpmeta>";
			byte[] With(string date, string id) => Pdf("<< /Root 1 0 R >>",
				new Obj(1, "<< /Type /Catalog /Metadata 2 0 R >>"),
				new Obj(2, "/Type /Metadata /Subtype /XML", Xmp(date, id)));
			var rows = Compare(With("2026-01-01T10:00:00+01:00", "ice-1111"), With("2026-02-14T09:30:00+01:00", "ice-2222")).Rows().ToDictionary(r => r.Label);
			Assert.True(rows["XMP modified"].Differs);
			Assert.Equal("2026-01-01 10:00:00 +01:00", rows["XMP modified"].A);
			Assert.Equal("uuid:ice-2222", rows["XMP instance ID"].B);
		}

		[Fact]
		public void Lines_SayWhatDiffers()
		{
			var same = Compare(Page(), Page(mod: "D:20260214000000Z")).Lines();
			Assert.Contains(same, l => l.StartsWith("Content: the same", StringComparison.Ordinal));
			Assert.Contains(same, l => l.Contains("/ModDate", StringComparison.Ordinal));

			var changed = Compare(Page(), Page(content: "BT (water) Tj ET")).Lines();
			Assert.Contains(changed, l => l.StartsWith("Content parts", StringComparison.Ordinal));
			Assert.Contains("Stamps: unchanged.", changed);
		}
	}
}
