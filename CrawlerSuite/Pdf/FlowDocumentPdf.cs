using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using CrawlerSuite.Core.Pdf;

namespace CrawlerSuite.Pdf;

/// <summary>Counts for a diagnostic line after an export.</summary>
/// <param name="Pages">Pages written.</param>
/// <param name="Fonts">Embedded fonts (each typeface / style simulation counts once).</param>
/// <param name="GlyphRuns">Text runs drawn.</param>
/// <param name="Links">Hyperlinks made clickable (one link may produce several annotations when it wraps).</param>
/// <param name="Skipped">Things not represented exactly: unlinked hyperlinks, approximations, full-font embedding.</param>
public sealed record PdfExportReport(int Pages, int Fonts, int GlyphRuns, int Links, IReadOnlyList<string> Skipped);

/// <summary>Thrown when the document contains something the PDF export cannot represent, or the file cannot be written.</summary>
public sealed class PdfExportException : Exception
{
    public PdfExportException() { }
    public PdfExportException(string message) : base(message) { }
    public PdfExportException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Writes a FlowDocument to PDF using WPF's own pagination and glyph layout, so the PDF matches the
/// WPF preview. Hyperlinks become /Link annotations, one per laid-out line fragment.
/// </summary>
public static class FlowDocumentPdf
{
    private const string Producer = "CrawlerSuite";
    private const double A4WidthDip = 793.7007874015748;   // 210 mm
    private const double A4HeightDip = 1122.5196850393702; // 297 mm

    /// <summary>
    /// Writes <paramref name="document"/> to <paramref name="path"/>. Must be called on the document's
    /// UI thread. Throws <see cref="PdfExportException"/> with a plain-English message on anything it cannot
    /// represent; the target file is only replaced when the whole PDF was built successfully.
    /// </summary>
    public static PdfExportReport Save(FlowDocument document, string path) => Save(document, null, path);

    /// <summary>
    /// As <see cref="Save(FlowDocument, string)"/>, with the pages taken from <paramref name="paginator"/> — a
    /// paginator that wraps the document's own (e.g. one that adds a footer to every page). It must be
    /// set up already (page size, count); the document is then only read for its hyperlinks.
    /// </summary>
    public static PdfExportReport Save(FlowDocument document, DocumentPaginator? paginator, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        document.VerifyAccess();

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new PdfExportException($"The PDF path is not valid: {path}", ex); }

        byte[] pdf;
        PdfExportReport report;
        try
        {
            (pdf, report) = Render(document, paginator);
        }
        catch (PdfExportException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new PdfExportException("PDF export failed: " + ex.Message, ex);
        }

        WriteAtomically(fullPath, pdf);
        return report;
    }

    private static (byte[] Pdf, PdfExportReport Report) Render(FlowDocument document, DocumentPaginator? given)
    {
        var skipped = new List<string>();
        var links = HyperlinkCollector.Collect(document, skipped);
        return given is null ? RenderOwnPages(document, links, skipped) : RenderPages(given, links, skipped);
    }

    private static (byte[] Pdf, PdfExportReport Report) RenderOwnPages(FlowDocument document, List<LinkTarget> links, List<string> skipped)
    {
        // Paginate at the document's own page size; A4 only where none is set. Restored afterwards.
        object oldWidth = document.ReadLocalValue(FlowDocument.PageWidthProperty);
        object oldHeight = document.ReadLocalValue(FlowDocument.PageHeightProperty);
        if (double.IsNaN(document.PageWidth)) document.PageWidth = A4WidthDip;
        if (double.IsNaN(document.PageHeight)) document.PageHeight = A4HeightDip;
        try
        {
            DocumentPaginator paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.ComputePageCount();
            return RenderPages(paginator, links, skipped);
        }
        finally
        {
            Restore(document, FlowDocument.PageWidthProperty, oldWidth);
            Restore(document, FlowDocument.PageHeightProperty, oldHeight);
        }
    }

    private static (byte[] Pdf, PdfExportReport Report) RenderPages(DocumentPaginator paginator, List<LinkTarget> links, List<string> skipped)
    {
        var builder = new PdfDocumentBuilder(Producer);
        var fonts = new FontCollector();
        var walker = new PageWalker(builder, fonts);
        var pages = new List<(byte[] Content, Size Size)>();

        if (!paginator.IsPageCountValid) paginator.ComputePageCount();
        for (int i = 0; i < paginator.PageCount; i++)
        {
            using DocumentPage page = paginator.GetPage(i);
            if (page == DocumentPage.Missing || page.Visual is null)
                throw new PdfExportException($"Page {i + 1} of the document could not be laid out.");
            pages.Add((walker.WalkPage(page.Visual, i, page.Size.Height), page.Size));
        }
        if (pages.Count == 0) throw new PdfExportException("The document has no pages.");
        if (walker.Unsupported.Count > 0) throw new PdfExportException(UnsupportedMessage(walker.Unsupported));

        fonts.AddTo(builder, skipped);
        skipped.AddRange(walker.Notes);

        var heights = new double[pages.Count];
        for (int i = 0; i < pages.Count; i++) heights[i] = pages[i].Size.Height;
        var annotations = LinkPlacer.Place(links, walker.Chars, heights, skipped, out int placed);

        for (int i = 0; i < pages.Count; i++)
            builder.AddPage(pages[i].Size.Width * PageWalker.PointsPerDip, pages[i].Size.Height * PageWalker.PointsPerDip,
                            pages[i].Content, annotations[i]);

        var report = new PdfExportReport(pages.Count, builder.FontCount, walker.GlyphRuns, placed, skipped.AsReadOnly());
        return (builder.Build(), report);
    }

    private static void Restore(FlowDocument document, DependencyProperty property, object localValue)
    {
        if (localValue == DependencyProperty.UnsetValue) document.ClearValue(property);
        else document.SetValue(property, localValue);
    }

    private static string UnsupportedMessage(IReadOnlyDictionary<string, int> items)
    {
        var sb = new StringBuilder("The document contains content the PDF export cannot represent, so no PDF was written:");
        foreach (var (what, count) in items)
            sb.Append("\n- ").Append(what).Append(count > 1 ? $" ({count}×)" : "");
        return sb.ToString();
    }

    private static void WriteAtomically(string fullPath, byte[] pdf)
    {
        string temp = fullPath + ".partial";
        try
        {
            File.WriteAllBytes(temp, pdf);
            File.Move(temp, fullPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new PdfExportException($"Could not write the PDF to {fullPath}: {ex.Message}", ex);
        }
    }
}
