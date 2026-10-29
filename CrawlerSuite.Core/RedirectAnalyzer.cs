using System.Text;

namespace CrawlerSuite.Core
{
	public static class RedirectAnalyzer
	{
		// TIMESTAMP | URL | HTTPSTATUS | VARIABLEINFORMATION
		// Follow 301/302/303/307/308 redirects up to depth 3.
		/// <summary>
		/// The overview's facts for the redirects of log 03: how many of each kind — information,
		/// not a verdict (a temporary redirect during work and a permanent one by decision are both
		/// normal). Null when the log is missing.
		/// </summary>
		public static Host.StepFacts? Facts(string redirectLogPath)
		{
			if (!File.Exists(redirectLogPath))
			{
				return null;
			}

			var byCode = new SortedDictionary<int, int>();
			int total = 0;
			foreach (var line in File.ReadLines(redirectLogPath, Encoding.UTF8))
			{
				var parts = line.Split(" | ");
				if (parts.Length < 2 || Display.HttpCodes.Parse(parts[1]) is not { } code)
				{
					continue;
				}

				total++;
				byCode[code] = byCode.GetValueOrDefault(code) + 1;
			}

			static string Kind(int code) => code switch
			{
				301 or 308 => "permanent",
				302 or 307 => "temporary",
				303 => "see other",
				_ => Display.HttpCodes.Name(code),
			};

			return new Host.StepFacts(
				Host.StepFacts.Count(total),
				$"Redirects {Host.StepFacts.Count(total)}",
				byCode.Select(kv => $"{kv.Key} {Kind(kv.Key)} · {Host.StepFacts.Count(kv.Value)}").ToList(),
				"Information, not a verdict: a temporary and a permanent redirect can both be right.");
		}

		/// <summary>
		/// The redirects of log 03 by the address that redirects: its status as written and the
		/// address it names first. Empty when the log is missing.
		/// </summary>
		public static IReadOnlyDictionary<string, (string Status, string Target)> Targets(string? redirectLogPath)
		{
			var targets = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrEmpty(redirectLogPath) || !File.Exists(redirectLogPath))
			{
				return targets;
			}

			foreach (var line in File.ReadLines(redirectLogPath, Encoding.UTF8))
			{
				// source | status | target > where it led
				var parts = line.Split(" | ");
				if (parts.Length < 3 || Display.HttpCodes.Parse(parts[1]) is not (>= 300 and < 400))
				{
					continue;
				}

				var source = parts[0].Trim().TrimStart('﻿');
				var target = parts[2].Split(" > ")[0].Trim();
				targets.TryAdd(source, (parts[1].Trim(), target));
			}

			return targets;
		}

		public static void AnalyzeRedirects(string logFilePath, string outputFilePath)
		{
			var lines = File.ReadAllLines(logFilePath, Encoding.UTF8);

			var recordsByNormalized = new Dictionary<string, List<LogRecord>>(StringComparer.OrdinalIgnoreCase);
			List<LogRecord> allRecords = [];

			foreach (var raw in lines)
			{
				if (string.IsNullOrWhiteSpace(raw))
				{
					continue;
				}

				var parts = raw.Split('|').Select(p => p.Trim()).ToArray();
				if (parts.Length < 4)
				{
					continue;
				}

				var rec = new LogRecord
				{
					Timestamp = parts[0],
					Url = parts[1],
					HttpStatus = parts[2],
					VariableInformation = parts[3]
				};
				rec.NormalizedUrl = NormalizeUrl(rec.Url);
				allRecords.Add(rec);

				if (!recordsByNormalized.TryGetValue(rec.NormalizedUrl, out var list))
				{
					list = [];
					recordsByNormalized[rec.NormalizedUrl] = list;
				}
				list.Add(rec);
			}

			LogRecord? FindRecordForUrl(string url)
			{
				if (url == null)
				{
					return null;
				}

				var norm = NormalizeUrl(url);
				if (recordsByNormalized.TryGetValue(norm, out var list) && list.Count > 0)
				{
					// The first row for a URL is always its "crawled" row (written before the
					// fetch), which says nothing about where the URL led. Prefer the row that
					// does: its redirect, then its saved row, then any outcome, then "crawled".
					return list.FirstOrDefault(r => IsRedirectStatus(r.HttpStatus) && !IsIgnoredVariableInfo(r.VariableInformation))
						?? list.FirstOrDefault(r => r.HttpStatus.Equals("saved", StringComparison.OrdinalIgnoreCase)
							|| r.HttpStatus.Equals("savedAsset", StringComparison.OrdinalIgnoreCase))
						?? list.FirstOrDefault(r => !r.HttpStatus.Equals("crawled", StringComparison.OrdinalIgnoreCase))
						?? list[0];
				}

				return allRecords.FirstOrDefault(r => string.Equals(r.NormalizedUrl, norm, StringComparison.OrdinalIgnoreCase));
			}

			bool IsRedirectStatus(string status)
			{
				if (string.IsNullOrEmpty(status))
				{
					return false;
				}

				// 301, 302, 303, 307 and 308 — including the alternative names .NET has for
				// some of these codes (Moved, Redirect, RedirectMethod, RedirectKeepVerb).
				return RedirectStatusNames.Contains(status.Trim());
			}

			bool IsIgnoredVariableInfo(string variableInformation)
			{
				if (string.IsNullOrEmpty(variableInformation))
				{
					return false;
				}

				return variableInformation.TrimStart().StartsWith("Response status code does not indicate success", StringComparison.OrdinalIgnoreCase);
			}

			string? ExtractRedirectUri(string variableInformation)
			{
				if (string.IsNullOrEmpty(variableInformation))
				{
					return null;
				}

				if (IsIgnoredVariableInfo(variableInformation))
				{
					return null;
				}

				var v = variableInformation.Trim();
				// collection expression directly in Split (C# 11)
				foreach (var t in v.Split([' ', ';', ','], StringSplitOptions.RemoveEmptyEntries))
				{
					if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
						|| t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
						|| t.Contains("://"))
					{
						return t.Trim();
					}
				}
				return v;
			}

			string NormalizeUrl(string url)
			{
				if (string.IsNullOrEmpty(url))
				{
					return string.Empty;
				}

				url = url.Trim();
				if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
				{
					var builder = new UriBuilder(abs) { Query = string.Empty, Fragment = string.Empty };
					return builder.Uri.ToString().TrimEnd('/');
				}
				var qIdx = url.IndexOfAny(['?', '#']); // collection expression (C# 11)
				var pathOnly = qIdx >= 0 ? url[..qIdx] : url; // range operator (C# 11)
				return pathOnly.TrimEnd('/');
			}

			List<string> outLines = [];

			foreach (var rec in allRecords)
			{
				if (!IsRedirectStatus(rec.HttpStatus))
				{
					continue;
				}

				if (IsIgnoredVariableInfo(rec.VariableInformation))
				{
					continue;
				}

				var redirectUri = ExtractRedirectUri(rec.VariableInformation);
				if (string.IsNullOrEmpty(redirectUri))
				{
					continue;
				}

				List<string> followParts = [];
				string currentUri = redirectUri;
				var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

				for (int depth = 0; depth < 3; depth++)
				{
					var normCurrent = NormalizeUrl(currentUri);
					if (!seen.Add(normCurrent))
					{
						followParts.Add($"{currentUri} > (Loop)");
						break;
					}

					var destRec = FindRecordForUrl(currentUri);
					if (destRec == null)
					{
						followParts.Add($"{currentUri} > (NotFound)");
						break;
					}

					// ignore records that don't contain redirect info
					if (IsIgnoredVariableInfo(destRec.VariableInformation))
					{
						followParts.Add($"{destRec.HttpStatus} {destRec.Url} > (IgnoredVariableInfo)");
						break;
					}

					followParts.Add($"{destRec.HttpStatus} {destRec.Url}");

					if (IsRedirectStatus(destRec.HttpStatus))
					{
						var nextUri = ExtractRedirectUri(destRec.VariableInformation);
						if (string.IsNullOrEmpty(nextUri))
						{
							break;
						}

						var normNext = NormalizeUrl(nextUri);
						if (string.Equals(normNext, normCurrent, StringComparison.OrdinalIgnoreCase))
						{
							break;
						}

						currentUri = nextUri;
					}
					else
					{
						break;
					}
				}

				var followup = string.Join(" -> ", followParts);
				// [KEEP] Light sanitization: strip CR / LF / control chars from each
				// field to prevent line corruption. The redirect-log delimiter is
				// " | " (with spaces) so we do NOT strip pipes; fields are URLs and
				// HTTP status codes which never legitimately contain pipes anyway.
				// Sanitized via IssueLogWriter.SanitizeField using a sentinel
				// delimiter that won't match the URL content.
				var outLine = string.Join(" | ",
					IssueLogWriter.SanitizeField(rec.Url, '\uFFFF').Cleaned,
					IssueLogWriter.SanitizeField(rec.HttpStatus, '\uFFFF').Cleaned,
					IssueLogWriter.SanitizeField(redirectUri, '\uFFFF').Cleaned + " > " +
					IssueLogWriter.SanitizeField(followup, '\uFFFF').Cleaned);
				outLines.Add(outLine);
			}

			FileIo.WriteAllLinesWithRetry(outputFilePath, outLines, Path.GetFileName(outputFilePath));
		}

		private static readonly HashSet<string> RedirectStatusNames = new(StringComparer.OrdinalIgnoreCase)
		{
			"MovedPermanently", "Moved",
			"Found", "Redirect",
			"SeeOther", "RedirectMethod",
			"TemporaryRedirect", "RedirectKeepVerb",
			"PermanentRedirect",
		};

		private class LogRecord
		{
			public string Timestamp { get; set; } = string.Empty;
			public string Url { get; set; } = string.Empty;
			public string HttpStatus { get; set; } = string.Empty;
			public string VariableInformation { get; set; } = string.Empty;
			public string NormalizedUrl { get; set; } = string.Empty;
		}
	}
}
