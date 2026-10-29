using HtmlAgilityPack;

namespace CrawlerSuite.Core.Quality
{
	internal static class HtmlParseErrors
	{
		/// <summary>
		/// Tier-2 MALFORMED_HTML check (<c>MALFORMED_HTML:&lt;code&gt;</c>): bridges
		/// HtmlAgilityPack's <c>HtmlDocument.ParseErrors</c> from a raw-HTML parse
		/// into findings. One finding per (file, error code): the code goes in
		/// Detail (so the promoted Word is MALFORMED_HTML:&lt;code&gt;, a stable Key)
		/// and the occurrence count goes in the Context only (e.g.
		/// <c>TagNotClosed (3 occurrence(s))</c>) so a run-to-run-varying count
		/// never churns the Key. Composite Word <c>MALFORMED_HTML:&lt;code&gt;</c>,
		/// log 10, auto-promoted.
		///
		/// Deliberately conservative on what it emits: only the parser-error
		/// <c>Code</c> and a count. HAP's free-text <c>Reason</c> and
		/// <c>SourceText</c> fields are NOT used — they are unbounded and can carry
		/// control characters / newlines that would need sanitising, and they add
		/// no triage value here (the code names the defect; the page is the fix
		/// target). There is no code whitelist; gate the whole check off via
		/// <see cref="MalformedHtmlConfig.DetectHtmlParseErrors"/> if a noisy
		/// site floods the log.
		///
		/// Parses raw HTML (not simplified) so the errors reflect what the server
		/// actually emitted, before any stripping/rewriting.
		/// </summary>
		/// <param name="parse">
		/// Gives the page parsed from <paramref name="html"/> with the standard settings, when the
		/// caller shares one parse between checks (the page is only read); null parses it here. Asked
		/// at the same point the check would parse, so a page that fails to parse fails at the same place.
		/// </param>
		internal static IEnumerable<QualityIssue> Check(
			string filename, string html, IReadOnlyCollection<string>? suppressCodes = null, System.Func<HtmlDocument>? parse = null)
		{
			var doc = parse is null ? CrawlerSuite.Core.Html.HtmlDocuments.Parse(html) : parse();

			var parseErrors = doc.ParseErrors;
			if (parseErrors is null)
			{
				yield break;
			}

			var suppressed = suppressCodes is { Count: > 0 }
				? new HashSet<string>(suppressCodes, StringComparer.OrdinalIgnoreCase)
				: null;

			// Aggregate by error code → occurrence count. Preserve first-seen
			// order so output is deterministic across runs (byte-stable logs).
			var counts = new Dictionary<string, int>(StringComparer.Ordinal);
			var order = new List<string>();
			foreach (var err in parseErrors)
			{
				var code = err.Code.ToString();
				if (suppressed is not null && suppressed.Contains(code))
				{
					continue;
				}

				if (!counts.TryGetValue(code, out var n))
				{
					counts[code] = 1;
					order.Add(code);
				}
				else
				{
					counts[code] = n + 1;
				}
			}

			foreach (var code in order)
			{
				var n = counts[code];
				yield return new QualityIssue(
					filename,
					"MALFORMED_HTML",
					code,
					$"{code} ({n} occurrence(s))");
			}
		}
	}
}
