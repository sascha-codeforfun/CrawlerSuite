namespace CrawlerSuite.Core
{
	using HtmlAgilityPack;

	public class SEO
	{
		/// <summary>
		/// Writes log 08 and returns, per page file (full path), the raw href values of its
		/// canonical links in document order. The canonical-link step reads those instead of
		/// parsing every page a second time (same file, same decoding, same parser settings).
		/// </summary>
		public static async Task<IReadOnlyDictionary<string, string[]>> ExtractDataAsync(string directoryPath, string outputFilePath, string filePattern) =>
			(await HarvestAsync(directoryPath, outputFilePath, filePattern)).CanonicalHrefsByFile;

		/// <summary>The last column of log 08: whether the page may appear in search, and if not, why — as far as the snapshot can tell (robots.txt is not read).</summary>
		public static class Search
		{
			public const string In = "in search";
			public const string NoindexInPage = "noindex in page";
			public const string NoindexByHeader = "noindex by header";
			public const string CanonicalElsewhere = "canonical elsewhere";

			/// <summary>Noindex in the page first, else by header; else a canonical naming another page; else in search.</summary>
			public static string Of(string metaRobots, IEnumerable<string> headerValues, string pageUrl, string[] canonicalHrefs) =>
				SearchRobots.IsOutOfSearch(metaRobots) ? NoindexInPage
				: SearchRobots.IsOutOfSearch(null, headerValues) ? NoindexByHeader
				: SEO.CanonicalElsewhere(pageUrl, canonicalHrefs) ? CanonicalElsewhere
				: In;
		}

		/// <summary>Between the texts of the H1s in column h1: a character headings hardly ever hold.</summary>
		public const string H1Separator = " ¦ ";

		/// <summary>
		/// The texts of column h1, in document order. <paramref name="count"/> (column h1_count)
		/// tells one H1 without text from none, which both write an empty column.
		/// </summary>
		public static IReadOnlyList<string> H1TextsOf(string column, int count) =>
			column.Trim().Length == 0 && !column.Contains(H1Separator.Trim())
				? Enumerable.Repeat(string.Empty, Math.Max(count, 0)).ToList()
				: column.Split(H1Separator.Trim()).Select(t => t.Trim()).ToList();

		/// <summary>The address the page's first canonical names, resolved against the page; empty without one.</summary>
		internal static string CanonicalTarget(string pageUrl, string[] canonicalHrefs)
		{
			if (canonicalHrefs.Length == 0)
			{
				return string.Empty;
			}

			var href = HtmlEntity.DeEntitize(canonicalHrefs[0]).Trim();
			return href.Length == 0 ? string.Empty
				: Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && Uri.TryCreate(page, href, out var target)
				? target.AbsoluteUri
				: href;
		}

		/// <summary>Pages that may appear in search, as far as the snapshot can tell (robots.txt is not read).</summary>
		public sealed record SearchFacts(int Pages, int NoindexInPage, int NoindexByHeader, int CanonicalElsewhere)
		{
			public int Eligible => Pages - NoindexInPage - NoindexByHeader - CanonicalElsewhere;
		}

		/// <summary>Log 08, the canonical hrefs per page file, and how many pages are eligible for search.</summary>
		public static async Task<(IReadOnlyDictionary<string, string[]> CanonicalHrefsByFile, SearchFacts Facts)> HarvestAsync(
			string directoryPath, string outputFilePath, string filePattern)
		{
			var htmlFiles = CrawlerSuite.Core.Host.FolderFiles.Sorted(directoryPath, filePattern);

			// Route through IssueLogWriter.ComposeCsvLine so each crawled field (title / description /
			// robots / keywords) is sanitized for CR / LF / control chars / bidi controls and then
			// RFC 4180-quoted for the ';' delimiter — SEO content holds ';', ',', '|', '@' freely, so
			// no delimiter is safe to strip; quoting preserves the content verbatim.
			// [KEEP] Sanitization through IssueLogWriter is mandatory — a local
			// escape of CR/LF/TAB alone would let control and bidi characters
			// through; the centralised path is strictly more defensive.
			// Use IssueLogWriter.Utf8WithBom (BOM-emitting) for consistency
			// with the rest of the log corpus: the BOM-prefixed logs written via
			// IssueLogWriter's non-streaming helpers (10, 16, 17, etc.).
			// NO "sep=" directive: German Excel honours the UTF-8 BOM (correct umlauts)
			// only when there is no "sep=" line — a sep= directive makes Excel fall back
			// to the locale code page and mojibake the umlauts. German Excel already uses
			// ';' as its list separator, so BOM + ';' data opens column-split without it.
			using var writer = new StreamWriter(outputFilePath, append: false,
				IssueLogWriter.Utf8WithBom) { NewLine = FileFormat.NewLine };
			await writer.WriteLineAsync(IssueLogWriter.ComposeCsvLine(';',
				"url", "robots", "title", "title_length",
				"description", "description_length", "keywords", "source", "h1_count", "search", "canonical", "h1"));

			// Pages are read and parsed in parallel; the rows are written in file order.
			var rows = new string[htmlFiles.Length];
			var canonicalHrefs = new string[htmlFiles.Length][];
			var search = new string[htmlFiles.Length];
			RunUi.StepTotal(htmlFiles.Length);
			Parallel.For(0, htmlFiles.Length, i => (rows[i], canonicalHrefs[i], search[i]) = Row(htmlFiles[i]));
			foreach (var row in rows)
			{
				await writer.WriteLineAsync(row);
			}

			var hrefsByFile = new Dictionary<string, string[]>(htmlFiles.Length, StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < htmlFiles.Length; i++)
			{
				hrefsByFile[htmlFiles[i]] = canonicalHrefs[i];
			}

			// Out of search: noindex in the page first, else by header; else a canonical naming another page.
			return (hrefsByFile, new SearchFacts(htmlFiles.Length,
				search.Count(v => v == Search.NoindexInPage),
				search.Count(v => v == Search.NoindexByHeader),
				search.Count(v => v == Search.CanonicalElsewhere)));
		}

		/// <summary>The page's first canonical names another address (a trailing '/' and the host's case do not count).</summary>
		internal static bool CanonicalElsewhere(string pageUrl, string[] canonicalHrefs)
		{
			if (canonicalHrefs.Length == 0 || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
				|| !Uri.TryCreate(page, HtmlEntity.DeEntitize(canonicalHrefs[0]).Trim(), out var canonical))
			{
				return false;
			}

			static string Key(Uri u) => u.GetLeftPart(UriPartial.Query).TrimEnd('/');   // the host comes lower-case
			return !string.Equals(Key(page), Key(canonical), StringComparison.Ordinal);
		}

		/// <summary>One page's row of log 08, and the raw href values of its canonical links.</summary>
		private static (string Row, string[] CanonicalHrefs, string Search) Row(string htmlFile)
		{
			{
				RunUi.StepAdvance();
				// Use the same byte-level encoding detection as the rest of the
				// pipeline (DetectEncoding.FromBytes) instead of letting
				// HtmlAgilityPack's doc.Load(path) apply its own detection, which can
				// produce different results for non-UTF-8 pages.
				var rawBytes = CrawlerSuite.Core.Host.WorkFile.ReadAllBytes(htmlFile);
				var encoding = DetectEncoding.FromBytes(rawBytes);
				var htmlContent = encoding.GetString(rawBytes);

				var doc = CrawlerSuite.Core.Html.HtmlDocuments.Create();
				doc.LoadHtml(htmlContent);

				var titleNode = doc.DocumentNode.SelectSingleNode("//title");
				string titleValue = titleNode?.InnerText ?? string.Empty;
				int titleLength = titleValue.Length;

				var descriptionNode = doc.DocumentNode.SelectSingleNode(CrawlerSuite.Core.Html.XPaths.MetaByName("description"));
				string descriptionValue = descriptionNode?.GetAttributeValue("content", string.Empty) ?? string.Empty;
				int descriptionLength = descriptionValue.Length;

				var robotsNode = doc.DocumentNode.SelectSingleNode(CrawlerSuite.Core.Html.XPaths.MetaByName("robots"));
				string robotsValue = robotsNode?.GetAttributeValue("content", string.Empty) ?? string.Empty;

				var keywordsNode = doc.DocumentNode.SelectSingleNode(CrawlerSuite.Core.Html.XPaths.MetaByName("keywords"));
				string keywordsValue = keywordsNode?.GetAttributeValue("content", string.Empty) ?? string.Empty;

				// H1 count (document-wide): 0 (missing) apart from several. SelectNodes returns null
				// when there are no matches, hence the ?? 0.
				var h1Nodes = doc.DocumentNode.SelectNodes("//h1");
				int h1Count = h1Nodes?.Count ?? 0;

				// The H1 texts as a reader sees them, so triage can tell one heading written twice
				// (a layout per screen width) from several different headings. The separator's
				// character is taken out of the texts, so the column splits back exactly.
				string h1Texts = h1Nodes is null
					? string.Empty
					: string.Join(H1Separator, h1Nodes.Select(n => Seo.SearchSnippet.Clean(n.InnerText.Replace(H1Separator.Trim(), " "))));

				string filename = Path.GetFileName(htmlFile);
				string url = CrawlIndex.LookUpUrlForFile(filename);
				string source = CrawlIndex.LookUpSourceForFile(filename);

				// Canonical links, read from the same parse for the canonical-link step: the raw
				// attribute values, exactly what that step would read from its own parse.
				var canonicalNodes = doc.DocumentNode.SelectNodes(CrawlerSuite.Core.Html.XPaths.CanonicalLinks);
				string[] canonicalHrefs = canonicalNodes is null
					? []
					: canonicalNodes.Select(n => n.GetAttributeValue("href", string.Empty)).ToArray();

				var search = Search.Of(robotsValue, SearchRobots.HeaderValuesOf(htmlFile), url, canonicalHrefs);
				return (IssueLogWriter.ComposeCsvLine(';',
					url, robotsValue, titleValue, titleLength.ToString(),
					descriptionValue, descriptionLength.ToString(), keywordsValue, source, h1Count.ToString(), search,
						CanonicalTarget(url, canonicalHrefs), h1Texts),
					canonicalHrefs, search);
			}
		}
	}
}
