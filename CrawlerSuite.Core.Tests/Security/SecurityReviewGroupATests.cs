using System.Collections.Generic;
using System.Linq;
using System.Text;
using CrawlerSuite.Core.AssetMetadata;
using CrawlerSuite.Core.Tests;
using Xunit;

namespace CrawlerSuite.Core.Tests.Security
{
	/// <summary>Hostile files and the connectivity check. Synthetic files only.</summary>
	public class SecurityReviewGroupATests
	{
		// ── Only the first EXIF and XMP block of a WebP or PNG ──────────────────

		[Fact]
		public void Webp_SeveralExifChunks_OnlyTheFirstIsRead()
		{
			var first = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "FirstCam") });
			var second = AssetMetadataFixtures.Tiff(little: true, ifd0: new[]
			{
				AssetMetadataFixtures.Ascii(0x010F, "SecondCam"), AssetMetadataFixtures.Ascii(0x0110, "SecondModel"),
			});
			var exif = new ExifResult();

			WebpReader.Read(AssetMetadataFixtures.Webp(("EXIF", first), ("EXIF", second)), exif, out _, new List<string>());

			Assert.Equal("FirstCam", exif.Make);
			Assert.Null(exif.Model);
		}

		[Fact]
		public void Webp_SeveralXmpChunks_OnlyTheFirstIsRead()
		{
			var first = AssetMetadataFixtures.XmpPacket("<dc:creator><rdf:Seq><rdf:li>Floe</rdf:li></rdf:Seq></dc:creator>");
			var second = AssetMetadataFixtures.XmpPacket("<dc:creator><rdf:Seq><rdf:li>Tide</rdf:li></rdf:Seq></dc:creator>");

			WebpReader.Read(AssetMetadataFixtures.Webp(("XMP ", Encoding.UTF8.GetBytes(first)), ("XMP ", Encoding.UTF8.GetBytes(second))),
				new ExifResult(), out var xmp, new List<string>());

			Assert.Contains("Floe", xmp.Creators);
			Assert.DoesNotContain("Tide", xmp.Creators);
		}

		[Fact]
		public void Png_SeveralXmpPackets_OnlyTheFirstIsRead()
		{
			var png = AssetMetadataFixtures.Png(
				("iTXt", AssetMetadataFixtures.ITxt("XML:com.adobe.xmp", AssetMetadataFixtures.XmpPacket("<dc:creator><rdf:Seq><rdf:li>Floe</rdf:li></rdf:Seq></dc:creator>"), false)),
				("iTXt", AssetMetadataFixtures.ITxt("XML:com.adobe.xmp", AssetMetadataFixtures.XmpPacket("<dc:creator><rdf:Seq><rdf:li>Tide</rdf:li></rdf:Seq></dc:creator>"), false)));

			PngReader.Read(png, new ExifResult(), out var xmp, new List<string>());

			Assert.Contains("Floe", xmp.Creators);
			Assert.DoesNotContain("Tide", xmp.Creators);
		}

		// ── The object scan stays linear without "endobj" ──────────────────────

		// Asserted on the work done, not on the clock: the dictionaries looked at add up to
		// at most the file's length. A quadratic scan of 200,000 headers is some 10^10 characters.
		[Theory]
		[InlineData("")]           // no "stream" at all
		[InlineData("stream\n")]   // one "stream" after all the headers
		public void PdfObjectScan_HeadersWithoutEndobj_FinishInOnePass(string tail)
		{
			var text = new StringBuilder("%PDF-1.7\n");
			for (int i = 1; i <= 200_000; i++)
			{
				text.Append(i).Append(" 0 obj\n");
			}

			var latin1 = text.Append(tail).ToString();
			var unpacked = PdfObjects.UnpackObjectStreams(Encoding.Latin1.GetBytes(latin1), latin1, out long work);

			Assert.Equal(string.Empty, unpacked);
			Assert.InRange(work, 0, latin1.Length);
		}

		[Fact]
		public void PackedObjects_EndAtTheirEndobj_OrTheNextHeader()
		{
			const string text = "1 0 obj <</A 1>> endobj 2 0 obj 3 0 obj <</B 2>> endobj";
			var bodies = PdfObjects.Bodies(text).Select(b => text[b.From..b.End].Trim()).ToList();

			Assert.Equal(["<</A 1>>", "", "<</B 2>>"], bodies);
		}

		[Fact]
		public void PackedObjects_ManyHeadersInOneObject_StayLinear()
		{
			var text = new StringBuilder("1 0 obj ");
			for (int i = 2; i <= 200_000; i++)
			{
				text.Append(i).Append(" 0 obj ");
			}

			var s = text.Append("endobj").ToString();
			Assert.InRange(PdfObjects.Bodies(s).Sum(b => (long)(b.End - b.From)), 0, s.Length);
		}

		[Fact]
		public void NextFrom_ReusesWhatIsStillAhead_AndRemembersNone()
		{
			const string text = "a endobj b endobj";
			Assert.Equal(2, PdfObjects.NextFrom(text, "endobj", 0, -2));
			Assert.Equal(2, PdfObjects.NextFrom(text, "endobj", 1, 2));   // still ahead: reused
			Assert.Equal(11, PdfObjects.NextFrom(text, "endobj", 9, 2));  // passed: searched again
			Assert.Equal(-1, PdfObjects.NextFrom(text, "endobj", 17, -1)); // none ahead stays none
		}

		// ── Offsets and lengths near int.MaxValue do not wrap ───────────────────

		[Fact]
		public void PdfObjectStream_HugeOffsetOrLength_IsLeftOut_NotAThrow()
		{
			// An uncompressed object stream whose header names an offset near int.MaxValue, and one whose /Length is.
			const string objects = "5 2147483647 6 0 <</A 1>>";
			var latin1 =
				"%PDF-1.7\n1 0 obj\n<</Type /ObjStm /N 2 /First 17 /Length " + objects.Length + ">>\nstream\n" + objects + "\nendstream\nendobj\n" +
				"2 0 obj\n<</Type /ObjStm /N 1 /First 4 /Length 2147483647>>\nstream\n6 0 <</B 2>>\nendstream\nendobj\n";

			var unpacked = PdfObjects.UnpackObjectStreams(Encoding.Latin1.GetBytes(latin1), latin1);

			Assert.DoesNotContain("5 0 obj", unpacked); // the wrapped offset is left out
			Assert.Contains("/B 2", unpacked);           // the huge /Length falls back to "endstream"
		}

		// ── Any 3xx counts as reachable; nothing is followed ────────────────────

		[Theory]
		[InlineData(200, true)]
		[InlineData(204, true)]
		[InlineData(301, true)]
		[InlineData(302, true)]
		[InlineData(307, true)]
		[InlineData(308, true)]
		[InlineData(404, false)]
		[InlineData(500, false)]
		public void Connectivity_AnyRedirectIsReachable(int status, bool reachable) =>
			Assert.Equal(reachable, ConnectivityCheck.IsReachable(status));
	}
}
