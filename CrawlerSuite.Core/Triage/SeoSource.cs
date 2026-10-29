using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Formats;
using CrawlerSuite.Core.Seo;
using CrawlerSuite.Core.Security;
using CrawlerSuite.Core.Text;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// How pages in search appear there (log 08), as page items: each is fixed in the page's own
	/// settings by its editor, so even a title several pages share is an item on each of them, with
	/// the others named. Only pages that may appear in search: a page out of search (noindex in
	/// the page or by header, a canonical naming another page) gets no item. Every item carries
	/// the page's search result preview. The page's main heading (H1) is read from the same log,
	/// under a family of its own: no H1, several different ones, or one written several times.
	/// </summary>
	public static class SeoSource
	{
		public const string Family = "Search appearance";

		public const string Keywords = "SEO_KEYWORDS";
		public const string TitleMissing = "SEO_TITLE_MISSING";
		public const string TitlePatternMissed = "SEO_TITLE_PATTERN";
		public const string TitleShared = "SEO_TITLE_SHARED";
		public const string TitleCut = "SEO_TITLE_CUT";
		public const string DescriptionMissing = "SEO_DESCRIPTION_MISSING";
		public const string DescriptionShared = "SEO_DESCRIPTION_SHARED";
		public const string DescriptionCut = "SEO_DESCRIPTION_CUT";

		public const string HeadingFamily = "Main heading";
		public const string H1Missing = "SEO_H1_MISSING";
		public const string H1Several = "SEO_H1_SEVERAL";
		public const string H1Repeated = "SEO_H1_REPEATED";

		/// <summary>
		/// The main-heading switches. Ride-along, like search appearance: on a whole site they list too
		/// many pages at once; they go into a page's ticket when the page is open for something else.
		/// Several H1s with one text are off: a template that writes the heading once per screen width
		/// and hides all but one is right, and the HTML alone cannot tell that from one that shows them all.
		/// </summary>
		public static IReadOnlyList<(string Type, string Label, TriageMode Default)> HeadingTypes { get; } =
		[
			(H1Missing, "No main heading (H1)", TriageMode.RideAlong),
			(H1Several, "Several H1s with different texts", TriageMode.RideAlong),
			(H1Repeated, "Several H1s with the same text", TriageMode.Off),
		];

		/// <summary>
		/// The switches, in order, with their labels and defaults. All ride along: search appearance is
		/// where an operator goes deeper once the pages are open for other reasons; on by default, it lists
		/// a large share of a site at once, which nobody starts on. Switch them on for an SEO pass.
		/// </summary>
		public static IReadOnlyList<(string Type, string Label, TriageMode Default)> Types { get; } =
		[
			(TitleMissing, "Title missing", TriageMode.RideAlong),
			(TitlePatternMissed, "Title doesn't follow the site's title pattern", TriageMode.RideAlong),
			(TitleShared, "Same title as other pages", TriageMode.RideAlong),
			(TitleCut, "Title likely cut off", TriageMode.RideAlong),
			(DescriptionMissing, "Description missing", TriageMode.RideAlong),
			(DescriptionShared, "Same description as other pages", TriageMode.RideAlong),
			(DescriptionCut, "Description likely cut off", TriageMode.RideAlong),
			(Keywords, "Meta keywords present", TriageMode.RideAlong),
		];

		/// <summary>Pages named on an item before "… and N more"; the ticket names them all.</summary>
		internal const int PagesListed = 10;

		public static bool IsSeo(string type) => Types.Any(t => t.Type == type) || HeadingTypes.Any(t => t.Type == type);

		/// <summary>Whether the snapshot holds log 08; without it "no finding" means "not looked".</summary>
		public static bool HasLogs(string snapshotDirectory) =>
			OutputFormats.ByNumber("08") is { } f && File.Exists(Path.Combine(snapshotDirectory, f.PrimaryFileName));

		/// <summary>Verdicts a match against this snapshot must keep: all SEO ones when it has no log 08.</summary>
		public static IEnumerable<string> KeptWithoutLogs(string snapshotDirectory, TriageLedger ledger) =>
			HasLogs(snapshotDirectory) ? [] : ledger.Findings.Values.Where(f => IsSeo(f.Type)).Select(f => f.Key).ToList();

		/// <summary>The ledger key: the finding and the page — not the text, so an edited text keeps its verdict until fixed.</summary>
		public static string KeyOf(string type, string page) => $"{type}|{page}";

		/// <summary>One page as log 08 has it.</summary>
		/// <param name="H1Count">The number of H1s; null when not readable.</param>
		/// <param name="H1s">Their texts; null for a log written before column h1.</param>
		internal sealed record Row(string Page, string Robots, string Title, string Description, string Keywords, string Search,
			int? H1Count = null, IReadOnlyList<string>? H1s = null);

		/// <summary>The search-appearance items of a snapshot. <paramref name="localOf"/> gives a page's saved file (null when none).</summary>
		/// <param name="titlePatterns">The site's title patterns (Seo.TitleTemplates); none: no pattern check, the whole title measured.</param>
		public static IReadOnlyList<TriageItem> Read(string snapshotDirectory, Func<string, string?> localOf, IReadOnlyList<string>? titlePatterns = null)
		{
			if (OutputFormats.ByNumber("08") is not { } format)
			{
				return [];
			}

			var path = Path.Combine(snapshotDirectory, format.PrimaryFileName);
			if (!File.Exists(path))
			{
				return [];
			}

			// url, robots, title, title_length, description, description_length, keywords, source, h1_count, search, canonical, h1
			bool hasH1Texts = FindingFiles.Csv(path, 12, header: false).FirstOrDefault() is { } head && head[11].Trim() == "h1";
			var rows = FindingFiles.Csv(path, 12, header: true)
				.Select(c => new Row(c[0].Trim(), c[1], CsvInjectionGuard.Denormalize(c[2]), CsvInjectionGuard.Denormalize(c[4]),
					CsvInjectionGuard.Denormalize(c[6]), c[9].Trim(),
					int.TryParse(c[8].Trim(), out var count) ? count : null,
					hasH1Texts ? SEO.H1TextsOf(CsvInjectionGuard.Denormalize(c[11]), count) : null))
				.ToList();
			return Items(rows, localOf, titlePatterns);
		}

		/// <summary>
		/// Whether a page may appear in search: the search column; for a log written before it
		/// existed, the meta robots alone.
		/// </summary>
		internal static bool InSearch(Row row) =>
			row.Search.Length > 0 ? row.Search == SEO.Search.In : !SearchRobots.IsOutOfSearch(row.Robots);

		internal static IReadOnlyList<TriageItem> Items(IReadOnlyList<Row> rows, Func<string, string?> localOf, IReadOnlyList<string>? titlePatterns = null)
		{
			var pages = rows
				.Where(r => r.Page.Length > 0 && r.Page != "error" && InSearch(r))
				.DistinctBy(r => r.Page, StringComparer.OrdinalIgnoreCase)
				.Select(r => (Row: r, Snippet: SearchSnippet.Of(r.Page, r.Title, r.Description), Keywords: SearchSnippet.Clean(r.Keywords)))
				.ToList();

			// The pages sharing a title or description, case aside.
			static Dictionary<string, List<string>> Sharing(IEnumerable<(string Text, string Page)> texts) =>
				texts.Where(t => t.Text.Length > 0)
					.GroupBy(t => t.Text, StringComparer.OrdinalIgnoreCase)
					.Where(g => g.Count() > 1)
					.ToDictionary(g => g.Key, g => g.Select(t => t.Page).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
			var titles = Sharing(pages.Select(p => (p.Snippet.Title, p.Row.Page)));
			var descriptions = Sharing(pages.Select(p => (p.Snippet.Description, p.Row.Page)));

			// What tells a page published twice from two pages with one title: the same title,
			// description and main heading (the H1s only when the log has them).
			var likeness = pages.ToDictionary(p => p.Row.Page,
				p => (p.Snippet.Title, p.Snippet.Description, H1: p.Row.H1s is { } h1s ? string.Join("\n", h1s) : null),
				StringComparer.OrdinalIgnoreCase);
			bool SamePage(string a, string b)
			{
				var (x, y) = (likeness[a], likeness[b]);
				return x.Title.Length > 0 && x.Description.Length > 0
					&& string.Equals(x.Title, y.Title, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(x.Description, y.Description, StringComparison.OrdinalIgnoreCase)
					&& (x.H1 is null || y.H1 is null || string.Equals(x.H1, y.H1, StringComparison.OrdinalIgnoreCase));
			}

			var items = new List<TriageItem>();
			foreach (var (row, snippet, keywords) in pages)
			{
				var local = localOf(row.Page);

				// Every sentence the app writes is built once per document language; the page's own texts stay as they are.
				void Add(string type, string detail, Func<DocumentLanguage, string> context, Func<DocumentLanguage, string>? detailIn = null)
				{
					var (family, label) = TriageSwitches.Describe(type);
					var key = KeyOf(type, row.Page);
					items.Add(new TriageItem(key, [key], row.Page, type, type, family, label,
						detailIn?.Invoke(DocumentLanguage.English) ?? detail, context(DocumentLanguage.English), local, Snippet: snippet,
						DetailDe: detailIn?.Invoke(DocumentLanguage.German), ContextDe: context(DocumentLanguage.German)));
				}

				var (own, before, matches) = TitlePattern.Of(snippet.Title, titlePatterns);
				if (snippet.Title.Length == 0)
				{
					Add(TitleMissing, string.Empty, l => T(l, "The page has no title: a search engine makes one up from the page."), l => T(l, "(no title)"));
				}
				else if (own.Length == 0)
				{
					Add(TitleMissing, snippet.Title, l => T(l, "The title is only the text the site adds to every title: the page's own title is empty."));
				}
				else
				{
					if (!matches)
					{
						Add(TitlePatternMissed, snippet.Title, l => T(l, "Matches none of the site's title patterns (Settings › SEO & links › Title patterns):") + "\n" +
							string.Join("\n", titlePatterns!.Where(p => !string.IsNullOrEmpty(p)).Select(p => "  " + p)));
					}

					if (titles.TryGetValue(snippet.Title, out var same))
					{
						Add(TitleShared, snippet.Title, l => Others(true, same, row.Page, SamePage, l));
					}

					// Only the page's own part counts: the text the site adds after it may be cut off by design.
					int ownWidth = SearchSnippet.WidthOf(before + own, SearchSnippet.TitleFontPixels);
					if (ownWidth > SearchSnippet.TitleLimitPixels)
					{
						bool added = own.Length < snippet.Title.Length;
						Add(TitleCut, snippet.Title, l => F(l, "Shown as: {0}", snippet.TitleShown) + "\n" +
							F(l, "The page's own title reaches about {0} px; a result shows about {1} px (estimate).", ownWidth, SearchSnippet.TitleLimitPixels) +
							(added ? " " + T(l, "The text the site adds to every title is not counted: it may be cut off.") : string.Empty));
					}
				}

				if (snippet.Description.Length == 0)
				{
					Add(DescriptionMissing, string.Empty, l => T(l, "The page has no meta description: a search engine picks text from the page instead."),
						l => T(l, "(no description)"));
				}
				else
				{
					if (descriptions.TryGetValue(snippet.Description, out var same))
					{
						Add(DescriptionShared, snippet.Description, l => Others(false, same, row.Page, SamePage, l));
					}

					if (snippet.DescriptionCut)
					{
						Add(DescriptionCut, snippet.Description, l => F(l, "Shown as: {0}", snippet.DescriptionShown) + "\n" +
							F(l, "About {0} px wide; a result shows about {1} px (estimate).", snippet.DescriptionWidth, SearchSnippet.DescriptionLimitPixels));
					}
				}

				if (keywords.Length > 0)
				{
					Add(Keywords, keywords, _ => string.Empty); // the card's fix says what to do: the item names the keywords only
				}

				Headings(row, Add);
			}

			return items;
		}

		private static string T(DocumentLanguage language, string english) => DocumentText.T(language, english);

		private static string F(DocumentLanguage language, string englishFormat, params object?[] args) => DocumentText.F(language, englishFormat, args);

		/// <summary>The main-heading items of one page: none, an empty one, several different, or one written several times.</summary>
		private static void Headings(Row row, Action<string, string, Func<DocumentLanguage, string>, Func<DocumentLanguage, string>?> add)
		{
			if (row.H1Count == 0)
			{
				add(H1Missing, string.Empty, l => T(l, "The page has no H1: no heading names what the page is about."), l => T(l, "(no H1)"));
				return;
			}

			if (row.H1s is not { Count: > 0 } h1s)
			{
				return; // a log written before column h1: one H1 or several, the texts are not known
			}

			if (h1s.All(t => t.Length == 0))
			{
				add(H1Missing, string.Empty, l => h1s.Count == 1
					? T(l, "The page's H1 has no text: a screen reader announces an empty heading.")
					: F(l, "The page's {0} H1s have no text: a screen reader announces empty headings.", h1s.Count),
					l => T(l, "(H1 without text)"));
				return;
			}

			if (h1s.Count < 2)
			{
				return;
			}

			static string Shown(string text, DocumentLanguage l) => text.Length == 0 ? T(l, "(no text)") : text;
			var distinct = h1s.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (distinct.Count > 1)
			{
				add(H1Several, string.Empty,
					l => F(l, "{0} H1s, in page order:", h1s.Count) + "\n" + string.Join("\n", h1s.Select((t, i) => $"  {i + 1}. {Shown(t, l)}")),
					l => string.Join(" · ", distinct.Select(t => Shown(t, l))));
			}
			else
			{
				add(H1Repeated, h1s[0], l => F(l, "{0} H1s, all reading \"{1}\". Fine when all but one are hidden at every screen width " +
					"(display:none or the hidden attribute): then a screen reader hears it once. To check, run the browser's " +
					"developer tools at a wide and a narrow width.", h1s.Count, h1s[0]), null);
			}
		}

		/// <summary>
		/// "The same title on 3 other page(s) in search.", then the pages, the first ten of each kind:
		/// those that look like this page published again (same title, description and main heading),
		/// where the fix is to choose one address, and the rest, which each need a text of their own.
		/// </summary>
		/// <param name="title">The title is shared; else the description.</param>
		internal static string Others(bool title, IReadOnlyList<string> sharing, string page, Func<string, string, bool>? samePage = null,
			DocumentLanguage language = DocumentLanguage.English)
		{
			var others = sharing.Where(p => !p.Equals(page, StringComparison.OrdinalIgnoreCase)).ToList();
			var copies = samePage is null ? [] : others.Where(p => samePage(page, p)).ToList();
			var distinct = others.Except(copies, StringComparer.OrdinalIgnoreCase).ToList();

			var lines = new List<string>
			{
				title ? F(language, "The same title on {0} other page(s) in search.", others.Count)
					: F(language, "The same description on {0} other page(s) in search.", others.Count),
			};
			void List(string heading, List<string> pages)
			{
				if (pages.Count == 0)
				{
					return;
				}

				lines.Add(heading);
				lines.AddRange(pages.Take(PagesListed).Select(p => "  " + p));
				if (pages.Count > PagesListed)
				{
					lines.Add(F(language, "  … and {0} more", pages.Count - PagesListed));
				}
			}

			List(T(language, "Likely the same page at another address — same title, description and main heading: decide which address " +
				"stays in search and give the others a canonical to it or a noindex:"), copies);
			List(title ? T(language, "Each needs its own title:") : T(language, "Each needs its own description:"), distinct);
			return string.Join("\n", lines);
		}
	}
}
