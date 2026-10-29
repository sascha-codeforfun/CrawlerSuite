namespace CrawlerSuite.Core.Urls
{
	using System;
	using System.Text.RegularExpressions;

	/// <summary>
	/// What a file address is, when the screen has nothing better to say about it: a stylesheet or a
	/// script, named by its file name without path and cache buster ("water.min.css").
	/// </summary>
	public static class FileKind
	{
		public const string Stylesheet = "Stylesheet";
		public const string Script = "Script";

		// A cache buster inside a file name: 16 or more hex digits between dots ("lib.min.<hash>.css"),
		// the same form the triage ledger key ignores.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex CacheBuster = new(@"\.[0-9A-Fa-f]{16,}(?=\.|$)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

		/// <summary>"Stylesheet" for .css, "Script" for .js and .mjs (by the address's path); null for anything else.</summary>
		public static string? Of(string? url)
		{
			var path = PathOf(url);
			return path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? Stylesheet
				: path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ? Script
				: null;
		}

		/// <summary>The file name without its path and cache buster: ".../water.min.0b21…e5.css" → "water.min.css".</summary>
		public static string ShortName(string? url)
		{
			var path = PathOf(url).TrimEnd('/');
			int slash = path.LastIndexOf('/');
			var name = slash >= 0 ? path[(slash + 1)..] : path;
			return name.Length == 0 ? url ?? string.Empty : CacheBuster.Replace(name, string.Empty);
		}

		private static string PathOf(string? url)
		{
			var trimmed = url?.Trim() ?? string.Empty;
			if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
			{
				return uri.AbsolutePath;
			}

			int cut = trimmed.IndexOfAny(['?', '#']);
			return cut >= 0 ? trimmed[..cut] : trimmed;
		}
	}
}
