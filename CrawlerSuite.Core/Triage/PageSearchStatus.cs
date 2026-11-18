namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// What triage shows of a page's search status: a pill reading "In search", "Not in search" or
	/// "Search unknown", with the reason in its tooltip, and the two filter chips. "In search" means
	/// the page may appear in search as far as the snapshot can tell; robots.txt is not read, and
	/// whether a search engine has indexed it is not known.
	/// <para>
	/// Worked out per triage entry, first source that knows it: log 08's search column (HTML pages);
	/// log 03 for an address that redirects (not in search — a search engine indexes where it leads);
	/// the saved response header for a PDF (X-Robots-Tag). A stylesheet, a script or a shared entry is
	/// no page and gets no status; any other page none of these know is "unknown".
	/// </para>
	/// </summary>
	public static class PageSearchStatus
	{
		/// <summary>The chip key of pages in search.</summary>
		public const string InKey = "in";

		/// <summary>The chip key of pages out of search.</summary>
		public const string OutKey = "out";

		/// <summary>The key of a page whose status is unknown: no chip lets it through on its own.</summary>
		public const string UnknownKey = "unknown";

		/// <summary>The key of an entry that is no page: no pill.</summary>
		public const string NoneKey = "";

		/// <summary>A PDF in search: its saved header has no noindex.</summary>
		public const string PdfIn = "in search (PDF)";

		/// <summary>A page no source knows.</summary>
		public const string Unknown = "unknown";

		/// <summary>The start of the value for an address that redirects; the target follows.</summary>
		public const string RedirectPrefix = "redirects to ";

		/// <summary>Whether a status value says the page may appear in search.</summary>
		public static bool IsIn(string? search) => search is SEO.Search.In or PdfIn;

		/// <summary>The key for a status value: in, out, unknown, or none (no pill).</summary>
		public static string KeyOf(string? search) =>
			search is null ? NoneKey : search == Unknown ? UnknownKey : IsIn(search) ? InKey : OutKey;

		/// <summary>The pill's text; empty when the entry has no status (no pill).</summary>
		public static string LabelOf(string? search) => KeyOf(search) switch
		{
			InKey => "In search",
			OutKey => "Not in search",
			UnknownKey => "Search unknown",
			_ => string.Empty,
		};

		/// <summary>The pill's tooltip: the status and why; empty when the entry has no status.</summary>
		public static string ReasonOf(string? search) => search switch
		{
			null => string.Empty,
			SEO.Search.In => "In search: no noindex, and the canonical names the page itself. As far as the snapshot can tell — robots.txt is not read.",
			PdfIn => "In search: a PDF with no noindex in its X-Robots-Tag response header. As far as the snapshot can tell — robots.txt is not read.",
			SEO.Search.NoindexInPage => "Not in search: noindex in the page's robots meta tag.",
			SEO.Search.NoindexByHeader => "Not in search: noindex in the X-Robots-Tag response header.",
			SEO.Search.CanonicalElsewhere => "Not in search: the page's canonical names another page.",
			Unknown => "Search unknown: the snapshot holds nothing to tell — no page in log 08, no redirect in log 03, no saved PDF.",
			_ when search.StartsWith(RedirectPrefix, StringComparison.Ordinal)
				=> "Not in search: the address redirects to " + search[RedirectPrefix.Length..] + " — a search engine indexes where it leads.",
			_ => "Not in search: " + search,
		};

		/// <summary>The status of each of <paramref name="pages"/> that is a page; entries that are no page are left out.</summary>
		public static IReadOnlyDictionary<string, string> ForPages(string snapshotDirectory, IEnumerable<string> pages)
		{
			var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var wanted = pages.Where(p => p.Length > 0 && !SharedEntries.IsShared(p) && Urls.FileKind.Of(p) is null)
				.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (wanted.Count == 0)
			{
				return map;
			}

			var seo = SeoSource.SearchByPage(snapshotDirectory);
			var redirects = RedirectAnalyzer.Targets(Path.Combine(snapshotDirectory, LogFileNames.RedirectAnalysis));
			Dictionary<string, string>? fileByUrl = null;

			foreach (var page in wanted)
			{
				if (Lookup(seo, page) is { } search)
				{
					map[page] = search;
				}
				else if (Lookup(redirects, page) is { } redirect && redirect.Target is { Length: > 0 })
				{
					map[page] = RedirectPrefix + redirect.Target;
				}
				else if (IsPdf(page) && SavedFile(snapshotDirectory, page, ref fileByUrl) is { } file)
				{
					map[page] = SearchRobots.IsOutOfSearch(null, SearchRobots.HeaderValuesOf(file)) ? SEO.Search.NoindexByHeader : PdfIn;
				}
				else
				{
					map[page] = Unknown;
				}
			}

			return map;
		}

		private static bool IsPdf(string url)
		{
			var path = url.Split('?', '#')[0];
			return path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
		}

		// The saved file of an address (crawler index → download folder); null when there is none.
		private static string? SavedFile(string snapshotDirectory, string url, ref Dictionary<string, string>? fileByUrl)
		{
			if (fileByUrl is null)
			{
				fileByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				var index = Path.Combine(snapshotDirectory, LogFileNames.CrawlerIndex);
				if (File.Exists(index))
				{
					foreach (var c in Findings.FindingFiles.Delimited(index, "|", 3, header: false))
					{
						if (c[0].Length > 0 && c[1].Length > 0)
						{
							fileByUrl.TryAdd(c[1], c[0]);
						}
					}
				}
			}

			if (Lookup(fileByUrl, url) is not { } name || name.IndexOfAny(['/', '\\']) >= 0)
			{
				return null;
			}

			var path = Path.Combine(snapshotDirectory, "download", name);
			return File.Exists(path) ? path : null;
		}

		// By address, with or without a trailing slash, as the canonical check matches.
		private static T? Lookup<T>(IReadOnlyDictionary<string, T> byUrl, string url)
		{
			var bare = url.TrimEnd('/');
			return byUrl.TryGetValue(url, out var value) || byUrl.TryGetValue(bare, out value) || byUrl.TryGetValue(bare + "/", out value)
				? value
				: default;
		}
	}
}
