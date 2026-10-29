using System.Text.RegularExpressions;

namespace CrawlerSuite.Core.Urls
{
	// ── Extractor ──────────────────────────────────────────────────────────
	//
	// [KEEP] Extracts URLs from HTML and CSS content that standard link-following
	// logic misses. Required because modern CMS platforms embed URLs
	// in locations that are invisible to <a href> crawlers:
	//
	//   - data-* attributes: component configuration, e.g. data-pdf-link="/content/..."
	//     Component-based pages hide PDFs, HTML pages, and API endpoints here.
	//     Without this, linked PDFs are never downloaded and cannot be analysed.
	//
	//   - <script src>: JavaScript files need 404 checking even though their
	//     content is not scanned (minified JS yields no useful URL signal).
	//
	//   - <link href> (non-canonical): CSS files need downloading for both
	//     404 checking and one level of url() extraction (fonts, images, PDFs
	//     referenced in stylesheets). Canonical is handled separately.
	//
	//   - <form action>: Form endpoints are real server-side URLs.
	//     A broken form action is a functional defect, not just an SEO issue.
	//
	//   - JSON string paths in <script> blocks: CMS configuration injected as
	//     inline JSON often contains paths to PDFs, images, and API endpoints.
	//     These are real URLs that would 404 if the asset is moved or deleted.
	//
	//   - CSS url(): Stylesheets reference fonts, background images, and
	//     occasionally PDFs. One level of extraction only — CSS from CSS is not
	//     followed because it creates circular dependency risks.
	//
	// [KEEP] CSS files are scanned one level deep. JS files are NOT scanned
	// (minified, no useful URL signal).
	// Images, fonts, and PDFs are leaf nodes — downloaded but not scanned.
	//
	// [KEEP] All extracted URLs are resolved to absolute form using the page URL
	// as the base. Same-site filtering is applied by the caller (Crawler.cs)
	// using Validity.IsInDownloadScope, consistent with <a href> handling.
	//
	// [KEEP] The ExtractedSource enum records WHY a URL was found. This feeds
	// into 18-extended-sources.log so operators know which URLs came from
	// non-standard locations and can audit the extraction logic.
	// ─────────────────────────────────────────────────────────────────────────

	public static partial class Extractor
	{
		// [KEEP] Source categories — recorded in 18-extended-sources.log.
		// Extend this enum when new extraction sources are added, never remove values
		// (existing log entries would lose meaning).
		public enum ExtractedSource
		{
			DataAttribute,   // data-* attribute on any element
			ScriptSrc,       // <script src="...">
			LinkHref,        // <link href="..."> (non-canonical CSS/assets)
			FormAction,      // <form action="...">
			JsonPath,        // string path inside <script> block JSON/config
			CssUrl,          // url(...) inside a CSS file
		}

		public record ExtractedUrl(string Url, ExtractedSource Source, string SourceDetail)
		{
			/// <summary>The reference as written in the page or stylesheet, before resolving.</summary>
			public string Raw { get; init; } = string.Empty;
		}

		// ── HTML extraction ───────────────────────────────────────────────────

		/// <summary>
		/// Extracts all non-standard URLs from raw HTML content.
		/// Returns absolute URLs resolved against pageUrl.
		/// Caller is responsible for same-site filtering.
		/// </summary>
		public static List<ExtractedUrl> FromHtml(string html, string pageUrl,
			IReadOnlyList<string>? jsonPathPrefixes = null)
		{
			var results = new List<ExtractedUrl>();
			var baseUri = TryParseUri(pageUrl);
			if (baseUri == null)
			{
				return results;
			}

			// [KEEP] data-* attributes: CMS components store URLs here for JS consumption.
			// This is the primary source of hidden PDFs and page links in component-based CMS sites.
			// Pattern matches both relative (/content/...) and absolute (https://...) values.
			ExtractDataAttributes(html, baseUri, results);

			// [KEEP] <script src>: JS files must be downloaded for 404 checking.
			// Content is NOT scanned — minified JS yields no useful URL signal.
			ExtractScriptSrc(html, baseUri, results);

			// [KEEP] <link href> for CSS and assets: stylesheets are scanned one level
			// deep for url() references. This catches fonts, background images, and PDFs
			// referenced in CSS. Canonical links are excluded (handled separately).
			ExtractLinkHref(html, baseUri, results);

			// [KEEP] <form action>: form endpoints are real server URLs.
			// A broken action URL is a functional defect — the form silently fails.
			ExtractFormActions(html, baseUri, results);

			// [KEEP] JSON/config paths in <script> blocks — only when jsonPathPrefixes
			// is configured. Prefixes are site-specific and come from the site's settings in
			// crawlersuite.config.json, so no site's path structure is built into the code.
			if (jsonPathPrefixes != null && jsonPathPrefixes.Count > 0)
			{
				ExtractJsonPaths(html, baseUri, results, jsonPathPrefixes);
			}

			return results;
		}

		/// <summary>
		/// Extracts url() references from raw CSS content.
		/// Returns absolute URLs resolved against cssUrl.
		/// [KEEP] Called once per CSS file — CSS from CSS is NOT followed to
		/// prevent circular dependency chains.
		/// </summary>
		public static List<ExtractedUrl> FromCss(string css, string cssUrl)
		{
			var results = new List<ExtractedUrl>();
			var baseUri = TryParseUri(cssUrl);
			if (baseUri == null)
			{
				return results;
			}

			// [KEEP] CSS url() references: fonts, background images, PDFs.
			// One level only — do not follow CSS imported from CSS.
			foreach (Match m in CssUrlPattern().Each(css))
			{
				var raw = m.Groups[1].Value.Trim().Trim('"', '\'');
				if (string.IsNullOrEmpty(raw) || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var resolved = Resolve(baseUri, raw);
				if (resolved != null)
				{
					results.Add(new ExtractedUrl(resolved, ExtractedSource.CssUrl, "url()") { Raw = raw });
				}
			}

			return results;
		}

		// ── Private extractors ────────────────────────────────────────────────

		private static void ExtractDataAttributes(string html, Uri baseUri,
			List<ExtractedUrl> results)
		{
			foreach (Match m in DataAttributePattern().Each(html))
			{
				var attrName = m.Groups[1].Value;
				var value = m.Groups[2].Value.Trim();
				if (string.IsNullOrEmpty(value))
				{
					continue;
				}

				var resolved = Resolve(baseUri, value);
				if (resolved != null)
				{
					results.Add(new ExtractedUrl(resolved, ExtractedSource.DataAttribute,
						$"data-{attrName}") { Raw = value });
				}
			}
		}

		private static void ExtractScriptSrc(string html, Uri baseUri,
			List<ExtractedUrl> results)
		{
			foreach (Match m in ScriptSrcPattern().Each(html))
			{
				var src = m.Groups[1].Value.Trim();
				var resolved = Resolve(baseUri, src);
				if (resolved != null)
				{
					results.Add(new ExtractedUrl(resolved, ExtractedSource.ScriptSrc, "<script src>") { Raw = src });
				}
			}
		}

		private static void ExtractLinkHref(string html, Uri baseUri,
			List<ExtractedUrl> results)
		{
			// rel before href: <link rel="stylesheet" href="...">
			foreach (Match m in LinkHrefRelFirstPattern().Each(html))
			{
				var rel = m.Groups[1].Value.Trim();
				var href = m.Groups[2].Value.Trim();
				if (rel.Equals("canonical", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var resolved = Resolve(baseUri, href);
				if (resolved != null)
				{
					results.Add(new ExtractedUrl(resolved, ExtractedSource.LinkHref,
						$"<link rel={rel}>") { Raw = href });
				}
			}
			// href before rel: <link href="..." rel="stylesheet">
			foreach (Match m in LinkHrefHrefFirstPattern().Each(html))
			{
				var href = m.Groups[1].Value.Trim();
				var rel = m.Groups[2].Value.Trim();
				if (rel.Equals("canonical", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var resolved = Resolve(baseUri, href);
				if (resolved != null)
				{
					results.Add(new ExtractedUrl(resolved, ExtractedSource.LinkHref,
						$"<link rel={rel}>") { Raw = href });
				}
			}
		}

		private static void ExtractFormActions(string html, Uri baseUri,
			List<ExtractedUrl> results)
		{
			foreach (Match m in FormActionPattern().Each(html))
			{
				var action = m.Groups[1].Value.Trim();
				// [KEEP] Skip non-HTML form targets — .bin, .json, .xml etc. are POST
				// endpoints that return errors on GET and are not crawlable pages.
				var ext = Path.GetExtension(action).ToLowerInvariant();
				if (!string.IsNullOrEmpty(ext) && ext != ".html" && ext != ".htm")
				{
					continue;
				}

				var resolved = Resolve(baseUri, action);
				if (resolved != null)
				{
					results.Add(new ExtractedUrl(resolved, ExtractedSource.FormAction, "<form action>") { Raw = action });
				}
			}
		}

		private static void ExtractJsonPaths(string html, Uri baseUri,
			List<ExtractedUrl> results, IReadOnlyList<string> prefixes)
		{
			// [KEEP] Build a regex from the configured prefixes — site-specific and
			// supplied via crawlersuite.config.json. Never hardcode path prefixes here.
			// The pattern matches quoted strings starting with any configured prefix.
			var escapedPrefixes = prefixes
				.Select(p => Regex.Escape(p.TrimEnd('/')))
				.ToList();
			if (escapedPrefixes.Count == 0)
			{
				return;
			}

			var pattern = new Regex(
				$@"[""']((?:{string.Join('|', escapedPrefixes)})/[^""'<>\s?#]{{3,}})[""']",
				RegexOptions.IgnoreCase);

			foreach (Match m in pattern.Each(html))
			{
				var path = m.Groups[1].Value.Trim();
				var resolved = Resolve(baseUri, path);
				if (resolved != null)
				{
					results.Add(new ExtractedUrl(resolved, ExtractedSource.JsonPath,
						"<script> JSON/config") { Raw = path });
				}
			}
		}

		// ── Regex patterns ────────────────────────────────────────────────────

		// [KEEP] data-* attribute pattern: captures the attribute name and value.
		// [KEEP] Restricted to values starting with / (relative path) or http (absolute URL).
		// Numeric values, short strings, and non-path values are excluded — they are
		// component configuration (timeouts, pixel sizes, IDs), not URLs.
		// Example: data-floe-report-link="/files/tide/...pdf"
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex DataAttributePatternRegex = new(@"data-([\w-]+)=[""']((?:/|https?://)[^""']{4,})[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static Regex DataAttributePattern() => DataAttributePatternRegex;

		// [KEEP] <script src>: JS files for 404 checking only.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex ScriptSrcPatternRegex = new(@"<script[^>]+\bsrc=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static Regex ScriptSrcPattern() => ScriptSrcPatternRegex;

		// [KEEP] <link rel href>: two patterns handle both attribute orderings.
		// rel before href: <link rel="stylesheet" href="...">
		// href before rel: <link href="..." rel="stylesheet">
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex LinkHrefRelFirstPatternRegex = new(@"<link[^>]+\brel=[""']([^""']+)[""'][^>]*\bhref=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static Regex LinkHrefRelFirstPattern() => LinkHrefRelFirstPatternRegex;

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex LinkHrefHrefFirstPatternRegex = new(@"<link[^>]+\bhref=[""']([^""']+)[""'][^>]*\brel=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static Regex LinkHrefHrefFirstPattern() => LinkHrefHrefFirstPatternRegex;

		// [KEEP] <form action>: form submission endpoints.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex FormActionPatternRegex = new(@"<form[^>]+\baction=[""']([^""'#?][^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static Regex FormActionPattern() => FormActionPatternRegex;

		// [KEEP] CSS url(): fonts, background images, PDFs in stylesheets.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex CssUrlPatternRegex = new(@"url\(\s*[""']?([^""')\s]+)[""']?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static Regex CssUrlPattern() => CssUrlPatternRegex;

		// ── Helpers ───────────────────────────────────────────────────────────

		private static Uri? TryParseUri(string url)
		{
			try { return new Uri(url); }
			catch { return null; }
		}

		/// <summary>
		/// Resolves a raw href/src/path against a base URI.
		/// Returns null for fragments, data URIs, mailto, tel, javascript, and empty strings.
		/// </summary>
		internal static string? Resolve(Uri baseUri, string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				return null;
			}

			if (raw.StartsWith('#'))
			{
				return null;
			}

			if (raw.StartsWith('?'))
			{
				return null;
			}

			if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			if (raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			if (raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			// Protocol-relative → assume https
			if (raw.StartsWith("//"))
			{
				raw = "https:" + raw;
			}

			try
			{
				var resolved = new Uri(baseUri, raw).ToString();
				// [KEEP] Guard against purely numeric paths like /10000, /-1234567890.
				// These are component configuration values (timeouts, IDs) mistakenly
				// matched by broad patterns. A real URL path always contains non-digit chars.
				var path = new Uri(resolved).AbsolutePath;
				if (path.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(
					path.TrimStart('/'), @"^-?\d+$"))
				{
					return null;
				}

				return resolved;
			}
			catch { return null; }
		}
	}
}
