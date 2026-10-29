using System.Text;
using Xunit;

namespace CrawlerSuite.Core.Tests.Pdf
{
	/// <summary>PDF text strings in the Info dictionary, read as the standard defines them.</summary>
	public class PdfTextStringTests
	{
		[Theory]
		[InlineData(@"Wasser \(H2O\)", "Wasser (H2O)")]
		[InlineData(@"Wasserst\366ff", "Wasserstöff")]          // octal escape of a PDFDocEncoding letter
		[InlineData(@"a\\b", @"a\b")]
		[InlineData("eins\\\r\nzwei", "einszwei")]                 // a backslash before a line end joins the lines
		public void ALiteralString_HasItsEscapesResolved(string inner, string text) =>
			Assert.Equal(text, PdfQualityAnalyzer.DecodeTextString(PdfQualityAnalyzer.LiteralBytes(inner)));

		[Fact]
		public void Utf16_AndUtf8WithItsMarker_AreDecoded()
		{
			var utf16 = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes("Molekül")).ToArray();
			var utf8 = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("Molekül")).ToArray();
			Assert.Equal("Molekül", PdfQualityAnalyzer.DecodeTextString(utf16));
			Assert.Equal("Molekül", PdfQualityAnalyzer.DecodeTextString(utf8));
		}

		[Fact]
		public void Utf8WithoutItsMarker_IsReadAsPdfReadersReadIt()
		{
			// Not allowed by the standard; readers take it as PDFDocEncoding and show it garbled — so does the report.
			Assert.Equal("MolekÃ¼l", PdfQualityAnalyzer.DecodeTextString(Encoding.UTF8.GetBytes("Molekül")));
		}

		[Fact]
		public void TheInfoTitle_LiteralAndHex_AreDecoded()
		{
			Assert.Equal("Wasserstöff", PdfQualityAnalyzer.ExtractInfoValue(@"/Title (Wasserst\366ff)", "/Title"));
			Assert.Equal("Molekül", PdfQualityAnalyzer.ExtractInfoValue("/Title <FEFF004D006F006C0065006B00FC006C>", "/Title"));
		}
	}
}
