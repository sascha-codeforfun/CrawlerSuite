using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CrawlerSuite.Core.Display;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Text;
using CrawlerSuite.Run;
using DocRun = System.Windows.Documents.Run; // "Run" alone is the CrawlerSuite.Run namespace here

namespace CrawlerSuite.Views;

/// <summary>
/// The ticket document of one triaged page: the page, its source and CMS link, then one section
/// per kind of defect with its explanation card and every item with the Ticket verdict, and a
/// section for text in an intended other language (the Locale verdict). A shared entry has its own
/// layout (<see cref="BuildShared"/>): the finding first, its pages only for scale. Laid out for paper (A4,
/// light colours) and written for the people who fix the page. Built only on demand; nothing is
/// stored. Every fixed text is written in English in <see cref="Texts"/> and given in the
/// document's language through <see cref="L"/>.
/// </summary>
internal static class TicketDocument
{
	/// <summary>Every fixed text of the document, in English and in one place; <see cref="L"/> translates each.</summary>
	internal static class Texts
	{
		public const string Title = "Defects on this page";
		public const string Security = "Security-relevant";
		public const string SecurityCount = "{0} security-relevant";
		public const string AllPages = "All {0} pages";
		public const string Source = "Source in the CMS";
		public const string CmsLink = "CMS editor link";
		public const string PreviousTickets = "Ticketed before";
		public const string ItemCount = "{0} item(s)";
		public const string NewCount = "{0} new since ticket {1}";
		public const string Wcag = FindingBasis.WcagLabel;
		public const string NewSince = "New since ticket";
		public const string Links = "References";
		public const string LocaleHeading = "Text in another language — needs language markup";
		public const string Footer = "CrawlerSuite {0} · snapshot {1} · page {2} of {3}";
		public const string PreviewHeading = "How a desktop search result would likely show this page (estimate; cut with … where it would likely be cut)";
		public const string NoTitle = "(no title — a search engine makes one up)";
		public const string NoDescription = "(no description — a search engine picks text from the page)";

		// A shared entry: the finding first, its scale after.
		public const string SharedFacts = "Shared across {0} pages · {1}";
		public const string AtAGlance = "At a glance";
		public const string What = "What";
		public const string WhereToFix = "Where to fix";
		public const string WhereToFixText = "In the one place all these pages share — a template, a component or a shared text block. One change there fixes every page; no page needs editing by itself.";
		public const string Fix = "Fix";
		public const string Scope = "Scope";
		public const string ScopeText = "{0} pages · {1} section(s) · {2} host(s)";
		public const string Found = "What was found";
		public const string Examples = "Pages to open";
		public const string WhereItAppears = "Where it appears";
		public const string Others = "{0} other section(s)";
		public const string Frequent = "Texts found most often";
		public const string FrequentNote = "Texts found on {0} or more pages. Many different texts mean editors type them; the defect is in how the shared place renders them.";
		public const string Background = "Background";
		public const string Scale = "All {0} pages — for scale";
		public const string SnapshotLine = "Snapshot {0} · created {1}";
	}

	// Paper colours: dark text on white, marks as light backgrounds that survive greyscale printing.
	private static readonly Brush Ink = Frozen(0x1A, 0x1A, 0x1A);
	private static readonly Brush Muted = Frozen(0x55, 0x5B, 0x66);
	private static readonly Brush Rule = Frozen(0xD0, 0xD5, 0xDD);
	private static readonly Brush CardBack = Frozen(0xF3, 0xF5, 0xF8);
	private static readonly Brush WcagBack = Frozen(0xFF, 0xF4, 0xE0);
	private static readonly Brush OffenderBack = Frozen(0xFD, 0xD8, 0xD8);
	private static readonly Brush OffenderInk = Frozen(0x8B, 0x10, 0x10);
	private static readonly Brush TintBack = Frozen(0xE1, 0xEA, 0xF8);
	private static readonly Brush PartnerBack = Frozen(0xFE, 0xEE, 0xEE);
	private static readonly Brush WarnBack = Frozen(0xFE, 0xF0, 0xC0);
	private static readonly Brush OkInk = Frozen(0x16, 0x65, 0x34);
	private static readonly Brush NewBack = Frozen(0xE6, 0xF4, 0xEA);
	private static readonly Brush SecurityBack = Frozen(0xFB, 0xE4, 0xE4);
	private static readonly Brush LinkInk = Frozen(0x1A, 0x4F, 0xA0);

	// A dry highlighter, not neon: pale yellow that still shows in greyscale.
	private static readonly Brush HighlighterBack = Frozen(0xFF, 0xF2, 0xB3);

	// The band over a new topic: a light blue-grey with a dark edge, distinct from the cards' grey.
	private static readonly Brush BandBack = Frozen(0xE4, 0xEA, 0xF2);
	private static readonly Brush BandEdge = Frozen(0x3A, 0x4A, 0x60);

	private static readonly FontFamily Body = new("Segoe UI");
	private static readonly FontFamily Mono = new("Consolas");

	/// <summary>
	/// The language of the document being built: set by <see cref="Build"/> for the time it builds,
	/// on the thread that builds it, and read by every helper through <see cref="L"/>.
	/// </summary>
	[ThreadStatic]
	private static DocumentLanguage language;

	/// <summary>A text of the app in the document's language (the German table gives the German of the English text).</summary>
	private static string L(string english) => DocumentText.T(language, english);

	/// <summary>A defect's family or name in the document's language: "Template defects › Styled headings".</summary>
	private static string HeadingOf(string family, string label) => $"{L(family)} › {L(label)}";

	/// <summary>A4 at 96 dpi.</summary>
	public static readonly Size PageSize = new(793.7, 1122.5);

	/// <summary>
	/// Builds the document. <paramref name="text"/> filters every text taken from the snapshot:
	/// identity for the saved document, the screen mask for the preview.
	/// </summary>
	public static FlowDocument Build(TriagePageVm page, string snapshotName, Func<string, string> text, DocumentLanguage documentLanguage = DocumentLanguage.English)
	{
		var outer = language;
		language = documentLanguage;
		try
		{
			return BuildIn(page, snapshotName, text);
		}
		finally
		{
			language = outer;
		}
	}

	private static FlowDocument BuildIn(TriagePageVm page, string snapshotName, Func<string, string> text)
	{
		var doc = new FlowDocument
		{
			PageWidth = PageSize.Width,
			PageHeight = PageSize.Height,
			PagePadding = new Thickness(56, 56, 56, 80), // room for the footer
			ColumnWidth = PageSize.Width,
			FontFamily = Body,
			FontSize = 13,
			Foreground = Ink,
			Background = Brushes.White,
			TextAlignment = TextAlignment.Left,
		};

		if (Core.Triage.SharedEntries.IsShared(page.Url))
		{
			BuildShared(doc, page, snapshotName, text);
			return doc;
		}

		doc.Blocks.Add(new Paragraph(new DocRun(L(Texts.Title))) { FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
		var all = page.AllItems.ToList();
		var previous = page.Tickets.Count > 0 ? page.Tickets[^1] : null;
		var ticketed = all.Where(i => i.IsTicket).ToList();
		var locale = all.Where(i => i.IsLocale).ToList();
		doc.Blocks.Add(Facts(page, snapshotName, text));
		doc.Blocks.Add(PageAtAGlance(ticketed, locale, previous, text));

		// One section per kind of defect, in the order the page view shows them: what to change first —
		// the gist and the fix in two lines, then what was found — and the full explanation at the end.
		var background = new List<(string Heading, FindingBasis Basis)>();
		bool previewShown = false;
		string? family = null;
		var kinds = ticketed.GroupBy(KindOf, StringComparer.Ordinal).ToList();
		foreach (var kind in kinds)
		{
			var first = kind.First();
			var heading = SectionOf(first);

			// A new family starts under a band that names it, with room above; kinds of one family follow closer.
			var familyOf = FamilyOf(first);
			bool newFamily = familyOf != family;
			if (newFamily)
			{
				family = familyOf;
				int inFamily = kinds.Where(k => FamilyOf(k.First()) == familyOf).Sum(k => k.Count());
				doc.Blocks.Add(FamilyBand(L(familyOf), inFamily));
			}

			// The band names the family: the kind's heading names the kind only (none where both are one, as for broken links).
			var kindName = first.IsBrokenLink ? null : L(first.Item.Label);
			if (kindName is not null)
			{
				doc.Blocks.Add(KindHeading(kindName, kind.Count(), newFamily));
			}
			if (FindingsCatalog.BasisFor(first.Type, first.Detail) is { } found)
			{
				doc.Blocks.Add(Gist(found.Basis));
				background.Add((heading, found.Basis));
			}

			// The search result preview where the search items are, once.
			if (!previewShown && page.Snippet is { } snippet && kind.Any(i => i.Item.Snippet is not null))
			{
				doc.Blocks.Add(SearchPreview(snippet, text));
				previewShown = true;
			}

			if (first.IsSpelling)
			{
				doc.Blocks.AddRange(SpellingItems(kind.ToList(), previous, text));
				continue;
			}

			foreach (var item in kind.OrderByDescending(i => i.IsSecurity))
			{
				doc.Blocks.Add(Item(item, previous, text));
			}
		}

		if (locale.Count > 0)
		{
			doc.Blocks.Add(FamilyBand(L(Texts.LocaleHeading), locale.Count));
			doc.Blocks.Add(Gist(FindingsCatalog.LanguageOfParts));
			background.Add((L(Texts.LocaleHeading), FindingsCatalog.LanguageOfParts));
			foreach (var item in locale)
			{
				doc.Blocks.Add(Item(item, previous, text));
			}
		}

		// The explanations in full, with their references: for whoever wants the why. Under the page's
		// findings, not on a page of its own: the band stays with the first card's opening, so it moves
		// to the next page only when that would not fit below.
		if (background.Count > 0)
		{
			doc.Blocks.Add(FamilyBand(L(Texts.Background), null));
			foreach (var (heading, basis) in background.DistinctBy(b => b.Basis))
			{
				doc.Blocks.Add(SubHeading(heading));
				doc.Blocks.Add(Card(basis));
			}
		}

		return doc;
	}

	/// <summary>
	/// A shared entry, finding first: its title; one line of facts; a box with what it is, where and
	/// how it is fixed and how far it reaches; what was found; a few pages to open; where on the site
	/// it appears and the texts that repeat; the card in full as background; and every page last, as
	/// paths, only to show the scale — the fix is made once, in the place the pages share.
	/// </summary>
	private static void BuildShared(FlowDocument doc, TriagePageVm page, string snapshotName, Func<string, string> text)
	{
		var all = page.AllItems.ToList();
		var previous = page.Tickets.Count > 0 ? page.Tickets[^1] : null;
		var ticketed = all.Where(i => i.IsTicket).ToList();
		var locale = all.Where(i => i.IsLocale).ToList();

		doc.Blocks.Add(new Paragraph(new DocRun(text(Core.Triage.SharedEntries.TitleOf(page.Url, language))))
		{
			FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
		});

		foreach (var item in ticketed.Concat(locale))
		{
			var summary = Core.Triage.SharedSummary.Of(item.Item);
			var facts = new Paragraph { FontSize = 11.5, Foreground = Muted, Margin = new Thickness(0, 0, 0, 2) };
			facts.Inlines.Add(new DocRun(string.Format(CultureInfo.InvariantCulture, L(Texts.SharedFacts), summary.Pages,
				Summary(ticketed, locale, previous, text))));
			doc.Blocks.Add(facts);
			var dates = string.Format(CultureInfo.InvariantCulture, L(Texts.SnapshotLine), text(snapshotName),
				DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
			if (page.Tickets.Count > 0)
			{
				dates += " · " + L(Texts.PreviousTickets) + ": " + text(string.Join(", ", page.Tickets));
			}

			doc.Blocks.Add(new Paragraph(new DocRun(dates)) { FontSize = 11.5, Foreground = Muted, Margin = new Thickness(0, 0, 0, 14) });

			var basis = FindingsCatalog.BasisFor(item.Type, item.Detail)?.Basis;
			doc.Blocks.Add(AtAGlance(basis, summary));

			// What was found as the finding states it; for a pattern (a styled heading) the pattern itself.
			var found = item.Item.Found is { Length: > 0 } stated ? stated : DisplayDetail(item.Item.DetailIn(language));
			if (found.Length > 0)
			{
				doc.Blocks.Add(SubHeading(L(Texts.Found)));
				doc.Blocks.Add(item.Type == "STYLED_HEADING"
					? new Paragraph(new DocRun(text(found))) { FontFamily = Mono, FontSize = 11.5, Background = CardBack, Padding = new Thickness(8, 6, 8, 6) }
					: Context(item.Type, found, item.Detail, text));
			}

			doc.Blocks.Add(SubHeading(L(Texts.Examples)));
			var examples = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
			foreach (var (example, note) in summary.Examples)
			{
				if (examples.Inlines.Count > 0)
				{
					examples.Inlines.Add(new LineBreak());
				}

				var link = Link(example, text);
				link.FontSize = 11;
				examples.Inlines.Add(link);
				if (note.Length > 0)
				{
					examples.Inlines.Add(new LineBreak());
					examples.Inlines.Add(new DocRun("    " + text(note)) { Foreground = Muted, FontSize = 11.5 });
				}
			}

			doc.Blocks.Add(examples);

			doc.Blocks.Add(SubHeading(L(Texts.WhereItAppears)));
			var rows = summary.Sections.Select(s => (text(s.Section), s.Pages)).ToList();
			int shownPages = summary.Sections.Sum(s => s.Pages);
			if (summary.SectionCount > summary.Sections.Count)
			{
				rows.Add((string.Format(CultureInfo.InvariantCulture, L(Texts.Others), summary.SectionCount - summary.Sections.Count), summary.Pages - shownPages));
			}

			doc.Blocks.Add(CountLines(rows));

			if (summary.Frequent.Count > 0)
			{
				doc.Blocks.Add(SubHeading(L(Texts.Frequent)));
				doc.Blocks.Add(new Paragraph(new DocRun(string.Format(CultureInfo.InvariantCulture, L(Texts.FrequentNote), Core.Triage.SharedSummary.FrequentMin)))
				{
					FontSize = 11.5, Foreground = Muted, Margin = new Thickness(0, 0, 0, 4), KeepWithNext = true,
				});
				doc.Blocks.Add(CountLines(summary.Frequent.Select(f => (text(f.Text), f.Pages)).ToList()));
			}

			if (basis is not null)
			{
				// As on a page's ticket: on a new page only when the card's opening would not fit below.
				doc.Blocks.Add(FamilyBand(L(Texts.Background), null));
				doc.Blocks.Add(Card(basis));
			}

			// Every page, only for scale: paths under their host, small, nothing else — the appendix, on its own page.
			doc.Blocks.Add(FamilyBand(string.Format(CultureInfo.InvariantCulture, L(Texts.Scale), summary.Pages), null, newPage: true));
			foreach (var (host, paths) in summary.ByHost)
			{
				var list = new Paragraph { FontFamily = Mono, FontSize = 9, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8) };
				list.Inlines.Add(new DocRun(text(host)) { FontWeight = FontWeights.SemiBold, Foreground = Ink });
				foreach (var path in paths)
				{
					list.Inlines.Add(new LineBreak());
					list.Inlines.Add(new DocRun(text(path)));
				}

				doc.Blocks.Add(list);
			}
		}
	}

	/// <summary>The box a reader takes in first: what, where to fix, how, and how far it reaches.</summary>
	private static Block AtAGlance(FindingBasis? basis, Core.Triage.SharedSummary summary)
	{
		var box = new Section
		{
			Background = basis?.IsWcag == true ? WcagBack : CardBack,
			BorderBrush = Rule,
			BorderThickness = new Thickness(1),
			Padding = new Thickness(14, 10, 14, 10),
			Margin = new Thickness(0, 0, 0, 12),
		};
		box.Blocks.Add(new Paragraph(new DocRun(L(Texts.AtAGlance))) { FontSize = 11.5, Foreground = Muted, Margin = new Thickness(0, 0, 0, 6), KeepWithNext = true });

		void Line(string label, string value)
		{
			var para = new Paragraph { Margin = new Thickness(0, 0, 0, 6), KeepTogether = true };
			para.Inlines.Add(new Bold(new DocRun(label + ": ")));
			Cited(para.Inlines, value);
			box.Blocks.Add(para);
		}

		if (basis is not null)
		{
			var cardText = L(basis.Text);
			var verdict = FindingBasis.VerdictOf(cardText);
			var what = Core.Triage.SharedSummary.WhatOf(cardText, verdict);
			Line(L(Texts.What), what == verdict ? what : verdict + " " + what);
		}

		Line(L(Texts.WhereToFix), L(Texts.WhereToFixText));
		if (basis is not null && Core.Triage.SharedSummary.FixOf(L(basis.Text), language) is { } fix)
		{
			Line(L(Texts.Fix), TicketText.WithoutFixWord(fix));
		}

		Line(L(Texts.Scope), string.Format(CultureInfo.InvariantCulture, L(Texts.ScopeText), summary.Pages, summary.SectionCount, summary.Hosts));
		return box;
	}

	private static Block SubHeading(string heading) =>
		new Paragraph(new DocRun(heading))
		{
			FontSize = 13.5,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0, 24, 0, 4),
			KeepWithNext = true,
		};

	/// <summary>
	/// Names with their counts as lines, the count first and bold ("12 · de/ice"). Paragraphs, not a
	/// table: a table's columns come out empty or full-page in the paginated document.
	/// </summary>
	private static Block CountLines(IReadOnlyList<(string Name, int Count)> rows, string separator = " · ")
	{
		var para = new Paragraph { Margin = new Thickness(0, 0, 0, 6), FontSize = 11.5 };
		foreach (var (name, count) in rows)
		{
			if (para.Inlines.Count > 0)
			{
				para.Inlines.Add(new LineBreak());
			}

			para.Inlines.Add(new Bold(new DocRun(count.ToString(CultureInfo.InvariantCulture))));
			para.Inlines.Add(new DocRun(separator + name));
		}

		return para;
	}

	/// <summary>A file name to suggest: date and the page's path, e.g. "2026-10-12 de-home-page".</summary>
	public static string SuggestedName(string url)
	{
		// A shared defect is named by its title ("Styled headings — …"), a page by its path.
		var path = Core.Triage.SharedEntries.IsShared(url) ? "shared " + Core.Triage.SharedEntries.TitleOf(url)
			: Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
		path = Core.Triage.SharedEntries.IsShared(url) ? path : Path.ChangeExtension(path.Trim('/'), null) ?? string.Empty;
		var slug = new string(path.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
		while (slug.Contains("--", StringComparison.Ordinal))
		{
			slug = slug.Replace("--", "-", StringComparison.Ordinal);
		}

		if (slug.Length > 80)
		{
			slug = slug[..80].TrimEnd('-');
		}

		return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (slug.Length > 0 ? " " + slug : string.Empty);
	}

	/// <summary>
	/// The name of an item's section: its heading, or "Broken links" for broken links, which share
	/// one section and name their reach on each item.
	/// </summary>
	private static string SectionOf(TriageItemVm item) =>
		item.IsBrokenLink ? L(Core.Triage.BrokenLinkSource.Family) : HeadingOf(item.Item.Family, item.Item.Label);

	/// <summary>Items explained by the same card belong together; others by their heading.</summary>
	private static string KindOf(TriageItemVm item) =>
		FindingsCatalog.BasisFor(item.Type, item.Detail)?.Key ?? item.Heading;

	/// <summary>
	/// The head of the document, compact: the page (clickable in the saved document), the CMS editor
	/// link, then source, earlier tickets, snapshot and date on one line.
	/// </summary>
	private static Block Facts(TriagePageVm page, string snapshotName, Func<string, string> text)
	{
		var facts = new Section { Margin = new Thickness(0, 0, 0, 12) };
		var address = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
		address.Inlines.Add(Link(page.Url, text));
		facts.Blocks.Add(address);
		if (page.CmsLink.Length > 0)
		{
			var cms = new Paragraph { Margin = new Thickness(0, 0, 0, 4), FontSize = 11.5 };
			cms.Inlines.Add(new DocRun(L(Texts.CmsLink) + ": ") { Foreground = Muted });
			var link = Link(page.CmsLink, text);
			link.FontSize = 11;
			cms.Inlines.Add(link);
			facts.Blocks.Add(cms);
		}

		var line = new List<string>();
		if (page.SourceText.Length > 0)
		{
			line.Add($"{L(Texts.Source)}: {page.SourceText}");
		}

		if (page.Tickets.Count > 0)
		{
			line.Add($"{L(Texts.PreviousTickets)}: {text(string.Join(", ", page.Tickets))}");
		}

		line.Add(string.Format(CultureInfo.InvariantCulture, L(Texts.SnapshotLine), text(snapshotName),
			DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))); // the day only
		facts.Blocks.Add(new Paragraph(new DocRun(string.Join(" · ", line))) { FontSize = 11.5, Foreground = Muted, Margin = new Thickness(0) });
		return facts;
	}

	/// <summary>
	/// What the page's ticket asks for, at a glance: each kind of defect with its count, in the order
	/// of the sections below (likely WCAG issues and security marked), and what is new since the last ticket.
	/// </summary>
	private static Block PageAtAGlance(IReadOnlyList<TriageItemVm> ticketed, IReadOnlyList<TriageItemVm> locale, string? previous, Func<string, string> text)
	{
		var box = new Section
		{
			Background = CardBack,
			BorderBrush = Rule,
			BorderThickness = new Thickness(1),
			Padding = new Thickness(14, 10, 14, 6),
			Margin = new Thickness(0, 0, 0, 6),
		};
		box.Blocks.Add(new Paragraph(new DocRun(L(Texts.AtAGlance))) { FontSize = 11.5, Foreground = Muted, Margin = new Thickness(0, 0, 0, 4), KeepWithNext = true });
		var rows = new List<(string, int)>();
		foreach (var kind in ticketed.GroupBy(KindOf, StringComparer.Ordinal))
		{
			var first = kind.First();
			bool wcag = FindingsCatalog.BasisFor(first.Type, first.Detail)?.Basis.IsWcag == true;
			int security = kind.Count(i => i.IsSecurity);
			var name = SectionOf(first) + (wcag ? $" ({L(Texts.Wcag)})" : string.Empty)
				+ (security > 0 ? " · " + string.Format(CultureInfo.InvariantCulture, L(Texts.SecurityCount), security) : string.Empty);
			rows.Add((name, kind.Count()));
		}

		if (locale.Count > 0)
		{
			rows.Add((L(Texts.LocaleHeading), locale.Count));
		}

		box.Blocks.Add(CountLines(rows, " × "));
		int fresh = previous is null ? 0 : ticketed.Concat(locale).Count(i => !i.IsFiled);
		if (fresh > 0)
		{
			box.Blocks.Add(new Paragraph(new DocRun(string.Format(CultureInfo.InvariantCulture, L(Texts.NewCount), fresh, text(previous!))))
			{
				FontSize = 11.5, Margin = new Thickness(0, 2, 0, 4),
			});
		}

		return box;
	}

	/// <summary>A kind's gist under its heading: the card's opening and its fix, two lines; the full card is in the background part.</summary>
	private static Block Gist(FindingBasis basis)
	{
		var cardText = L(basis.Text);
		var verdict = FindingBasis.VerdictOf(cardText);
		var para = new Paragraph
		{
			Background = basis.IsWcag ? WcagBack : CardBack,
			Padding = new Thickness(12, 6, 12, 6),
			Margin = new Thickness(0, 0, 0, 8),
			KeepWithNext = true,
		};
		// The verdict alone may be a word ("Typography issue."): the sentence after it says what is wrong.
		para.Inlines.Add(new Bold(new DocRun(verdict)));
		var what = Core.Triage.SharedSummary.WhatOf(cardText, verdict);
		if (what != verdict)
		{
			Cited(para.Inlines, " " + what);
		}
		if (Core.Triage.SharedSummary.FixOf(cardText, language) is { } fix)
		{
			para.Inlines.Add(new LineBreak());
			Cited(para.Inlines, fix);
		}

		return para;
	}

	/// <summary>Spelling, compact: per word the word in bold and where it stands, marked; the languages checked are left out.</summary>
	private static IEnumerable<Block> SpellingItems(IReadOnlyList<TriageItemVm> items, string? previous, Func<string, string> text)
	{
		foreach (var item in items)
		{
			var section = new Section { Margin = new Thickness(0, 0, 0, 8), BorderBrush = Rule, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2) };
			var word = new Paragraph { Margin = new Thickness(0, 0, 0, 3), KeepWithNext = true };
			if (previous is not null && !item.IsFiled)
			{
				word.Inlines.Add(new DocRun($" {L(Texts.NewSince)} {text(previous)} ") { Background = NewBack, FontWeight = FontWeights.SemiBold });
				word.Inlines.Add(new DocRun("  "));
			}

			word.Inlines.Add(new DocRun(text(SpelledLanguages.Replace(item.Detail, string.Empty))) { FontWeight = FontWeights.SemiBold });
			section.Blocks.Add(word);
			section.Blocks.Add(Context(item.Type, item.Item.ContextIn(language), item.Detail, text));
			yield return section;
		}
	}

	// "word (de, en-GB)": the languages a word was checked against, the check's own setting.
	private static readonly System.Text.RegularExpressions.Regex SpelledLanguages = new(@"\s+\([^()]*\)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	/// <summary>"4 item(s) · 2 × Line-break glue (double space) · 1 × Links without a name (WCAG) · 1 new since ticket X".</summary>
	internal static string Summary(IReadOnlyList<TriageItemVm> ticketed, IReadOnlyList<TriageItemVm> locale, string? previous, Func<string, string> text)
	{
		var parts = new List<string> { string.Format(CultureInfo.InvariantCulture, L(Texts.ItemCount), ticketed.Count + locale.Count) };
		int security = ticketed.Count(i => i.IsSecurity);
		if (security > 0)
		{
			parts.Add(string.Format(CultureInfo.InvariantCulture, L(Texts.SecurityCount), security));
		}
		foreach (var kind in ticketed.GroupBy(KindOf, StringComparer.Ordinal))
		{
			var first = kind.First();
			var label = SectionOf(first);
			int arrow = label.LastIndexOf('›');
			label = arrow < 0 ? label : label[(arrow + 1)..].Trim();
			bool wcag = FindingsCatalog.BasisFor(first.Type, first.Detail)?.Basis.IsWcag == true;
			parts.Add($"{kind.Count().ToString(CultureInfo.InvariantCulture)} × {label}{(wcag ? $" ({L(Texts.Wcag)})" : string.Empty)}");
		}

		if (locale.Count > 0)
		{
			parts.Add($"{locale.Count.ToString(CultureInfo.InvariantCulture)} × {L(Texts.LocaleHeading)}");
		}

		int fresh = previous is null ? 0 : ticketed.Concat(locale).Count(i => !i.IsFiled);
		if (fresh > 0)
		{
			parts.Add(string.Format(CultureInfo.InvariantCulture, L(Texts.NewCount), fresh, text(previous!)));
		}

		return string.Join(" · ", parts);
	}

	/// <summary>
	/// An address as a link: clickable in the saved document (whether a PDF keeps it depends on
	/// the PDF printer). Masked on screen, it is plain text — a masked address leads nowhere.
	/// </summary>
	/// <summary>
	/// A card's words with each rule it cites ("UAX #14", "LB12a", "2.4.4") a link to it, in place;
	/// the address is written out under Links, for a printed page.
	/// </summary>
	private static void Cited(InlineCollection inlines, string text)
	{
		foreach (var piece in Core.Text.Citations.Split(text))
		{
			if (piece.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
			{
				inlines.Add(new Hyperlink(new DocRun(piece.Text)) { NavigateUri = uri, Foreground = LinkInk });
			}
			else
			{
				inlines.Add(new DocRun(piece.Text));
			}
		}
	}

	private static Inline Link(string url, Func<string, string> text)
	{
		var shown = text(url);
		var run = new DocRun(shown) { FontFamily = Mono };
		if (shown == url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
		{
			return new Hyperlink(run) { NavigateUri = uri, Foreground = LinkInk };
		}

		return run;
	}

	/// <summary>The page as a desktop search result would likely show it: address, title and description, cut where they would likely be cut.</summary>
	private static Block SearchPreview(Core.Seo.SearchSnippet snippet, Func<string, string> text)
	{
		var section = new Section { Margin = new Thickness(0, 6, 0, 14) };
		section.Blocks.Add(new Paragraph(new DocRun(L(Texts.PreviewHeading))) { FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 0, 0, 4), KeepWithNext = true });
		var box = new Section { BorderBrush = Rule, BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 12, 8) };
		box.Blocks.Add(new Paragraph(new DocRun(text(snippet.Url))) { FontSize = 11, Foreground = Muted, Margin = new Thickness(0) });
		box.Blocks.Add(new Paragraph(new DocRun(snippet.Title.Length > 0 ? text(snippet.TitleShown) : L(Texts.NoTitle)))
		{
			FontFamily = PreviewFont, FontSize = 18, Foreground = LinkInk, Margin = new Thickness(0, 2, 0, 0),
		});
		box.Blocks.Add(new Paragraph(new DocRun(snippet.Description.Length > 0 ? text(snippet.DescriptionShown) : L(Texts.NoDescription)))
		{
			FontFamily = PreviewFont, FontSize = 13, Margin = new Thickness(0, 4, 0, 0),
		});
		section.Blocks.Add(box);
		return section;
	}

	private static readonly FontFamily PreviewFont = new("Arial");

	/// <summary>The family an item's kind belongs to: its family, broken links one of their own.</summary>
	private static string FamilyOf(TriageItemVm item) =>
		item.IsBrokenLink ? Core.Triage.BrokenLinkSource.Family : item.Item.Family;

	/// <summary>
	/// Where a new topic begins: room above, then a full-width tinted band with a dark edge and the
	/// topic's name in capitals — and its count — so a reader skimming the pages sees that something
	/// unrelated starts. Visible in greyscale print too.
	/// </summary>
	private static Block FamilyBand(string name, int? count, bool newPage = false)
	{
		var band = new Paragraph
		{
			Background = BandBack,
			BorderBrush = BandEdge,
			BorderThickness = new Thickness(4, 0, 0, 0),
			Padding = new Thickness(10, 5, 10, 5),
			Margin = new Thickness(0, newPage ? 0 : 56, 0, 10),
			FontSize = 11.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = Ink,
			KeepWithNext = true,
			BreakPageBefore = newPage,
		};
		band.Inlines.Add(new DocRun(name.ToUpper(language == DocumentLanguage.German ? GermanCulture : CultureInfo.InvariantCulture)));
		if (count is { } n)
		{
			band.Inlines.Add(new DocRun("  ·  " + n.ToString(CultureInfo.InvariantCulture)) { Foreground = Muted });
		}

		return band;
	}

	private static readonly CultureInfo GermanCulture = CultureInfo.GetCultureInfo("de-DE");

	/// <summary>A kind's heading under its family's band: close below the band, with room above it after another kind.</summary>
	private static Block KindHeading(string name, int count, bool firstInFamily) =>
		new Paragraph(new DocRun($"{name}  ({count.ToString(CultureInfo.InvariantCulture)})"))
		{
			FontSize = 15,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0, firstInFamily ? 4 : 32, 0, 6),
			BorderBrush = Rule,
			BorderThickness = new Thickness(0, 0, 0, 1),
			Padding = new Thickness(0, 0, 0, 4),
			KeepWithNext = true,
		};

	/// <summary>The explanation card: its opening in bold, the text, for a likely WCAG issue the requirement and the legal benchmark, and the references written out.</summary>
	private static Block Card(FindingBasis basis)
	{
		var section = new Section
		{
			Background = basis.IsWcag ? WcagBack : CardBack,
			Padding = new Thickness(12, 8, 12, 8),
			Margin = new Thickness(0, 0, 0, 10),
			BorderBrush = Rule,
			BorderThickness = new Thickness(1),
		};
		var text = L(basis.Text);
		var verdict = FindingBasis.VerdictOf(text);
		var rest = text.Length > verdict.Length ? text[verdict.Length..].TrimStart() : string.Empty;
		// Kept whole and with what follows: the heading above it never stands alone at a page's foot.
		var lead = new Paragraph { Margin = new Thickness(0, 0, 0, 4), KeepWithNext = true, KeepTogether = true };
		lead.Inlines.Add(new Bold(new DocRun(verdict)));
		if (rest.Length > 0)
		{
			Cited(lead.Inlines, " " + rest);
		}

		section.Blocks.Add(lead);
		if (basis.IsWcag)
		{
			if (basis.Requirement is { Length: > 0 } requirement)
			{
				var required = new Paragraph { FontSize = 12, Margin = new Thickness(0, 6, 0, 2) };
				required.Inlines.Add(new DocRun(L(FindingBasis.RequirementLabel) + ": ") { Foreground = Muted });
				Cited(required.Inlines, L(requirement));
				section.Blocks.Add(required);
			}

			var legal = new Paragraph { FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 4, 0, 4) };
			Cited(legal.Inlines, L(FindingBasis.LegalNote));
			section.Blocks.Add(legal);
		}

		if (basis.AllLinks.Count > 0)
		{
			var links = new Paragraph { FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
			links.Inlines.Add(new DocRun(L(Texts.Links) + ":") { Foreground = Muted });
			foreach (var (label, url) in basis.AllLinks)
			{
				// The address is written out (a printed page or a PDF without links still has it), and clickable where the format keeps links.
				links.Inlines.Add(new LineBreak());
				links.Inlines.Add(new DocRun("• " + L(label) + " — "));
				var link = Link(url, t => t);
				link.FontSize = 11;
				links.Inlines.Add(link);
			}

			section.Blocks.Add(links);
		}

		return section;
	}

	/// <summary>One item: what was found, where (the context, marked as in the app), and whether it is new since the last ticket.</summary>
	private static Block Item(TriageItemVm item, string? previous, Func<string, string> text)
	{
		var section = new Section { Margin = new Thickness(0, 0, 0, 10), BorderBrush = Rule, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2) };
		var head = new Paragraph { Margin = new Thickness(0, 0, 0, 4), KeepWithNext = true };
		if (previous is not null && !item.IsFiled)
		{
			head.Inlines.Add(new DocRun($" {L(Texts.NewSince)} {text(previous)} ") { Background = NewBack, FontWeight = FontWeights.SemiBold });
			head.Inlines.Add(new DocRun("  "));
		}

		if (item.IsSecurity)
		{
			head.Inlines.Add(new DocRun($" {L(Texts.Security)} ") { Foreground = OffenderInk, Background = SecurityBack, FontWeight = FontWeights.SemiBold });
			head.Inlines.Add(new DocRun("  "));
		}

		if (item.IsBrokenLink)
		{
			// The reach first: who runs into it is what the recipient weighs.
			head.Inlines.Add(new DocRun(L(item.Item.Label) + " · ") { Foreground = Muted, FontWeight = FontWeights.SemiBold });
		}

		head.Inlines.Add(new DocRun(text(Readable(FindingDetails.In(language, item.Type, DisplayDetail(item.Item.DetailIn(language)))))) { FontWeight = FontWeights.SemiBold });
		section.Blocks.Add(head);

		var context = item.Item.ContextIn(language);

		// A styled heading's "as found: class=…" line names the template's markup, not a heading:
		// a caption of its own, above the list of headings.
		const string AsFound = "as found: ";
		if (item.Type == "STYLED_HEADING" && context.StartsWith(AsFound, StringComparison.Ordinal))
		{
			int end = context.IndexOf('\n');
			var found = L(AsFound) + (end < 0 ? context : context[..end])[AsFound.Length..];
			section.Blocks.Add(new Paragraph(new DocRun(text(found)) { Foreground = Muted })
			{
				FontFamily = Mono,
				FontSize = 10.5,
				Margin = new Thickness(0, 0, 0, 4),
				KeepWithNext = true,
			});
			context = end < 0 ? string.Empty : context[(end + 1)..];
		}

		if (context.Length > 0)
		{
			section.Blocks.Add(Context(item.Type, context, item.Detail, text));
		}

		// An item naming several pages (an image used on several): the item names the first, the ticket all of them.
		if (item.Item.Pages is { Count: > Core.Triage.SharedEntries.ListedInItem } pages)
		{
			var all = new Paragraph { Margin = new Thickness(0, 4, 0, 0), FontFamily = Mono, FontSize = 10.5 };
			all.Inlines.Add(new DocRun(string.Format(CultureInfo.InvariantCulture, L(Texts.AllPages), pages.Count)) { Foreground = Muted });
			foreach (var p in pages)
			{
				all.Inlines.Add(new LineBreak());
				all.Inlines.Add(Link(p, text));
				if (item.Item.PageNotes?.GetValueOrDefault(p) is { Length: > 0 } note)
				{
					// The app's words for how a page uses an image ("shown, linked"), each in the document's language.
					all.Inlines.Add(new DocRun(" — " + text(string.Join(", ", note.Split(", ").Select(L)))) { FontFamily = Body });
				}
			}

			section.Blocks.Add(all);
		}

		return section;
	}

	/// <summary>
	/// The detail as a person reads it: without the identity part some findings carry for tracking,
	/// and without the leading "[n]" position marker, which means nothing to a recipient.
	/// </summary>
	internal static string DisplayDetail(string detail)
	{
		int at = detail.IndexOf(" ¦¦", StringComparison.Ordinal);
		var shown = at < 0 ? detail : detail[..at];
		return LeadingPosition.Replace(shown, string.Empty);
	}

	private static readonly System.Text.RegularExpressions.Regex LeadingPosition = new(@"^\[\d+\]\s*", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	/// <summary>The finding's line for a reader (<see cref="TicketText.Readable"/>), with "(no text)" in the document's language.</summary>
	internal static string Readable(string line) => TicketText.Readable(line, L("(no text)"));

	/// <summary>The context in a monospaced block, with the app's marks drawn as light backgrounds.</summary>
	private static Block Context(string type, string context, string detail, Func<string, string> text)
	{
		MarkedContext marked;
		try
		{
			marked = QualityMarks.For(type, Core.Triage.SpellingSource.Type == type ? context : TicketText.WholeTags(context), detail, QualityContextCell.Settings);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
		{
			marked = new MarkedContext(context, [], null);
		}

		var value = marked.Text;
		var role = new MarkRole?[value.Length];
		foreach (var mark in marked.Marks.OrderBy(m => Priority(m.Role)))
		{
			for (int i = mark.Start; i < mark.Start + mark.Length && i < value.Length; i++)
			{
				role[i] = mark.Role;
			}
		}

		// The marker that makes an empty link visible, in the document's words, keeping its mark.
		(value, role) = TicketText.MarkerInWords(value, role, Core.Display.TriageHighlights.EmptyLinkMarker, "[" + L("Missing link text") + "]");

		var para = new Paragraph
		{
			FontFamily = Mono,
			FontSize = 11.5,
			Background = CardBack,
			Padding = new Thickness(8, 6, 8, 6),
			Margin = new Thickness(0, 0, 0, 0),
		};
		for (int start = 0; start < value.Length;)
		{
			int end = start + 1;
			while (end < value.Length && role[end] == role[start])
			{
				end++;
			}

			para.Inlines.Add(Styled(text(value[start..end]), role[start]));
			start = end;
		}

		if (marked.Note is { Length: > 0 } note)
		{
			// "Offender: …" names what the app found; the rest of a note is the app's own sentence.
			const string Offender = "Offender: ";
			var shown = note.StartsWith(Offender, StringComparison.Ordinal) ? L(Offender) + note[Offender.Length..] : L(note);
			para.Inlines.Add(new LineBreak());
			para.Inlines.Add(new DocRun(text(shown)) { Foreground = Muted, FontFamily = Body });
		}

		return para;
	}

	private static int Priority(MarkRole role) => role switch
	{
		MarkRole.Highlight => -1, // under everything
		MarkRole.Dim => 0,
		MarkRole.Tint => 1,
		MarkRole.Ok => 2,
		MarkRole.Partner => 3,
		MarkRole.Warn => 3,
		MarkRole.Fill => 4,
		_ => 5,
	};

	private static Inline Styled(string value, MarkRole? role)
	{
		var run = new DocRun(value);
		switch (role)
		{
			case MarkRole.Offender:
			case MarkRole.Fill:
				run.Background = OffenderBack;
				run.Foreground = OffenderInk;
				run.FontWeight = FontWeights.Bold;
				break;
			case MarkRole.Warn:
				run.Background = WarnBack;
				break;
			case MarkRole.Tint:
				run.Background = TintBack;
				break;
			case MarkRole.Partner:
				run.Background = PartnerBack;
				run.Foreground = OffenderInk;
				run.FontWeight = FontWeights.Bold;
				break;
			case MarkRole.Ok:
				run.Foreground = OkInk;
				run.FontWeight = FontWeights.SemiBold;
				break;
			case MarkRole.Dim:
				run.Foreground = Muted;
				break;
			case MarkRole.Highlight:
				run.Background = HighlighterBack;
				break;
		}

		return run;
	}

	private static Brush Frozen(byte r, byte g, byte b)
	{
		var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
		brush.Freeze();
		return brush;
	}

	/// <summary>
	/// The document's pages with a footer on each: app version, snapshot and "page n of m". The
	/// page count is final before the first page is drawn.
	/// </summary>
	internal sealed class FooterPaginator : DocumentPaginator
	{
		private readonly DocumentPaginator inner;
		private readonly string version;
		private readonly string snapshot;
		private readonly DocumentLanguage footerLanguage;

		public FooterPaginator(FlowDocument doc, string version, string snapshot, DocumentLanguage documentLanguage = DocumentLanguage.English)
		{
			footerLanguage = documentLanguage;
			inner = ((IDocumentPaginatorSource)doc).DocumentPaginator;
			inner.PageSize = TicketDocument.PageSize;
			inner.ComputePageCount();
			this.version = version;
			this.snapshot = snapshot;
		}

		public override bool IsPageCountValid => inner.IsPageCountValid;

		public override int PageCount => inner.PageCount;

		public override Size PageSize
		{
			get => inner.PageSize;
			set => inner.PageSize = value;
		}

		public override IDocumentPaginatorSource Source => inner.Source;

		public override DocumentPage GetPage(int pageNumber)
		{
			var page = inner.GetPage(pageNumber);
			var container = new ContainerVisual();
			container.Children.Add(page.Visual);

			var footer = new DrawingVisual();
			using (var dc = footer.RenderOpen())
			{
				var line = DocumentText.F(footerLanguage, Texts.Footer, version, snapshot, pageNumber + 1, PageCount);
				var formatted = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
					new Typeface(Body, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, Muted, 1.0);
				dc.DrawText(formatted, new Point(56, page.Size.Height - 48));
			}

			container.Children.Add(footer);
			return new DocumentPage(container, page.Size, page.BleedBox, page.ContentBox);
		}
	}
}
