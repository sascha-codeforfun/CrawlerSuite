namespace CrawlerSuite.Core.Snapshots
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Text;

	/// <summary>What a snapshot's crawl did with one address: its last outcome, and where it was sent when redirected.</summary>
	/// <param name="Status">The status column: saved, savedAsset, N/A, NotFound, Found …; "crawled" when nothing followed.</param>
	/// <param name="Message">The message column of that row.</param>
	/// <param name="RedirectedTo">Where a redirect sent it; null when it was not redirected.</param>
	public sealed record CrawlOutcome(string Status, string Message, string? RedirectedTo);

	/// <summary>
	/// Reads a snapshot's crawl log to say why a file is in one snapshot and not the other: the
	/// other crawl failed to download it, got a 404, was redirected, or never asked for it. From
	/// the raw log (00), which keeps every row; the projected log (01) when 00 is gone.
	/// </summary>
	public static class CrawlOutcomes
	{
		private const string ResponseNote = "Response status code does not indicate success";

		/// <summary>The outcome of every address in the snapshot's crawl log; empty when it has none.</summary>
		public static IReadOnlyDictionary<string, CrawlOutcome> Read(string snapshot)
		{
			var outcomes = new Dictionary<string, CrawlOutcome>(StringComparer.Ordinal);
			var path = Path.Combine(snapshot, LogFileNames.CrawlerRaw);
			if (!File.Exists(path))
			{
				path = Path.Combine(snapshot, LogFileNames.Crawler);
			}

			if (!File.Exists(path))
			{
				return outcomes;
			}

			try
			{
				foreach (var line in File.ReadLines(path, Encoding.UTF8))
				{
					var parts = line.Split(" | ");
					if (parts.Length < 4)
					{
						continue;
					}

					var url = Key(parts[1]);
					var (status, message) = (parts[2].Trim(), parts[3].Trim());
					outcomes.TryGetValue(url, out var known);
					if (status == "crawled")
					{
						outcomes[url] = known ?? new CrawlOutcome(status, message, null);
						continue;
					}

					// A redirect is logged twice: once with its target, once with the response note.
					var target = message.StartsWith("http", StringComparison.OrdinalIgnoreCase) && IsRedirect(status) ? message : known?.RedirectedTo;
					outcomes[url] = new CrawlOutcome(status, message, IsRedirect(status) ? target : null);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Logger.LogWarning($"Comparison: could not read a crawl log — {ex.Message}");
			}

			return outcomes;
		}

		/// <summary>The address as the logs compare it: trimmed, the default https port dropped.</summary>
		public static string Key(string url) => url.Replace(":443", string.Empty, StringComparison.Ordinal).Trim();

		/// <summary>
		/// What <paramref name="side"/>'s crawl did with the address, in a sentence; empty when that
		/// snapshot has no crawl log to tell.
		/// </summary>
		public static string Describe(string side, IReadOnlyDictionary<string, CrawlOutcome> log, string? url)
		{
			if (log.Count == 0 || string.IsNullOrEmpty(url))
			{
				return string.Empty;
			}

			if (!log.TryGetValue(Key(url), out var outcome))
			{
				return $"{side}'s crawl never asked for this address: no page it saw linked to it.";
			}

			var detail = outcome.Message.StartsWith(ResponseNote, StringComparison.Ordinal)
				? Between(outcome.Message, ": ", ".")
				: outcome.Message;
			return outcome.Status switch
			{
				"crawled" => $"{side}'s crawl asked for this address but recorded no outcome (the crawl did not finish).",
				"saved" or "savedAsset" => $"{side}'s crawl saved this address, under another file name.",
				"N/A" => $"{side}'s crawl could not download it: {detail}",
				"Oversized" => $"{side}'s crawl did not store it: over the single-file limit.",
				_ when outcome.RedirectedTo is { } target => $"{side}'s crawl was redirected ({outcome.Status}) to {target}",
				_ => $"{side}'s crawl got {detail}.",
			};
		}

		private static bool IsRedirect(string status) => status is "MovedPermanently" or "Found" or "Redirect" or "SeeOther"
			or "TemporaryRedirect" or "PermanentRedirect" or "RedirectKeepVerb" or "RedirectMethod" or "Moved";

		private static string Between(string text, string start, string end)
		{
			int from = text.IndexOf(start, StringComparison.Ordinal);
			if (from < 0)
			{
				return text;
			}

			from += start.Length;
			int to = text.LastIndexOf(end, StringComparison.Ordinal);
			return to > from ? text[from..to] : text[from..];
		}
	}
}
