using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Formats;

namespace CrawlerSuite.Run;

/// <summary>One output file in the results table.</summary>
public sealed record ResultRow(
	string Number,
	string Title,
	string FileName,
	string Group,
	bool Present,
	string PresentText,
	string Rows,
	string Size,
	string Note,
	string Path);

/// <summary>The Results page: what a snapshot contains, per topic and file.</summary>
public sealed class ResultsModel : Observable
{
	private string snapshotPath = string.Empty;
	private string heading = "No snapshot loaded";
	private string subheading = "Run a crawl, or choose a snapshot folder.";
	private bool hasSnapshot;

	public ObservableCollection<ResultRow> Rows { get; } = [];

	public ObservableCollection<string> Warnings { get; } = [];

	/// <summary>The findings tabs (Content quality … Crawl), then the file list.</summary>
	public ObservableCollection<FindingTabVm> Tabs { get; } = [];

	public string SnapshotPath { get => snapshotPath; private set => Set(ref snapshotPath, value); }

	public string Heading { get => heading; private set => Set(ref heading, value); }

	public string Subheading { get => subheading; private set => Set(ref subheading, value); }

	/// <summary>The root the snapshot's addresses share ("https://site.test"): addresses on it are shown from their path.</summary>
	public string SiteRoot { get => siteRoot; private set => Set(ref siteRoot, value); }

	private string siteRoot = string.Empty;

	public bool HasSnapshot { get => hasSnapshot; private set => Set(ref hasSnapshot, value); }

	/// <summary>Reads a snapshot's output files. Touches the disk; call it off the UI thread.</summary>
	public static IReadOnlyList<OutputFileStatus> ReadFiles(string snapshotDirectory) => SnapshotSummary.Read(snapshotDirectory);

	/// <summary>Reads every findings view of a snapshot. Touches the disk; call it off the UI thread.</summary>
	public static IReadOnlyList<FindingTabVm> ReadFindings(string snapshotDirectory) =>
		ReadFindings(FindingsCatalog.Read(snapshotDirectory));

	/// <summary>
	/// The tabs for findings already read; <paramref name="sources"/> gives each page its source
	/// from the content list (null: none). Safe off the UI thread.
	/// </summary>
	public static IReadOnlyList<FindingTabVm> ReadFindings(IReadOnlyList<FindingNode> nodes, Func<string, PageSource?>? sources = null) =>
		nodes.Select(n => FindingTabVm.From(n, sources)).ToList();

	private FindingTabVm? selectedTab;

	/// <summary>The top tab on screen.</summary>
	public FindingTabVm? SelectedTab { get => selectedTab; set { if (Set(ref selectedTab, value)) { RefreshTabPath(); } } }

	/// <summary>
	/// The tab rows are shown (they start shown: a tab is picked first, then folded out of the way).
	/// Folded, one line names the tab on screen; the choice holds while the app runs.
	/// </summary>
	public bool TabsOpen { get => tabsOpen; set { if (Set(ref tabsOpen, value)) { RefreshTabPath(); } } }

	private bool tabsOpen = true;

	/// <summary>The line beside the chevron: where you are while folded, what it does while open.</summary>
	public string TabToggleText => TabsOpen ? "Fold the tab rows" : tabPath;

	private string tabPath = string.Empty;

	/// <summary>The path through the tab rows to the tab on screen, with the counts ("Content quality 1,234 › Quotes 56").</summary>
	internal void RefreshTabPath()
	{
		var parts = new List<string>();
		for (var tab = SelectedTab; tab is not null; tab = (tab as FindingBranchVm)?.SelectedChild)
		{
			parts.Add($"{tab.Title} {tab.CountText}");
		}

		tabPath = string.Join(" › ", parts);
		Raise(nameof(TabToggleText));
	}

	/// <summary>Brings a findings tab to the front (a findings chip on the Run page was clicked).</summary>
	public void Open(FindingsTarget target)
	{
		var top = Tabs.FirstOrDefault(t => t.Title == target.Tab);
		if (top is null)
		{
			return;
		}

		SelectedTab = top;
		if (target.SubTab is not null && top is FindingBranchVm branch
			&& branch.Children.FirstOrDefault(c => c.Title == target.SubTab) is { } child)
		{
			branch.SelectedChild = child;
		}

		RefreshTabPath();
	}

	/// <summary>
	/// Every Results tab with its count as plain text for sharing with an AI: one line per tab,
	/// the path through the tab rows ("Content quality › Characters › Control characters") and
	/// the number on it. The notices above the tabs come first. Masked at the strictest level,
	/// whatever the screen shows.
	/// </summary>
	public string CountsForAi(string version)
	{
		var nl = System.Environment.NewLine;
		var sb = new System.Text.StringBuilder();
		sb.Append($"CrawlerSuite {version} · results · {System.DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}").Append(nl);
		sb.Append("Snapshot: ").Append(Cell(System.IO.Path.GetFileName(SnapshotPath.TrimEnd('\\', '/')))).Append(nl);
		foreach (var warning in Warnings)
		{
			sb.Append("Notice: ").Append(Cell(warning)).Append(nl);
		}

		sb.Append(nl).Append("| Tab | Count |").Append(nl).Append("|---|---:|").Append(nl);
		void Walk(FindingTabVm tab, string path)
		{
			var here = path.Length == 0 ? tab.Title : path + " › " + tab.Title;
			var count = tab.IsWritten ? tab.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "– (not written)";
			sb.Append($"| {Cell(here)} | {count} |").Append(nl);
			if (tab is FindingBranchVm branch)
			{
				foreach (var child in branch.Children)
				{
					Walk(child, here);
				}
			}
		}

		foreach (var tab in Tabs)
		{
			Walk(tab, string.Empty);
		}

		AppendSourcesForAi(sb, nl);
		return sb.ToString();
	}

	/// <summary>
	/// Rows per page source for every table that shows sources. The content list's values and
	/// labels are replaced by neutral names, so the copy never carries the CMS's words.
	/// </summary>
	private void AppendSourcesForAi(System.Text.StringBuilder sb, string nl)
	{
		var leaves = new List<(string Path, FindingLeafVm Leaf)>();
		void Collect(FindingTabVm tab, string path)
		{
			var here = path.Length == 0 ? tab.Title : path + " › " + tab.Title;
			if (tab is FindingLeafVm { HasSources: true } leaf)
			{
				leaves.Add((here, leaf));
			}
			else if (tab is FindingBranchVm branch)
			{
				foreach (var child in branch.Children)
				{
					Collect(child, here);
				}
			}
		}

		foreach (var tab in Tabs)
		{
			Collect(tab, string.Empty);
		}

		if (leaves.Count == 0)
		{
			return;
		}

		var names = new NeutralSourceNames(leaves.SelectMany(l => l.Leaf.Rows.Select(r => r.Source)));
		sb.Append(nl).Append("Page source (from the CMS content list; values and labels replaced by neutral names):").Append(nl).Append(nl);
		sb.Append("| Tab | Source | Rows |").Append(nl).Append("|---|---|---:|").Append(nl);
		foreach (var (path, leaf) in leaves)
		{
			foreach (var (name, rows) in names.Count(leaf.Rows.Select(r => r.Source)))
			{
				sb.Append($"| {Cell(path)} | {name} | {rows.ToString(System.Globalization.CultureInfo.InvariantCulture)} |").Append(nl);
			}
		}
	}

	private static string Cell(string text) =>
		Mask.ApplyStrict(text).Replace("|", "/", System.StringComparison.Ordinal)
			.Replace("\r", " ", System.StringComparison.Ordinal).Replace("\n", " ", System.StringComparison.Ordinal);

	/// <summary>The scoped site has no snapshot yet.</summary>
	public void ShowNone(string siteLabel)
	{
		Tabs.Clear();
		Rows.Clear();
		Warnings.Clear();
		SnapshotPath = string.Empty;
		Heading = siteLabel;
		Subheading = "No snapshot of this site yet — crawl it, or choose a snapshot folder.";
		SiteRoot = string.Empty;
		HasSnapshot = false;
	}

	/// <summary>Shows what <see cref="ReadFiles"/> and <see cref="ReadFindings"/> returned. UI thread only.</summary>
	public void Show(string snapshotDirectory, string siteLabel, IReadOnlyList<OutputFileStatus> files, IReadOnlyList<FindingTabVm> findings)
	{
		Rows.Clear();
		foreach (var f in files)
		{
			Rows.Add(new ResultRow(
				f.Format.Number,
				f.Format.Title,
				f.Format.PrimaryFileName,
				f.Format.Group,
				f.Exists,
				f.Exists ? "Written" : "Not written",
				f.Rows is { } r ? Format.Count(r) : (f.Exists ? "—" : string.Empty),
				f.Exists ? Format.Bytes(f.Bytes) : string.Empty,
				f.Explanation,
				f.Path));
		}

		Warnings.Clear();
		var present = files.Where(f => f.Exists).ToList();
		var oversized = present.FirstOrDefault(f => f.Format.Number == "34");
		if (oversized?.Rows is > 0)
		{
			Warnings.Add($"{Format.Count(oversized.Rows ?? 0)} file(s) over the single-file limit were not stored — see 34-oversized-files.log.");
		}

		// No notice for 404s: Links › Broken links counts them and the run chip is red. These notices
		// say the snapshot itself is incomplete.
		if (!present.Any(f => f.Format.Number == "01"))
		{
			Warnings.Add("This snapshot has no 01-crawler.log — its crawl did not finish, so the analysis is incomplete.");
		}

		Tabs.Clear();
		foreach (var tab in findings)
		{
			Tabs.Add(tab);
		}

		Tabs.Add(new FilesTabVm(Rows, present.Count(f => !f.Format.IsFolder)));

		SnapshotPath = snapshotDirectory;
		SiteRoot = Core.Urls.SiteRoot.FromSnapshot(snapshotDirectory);
		Heading = Views.UrlDisplay.HeadingWithRoot(string.IsNullOrEmpty(siteLabel) ? System.IO.Path.GetFileName(snapshotDirectory) : siteLabel, SiteRoot);
		Subheading = $"Snapshot {System.IO.Path.GetFileName(snapshotDirectory)} · {present.Count} of {files.Count} outputs written";
		RefreshTabPath();
		HasSnapshot = true;
	}
}
