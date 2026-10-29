using CrawlerSuite.Core.Host;

namespace CrawlerSuite.Core.Findings
{
	/// <summary>Where a run step's findings show on the Results page: a top tab, and a tab inside it (null = the top tab itself).</summary>
	public sealed record FindingsTarget(string Tab, string? SubTab);

	/// <summary>
	/// Which Results tab each run step feeds. The run overview shows the tab's count next to the
	/// step, counted by the Results page's own rule (the same <see cref="FindingNode.Count"/>),
	/// so the number during the run is the number on the tab. Steps without findings are not listed.
	/// </summary>
	public static class StepFindings
	{
		private static readonly Dictionary<string, FindingsTarget> Targets = new(StringComparer.Ordinal)
		{
			[RunSteps.MainCrawl] = new("Crawl", "Failed downloads"),
			[RunSteps.ContentLists] = new("Crawl", "Not directly discoverable"),
			[RunSteps.Redirects] = new("Links", "Redirect chains"),
			[RunSteps.OversizedFiles] = new("Crawl", "Oversized"),
			[RunSteps.BrokenLinks] = new("Links", "Broken links"),
			[RunSteps.CanonicalLinks] = new("SEO", "Canonical issues"),
			[RunSteps.PdfQuality] = new("Assets", "PDF remediation"),
			[RunSteps.Base64Assets] = new("Assets", "Base64 assets"),
			[RunSteps.AssetQuality] = new("Assets", "Images"),
			[RunSteps.ResourceBloatBaseline] = new("Assets", "Above baseline"),
			[RunSteps.SelfLinks] = new("Links", "Self-links"),
			[RunSteps.ContentQuality] = new("Content quality", null),
			[RunSteps.SpellCheck] = new("Spelling", null),
			[RunSteps.BulkPageScriptScan] = new("Scripts", "Inline scripts"),
			[RunSteps.JsFileScan] = new("Scripts", "JS files"),
			[RunSteps.DictionaryMaintenance] = new("Spelling", "Dictionary orphans"),
		};

		/// <summary>The Results tab a step feeds, or null when the step has no findings of its own.</summary>
		public static FindingsTarget? TargetOf(string stepLabel) => Targets.GetValueOrDefault(stepLabel);

		/// <summary>
		/// Where a step's chip opens on Results: its own tab, or for a step whose chip is a fact
		/// rather than findings, the tab with its data (SEO data → SEO › Metadata). Null: no tab.
		/// </summary>
		public static FindingsTarget? OpenTargetOf(string stepLabel) =>
			TargetOf(stepLabel) ?? (stepLabel == RunSteps.SeoData ? new FindingsTarget("SEO", "Metadata") : null);

		/// <summary>Every step that feeds a Results tab.</summary>
		public static IEnumerable<string> Steps => Targets.Keys;

		/// <summary>The tab's count in a findings tree; null when the tab is missing or its file was not written.</summary>
		public static int? CountIn(IEnumerable<FindingNode> tabs, FindingsTarget target)
		{
			var node = Find(tabs, target);
			return node is { IsWritten: true } ? node.Count : null;
		}

		/// <summary>The tab a target names in a findings tree, or null.</summary>
		public static FindingNode? Find(IEnumerable<FindingNode> tabs, FindingsTarget target)
		{
			var top = tabs.FirstOrDefault(t => t.Title == target.Tab);
			return target.SubTab is null || top is null
				? top
				: top.Children.FirstOrDefault(c => c.Title == target.SubTab);
		}

		/// <summary>Reads the step's tab from the snapshot and counts it; null when the step has none. Never throws.</summary>
		internal static int? Count(string snapshotDirectory, string stepLabel)
		{
			if (TargetOf(stepLabel) is not { } target || string.IsNullOrEmpty(snapshotDirectory) || !Directory.Exists(snapshotDirectory))
			{
				return null;
			}

			try
			{
				return FindingsCatalog.ReadTab(snapshotDirectory, target.Tab) is { } tab ? CountIn([tab], target) : null;
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Step findings: could not count {stepLabel} — {ex.Message}");
				return null;
			}
		}
	}
}
