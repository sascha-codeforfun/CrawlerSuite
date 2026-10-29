namespace CrawlerSuite.Core
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Text.Json;
	using System.Text.RegularExpressions;
	using CrawlerSuite.Core.Host;

	/// <summary>
	/// Turns configuration failures into a calm what / where / why / fix explanation the
	/// app can show. The "where" of a JSON error comes from the exception's structured
	/// path and line, never from scraping its message.
	/// </summary>
	internal static class ConfigErrors
	{
		/// <summary>"$.A.B.C[30]" → "A → B → C, entry #31".</summary>
		internal static string FriendlyPath(string? jsonPath)
		{
			if (string.IsNullOrWhiteSpace(jsonPath))
			{
				return "(location not reported)";
			}

			string p = jsonPath.Trim();
			if (p.StartsWith("$.", StringComparison.Ordinal)) p = p[2..];
			else if (p.StartsWith('$')) p = p[1..];

			if (p.Length == 0)
			{
				return "(top level)";
			}

			p = Regex.Replace(p, @"\[(\d+)\]", m =>
				long.TryParse(m.Groups[1].Value, out long i) ? $", entry #{i + 1}" : m.Value);
			return p.Replace(".", " → ");
		}

		/// <summary>System.Text.Json line numbers are zero-based; editors count from 1.</summary>
		internal static string HumanLine(long? zeroBasedLineNumber) =>
			zeroBasedLineNumber.HasValue ? (zeroBasedLineNumber.Value + 1).ToString() : "(not reported)";

		internal static IReadOnlyList<CheckBlock> JsonError(string configFilePath, JsonException ex)
		{
			string file = Path.GetFileName(configFilePath);
			return
			[
				new("Problem",
				[
					new(CheckTone.Prose, "The configuration file has a formatting error, so the run can't start until it's fixed."),
					new(CheckTone.Data, file, "File"),
					new(CheckTone.Data, FriendlyPath(ex.Path), "Setting"),
					new(CheckTone.Data, HumanLine(ex.LineNumber), "Line"),
				]),
				new("Why",
				[
					new(CheckTone.Prose,
						"The file isn't valid JSON at this point. Common causes are a missing comma between entries, "
						+ "a missing or extra quote or bracket, or a line break inside a value. The line is where parsing "
						+ "stopped — the actual slip is sometimes just above it."),
				]),
				new("Fix",
				[
					new(CheckTone.Accent, $"Open {file} and check that line and the lines just above it, focusing on the entry named above."),
					new(CheckTone.Data, "Full technical detail is in application.log."),
				]),
			];
		}

		internal static IReadOnlyList<CheckBlock> NotFound(string path) =>
		[
			new("Problem", [new(CheckTone.Prose, "No configuration file was found next to the program."), new(CheckTone.Data, path, "Expected")]),
			new("Fix", [new(CheckTone.Accent, "Open Settings (the gear in the header), unlock them and add a site; saving creates crawlersuite.config.json next to CrawlerSuite.exe. It is the only configuration file the app reads.")]),
		];

		internal static IReadOnlyList<CheckBlock> FromLines(IEnumerable<string> lines) =>
		[
			new("Details", lines
				.Select(l => l.TrimEnd('\r'))
				.Where(l => l.Trim().Length > 0)
				.Select(l => new CheckLine(CheckTone.Prose, l.Trim()))
				.ToList()),
		];
	}
}
