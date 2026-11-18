using System.Text.RegularExpressions;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Formats;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// One thing to decide on a page: a finding, or several findings decided together (the
	/// adjacent anchors of a page, identical findings). <see cref="Keys"/> are the ledger keys
	/// a verdict is recorded under.
	/// </summary>
	public sealed record TriageItem(
		string ItemKey,
		IReadOnlyList<string> Keys,
		string Page,
		string Type,
		string SwitchId,
		string Family,
		string Label,
		string Detail,
		string Context,
		string? LocalFile,
		string ValidIn = "",
		IReadOnlyList<string>? Pages = null,
		IReadOnlyDictionary<string, string>? PageNotes = null,
		Seo.SearchSnippet? Snippet = null,
		string? Found = null,
		string? DetailDe = null,
		string? ContextDe = null,
		string? Markup = null,
		string? MarkupDe = null)
	{
		// Markup, MarkupDe: a spelling finding as the page's own HTML (SpellingMarkup), shown in place
		// of the context in triage and in the ticket; null where there is none. The context stays the
		// plain excerpt: the ticket sweep and the search read that.

		// Found: for a shared entry, what was found as the finding states it — its context before the
		// page list was added (a styled heading's "as found" line); null for a page's own item.
		// DetailDe, ContextDe: the detail and context in German where the app wrote them (a sentence of
		// its own); null where they are the site's or a log's text, the same in every language.

		/// <summary>The detail in a document language.</summary>
		public string DetailIn(Text.DocumentLanguage language) =>
			language == Text.DocumentLanguage.German && DetailDe is not null ? DetailDe : Detail;

		/// <summary>The context in a document language.</summary>
		public string ContextIn(Text.DocumentLanguage language) =>
			language == Text.DocumentLanguage.German && ContextDe is not null ? ContextDe : Context;

		/// <summary>What triage and the ticket show of the context: the markup where there is one, else the context.</summary>
		public string ShownIn(Text.DocumentLanguage language) =>
			(language == Text.DocumentLanguage.German ? MarkupDe ?? Markup : Markup) ?? ContextIn(language);

		/// <summary>An entry for one defect repeated on several pages (<see cref="SharedEntries"/>), not a page.</summary>
		public bool IsShared => SharedEntries.IsShared(Page);

		/// <summary>A finding of an unwanted-pattern group marked security-relevant, or a link handing the server another site's address.</summary>
		public bool IsSecurity => Type == ForwardingLinkSource.External || Quality.SecurityPatterns.IsSecurity(Type, Detail);
	}

	/// <summary>Reads a snapshot's content-quality findings (logs 10 and 22), broken links (log 07), self-links (log 09), canonical issues (log 16), search appearance (log 08), image findings (logs 25 and 38) and spelling findings (logs 11–13) as triage items.</summary>
	public static partial class TriageSource
	{
		// Positions and counts change from crawl to crawl; they must not change a finding's identity.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex LeadingPositionRegex = new(@"^\[\d+\]\s*", RegexOptions.Compiled);

		private static Regex LeadingPosition() => LeadingPositionRegex;

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex DigitsRegex = new(@"\d+", RegexOptions.Compiled);

		private static Regex Digits() => DigitsRegex;

		/// <summary>
		/// The ledger key of a finding: type, page and its detail with numbers neutralised
		/// ("found at 3 position(s)" and "found at 4 position(s)" are the same finding).
		/// </summary>
		public static string KeyOf(string type, string page, string detail)
		{
			// The page language a quote finding names is how it was judged, not what was found: the
			// same finding keeps its verdict with or without it.
			var normal = Digits().Replace(LeadingPosition().Replace(Quality.Quotes.WithoutPageLanguage(detail.Trim()), string.Empty), "#");
			return $"{type}|{page}|{normal}";
		}

		/// <summary>
		/// Whether the snapshot holds the content-quality logs triage reads (10 and 22). Without them
		/// — a crawl only, an aborted analysis, content quality switched off — "no finding" means
		/// "not looked", not "all fixed", so the ledger must not be matched against it.
		/// </summary>
		public static bool HasFindingLogs(string snapshotDirectory) =>
			OutputFormats.ByNumber("10") is { } editor && File.Exists(Path.Combine(snapshotDirectory, editor.PrimaryFileName))
			&& OutputFormats.ByNumber("22") is { } template && FindingFiles.PairPath(snapshotDirectory, template.PrimaryFileName) is not null;

		/// <summary>
		/// Every content-quality finding of the snapshot as triage items, grouped by page, then its
		/// spelling findings (one item per word per page; <paramref name="decidedWord"/> names words a
		/// dictionary holds by now, which are left out). Off the UI thread.
		/// </summary>
		/// <param name="titlePatterns">The site's title patterns (Seo.TitleTemplates), for the search-appearance items.</param>
		/// <param name="canonicalsOfNoindexPages">
		/// Seo.TriageCanonicalsOfNoindexPages: false leaves out the canonical issues of pages kept out
		/// of search by noindex (log 08), as the search-appearance items already are.
		/// </param>
		public static IReadOnlyList<TriageItem> Read(string snapshotDirectory, Func<string, bool>? decidedWord = null, IReadOnlyList<string>? titlePatterns = null, bool canonicalsOfNoindexPages = false)
		{
			var urlByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var index = Path.Combine(snapshotDirectory, LogFileNames.CrawlerIndex);
			if (File.Exists(index))
			{
				foreach (var c in FindingFiles.Delimited(index, "|", 3, header: false))
				{
					if (c[0].Length > 0 && c[1].Length > 0)
					{
						urlByFile.TryAdd(c[0], c[1]);
					}
				}
			}

			var download = Path.Combine(snapshotDirectory, "download");
			string? Local(string file) =>
				file.Length > 0 && file.IndexOfAny(['/', '\\']) < 0 && File.Exists(Path.Combine(download, file))
					? Path.Combine(download, file)
					: null;
			string UrlOf(string file) => urlByFile.TryGetValue(file, out var url) ? url : file;

			var raw = new List<(string Type, string Page, string Detail, string Context, string? Local)>();
			if (OutputFormats.ByNumber("10") is { } editor && Path.Combine(snapshotDirectory, editor.PrimaryFileName) is var editorPath
				&& File.Exists(editorPath))
			{
				foreach (var c in FindingFiles.Delimited(editorPath, "|", 4, header: true))
				{
					raw.Add((BaseType(c[1]), UrlOf(c[0]), c[2], c[3], Local(c[0])));
				}
			}

			if (OutputFormats.ByNumber("22") is { } template && FindingFiles.PairPath(snapshotDirectory, template.PrimaryFileName) is { } templatePath)
			{
				foreach (var c in FindingFiles.Csv(templatePath, 5, header: true))
				{
					raw.Add((BaseType(c[2]), c[1].Length > 0 ? c[1] : UrlOf(c[0]), c[3], c[4], Local(c[0])));
				}
			}

			// One defect repeated on several pages: one shared entry, taken off those pages.
			var items = SharedEntries.Apply(Group(raw)).ToList();
			var fileByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var (file, url) in urlByFile)
			{
				fileByUrl.TryAdd(url, file);
			}

			// Broken links (log 07): before spelling, as on the page.
			string? LocalOfUrl(string url) => fileByUrl.TryGetValue(url, out var saved) ? Local(saved) : null;
			items.AddRange(BrokenLinkSource.Read(snapshotDirectory, LocalOfUrl));

			// Self-links (log 09) and canonical issues (log 16): page items like content quality.
			items.AddRange(SelfLinkSource.Read(snapshotDirectory, LocalOfUrl));
			var canonicals = CanonicalSource.Read(snapshotDirectory, LocalOfUrl);
			if (!canonicalsOfNoindexPages && canonicals.Count > 0)
			{
				var search = SeoSource.SearchByPage(snapshotDirectory);
				canonicals = canonicals.Where(i => !SeoSource.IsNoindex(search.TryGetValue(i.Page, out var s) ? s : null)).ToList();
			}

			items.AddRange(canonicals);

			// Forwarding links (log 40): a page's links that hand the server an address to forward to.
			items.AddRange(ForwardingLinkSource.Read(snapshotDirectory, LocalOfUrl));

			// How pages in search appear there (log 08).
			items.AddRange(SeoSource.Read(snapshotDirectory, LocalOfUrl, titlePatterns));

			// Images (log 25), placed by who uses them (log 38).
			items.AddRange(ImageSource.Read(snapshotDirectory, LocalOfUrl));

			// Each saved page is parsed once for the markup of its spelling findings, which log 12 lists
			// page by page; only a few pages are held at a time.
			var parsed = new Dictionary<string, SpellingMarkup.Page?>(StringComparer.OrdinalIgnoreCase);
			SpellingMarkup.Page? PageOf(string? local)
			{
				if (local is null)
				{
					return null;
				}

				if (!parsed.TryGetValue(local, out var doc))
				{
					if (parsed.Count >= 8)
					{
						parsed.Clear();
					}

					doc = SpellingMarkup.Load(local);
					parsed[local] = doc;
				}

				return doc;
			}

			foreach (var f in SpellingSource.Read(snapshotDirectory, decidedWord))
			{
				var key = KeyOf(SpellingSource.Type, f.Page, f.Detail);
				var local = fileByUrl.TryGetValue(f.Page, out var file) ? Local(file) : null;
				var occurrences = f.Occurrences ?? [];
				var page = occurrences.Count > 0 ? PageOf(local) : null;
				items.Add(Item(SpellingSource.Type, f.Page, [key], f.Detail, f.Context, local) with
				{
					ValidIn = f.ValidIn,
					ContextDe = f.ContextDe,
					Markup = SpellingMarkup.For(page, f.Word, occurrences, Text.DocumentLanguage.English),
					MarkupDe = SpellingMarkup.For(page, f.Word, occurrences, Text.DocumentLanguage.German),
				});
			}

			return items;
		}

		/// <summary>Builds the items: adjacent anchors one per page; identical findings (same key) one item.</summary>
		internal static IReadOnlyList<TriageItem> Group(IEnumerable<(string Type, string Page, string Detail, string Context, string? Local)> raw)
		{
			var items = new List<TriageItem>();
			foreach (var page in raw.GroupBy(r => r.Page, StringComparer.OrdinalIgnoreCase))
			{
				foreach (var byType in page.GroupBy(r => r.Type, StringComparer.Ordinal))
				{
					if (byType.Key == "ADJACENT_ANCHOR")
					{
						var rows = byType.Select(r => new FindingRow([r.Page, r.Detail, r.Context], r.Local)).ToList();
						var joined = FindingsCatalog.AdjacentByPage(rows)[0];
						var keys = byType.Select(r => KeyOf(r.Type, r.Page, r.Detail)).Distinct(StringComparer.Ordinal).ToList();
						items.Add(Item(byType.Key, page.Key, keys, joined.Cells[1], joined.Cells[2], joined.LocalFile));
						continue;
					}

					foreach (var same in byType.GroupBy(r => KeyOf(r.Type, r.Page, r.Detail), StringComparer.Ordinal))
					{
						var first = same.First();
						// A styled heading lists every heading text: one line each on the page and in the ticket.
						var contexts = same.Select(r => r.Type == Quality.StyledHeadings.IssueType
							? string.Join("\n", Quality.StyledHeadings.Lines(r.Context))
							: r.Context).Distinct(StringComparer.Ordinal);
						items.Add(Item(byType.Key, page.Key, [same.Key], first.Detail, string.Join("\n", contexts), first.Local));
					}
				}
			}

			return items;
		}

		private static TriageItem Item(string type, string page, IReadOnlyList<string> keys, string detail, string context, string? local)
		{
			var switchId = TriageSwitches.IdOf(type, detail);
			var (family, label) = TriageSwitches.Describe(switchId);
			return new TriageItem(string.Join("‖", keys), keys, page, type, switchId, family, label, detail, context, local);
		}

		/// <summary>"MALFORMED_HTML:12" → "MALFORMED_HTML".</summary>
		private static string BaseType(string type)
		{
			int colon = type.IndexOf(':');
			return (colon < 0 ? type : type[..colon]).Trim();
		}
	}
}
