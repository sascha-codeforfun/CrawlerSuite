using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Pdf;
using Xunit;

namespace CrawlerSuite.Core.Tests.Pdf;

/// <summary>An embedded font keeps only what a PDF draws with; one program shared by two fonts is embedded once.</summary>
public class SfntTablesTests
{
    private static byte[] Filled(int length, byte value)
    {
        var data = new byte[length];
        Array.Fill(data, value);
        return data;
    }

    // A font as a Windows subset leaves it: outlines plus colour layers, layout and hinting caches.
    private static byte[] Heavy() => TestFont.Build(new[] { 8, 12, 0 }, extra: new List<(string, byte[])>
    {
        ("COLR", Filled(5000, 0xC0)),
        ("CPAL", Filled(301, 0xCA)),
        ("GPOS", Filled(700, 0x90)),
        ("hdmx", Filled(90, 0x4D)),
        ("hhea", Filled(36, 0x11)),
        ("hmtx", Filled(12, 0x22)),
        ("cmap", Filled(30, 0x33)),
    });

    private static Dictionary<string, (uint Sum, int Offset, int Length)> Directory(byte[] font)
    {
        var tables = new Dictionary<string, (uint, int, int)>(StringComparer.Ordinal);
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int rec = 12 + i * 16;
            tables[Encoding.ASCII.GetString(font, rec, 4)] = (
                BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(rec + 4)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(rec + 8)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(rec + 12)));
        }

        return tables;
    }

    [Fact]
    public void ColourLayoutAndCacheTables_AreLeftOut_TheDrawingTablesKept()
    {
        var heavy = Heavy();
        var light = SfntTables.KeepForPdf(heavy);

        var tables = Directory(light);
        Assert.Equal(["cmap", "glyf", "head", "hhea", "hmtx", "loca", "maxp"], new SortedSet<string>(tables.Keys, StringComparer.Ordinal));
        Assert.True(light.Length < heavy.Length - 6000);

        // Outlines and metrics byte for byte as they were.
        var before = Directory(heavy);
        foreach (var tag in new[] { "glyf", "loca", "hmtx", "cmap", "hhea" })
        {
            Assert.Equal(heavy.AsSpan(before[tag].Offset, before[tag].Length).ToArray(),
                light.AsSpan(tables[tag].Offset, tables[tag].Length).ToArray());
        }
    }

    [Fact]
    public void TheResult_IsAValidFont_WithTheSameGlyphs()
    {
        var light = SfntTables.KeepForPdf(Heavy());
        var tt = TrueTypeFile.Parse(light);

        Assert.Equal(3, tt.NumGlyphs);
        Assert.True(tt.HasOutline(0));
        Assert.True(tt.HasOutline(1));
        Assert.False(tt.HasOutline(2));
    }

    [Fact]
    public void Checksums_AndTheWholeFileAdjustment_AreRight()
    {
        var light = SfntTables.KeepForPdf(Heavy());

        foreach (var (tag, (sum, offset, length)) in Directory(light))
        {
            var data = light.AsSpan(offset, (length + 3) & ~3).ToArray();
            if (tag == "head")
            {
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 0);
            }

            Assert.Equal(SfntTables.Checksum(data), sum);
            Assert.Equal(0, offset % 4);
        }

        Assert.Equal(0xB1B0AFBAu, SfntTables.Checksum(light));
    }

    [Fact]
    public void AFontWithNothingToLeaveOut_IsReturnedUnchanged()
    {
        var plain = TestFont.Build(new[] { 8, 12 });

        Assert.Same(plain, SfntTables.KeepForPdf(plain));
    }

    [Fact]
    public void ACollectionOrGarbage_IsLeftToTheParser()
    {
        var ttc = TestFont.Build(new[] { 4 }, sfnt: "ttcf");
        var garbage = new byte[] { 1, 2, 3 };

        Assert.Same(ttc, SfntTables.KeepForPdf(ttc));
        Assert.Same(garbage, SfntTables.KeepForPdf(garbage));
    }

    [Fact]
    public void OneProgram_UsedByTwoFonts_IsEmbeddedOnce()
    {
        var program = SfntTables.KeepForPdf(Heavy());
        PdfFontData Font(string name) => new()
        {
            BaseName = name,
            FontProgram = program,
            IsSubset = true,
            Widths = new SortedDictionary<ushort, int> { [1] = 600 },
            Unicode = new SortedDictionary<ushort, string> { [1] = "\u2713" },
            Ascent = 900,
            Descent = -200,
            CapHeight = 700,
        };

        var doc = new PdfDocumentBuilder("CrawlerSuite");
        doc.AddFont(Font("XyzzySymbol-Regular"));
        doc.AddFont(Font("XyzzySymbol-RegularSimBold"));
        doc.AddPage(595.28, 841.89, new PdfContentBuilder().ToBytes());
        string pdf = Encoding.Latin1.GetString(doc.Build());

        Assert.Single(Regex.Matches(pdf, "/Length1 ").Cast<Match>());
        var files = Regex.Matches(pdf, @"/FontFile2 (\d+) 0 R").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal(files[0], files[1]);
    }
}
