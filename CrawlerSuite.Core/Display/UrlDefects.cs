using System.Text.RegularExpressions;

namespace CrawlerSuite.Core.Display
{
	/// <summary>
	/// Obvious defects in a URL, for colouring links amber: a doubled extension, a double slash
	/// in the path, script leftovers, whitespace, and a second '?'. Read-only; never changes the URL.
	/// </summary>
	public static partial class UrlDefects
	{
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex DoubledExtensionRegex = new(@"\.(html?|php|aspx?|jsp|pdf|jpe?g|png|gif|svg|webp|css|js|xml|json|txt|docx?|xlsx?|pptx?|zip)\.\1(?=$|[?#/])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static Regex DoubledExtension() => DoubledExtensionRegex;

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex ScriptLeftoverRegex = new(@"(?:^|/)(?:undefined|null|NaN|\[object%20Object\]|\[object Object\])(?=$|[/?#.])|\{\{|\}\}|%7B%7B|%7D%7D", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static Regex ScriptLeftover() => ScriptLeftoverRegex;

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex WhitespaceRegex = new(@"\s|%20(?=$|[/?#])|(?<=/)%20", RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static Regex Whitespace() => WhitespaceRegex;

		/// <summary>The defects found, as short names for a tooltip; empty when the URL looks fine.</summary>
		public static IReadOnlyList<string> Find(string? url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return [];
			}

			var found = new List<string>();
			var path = PathOf(url);

			if (DoubledExtension().IsMatch(path))
			{
				found.Add("Doubled extension");
			}

			if (path.Contains("//", StringComparison.Ordinal))
			{
				found.Add("Double slash in the path");
			}

			if (ScriptLeftover().IsMatch(url))
			{
				found.Add("Script leftover in the URL");
			}

			if (Whitespace().IsMatch(url.Trim()) || url.Length != url.Trim().Length)
			{
				found.Add("Whitespace in the URL");
			}

			int hash = url.IndexOf('#');
			if ((hash < 0 ? url : url[..hash]).Count(c => c == '?') >= 2)
			{
				found.Add("Two '?' in one URL");
			}

			return found;
		}

		/// <summary>The path part: after scheme and host, before the query and fragment.</summary>
		private static string PathOf(string url)
		{
			int start = 0;
			int scheme = url.IndexOf("://", StringComparison.Ordinal);
			if (scheme >= 0)
			{
				int slash = url.IndexOf('/', scheme + 3);
				start = slash < 0 ? url.Length : slash;
			}

			int end = url.IndexOfAny(['?', '#'], start);
			return end < 0 ? url[start..] : url[start..end];
		}
	}
}
