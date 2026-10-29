using System;
using System.Collections.Generic;
using System.Globalization;
using CrawlerSuite.Core.Pdf;
using Xunit;

namespace CrawlerSuite.Core.Tests.Pdf;

public class PdfSyntaxTests
{
    [Fact]
    public void Num_IsInvariant_Rounded_AndNeverNegativeZero()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.5", PdfFormat.Num(1.5));
            Assert.Equal("0.1235", PdfFormat.Num(0.123456));
            Assert.Equal("0", PdfFormat.Num(-0.00001));
            Assert.Equal("-12", PdfFormat.Num(-12));
            Assert.Equal("1234567", PdfFormat.Num(1234567));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Num_RejectsNonFinite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfFormat.Num(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfFormat.Num(double.PositiveInfinity));
    }

    [Fact]
    public void Name_EscapesDelimitersSpacesAndNonAscii()
    {
        Assert.Equal("/F1", PdfFormat.Name("F1"));
        Assert.Equal("/Xyzzy#20Haus", PdfFormat.Name("Xyzzy Haus"));
        Assert.Equal("/A#2FB#28C#29", PdfFormat.Name("A/B(C)"));
        Assert.Equal("/#C3#A4", PdfFormat.Name("ä"));
    }

    [Fact]
    public void AsciiLiteral_EscapesParenthesesAndBackslash()
    {
        Assert.Equal("(https://example.test/a\\(b\\)c\\\\d)", PdfFormat.AsciiLiteral("https://example.test/a(b)c\\d"));
        Assert.Equal("(\\012)", PdfFormat.AsciiLiteral("\n"));
    }

    [Fact]
    public void TextString_UsesUtf16WithBomForNonAscii()
    {
        Assert.Equal("(Xyzzy)", PdfFormat.TextString("Xyzzy"));
        Assert.Equal("<FEFF00E4>", PdfFormat.TextString("ä"));
    }

    [Fact]
    public void WidthsArray_GroupsConsecutiveGlyphIds()
    {
        var w = new SortedDictionary<ushort, int> { [3] = 500, [4] = 600, [5] = 610, [10] = 250, [0] = 1000 };
        Assert.Equal("[0 [1000] 3 [500 600 610] 10 [250]]", PdfFontEncoding.BuildWidthsArray(w));
        Assert.Equal("[]", PdfFontEncoding.BuildWidthsArray(new SortedDictionary<ushort, int>()));
    }

    [Fact]
    public void ToUnicode_MapsUmlautsQuotesLigaturesAndSurrogates()
    {
        var map = new SortedDictionary<ushort, string>
        {
            [0x10] = "ä",
            [0x11] = "„",          // „
            [0x12] = "ffi",             // ligature cluster
            [0x13] = "\U0001F600",      // non-BMP
            [0x14] = "",                // continuation glyph: no entry
        };
        string cmap = PdfFontEncoding.BuildToUnicodeCMap(map);
        Assert.Contains("4 beginbfchar", cmap, StringComparison.Ordinal);
        Assert.Contains("<0010> <00E4>", cmap, StringComparison.Ordinal);
        Assert.Contains("<0011> <201E>", cmap, StringComparison.Ordinal);
        Assert.Contains("<0012> <006600660069>", cmap, StringComparison.Ordinal);
        Assert.Contains("<0013> <D83DDE00>", cmap, StringComparison.Ordinal);
        Assert.DoesNotContain("<0014>", cmap, StringComparison.Ordinal);
        Assert.Contains("<0000> <FFFF>", cmap, StringComparison.Ordinal);
    }

    [Fact]
    public void ToUnicode_SplitsIntoBlocksOfAtMostHundred()
    {
        var map = new SortedDictionary<ushort, string>();
        for (ushort g = 1; g <= 250; g++) map[g] = "x";
        string cmap = PdfFontEncoding.BuildToUnicodeCMap(map);
        Assert.Equal(2, Count(cmap, "100 beginbfchar"));
        Assert.Equal(1, Count(cmap, "50 beginbfchar"));
    }

    [Fact]
    public void ShowGlyphs_WritesHexCodesAndAdjustments()
    {
        var cb = new PdfContentBuilder();
        cb.ShowGlyphs(new ushort[] { 0x41, 0x42, 0x1F3 }, new double[] { 0, -12.346, 0 });
        Assert.Equal("[<00410042> -12.35 <01F3>] TJ\n", cb.ToString());
    }

    [Fact]
    public void ContentOperators_AreWrittenInOrder()
    {
        var cb = new PdfContentBuilder()
            .SaveState().Transform(0.75, 0, 0, -0.75, 0, 841.89)
            .SetFillRgb(1, 0.5, 0).Rectangle(10, 20, 30, 40).Fill(false)
            .SetDash(new[] { 2.0, 1.0 }, 0.5).RestoreState();
        Assert.Equal("q\n0.75 0 0 -0.75 0 841.89 cm\n1 0.5 0 rg\n10 20 30 40 re\nf\n[2 1] 0.5 d\nQ\n", cb.ToString());
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
