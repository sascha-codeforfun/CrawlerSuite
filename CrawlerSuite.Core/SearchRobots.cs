namespace CrawlerSuite.Core
{
	using System.Text;

	/// <summary>
	/// Whether a page is out of search, by the rule search engines apply: its robots directives
	/// contain <c>noindex</c> or <c>none</c>, matched as single directives (case, order, spaces and
	/// any other directive do not matter). The directives come from <c>&lt;meta name="robots"&gt;</c>
	/// and from the <c>X-Robots-Tag</c> response header. In the header, directives aimed at one
	/// crawler (<c>googlebot: noindex</c>) are left out, as the page check reads only
	/// <c>name="robots"</c>, not a crawler's own meta tag. Fixed, not a setting.
	/// </summary>
	public static class SearchRobots
	{
		private static readonly HashSet<string> OutOfSearch = new(StringComparer.OrdinalIgnoreCase) { "noindex", "none" };

		// Directives that carry a value after a colon; anything else before a colon names a crawler.
		private static readonly HashSet<string> ValueDirectives = new(StringComparer.OrdinalIgnoreCase)
		{
			"max-snippet", "max-image-preview", "max-video-preview", "unavailable_after", "indexifembedded",
		};

		/// <summary>True when the meta robots value or any of the header values takes the page out of search.</summary>
		public static bool IsOutOfSearch(string? metaRobots, IEnumerable<string>? headerValues = null) =>
			Directives(metaRobots).Any(OutOfSearch.Contains)
			|| (headerValues ?? []).Any(h => GeneralHeaderDirectives(h).Any(OutOfSearch.Contains));

		/// <summary>The directives of a meta robots value.</summary>
		public static IEnumerable<string> Directives(string? value) =>
			(value ?? string.Empty).Split(',').Select(d => d.Trim()).Where(d => d.Length > 0);

		/// <summary>The directives of an X-Robots-Tag value that apply to every crawler.</summary>
		public static IEnumerable<string> GeneralHeaderDirectives(string? value)
		{
			bool crawlerScope = false;
			foreach (var raw in Directives(value))
			{
				var token = raw;
				var colon = token.IndexOf(':');
				if (colon > 0 && !ValueDirectives.Contains(token[..colon].Trim()))
				{
					// "googlebot: noindex" — from here on the directives are that crawler's.
					crawlerScope = true;
					token = token[(colon + 1)..].Trim();
				}

				if (!crawlerScope && token.Length > 0)
				{
					yield return token;
				}
			}
		}

		/// <summary>
		/// The X-Robots-Tag values of a downloaded file, from its ".header" sidecar (every
		/// occurrence: a server may send the header more than once). Empty when there is none.
		/// </summary>
		public static IReadOnlyList<string> HeaderValuesOf(string downloadedFile)
		{
			var sidecar = Path.ChangeExtension(downloadedFile, HeaderSidecar.HeaderSidecarExtension.TrimStart('.'));
			if (!File.Exists(sidecar))
			{
				return [];
			}

			try
			{
				var values = new List<string>();
				bool inResponse = false;
				foreach (var line in File.ReadLines(sidecar, Encoding.UTF8))
				{
					if (line.StartsWith("=== RESPONSE ===", StringComparison.Ordinal))
					{
						inResponse = true;
						continue;
					}

					var colon = line.IndexOf(':');
					if (inResponse && colon > 0 && line[..colon].Trim().Equals("X-Robots-Tag", StringComparison.OrdinalIgnoreCase))
					{
						values.Add(line[(colon + 1)..].Trim());
					}
				}

				return values;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return [];
			}
		}
	}
}
