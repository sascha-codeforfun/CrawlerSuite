namespace CrawlerSuite.Core.Display
{
	/// <summary>One step of a redirect chain: the URL reached, the status it answered with, and how the chain ended there.</summary>
	public sealed record RedirectHop(string Url, int? Status, string? Ending);

	/// <summary>
	/// Reads the third column of log 03 — "target &gt; status url -&gt; status url &gt; (Loop)" — into
	/// one hop per line. The first hop is where the redirect points; each later part says what that
	/// URL answered. Never throws: an unexpected shape becomes a single hop with the raw text.
	/// </summary>
	public static class RedirectChain
	{
		private static readonly string[] Endings = ["(Loop)", "(NotFound)", "(IgnoredVariableInfo)"];

		public static IReadOnlyList<RedirectHop> Parse(string? chain)
		{
			if (string.IsNullOrWhiteSpace(chain))
			{
				return [];
			}

			var hops = new List<RedirectHop>();
			int split = chain.IndexOf(" > ", StringComparison.Ordinal);
			var target = (split < 0 ? chain : chain[..split]).Trim();
			hops.Add(new RedirectHop(target, null, null));
			if (split < 0)
			{
				return hops;
			}

			foreach (var raw in chain[(split + 3)..].Split(" -> ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var part = raw;
				string? ending = null;
				foreach (var candidate in Endings)
				{
					if (part.EndsWith(" > " + candidate, StringComparison.Ordinal))
					{
						ending = candidate.Trim('(', ')');
						part = part[..^(candidate.Length + 3)].Trim();
						break;
					}
				}

				// "Found https://…" or just "https://…" (a loop names only the URL).
				int space = part.IndexOf(' ');
				int? status = space > 0 ? HttpCodes.Parse(part[..space]) : null;
				var url = status is null ? part : part[(space + 1)..].Trim();

				// The URL a hop answered for is usually the one the previous line pointed to: merge.
				if (hops[^1].Status is null && hops[^1].Ending is null && hops[^1].Url.Equals(url, StringComparison.OrdinalIgnoreCase))
				{
					hops[^1] = new RedirectHop(url, status, ending);
				}
				else
				{
					hops.Add(new RedirectHop(url, status, ending));
				}
			}

			return hops;
		}

		/// <summary>"NotFound" → "Not crawled" etc.: the ending in words for the screen.</summary>
		public static string EndingText(string ending) => ending switch
		{
			"Loop" => "Loop",
			"NotFound" => "Not crawled",
			"IgnoredVariableInfo" => "Ignored (variable info)",
			_ => ending,
		};
	}
}
