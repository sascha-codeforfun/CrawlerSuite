using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using CrawlerSuite.Core.Pdf;

namespace CrawlerSuite.Pdf;

/// <summary>A hyperlink to place: its laid-out text (whitespace removed) and its ASCII URI.</summary>
internal sealed record LinkTarget(string Key, string Uri);

/// <summary>Finds every Hyperlink in the document, including inside BlockUIContainer / TextBlock content.</summary>
internal static class HyperlinkCollector
{
    public static List<LinkTarget> Collect(FlowDocument document, List<string> notes)
    {
        var result = new List<LinkTarget>();
        Walk(document, result, notes);
        return result;
    }

    private static void Walk(DependencyObject node, List<LinkTarget> result, List<string> notes)
    {
        if (node is Hyperlink link)
        {
            Add(link, result, notes);
            return;
        }
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject d) Walk(d, result, notes);
    }

    private static void Add(Hyperlink link, List<LinkTarget> result, List<string> notes)
    {
        string text = new TextRange(link.ContentStart, link.ContentEnd).Text;
        string shown = text.Length > 60 ? text[..57] + "…" : text;
        Uri? uri = link.NavigateUri;
        if (uri is null) { notes.Add($"hyperlink \"{shown}\" has no NavigateUri and is not linked"); return; }
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        { notes.Add($"hyperlink \"{shown}\" is not an absolute http/https link ({uri.OriginalString}) and is not linked"); return; }

        string key = LinkPlacer.Normalize(text);
        if (key.Length == 0) { notes.Add($"hyperlink to {uri.AbsoluteUri} has no text and is not linked"); return; }
        result.Add(new LinkTarget(key, ToAscii(uri)));
    }

    /// <summary>URI in 7-bit ASCII: percent-encoded path/query (AbsoluteUri) with an IDN host as punycode.</summary>
    private static string ToAscii(Uri uri)
    {
        string s = uri.AbsoluteUri;
        foreach (char c in s)
        {
            if (c > 0x7E)
            {
                var b = new UriBuilder(uri) { Host = uri.IdnHost };
                s = b.Uri.AbsoluteUri;
                break;
            }
        }
        return s;
    }
}

/// <summary>
/// Matches link texts against the laid-out characters in reading order and produces one
/// annotation rectangle per line fragment (consecutive characters from the same line).
/// </summary>
internal static class LinkPlacer
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (!char.IsWhiteSpace(c) && c != '­') sb.Append(c);
        return sb.ToString();
    }

    public static List<PdfLink>[] Place(IReadOnlyList<LinkTarget> targets, IReadOnlyList<PlacedChar> chars,
                                        IReadOnlyList<double> pageHeightsDip, List<string> notes, out int placed)
    {
        var perPage = new List<PdfLink>[pageHeightsDip.Count];
        for (int i = 0; i < perPage.Length; i++) perPage[i] = new List<PdfLink>();
        placed = 0;

        var seq = new List<PlacedChar>(chars.Count);
        var text = new StringBuilder(chars.Count);
        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c.Ch) || c.Ch == '­') continue;
            seq.Add(c);
            text.Append(c.Ch);
        }
        string all = text.ToString();

        int cursor = 0;
        foreach (var target in targets)
        {
            int at = all.IndexOf(target.Key, cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                notes.Add($"link text for {target.Uri} was not found on the pages and is not linked");
                continue;
            }
            cursor = at + target.Key.Length;
            placed++;
            foreach (var (page, box) in Fragments(seq, at, target.Key.Length))
            {
                double h = pageHeightsDip[page];
                const double k = PageWalker.PointsPerDip;
                perPage[page].Add(new PdfLink(box.Left * k, (h - box.Bottom) * k, box.Right * k, (h - box.Top) * k, target.Uri));
            }
        }
        return perPage;
    }

    /// <summary>Unions character boxes per glyph run, then merges neighbouring runs on the same line.</summary>
    private static List<(int Page, Rect Box)> Fragments(List<PlacedChar> seq, int start, int length)
    {
        var result = new List<(int Page, Rect Box)>();
        int lastRun = -1;
        for (int i = start; i < start + length; i++)
        {
            var c = seq[i];
            if (result.Count > 0)
            {
                var (page, box) = result[^1];
                bool sameLine = page == c.Page &&
                    (c.Run == lastRun || (Math.Abs(box.Top - c.Box.Top) < 1.0 && Math.Abs(box.Bottom - c.Box.Bottom) < 1.0
                                          && c.Box.Left >= box.Left));
                if (sameLine)
                {
                    box.Union(c.Box);
                    result[^1] = (page, box);
                    lastRun = c.Run;
                    continue;
                }
            }
            result.Add((c.Page, c.Box));
            lastRun = c.Run;
        }
        return result;
    }
}
