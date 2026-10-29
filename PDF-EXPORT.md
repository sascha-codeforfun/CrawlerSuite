# PDF export (FlowDocument → PDF with clickable links)

## What it does

`CrawlerSuite.Pdf.FlowDocumentPdf.Save(document, path)` writes a FlowDocument to a PDF whose
text, colours, boxes and page breaks come from WPF's own layout. Every hyperlink is clickable
along its whole length, also where it wraps. There are no third-party or NuGet dependencies.

## How it works

1. **Pagination.** The document is paginated with its own `DocumentPaginator`, at its own
   `PageWidth`/`PageHeight`. A4 is used only where none is set, and the original values are
   restored afterwards.
2. **Visual walk** (`PageWalker`). Each `DocumentPage.Visual` is walked recursively: offsets,
   transforms, clips and opacity, then `VisualTreeHelper.GetDrawing`, then the child visuals.
   - `GlyphRunDrawing` → text drawn with the exact WPF glyph IDs and advances (TJ adjustments).
   - `GeometryDrawing` → filled and stroked paths: backgrounds, borders, marks, underlines.
   - Every visual pushes its own `q … cm … Q`, so drawing uses WPF coordinates unchanged. One
     page matrix maps DIP (1/96 in, y down) to points (1/72 in, y up): scale 0.75, y flipped.
3. **Fonts** (`FontCollector`).
   - Each `GlyphTypeface` is subset with `GlyphTypeface.ComputeSubset` and embedded as
     Type0 / CIDFontType2 / FontFile2, Identity-H, CIDToGIDMap Identity.
   - The subset keeps every table of the font; only the tables a PDF draws with are embedded
     (`SfntTables.KeepForPdf`: head, hhea, maxp, hmtx, loca, glyf, cvt, fpgm, prep, cmap, name,
     post, OS/2, or CFF). Colour layers, bitmaps, layout and hinting caches are dropped — a colour
     emoji font otherwise adds megabytes for one symbol. Glyph IDs and outlines are unchanged.
   - A font program used by two fonts (a face and its simulated bold) is embedded once.
   - `/W` widths come from the typeface.
   - A ToUnicode CMap is built from `GlyphRun.Characters` and `ClusterMap`, so copy and search
     return the document's characters (umlauts, typographic quotes, other scripts, ligatures).
4. **Links** (`LinkPlacer`).
   - Every `Hyperlink` is collected first, including those in `BlockUIContainer` / `TextBlock`.
   - Its text, with whitespace removed, is matched in reading order against the laid-out
     characters.
   - One `/Link` annotation with a `/URI` action is written per line fragment, with border 0.
5. **File layer** (`CrawlerSuite.Core.Pdf`, no WPF). This holds the objects, Flate streams,
   font dictionaries, `/W`, the CMap, annotations, the xref table and the trailer.
   - It is written from ISO 32000-1.
   - Output is deterministic: no dates, no XMP, `/Producer (CrawlerSuite)`, `/ID` taken from a
     SHA-256 of the content, and subset tags derived from the glyph set.

## Integration

**In CrawlerSuite:** `Views/TicketPreview.SavePdf` builds the real (unmasked) ticket document,
wraps it in the footer paginator, and calls `FlowDocumentPdf.Save(document, paginator, path)`.
That overload (added on plugging in) takes its pages from the paginator, so the footer is in the
PDF; the document is read only for its hyperlinks. The general example below is the component
as delivered.

Call it on the UI thread with the **same FlowDocument instance the print path uses**, already
configured (PageWidth/Height, PagePadding, ColumnWidth). Do not pass a document that is
currently hosted in a viewer, because pagination is shared with the viewer. Build a fresh
instance, as for printing.

```csharp
// using CrawlerSuite.Pdf; using Microsoft.Win32;
private void ExportPdf_Click(object sender, RoutedEventArgs e)
{
    var dialog = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", DefaultExt = ".pdf", FileName = "ticket.pdf" };
    if (dialog.ShowDialog(this) != true) return;
    try
    {
        FlowDocument doc = BuildTicketDocument();            // the host's existing builder
        PdfExportReport r = FlowDocumentPdf.Save(doc, dialog.FileName);
        StatusText = $"PDF: {r.Pages} pages, {r.Fonts} fonts, {r.GlyphRuns} text runs, {r.Links} links"
                     + (r.Skipped.Count > 0 ? $"; skipped: {string.Join("; ", r.Skipped)}" : "");
    }
    catch (PdfExportException ex)
    {
        MessageBox.Show(this, ex.Message, "PDF export", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
```

`Save` is synchronous. It writes `path + ".partial"` first and then replaces the target, so a
failed export never leaves a half file.

## Limits (fail loudly, never silently)

**Stops with `PdfExportException`, listing each item and page:**
- images (`ImageDrawing`), video (`VideoDrawing`)
- gradient, image or visual brushes
- opacity masks, effects
- sideways text
- 3D content
- fonts whose licence forbids embedding or allows bitmap embedding only

**Reported in `Skipped`; the PDF is still written:**
- hyperlinks that have no `NavigateUri`
- hyperlinks that are not absolute http/https
- hyperlinks whose text was not found on the pages
- fonts that forbid subsetting (embedded in full)
- triangle line caps (drawn flat)

**Approximations:**
- Bold and italic *simulation* (a font face missing that style) is drawn as fill+stroke and as
  a ⅓ shear. Real bold/italic faces are exact.
- Element opacity is applied per paint operation, not as a group. Overlapping semi-transparent
  shapes inside one transparent element can look slightly different.

**Link matching:**
- Matching is text-based and runs in reading order. If the same URL text also appears as plain
  text *before* its hyperlink, the annotation lands on that earlier occurrence.
- Hyperlinks are matched in document order, so this is rare in ticket documents.

**Fonts:**
- TrueType outlines are embedded as FontFile2. CFF-flavoured OpenType is embedded as
  FontFile3/OpenType.
- Font collections (.ttc) cannot be embedded in full. This only matters when subsetting is
  forbidden.

## Checks the export makes on itself

1. **Glyph IDs in the font subset.** Every glyph that carries visible text must have an outline at
   the same ID in the embedded subset. If not, the export stops with: *"The embedded subset of
   font … has no outline for glyph …"*. Verified on Windows 11 with Segoe UI (Regular, Semibold,
   Bold) and Consolas (Regular, Bold): text copies out of the PDF with the right characters.
2. **Every glyph run and geometry is drawn.** `GlyphRuns` in the report counts the text runs
   drawn. A missing border or background would show as a visual difference against the preview,
   not as an error; card borders and backgrounds, the marks in excerpts, the monospace blocks and
   the footer match the preview.
3. **No drawing type is skipped silently.** Every unhandled type stops the export with a list
   (`… on page N (k×)`). A ticket document holds only text, rectangles and borders; links reach
   their targets on every line of a wrapped address.

## Files

| Folder | Files |
|---|---|
| `CrawlerSuite.Core/Pdf/` | `PdfFormat`, `PdfContentBuilder`, `TrueTypeFile`, `PdfFontData` (+ `/W`, ToUnicode), `PdfDocumentBuilder` (all `internal`) |
| `CrawlerSuite/Pdf/` | `FlowDocumentPdf` (+ `PdfExportReport`, `PdfExportException`), `PageWalker`, `FontCollector`, `LinkPlacer` (+ `HyperlinkCollector`) |
| `CrawlerSuite.Core.Tests/Pdf/` | `PdfSyntaxTests`, `PdfDocumentTests`, `TestFont` (synthetic TrueType builder) |
