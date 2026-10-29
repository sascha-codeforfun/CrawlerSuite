namespace CrawlerSuite.Core.Logging
{
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using CrawlerSuite.Core.Links;

	/// <summary>
	/// Writes log 07: where each 404 comes from — one row per reference (page, stylesheet,
	/// redirect or content list), with how it is referenced, as written, and for stylesheet
	/// references when a browser requests it. Routed through <see cref="IssueLogWriter"/> for
	/// consistent field sanitisation.
	/// </summary>
	public static class ErrorSourcesLog
	{
		public static readonly string?[] Header = ["BrokenUrl", "Kind", "Referrer", "AsWritten", "Grade", "Evidence", "Reach"];

		[ExcludeFromCodeCoverage(Justification =
			"I/O writer delegating to IssueLogWriter: one record per reference; the dual-locale CSV " +
			"emission, RFC-4180 quoting and BOM live in IssueLogWriter.WriteCsvPair and are tested there.")]
		public static void Write(IEnumerable<BrokenReference> rows, string errorSourcesCsvBasePath)
		{
			var records = new List<string?[]> { Header };
			foreach (var r in rows)
			{
				records.Add([r.Broken, r.Kind, r.Referrer, r.AsWritten, r.Grade, r.Evidence, r.Reach]);
			}

			IssueLogWriter.WriteCsvPair(errorSourcesCsvBasePath, records);
		}
	}
}
