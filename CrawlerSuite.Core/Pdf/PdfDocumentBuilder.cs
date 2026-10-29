using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CrawlerSuite.Core.Pdf;

/// <summary>A URI link annotation rectangle in PDF user space (points, origin bottom-left).</summary>
internal readonly record struct PdfLink(double X1, double Y1, double X2, double Y2, string Uri);

/// <summary>
/// Assembles a complete PDF 1.7 file: catalog, page tree, one shared resource dictionary,
/// embedded Type0 fonts, content streams, link annotations, classic xref table and trailer.
/// Output is deterministic: no dates, no XMP, /ID derived from the file content.
/// </summary>
internal sealed class PdfDocumentBuilder
{
    private sealed record Page(double Width, double Height, byte[] Content, IReadOnlyList<PdfLink> Links);

    private readonly string _producer;
    private readonly List<PdfFontData> _fonts = new();
    private readonly List<Page> _pages = new();
    private readonly List<(double Fill, double Stroke)> _alphaStates = new();

    public PdfDocumentBuilder(string producer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        _producer = producer;
    }

    public int PageCount => _pages.Count;
    public int FontCount => _fonts.Count;

    /// <summary>Resource name of the font with the given index, in the order fonts are added.</summary>
    public static string FontResourceName(int index) => "F" + PdfFormat.Int(index + 1);

    /// <summary>Adds a font and returns its index; its resource name is <see cref="FontResourceName"/>.</summary>
    public int AddFont(PdfFontData font)
    {
        ArgumentNullException.ThrowIfNull(font);
        _fonts.Add(font);
        return _fonts.Count - 1;
    }

    /// <summary>Returns the ExtGState resource name for constant fill/stroke alpha, creating it once.</summary>
    public string AlphaState(double fillAlpha, double strokeAlpha)
    {
        var key = (Math.Round(Math.Clamp(fillAlpha, 0, 1), 3), Math.Round(Math.Clamp(strokeAlpha, 0, 1), 3));
        int i = _alphaStates.IndexOf(key);
        if (i < 0) { _alphaStates.Add(key); i = _alphaStates.Count - 1; }
        return "GS" + PdfFormat.Int(i + 1);
    }

    /// <summary>Adds a page. Size in points; content is an uncompressed content stream.</summary>
    public void AddPage(double widthPt, double heightPt, byte[] content, IReadOnlyList<PdfLink>? links = null)
    {
        if (!(widthPt > 0) || !(heightPt > 0)) throw new ArgumentOutOfRangeException(nameof(widthPt), "Page size must be positive.");
        ArgumentNullException.ThrowIfNull(content);
        foreach (var l in links ?? Array.Empty<PdfLink>()) ValidateUri(l.Uri);
        _pages.Add(new Page(widthPt, heightPt, content, links ?? Array.Empty<PdfLink>()));
    }

    public byte[] Build()
    {
        if (_pages.Count == 0) throw new InvalidOperationException("A PDF needs at least one page.");

        var objects = new List<byte[]?> { null }; // index = object number; 0 is the free head
        int Reserve() { objects.Add(null); return objects.Count - 1; }
        void Set(int num, string body) => objects[num] = PdfFormat.Bytes(body);

        int catalog = Reserve(), pages = Reserve(), resources = Reserve(), info = Reserve();

        // Fonts: Type0 -> CIDFont -> FontDescriptor -> FontFile, plus ToUnicode.
        var fontRefs = new List<int>();
        // One program used by two fonts (a face and its simulated bold) is embedded once.
        var programs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var font in _fonts)
        {
            int type0 = Reserve(), cid = Reserve(), desc = Reserve();
            string programKey = Convert.ToHexString(SHA256.HashData(font.FontProgram));
            bool shared = programs.TryGetValue(programKey, out int file);
            if (!shared)
            {
                file = Reserve();
                programs[programKey] = file;
            }

            int toUni = Reserve();
            fontRefs.Add(type0);
            var tt = TrueTypeFile.Parse(font.FontProgram);
            string name = (font.IsSubset ? SubsetTag(font) + "+" : "") + font.BaseName;
            string nameTok = PdfFormat.Name(name);
            double k = 1000.0 / tt.UnitsPerEm;

            Set(type0, $"<< /Type /Font /Subtype /Type0 /BaseFont {nameTok} /Encoding /Identity-H " +
                       $"/DescendantFonts [{Ref(cid)}] /ToUnicode {Ref(toUni)} >>");
            Set(cid, $"<< /Type /Font /Subtype {(tt.IsCff ? "/CIDFontType0" : "/CIDFontType2")} /BaseFont {nameTok} " +
                     "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                     $"/FontDescriptor {Ref(desc)} /DW 1000 /W {PdfFontEncoding.BuildWidthsArray(font.Widths)}" +
                     (tt.IsCff ? "" : " /CIDToGIDMap /Identity") + " >>");
            Set(desc, $"<< /Type /FontDescriptor /FontName {nameTok} /Flags 4 " +
                      $"/FontBBox [{PdfFormat.Num(tt.XMin * k)} {PdfFormat.Num(tt.YMin * k)} {PdfFormat.Num(tt.XMax * k)} {PdfFormat.Num(tt.YMax * k)}] " +
                      $"/ItalicAngle 0 /Ascent {PdfFormat.Int(font.Ascent)} /Descent {PdfFormat.Int(font.Descent)} " +
                      $"/CapHeight {PdfFormat.Int(font.CapHeight)} /StemV 80 " +
                      $"{(tt.IsCff ? "/FontFile3" : "/FontFile2")} {Ref(file)} >>");
            if (!shared)
            {
                objects[file] = StreamObject(
                    tt.IsCff ? "/Subtype /OpenType" : $"/Length1 {PdfFormat.Int(font.FontProgram.Length)}",
                    font.FontProgram);
            }
            objects[toUni] = StreamObject("", PdfFormat.Bytes(PdfFontEncoding.BuildToUnicodeCMap(font.Unicode)));
        }

        // Shared resources.
        var res = new StringBuilder("<< /ProcSet [/PDF /Text]");
        if (_fonts.Count > 0)
        {
            res.Append(" /Font <<");
            for (int i = 0; i < fontRefs.Count; i++)
                res.Append(' ').Append(PdfFormat.Name(FontResourceName(i))).Append(' ').Append(Ref(fontRefs[i]));
            res.Append(" >>");
        }
        if (_alphaStates.Count > 0)
        {
            res.Append(" /ExtGState <<");
            for (int i = 0; i < _alphaStates.Count; i++)
                res.Append(" /GS").Append(PdfFormat.Int(i + 1))
                   .Append(" << /Type /ExtGState /ca ").Append(PdfFormat.Num(_alphaStates[i].Fill))
                   .Append(" /CA ").Append(PdfFormat.Num(_alphaStates[i].Stroke)).Append(" >>");
            res.Append(" >>");
        }
        Set(resources, res.Append(" >>").ToString());

        // Pages.
        var kids = new StringBuilder();
        foreach (var page in _pages)
        {
            int pageNum = Reserve(), content = Reserve();
            kids.Append(kids.Length > 0 ? " " : "").Append(Ref(pageNum));
            objects[content] = StreamObject("", page.Content);
            var annots = new StringBuilder();
            foreach (var link in page.Links)
            {
                int a = Reserve();
                annots.Append(annots.Length > 0 ? " " : "").Append(Ref(a));
                double x1 = Math.Min(link.X1, link.X2), x2 = Math.Max(link.X1, link.X2);
                double y1 = Math.Min(link.Y1, link.Y2), y2 = Math.Max(link.Y1, link.Y2);
                Set(a, "<< /Type /Annot /Subtype /Link " +
                       $"/Rect [{PdfFormat.Num(x1)} {PdfFormat.Num(y1)} {PdfFormat.Num(x2)} {PdfFormat.Num(y2)}] " +
                       $"/Border [0 0 0] /F 4 /P {Ref(pageNum)} " +
                       $"/A << /Type /Action /S /URI /URI {PdfFormat.AsciiLiteral(link.Uri)} >> >>");
            }
            Set(pageNum, $"<< /Type /Page /Parent {Ref(pages)} " +
                         $"/MediaBox [0 0 {PdfFormat.Num(page.Width)} {PdfFormat.Num(page.Height)}] " +
                         $"/Resources {Ref(resources)} /Contents {Ref(content)}" +
                         (annots.Length > 0 ? $" /Annots [{annots}]" : "") + " >>");
        }
        Set(pages, $"<< /Type /Pages /Kids [{kids}] /Count {PdfFormat.Int(_pages.Count)} >>");
        Set(catalog, $"<< /Type /Catalog /Pages {Ref(pages)} >>");
        Set(info, $"<< /Producer {PdfFormat.TextString(_producer)} >>");

        return Serialize(objects, catalog, info);
    }

    private static byte[] Serialize(List<byte[]?> objects, int catalog, int info)
    {
        using var ms = new MemoryStream();
        void W(string s) { var b = PdfFormat.Bytes(s); ms.Write(b, 0, b.Length); }

        W("%PDF-1.7\n");
        ms.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        var offsets = new long[objects.Count];
        for (int n = 1; n < objects.Count; n++)
        {
            var body = objects[n] ?? throw new InvalidOperationException($"PDF object {n} was reserved but never written.");
            offsets[n] = ms.Position;
            W($"{PdfFormat.Int(n)} 0 obj\n");
            ms.Write(body, 0, body.Length);
            W("\nendobj\n");
        }

        byte[] id = SHA256.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length))[..16];
        long xref = ms.Position;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(PdfFormat.Int(objects.Count)).Append('\n');
        sb.Append("0000000000 65535 f\r\n");
        for (int n = 1; n < objects.Count; n++)
            sb.Append(offsets[n].ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n\r\n");
        string hexId = PdfFormat.Hex(id);
        sb.Append("trailer\n<< /Size ").Append(PdfFormat.Int(objects.Count))
          .Append(" /Root ").Append(Ref(catalog)).Append(" /Info ").Append(Ref(info))
          .Append(" /ID [<").Append(hexId).Append("> <").Append(hexId).Append(">] >>\n")
          .Append("startxref\n").Append(PdfFormat.Int(xref)).Append("\n%%EOF\n");
        W(sb.ToString());
        return ms.ToArray();
    }

    /// <summary>Stream object with FlateDecode (zlib, §7.4.4). <paramref name="extraEntries"/> go into the dictionary.</summary>
    private static byte[] StreamObject(string extraEntries, byte[] data)
    {
        byte[] packed = Deflate(data);
        string dict = $"<< /Length {PdfFormat.Int(packed.Length)} /Filter /FlateDecode" +
                      (extraEntries.Length > 0 ? " " + extraEntries : "") + " >>\nstream\n";
        var head = PdfFormat.Bytes(dict);
        var tail = PdfFormat.Bytes("\nendstream");
        var result = new byte[head.Length + packed.Length + tail.Length];
        head.CopyTo(result, 0);
        packed.CopyTo(result, head.Length);
        tail.CopyTo(result, head.Length + packed.Length);
        return result;
    }

    internal static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>Six uppercase letters derived from the font name and its glyph set (§9.6.4), so output stays deterministic.</summary>
    internal static string SubsetTag(PdfFontData font)
    {
        var sb = new StringBuilder(font.BaseName);
        foreach (var gid in font.Widths.Keys) sb.Append(',').Append(PdfFormat.Int(gid));
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        var tag = new char[6];
        for (int i = 0; i < 6; i++) tag[i] = (char)('A' + h[i] % 26);
        return new string(tag);
    }

    private static void ValidateUri(string uri)
    {
        if (string.IsNullOrEmpty(uri)) throw new ArgumentException("Link URI is empty.");
        foreach (char c in uri)
            if (c < 0x21 || c > 0x7E) throw new ArgumentException("Link URI must be 7-bit ASCII without spaces: " + uri);
    }

    private static string Ref(int num) => PdfFormat.Int(num) + " 0 R";
}
