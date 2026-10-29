using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Pdf;
using Xunit;

namespace CrawlerSuite.Core.Tests.Pdf;

public class PdfDocumentTests
{
    private static readonly Regex ObjHeader = new(@"(\d+) 0 obj", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void TrueTypeFile_ReadsHeadAndMaxp_AndReportsOutlines()
    {
        var tt = TrueTypeFile.Parse(TestFont.Build(new[] { 10, 0, 24 }));
        Assert.False(tt.IsCff);
        Assert.Equal(2048, tt.UnitsPerEm);
        Assert.Equal(3, tt.NumGlyphs);
        Assert.Equal(-400, tt.YMin);
        Assert.True(tt.HasOutline(0));
        Assert.False(tt.HasOutline(1));   // empty glyph
        Assert.True(tt.HasOutline(2));
        Assert.False(tt.HasOutline(3));   // out of range
    }

    [Fact]
    public void TrueTypeFile_RejectsCollectionsAndGarbage()
    {
        var ttc = TestFont.Build(new[] { 4 }, sfnt: "ttcf");
        var ex = Assert.Throws<FormatException>(() => TrueTypeFile.Parse(ttc));
        Assert.Contains("collection", ex.Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => TrueTypeFile.Parse(new byte[] { 1, 2, 3 }));
        var truncated = TestFont.Build(new[] { 4 })[..40];
        Assert.Throws<FormatException>(() => TrueTypeFile.Parse(truncated));
    }

    [Fact]
    public void Build_XrefOffsetsPointAtEachObject_AndStartxrefPointsAtXref()
    {
        byte[] pdf = SampleDocument();
        string s = Encoding.Latin1.GetString(pdf);

        int startxrefAt = s.LastIndexOf("startxref\n", StringComparison.Ordinal);
        int xref = int.Parse(s[(startxrefAt + 10)..s.IndexOf('\n', startxrefAt + 10)], CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n0 ", s[xref..], StringComparison.Ordinal);

        string[] lines = s[xref..].Split('\n');
        int count = int.Parse(lines[1].Split(' ')[1], CultureInfo.InvariantCulture);
        Assert.Equal("0000000000 65535 f\r", lines[2]);
        for (int n = 1; n < count; n++)
        {
            string entry = lines[2 + n];
            Assert.Equal(19, entry.Length); // 20 bytes including the '\n' split off
            int offset = int.Parse(entry[..10], CultureInfo.InvariantCulture);
            Assert.StartsWith($"{n} 0 obj\n", s[offset..], StringComparison.Ordinal);
        }
        Assert.Equal(count - 1, ObjHeader.Matches(s).Count);
        Assert.EndsWith("%%EOF\n", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_IsByteIdentical_AndHasNoDatesOrMetadata()
    {
        byte[] a = SampleDocument(), b = SampleDocument();
        Assert.Equal(a, b);
        string s = Encoding.Latin1.GetString(a);
        Assert.DoesNotContain("CreationDate", s, StringComparison.Ordinal);
        Assert.DoesNotContain("ModDate", s, StringComparison.Ordinal);
        Assert.DoesNotContain("/Metadata", s, StringComparison.Ordinal);
        Assert.Contains("/Producer (CrawlerSuite)", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_IdIsHashOfBodyBeforeXref()
    {
        byte[] pdf = SampleDocument();
        string s = Encoding.Latin1.GetString(pdf);
        int xref = s.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        string expected = Convert.ToHexString(SHA256.HashData(pdf.AsSpan(0, xref))[..16]);
        Assert.Contains($"/ID [<{expected}> <{expected}>]", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WritesOneLinkAnnotationPerFragment_WithUriAndNoBorder()
    {
        string s = Encoding.Latin1.GetString(SampleDocument());
        Assert.Equal(2, CountOf(s, "/Subtype /Link"));
        Assert.Equal(2, CountOf(s, "/S /URI /URI (https://example.test/xyzzy-haus/very/long/path?q=1)"));
        Assert.Contains("/Rect [10 700 300 712]", s, StringComparison.Ordinal);
        Assert.Contains("/Rect [10 686 120 698]", s, StringComparison.Ordinal); // corners were given swapped
        Assert.Contains("/Border [0 0 0]", s, StringComparison.Ordinal);
        Assert.Matches(@"/Annots \[\d+ 0 R \d+ 0 R\]", s);
    }

    [Fact]
    public void Build_EmbedsSubsetFontAsType0WithIdentityAndToUnicode()
    {
        string s = Encoding.Latin1.GetString(SampleDocument());
        Assert.Matches(@"/BaseFont /[A-Z]{6}\+XyzzySans-Regular /Encoding /Identity-H", s);
        Assert.Contains("/Subtype /CIDFontType2", s, StringComparison.Ordinal);
        Assert.Contains("/CIDToGIDMap /Identity", s, StringComparison.Ordinal);
        Assert.Contains("/W [1 [500 520 250]]", s, StringComparison.Ordinal);
        Assert.Contains("/FontBBox [-48.8281 -195.3125 976.5625 927.7344]", s, StringComparison.Ordinal);
        Assert.Contains("/Font << /F1 ", s, StringComparison.Ordinal);
        Assert.Contains("/ExtGState << /GS1 << /Type /ExtGState /ca 0.5 /CA 1 >> >>", s, StringComparison.Ordinal);

        var streams = DecodeStreams(SampleDocument());
        Assert.Contains(streams, x => x.Contains("<0001> <00E4>", StringComparison.Ordinal) && x.Contains("<0002> <201C>", StringComparison.Ordinal));
        Assert.Contains(streams, x => x.Contains("/F1 12 Tf", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_UnsubsettedFontHasNoTag()
    {
        var font = SampleFont();
        var doc = new PdfDocumentBuilder("CrawlerSuite");
        doc.AddFont(new PdfFontData
        {
            BaseName = font.BaseName, FontProgram = font.FontProgram, IsSubset = false,
            Widths = font.Widths, Unicode = font.Unicode, Ascent = 900, Descent = -200, CapHeight = 700,
        });
        doc.AddPage(100, 100, Array.Empty<byte>());
        Assert.Contains("/BaseFont /XyzzySans-Regular ", Encoding.Latin1.GetString(doc.Build()), StringComparison.Ordinal);
    }

    [Fact]
    public void AlphaStates_AreDeduplicated()
    {
        var doc = new PdfDocumentBuilder("CrawlerSuite");
        Assert.Equal("GS1", doc.AlphaState(0.5, 1));
        Assert.Equal("GS2", doc.AlphaState(0.25, 0.25));
        Assert.Equal("GS1", doc.AlphaState(0.5000001, 1));
    }

    [Fact]
    public void AddPage_RejectsNonAsciiOrSpacedUris()
    {
        var doc = new PdfDocumentBuilder("CrawlerSuite");
        Assert.Throws<ArgumentException>(() =>
            doc.AddPage(100, 100, Array.Empty<byte>(), new[] { new PdfLink(0, 0, 1, 1, "https://example.test/ä") }));
        Assert.Throws<ArgumentException>(() =>
            doc.AddPage(100, 100, Array.Empty<byte>(), new[] { new PdfLink(0, 0, 1, 1, "https://example.test/a b") }));
    }

    [Fact]
    public void Build_WithoutPages_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder("CrawlerSuite").Build());
    }

    [Fact]
    public void SubsetTag_IsStableAndDependsOnGlyphSet()
    {
        var a = SampleFont();
        string tag = PdfDocumentBuilder.SubsetTag(a);
        Assert.Matches("^[A-Z]{6}$", tag);
        Assert.Equal(tag, PdfDocumentBuilder.SubsetTag(SampleFont()));
        a.Widths[9] = 300;
        Assert.NotEqual(tag, PdfDocumentBuilder.SubsetTag(a));
    }

    private static PdfFontData SampleFont() => new()
    {
        BaseName = "XyzzySans-Regular",
        FontProgram = TestFont.Build(new[] { 8, 12, 12, 0 }),
        IsSubset = true,
        Widths = new SortedDictionary<ushort, int> { [1] = 500, [2] = 520, [3] = 250 },
        Unicode = new SortedDictionary<ushort, string> { [1] = "ä", [2] = "“", [3] = " " },
        Ascent = 900,
        Descent = -200,
        CapHeight = 700,
    };

    private static byte[] SampleDocument()
    {
        var doc = new PdfDocumentBuilder("CrawlerSuite");
        int f = doc.AddFont(SampleFont());
        var cb = new PdfContentBuilder()
            .SaveState().SetGraphicsState(doc.AlphaState(0.5, 1)).SetFillRgb(0.9, 0.9, 0.5)
            .Rectangle(10, 10, 200, 20).Fill(false).RestoreState()
            .BeginText().SetFont(PdfDocumentBuilder.FontResourceName(f), 12)
            .SetTextMatrix(1, 0, 0, 1, 10, 700).ShowGlyphs(new ushort[] { 1, 2, 3 }).EndText();
        const string uri = "https://example.test/xyzzy-haus/very/long/path?q=1";
        doc.AddPage(595.28, 841.89, cb.ToBytes(), new[]
        {
            new PdfLink(10, 700, 300, 712, uri),
            new PdfLink(120, 698, 10, 686, uri),
        });
        doc.AddPage(595.28, 841.89, new PdfContentBuilder().ToBytes());
        return doc.Build();
    }

    private static List<string> DecodeStreams(byte[] pdf)
    {
        var result = new List<string>();
        string s = Encoding.Latin1.GetString(pdf);
        int i = 0;
        while ((i = s.IndexOf("stream\n", i, StringComparison.Ordinal)) >= 0)
        {
            int start = i + 7;
            int end = s.IndexOf("\nendstream", start, StringComparison.Ordinal);
            if (s[..i].EndsWith("end", StringComparison.Ordinal)) { i = start; continue; }
            using var z = new ZLibStream(new MemoryStream(pdf, start, end - start), CompressionMode.Decompress);
            using var r = new StreamReader(z, Encoding.Latin1);
            result.Add(r.ReadToEnd());
            i = end;
        }
        return result;
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
