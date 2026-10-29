using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Host;
using CrawlerSuite.Core.Html;
using CrawlerSuite.Core.Quality.Css;
using CrawlerSuite.Core.Urls;
using HtmlAgilityPack;

namespace CrawlerSuite.Core.Links
{
	/// <summary>One place a broken address is referenced from, and what that reference is.</summary>
	/// <param name="Broken">The address that answered 404.</param>
	/// <param name="Kind">How it is referenced (link, image, stylesheet url(), canonical …).</param>
	/// <param name="Referrer">The page, stylesheet or redirecting address that references it; empty when none was found.</param>
	/// <param name="AsWritten">The reference exactly as it stands in the source.</param>
	/// <param name="Grade">For stylesheet references: when a browser requests it; "canonical" or "relative link" where that explains it.</param>
	/// <param name="Evidence">What the grade rests on, in words.</param>
	public sealed record BrokenReference(string Broken, string Kind, string Referrer, string AsWritten, string Grade, string Evidence, string Reach);

	/// <summary>
	/// Where each 404 of a crawl comes from — found after the crawl, without a request: the crawler's
	/// own discovery is run again over the saved pages and stylesheets, the redirects of log 01 and
	/// the content list (log 05), and every reference is resolved the way the crawler resolved it
	/// (pages without their query, as fetched; assets as written). A reference in a stylesheet is
	/// graded by when a browser requests it: when a page loads, when a script sets the classes its
	/// rule needs, never (dead reference), or undecided. Scripts are not run — a class built at run
	/// time from parts, or markup loaded later, is beyond what the snapshot shows.
	/// </summary>
	public static class BrokenLinkOrigins
	{
		public const string OnLoad = "fires on load";
		/// <summary>The rule needs a class only a script names: it can apply once a script adds it — the snapshot cannot show that it does.</summary>
		public const string WhenScriptRuns = "can fire if a script adds the class";
		/// <summary>An element of a page matches, but only in a state the visitor brings about (:hover, :focus, :checked …).</summary>
		public const string InState = "fires in a state";
		/// <summary>An element of a page matches under a media or container query that describes the device (screen size, orientation, colour scheme): requested on load where it holds.</summary>
		public const string OnDevices = "fires on matching devices";
		/// <summary>An element of a page matches, but only under a print media query.</summary>
		public const string WhenPrinted = "fires when printed";
		/// <summary>No crawled page and no script names a class the rule needs: unused on the crawled pages.</summary>
		public const string Dead = "dead reference";
		public const string Undecided = "undecided";
		public const string CanonicalOnly = "canonical";
		public const string RelativeLink = "relative link";

		/// <summary>
		/// True when a grade says when a browser requests the address; false for one that says what kind
		/// of reference it is instead (canonical, relative link) — a ticket names that a kind, not a time.
		/// </summary>
		public static bool IsTime(string grade) => grade is not (CanonicalOnly or RelativeLink);

		/// <summary>The Kind of a broken address no saved source references.</summary>
		public const string NoReference = "no reference found";

		/// <summary>How many pages loading a stylesheet are searched for an element its rule applies to.</summary>
		internal const int PagesToMatch = 60;

		// url(...) inside a declaration value, quoted or not.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex CssUrl = new(@"url\(\s*(['""]?)(?<raw>.*?)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// A class in a selector: ".name".
		private static readonly Regex SelectorClass = new(@"\.(?<name>-?[_a-zA-Z][\w-]*)", RegexOptions.Compiled);

		// A class attribute's value, and an inline script's body: where a needed class name is looked for.
		private static readonly Regex ClassAttribute = new(@"\bclass\s*=\s*([""'])(?<value>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

		private static readonly Regex InlineScript = new(@"<script\b(?![^>]*\bsrc\s*=)[^>]*>(?<body>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

		/// <summary>The Kind of an image a page or stylesheet srcset names (log 38 only).</summary>
		public const string SrcSet = "srcset";

		/// <summary>
		/// The fixed words of log 07 and 38 that reach a ticket — how an address is referenced, when a
		/// browser requests it — each translated for the documents (<see cref="Text.DocumentText"/>).
		/// </summary>
		public static IReadOnlyList<string> DocumentWords { get; } =
		[
			"image", SrcSet, "link", "link element", "data attribute", "inline data", "stylesheet url()", "redirect", "script",
			CanonicalOnly, "content list", RelativeLink, NoReference,
			OnLoad, WhenScriptRuns, InState, OnDevices, WhenPrinted, Dead, Undecided,
		];

		/// <summary>The summary of a run: the broken addresses and what they turned out to be.</summary>
		public sealed record Result(IReadOnlyList<BrokenReference> Rows, int Addresses, int Pages, int Files, int OnlyCanonical)
		{
			/// <summary>The references of the images asked for (log 38), in their order; Broken holds the image.</summary>
			public IReadOnlyList<BrokenReference> ImageRows { get; init; } = [];
		}

		private sealed record Ref(string Kind, string Referrer, string AsWritten, string? Stylesheet, string Grade, string Evidence, string Rel = "");

		/// <summary>The address as the crawl log keys it: ":443" left out.</summary>
		internal static string KeyOf(string url) => url.Replace(":443", string.Empty, StringComparison.Ordinal).Trim();

		private static string StripFragment(string url)
		{
			int hash = url.IndexOf('#');
			return hash >= 0 ? url[..hash] : url;
		}

		private static string? Absolute(Uri baseUri, string reference) =>
			reference.Length > 0 && Uri.TryCreate(baseUri, reference, out var abs) && abs.Scheme is "http" or "https" ? abs.AbsoluteUri : null;

		/// <summary>
		/// Finds the references of <paramref name="broken"/> in the snapshot. <paramref name="siteUrl"/>,
		/// <paramref name="modalQueryParameters"/> and <paramref name="jsonPathPrefixes"/> are the crawl's own.
		/// <paramref name="images"/> are saved images whose users are wanted as well (log 38): searched in
		/// the same pass over pages and stylesheets — as an image, a srcset candidate, a link to the file,
		/// a link element, a data attribute or a stylesheet url() — without changing what is found for
		/// <paramref name="broken"/>.
		/// </summary>
		public static Result Find(
			string snapshotDirectory,
			IReadOnlyList<string> broken,
			string siteUrl,
			string filePattern,
			List<ModalQueryParam> modalQueryParameters,
			IReadOnlyList<string>? jsonPathPrefixes,
			IReadOnlyList<string>? images = null)
		{
			var brokenKeys = new HashSet<string>(broken.Select(KeyOf), StringComparer.OrdinalIgnoreCase);
			var refs = new Dictionary<string, List<Ref>>(StringComparer.OrdinalIgnoreCase);
			var imageKeys = new HashSet<string>((images ?? []).Select(ImageKeyOf), StringComparer.OrdinalIgnoreCase);
			var imageRefs = new Dictionary<string, List<Ref>>(StringComparer.OrdinalIgnoreCase);
			if (brokenKeys.Count == 0 && imageKeys.Count == 0)
			{
				return new Result([], 0, 0, 0, 0);
			}

			bool IsImage(string absolute) => imageKeys.Contains(ImageKeyOf(absolute));

			void AddImage(string address, Ref r)
			{
				var key = ImageKeyOf(address);
				if (!imageKeys.Contains(key))
				{
					return;
				}

				if (!imageRefs.TryGetValue(key, out var list))
				{
					list = [];
					imageRefs[key] = list;
				}

				if (!list.Contains(r))
				{
					list.Add(r);
				}
			}

			void Add(string address, Ref r)
			{
				var key = KeyOf(address);
				if (!brokenKeys.Contains(key))
				{
					return;
				}

				if (!refs.TryGetValue(key, out var list))
				{
					list = [];
					refs[key] = list;
				}

				if (!list.Contains(r))
				{
					list.Add(r);
				}
			}

			// Pages are fetched without their query (and with modal parameters rewritten), as the crawler does.
			string AsPageFetch(string absolute)
			{
				try
				{
					return Crawl.CanonicalizeUrlForFetch(StripFragment(absolute), siteUrl, modalQueryParameters);
				}
				catch (Exception ex) when (ex is UriFormatException or ArgumentException)
				{
					return absolute;
				}
			}

			string? Read(string path)
			{
				try
				{
					return WorkFile.ReadAllText(path, Encoding.UTF8);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					return null;
				}
			}

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

			var saved = new HashSet<string>(urlByFile.Values.Select(KeyOf), StringComparer.OrdinalIgnoreCase);
			var download = Path.Combine(snapshotDirectory, "download");

			// Where the step's time goes, written to the application log once: read, extractor, names, pre-check, parse, grades.
			var time = new Dictionary<string, long>(StringComparer.Ordinal);
			long mark = System.Diagnostics.Stopwatch.GetTimestamp();
			void Lap(string part)
			{
				long now = System.Diagnostics.Stopwatch.GetTimestamp();
				time[part] = time.GetValueOrDefault(part) + (now - mark);
				mark = now;
			}

			int parsed = 0;
			var pagesByStylesheet = new Dictionary<string, List<(string File, string Url)>>(StringComparer.OrdinalIgnoreCase);
			var pageNames = new HashSet<string>(StringComparer.Ordinal);
			var scriptNames = new Dictionary<string, string>(StringComparer.Ordinal);

			if (Directory.Exists(download))
			{
				// ── 1. Stylesheets: their url() references, and the class names the rules using a broken one need ──
				var sheetsByHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
				var sheets = new Dictionary<string, CssSheet>(StringComparer.OrdinalIgnoreCase);
				var scripts = new List<(string Path, string Url)>();
				foreach (var (file, url) in urlByFile)
				{
					var ext = Path.GetExtension(file).ToLowerInvariant();
					var path = Path.Combine(download, file);
					if (ext == ".js" && File.Exists(path))
					{
						scripts.Add((path, url));
						continue;
					}

					if (ext != ".css" || !File.Exists(path) || Read(path) is not { } text)
					{
						continue;
					}

					var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
					if (!sheetsByHash.TryGetValue(hash, out var same))
					{
						same = [];
						sheetsByHash[hash] = same;
					}

					same.Add(url);
					bool any = false;
					foreach (var e in Extractor.FromCss(text, url))
					{
						if (brokenKeys.Contains(KeyOf(e.Url)))
						{
							Add(e.Url, new Ref("stylesheet url()", url, e.Raw, path, string.Empty, string.Empty));
							any = true;
						}
						else if (IsImage(e.Url))
						{
							AddImage(e.Url, new Ref("stylesheet url()", url, e.Raw, path, string.Empty, string.Empty));
							any = true;
						}
					}

					if (any)
					{
						sheets[path] = CssSheet.ParseUrls(text, file);
					}
				}

				Lap("stylesheets");
				var needed = new HashSet<string>(StringComparer.Ordinal);
				foreach (var sheet in sheets.Values)
				{
					foreach (var rule in sheet.Rules)
					{
						if (rule.Declarations.Any(d => d.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)))
						{
							foreach (Match m in SelectorClass.Each(rule.SelectorText))
							{
								needed.Add(m.Groups["name"].Value);
							}
						}
					}
				}

				// A needed name counts only as a whole word: one lookup per word of the text, not one try per name per position.
				var neededNames = needed.Count == 0 ? null : needed;

				// ── 2. Pages: the extended sources from the text; a parse only where a href or src resolves to a broken address ──
				bool IsBroken(string absolute) =>
					brokenKeys.Contains(KeyOf(absolute)) || brokenKeys.Contains(KeyOf(StripFragment(absolute))) || brokenKeys.Contains(KeyOf(AsPageFetch(absolute)));
				// The pages are read in parallel, each into its own slot; the slots are added in page
				// order afterwards, so every list, every "first found" and every row comes out as a
				// page-by-page pass would make it. Part times are work time summed over the threads.
				var pageFiles = Directory.Exists(download) ? FolderFiles.Sorted(download, filePattern) : [];
				var slots = new PageWork?[pageFiles.Length];
				var parts = new long[PagePart.Count];
				long pagesStart = System.Diagnostics.Stopwatch.GetTimestamp();
				Parallel.For(0, pageFiles.Length, Threads, i =>
				{
					long mark = System.Diagnostics.Stopwatch.GetTimestamp();
					void Part(int part)
					{
						long now = System.Diagnostics.Stopwatch.GetTimestamp();
						Interlocked.Add(ref parts[part], now - mark);
						mark = now;
					}

					var path = pageFiles[i];
					var file = Path.GetFileName(path);
					if (!urlByFile.TryGetValue(file, out var pageUrl) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageBase)
						|| Read(path) is not { } content)
					{
						return;
					}

					var work = new PageWork(path, pageUrl);
					slots[i] = work;
					Part(PagePart.Read);
					foreach (var e in Extractor.FromHtml(content, pageUrl, jsonPathPrefixes))
					{
						var target = StripFragment(e.Url);
						if (e.Source == Extractor.ExtractedSource.LinkHref)
						{
							work.Stylesheets.Add(KeyOf(target));
						}

						var extended = new Ref(KindOf(e.Source), pageUrl, e.Raw, null, string.Empty, string.Empty,
							e.Source == Extractor.ExtractedSource.LinkHref ? RelOf(e.SourceDetail) : string.Empty);
						if (brokenKeys.Contains(KeyOf(target)))
						{
							work.Broken.Add((target, extended));
						}

						if (IsImage(target))
						{
							work.Images.Add((target, extended with { Rel = string.Empty }));
						}
					}

					Part(PagePart.Extractor);
					if (neededNames is not null)
					{
						foreach (Match m in ClassAttribute.Each(content))
						{
							work.ClassNames.AddRange(WholeWordsIn(m.Groups["value"].Value, neededNames));
						}

						foreach (Match m in InlineScript.Each(content))
						{
							work.ScriptNames.AddRange(WholeWordsIn(m.Groups["body"].Value, neededNames));
						}
					}

					Part(PagePart.ClassNames);
					// Two pre-checks in one scan: the 404 rows are made only on a page that names a broken
					// address, the image rows only on one that names a flagged image — a page that names
					// only an image (one on every page, say) does not pay for the 404 work.
					var (namesBroken, namesImage) = Names(content, pageBase, IsBroken, imageKeys.Count == 0 ? null : IsImage);
					if (!namesImage && imageKeys.Count > 0)
					{
						namesImage = NamesInSrcSet(content, pageBase, IsImage);
					}

					Part(PagePart.PreCheck);
					if (!namesBroken)
					{
						// A page that names a flagged image but no broken address is not parsed: its
						// image tags are read from the text (comments, scripts and styles left out).
						if (namesImage)
						{
							work.Images.AddRange(ImageRefsFromText(content, pageBase, pageUrl, IsImage));
							Part(PagePart.ImageTags);
						}

						return;
					}

					work.Parsed = true;
					var doc = HtmlDocuments.Parse(content);
					foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
					{
						var attribute = a.Attributes["href"];
						var href = attribute?.DeEntitizeValue ?? string.Empty;
						if (Absolute(pageBase, href) is not { } abs)
						{
							continue;
						}

						var fetch = AsPageFetch(abs);
						if (brokenKeys.Contains(KeyOf(fetch)))
						{
							var (grade, evidence) = Relative(href, pageBase, root => saved.Contains(KeyOf(AsPageFetch(root))));
							work.Broken.Add((fetch, new Ref("link", pageUrl, attribute!.Value, null, grade, evidence)));
						}
					}

					foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
					{
						var src = img.GetAttributeValue("src", img.GetAttributeValue("href", string.Empty));
						if (Absolute(pageBase, src) is { } abs)
						{
							work.Broken.Add((abs, new Ref("image", pageUrl, src, null, string.Empty, string.Empty)));
						}
					}

					foreach (var node in doc.DocumentNode.SelectNodes(XPaths.CanonicalLinks) ?? Enumerable.Empty<HtmlNode>())
					{
						var raw = node.GetAttributeValue("href", string.Empty);
						if (Absolute(pageBase, HtmlEntity.DeEntitize(raw)) is { } abs)
						{
							work.Broken.Add((AsPageFetch(abs), new Ref("canonical", pageUrl, raw, null, CanonicalOnly,
								"The page's canonical points here — fix the canonical (SEO › Canonical issues).")));
						}
					}

					if (namesImage)
					{
						work.Images.AddRange(ImageRefsOf(doc, pageBase, pageUrl, IsImage));
					}

					Part(PagePart.Parse);
				});

				// In page order: what a page-by-page pass would have added, in the same order.
				foreach (var work in slots)
				{
					if (work is null)
					{
						continue;
					}

					foreach (var key in work.Stylesheets)
					{
						if (!pagesByStylesheet.TryGetValue(key, out var pages))
						{
							pages = [];
							pagesByStylesheet[key] = pages;
						}

						pages.Add((work.Path, work.Url));
					}

					foreach (var (address, r) in work.Broken)
					{
						Add(address, r);
					}

					foreach (var (address, r) in work.Images)
					{
						AddImage(address, r);
					}

					pageNames.UnionWith(work.ClassNames);
					foreach (var name in work.ScriptNames)
					{
						scriptNames.TryAdd(name, "an inline script on " + work.Url);
					}

					parsed += work.Parsed ? 1 : 0;
				}

				for (int part = 0; part < PagePart.Count; part++)
				{
					if (parts[part] > 0)
					{
						time["pages: " + PagePart.Labels[part]] = parts[part];
					}
				}

				time[$"pages: wall time on {Math.Max(1, Environment.ProcessorCount - 1)} threads"] = System.Diagnostics.Stopwatch.GetTimestamp() - pagesStart;
				mark = System.Diagnostics.Stopwatch.GetTimestamp();

				// ── 3. Scripts: only the class names a graded rule needs ──
				// Read in parallel, each into its slot; the names are taken in script order, so the
				// script named for a class is the first that names it, as a pass one by one finds it.
				if (neededNames is not null)
				{
					var named = new List<string>?[scripts.Count];
					Parallel.For(0, scripts.Count, Threads, i =>
					{
						if (Read(scripts[i].Path) is { } text)
						{
							named[i] = WholeWordsIn(text, neededNames);
						}
					});

					for (int i = 0; i < scripts.Count; i++)
					{
						foreach (var name in named[i] ?? [])
						{
							scriptNames.TryAdd(name, scripts[i].Url);
						}
					}
				}

				Lap("scripts");

				// ── 4. Grade the stylesheet references ──
				// Each reference is graded on its own (reading the pages that load its stylesheet), in
				// parallel; what the grade reads — the sheets, the pages, the names — is complete here.
				var toGrade = new List<(List<Ref> List, int Index, string Key, CssSheet Sheet)>();
				foreach (var (key, list) in refs.Concat(imageRefs))
				{
					for (int i = 0; i < list.Count; i++)
					{
						if (list[i].Stylesheet is { } stylesheet && sheets.TryGetValue(stylesheet, out var sheet))
						{
							toGrade.Add((list, i, key, sheet));
						}
					}
				}

				var graded = new (string Grade, string Evidence)[toGrade.Count];
				Parallel.For(0, toGrade.Count, Threads, g =>
				{
					var (list, i, key, sheet) = toGrade[g];
					var r = list[i];
					var (grade, evidence) = Grade(sheet, r.Referrer, key, pagesByStylesheet.GetValueOrDefault(KeyOf(r.Referrer)) ?? [], pageNames, scriptNames);
					var twins = sheetsByHash.Values.FirstOrDefault(v => v.Contains(r.Referrer, StringComparer.OrdinalIgnoreCase));
					if (twins is { Count: > 1 })
					{
						evidence += $" The same stylesheet is also at {string.Join(", ", twins.Where(t => !t.Equals(r.Referrer, StringComparison.OrdinalIgnoreCase)))}.";
					}

					graded[g] = (grade, evidence);
				});

				for (int g = 0; g < toGrade.Count; g++)
				{
					var (list, i, _, _) = toGrade[g];
					list[i] = list[i] with { Grade = graded[g].Grade, Evidence = graded[g].Evidence };
				}
			}

			Lap("grades");

			// ── 5. Redirects (log 01) and the content list (log 05) ──
			var crawlLog = Path.Combine(snapshotDirectory, LogFileNames.Crawler);
			if (File.Exists(crawlLog))
			{
				foreach (var line in File.ReadLines(crawlLog, Encoding.UTF8))
				{
					var parts = line.Split('|');
					if (parts.Length < 4 || Display.HttpCodes.Parse(parts[2]) is not (>= 300 and < 400)
						|| !Uri.TryCreate(parts[1].Trim(), UriKind.Absolute, out var from))
					{
						continue;
					}

					var location = parts[3].Trim();
					if (Absolute(from, location) is { } abs)
					{
						Add(AsPageFetch(abs), new Ref("redirect", from.AbsoluteUri, location, null, string.Empty, string.Empty));
					}
				}
			}

			var contentList = Path.Combine(snapshotDirectory, LogFileNames.NotDirectlyCrawlable);
			if (File.Exists(contentList))
			{
				var listed = FindingFiles.Lines(contentList).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
				foreach (var address in brokenKeys)
				{
					if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && listed.Contains(uri.PathAndQuery))
					{
						Add(address, new Ref("content list", "content list (log 05)", uri.PathAndQuery, null, string.Empty, string.Empty));
					}
				}
			}

			Lap("redirects and list");
			Logger.LogInfo($"Broken links: {parsed} page(s) parsed; time — " + string.Join(" · ",
				time.Select(t => $"{t.Key} {System.Diagnostics.Stopwatch.GetElapsedTime(0, t.Value).TotalSeconds:0.00} s")));

			// ── Rows, in the order of log 06 ──
			var rows = new List<BrokenReference>();
			int pagesCount = 0, filesCount = 0, onlyCanonical = 0;
			foreach (var address in broken.Select(KeyOf).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				var list = refs.GetValueOrDefault(address) ?? [];
				if (list.Count > 0 && list.All(r => r.Kind == "canonical"))
				{
					onlyCanonical++;
				}
				else if (IsFile(address))
				{
					filesCount++;
				}
				else
				{
					pagesCount++;
				}

				if (list.Count == 0)
				{
					rows.Add(new BrokenReference(address, NoReference, string.Empty, string.Empty, Undecided,
						"Not referenced by a saved page or stylesheet, a redirect or the content list — e.g. a script builds it, or the page that links it was not saved.",
						BrokenLinkReach.Unknown));
					continue;
				}

				foreach (var r in list)
				{
					rows.Add(new BrokenReference(address, r.Kind, r.Referrer, r.AsWritten, r.Grade, r.Evidence, BrokenLinkReach.Of(r.Kind, r.Grade, r.Rel)));
				}
			}

			// ── The images asked for, in their order ──
			var imageRows = new List<BrokenReference>();
			foreach (var image in (images ?? []).Select(KeyOf).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				var list = imageRefs.GetValueOrDefault(ImageKeyOf(image)) ?? [];
				if (list.Count == 0)
				{
					imageRows.Add(new BrokenReference(image, NoReference, string.Empty, string.Empty, string.Empty,
						"Not referenced by a saved page or stylesheet — e.g. a script builds the address, or the page that uses it was not saved.",
						string.Empty));
					continue;
				}

				foreach (var r in list)
				{
					imageRows.Add(new BrokenReference(image, r.Kind, r.Referrer, r.AsWritten, r.Grade, r.Evidence, string.Empty));
				}
			}

			return new Result(rows, pagesCount + filesCount, pagesCount, filesCount, onlyCanonical) { ImageRows = imageRows };
		}

		/// <summary>
		/// The references of the images asked for in a parsed page, in the order the page is read:
		/// links to the file (offered, not shown), images, srcset candidates.
		/// </summary>
		private static List<(string Address, Ref Ref)> ImageRefsOf(HtmlDocument doc, Uri pageBase, string pageUrl, Func<string, bool> isImage)
		{
			var found = new List<(string, Ref)>();
			foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
			{
				var attribute = a.Attributes["href"];
				if (attribute is not null && Absolute(pageBase, attribute.DeEntitizeValue ?? string.Empty) is { } abs && isImage(abs))
				{
					found.Add((abs, new Ref("link", pageUrl, attribute.Value, null, string.Empty, string.Empty)));
				}
			}

			foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
			{
				var src = img.GetAttributeValue("src", img.GetAttributeValue("href", string.Empty));
				if (Absolute(pageBase, src) is { } abs && isImage(abs))
				{
					found.Add((abs, new Ref("image", pageUrl, src, null, string.Empty, string.Empty)));
				}
			}

			// srcset of an image or a picture's source: for the images asked for only, so log 07 stays as it is.
			foreach (var node in doc.DocumentNode.SelectNodes("//img[@srcset] | //source[@srcset]") ?? Enumerable.Empty<HtmlNode>())
			{
				foreach (var candidate in SrcSetCandidates(node.GetAttributeValue("srcset", string.Empty)))
				{
					if (Absolute(pageBase, candidate) is { } abs && isImage(abs))
					{
						found.Add((abs, new Ref(SrcSet, pageUrl, candidate, null, string.Empty, string.Empty)));
					}
				}
			}

			return found;
		}

		// Read from the text in place of a parse: what a parse does not take as tags.
		// Plain Regexes, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex NotMarkup = new(@"<!--.*?-->|<(script|style)\b[^>]*>.*?</\1\s*>",
			RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

		// A link, image or picture source start tag, its attributes (quoted values may hold '>').
		private static readonly Regex ImageTag = new(@"<(?<tag>a|img|source)(?=[\s/>])(?<attrs>(?:""[^""]*""|'[^']*'|[^'"">])*)>",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

		// Every attribute of such a tag in turn — name, and value quoted or not — so a value never reads as an attribute.
		private static readonly Regex TagAttribute = new(@"(?<name>[^\s""'>/=]+)(?:\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+)))?",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

		/// <summary>
		/// The references of the images asked for, read from a page's text without a parse, in the
		/// order <see cref="ImageRefsOf"/> gives them: links to the file, images, srcset candidates,
		/// each in document order. HTML comments and the bodies of script and style elements are
		/// left out, as a parse does not take them as tags; an attribute counts the first time it is
		/// written in a tag, as in a parse.
		/// </summary>
		private static List<(string Address, Ref Ref)> ImageRefsFromText(string content, Uri pageBase, string pageUrl, Func<string, bool> isImage)
		{
			var links = new List<(string, Ref)>();
			var images = new List<(string, Ref)>();
			var sources = new List<(string, Ref)>();
			var markup = NotMarkup.Replace(content, " ");
			foreach (Match tag in ImageTag.Each(markup))
			{
				var name = tag.Groups["tag"].Value.ToLowerInvariant();
				string? href = null, src = null, srcset = null;
				foreach (Match attribute in TagAttribute.Each(tag.Groups["attrs"].Value))
				{
					var value = attribute.Groups["v"].Value; // empty for an attribute without a value, as in a parse
					switch (attribute.Groups["name"].Value.ToLowerInvariant())
					{
						case "href": href ??= value; break;
						case "src": src ??= value; break;
						case "srcset": srcset ??= value; break;
					}
				}

				if (name == "a" && href is not null && Absolute(pageBase, HtmlEntity.DeEntitize(href) ?? href) is { } link && isImage(link))
				{
					links.Add((link, new Ref("link", pageUrl, href, null, string.Empty, string.Empty)));
				}

				if (name == "img" && src is not null && Absolute(pageBase, src) is { } image && isImage(image))
				{
					images.Add((image, new Ref("image", pageUrl, src, null, string.Empty, string.Empty)));
				}

				if (name is "img" or "source" && srcset is not null)
				{
					foreach (var candidate in SrcSetCandidates(srcset))
					{
						if (Absolute(pageBase, candidate) is { } abs && isImage(abs))
						{
							sources.Add((abs, new Ref(SrcSet, pageUrl, candidate, null, string.Empty, string.Empty)));
						}
					}
				}
			}

			links.AddRange(images);
			links.AddRange(sources);
			return links;
		}

		/// <summary>For tests: the image uses of a page read from its text and from a parse, as (address, kind, as written).</summary>
		internal static (List<(string, string, string)> FromText, List<(string, string, string)> FromParse) ImageUsesBothWays(
			string content, Uri pageBase, Func<string, bool> isImage)
		{
			static List<(string, string, string)> Of(List<(string Address, Ref Ref)> refs) => refs.Select(r => (r.Address, r.Ref.Kind, r.Ref.AsWritten)).ToList();
			var page = pageBase.AbsoluteUri;
			return (Of(ImageRefsFromText(content, pageBase, page, isImage)), Of(ImageRefsOf(HtmlDocuments.Parse(content), pageBase, page, isImage)));
		}

		/// <summary>The parallel parts of the step: every core but one.</summary>
		private static readonly ParallelOptions Threads = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

		/// <summary>What one page contributes, gathered off the page loop and added in page order.</summary>
		private sealed class PageWork(string path, string url)
		{
			public string Path { get; } = path;
			public string Url { get; } = url;
			public List<string> Stylesheets { get; } = [];
			public List<(string Address, Ref Ref)> Broken { get; } = [];
			public List<(string Address, Ref Ref)> Images { get; } = [];
			public List<string> ClassNames { get; } = [];
			public List<string> ScriptNames { get; } = [];
			public bool Parsed { get; set; }
		}

		/// <summary>The parts of a page's work, as the step's time line names them.</summary>
		private static class PagePart
		{
			public const int Read = 0, Extractor = 1, ClassNames = 2, PreCheck = 3, ImageTags = 4, Parse = 5, Count = 6;

			public static readonly string[] Labels = ["read", "extractor", "class and script names", "pre-check", "image tags from the text", "parse and rows"];
		}

		/// <summary>An image as matched to its references: the address without query or fragment — a link may add a size or version query to the saved file.</summary>
		internal static string ImageKeyOf(string address)
		{
			var key = KeyOf(address);
			int cut = key.IndexOfAny(['?', '#']);
			return cut < 0 ? key : key[..cut];
		}

		// A srcset value as written, quoted or not — read without a parse.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex SrcSetAttribute = new(@"\bsrcset\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

		/// <summary>The addresses of a srcset ("a.jpg 1x, b.jpg 480w" → a.jpg, b.jpg), entity-decoded.</summary>
		internal static IEnumerable<string> SrcSetCandidates(string srcset)
		{
			foreach (var part in (HtmlEntity.DeEntitize(srcset) ?? srcset).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
			{
				int space = part.IndexOfAny([' ', '\t', '\n', '\r']);
				var candidate = space < 0 ? part : part[..space];
				if (candidate.Length > 0)
				{
					yield return candidate;
				}
			}
		}

		/// <summary>Whether a srcset candidate of the page, resolved against it, is an image asked for.</summary>
		internal static bool NamesInSrcSet(string content, Uri pageBase, Func<string, bool> isWanted)
		{
			foreach (Match m in SrcSetAttribute.Each(content))
			{
				foreach (var candidate in SrcSetCandidates(m.Groups["v"].Value))
				{
					if (Absolute(pageBase, candidate) is { } abs && isWanted(abs))
					{
						return true;
					}
				}
			}

			return false;
		}

		// Every href and src value as written, quoted or not — read without a parse.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex HrefOrSrc = new(@"\b(?:href|src)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+))",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

		/// <summary>
		/// Whether a href or src value of the page, as written or entity-decoded and resolved against
		/// the page, is a broken address (<paramref name="isBroken"/> takes the resolved address).
		/// Only such a page is parsed: the parse then makes the exact rows, as the crawler reads links.
		/// </summary>
		internal static bool NamesBroken(string content, Uri pageBase, Func<string, bool> isBroken) =>
			Names(content, pageBase, isBroken, null).Broken;

		/// <summary>
		/// <see cref="NamesBroken"/> and, in the same scan, whether a href or src value is an image
		/// asked for (<paramref name="isImage"/>; null: none asked). Stops once both are known.
		/// </summary>
		internal static (bool Broken, bool Image) Names(string content, Uri pageBase, Func<string, bool> isBroken, Func<string, bool>? isImage)
		{
			bool broken = false;
			bool image = false;
			bool Check(string absolute)
			{
				broken = broken || isBroken(absolute);
				image = image || (isImage is not null && isImage(absolute));
				return broken && (image || isImage is null);
			}

			foreach (Match m in HrefOrSrc.Each(content))
			{
				var raw = m.Groups["v"].Value;
				var decodedValue = HtmlEntity.DeEntitize(raw) ?? raw;
				if (Absolute(pageBase, decodedValue) is { } decoded && Check(decoded))
				{
					break;
				}

				// As written only when decoding changed the value; otherwise it is the same address again.
				if (!string.Equals(decodedValue, raw, StringComparison.Ordinal) && Absolute(pageBase, raw) is { } asWritten && Check(asWritten))
				{
					break;
				}
			}

			return (broken, image);
		}

		// A word: a run of the characters a class name is made of. A name is found only as a whole word.
		private static readonly Regex WordRun = new(@"[\w-]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

		/// <summary>
		/// The words of <paramref name="text"/> that are in <paramref name="names"/> (an ordinal set), in text order.
		/// Same result as searching each name with no word character or '-' on either side.
		/// </summary>
		internal static List<string> WholeWordsIn(string text, HashSet<string> names)
		{
			var found = new List<string>();
			var lookup = names.GetAlternateLookup<ReadOnlySpan<char>>();
			var span = text.AsSpan();
			foreach (var m in WordRun.EnumerateMatches(span))
			{
				if (lookup.TryGetValue(span.Slice(m.Index, m.Length), out var name))
				{
					found.Add(name);
				}
			}

			return found;
		}

		/// <summary>
		/// Whether a rule's condition holds only when printing: some @media around it lists nothing
		/// but print ("@media print", "@media only print, print and (orientation: portrait)"). A list
		/// that also names a screen or no media type ("print, screen", "(min-width: 48em)") describes
		/// devices that show the page on load. A container query always describes the layout.
		/// </summary>
		internal static bool OnlyWhenPrinted(string condition)
		{
			foreach (var segment in Regex.Split(condition, @"(?=@(?:media|container)\b)", RegexOptions.IgnoreCase))
			{
				var part = segment.Trim();
				if (part.EndsWith(" and", StringComparison.OrdinalIgnoreCase))
				{
					part = part[..^4].TrimEnd();
				}

				if (!part.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var queries = part["@media".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
				if (queries.Length > 0 && queries.All(q => MediaTypeOf(q) == "print"))
				{
					return true;
				}
			}

			return false;
		}

		// The media type a query starts with ("only print" → "print", "not print" → "not"); empty when it starts with a feature.
		private static string MediaTypeOf(string query)
		{
			var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			int i = words.Length > 0 && words[0].Equals("only", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
			return i < words.Length && !words[i].StartsWith('(') ? words[i].ToLowerInvariant() : string.Empty;
		}

		/// <summary>An address of a file (an image, a script, a document) rather than of a page.</summary>
		internal static bool IsFile(string address)
		{
			var path = Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.AbsolutePath : address;
			var ext = Path.GetExtension(path).ToLowerInvariant();
			return ext.Length > 1 && ext is not (".html" or ".htm" or ".php" or ".jsp" or ".asp" or ".aspx");
		}

		/// <summary>
		/// The run overview's breakdown of the broken addresses by reach: each address once, under its
		/// most serious reach. The chip stays the findings chip (red, the Results count), so an address
		/// referenced only by a canonical — the canonical's finding — is named apart, not counted.
		/// </summary>
		public static Host.StepFacts ReachFacts(IReadOnlyList<BrokenReference> rows)
		{
			var groups = rows.GroupBy(r => r.Broken, StringComparer.OrdinalIgnoreCase).ToList();
			bool CanonicalOnlyGroup(IGrouping<string, BrokenReference> g) => g.All(r => r.Grade == CanonicalOnly);
			var byAddress = groups.Where(g => !CanonicalOnlyGroup(g))
				.Select(g => g.MinBy(r => BrokenLinkReach.Rank(r.Reach))!.Reach)
				.ToList();
			int canonicalOnly = groups.Count(CanonicalOnlyGroup);
			var lines = BrokenLinkReach.All
				.Select(reach => (reach, (long)byAddress.Count(r => r == reach)))
				.ToArray();
			var note = "Each address counts once, under its most serious reach. Triage › Broken links holds the decision.";
			if (canonicalOnly > 0)
			{
				note += $" Not counted: {Host.StepFacts.Count(canonicalOnly)} referenced only by a canonical (SEO › Canonical issues).";
			}

			return new Host.StepFacts(
				Host.StepFacts.Count(byAddress.Count),
				$"Broken addresses by reach: {Host.StepFacts.Count(byAddress.Count)}",
				[.. Host.StepFacts.NonZero(lines)],
				note,
				IsFinding: true);
		}

		/// <summary>"&lt;link rel=preload&gt;" → "preload".</summary>
		internal static string RelOf(string sourceDetail)
		{
			const string Prefix = "<link rel=";
			return sourceDetail.StartsWith(Prefix, StringComparison.Ordinal) && sourceDetail.EndsWith('>')
				? sourceDetail[Prefix.Length..^1].Trim().Trim('"', '\'')
				: string.Empty;
		}

		private static string KindOf(Extractor.ExtractedSource source) => source switch
		{
			Extractor.ExtractedSource.DataAttribute => "data attribute",
			Extractor.ExtractedSource.ScriptSrc => "script",
			Extractor.ExtractedSource.LinkHref => "link element",
			Extractor.ExtractedSource.FormAction => "form action",
			Extractor.ExtractedSource.JsonPath => "inline data",
			_ => "stylesheet url()",
		};

		/// <summary>
		/// A link written without a leading '/' on a page inside a folder, whose address read from
		/// the top level is a saved page: the browser puts the page's folder in front, so the link
		/// works only from a page at the top level. Said only when <paramref name="isSaved"/> proves it.
		/// </summary>
		internal static (string Grade, string Evidence) Relative(string href, Uri page, Func<string, bool> isSaved)
		{
			var h = href.Trim();
			if (h.Length == 0 || h[0] is '/' or '#' or '?' or '.' || h.IndexOf(':') is var colon and >= 0 && (h.IndexOf('/') < 0 || colon < h.IndexOf('/')))
			{
				return (string.Empty, string.Empty);
			}

			var folder = page.AbsolutePath[..(page.AbsolutePath.LastIndexOf('/') + 1)];
			if (folder == "/" || !Uri.TryCreate(page, "/" + h, out var fromRoot) || !isSaved(fromRoot.AbsoluteUri))
			{
				return (string.Empty, string.Empty);
			}

			return (RelativeLink, $"Written without a leading '/': the browser puts the page's folder {folder} in front. Written as /{h} it leads to a page of the site; it works as written only on a page at the top level.");
		}

		private static string Shorten(string selector)
		{
			var s = string.Join(' ', selector.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
			return s.Length <= 160 ? s : s[..157] + "…";
		}

		/// <summary>When a browser requests a file a stylesheet references, and why.</summary>
		private static (string Grade, string Evidence) Grade(
			CssSheet sheet,
			string sheetUrl,
			string address,
			IReadOnlyList<(string File, string Url)> pages,
			IReadOnlySet<string> pageClasses,
			IReadOnlyDictionary<string, string> scriptNames)
		{
			if (!Uri.TryCreate(sheetUrl, UriKind.Absolute, out var sheetBase))
			{
				return (Undecided, "The stylesheet's address cannot be read.");
			}

			bool Uses(CssRule rule) => rule.Declarations.Any(d => CssUrl.Each(d.Value).Any(m =>
				Extractor.Resolve(sheetBase, m.Groups["raw"].Value.Trim()) is { } abs
				&& KeyOf(abs).Equals(address, StringComparison.OrdinalIgnoreCase)));
			var rules = sheet.Rules.Where(Uses).ToList();
			if (rules.Count == 0)
			{
				return sheet.UnreadableRules.FirstOrDefault(Uses) is { } unread
					? (Undecided, $"In a rule whose selector the app cannot read: {Shorten(unread.SelectorText)}.")
					: (Undecided, "Not in a style rule (e.g. @font-face or a nested rule): loaded when that font or rule is used.");
			}

			// On load: an element of a page that loads the stylesheet matches a rule (or the element of its ::before / ::after).
			// Short of that, the first match of each other kind is kept, in order of who runs into it:
			// a device condition (on load where it holds), a state (a visitor acts), print.
			string? onDevices = null;
			string? inState = null;
			string? whenPrinted = null;
			foreach (var (file, url) in pages.Take(PagesToMatch))
			{
				HtmlDocument doc;
				try
				{
					doc = HtmlDocuments.Parse(WorkFile.ReadAllText(file, Encoding.UTF8));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					continue;
				}

				foreach (var rule in rules)
				{
					foreach (var selector in rule.Selectors)
					{
						foreach (var node in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
						{
							var match = selector.MatchOrigin(node);
							if (match == CssMatch.No)
							{
								continue;
							}

							if (match == CssMatch.Yes && rule.Condition is null)
							{
								return (OnLoad, $"{rule.SelectorText.Trim()} applies on {url}.");
							}

							if (match == CssMatch.InState)
							{
								// A state wins over a condition: a visitor still has to act.
								var states = string.Join(", ", CssSelector.StatesIn(selector.Text));
								var under = rule.Condition is null ? string.Empty : $", under {rule.Condition}";
								inState ??= $"{selector.Text} applies on {url} only in a state ({(states.Length > 0 ? states : "named in the selector")}{under})";
							}
							else if (onDevices is null || whenPrinted is null)
							{
								if (OnlyWhenPrinted(rule.Condition!))
								{
									whenPrinted ??= $"{selector.Text} applies on {url} only when printed ({rule.Condition})";
								}
								else
								{
									onDevices ??= $"{selector.Text} applies on {url} under {rule.Condition}: requested on load on every device that matches";
								}
							}
						}
					}
				}
			}

			var checkedOn = pages.Count == 0 ? "No saved page loads the stylesheet directly." : $"Checked on {Math.Min(pages.Count, PagesToMatch)} page(s) that load it.";
			if (onDevices is not null)
			{
				return (OnDevices, $"{onDevices}. {checkedOn}");
			}

			if (inState is not null)
			{
				return (InState, $"{inState}. {checkedOn}");
			}

			if (whenPrinted is not null)
			{
				return (WhenPrinted, $"{whenPrinted}. {checkedOn}");
			}

			// Not on load: each selector of the rules is alive while every class it needs is on a page
			// or named in a script; the reference is dead only when no selector is.
			string? missing = null;
			string? fromScript = null;
			bool aliveWithoutScript = false;
			foreach (var part in rules.SelectMany(r => CssSelector.SplitList(r.SelectorText)))
			{
				string? gap = null;
				string? scripted = null;
				foreach (var name in SelectorClass.Each(part).Select(m => m.Groups["name"].Value))
				{
					if (pageClasses.Contains(name))
					{
						continue;
					}

					if (scriptNames.TryGetValue(name, out var where))
					{
						scripted ??= $"{part.Trim()} does not apply on load; .{name} is named in {where}";
						continue;
					}

					gap = $"{part.Trim()} needs .{name}, which no crawled page and no script names";
					break;
				}

				if (gap is not null)
				{
					missing ??= gap;
				}
				else if (scripted is not null)
				{
					fromScript ??= scripted;
				}
				else
				{
					aliveWithoutScript = true;
				}
			}

			if (fromScript is not null)
			{
				return (WhenScriptRuns, $"{fromScript}. {checkedOn}");
			}

			if (aliveWithoutScript || missing is null)
			{
				return (Undecided, $"{rules[0].SelectorText.Trim()}: its classes are on the pages, but no element matches on load. {checkedOn}");
			}

			return (Dead, $"{missing}. {checkedOn}");
		}
	}
}
