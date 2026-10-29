using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Settings;

namespace CrawlerSuite.Run;

/// <summary>
/// The settings screen: crawlersuite.config.json in tabs. Locked at every start; unlocked for
/// the session after the warning about what the app lets in, and locked again when a crawl
/// starts. Each setting is either an override (in the file, editable) or the built-in default
/// (locked, greyed; shown only with "Show defaults"). Changes are collected and written on Save,
/// after the start-up validation passed.
/// </summary>
public sealed class SettingsModel : Observable
{
	/// <summary>Restated defaults answered this session (path → use default); asked once each.</summary>
	private static readonly Dictionary<string, bool> Answered = new(StringComparer.Ordinal);

	private SettingsDocument document = SettingsDocument.From(new JsonObject());
	private string path = string.Empty;
	private bool isUnlocked;
	private bool isRunning;
	private bool showDefaults;
	private string notice = string.Empty;
	private string status = string.Empty;
	private SettingsTabVm? selectedTab;
	private bool maskActive;

	public ObservableCollection<SettingsTabVm> Tabs { get; } = [];

	/// <summary>The test levers of the dictionary checks: this session only, never saved.</summary>
	public CheckLeversVm Levers { get; } = new();

	/// <summary>The levers can change whenever no run is going (they are not part of the configuration).</summary>
	public bool CanChangeLevers => !IsRunning;

	/// <summary>Debug mode as the file now says (unsaved changes included); the levers show only then.</summary>
	public bool DebugModeOn => document.Get("DebugMode") is JsonValue v && v.TryGetValue<bool>(out var on) && on;

	private string debugFolderSize = string.Empty;

	/// <summary>"The debug folder next to the program holds 12.3 MB." — or that there is none yet.</summary>
	public string DebugFolderSize { get => debugFolderSize; private set => Set(ref debugFolderSize, value); }

	/// <summary>Measures the debug folder again (after a run archived into it).</summary>
	public void RefreshDebugFolderSize()
	{
		try
		{
			var root = CrawlerSuite.Core.Debugging.RunArchive.Root;
			DebugFolderSize = Directory.Exists(root)
				? $"The debug folder next to the program holds {CrawlerSuite.Core.Debugging.RunArchive.SizeOf(root) / 1048576.0:0.0} MB. The app never deletes anything in it; clear it yourself when you no longer need the runs."
				: "There is no debug folder yet: the first run in Debug mode creates it next to the program.";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			DebugFolderSize = $"The debug folder could not be measured: {ex.Message}";
		}
	}

	public ObservableCollection<SiteRowVm> Sites { get; } = [];

	/// <summary>Problems with the sites as they stand; Save refuses while there are any.</summary>
	public ObservableCollection<string> SiteProblems { get; } = [];

	public SettingsTabVm? SelectedTab
	{
		get => selectedTab;
		set
		{
			if (Set(ref selectedTab, value))
			{
				Raise(nameof(IsSitesTab));
			}
		}
	}

	public bool IsSitesTab => SelectedTab?.Name == SettingsCatalog.SitesTab;

	private string siteQuery = string.Empty;

	/// <summary>Each tab's search text and folded sections, kept while the app runs (a reload keeps them).</summary>
	private readonly Dictionary<string, TabState> tabStates = new(StringComparer.Ordinal);

	/// <summary>The Sites tab's search: shows the sites whose name or address contains the text.</summary>
	private void FilterSites(string text)
	{
		siteQuery = text.Trim();
		foreach (var site in Sites)
		{
			site.IsSearchMatch = siteQuery.Length == 0 || site.MatchesSearch(siteQuery);
		}

		Raise(nameof(NoSiteMatch));
	}

	/// <summary>"No site contains “…”." while the Sites search hides every site.</summary>
	public string NoSiteMatch => siteQuery.Length > 0 && Sites.Count > 0 && Sites.All(s => !s.IsSearchMatch)
		? $"No site contains \u201C{siteQuery}\u201D."
		: string.Empty;

	public string FilePath { get => path; private set => Set(ref path, value); }

	/// <summary>Unlocked for this session after the warning.</summary>
	public bool IsUnlocked
	{
		get => isUnlocked;
		private set
		{
			if (Set(ref isUnlocked, value))
			{
				RaiseEditable();
			}
		}
	}

	/// <summary>A crawl is running: nothing can change.</summary>
	public bool IsRunning
	{
		get => isRunning;
		set
		{
			if (Set(ref isRunning, value))
			{
				if (value)
				{
					IsUnlocked = false; // a run locks the settings; they stay locked afterwards
				}

				RaiseEditable();
				Raise(nameof(CanChangeLevers));
			}
		}
	}

	public bool CanEdit => IsUnlocked && !IsRunning;

	public bool IsLocked => !IsUnlocked;

	/// <summary>Defaults are listed too (greyed), so they can be found and unlocked.</summary>
	public bool ShowDefaults
	{
		get => showDefaults;
		set
		{
			if (Set(ref showDefaults, value))
			{
				foreach (var tab in Tabs)
				{
					tab.Refilter(value);
				}
			}
		}
	}

	/// <summary>A problem reading the file, or the note that it does not exist yet.</summary>
	public string Notice { get => notice; private set => Set(ref notice, value); }

	/// <summary>The line next to Save: unsaved changes, or what keeps it from saving.</summary>
	public string Status { get => status; private set => Set(ref status, value); }

	public bool IsDirty => document.IsDirty;

	/// <summary>The processors of this computer, read when the page loads.</summary>
	public ProcessorReport Processors { get; private set; } = new(Environment.ProcessorCount, 0, 0, 0, 0);

	/// <summary>The screen mask is on: drive letters and site names stay hidden on the cards.</summary>
	public bool MaskActive
	{
		get => maskActive;
		set
		{
			if (Set(ref maskActive, value))
			{
				RefreshExtras();
			}
		}
	}

	/// <summary>Asks before switching off a site's subdomains removes its entries (set by the view).</summary>
	public Func<SiteRowVm, bool>? ConfirmClearSubdomains { get; set; }

	/// <summary>The languages of the dictionary bundles as they stand on the form.</summary>
	public IReadOnlyList<string> BundleLanguages { get; private set; } = [];

	/// <summary>How much entries were needed in the primary site's latest analysed snapshot; null when there is none.</summary>
	public UsageReport? Usage { get; private set; }

	/// <summary>The sites as they stand on the form, saved or not.</summary>
	internal IReadOnlyList<SiteDraft> SiteDrafts => Sites.Select(s => s.Draft).ToList();

	public bool CanSave => CanEdit && document.IsDirty;

	/// <summary>Reads the file afresh; changes not saved are dropped.</summary>
	public void Load(string configPath)
	{
		FilePath = configPath;
		try
		{
			document = SettingsDocument.Load(configPath);
			Notice = document.Existed
				? string.Empty
				: $"{Path.GetFileName(configPath)} does not exist yet. Everything shows its default; saving creates the file.";
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			document = SettingsDocument.From(new JsonObject());
			Notice = $"{Path.GetFileName(configPath)} could not be read: {ex.Message} Saving here would replace it — fix the file first.";
		}

		Processors = MachineInfo.Processors();
		LoadSites();
		BundleLanguages = SettingEditors.BundleLanguages(document.Get("DictionaryBundles"));
		Usage = ReadUsage();
		var previous = SelectedTab?.Name;
		Tabs.Clear();
		foreach (var name in SettingsCatalog.Tabs)
		{
			var rows = SettingsCatalog.All.Where(s => s.Tab == name).Select(s => new SettingRowVm(s, this)).ToList();
			if (!tabStates.TryGetValue(name, out var tabState))
			{
				tabState = new TabState();
				tabStates[name] = tabState;
			}

			Tabs.Add(new SettingsTabVm(name, rows, showDefaults, tabState, name == SettingsCatalog.SitesTab ? FilterSites : null));
			if (name == SettingsCatalog.SitesTab)
			{
				FilterSites(tabState.Search);
			}
		}

		SelectedTab = Tabs.FirstOrDefault(t => t.Name == previous) ?? Tabs.FirstOrDefault();
		RefreshDebugFolderSize();
		Changed();
	}

	/// <summary>Unlocks for this session; the view asks for the confirmation first.</summary>
	public void Unlock() => IsUnlocked = true;

	public void Lock() => IsUnlocked = false;

	// ── Rows ─────────────────────────────────────────────────────────────────

	internal JsonNode? ValueOf(string settingPath) => document.Get(settingPath);

	/// <summary>A whole-number setting as the app will use it: the file's value, else the default.</summary>
	internal long IntOf(string settingPath) =>
		(document.Get(settingPath) ?? SettingsCatalog.DefaultOf(settingPath)) is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;

	internal bool Has(string settingPath) => document.Has(settingPath);

	/// <summary>A switch setting as the app will use it: the file's value, else the default.</summary>
	internal bool SwitchOn(string settingPath) =>
		(document.Get(settingPath) ?? SettingsCatalog.DefaultOf(settingPath)) is JsonValue v && v.TryGetValue<bool>(out var on) && on;

	internal void SetValue(string settingPath, JsonNode? value)
	{
		document.Set(settingPath, value);
		Changed();
		Depends(settingPath);
	}

	internal void RemoveValue(string settingPath)
	{
		document.Remove(settingPath);
		Changed();
		Depends(settingPath);
	}

	/// <summary>The free space is rated against the download limit: a new limit re-rates it.</summary>
	private void Depends(string settingPath)
	{
		foreach (var row in Tabs.SelectMany(t => t.AllRows).Where(r => r.Info.ActiveWhen == settingPath))
		{
			row.RaiseInactive();
		}

		if (settingPath == "MaxCrawlDownloadGB")
		{
			RowOf("BaseDirectory")?.RefreshExtras();
		}

		if (settingPath == "DictionaryBundles")
		{
			BundleLanguages = SettingEditors.BundleLanguages(document.Get(settingPath));
			foreach (var row in Tabs.SelectMany(t => t.AllRows))
			{
				row.RefreshLanguages();
			}
		}
	}

	/// <summary>
	/// The usage numbers of the primary site's latest snapshot (the download folder and site as
	/// the file has them). One site, so there is no question which snapshot is meant.
	/// </summary>
	private UsageReport? ReadUsage()
	{
		try
		{
			var primary = Sites.FirstOrDefault(s => s.IsPrimary) ?? Sites.FirstOrDefault();
			var baseDirectory = document.Get("BaseDirectory") is JsonValue v && v.TryGetValue<string>(out var b) ? b : string.Empty;
			if (primary is null || baseDirectory.Trim().Length == 0 || primary.Url.Length == 0)
			{
				return null;
			}

			var siteFolder = Path.Combine(CrawlerSuite.Core.AppPaths.Resolve(baseDirectory), CrawlerSuite.Core.Findings.SnapshotLocator.FolderName(primary.Url));
			return CrawlerSuite.Core.Findings.SnapshotLocator.Latest(siteFolder) is { } latest ? SettingsUsage.Read(latest) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	private void RefreshExtras()
	{
		foreach (var row in Tabs.SelectMany(t => t.AllRows))
		{
			row.RefreshExtras();
		}
	}

	// ── Sites ────────────────────────────────────────────────────────────────

	private void LoadSites()
	{
		Sites.Clear();
		List<SiteDraft> drafts;
		try
		{
			drafts = document.ReadSites();
		}
		catch (JsonException)
		{
			drafts = [];
		}

		foreach (var draft in drafts)
		{
			Sites.Add(new SiteRowVm(draft, this));
		}

		CheckSites();
	}

	public SiteRowVm AddSite()
	{
		// A new site uses the content list until switched off: tenant and group are then required, so nothing is forgotten.
		var row = new SiteRowVm(new SiteDraft { Name = $"Site {Sites.Count + 1}", IsPrimary = Sites.Count == 0, UsesContentList = true }, this) { IsNew = true };
		Sites.Add(row);
		SitesChanged();
		return row;
	}

	public void RemoveSite(SiteRowVm site)
	{
		Sites.Remove(site);
		SitesChanged();
	}

	/// <summary>Makes one site the primary; the others lose the mark.</summary>
	internal void MakePrimary(SiteRowVm primary)
	{
		foreach (var site in Sites)
		{
			site.SetPrimarySilently(site == primary);
		}

		SitesChanged();
	}

	internal void SitesChanged()
	{
		document.WriteSites(Sites.Select(s => s.Draft));
		CheckSites();
		Changed();
		foreach (var row in Tabs.SelectMany(t => t.AllRows).Where(r => r.HasPlaceholders))
		{
			row.RefreshExtras(); // what a placeholder becomes follows the sites as they are typed
		}
	}

	private void CheckSites()
	{
		SiteProblems.Clear();
		foreach (var problem in SiteRules.Check(Sites.Select(s => s.Draft).ToList()))
		{
			SiteProblems.Add(problem);
		}

		foreach (var site in Sites.Where(s => s.MissingSubdomain))
		{
			SiteProblems.Add($"{(site.Name.Length > 0 ? site.Name : "A site")}: add an allowed subdomain, or switch \"has allowed subdomains\" off.");
		}

		Raise(nameof(HasSiteProblems));
	}

	public bool HasSiteProblems => SiteProblems.Count > 0;

	/// <summary>Sites read from the file whose address was changed: their history stays under the old address.</summary>
	public IReadOnlyList<(string Old, string New)> ChangedSiteUrls() =>
		Sites.Where(s => !s.IsNew && s.OriginalUrl.Length > 0 && !string.Equals(s.OriginalUrl, s.Draft.Url, StringComparison.OrdinalIgnoreCase))
			.Select(s => (s.OriginalUrl, s.Draft.Url))
			.ToList();

	// ── Save ─────────────────────────────────────────────────────────────────

	/// <summary>Settings that restate their default and have not been answered this session.</summary>
	public IReadOnlyList<SettingInfo> RestatedDefaultsToAsk() =>
		document.RestatedDefaults().Where(s => !Answered.ContainsKey(s.Path)).ToList();

	/// <summary>Records the answer for a restated default; "use default" removes it from the file.</summary>
	public void Answer(SettingInfo setting, bool useDefault)
	{
		Answered[setting.Path] = useDefault;
		if (useDefault)
		{
			RemoveValue(setting.Path);
			RowOf(setting.Path)?.Refresh();
		}
	}

	/// <summary>Keys in the file the app has no setting for; they go on save.</summary>
	public IReadOnlyList<string> UnknownKeys() => document.UnknownKeys();

	/// <summary>Null when everything can be saved; else what stands in the way.</summary>
	public string? CheckBeforeSave()
	{
		var fieldErrors = Tabs.SelectMany(t => t.AllRows).Where(r => r.Error.Length > 0 || r.HasTableProblems).ToList();
		if (fieldErrors.Count > 0)
		{
			return "Fix the marked fields first: " + string.Join(", ", fieldErrors.Select(r => $"{r.Label} ({r.Tab?.Name})")) + ".";
		}

		if (HasSiteProblems)
		{
			return "Fix the sites first: " + SiteProblems[0];
		}

		return document.Validate();
	}

	/// <summary>Writes the file. Throws IOException / UnauthorizedAccessException when it cannot be written.</summary>
	public void Save()
	{
		document.Save(FilePath);
		foreach (var site in Sites)
		{
			site.MarkSaved();
		}

		Changed();
	}

	private SettingRowVm? RowOf(string settingPath) =>
		Tabs.SelectMany(t => t.AllRows).FirstOrDefault(r => r.Info.Path == settingPath);

	private void Changed()
	{
		Raise(nameof(DebugModeOn));
		Raise(nameof(IsDirty));
		Raise(nameof(CanSave));
		Status = document.IsDirty ? "Unsaved changes." : string.Empty;
	}

	private void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		Raise(nameof(IsLocked));
		Raise(nameof(CanSave));
		foreach (var row in Tabs.SelectMany(t => t.AllRows))
		{
			row.RaiseEditable();
		}

		foreach (var site in Sites)
		{
			site.RaiseEditable();
		}
	}
}

/// <summary>One tab: its rows by section, showing overrides only unless defaults are shown.</summary>
public sealed class SettingsTabVm : Observable
{
	private readonly IReadOnlyList<SettingRowVm> rows;
	private bool hasVisible;

	public SettingsTabVm(string name, IReadOnlyList<SettingRowVm> rows, bool showDefaults, TabState? state = null, Action<string>? searchSites = null)
	{
		Name = name;
		this.rows = rows;
		this.searchSites = searchSites;
		this.state = state ?? new TabState();
		foreach (var row in rows)
		{
			row.Tab = this;
		}

		Refilter(showDefaults);
	}

	public string Name { get; }

	/// <summary>The test levers panel sits at the bottom of the Spelling tab.</summary>
	public bool ShowsLevers => Name == SettingsCatalog.LeversTab;

	/// <summary>A fixed rule stated at the top of the tab, or empty.</summary>
	public string Note => SettingsCatalog.TabNote(Name);

	/// <summary>Where the tab was scrolled to, so coming back finds it there (kept while the app runs).</summary>
	public double ScrollOffset { get; set; }

	public IReadOnlyList<SettingRowVm> AllRows => rows;

	/// <summary>The rows shown, in order; the first row of a section carries its heading.</summary>
	public ObservableCollection<SettingRowVm> Shown { get; } = [];

	public bool HasVisible { get => hasVisible; private set => Set(ref hasVisible, value); }

	/// <summary>What the tab says when it shows nothing.</summary>
	public string EmptyText => Name == SettingsCatalog.SitesTab ? string.Empty
		: SearchActive ? $"No setting on this tab contains \u201C{searchText.Trim()}\u201D."
		: "Nothing changed here — all defaults. Show defaults to see and unlock them.";

	internal bool ShowDefaults { get; private set; }

	// ── Search and folding: per tab, independent of the other tabs, kept while the app runs ──

	private readonly Action<string>? searchSites;
	private readonly TabState state;

	private HashSet<string> folded => state.Folded;

	private string searchText => state.Search;

	/// <summary>The tab's own search text: shows only the settings whose name, key, help or value contains it.</summary>
	public string SearchText
	{
		get => state.Search;
		set
		{
			value ??= string.Empty;
			if (value != state.Search)
			{
				state.Search = value;
				Raise();
				Raise(nameof(SearchActive));
				searchSites?.Invoke(searchText);
				Refilter(ShowDefaults);
			}
		}
	}

	public bool SearchActive => searchText.Trim().Length > 0;

	/// <summary>The tab has sections to fold (the Sites tab has none).</summary>
	public bool HasSections => rows.Any(r => r.Info.Section.Length > 0);

	/// <summary>Folds or unfolds one section; while searching, every section with a match stays open.</summary>
	public void ToggleSection(string section)
	{
		if (!folded.Remove(section))
		{
			folded.Add(section);
		}

		Refilter(ShowDefaults);
	}

	public void FoldAll()
	{
		folded.UnionWith(rows.Select(r => r.Info.Section).Where(s => s.Length > 0));
		Refilter(ShowDefaults);
	}

	public void UnfoldAll()
	{
		folded.Clear();
		Refilter(ShowDefaults);
	}

	public void Refilter(bool showDefaults)
	{
		ShowDefaults = showDefaults;
		Shown.Clear();
		var query = searchText.Trim();
		bool searching = query.Length > 0;

		// While searching, every match shows — also a default the filter would hide.
		// The triage switches are always shown: Triage links here, and they read as switches even at their defaults.
		var visible = rows.Where(r => searching ? r.Matches(query)
			: showDefaults || r.IsSet || r.KeepShown || r.Info.Editor == SettingEditor.TriageModes).ToList();
		foreach (var row in rows)
		{
			row.SetQuery(query);
		}

		string? section = null;
		bool sectionFolded = false;
		foreach (var row in visible)
		{
			bool first = row.Info.Section != section;
			section = row.Info.Section;
			if (first)
			{
				sectionFolded = !searching && section.Length > 0 && folded.Contains(section);
				row.SectionHeading = section;
				row.SectionCount = visible.Count(r => r.Info.Section == section);
				row.IsFolded = sectionFolded;
				Shown.Add(row);
				continue;
			}

			row.SectionHeading = string.Empty;
			row.IsFolded = false;
			if (!sectionFolded)
			{
				Shown.Add(row);
			}
		}

		HasVisible = Shown.Count > 0;
		Raise(nameof(EmptyText));
	}
}

/// <summary>A tab's search text and folded sections: memory only, never saved.</summary>
public sealed class TabState
{
	public string Search { get; set; } = string.Empty;

	public HashSet<string> Folded { get; } = new(StringComparer.Ordinal);
}
