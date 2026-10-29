namespace CrawlerSuite.Core.Urls
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using CrawlerSuite.Core.Findings;

	/// <summary>
	/// The root a list of addresses shares — scheme and host (with a port that is not the default),
	/// e.g. "https://site.test" — so the screen can show an address on it from its path. Worked out
	/// from the addresses themselves: a snapshot opened from a folder carries no site name.
	/// </summary>
	public static class SiteRoot
	{
		/// <summary>
		/// The root most of <paramref name="urls"/> share; a tie goes to the first in ordinal order.
		/// Empty when none is an http(s) address.
		/// </summary>
		public static string Of(IEnumerable<string> urls) =>
			urls.Select(RootOf)
				.Where(r => r.Length > 0)
				.GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
				.OrderByDescending(g => g.Count())
				.ThenBy(g => g.Key, StringComparer.Ordinal)
				.Select(g => g.Key)
				.FirstOrDefault() ?? string.Empty;

		/// <summary>The root of a snapshot: the one most of its saved files share (crawler index, log 02). Empty without an index.</summary>
		public static string FromSnapshot(string snapshotDirectory)
		{
			var index = Path.Combine(snapshotDirectory, LogFileNames.CrawlerIndex);
			if (!File.Exists(index))
			{
				return string.Empty;
			}

			try
			{
				return Of(FindingFiles.Delimited(index, "|", 3, header: false).Select(c => c[1]));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return string.Empty;
			}
		}

		/// <summary>"https://site.test/a/b?x" → "https://site.test"; empty for anything not http(s).</summary>
		public static string RootOf(string? url) =>
			Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
				? uri.GetLeftPart(UriPartial.Authority)
				: string.Empty;

		/// <summary>
		/// The address from its path ("/a/b?x"), exactly as written, when it stands on
		/// <paramref name="root"/>; null for another host, another scheme or no root. A bare root
		/// becomes "/".
		/// </summary>
		public static string? PathOn(string? url, string? root)
		{
			if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(root))
			{
				return null;
			}

			var trimmed = url.Trim();
			if (!trimmed.StartsWith(root, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			// The root must end where the host ends: "https://site.test" is not the root of "https://site.test.example".
			var rest = trimmed[root.Length..];
			return rest.Length == 0 ? "/"
				: rest[0] is '/' or '?' or '#' ? (rest[0] == '/' ? rest : "/" + rest)
				: null;
		}
	}
}
