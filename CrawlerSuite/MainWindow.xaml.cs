using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Display;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Host;
using CrawlerSuite.Dialogs;
using CrawlerSuite.Run;
using CrawlerSuite.Views;
using HueThemeKit;

namespace CrawlerSuite;

public partial class MainWindow : Window
{
	private readonly RunPageModel runModel = new();
	private readonly ResultsModel resultsModel = new();
	private readonly RunView runView;
	private readonly ResultsView resultsView;
	private readonly TriageModel triageModel = new();
	private readonly TriageView triageView;
	private readonly OutputFilesView formatsView;
	private readonly DictionariesModel dictionariesModel = new();
	private readonly DictionariesView dictionariesView;
	private readonly SpellFindingsModel spellModel = new();
	private readonly SpellFindingsView spellView;
	private readonly SettingsModel settingsModel = new();
	private readonly SettingsView settingsView;
	private readonly ComparisonModel comparisonModel = new();
	private readonly ComparisonView comparisonView;
	private bool closeConfirmed;
	private string statusRaw = "Ready";
	private bool resultsStale = true;
	private bool triageStale = true;

	/// <summary>A word from Spell findings' "Find in Triage": searched for once Triage shows the snapshot.</summary>
	private string? pendingTriageWord;

	// The page sources of the snapshot the Results page shows (null: no content list); triage reuses them.
	private Func<string, PageSource?>? pageSources;
	private string pageSourcesSnapshot = string.Empty;

	public MainWindow()
	{
		InitializeComponent();
		HueTitleBar.Attach(this);

		runView = new RunView { DataContext = runModel };
		resultsView = new ResultsView { DataContext = resultsModel };
		triageView = new TriageView { DataContext = triageModel };
		triageModel.TicketLanguageOf = () => runModel.Config?.TicketLanguage; // read when a ticket document is made: the setting as it stands
		formatsView = new OutputFilesView();
		dictionariesView = new DictionariesView { DataContext = dictionariesModel };
		spellView = new SpellFindingsView { DataContext = spellModel };
		settingsView = new SettingsView { DataContext = settingsModel };
		comparisonView = new ComparisonView { DataContext = comparisonModel };
		comparisonModel.SettingsRequested += (_, _) =>
		{
			NavSettings.IsChecked = true;
			settingsModel.SelectedTab = settingsModel.Tabs.FirstOrDefault(t => t.Name == "Spelling") ?? settingsModel.SelectedTab;
		};
		settingsView.Saved += async (_, _) =>
		{
			SetStatus("Settings saved", "Brush.Ok");
			triageStale = true; // what triage counts may have changed
			await LoadConfigurationAsync();
		};
		triageView.SettingsRequested += (_, _) =>
		{
			NavSettings.IsChecked = true;
			settingsModel.SelectedTab = settingsModel.Tabs.FirstOrDefault(t => t.Name == CrawlerSuite.Core.Settings.SettingsCatalog.TriageTab) ?? settingsModel.SelectedTab;
		};
		triageView.SweepRequested += async (_, _) => await RunTicketSweepAsync();
		spellView.FindInTriageRequested += (_, word) =>
		{
			pendingTriageWord = word; // applied once Triage has read the snapshot
			NavTriage.IsChecked = true;
		};
		dictionariesView.FindRequested += async (_, _) => await StartRunAsync(quick: true, findUnneeded: true);

		runView.StartRequested += async (_, _) => await StartRunAsync();
		runView.QuickRunRequested += async (_, _) => await StartRunAsync(quick: true);
		runView.ReloadRequested += async (_, _) => await LoadConfigurationAsync();
		runView.ResultsRequested += (_, _) => NavResults.IsChecked = true;
		runView.OutputFilesRequested += (_, _) => NavFormats.IsChecked = true;
		runView.FindingsRequested += (_, target) =>
		{
			NavResults.IsChecked = true;
			resultsModel.Open(target);
		};

		// The site scope sits in the side menu; Results follows it.
		ScopePanel.DataContext = runModel;
		ClocksPanel.DataContext = runModel.State.Clocks;
		runModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(RunPageModel.SelectedSite))
			{
				OnScopeChanged();
			}
		};

		ShowVersion();
		Mask.Instance.PropertyChanged += (_, _) =>
		{
			StatusText.Text = Mask.Apply(statusRaw);
			settingsModel.MaskActive = Mask.Instance.IsActive;
		};
		PageHost.Content = runView;
		Loaded += async (_, _) => await LoadConfigurationAsync();
		Closing += OnClosing;
	}

	// ── Header ───────────────────────────────────────────────────────────────

	private void ShowVersion()
	{
		var assembly = Assembly.GetExecutingAssembly();
		var version = assembly.GetName().Version;
		VersionText.Text = version is null ? "version 2.0.0" : $"version {version.Major}.{version.Minor}.{version.Build}";
		var sdk = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => a.Key == "BuildSdkVersion")?.Value;
		SdkText.Text = string.IsNullOrWhiteSpace(sdk) ? string.Empty : $"built with SDK {sdk}";
		RuntimeText.Text = $"runtime {Environment.Version}";
	}

	// ── Navigation ───────────────────────────────────────────────────────────

	private void OnNavChecked(object sender, RoutedEventArgs e)
	{
		if (PageHost is null || runView is null)
		{
			return; // fired during InitializeComponent
		}

		PageHost.Content = sender == NavResults ? resultsView
			: sender == NavDictionaries ? dictionariesView
			: sender == NavSpell ? spellView
			: sender == NavTriage ? triageView
			: sender == NavFormats ? formatsView
			: sender == NavSettings ? settingsView
			: sender == NavComparison ? comparisonView
			: runView;

		if (sender == NavResults && resultsStale)
		{
			_ = ShowScopedResultsAsync();
		}

		if (sender == NavTriage)
		{
			_ = ShowTriageAsync();
		}

		if (sender == NavDictionaries)
		{
			_ = ShowDictionariesAsync();
		}

		if (sender == NavSpell)
		{
			_ = ShowSpellFindingsAsync();
		}

		if (sender == NavSettings)
		{
			ShowSettings();
		}

		if (sender == NavComparison)
		{
			ShowComparison();
		}
	}

	/// <summary>The Comparison page lists the scoped site's snapshots afresh, and takes the attribute rules as the settings stand.</summary>
	private void ShowComparison()
	{
		var config = runModel.Config;
		string? siteFolder = null;
		if (config is not null && runModel.SelectedSite is { } site)
		{
			try
			{
				siteFolder = SnapshotLocator.SiteFolder(config, site.Url);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				siteFolder = null;
			}
		}

		comparisonModel.ShowSite(config, siteFolder);
	}

	/// <summary>The Settings page reads the file afresh each time it opens, unless it holds changes not saved yet.</summary>
	private void ShowSettings()
	{
		settingsModel.IsRunning = runModel.State.IsRunning;
		if (!settingsModel.IsDirty)
		{
			settingsModel.Load(AppPaths.ConfigFile);
		}
	}

	// ── Site scope ───────────────────────────────────────────────────────────

	/// <summary>The scope changed: Results follows now if it is on screen, else when it is next opened.</summary>
	/// <summary>Whether the scoped site has a snapshot "Re-run latest" can replay.</summary>
	private void RefreshLatestSnapshot()
	{
		var config = runModel.Config;
		var site = runModel.SelectedSite;
		try
		{
			runModel.HasLatestSnapshot = config is not null && site is not null
				&& SnapshotLocator.Latest(SnapshotLocator.SiteFolder(config, site.Url)) is not null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			runModel.HasLatestSnapshot = false;
		}
	}

	private void OnScopeChanged()
	{
		RefreshLatestSnapshot();
		resultsStale = true;
		triageStale = true;
		if (PageHost.Content == resultsView)
		{
			_ = ShowScopedResultsAsync();
		}
		else if (PageHost.Content == triageView)
		{
			_ = ShowTriageAsync();
		}
		else if (PageHost.Content == comparisonView)
		{
			ShowComparison();
		}
		else if (PageHost.Content == dictionariesView)
		{
			_ = ShowDictionariesAsync();
		}
		else if (PageHost.Content == spellView)
		{
			_ = ShowSpellFindingsAsync();
		}
	}

	/// <summary>
	/// The user dictionary of the loaded configuration and the dictionary of the site a snapshot
	/// belongs to: the snapshot sits in its site's folder, whose name is the site dictionary's name.
	/// </summary>
	private static (string User, string Site) DictionaryPathsFor(Config config, string snapshot)
	{
		var userPath = AppPaths.Dictionary(config.CustomDictionaryFile);
		var siteFolder = Path.GetFileName(Path.GetDirectoryName(snapshot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty);
		var sitePath = siteFolder.Length > 0 ? AppPaths.Dictionary(siteFolder + ".dic") : string.Empty;
		return (userPath, sitePath);
	}

	/// <summary>
	/// The Spell findings page: the unique misspellings of the snapshot the Results page shows,
	/// read again on every visit (the dictionaries may have changed in between).
	/// </summary>
	private async Task ShowSpellFindingsAsync()
	{
		if (runModel.State.IsRunning)
		{
			spellModel.ShowNone("Spell findings open once the crawl has finished.");
			return;
		}

		if (resultsStale)
		{
			await ShowScopedResultsAsync();
		}

		if (!resultsModel.HasSnapshot || runModel.Config is not { } config)
		{
			spellModel.ShowNone("No snapshot to look at — crawl the site, or choose a snapshot on the Results page.");
			return;
		}

		var snapshot = resultsModel.SnapshotPath;
		var (userPath, sitePath) = DictionaryPathsFor(config, snapshot);
		try
		{
			var data = await Task.Run(() => SpellFindingsModel.Read(snapshot, userPath, sitePath));
			spellModel.Show(snapshot, resultsModel.Heading, data, userPath, sitePath);
			triageStale = true; // a word decided here leaves triage: read it again on the next visit
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.LogWarning($"Spell findings: could not read {snapshot} — {ex.Message}");
			spellModel.ShowNone($"The snapshot or a dictionary could not be read: {ex.Message}");
		}
	}

	/// <summary>
	/// The Dictionaries page works on the snapshot the Results page shows, straight from its logs
	/// 15 and 35 — no run needed to open it. Read again on every visit, since the dictionary files
	/// may have changed in between. The button re-runs the scoped site's latest snapshot, so it is
	/// offered only when that is the snapshot on screen.
	/// </summary>
	private async Task ShowDictionariesAsync()
	{
		if (runModel.State.IsRunning)
		{
			dictionariesModel.ShowNone("The Dictionaries page opens once the crawl has finished.");
			return;
		}

		if (resultsStale)
		{
			await ShowScopedResultsAsync();
		}

		if (!resultsModel.HasSnapshot || runModel.Config is not { } config)
		{
			dictionariesModel.ShowNone("No snapshot to look at — crawl the site, or choose a snapshot on the Results page.");
			return;
		}

		var snapshot = resultsModel.SnapshotPath;
		var (userPath, sitePath) = DictionaryPathsFor(config, snapshot);
		try
		{
			var data = await Task.Run(() => DictionariesModel.Read(snapshot, userPath, sitePath));
			dictionariesModel.UserPath = userPath;
			dictionariesModel.Show(snapshot, resultsModel.Heading, data);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.LogWarning($"Dictionaries: could not read {snapshot} — {ex.Message}");
			dictionariesModel.ShowNone($"The snapshot or a dictionary could not be read: {ex.Message}");
			return;
		}

		var maint = config.DictionaryMaintenance;
		string? latest = null;
		if (runModel.SelectedSite is { } site)
		{
			try
			{
				latest = SnapshotLocator.Latest(SnapshotLocator.SiteFolder(config, site.Url));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				latest = null;
			}
		}

		if (!maint.UpdateUserDictionary && !maint.UpdateSiteSpecificDictionary)
		{
			dictionariesModel.SetFindState(false, "Off: neither dictionary is selected for maintenance in the configuration.");
		}
		else if (latest is null || !string.Equals(Path.GetFullPath(latest), Path.GetFullPath(snapshot), StringComparison.OrdinalIgnoreCase))
		{
			dictionariesModel.SetFindState(false, "Works on the site's latest snapshot — the Results page shows another one.");
		}
		else
		{
			dictionariesModel.SetFindState(true, "Re-runs the latest snapshot (no download) with a second spell check without your dictionaries.");
		}
	}

	/// <summary>
	/// Debug mode's ticket sweep: every triage page as a ticket PDF into the snapshot's
	/// <c>debug\…\ticket-sweeps</c> folder, checked, with sweep.md to read. Verdicts are made on a
	/// copy in memory; the ledger is not touched.
	/// </summary>
	private async Task RunTicketSweepAsync()
	{
		if (Busy.IsBusy || !triageModel.CanSweep || runModel.Config is not { DebugMode: true } config)
		{
			return;
		}

		var snapshot = triageModel.SnapshotPath;
		var snapshotName = Path.GetFileName(Path.TrimEndingDirectorySeparator(snapshot));
		var version = Views.CopyForAi.AppVersion();
		var language = CrawlerSuite.Core.Text.DocumentText.Parse(config.TicketLanguage);
		var sweeps = CrawlerSuite.Core.Debugging.TicketSweep.Folder(
			CrawlerSuite.Core.Debugging.RunArchive.SnapshotFolder(CrawlerSuite.Core.Debugging.RunArchive.Root, snapshot));
		var folder = Path.Combine(sweeps, CrawlerSuite.Core.Debugging.TicketSweep.NextName(sweeps, version));
		var pages = triageModel.SweepPages();
		using var cancel = new CancellationTokenSource();
		try
		{
			var outcome = await Busy.RunAsync("Ticket sweep", report =>
				Views.TicketSweepRunner.RunAsync(pages, folder, snapshotName, version, language, Dispatcher, report, cancel.Token), cancel);
			Logger.LogInfo($"Ticket sweep: {outcome.Pdfs} PDF(s) of {outcome.Tickets} ticket(s), {outcome.Flagged} flagged, in {outcome.Folder}");
			settingsModel.RefreshDebugFolderSize();
			HueMessage.Show(this, outcome.Cancelled ? "Ticket sweep stopped" : "Ticket sweep written",
				Mask.Apply($"{outcome.Pdfs} PDF(s) of {outcome.Tickets} ticket(s), {outcome.Shapes} shape(s); {outcome.Flagged} with a check finding or an error." +
					Environment.NewLine + Environment.NewLine + outcome.Folder + Environment.NewLine + Environment.NewLine +
					"sweep.md lists the checks, one ticket per shape to read first, then every ticket" +
					(outcome.Compared ? "; sweep-compare.md what changed against the sweep before." : ".") +
					" The triage ledger was not touched."));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.LogWarning($"Ticket sweep: stopped — {ex.Message}");
			HueMessage.Show(this, "The ticket sweep could not be written", Mask.Apply(ex.Message));
		}
	}

	/// <summary>
	/// Triage works on the snapshot the Results page shows (the scoped site's latest, or one chosen
	/// there). It is read again when that snapshot changed; the switches start from their defaults.
	/// </summary>
	private async Task ShowTriageAsync()
	{
		var word = pendingTriageWord;
		pendingTriageWord = null;
		if (runModel.State.IsRunning)
		{
			triageModel.ShowNone("Triage opens once the crawl has finished.");
			return;
		}

		if (resultsStale)
		{
			await ShowScopedResultsAsync();
		}

		if (!resultsModel.HasSnapshot)
		{
			triageModel.ShowNone("No snapshot to triage — crawl the site, or choose a snapshot on the Results page.");
			return;
		}

		var snapshot = resultsModel.SnapshotPath;
		if (triageModel.HasSnapshot && string.Equals(triageModel.SnapshotPath, snapshot, StringComparison.OrdinalIgnoreCase) && !triageStale)
		{
			if (word is not null)
			{
				triageView.FindWord(word);
			}

			return;
		}

		if (Busy.IsBusy)
		{
			return;
		}

		try
		{
			var known = string.Equals(pageSourcesSnapshot, snapshot, StringComparison.OrdinalIgnoreCase) ? pageSources : null;
			// Spelling findings a dictionary holds by now (added on Spell findings or by hand) are not triaged.
			var (userPath, sitePath) = runModel.Config is { } config ? DictionaryPathsFor(config, snapshot) : (string.Empty, string.Empty);
			// Proper names are kept in the configuration: not while a run holds it, nor over settings not saved yet.
			Func<string?> blocked = () =>
				runModel.State.IsRunning ? "A run is going: proper names can be kept when it has finished."
				: settingsModel.IsDirty ? "Settings has changes not saved yet: save or discard them first, then keep the name."
				: null;
			var (read, sources, names) = await Busy.RunAsync("Reading the findings for triage", report =>
			{
				report.Say("Matching the ledger against this snapshot");
				var triage = TriageModel.Read(snapshot, CrawlerSuite.Core.Lexicon.DictionaryCoverage.For(snapshot, userPath, sitePath),
					runModel.Config?.Seo.TitleTemplates);
				return (triage, known ?? PageSourcesFor(snapshot), new CrawlerSuite.Core.Quality.ProperNameKeeper(AppPaths.ConfigFile, blocked));
			});
			triageModel.Show(snapshot, resultsModel.Heading, read, sources, names, runModel.Config?.TriageModes,
				new TriageDictionary(snapshot, userPath, sitePath));
			triageModel.DebugMode = runModel.Config?.DebugMode == true;
			triageStale = false;
			if (word is not null)
			{
				triageView.FindWord(word);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			Logger.LogWarning($"Triage: could not read {snapshot} — {ex.Message}");
			triageModel.ShowNone($"Triage could not read the snapshot or its ledger: {ex.Message}");
		}
	}

	/// <summary>
	/// The page sources for a snapshot: the CMS content list of the configured site the snapshot
	/// belongs to, read with a freshly loaded configuration (resolved for that site here, so the
	/// configuration on screen is not touched). Null when there is none or it cannot be read —
	/// the Results then simply show no Source column. Off the UI thread.
	/// </summary>
	private static Func<string, PageSource?>? PageSourcesFor(string snapshotDirectory)
	{
		try
		{
			var load = CrawlerApp.LoadConfiguration();
			return load.Config is { } config ? PageSources.ForSnapshot(config, snapshotDirectory) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
		{
			Logger.LogWarning($"Page sources: the content list could not be read for {snapshotDirectory} — {ex.Message}");
			return null;
		}
	}

	/// <summary>Shows the scoped site's latest snapshot on the Results page.</summary>
	private async Task ShowScopedResultsAsync()
	{
		var config = runModel.Config;
		var site = runModel.SelectedSite;
		if (config is null || site is null || runModel.State.IsRunning || Busy.IsBusy)
		{
			return;
		}

		resultsStale = false;
		var label = $"{site.Name} — {site.Url}";
		string? latest;
		try
		{
			latest = SnapshotLocator.Latest(SnapshotLocator.SiteFolder(config, site.Url));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			Logger.LogWarning($"Results: could not look for snapshots of {site.Name} — {ex.Message}");
			resultsModel.ShowNone(label);
			return;
		}

		if (latest is null)
		{
			resultsModel.ShowNone(label);
			return;
		}

		await LoadResultsAsync(latest, label);
	}

	/// <summary>Where "Choose snapshot…" starts: the scoped site's folder, else the configured download folder.</summary>
	internal string? ScopeFolder()
	{
		if (runModel.Config is not { } config)
		{
			return null;
		}

		if (runModel.SelectedSite is { } site && SnapshotLocator.SiteFolder(config, site.Url) is var folder && Directory.Exists(folder))
		{
			return folder;
		}

		var root = SnapshotLocator.BaseFolder(config);
		return Directory.Exists(root) ? root : null;
	}

	// ── Configuration ────────────────────────────────────────────────────────

	/// <summary>Loads the configuration and runs the start-up checks (off the UI thread).</summary>
	private async Task LoadConfigurationAsync()
	{
		if (runModel.State.IsRunning || Busy.IsBusy)
		{
			return;
		}

		var collector = new CollectingHost();
		ConfigLoadResult load;
		bool checksOk;
		try
		{
			(load, checksOk) = await Busy.RunAsync("Loading configuration", report =>
			{
				report.Say("Reading " + Path.GetFileName(AppPaths.ConfigFile));
				var result = CrawlerApp.LoadConfiguration();
				if (!result.Ok)
				{
					return (result, false);
				}

				report.Say("Checking working folder, dictionaries and settings");
				return (result, CrawlerApp.RunStartupChecks(result.Config!, collector));
			});
		}
		catch (Exception ex)
		{
			Logger.LogError($"Loading the configuration failed: {ex}");
			runModel.ShowProblem("The configuration could not be loaded",
				[new ConfigCheckItem("Unexpected error", [new CheckBlock("Details",
					[new CheckLine(CheckTone.Prose, ex.Message), new CheckLine(CheckTone.Prose, "Details are in application.log next to the program.")])])],
				AppPaths.ConfigFile);
			SetStatus("Configuration needs attention", "Brush.Error");
			return;
		}

		Mask.Instance.Learn(load.Config, load.ConfigPath);
		QualityContextCell.Settings = load.Config?.ContentQuality;
		CrawlerSuite.Core.Quality.SecurityPatterns.Learn(load.Config?.ContentUnwantedPatterns);

		if (!load.Ok)
		{
			runModel.ShowProblem(load.ErrorHeading ?? "The configuration could not be loaded",
				[RunPageModel.FromLoad(load)], load.ConfigPath);
			SetStatus("Configuration needs attention", "Brush.Error");
			return;
		}

		if (!checksOk)
		{
			List<ConfigCheckItem> problems = collector.Checks.Count > 0
				? collector.Checks
				: [new ConfigCheckItem("Start-up check", [new CheckBlock("Details",
					collector.Notices.Select(n => new CheckLine(CheckTone.Prose, n)).ToList())])];
			runModel.ShowProblem("A start-up check failed", problems, load.ConfigPath);
			SetStatus("Configuration needs attention", "Brush.Error");
			return;
		}

		resultsStale = true;
		runModel.ShowConfig(load.Config!, load.ConfigPath);
		SetStatus("Ready", "Brush.Ok");
	}

	// ── Run ──────────────────────────────────────────────────────────────────

	// ── Header clocks ────────────────────────────────────────────────────────

	private const double HeaderGap = 24;
	private const double TitleMinWidth = 160;

	private void OnHeaderLayoutChanged(object sender, SizeChangedEventArgs e) => PlaceClocks();

	private void OnClocksVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
		Dispatcher.BeginInvoke(PlaceClocks, System.Windows.Threading.DispatcherPriority.Loaded);

	/// <summary>
	/// Centres the run clocks in the header. Where the centre would run into the title or the
	/// screen-mask buttons they move aside; the title's description wraps to make room. In a
	/// window too narrow for both, the clocks step out of the header until it is wider again.
	/// </summary>
	private void PlaceClocks()
	{
		if (ClocksPanel.Visibility != Visibility.Visible)
		{
			TitlePanel.MaxWidth = double.PositiveInfinity;
			return;
		}

		double width = HeaderGrid.ActualWidth;
		double clocks = ClocksPanel.ActualWidth;
		double titleLeft = TitlePanel.TranslatePoint(new Point(0, 0), HeaderGrid).X;
		double maskLeft = MaskPanel.TranslatePoint(new Point(0, 0), HeaderGrid).X;
		if (width <= 0 || clocks <= 0)
		{
			return;
		}

		double minLeft = titleLeft + TitleMinWidth + HeaderGap;
		double maxLeft = maskLeft - HeaderGap - clocks;
		if (maxLeft < minLeft)
		{
			// No room between the title and the mask buttons: the clocks wait for a wider window.
			ClocksPanel.Opacity = 0;
			ClocksPanel.IsHitTestVisible = false;
			TitlePanel.MaxWidth = double.PositiveInfinity;
			return;
		}

		ClocksPanel.Opacity = 1;
		ClocksPanel.IsHitTestVisible = true;
		double left = Math.Clamp((width - clocks) / 2, minLeft, maxLeft);
		ClocksPanel.Margin = new Thickness(left, 0, 0, 0);
		TitlePanel.MaxWidth = Math.Max(TitleMinWidth, left - HeaderGap - titleLeft);
	}

	/// <param name="quick">"Re-run latest": a replay of the latest snapshot without any question (and no network, so no proxy sign-in).</param>
	/// <param name="findUnneeded">From the Dictionaries page: the re-run also looks for the entries no longer needed (log 35), then the page opens again.</param>
	private async Task StartRunAsync(bool quick = false, bool findUnneeded = false)
	{
		var config = runModel.Config;
		var site = runModel.SelectedSite;
		if (config is null || site is null || runModel.State.IsRunning || Busy.IsBusy)
		{
			return;
		}

		// The crawl reads the saved file; changes still on the Settings page do not take part.
		if (settingsModel.IsDirty && !HueConfirm.Ask(this, "Settings are not saved",
			"The Settings page has changes that are not saved. The crawl uses the settings as they were last saved; the changes stay on the page, locked until you unlock the settings again.",
			"Start anyway"))
		{
			NavSettings.IsChecked = true;
			return;
		}

		SpellFindingsModel.ForgetTriage(); // a run gives a fresh list: Triage decisions last until here

		var state = runModel.State;
		state.Reset();
		state.Clocks.Start();
		var clockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		clockTimer.Tick += (_, _) => state.Clocks.Refresh();
		clockTimer.Start();
		state.IsRunning = true;
		settingsModel.IsRunning = true; // nothing can change while it runs; the settings stay locked afterwards
		runModel.LastOutcome = string.Empty;
		runModel.HasResults = false;
		runModel.ArchiveFolder = null;
		runModel.RefreshCanStart();
		NavRun.IsChecked = true;
		SetStatus(quick ? $"Re-running {site.Name}" : $"Crawling {site.Name}", "Brush.Accent");

		IRunHost host = quick ? new QuickRunHost(new WpfRunHost(this, state)) : new WpfRunHost(this, state);
		var outcome = RunOutcome.Failed;
		try
		{
			outcome = await Task.Run(() => CrawlerApp.RunAsync(site.Name, host, quickReplay: quick, findUnneededEntries: findUnneeded));
		}
		catch (Exception ex)
		{
			Logger.LogError($"Crawl failed: {ex}");
			outcome = RunOutcome.Failed;
		}
		finally
		{
			state.IsRunning = false;
			settingsModel.IsRunning = false;
			state.FinishPhases(outcome == RunOutcome.Completed);
			clockTimer.Stop();
			RefreshLatestSnapshot();
			runModel.RefreshCanStart();
		}

		switch (outcome)
		{
			case RunOutcome.Completed when CrawlerApp.LastRun is { } last:
				runModel.LastOutcome = $"Crawl complete — {last.SiteLabel}" + (last.WasReplay ? " (replay of an existing snapshot)" : string.Empty);
				runModel.HasResults = true;
				SetStatus("Crawl complete", "Brush.Ok");
				var tabs = await LoadResultsAsync(last.SnapshotDirectory, last.SiteLabel, quiet: true);
				resultsStale = false;
				triageStale = true;
				if (tabs is not null)
				{
					// The overview's chips take the Results page's own counts and open its tabs.
					state.ApplyFindingCounts(tabs);
					state.CanOpenFindings = true;
				}

				if (last.ArchiveFolder is { } archive)
				{
					// Debug mode: the run's overview and steps join its archive, as their copies for AI read.
					WriteArchiveTexts(archive, state);
					runModel.ArchiveFolder = archive;
				}

				settingsModel.RefreshDebugFolderSize();

				if (findUnneeded)
				{
					NavDictionaries.IsChecked = true; // read again: the page shows the new log 35
				}
				break;
			case RunOutcome.Aborted:
				runModel.LastOutcome = "The crawl was cancelled.";
				SetStatus("Crawl cancelled", "Brush.Stale");
				break;
			case RunOutcome.Halted:
				runModel.LastOutcome = "The crawl stopped at a check that needs attention — see the messages below.";
				SetStatus("Crawl stopped", "Brush.Warn");
				break;
			default:
				runModel.LastOutcome = "The crawl stopped because of an error — see the messages below and application.log.";
				SetStatus("Crawl failed", "Brush.Error");
				break;
		}
	}

	/// <summary>Writes the run overview and steps (masked, as copied for AI) into the run's debug archive.</summary>
	private static void WriteArchiveTexts(string archive, RunState state)
	{
		try
		{
			var version = CopyForAi.AppVersion();
			File.WriteAllText(Path.Combine(archive, CrawlerSuite.Core.Debugging.RunArchive.RunOverviewFile), state.OverviewForAi(version));
			File.WriteAllText(Path.Combine(archive, CrawlerSuite.Core.Debugging.RunArchive.RunStepsFile), state.StepsForAi(version));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.LogWarning($"Run archive: overview and steps not written — {ex.Message}");
		}
	}

	/// <summary>Reads a snapshot's output files under the overlay and shows them on the Results page.</summary>
	/// <returns>The findings as read (for the run overview's counts); null when nothing was read.</returns>
	internal async Task<IReadOnlyList<FindingNode>?> LoadResultsAsync(string snapshotDirectory, string siteLabel, bool quiet = false)
	{
		if (Busy.IsBusy)
		{
			return null;
		}

		try
		{
			var (files, nodes, findings, sources) = await Busy.RunAsync("Reading the snapshot", report =>
			{
				report.Say("Counting the rows of every output file");
				var written = ResultsModel.ReadFiles(snapshotDirectory);
				report.Say("Reading the findings");
				var read = FindingsCatalog.Read(snapshotDirectory);
				var found = PageSourcesFor(snapshotDirectory);
				return (written, read, ResultsModel.ReadFindings(read, found), found);
			});
			pageSources = sources;
			pageSourcesSnapshot = snapshotDirectory;
			resultsModel.Show(snapshotDirectory, siteLabel, files, findings);
			return nodes;
		}
		catch (Exception ex)
		{
			Logger.LogWarning($"Results: could not read {snapshotDirectory} — {ex.Message}");
			if (!quiet)
			{
				HueMessage.Show(this, "The folder could not be read", Mask.Apply(ex.Message));
			}

			return null;
		}
	}

	// ── Screen mask ──────────────────────────────────────────────────────────

	private void OnMaskChecked(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<MaskLevel>(tag, out var level))
		{
			Mask.Instance.Level = level;
		}
	}

	// ── Status bar and footer actions ───────────────────────────────────────

	private void SetStatus(string text, string brushKey)
	{
		statusRaw = text;
		StatusText.Text = Mask.Apply(text);
		StatusDot.SetResourceReference(Control.ForegroundProperty, brushKey);
	}

	private void OnOpenProgramFolder(object sender, RoutedEventArgs e) => Shell.Open(AppPaths.BaseDirectory);

	private void OnOpenLog(object sender, RoutedEventArgs e)
	{
		if (Logger.LogFilePath is { } path && File.Exists(path))
		{
			Shell.Open(path);
		}
	}

	// ── Closing ──────────────────────────────────────────────────────────────

	private void OnClosing(object? sender, CancelEventArgs e)
	{
		if (closeConfirmed)
		{
			return;
		}

		if (!runModel.State.IsRunning)
		{
			if (settingsModel.IsDirty && !HueConfirm.Ask(this, "Settings are not saved",
				"The Settings page has changes that are not saved. Closing drops them.", "Close", destructive: true))
			{
				e.Cancel = true;
				NavSettings.IsChecked = true;
			}

			return;
		}

		var close = HueConfirm.Ask(this, "A crawl is in progress",
			"Closing stops the crawl. A crawl that is stopped leaves an incomplete snapshot, which you'll be offered to delete on the next start.",
			"Close", destructive: true);
		if (!close)
		{
			e.Cancel = true;
			return;
		}

		closeConfirmed = true;
		Logger.LogWarning("Window closed during a crawl; the crawl was abandoned and its snapshot is incomplete.");
	}

	/// <summary>Collects what the start-up checks report; they never ask questions.</summary>
	private sealed class CollectingHost : IRunHost
	{
		public List<ConfigCheckItem> Checks { get; } = [];

		public List<string> Notices { get; } = [];

		public void Phase(string title) { }

		public void Step(StepRow row) { }

		public void WorkTotals(StepWork totals) { }

		public void Message(MessageTone tone, string text)
		{
			if (tone is MessageTone.Error or MessageTone.Warning or MessageTone.ActionRequired)
			{
				lock (Notices)
				{
					Notices.Add(text);
				}
			}
		}

		public void Notice(Notice notice)
		{
			lock (Notices)
			{
				Notices.Add(notice.Heading);
				Notices.AddRange(notice.Lines);
			}
		}

		public void ConfigCheck(string subtitle, IReadOnlyList<CheckBlock> blocks)
		{
			lock (Checks)
			{
				Checks.Add(new ConfigCheckItem(subtitle, blocks));
			}
		}

		public void Progress(CrawlProgress progress) { }

		public SnapshotChoice AskSnapshotChoice(SnapshotPrompt prompt) => throw Unexpected();
		public IncompleteSnapshotChoice AskIncompleteSnapshot(IncompleteSnapshotPrompt prompt) => throw Unexpected();
		public bool AskCleanSweep(string snapshotPath) => throw Unexpected();
		public bool AskEmptyDownloadStartFresh(string downloadDirectory) => throw Unexpected();
		public bool AskProceedWithStaleContentList(CmsContentListFreshness freshness) => throw Unexpected();
		public IReadOnlyCollection<string> AskOrphansToRename(IReadOnlyList<string> orphans, string backupExtension) => throw Unexpected();
		public bool AskReSettle(ReSettlePrompt prompt) => throw Unexpected();
		public bool AskRetryLockedFile(string displayName, string error, bool isDelete) => false;
		public bool AskRetryLockedCsv(string path, string error) => false;
		public (string User, string Password)? AskProxySignIn(string proxyUrl, bool typedSignInRefused) => throw Unexpected();
		public bool AskContinueAfterConnectivityFailure(IReadOnlyList<string> details, bool proxyTip) => throw Unexpected();
		public CrawlLimitChoice AskCrawlLimitExceeded(CrawlLimitReport report) => throw Unexpected();

		private static InvalidOperationException Unexpected() =>
			new("The start-up checks asked a question; they are not meant to.");
	}
}
