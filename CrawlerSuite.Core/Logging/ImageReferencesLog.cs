namespace CrawlerSuite.Core.Logging
{
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using CrawlerSuite.Core.Links;

	/// <summary>
	/// Writes log 38: who uses each image asset quality flagged for triage — one row per reference
	/// (page or stylesheet), with how it is referenced, as written, and for a stylesheet reference
	/// when a browser requests it. Routed through <see cref="IssueLogWriter"/> for consistent field
	/// sanitisation.
	/// </summary>
	public static class ImageReferencesLog
	{
		public static readonly string?[] Header = ["Image", "Kind", "Referrer", "AsWritten", "WhenRequested", "Evidence"];

		[ExcludeFromCodeCoverage(Justification =
			"I/O writer delegating to IssueLogWriter: one record per reference; the dual-locale CSV " +
			"emission, RFC-4180 quoting and BOM live in IssueLogWriter.WriteCsvPair and are tested there.")]
		public static void Write(IEnumerable<BrokenReference> rows, string csvBasePath)
		{
			var records = new List<string?[]> { Header };
			foreach (var r in rows)
			{
				records.Add([r.Broken, r.Kind, r.Referrer, r.AsWritten, r.Grade, r.Evidence]);
			}

			IssueLogWriter.WriteCsvPair(csvBasePath, records);
		}
	}
}
