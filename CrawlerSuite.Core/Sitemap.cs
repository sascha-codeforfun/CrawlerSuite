using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Html;

namespace CrawlerSuite.Core
{
	// ── Sitemap ──────────────────────────────────────────────────────────────
	//
	// Generates an XML sitemap from the downloaded HTML corpus. Each page is
	// included unless one of the following applies:
	//   1. Its <meta name="robots"> declares "noindex".
	//   2. Its crawler-log source is "list" (post-crawl pass — not a normal
	//      discovery, never appears in sitemaps regardless of robots).
	//   3. Its URL matches a configured exclusion AND no forced-inclusion override.
	//
	// Performance notes:
	//   - Only the head is read (≤16KB per file, stops at </head>), and a compiled
	//     regex extracts the robots meta content: reading every byte and building a
	//     full HtmlAgilityPack DOM just to read one tag would dominate the run time
	//     on a large site. Files are processed in parallel.
	// ─────────────────────────────────────────────────────────────────────────

	public partial class Sitemap
	{
		/// <summary>Why pages are not in the sitemap — counted while it is built, for the run overview.</summary>
		public sealed class Tally
		{
			public int Pages;
			public int NoindexInPage;
			public int NoindexByHeader;
			public int ContentListOnly;
			public int Excluded;
			public int Entries;
		}

		// Maximum bytes per file to read in search of <meta name="robots">.
		// A typical CMS head is well under 4KB; 16KB is ~8× safety margin for
		// pages with rich JSON-LD, social meta tags, or instrumentation scripts.
		private const int HeadReadCapBytes = 16 * 1024;

		// Matches <meta name="robots" content="..."> with attributes in either order,
		// single or double quotes, and arbitrary whitespace. Captures the content value.
		// Case-insensitive — HTML attribute names are case-insensitive.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex RobotsMetaNameFirstRegex = new("<meta\\b[^>]*?\\bname\\s*=\\s*[\"']robots[\"'][^>]*?\\bcontent\\s*=\\s*[\"'](?<content>[^\"']*)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static Regex RobotsMetaNameFirst() => RobotsMetaNameFirstRegex;

		// Same but with attribute order reversed: content="..." appearing before name="robots".
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex RobotsMetaContentFirstRegex = new("<meta\\b[^>]*?\\bcontent\\s*=\\s*[\"'](?<content>[^\"']*)[\"'][^>]*?\\bname\\s*=\\s*[\"']robots[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static Regex RobotsMetaContentFirst() => RobotsMetaContentFirstRegex;

		public static void Generate(
			string baseDirectory,
			string sitemapFilePath,
			List<string> exclusions,
			List<string> forcedInclusions,
			string filePattern,
			int degreeOfParallelism = 0,
			Tally? tally = null)
		{
			string sitemap = GenerateSitemap(baseDirectory, exclusions, forcedInclusions, filePattern, degreeOfParallelism, tally);
			FileIo.WriteAllTextWithRetry(sitemapFilePath, sitemap, Path.GetFileName(sitemapFilePath));
		}

		public static string GenerateSitemap(
			string directoryPath,
			List<string> exclusions,
			List<string> forcedInclusions,
			string filePattern,
			int degreeOfParallelism = 0,
			Tally? tally = null)
		{
			var htmlFiles = CrawlerSuite.Core.Host.FolderFiles.Sorted(directoryPath, filePattern);
			int noindexInPage = 0, noindexByHeader = 0, listOnly = 0, excluded = 0;
			var validUrls = new ConcurrentBag<string>();
			int truncatedHeads = 0;

			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = degreeOfParallelism <= 0
					? Environment.ProcessorCount
					: degreeOfParallelism
			};

			RunUi.StepTotal(htmlFiles.Length);
			Parallel.ForEach(htmlFiles, parallelOptions, file =>
			{
				RunUi.StepAdvance();
				// 1. Read only the head (or up to the cap if </head> is absent/late).
				var headBytes = MarkupFile.ReadHeadBytes(file, HeadReadCapBytes, out bool reachedCap);
				if (reachedCap)
				{
					Interlocked.Increment(ref truncatedHeads);
				}

				// 2. Detect encoding from the head bytes (BOM + meta-charset).
				var encoding = DetectEncoding.FromBytes(headBytes);
				var headText = encoding.GetString(headBytes);

				// 3. Check <meta name="robots"> content for "noindex".
				// Tolerate both attribute orders; first match wins.
				var match = RobotsMetaNameFirst().Match(headText);
				if (!match.Success)
				{
					match = RobotsMetaContentFirst().Match(headText);
				}

				// Out of search (noindex or none, in the page or the X-Robots-Tag header) stays out.
				var content = match.Success ? match.Groups["content"].Value : null;
				if (SearchRobots.IsOutOfSearch(content))
				{
					Interlocked.Increment(ref noindexInPage);
					return;
				}

				if (SearchRobots.IsOutOfSearch(null, SearchRobots.HeaderValuesOf(file)))
				{
					Interlocked.Increment(ref noindexByHeader);
					return;
				}

				var fileName = Path.GetFileName(file);

				// 4. Safeguard — pages from the CMS list pass (not reachable via normal
				// crawl) must never enter the sitemap regardless of their robots directive.
				var source = CrawlIndex.LookUpSourceForFile(fileName);
				if (source.Equals("list", StringComparison.OrdinalIgnoreCase))
				{
					Interlocked.Increment(ref listOnly);
					return;
				}

				// 5. Apply URL exclusion + forced-inclusion rules.
				var relativeUrl = CrawlIndex.LookUpUrlForFile(fileName);
				if (relativeUrl == "error")
				{
					// Index miss sentinel — not a URL; never emit it as <loc>error</loc>.
					return;
				}

				if (exclusions.Any(page => relativeUrl.Contains(page)))
				{
					if (!forcedInclusions.Any(page => relativeUrl.Contains(page)))
					{
						Interlocked.Increment(ref excluded);
						return;
					}
				}

				// XML-escape: a URL with '&' (query strings) would otherwise make the sitemap invalid.
				validUrls.Add($"<url><loc>{System.Security.SecurityElement.Escape(relativeUrl)}</loc></url>");
			});

			if (truncatedHeads > 0)
			{
				Logger.LogWarning(
					$"Sitemap: {truncatedHeads} file(s) had a </head> not found within {HeadReadCapBytes / 1024}KB. " +
					$"Their robots meta may have been missed if it appears beyond that point.");
			}

			// Sort for stable output — sitemap.xml diffs cleanly across runs regardless
			// of file-system enumeration order or parallel completion order.
			var sortedUrls = validUrls.OrderBy(s => s, StringComparer.Ordinal).ToList();
			if (tally is not null)
			{
				tally.Pages = htmlFiles.Length;
				tally.NoindexInPage = noindexInPage;
				tally.NoindexByHeader = noindexByHeader;
				tally.ContentListOnly = listOnly;
				tally.Excluded = excluded;
				tally.Entries = sortedUrls.Count;
			}

			return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+ "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\" "
				+ "xmlns:image=\"http://www.google.com/schemas/sitemap-image/1.1\" "
				+ "xmlns:video=\"http://www.google.com/schemas/sitemap-video/1.1\">"
				+ string.Join(FileFormat.NewLine, sortedUrls)
				+ "</urlset>";
		}

		// Robots meta content is a comma-separated directive list (e.g. "noindex,follow").
		// Match whole-token only so "noindex" is not falsely detected as a substring
		// of an unrelated directive (no current example, but defensive).

	}
}

