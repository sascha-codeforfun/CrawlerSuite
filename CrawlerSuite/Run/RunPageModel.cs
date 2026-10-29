using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Host;

namespace CrawlerSuite.Run;

/// <summary>A site in the site picker.</summary>
public sealed record SiteItem(string Name, string Url, bool IsPrimary)
{
	public string Label => IsPrimary ? $"{Name}  (primary)" : Name;

	/// <summary>What the site picker's type-to-search and screen readers get: masked like the screen.</summary>
	public override string ToString() => Mask.Display(this);
}

/// <summary>One line of the configuration summary: "Download folder — D:\Crawls".</summary>
/// <summary>One line of the configuration summary; a warning is shown in amber.</summary>
public sealed record SummaryRow(string Label, string Value, bool IsWarning = false);

/// <summary>The Run page: configuration state, site choice, and the live run.</summary>
public sealed class RunPageModel : Observable
{
	private SiteItem? selectedSite;
	private bool configOk;
	private string configHeading = string.Empty;
	private string lastOutcome = string.Empty;
	private bool hasResults;

	public RunState State { get; } = new();

	public ObservableCollection<SiteItem> Sites { get; } = [];

	public ObservableCollection<SummaryRow> Summary { get; } = [];

	/// <summary>Why the configuration cannot be used (empty when it can).</summary>
	public ObservableCollection<ConfigCheckItem> ConfigProblems { get; } = [];

	public Config? Config { get; private set; }

	public SiteItem? SelectedSite
	{
		get => selectedSite;
		set
		{
			if (Set(ref selectedSite, value))
			{
				Raise(nameof(CanStart));
				Raise(nameof(CanQuickRun));
			}
		}
	}

	/// <summary>True when the configuration loaded and passed the start-up checks.</summary>
	public bool ConfigOk
	{
		get => configOk;
		private set
		{
			if (Set(ref configOk, value))
			{
				Raise(nameof(CanStart));
				Raise(nameof(CanChangeSite));
				Raise(nameof(CanQuickRun));
			}
		}
	}

	public string ConfigHeading { get => configHeading; private set => Set(ref configHeading, value); }


	public string LastOutcome { get => lastOutcome; set => Set(ref lastOutcome, value); }

	/// <summary>True after a completed run, so the page can offer the results.</summary>
	public bool HasResults { get => hasResults; set => Set(ref hasResults, value); }

	private string? archiveFolder;
	private string archiveNote = string.Empty;

	/// <summary>Debug mode: the last run's archive folder; null before a run, outside debug mode, or when it failed.</summary>
	public string? ArchiveFolder
	{
		get => archiveFolder;
		set
		{
			if (Set(ref archiveFolder, value))
			{
				Raise(nameof(CanMarkBaseline));
				ArchiveNote = value is null ? string.Empty : $"Archived as {Path.GetFileName(value)}.";
			}
		}
	}

	/// <summary>The last run can be marked as the baseline of its snapshot.</summary>
	public bool CanMarkBaseline => archiveFolder is not null;

	/// <summary>What the archive did last ("Archived as &lt;version&gt;-003.", "… is now the baseline …").</summary>
	public string ArchiveNote { get => archiveNote; private set => Set(ref archiveNote, value); }

	/// <summary>Marks the last archived run as its snapshot's baseline (replacing an earlier mark).</summary>
	public void MarkBaseline()
	{
		if (archiveFolder is not { } folder || Path.GetDirectoryName(folder) is not { } snapshotArchive)
		{
			return;
		}

		var name = Path.GetFileName(folder);
		try
		{
			CrawlerSuite.Core.Debugging.RunArchive.MarkBaseline(snapshotArchive, name);
			ArchiveNote = $"{name} is now the baseline of this snapshot: later runs are compared with it.";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			ArchiveNote = $"Could not mark {name} as baseline: {ex.Message}";
		}
	}

	public bool CanStart => ConfigOk && SelectedSite is not null && !State.IsRunning;

	private bool hasLatestSnapshot;

	/// <summary>True when the scoped site has a snapshot to re-run.</summary>
	public bool HasLatestSnapshot
	{
		get => hasLatestSnapshot;
		set
		{
			if (Set(ref hasLatestSnapshot, value))
			{
				Raise(nameof(CanQuickRun));
				Raise(nameof(QuickRunToolTip));
			}
		}
	}

	/// <summary>"Re-run latest": a replay of the latest snapshot without any question.</summary>
	public bool CanQuickRun => CanStart && hasLatestSnapshot;

	public string QuickRunToolTip => hasLatestSnapshot
		? "Replays the latest snapshot without asking: no connectivity check, clean sweep first, no files renamed or promoted. A locked file still asks."
		: "This site has no snapshot yet — crawl it first.";

	/// <summary>The site scope can change while no crawl runs; a running crawl locks it to its site.</summary>
	public bool CanChangeSite => ConfigOk && !State.IsRunning;

	public void RefreshCanStart()
	{
		Raise(nameof(CanStart));
		Raise(nameof(CanChangeSite));
		Raise(nameof(CanQuickRun));
	}

	/// <summary>Shows a configuration that failed to load or failed a start-up check.</summary>
	public void ShowProblem(string heading, IEnumerable<ConfigCheckItem> problems, string configPath)
	{
		Config = null;
		ConfigOk = false;
		ConfigHeading = heading;
		Sites.Clear();
		Summary.Clear();
		Summary.Add(new SummaryRow("Configuration file", configPath));
		ConfigProblems.Clear();
		foreach (var p in problems)
		{
			ConfigProblems.Add(p);
		}

		SelectedSite = null;
	}

	/// <summary>Shows a configuration that loaded and passed the start-up checks.</summary>
	public void ShowConfig(Config config, string configPath)
	{
		Config = config;
		ConfigProblems.Clear();
		ConfigHeading = string.Empty;

		var previous = SelectedSite?.Name;
		Sites.Clear();
		foreach (var s in config.Sites)
		{
			Sites.Add(new SiteItem(s.Name, s.Url, s.IsPrimary));
		}

		SelectedSite = Sites.FirstOrDefault(s => s.Name == previous)
			?? Sites.FirstOrDefault(s => s.IsPrimary)
			?? Sites.FirstOrDefault();

		Summary.Clear();
		Summary.Add(new SummaryRow("Configuration file", configPath));
		Summary.Add(new SummaryRow("Download folder", string.IsNullOrWhiteSpace(config.BaseDirectory) ? "(not set)" : config.BaseDirectory));
		Summary.Add(new SummaryRow("Proxy", config.UseProxy
			? (string.IsNullOrWhiteSpace(config.ProxyUrl) ? "on" : $"on — {config.ProxyUrl}")
			: "off (direct connection)"));
		Summary.Add(new SummaryRow("Dictionaries", config.DictionaryBundles.Count == 0
			? "none configured — spell-check finds nothing"
			: string.Join(" · ", config.DictionaryBundles.Select(b =>
				string.IsNullOrWhiteSpace(b.DisplayName) ? b.LanguageCode : $"{b.DisplayName} ({b.LanguageCode})"))));
		// Codes not written as language tags, a language with several flavours and no (or two) Primary.
		var problems = CrawlerSuite.Core.Lexicon.DictionaryFlavours.Problems(config.DictionaryBundles);
		if (problems.Count > 0)
		{
			Summary.Add(new SummaryRow("Dictionary languages", string.Join(Environment.NewLine, problems), IsWarning: true));
		}

		// A language named in "Languages by page" without a bundle: the run goes on without it, and says so here first.
		var flavours = CrawlerSuite.Core.Lexicon.DictionaryFlavours.From(config.DictionaryBundles);
		var missing = CrawlerSuite.Core.SpellCheck.PageLanguageResolver.MissingLanguages(
			config.SpellCheckEngine.PageLanguageOverrides,
			code => flavours.Resolve(code) is not null);
		if (missing.Count > 0)
		{
			Summary.Add(new SummaryRow("Languages by page", string.Join(Environment.NewLine, missing), IsWarning: true));
		}

		Summary.Add(new SummaryRow("Download limits",
			$"{Limit(config.MaxSingleFileMB, "MB")} per file · {Limit(config.MaxCrawlDownloadGB, "GB")} per crawl · stall timeout {config.StallTimeoutSeconds} s"));
		Summary.Add(new SummaryRow("Concurrent downloads",
			$"{config.MaxConcurrentPageDownloads} pages · {config.MaxConcurrentAssetDownloads} assets · "
			+ $"{(config.MaxRequestsPerSecond > 0 ? $"at most {config.MaxRequestsPerSecond} requests/s" : "no request ceiling")} · "
			+ $"{config.MaxConnectionsPerServer} connections per server · up to HTTP/{config.HighestHttpVersion}"
			+ (config.MaxRequestsPerSecond > Config.GentleRequestsPerSecond || config.MaxRequestsPerSecond == 0 ? " — faster than gentle" : string.Empty)));
		ConfigOk = true;
	}

	private static string Limit(int value, string unit) => value <= 0 ? "no limit" : $"{value} {unit}";

	/// <summary>Turns a load failure into a problem card.</summary>
	public static ConfigCheckItem FromLoad(ConfigLoadResult load) =>
		new(Path.GetFileName(load.ConfigPath), load.ErrorBlocks);
}
