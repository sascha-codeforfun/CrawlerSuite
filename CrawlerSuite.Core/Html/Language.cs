namespace CrawlerSuite.Core.Html
{
	using System.Diagnostics.CodeAnalysis;
	using System.IO;
	using System.Text;
	using HtmlAgilityPack;
	using System.Text.RegularExpressions;

	/// <summary>
	/// Resolves the page language for a downloaded HTML file or a parsed document.
	/// FromHtmlFile is the file-reading wrapper;
	/// FromMeta carries the resolution logic: &lt;html lang&gt; attribute
	/// first, then &lt;meta name="language"&gt;, then the fallback language.
	/// </summary>
	public static partial class Language
	{
		[ExcludeFromCodeCoverage(Justification =
			"Thin wrapper: reads file → calls FromMeta. FromMeta " +
			"carries the real logic and is tested directly.")]
		public static string FromHtmlFile(string filename, string fileDownloadDirectory, string fallbackLanguage)
		{
			var source = Path.Combine(fileDownloadDirectory, filename);
			var doc = CrawlerSuite.Core.Html.HtmlDocuments.Create();
			doc.LoadHtml(CrawlerSuite.Core.Host.WorkFile.ReadAllText(source, Encoding.UTF8));
			return FromMeta(doc, fallbackLanguage);
		}

		/// <summary>
		/// Resolves the page language from the HTML document.
		/// Priority:
		///   1. &lt;html lang="..."&gt; — most authoritative, used by browsers and screen readers
		///   2. &lt;meta name="language" content="..."&gt; — CMS-set, sometimes incorrect
		///   3. fallback language
		/// When both are present and disagree, the html lang attribute wins. The mismatch
		/// is detected separately by ContentQuality.Analyse and reported as LANGUAGE_MISMATCH.
		/// </summary>
		public static string FromMeta(HtmlDocument doc, string fallback)
		{
			// Prefer <html lang="..."> — most reliable signal.
			var htmlNode = doc.DocumentNode.SelectSingleNode("//html");
			var htmlLang = htmlNode?.GetAttributeValue("lang", string.Empty)?.Trim();
			if (!string.IsNullOrWhiteSpace(htmlLang))
			{
				// Normalise "de-DE" → "de" etc.
				var code = htmlLang.Split('-')[0].ToLowerInvariant();
				if (IsISO6391(code))
				{
					return code;
				}
			}

			// Fall back to <meta name="language">.
			var langNode = doc.DocumentNode.SelectSingleNode("//meta[@name='language']");
			if (langNode != null)
			{
				var metaLang = langNode.GetAttributeValue("content", string.Empty);
				if (!string.IsNullOrWhiteSpace(metaLang) && IsISO6391(metaLang))
				{
					return metaLang;
				}
			}

			return fallback;
		}

		/// <summary>
		/// As <see cref="FromMeta"/>, but the tag kept whole ("en-US" stays "en-us", normalised): the
		/// spell check picks a dictionary flavour from it. Every other check uses the language alone.
		/// </summary>
		public static string TagFromMeta(HtmlDocument doc, string fallback)
		{
			var htmlLang = doc.DocumentNode.SelectSingleNode("//html")?.GetAttributeValue("lang", string.Empty)?.Trim();
			if (!string.IsNullOrWhiteSpace(htmlLang) && IsISO6391(htmlLang.Split('-')[0].ToLowerInvariant()))
			{
				return CrawlerSuite.Core.Lexicon.LanguageTags.Normalize(htmlLang);
			}

			var metaLang = doc.DocumentNode.SelectSingleNode("//meta[@name='language']")?.GetAttributeValue("content", string.Empty)?.Trim();
			if (!string.IsNullOrWhiteSpace(metaLang) && IsISO6391(metaLang))
			{
				return metaLang; // the same rule as FromMeta: a bare code only
			}

			return fallback;
		}

		/// <summary>As <see cref="NearestElementLanguage"/>, but the tag kept whole (normalised): for the spell check's flavour.</summary>
		public static string NearestElementTag(HtmlNode? node, string fallback) =>
			NearestLanguageElement(node) is { } element
				? CrawlerSuite.Core.Lexicon.LanguageTags.Normalize(element.GetAttributeValue("lang", string.Empty))
				: fallback;

		internal static bool IsISO6391(string language)
		{
			return ISO6391().IsMatch(language);
		}

		/// <summary>
		/// Resolves the language declared CLOSEST to <paramref name="node"/> by walking up the
		/// ancestor chain and returning the first valid <c>lang</c> attribute — child overrides
		/// ancestor, per HTML inheritance — normalised "de-DE" → "de". Falls back to
		/// <paramref name="fallback"/> when no ancestor declares a usable lang (the page-level
		/// branch language, itself already resolved from &lt;html lang&gt; / meta / default, so a
		/// node with no nearer island still lands on the page language). Honours per-element lang
		/// islands so a word is checked against the dictionary for the language it is written in.
		/// </summary>
		public static string NearestElementLanguage(HtmlNode? node, string fallback) =>
			NearestLanguageElement(node) is { } element ? CodeOf(element)! : fallback;

		/// <summary>
		/// The nearest element at or above <paramref name="node"/> whose <c>lang</c> names a language
		/// (the element <see cref="NearestElementLanguage"/> takes its code from); null when none does.
		/// Two text nodes with different such elements are in different languages.
		/// </summary>
		public static HtmlNode? NearestLanguageElement(HtmlNode? node)
		{
			for (HtmlNode? current = node;
				current != null && current.NodeType != HtmlNodeType.Document;
				current = current.ParentNode)
			{
				if (current.NodeType == HtmlNodeType.Element && CodeOf(current) is not null)
				{
					return current;
				}
			}

			return null;
		}

		private static string? CodeOf(HtmlNode element)
		{
			var raw = element.GetAttributeValue("lang", string.Empty)?.Trim();
			if (string.IsNullOrWhiteSpace(raw))
			{
				return null;
			}

			var code = raw.Split('-')[0].ToLowerInvariant();
			return IsISO6391(code) ? code : null;
		}

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex ISO6391Regex = new(@"^[a-z]{2}$", RegexOptions.Compiled);

		private static Regex ISO6391() => ISO6391Regex;
	}
}
