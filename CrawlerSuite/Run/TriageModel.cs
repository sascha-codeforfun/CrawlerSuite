using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Triage;

namespace CrawlerSuite.Run;

/// <summary>
/// The Triage page: the switches, the list of pages with defects, and the page being decided.
/// The ledger is saved on every verdict; a failed save is reported, never swallowed.
/// UI thread only, except <see cref="Read"/>.
/// </summary>
public sealed class TriageModel : Observable
{
	private IReadOnlyList<TriageItem> items = [];
	private Dictionary<string, TriageMode> modes = TriageSwitches.Defaults();
	private TriageLedger ledger = new();
	private string ledgerPath = string.Empty;
	private string snapshotPath = string.Empty;
	private bool hasSnapshot;
	private bool isReadOnly;
	private string heading = "Triage";
	private string notice = string.Empty;
	private string listSummary = string.Empty;
	private TriagePageVm? page;
	private string filterSummary = string.Empty;
	private string pendingText = string.Empty;
	private readonly Dictionary<string, TriagePageRowVm> rows = new(StringComparer.OrdinalIgnoreCase);
	private Func<string, PageSource?>? sources;
	private bool hasSources;

	/// <summary>Keeps the names offered by hyphen-spacing findings in the user or site dictionary.</summary>
	internal Core.Quality.ProperNameKeeper Names { get; private set; } = Core.Quality.ProperNameKeeper.None;

	/// <summary>User / Site from a spelling item (the same choice as on Spell findings).</summary>
	internal TriageDictionary Dictionaries { get; private set; } = TriageDictionary.None;

	/// <summary>Spelling items set aside because their word went into a dictionary from triage, by word: back on Undo.</summary>
	private readonly Dictionary<string, List<TriageItem>> setAside = new(StringComparer.Ordinal);

	/// <summary>
	/// A spelling item's word goes into the user or site dictionary: every page's item of that word
	/// is set aside (as the next run would not flag it), the list recounts, and the page shows it added.
	/// </summary>
	internal string? AddToDictionary(TriageItem item, bool user, Func<bool> confirmShared)
	{
		var word = SpellingSource.WordOf(item.Detail);
		var problem = Dictionaries.Add(word, user, confirmShared, out bool declined);
		if (problem is not null || declined)
		{
			return problem;
		}

		var ofWord = items.Where(i => TriageSwitches.IsSpelling(i.Type) && SpellingSource.WordOf(i.Detail) == word).ToList();
		setAside[word] = ofWord;
		items = items.Except(ofWord).ToList();
		RefreshPages();
		return null;
	}

	/// <summary>Takes a dictionary choice from triage back: the word's items return to every page.</summary>
	internal string? UndoDictionary(TriageItem item)
	{
		var word = SpellingSource.WordOf(item.Detail);
		var problem = Dictionaries.Undo(word);
		if (problem is not null)
		{
			return problem;
		}

		if (setAside.Remove(word, out var back))
		{
			items = items.Concat(back).ToList();
			RefreshPages();
		}

		return null;
	}

	/// <summary>True when at least one page has a source in the content list: the list then shows the Source column.</summary>
	public bool HasSources { get => hasSources; private set => Set(ref hasSources, value); }

	/// <summary>The page's source from the content list; null when unknown.</summary>
	internal PageSource? SourceOf(string url) => sources?.Invoke(url);

	public ObservableCollection<TriageSwitchFamilyVm> Families { get; } = [];

	/// <summary>The ticket language as the settings name it now ("English", "Deutsch"); read when a document is made.</summary>
	public Func<string?>? TicketLanguageOf { get; set; }

	public ObservableCollection<TriagePageRowVm> Pages { get; } = [];

	/// <summary>Pages with bidi controls: always named on top.</summary>
	public ObservableCollection<TriageBidiVm> BidiPages { get; } = [];

	public bool HasBidi => BidiPages.Count > 0;

	/// <summary>Any page with defects carries a security or bidi badge: only then does the list show its badge column.</summary>
	public bool HasBadges { get => hasBadges; private set => Set(ref hasBadges, value); }

	private bool hasBadges;

	public string SnapshotPath { get => snapshotPath; private set => Set(ref snapshotPath, value); }

	public bool HasSnapshot { get => hasSnapshot; private set => Set(ref hasSnapshot, value); }

	private bool debugMode;

	/// <summary>Debug mode is on and saved: the ticket sweep is offered.</summary>
	public bool DebugMode
	{
		get => debugMode;
		set
		{
			if (Set(ref debugMode, value))
			{
				Raise(nameof(CanSweep));
			}
		}
	}

	/// <summary>The ticket sweep button: Debug mode, a snapshot, and something to sweep.</summary>
	public bool CanSweep => debugMode && hasSnapshot && items.Count > 0;

	// A copy for the ticket sweep: its verdicts live in memory, nothing is written.
	private bool memoryOnly;

	/// <summary>
	/// The ticket sweep's pages: every page triage counts (shared entries too) with every item as
	/// Ticket — decided in a ledger of their own, in memory, on a copy of this model. The real
	/// ledger, its file and its backups are not touched; earlier verdicts and ticket numbers are
	/// not used, so each ticket is the "everything filed" case.
	/// </summary>
	internal IReadOnlyList<TriagePageVm> SweepPages()
	{
		var ledgerOfSweep = new TriageLedger();
		var copy = new TriageModel
		{
			items = items,
			modes = new Dictionary<string, TriageMode>(modes, StringComparer.Ordinal),
			ledger = ledgerOfSweep,
			sources = sources,
			hasSources = hasSources,
			snapshotPath = snapshotPath,
			hasSnapshot = true,
			siteRoot = siteRoot,
			memoryOnly = true,
			TicketLanguageOf = TicketLanguageOf,
		};

		var day = DateTime.Now;
		foreach (var item in TriageBoard.Pages(items, modes, ledgerOfSweep).SelectMany(p => p.All))
		{
			ledgerOfSweep.Decide(item, Verdict.Ticket, string.Empty, day);
		}

		return TriageBoard.Pages(items, modes, ledgerOfSweep)
			.Select(p => new TriagePageVm(copy, p, ledgerOfSweep.PageOf(p.Url), ledgerOfSweep.PreviouslyTicketed(p.Url)))
			.ToList();
	}

	/// <summary>True for a snapshot older than the one the ledger was last matched against: verdicts cannot be changed.</summary>
	public bool IsReadOnly { get => isReadOnly; private set => Set(ref isReadOnly, value); }

	public string Heading { get => heading; private set => Set(ref heading, value); }

	/// <summary>The root the snapshot's addresses share ("https://site.test"): addresses on it are shown from their path.</summary>
	public string SiteRoot { get => siteRoot; private set => Set(ref siteRoot, value); }

	private string siteRoot = string.Empty;

	/// <summary>A problem to show (a ledger that could not be saved, an older snapshot).</summary>
	public string Notice { get => notice; set => Set(ref notice, value); }

	public string ListSummary { get => listSummary; private set => Set(ref listSummary, value); }

	/// <summary>The folded filter's one line: what differs from "everything on".</summary>
	public string FilterSummary { get => filterSummary; private set => Set(ref filterSummary, value); }

	/// <summary>What the list does not show yet (added, removed, re-sorted pages); empty when it is up to date.</summary>
	public string PendingText
	{
		get => pendingText;
		private set
		{
			if (Set(ref pendingText, value))
			{
				Raise(nameof(HasPending));
			}
		}
	}

	public bool HasPending => pendingText.Length > 0;

	private string searchText = string.Empty;

	/// <summary>
	/// Finds pages whose address contains the text, or that have the text as a flagged word (the
	/// whole word), ignoring case. The view hands it over once typing pauses or on Enter, so the
	/// list is not rebuilt on every key.
	/// </summary>
	public string SearchText
	{
		get => searchText;
		set
		{
			if (Set(ref searchText, value ?? string.Empty))
			{
				Raise(nameof(SearchActive));
				UpdateList();
			}
		}
	}

	public bool SearchActive => searchText.Trim().Length > 0;

	/// <summary>
	/// The pages with a flagged word, from Spell findings or from a spelling item: back to the
	/// list (an open page is closed), searched for the word. The lit chips stay as they are.
	/// </summary>
	public void FindWord(string word)
	{
		if (!HasSnapshot)
		{
			return;
		}

		Page = null;
		SearchText = word.Trim();
		UpdateList(); // also when the text was the same: the list is brought up to date
	}

	// Pages per flagged word, worked out once while the items and switches stay as they are.
	private IReadOnlyDictionary<string, int>? wordPages;
	private (IReadOnlyList<TriageItem> Items, string Modes)? wordPagesFor;

	/// <summary>How many pages triage counts have a spelling finding for the word, the chips and the search aside.</summary>
	internal int PagesWithWord(string word)
	{
		var key = (items, string.Join(",", modes.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key}={m.Value}")));
		if (wordPages is null || wordPagesFor is not { } was || !ReferenceEquals(was.Items, key.items) || was.Modes != key.Item2)
		{
			wordPages = PageSearch.WordPages(TriageBoard.Pages(items, modes, ledger));
			wordPagesFor = key;
		}

		return PageSearch.PagesOf(wordPages, word);
	}

	/// <summary>
	/// The source chips above the list: one per page source in the list (value and label as the
	/// content list has them), coloured like the table's chips, with "No source" last. Lit chips
	/// are shown; none on a site without source information.
	/// </summary>
	public ObservableCollection<TriageFilterChipVm> SourceChips { get; } = [];

	/// <summary>The decision chips: Open · Edited · Ticketed · Changed since ticketed. None lit at the start: every page is listed, ticketed ones too.</summary>
	public ObservableCollection<TriageFilterChipVm> DecisionChips { get; } = [];

	private const string NoSource = "";

	private void BuildDecisionChips()
	{
		DecisionChips.Clear();
		foreach (var (decision, label) in new[]
		{
			(PageDecision.Open, "Open"),
			(PageDecision.Edited, "Edited"),
			(PageDecision.Ticketed, "Ticketed"),
			(PageDecision.Changed, "Changed since ticketed"),
		})
		{
			DecisionChips.Add(new TriageFilterChipVm(decision.ToString(), label, PageSourceRole.Other, false, UpdateList));
		}
	}

	// A stylesheet or script entry without a source is a group of its own ("stylesheet", "script"),
	// not one of the pages without a source. The prefix keeps it apart from any source value.
	private const string KindKeyPrefix = "\u0001kind:";

	private string SourceKeyOf(string url) =>
		SharedEntries.IsShared(url) ? KindKeyPrefix + SharedEntries.Label
		: SourceOf(url)?.Text ?? (Core.Urls.FileKind.Of(url) is { } kind ? KindKeyPrefix + kind.ToLowerInvariant() : NoSource);

	private static string ChipLabelOf(string key) =>
		key.Length == 0 ? "No source" : key.StartsWith(KindKeyPrefix, StringComparison.Ordinal) ? key[KindKeyPrefix.Length..] : key;

	/// <summary>Brings the source chips in line with the pages: new values added, gone ones removed, lit state kept.</summary>
	private void RefreshChips(IReadOnlyList<PageSummary> all)
	{
		foreach (var chip in DecisionChips)
		{
			chip.Count = all.Count(p => p.Decision.ToString() == chip.Key);
		}

		if (!HasSources)
		{
			SourceChips.Clear();
			return;
		}

		var groups = all.GroupBy(p => SourceKeyOf(p.Url), StringComparer.Ordinal)
			.Select(g => (Key: g.Key, Count: g.Count(), Role: SourceOf(g.First().Url)?.Role ?? PageSourceRole.Other))
			.OrderBy(g => g.Key.Length == 0)
			.ThenBy(g => g.Role switch { PageSourceRole.Mapped => 0, PageSourceRole.External => 1, PageSourceRole.Local => 2, _ => 3 })
			.ThenBy(g => g.Key, StringComparer.Ordinal)
			.ToList();
		var known = SourceChips.ToDictionary(c => c.Key, StringComparer.Ordinal);
		if (!groups.Select(g => g.Key).SequenceEqual(SourceChips.Select(c => c.Key), StringComparer.Ordinal))
		{
			SourceChips.Clear();
			foreach (var g in groups)
			{
				var on = known.TryGetValue(g.Key, out var old) && old.IsOn;
				SourceChips.Add(new TriageFilterChipVm(g.Key, ChipLabelOf(g.Key), g.Role, on, UpdateList));
			}
		}

		foreach (var g in groups)
		{
			SourceChips.First(c => c.Key == g.Key).Count = g.Count;
		}
	}

	/// <summary>The page being decided; null shows the list.</summary>
	public TriagePageVm? Page
	{
		get => page;
		private set
		{
			if (Set(ref page, value))
			{
				Raise(nameof(IsPageOpen));
			}
		}
	}

	public bool IsPageOpen => page is not null;

	/// <summary>
	/// Reads what triage needs from a snapshot. Touches the disk; call it off the UI thread.
	/// <c>ReadOnly</c> comes with the reason shown on the page: an older snapshot, or one without
	/// the content-quality logs — matching the ledger against that would remove every verdict.
	/// Before a match removes anything, the ledger file is backed up. <paramref name="decidedWord"/>
	/// names spelling findings a dictionary holds by now: they are not triaged.
	/// </summary>
	public static (IReadOnlyList<TriageItem> Items, TriageLedger Ledger, string LedgerPath, bool ReadOnly, int Gone, string ReadOnlyReason) Read(
		string snapshotDirectory, Func<string, bool>? decidedWord = null, IReadOnlyList<string>? titlePatterns = null)
	{
		var found = TriageSource.Read(snapshotDirectory, decidedWord, titlePatterns);
		var path = TriageLedger.PathFor(snapshotDirectory);
		var loaded = TriageLedger.Load(path);
		var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(snapshotDirectory));
		if (!loaded.AcceptsSnapshot(name))
		{
			return (found, loaded, path, true, 0,
				$"This snapshot is older than {loaded.Snapshot}, which the ledger was last matched against. Verdicts are shown but cannot be changed — open the newest snapshot to triage.");
		}

		if (!TriageSource.HasFindingLogs(snapshotDirectory))
		{
			return (found, loaded, path, true, 0,
				"This snapshot has no content-quality logs (10 and 22): a crawl only, an analysis that stopped, or content quality switched off. " +
				"The ledger is left as it is; verdicts cannot be changed here. Run the analysis on it to triage.");
		}

		// The newest snapshot: what is gone is gone — with the ledger as it was kept as a backup.
		int pagesBefore = loaded.Pages.Count;
		var keys = found.SelectMany(i => i.Keys).ToList();
		keys.AddRange(SpellingSource.KeptWithoutLogs(snapshotDirectory, loaded)); // spell check off or stopped: verdicts stay
		keys.AddRange(BrokenLinkSource.KeptWithoutLogs(snapshotDirectory, loaded)); // no log 07: not looked is not fixed
		keys.AddRange(SelfLinkSource.KeptWithoutLogs(snapshotDirectory, loaded)); // no log 09: the same
		keys.AddRange(CanonicalSource.KeptWithoutLogs(snapshotDirectory, loaded)); // no log 16: the same
		keys.AddRange(ForwardingLinkSource.KeptWithoutLogs(snapshotDirectory, loaded)); // no log 40: the same
		keys.AddRange(ImageSource.KeptWithoutLogs(snapshotDirectory, loaded)); // no log 25: the same
		keys.AddRange(SeoSource.KeptWithoutLogs(snapshotDirectory, loaded)); // no log 08: the same

		int gone = loaded.Reconcile(name, keys);
		if (gone > 0 || loaded.Pages.Count < pagesBefore)
		{
			TriageLedger.Backup(path);
		}

		loaded.Save(path);
		return (found, loaded, path, false, gone, string.Empty);
	}

	/// <summary>Shows what <see cref="Read"/> returned, counted by the switches the settings set (Settings → Triage).</summary>
	public void Show(string snapshotDirectory, string siteLabel,
		(IReadOnlyList<TriageItem> Items, TriageLedger Ledger, string LedgerPath, bool ReadOnly, int Gone, string ReadOnlyReason) read,
		Func<string, PageSource?>? pageSources = null,
		Core.Quality.ProperNameKeeper? names = null,
		IReadOnlyDictionary<string, string>? configuredModes = null,
		TriageDictionary? dictionaries = null)
	{
		Names = names ?? Core.Quality.ProperNameKeeper.None;
		Dictionaries = dictionaries ?? TriageDictionary.None;
		setAside.Clear();
		items = read.Items;
		sources = pageSources;
		// A stylesheet or script entry has a name to show even without a content list.
		HasSources = items.Select(i => i.Page).Distinct(StringComparer.OrdinalIgnoreCase)
			.Any(url => pageSources?.Invoke(url) is not null || Core.Urls.FileKind.Of(url) is not null || SharedEntries.IsShared(url));
		ledger = read.Ledger;
		ledgerPath = read.LedgerPath;
		IsReadOnly = read.ReadOnly;
		SnapshotPath = snapshotDirectory;
		HasSnapshot = true;
		SiteRoot = Core.Urls.SiteRoot.FromSnapshot(snapshotDirectory);
		Heading = Views.UrlDisplay.HeadingWithRoot(siteLabel.Length > 0 ? siteLabel : Path.GetFileName(snapshotDirectory), SiteRoot);
		Notice = read.ReadOnly
			? read.ReadOnlyReason
			: read.Gone > 0 ? $"{Format.Count(read.Gone)} verdict(s) removed: their findings are gone in this snapshot. The ledger as it was is kept as triage-ledger.json.1." : string.Empty;
		modes = TriageSwitches.ModesFrom(configuredModes);
		searchText = string.Empty;
		Raise(nameof(SearchText));
		Raise(nameof(SearchActive));
		BuildSwitches();
		BuildDecisionChips();
		SourceChips.Clear();
		RefreshChips(TriageBoard.Pages(items, modes, ledger));
		Page = null;
		rows.Clear();
		Pages.Clear();
		UpdateList();
		Raise(nameof(CanSweep));
	}

	/// <summary>No snapshot to triage.</summary>
	public void ShowNone(string message)
	{
		items = [];
		ledger = new TriageLedger();
		sources = null;
		HasSources = false;
		SourceChips.Clear();
		DecisionChips.Clear();
		HasSnapshot = false;
		Page = null;
		Families.Clear();
		rows.Clear();
		Pages.Clear();
		PendingText = string.Empty;
		BidiPages.Clear();
		Raise(nameof(HasBidi));
		Heading = "Triage";
		SiteRoot = string.Empty;
		Notice = message;
		Raise(nameof(CanSweep));
		ListSummary = string.Empty;
		listSummaryForAi = string.Empty;
	}

	private void BuildSwitches()
	{
		Families.Clear();
		var counts = items.GroupBy(i => i.SwitchId).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
		foreach (var family in TriageSwitches.All.GroupBy(s => s.Family))
		{
			var vm = new TriageSwitchFamilyVm(family.Key);
			foreach (var s in family)
			{
				vm.Switches.Add(new TriageSwitchVm(s, modes[s.Id], counts.GetValueOrDefault(s.Id), OnSwitchChanged));
			}

			vm.Refresh();
			Families.Add(vm);
		}

		RefreshFilterSummary();
	}

	private void RefreshFilterSummary()
	{
		// Two switches may share a label in different families ("Control characters" in the page
		// title and in the template): those are named with their family, so the summary is unambiguous.
		var shared = Families.SelectMany(f => f.Switches).GroupBy(s => s.Label, StringComparer.Ordinal)
			.Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
		var changed = Families.SelectMany(f => f.Switches.Select(s => (Family: f.Title, Switch: s)))
			.Where(x => x.Switch.Mode != TriageMode.On)
			.Select(x => $"{(shared.Contains(x.Switch.Label) ? x.Family + " › " : string.Empty)}{x.Switch.Label} {x.Switch.ModeText}")
			.ToList();
		var isDefault = TriageSwitches.All.All(d => modes[d.Id] == d.Default);
		FilterSummary = (isDefault ? "Defaults" : "Set in Settings")
			+ (changed.Count == 0 ? " · everything on" : " · " + string.Join(" · ", changed));
	}

	private void OnSwitchChanged(TriageSwitchVm changed)
	{
		modes[changed.Id] = changed.Mode;
		foreach (var family in Families)
		{
			family.Refresh();
		}

		RefreshFilterSummary();
		RefreshPages();
	}

	/// <summary>
	/// The pages the chips let through: each group (decision, source) lets through what its lit
	/// chips stand for, everything while none is lit. Pages with bidi controls are always listed,
	/// whatever their decision.
	/// </summary>
	private List<PageSummary> Listed(IReadOnlyList<PageSummary> all)
	{
		TriageFilterChipVm.Dim(DecisionChips);
		TriageFilterChipVm.Dim(SourceChips);
		var search = searchText.Trim();
		return all.Where(p => (p.HasBidi || TriageFilterChipVm.Lets(DecisionChips, p.Decision.ToString()))
				&& (SourceChips.Count == 0 || TriageFilterChipVm.Lets(SourceChips, SourceKeyOf(p.Url)))
				&& PageSearch.Lets(p, search))
			.ToList();
	}

	/// <summary>
	/// After a verdict, a filing or a switch: the rows on screen update their contents in place;
	/// pages to add or remove and a changed order are only counted (<see cref="PendingText"/>)
	/// until the operator asks for <see cref="UpdateList"/>. The list never moves under the eye.
	/// </summary>
	public void RefreshPages()
	{
		var all = TriageBoard.Pages(items, modes, ledger);
		var listed = Listed(all);
		var byUrl = all.ToDictionary(p => p.Url, StringComparer.OrdinalIgnoreCase);
		foreach (var row in Pages)
		{
			if (byUrl.TryGetValue(row.Url, out var summary))
			{
				row.Update(summary);
			}
			else
			{
				row.MarkNotCounted();
			}
		}

		var shown = Pages.Select(r => r.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var wanted = listed.Select(p => p.Url).ToList();
		var wantedSet = wanted.ToHashSet(StringComparer.OrdinalIgnoreCase);
		int add = wanted.Count(u => !shown.Contains(u));
		int remove = Pages.Count(r => !wantedSet.Contains(r.Url));
		var commonNow = Pages.Select(r => r.Url).Where(wantedSet.Contains).ToList();
		var commonWanted = wanted.Where(shown.Contains).ToList();
		bool resorted = !commonNow.SequenceEqual(commonWanted, StringComparer.OrdinalIgnoreCase);

		var parts = new List<string>();
		if (add > 0) parts.Add($"{Format.Count(add)} page(s) to add");
		if (remove > 0) parts.Add($"{Format.Count(remove)} to remove");
		if (resorted) parts.Add("order changed");
		PendingText = parts.Count == 0 ? string.Empty : "The list is not up to date: " + string.Join(" · ", parts);

		RefreshBanner(all, listed);
	}

	/// <summary>Brings the list up to date — only the differences: rows are added, removed or moved, never rebuilt.</summary>
	public void UpdateList()
	{
		var all = TriageBoard.Pages(items, modes, ledger);
		var listed = Listed(all);
		var wanted = listed.ToDictionary(p => p.Url, StringComparer.OrdinalIgnoreCase);

		for (int i = Pages.Count - 1; i >= 0; i--)
		{
			if (!wanted.ContainsKey(Pages[i].Url))
			{
				rows.Remove(Pages[i].Url);
				Pages.RemoveAt(i);
			}
		}

		for (int i = 0; i < listed.Count; i++)
		{
			var summary = listed[i];
			if (!rows.TryGetValue(summary.Url, out var row))
			{
				row = new TriagePageRowVm(summary, SourceOf(summary.Url));
				rows[summary.Url] = row;
				Pages.Insert(i, row);
				continue;
			}

			row.Update(summary);
			int at = Pages.IndexOf(row);
			if (at != i)
			{
				Pages.Move(at, i);
			}
		}

		PendingText = string.Empty;
		RefreshBanner(all, listed);
	}

	private void RefreshBanner(IReadOnlyList<PageSummary> all, IReadOnlyList<PageSummary> listed)
	{
		BidiPages.Clear();
		foreach (var p in all.Where(p => p.HasBidi))
		{
			foreach (var item in p.Triaged.Where(i => TriageSwitches.IsBidi(i.SwitchId)))
			{
				BidiPages.Add(new TriageBidiVm(p.Url, item.Detail, item.Context, item.Type));
			}
		}

		Raise(nameof(HasBidi));
		HasBadges = all.Any(p => p.HasSecurity || p.HasBidi);
		RefreshChips(all);
		ListSummary = Summary(all, listed, $"\u201C{searchText.Trim()}\u201D");
		listSummaryForAi = Summary(all, listed, "the search text");
	}

	// For Copy for AI: the same line without what was searched for (it may be a flagged word).
	private string listSummaryForAi = string.Empty;

	/// <summary>
	/// "12 of 184 page(s) shown"; with a search, how many have it as a flagged word and how many
	/// more the lit chips hide; with none found, why that may be.
	/// </summary>
	private string Summary(IReadOnlyList<PageSummary> all, IReadOnlyList<PageSummary> listed, string searched)
	{
		if (all.Count == 0)
		{
			return "No page has defects that triage counts with these switches.";
		}

		var shown = $"{Format.Count(Pages.Count)} of {Format.Count(all.Count)} page(s) shown";
		if (!SearchActive)
		{
			return shown;
		}

		var search = searchText.Trim();
		int found = all.Count(p => PageSearch.Lets(p, search));
		int hidden = found - listed.Count;
		var hiddenText = hidden > 0 ? $"{Format.Count(hidden)} more hidden by the lit chips" : string.Empty;
		if (Pages.Count == 0)
		{
			return hidden > 0
				? $"No page shown has {searched} in its address or as a flagged word: {hiddenText}."
				: $"No page has {searched} in its address or as a flagged word. A word a dictionary holds is not triaged, nor one whose switch is off.";
		}

		int withWord = listed.Count(p => PageSearch.HasWord(p, search));
		return shown
			+ (withWord > 0 ? $" · {Format.Count(withWord)} with {searched} as a flagged word" : string.Empty)
			+ (hidden > 0 ? " · " + hiddenText : string.Empty);
	}

	/// <summary>
	/// What triage shows, for an AI to reason about: switches, list counts and, with a page open,
	/// its items with their kind and verdict. Structure only — no page address, finding text,
	/// excerpt, source value or ticket number leaves the app this way.
	/// </summary>
	public string ForAi(string version)
	{
		var nl = Environment.NewLine;
		var sb = new System.Text.StringBuilder();
		sb.Append($"CrawlerSuite {version} · triage · {DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}").Append(nl);
		sb.Append("Snapshot: ").Append(Path.GetFileName(Path.TrimEndingDirectorySeparator(snapshotPath))).Append(nl);
		if (IsReadOnly) sb.Append("Read-only: an older snapshot").Append(nl);
		if (notice.Length > 0) sb.Append("Notice: ").Append(Mask.ApplyStrict(notice)).Append(nl);
		sb.Append("Switches: ").Append(filterSummary).Append(nl);
		sb.Append("List: ").Append(Mask.ApplyStrict(listSummaryForAi)).Append(nl);
		sb.Append("Decision chips: ").Append(string.Join(" · ", DecisionChips.Select(c => $"{c.Label} {c.Count}{(c.IsOn ? " (lit)" : string.Empty)}"))).Append(nl);
		if (SourceChips.Count > 0)
		{
			sb.Append($"Source chips: {SourceChips.Count}, {SourceChips.Count(c => c.IsOn)} lit").Append(nl);
		}

		if (SearchActive) sb.Append("Search: active").Append(nl);
		if (HasPending) sb.Append("Pending: ").Append(pendingText).Append(nl);
		if (HasBidi) sb.Append($"Bidi banner: {BidiPages.Count} item(s)").Append(nl);

		if (page is { } open)
		{
			sb.Append(nl).Append($"Open page: {open.DecisionSummary}{(open.HasTickets ? $" · {open.Tickets.Count} ticket number(s)" : string.Empty)}").Append(nl);
			sb.Append("| # | Kind | Ride-along | Verdict | State | Name kept |").Append(nl).Append("|---:|---|---|---|---|---|").Append(nl);
			int n = 0;
			foreach (var (item, rides) in open.Items.Select(i => (i, false)).Concat(open.RideAlong.Select(i => (i, true))).Concat(open.Spelling.Select(i => (i, false))))
			{
				var verdict = item.Verdict?.ToString() ?? "–";
				var kept = item.IsName ? (item.IsKept ? "yes" : "no") : "–";
				sb.Append($"| {++n} | {item.Heading} | {(rides ? "yes" : "no")} | {verdict} | {(item.IsGated ? "waits for content quality" : item.StateText)} | {kept} |").Append(nl);
			}
		}
		else
		{
			sb.Append(nl).Append("| # | Status | Defects | Families | Ride-along | Bidi | Security |").Append(nl).Append("|---:|---|---:|---|---|---|---|").Append(nl);
			int n = 0;
			foreach (var row in Pages)
			{
				sb.Append($"| {++n} | {row.StatusText} | {row.Count} | {row.Families} | {row.RideAlong} | {(row.HasBidi ? "yes" : "no")} | {(row.HasSecurity ? "yes" : "no")} |").Append(nl);
			}
		}

		return sb.ToString();
	}

	/// <summary>Opens a page for deciding.</summary>
	public void Open(string url)
	{
		var summary = TriageBoard.Pages(items, modes, ledger).FirstOrDefault(p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase));
		Page = summary is null ? null : new TriagePageVm(this, summary, ledger.PageOf(url), ledger.PreviouslyTicketed(url));
	}

	/// <summary>Back to the list: the rows update in place; nothing is rebuilt.</summary>
	public void Close()
	{
		Page = null;
		RefreshPages();
	}

	/// <summary>The next page that still needs deciding after <paramref name="url"/>, or null.</summary>
	public string? NextAfter(string url)
	{
		var open = TriageBoard.Pages(items, modes, ledger).Where(p => p.Status != PageStatus.Filed).Select(p => p.Url).ToList();
		int at = open.FindIndex(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
		var rest = at < 0 ? open : open.Skip(at + 1).Concat(open.Take(at));
		return rest.FirstOrDefault(u => !string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
	}

	// ── Ledger writes (from the page view) ──────────────────────────────────

	internal bool Decide(TriageItem item, Verdict? verdict, string comment)
	{
		if (IsReadOnly)
		{
			return false;
		}

		if (verdict is { } v)
		{
			ledger.Decide(item, v, comment, DateTime.Now); // the local day; the ledger keeps no time of day
		}
		else
		{
			ledger.Undecide(item);
		}

		return Save();
	}

	internal LedgerFinding? VerdictOf(TriageItem item) => ledger.VerdictOf(item);

	internal bool IsFiled(TriageItem item) => ledger.IsFiled(item);

	internal bool SetTickets(string url, IEnumerable<string> tickets)
	{
		if (IsReadOnly)
		{
			return false;
		}

		var entry = ledger.PageOf(url);
		entry.Tickets = tickets.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		return Save();
	}

	internal bool FilePage(string url, IReadOnlyList<TriageItem> pageItems, string mode, string ticket)
	{
		if (IsReadOnly)
		{
			return false;
		}

		ledger.FilePage(url, pageItems, mode, ticket, DateTime.Now);
		return Save();
	}

	private bool Save()
	{
		if (memoryOnly)
		{
			return true; // the ticket sweep's copy: nothing goes to disk
		}

		try
		{
			ledger.Save(ledgerPath);
			Notice = string.Empty;
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Core.Logger.LogWarning($"Triage: the ledger could not be saved — {ex.Message}");
			Notice = $"The ledger could not be saved: {ex.Message} The verdict is kept in memory; close the program that holds {TriageLedger.FileName} and decide again to save.";
			return false;
		}
	}
}

/// <summary>A family of switches (Quotes, Accessibility …), with a one-line summary of its switches.</summary>
public sealed class TriageSwitchFamilyVm : Observable
{
	private string summary = string.Empty;

	public TriageSwitchFamilyVm(string title) => Title = title;

	public string Title { get; }

	public ObservableCollection<TriageSwitchVm> Switches { get; } = [];

	/// <summary>"3 on · 1 ride-along · 1 off".</summary>
	public string Summary { get => summary; private set => Set(ref summary, value); }

	public void Refresh()
	{
		int on = Switches.Count(s => s.Mode == TriageMode.On);
		int ride = Switches.Count(s => s.Mode == TriageMode.RideAlong);
		int off = Switches.Count(s => s.Mode == TriageMode.Off);
		var parts = new List<string>();
		if (on > 0) parts.Add($"{on} on");
		if (ride > 0) parts.Add($"{ride} ride-along");
		if (off > 0) parts.Add($"{off} off");
		Summary = string.Join(" · ", parts);
	}
}

/// <summary>One three-state switch: off (false), ride-along (null), on (true).</summary>
public sealed class TriageSwitchVm : Observable
{
	private readonly Action<TriageSwitchVm> changed;
	private TriageMode mode;

	public TriageSwitchVm(TriageSwitch definition, TriageMode mode, int count, Action<TriageSwitchVm> changed)
	{
		Id = definition.Id;
		Label = definition.Label;
		IsLocked = definition.Locked;
		Count = count;
		this.mode = mode;
		this.changed = changed;
	}

	public string Id { get; }

	public string Label { get; }

	/// <summary>A count not known (the settings screen has no snapshot): none is shown.</summary>
	public const int NoCount = -1;

	/// <summary>Findings of this type in the snapshot.</summary>
	public int Count { get; }

	public string CountText => Count < 0 ? string.Empty : Format.Count(Count);

	public bool IsLocked { get; }

	/// <summary>Whether the switch may be changed now besides its lock (the settings row unlocked); always when not given.</summary>
	public Func<bool>? CanChange { get; init; }

	public bool IsEnabled => !IsLocked && (CanChange?.Invoke() ?? true);

	internal void RaiseEnabled() => Raise(nameof(IsEnabled));

	public string ToolTipText => IsLocked
		? "Always triaged — bidi controls can change what a page shows."
		: (Id == TriageSwitches.SoftHyphens
			? "Covers soft hyphens from two finding types: invisible characters in template text and control characters in title, meta and text." + Environment.NewLine
			: string.Empty)
			+ "Left: off (not triaged) · middle: ride-along (only in tickets of pages with other defects; an entry shared by several pages waits) · right: on";

	public TriageMode Mode => mode;

	/// <summary>
	/// Next to "Your patterns": when security-relevant groups exist and the switch is not on,
	/// says what that means for them. Off or ride-along can be right for a session; it shows.
	/// </summary>
	public string SecurityWarning
	{
		get
		{
			int groups = CrawlerSuite.Core.Quality.SecurityPatterns.Groups.Count;
			if (Id != CrawlerSuite.Core.Quality.SecurityPatterns.Type || groups == 0 || mode == TriageMode.On)
			{
				return string.Empty;
			}

			var which = groups == 1 ? "1 pattern group is" : $"{groups} pattern groups are";
			return mode == TriageMode.RideAlong
				? $"{which} security-relevant. With ride-along, their findings are only filed on pages that have other defects."
				: $"{which} security-relevant. With off, their findings are not triaged at all.";
		}
	}

	/// <summary>Said only where it adds something: "off", "ride-along", "always". Plain on is the norm.</summary>
	public string ModeText => mode switch
	{
		TriageMode.Off => "off",
		TriageMode.RideAlong => "ride-along",
		_ => IsLocked ? "always" : string.Empty,
	};

	/// <summary>The switch's state: false = off, null = ride-along, true = on.</summary>
	public bool? State
	{
		get => mode switch { TriageMode.Off => false, TriageMode.RideAlong => null, _ => true };
		set
		{
			var next = value switch { false => TriageMode.Off, null => TriageMode.RideAlong, _ => TriageMode.On };
			if (!IsEnabled || next == mode)
			{
				Raise();
				return;
			}

			mode = next;
			Raise();
			Raise(nameof(ModeText));
			Raise(nameof(SecurityWarning));
			changed(this);
		}
	}
}

/// <summary>A row of the page list. Updated in place, so the list never has to be rebuilt for a change.</summary>
public sealed class TriagePageRowVm : Observable
{
	private bool hasBidi;
	private bool hasSecurity;
	private PageStatus status;
	private int count;
	private string families = string.Empty;
	private string rideAlong = string.Empty;
	private string tickets = string.Empty;
	private bool notCounted;

	public TriagePageRowVm(PageSummary page, PageSource? source = null)
	{
		Url = page.Url;
		SourceText = source?.Text ?? string.Empty;
		CmsLink = source?.CmsLink ?? string.Empty;
		SourceRole = source?.Role ?? PageSourceRole.Other;
		NoSourceAddress = source is null && !SharedEntries.IsShared(Url) ? Url : string.Empty;
		Update(page);
	}

	public string Url { get; }

	/// <summary>The entry's address when it has no source: the chip names a stylesheet or script by its file, anything else by its host; else empty.</summary>
	public string NoSourceAddress { get; }

	/// <summary>The page's source value and label as the content list has them; empty when unknown.</summary>
	public string SourceText { get; }

	/// <summary>The CMS editor link for the page; empty when it cannot be built.</summary>
	public string CmsLink { get; }

	/// <summary>Which configured key the page's source matched (the chip's colour).</summary>
	public PageSourceRole SourceRole { get; }

	public bool HasBidi { get => hasBidi; private set => Set(ref hasBidi, value); }

	/// <summary>The page has a security-relevant finding (a pattern group marked so in Settings).</summary>
	public bool HasSecurity { get => hasSecurity; private set => Set(ref hasSecurity, value); }

	public PageStatus Status { get => status; private set => Set(ref status, value); }

	public int Count { get => count; private set => Set(ref count, value); }

	public string CountText => Format.Count(Count);

	public string Families { get => families; private set => Set(ref families, value); }

	public string RideAlong { get => rideAlong; private set => Set(ref rideAlong, value); }

	public string Tickets { get => tickets; private set => Set(ref tickets, value); }

	public string StatusText => notCounted ? "not counted now" : Status switch
	{
		PageStatus.PartlyDecided => "partly decided",
		PageStatus.ReadyToFile => "ready to file",
		PageStatus.Filed => "filed",
		PageStatus.Reopened => "reopened",
		_ => "open",
	};

	public void Update(PageSummary page)
	{
		notCounted = false;
		HasBidi = page.HasBidi;
		HasSecurity = page.HasSecurity;
		Status = page.Status;
		Count = page.Triaged.Count;
		Families = string.Join(" · ", page.ByFamily.Select(f => $"{f.Family} {f.Count}"));
		RideAlong = page.RideAlong.Count > 0 ? $"+ {page.RideAlong.Count} ride-along" : string.Empty;
		Tickets = string.Join(", ", page.Tickets);
		Raise(nameof(CountText));
		Raise(nameof(StatusText));
	}

	/// <summary>The switches no longer put this page on the list; it stays until the list is updated.</summary>
	public void MarkNotCounted()
	{
		notCounted = true;
		Raise(nameof(StatusText));
	}
}

/// <summary>A bidi control on a page, for the banner.</summary>
public sealed record TriageBidiVm(string Url, string Detail, string Context, string Type);

/// <summary>The page being decided: its items with their verdicts, its tickets, and filing.</summary>
public sealed class TriagePageVm : Observable
{
	private readonly TriageModel owner;
	private readonly PageSummary summary;
	private readonly IReadOnlySet<string> previouslyTicketed;
	private string ticketsText;

	public TriagePageVm(TriageModel owner, PageSummary summary, LedgerPage entry, IReadOnlySet<string> previouslyTicketed)
	{
		this.owner = owner;
		this.summary = summary;
		this.previouslyTicketed = previouslyTicketed;
		Url = summary.Url;
		var source = owner.SourceOf(Url);
		SourceText = source?.Text ?? string.Empty;
		CmsLink = source?.CmsLink ?? string.Empty;
		SourceRole = source?.Role ?? PageSourceRole.Other;
		NoSourceAddress = source is null && !SharedEntries.IsShared(Url) ? Url : string.Empty;
		ticketsText = string.Join(", ", entry.Tickets);
		Tickets = entry.Tickets.ToList();
		Snippet = summary.All.Select(i => i.Snippet).FirstOrDefault(s => s is not null);

		// One explanation card per kind of defect on the page, above its first item. The broken-links
		// card explains all broken links, whatever the reach of the first.
		var explained = new HashSet<string>(StringComparer.Ordinal);
		TriageCardVm? CardFor(TriageItem item) =>
			Core.Findings.FindingsCatalog.BasisFor(item.Type, item.Detail) is { } found && explained.Add(found.Key)
				? new TriageCardVm(found.Key, TriageSwitches.IsBrokenLink(item.Type) ? BrokenLinkSource.Family : item.Label, found.Basis)
				: null;

		// Drawn in the order they are decided: content quality, its ride-along, broken links, then
		// spelling — which waits for the content quality above it.
		static bool OwnSection(TriageItem i) => TriageSwitches.IsSpelling(i.Type) || TriageSwitches.IsBrokenLink(i.Type);
		foreach (var item in summary.Triaged.Where(i => !OwnSection(i)))
		{
			Items.Add(new TriageItemVm(this, item, CardFor(item)));
		}

		foreach (var item in summary.RideAlong.Where(i => !OwnSection(i)))
		{
			RideAlong.Add(new TriageItemVm(this, item, CardFor(item)));
		}

		// Most serious reach first; the card above the first.
		foreach (var item in summary.All.Where(i => TriageSwitches.IsBrokenLink(i.Type))
			.OrderBy(i => Core.Links.BrokenLinkReach.Rank(i.Label)).ThenBy(i => i.Detail, StringComparer.Ordinal))
		{
			BrokenLinks.Add(new TriageItemVm(this, item, CardFor(item)));
		}

		foreach (var item in summary.All.Where(i => TriageSwitches.IsSpelling(i.Type)))
		{
			Spelling.Add(new TriageItemVm(this, item, CardFor(item)));
		}
	}

	public string Url { get; }

	/// <summary>The page's source value and label as the content list has them; empty when unknown.</summary>
	public string SourceText { get; }

	/// <summary>The CMS editor link for the page; empty when it cannot be built.</summary>
	public string CmsLink { get; }

	/// <summary>Which configured key the page's source matched (the chip's colour).</summary>
	public PageSourceRole SourceRole { get; }

	/// <summary>The entry's address when it has no source: the chip names a stylesheet or script by its file, anything else by its host; else empty.</summary>
	public string NoSourceAddress { get; }

	/// <summary>How the page would likely appear as a search result; null when it has no search-appearance item.</summary>
	public Core.Seo.SearchSnippet? Snippet { get; }

	public bool HasSnippet => Snippet is not null;

	/// <summary>The preview's title line: as a result would show it, or a note when there is none.</summary>
	public string SnippetTitle => Snippet is null ? string.Empty : Snippet.Title.Length > 0 ? Snippet.TitleShown : "(no title — a search engine makes one up)";

	/// <summary>The preview's text: as a result would show it, or a note when there is none.</summary>
	public string SnippetDescription => Snippet is null ? string.Empty
		: Snippet.Description.Length > 0 ? Snippet.DescriptionShown : "(no description — a search engine picks text from the page)";

	public bool IsReadOnly => owner.IsReadOnly;

	public bool IsEditable => !owner.IsReadOnly;

	public ObservableCollection<TriageItemVm> Items { get; } = [];

	/// <summary>Items that travel with the ticket (their switch is on ride-along).</summary>
	public ObservableCollection<TriageItemVm> RideAlong { get; } = [];

	public bool HasRideAlong => RideAlong.Count > 0;

	/// <summary>Content-quality items (on): the first section; hidden on a page with only typos.</summary>
	public bool HasItems => Items.Count > 0;

	/// <summary>The broken addresses this entry references (log 07): their own section, before spelling.</summary>
	public ObservableCollection<TriageItemVm> BrokenLinks { get; } = [];

	public bool HasBrokenLinks => BrokenLinks.Count > 0;

	/// <summary>The page's misspelled words: the last section, decided after the content quality above.</summary>
	public ObservableCollection<TriageItemVm> Spelling { get; } = [];

	public bool HasSpelling => Spelling.Count > 0;

	/// <summary>The Spelling section's note while content quality above is still open.</summary>
	public bool SpellingWaits => HasSpelling && QualityUndecided > 0;

	/// <summary>Every item of the page, in the order shown.</summary>
	public IEnumerable<TriageItemVm> AllItems => Items.Concat(RideAlong).Concat(BrokenLinks).Concat(Spelling);

	/// <summary>The ticket numbers, as saved.</summary>
	public List<string> Tickets { get; private set; }

	public bool HasTickets => Tickets.Count > 0;

	/// <summary>The ticket numbers as typed ("INC-1, INC-2"); saved when the field loses focus.</summary>
	public string TicketsText
	{
		get => ticketsText;
		set
		{
			if (Set(ref ticketsText, value ?? string.Empty))
			{
				var list = ticketsText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
				if (owner.SetTickets(Url, list))
				{
					Tickets = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
					Raise(nameof(HasTickets));
				}
			}
		}
	}

	/// <summary>The items that count: a spelling item whose word went into a dictionary is no defect any more.</summary>
	private IEnumerable<TriageItemVm> All => AllItems.Where(i => !i.IsInDictionary);

	public int Undecided => All.Count(i => i.Verdict is null);

	/// <summary>Content-quality items still without a verdict: while there are any, spelling waits.</summary>
	public int QualityUndecided => All.Count(i => i.Verdict is null && !i.IsSpelling && !i.IsBrokenLink);

	/// <summary>
	/// The ticket document can be made once every item has a verdict and at least one goes to
	/// the recipients (Ticket, or Locale: an intended other language still needs markup).
	/// Also for a filed page, e.g. when the first document got lost.
	/// </summary>
	public bool CanMakeDocument => Undecided == 0 && All.Any(i => i.IsTicket || i.IsLocale);

	public int ToTicket => All.Count(i => i.Verdict == Core.Triage.Verdict.Ticket);

	public bool CanFile => IsEditable && Undecided == 0 && All.Any(i => !i.IsFiled);

	public bool IsFiled => Undecided == 0 && All.All(i => i.IsFiled);

	/// <summary>"3 to ticket · 2 won't fix · 1 undecided".</summary>
	public string DecisionSummary
	{
		get
		{
			int wontfix = All.Count(i => i.Verdict == Core.Triage.Verdict.Wontfix);
			int locale = All.Count(i => i.Verdict == Core.Triage.Verdict.Locale);
			var parts = new List<string> { $"{ToTicket} to ticket", $"{wontfix} won't fix" };
			if (locale > 0) parts.Add($"{locale} locale");
			parts.Add(Undecided == 0 ? "all decided" : $"{Undecided} undecided");
			if (IsFiled) parts.Add("filed");
			return string.Join(" · ", parts);
		}
	}

	public string FileButtonText => ToTicket == 0 ? "File (nothing to ticket)" : "Ticket";

	/// <summary>Ticket items filed under an earlier ticket, and those new in this filing.</summary>
	public (IReadOnlyList<TriageItemVm> Before, IReadOnlyList<TriageItemVm> New) TicketSplit()
	{
		var tickets = All.Where(i => i.Verdict == Core.Triage.Verdict.Ticket).ToList();
		var before = tickets.Where(i => i.Item.Keys.All(previouslyTicketed.Contains)).ToList();
		return (before, tickets.Except(before).ToList());
	}

	/// <summary>Files the page. <paramref name="mode"/>: "new", "addendum" (with <paramref name="ticket"/>) or "none".</summary>
	public bool File(string mode, string ticket)
	{
		var pageItems = All.Select(i => i.Item).ToList();
		if (!owner.FilePage(Url, pageItems, mode, ticket))
		{
			return false;
		}

		foreach (var item in All)
		{
			item.Refresh();
		}

		Changed();
		return true;
	}

	/// <summary>The Language of Parts card, shared by the page's Locale items (one is shown, above the first).</summary>
	private TriageCardVm? localeCard;

	internal TriageCardVm LocaleCard => localeCard ??= new TriageCardVm(
		"LOCALE", "Intended other language", Core.Findings.FindingsCatalog.LanguageOfParts);

	/// <summary>The first item with the Locale verdict carries the Language of Parts card.</summary>
	internal bool IsFirstLocale(TriageItemVm item) =>
		AllItems.FirstOrDefault(i => i.IsLocale) == item;

	internal void Changed()
	{
		foreach (var item in AllItems)
		{
			item.LocaleChanged();
			item.GateChanged();
		}

		Raise(nameof(SpellingWaits));

		Raise(nameof(Undecided));
		Raise(nameof(CanMakeDocument));
		Raise(nameof(ToTicket));
		Raise(nameof(CanFile));
		Raise(nameof(IsFiled));
		Raise(nameof(DecisionSummary));
		Raise(nameof(FileButtonText));
	}

	internal bool Decide(TriageItem item, Verdict? verdict, string comment) => owner.Decide(item, verdict, comment);

	internal LedgerFinding? VerdictOf(TriageItem item) => owner.VerdictOf(item);

	internal bool IsItemFiled(TriageItem item) => owner.IsFiled(item);

	internal PageSummary Summary => summary;

	internal Core.Quality.ProperNameKeeper Names => owner.Names;

	internal TriageDictionary Dictionaries => owner.Dictionaries;

	internal string? AddToDictionary(TriageItem item, bool user, Func<bool> confirmShared) => owner.AddToDictionary(item, user, confirmShared);

	internal string? UndoDictionary(TriageItem item) => owner.UndoDictionary(item);

	/// <summary>How many other pages triage counts have the word as a spelling finding.</summary>
	internal int OtherPagesWith(string word) => Math.Max(0, owner.PagesWithWord(word) - 1);

	/// <summary>Back to the list, searched for the word: the pages that have it.</summary>
	internal void FindWord(string word) => owner.FindWord(word);

	/// <summary>A word went into a dictionary or came back: every item of it on the page shows the new state, and the page recounts.</summary>
	internal void DictionaryChanged()
	{
		foreach (var item in AllItems)
		{
			item.RaiseDictionary();
		}

		Changed();
	}

	/// <summary>A name was kept or taken back: every item on the page offering it shows the new state.</summary>
	internal void NamesChanged()
	{
		foreach (var item in All)
		{
			item.RaiseName();
		}
	}
}

/// <summary>One item on the page with its verdict: Ticket · Wontfix · Locale.</summary>
public sealed class TriageItemVm : Observable
{
	private readonly TriagePageVm page;
	private Verdict? verdict;
	private string comment;
	private bool isFiled;
	private bool commentOpened;

	public TriageItemVm(TriagePageVm page, TriageItem item, TriageCardVm? card = null)
	{
		this.page = page;
		Item = item;
		Card = card;
		var recorded = page.VerdictOf(item);
		verdict = recorded?.Verdict;
		comment = recorded?.Comment ?? string.Empty;
		isFiled = page.IsItemFiled(item);
		Name = Core.Quality.HyphenSpacing.NameFrom(item.Type, item.Detail);
	}

	public TriageItem Item { get; }

	// ── A name from a hyphen-spacing finding, kept in a dictionary by a click ──

	private bool includeExtra = true;
	private string nameNotice = string.Empty;

	/// <summary>The name this finding offers; null for other findings.</summary>
	public Core.Quality.HyphenNameCandidate? Name { get; }

	public bool IsName => Name is not null;

	public string NameCore => Name?.Core ?? string.Empty;

	public string NameExtra => Name?.Extra ?? string.Empty;

	public bool HasExtraBefore => Name is { Extra: not null, ExtraFirst: true };

	public bool HasExtraAfter => Name is { Extra: not null, ExtraFirst: false };

	/// <summary>Whether the word next to the two at the hyphen belongs to the name (its chip).</summary>
	public bool IncludeExtra
	{
		get => includeExtra;
		set
		{
			if (Set(ref includeExtra, value))
			{
				RaiseName();
			}
		}
	}

	/// <summary>The entry a click writes, exactly as the page has it.</summary>
	public string NameText => Name?.With(includeExtra) ?? string.Empty;

	// The name counts as kept in either form, with or without the extra word: the check asks both.
	private string? KeptPhrase =>
		Name is null ? null
		: page.Names.IsKept(Name.With(true)) ? Name.With(true)
		: page.Names.IsKept(Name.With(false)) ? Name.With(false)
		: null;

	public bool IsKept => KeptPhrase is not null;

	public bool CanKeep => IsName && !IsKept && page.Names.CanWrite;

	/// <summary>Undo: only for a name this session added.</summary>
	public bool CanUndoName => KeptPhrase is { } phrase && page.Names.CanUndo(phrase);

	public string KeptText => IsKept ? "Kept as a proper name in the settings — gone on the next run" : string.Empty;

	/// <summary>Why a click did not work (a run, unsaved settings, a file that could not be written).</summary>
	public string NameNotice { get => nameNotice; private set => Set(ref nameNotice, value); }

	public void KeepName()
	{
		if (!CanKeep)
		{
			return;
		}

		NameNotice = page.Names.Keep(NameText) ?? string.Empty;
		page.NamesChanged();
	}

	public void UndoName()
	{
		if (KeptPhrase is { } phrase)
		{
			NameNotice = page.Names.Undo(phrase) ?? string.Empty;
			page.NamesChanged();
		}
	}

	internal void RaiseName()
	{
		Raise(nameof(NameText));
		Raise(nameof(IsKept));
		Raise(nameof(CanKeep));
		Raise(nameof(CanUndoName));
		Raise(nameof(KeptText));
	}

	// ── The word of a spelling item, into the user or site dictionary by a click ──

	private string dictionaryNotice = string.Empty;

	/// <summary>The word as it is added: the item's word, exactly as flagged.</summary>
	public string Word => IsSpelling ? Core.Triage.SpellingSource.WordOf(Item.Detail) : string.Empty;

	/// <summary>Where the word went from triage in this session: "user", "site", or null.</summary>
	private string? AddedTo => IsSpelling ? page.Dictionaries.AddedTo(Word) : null;

	/// <summary>Its word went into a dictionary from triage: no defect any more, not counted, its verdict buttons off.</summary>
	public bool IsInDictionary => AddedTo is not null;

	public bool CanAddUser => IsSpelling && !IsInDictionary && IsEditable && !IsGated && page.Dictionaries.CanWriteUser;

	public bool CanAddSite => IsSpelling && !IsInDictionary && IsEditable && !IsGated && page.Dictionaries.CanWriteSite;

	public bool ShowsDictionaryButtons => IsSpelling && !IsInDictionary && page.Dictionaries.CanWrite;

	public string DictionaryText => AddedTo switch
	{
		"user" => "In the user dictionary — right on every site; gone from every page on the next run",
		"site" => "In the site dictionary — right for this site; gone from every page on the next run",
		_ => string.Empty,
	};

	/// <summary>Why a click did not work (a file that could not be written).</summary>
	public string DictionaryNotice { get => dictionaryNotice; private set => Set(ref dictionaryNotice, value); }

	public void AddToDictionary(bool user, Func<bool> confirmShared)
	{
		if (!(user ? CanAddUser : CanAddSite))
		{
			return;
		}

		DictionaryNotice = page.AddToDictionary(Item, user, confirmShared) ?? string.Empty;
		page.DictionaryChanged();
	}

	public void UndoDictionary()
	{
		DictionaryNotice = page.UndoDictionary(Item) ?? string.Empty;
		page.DictionaryChanged();
	}

	internal void RaiseDictionary()
	{
		Raise(nameof(IsInDictionary));
		Raise(nameof(CanAddUser));
		Raise(nameof(CanAddSite));
		Raise(nameof(ShowsDictionaryButtons));
		Raise(nameof(DictionaryText));
		Raise(nameof(CanDecide));
		Raise(nameof(StateText));
		Raise(nameof(IsUndecided));
		Raise(nameof(OtherPages));
		Raise(nameof(HasOtherPages));
		Raise(nameof(OtherPagesText));
		Raise(nameof(OnlyHereText));
	}

	// ── The other pages with the word of a spelling item ──

	/// <summary>How many other pages have this item's word as a finding; 0 for other items and for a word in a dictionary.</summary>
	public int OtherPages => IsSpelling && !IsInDictionary ? page.OtherPagesWith(Word) : 0;

	public bool HasOtherPages => OtherPages > 0;

	public string OtherPagesText => OtherPages == 1 ? "1 other page" : $"{Format.Count(OtherPages)} other pages";

	/// <summary>Said when no other page has the word, so the question does not come up.</summary>
	public string OnlyHereText => IsSpelling && !IsInDictionary && OtherPages == 0 ? "Only on this page" : string.Empty;

	/// <summary>The list, searched for this item's word: every page that has it.</summary>
	public void FindOtherPages()
	{
		if (IsSpelling)
		{
			page.FindWord(Word);
		}
	}

	/// <summary>The explanation of this kind of defect, on the first item of its kind on the page; null otherwise.</summary>
	public TriageCardVm? Card { get; }

	/// <summary>
	/// The Language of Parts card (WCAG 3.1.2), on the first item with the Locale verdict: an
	/// intended other language still needs language markup. Null otherwise.
	/// </summary>
	public TriageCardVm? LocaleCard => IsLocale && page.IsFirstLocale(this) ? page.LocaleCard : null;

	internal void LocaleChanged() => Raise(nameof(LocaleCard));

	public string Heading => $"{Item.Family} › {Item.Label}";

	/// <summary>From a pattern group marked security-relevant.</summary>
	public bool IsSecurity => Item.IsSecurity;

	public string Detail => Item.Detail;

	public string Context => Item.Context;

	public string Type => Item.Type;

	public string Url => Item.Page;

	public bool AllowsLocale => TriageSwitches.AllowsLocale(Item.Type);

	public bool IsEditable => page.IsEditable;

	public bool IsSpelling => TriageSwitches.IsSpelling(Item.Type);

	public bool IsBrokenLink => TriageSwitches.IsBrokenLink(Item.Type);

	/// <summary>
	/// A spelling item on a page whose content quality is not fully decided: a content-quality
	/// verdict can change what counts as a spelling error, so it waits. A verdict already given
	/// stays changeable.
	/// </summary>
	public bool IsGated => IsSpelling && verdict is null && page.QualityUndecided > 0;

	/// <summary>The verdict buttons work: the page can be changed and the item is not waiting.</summary>
	public bool CanDecide => IsEditable && !IsGated && !IsInDictionary;

	/// <summary>Other dictionaries that know the word (spelling only): information, the word stays flagged.</summary>
	public bool HasValidIn => Item.ValidIn.Length > 0;

	public string ValidInText => HasValidIn ? "valid " + Item.ValidIn : string.Empty;

	internal void GateChanged()
	{
		Raise(nameof(IsGated));
		Raise(nameof(CanDecide));
		Raise(nameof(CanAddUser));
		Raise(nameof(CanAddSite));
	}

	public Verdict? Verdict => verdict;

	public bool IsFiled => isFiled;

	public bool IsUndecided => verdict is null && !IsInDictionary;

	public string StateText => IsInDictionary ? "in a dictionary" : verdict is null ? "undecided" : isFiled ? "filed" : "not filed yet";

	public bool IsTicket { get => verdict == Core.Triage.Verdict.Ticket; set => Toggle(Core.Triage.Verdict.Ticket, value); }

	public bool IsWontfix { get => verdict == Core.Triage.Verdict.Wontfix; set => Toggle(Core.Triage.Verdict.Wontfix, value); }

	public bool IsLocale { get => verdict == Core.Triage.Verdict.Locale; set => Toggle(Core.Triage.Verdict.Locale, value); }

	/// <summary>Why it is not fixed (Wontfix only); saved when the field loses focus.</summary>
	public string Comment
	{
		get => comment;
		set
		{
			if (Set(ref comment, value ?? string.Empty) && verdict == Core.Triage.Verdict.Wontfix)
			{
				Apply(verdict);
			}

			RaiseComment();
		}
	}

	/// <summary>The comment box shows on a Wontfix item that has a comment, or once "Add a comment" was clicked.</summary>
	public bool ShowsCommentBox => IsWontfix && (commentOpened || comment.Length > 0);

	/// <summary>"Add a comment": on a Wontfix item without one, while it can still be changed.</summary>
	public bool ShowsAddComment => IsWontfix && !ShowsCommentBox && IsEditable;

	/// <summary>Opens the comment box ("Add a comment").</summary>
	public void OpenComment()
	{
		commentOpened = true;
		RaiseComment();
	}

	private void RaiseComment()
	{
		Raise(nameof(ShowsCommentBox));
		Raise(nameof(ShowsAddComment));
	}

	/// <summary>Clicking the chosen verdict again takes it back.</summary>
	private void Toggle(Verdict target, bool on)
	{
		var next = on ? target : verdict == target ? null : verdict;
		if (next == verdict)
		{
			Raise(nameof(IsTicket));
			Raise(nameof(IsWontfix));
			Raise(nameof(IsLocale));
			return;
		}

		Apply(next);
	}

	private void Apply(Verdict? next)
	{
		if (page.Decide(Item, next, comment))
		{
			verdict = next;
		}

		Refresh();
		page.Changed();
	}

	/// <summary>Re-reads the verdict and filed state after a save or a filing.</summary>
	public void Refresh()
	{
		var recorded = page.VerdictOf(Item);
		verdict = recorded?.Verdict;
		isFiled = page.IsItemFiled(Item);
		Raise(nameof(Verdict));
		Raise(nameof(IsTicket));
		Raise(nameof(IsWontfix));
		Raise(nameof(IsLocale));
		Raise(nameof(IsFiled));
		Raise(nameof(IsUndecided));
		Raise(nameof(StateText));
		RaiseComment();
	}
}

/// <summary>
/// The explanation of a kind of defect, shown once per page above its first item: the same card
/// as above its Results table. Folded to one line; whether it is open is remembered per kind for
/// the session, so an opened explanation stays open on the next page.
/// </summary>
public sealed class TriageCardVm : Observable
{
	private static readonly HashSet<string> Opened = new(StringComparer.Ordinal);
	private readonly string key;
	private bool isExpanded;

	public TriageCardVm(string key, string label, Core.Findings.FindingBasis basis)
	{
		this.key = key;
		Label = label;
		Basis = basis;
		isExpanded = Opened.Contains(key);
	}

	public string Label { get; }

	public Core.Findings.FindingBasis Basis { get; }

	/// <summary>"Hidden but focusable — likely WCAG issue" on the folded line; other cards name their verdict.</summary>
	public string Headline => $"{Label} — {Basis.Headline}";

	public bool IsExpanded
	{
		get => isExpanded;
		set
		{
			if (Set(ref isExpanded, value))
			{
				if (value)
				{
					Opened.Add(key);
				}
				else
				{
					Opened.Remove(key);
				}
			}
		}
	}
}

/// <summary>
/// One filter chip above the triage list: a page source or a decision group, with its page count.
/// Lit chips let their pages through; a click toggles it and brings the list up to date.
/// </summary>
/// <summary>
/// A filter chip, the same on every page (Triage, Dictionaries, Spell findings; the Comparison's
/// follow the same rule): a click lights it and shows only its items, another click puts it out;
/// lit chips of a group add up; a group with none lit shows everything. While one is lit, the
/// others of its group are dimmed. Every page starts with none lit.
/// </summary>
public sealed class TriageFilterChipVm : Observable
{
	private readonly Action changed;
	private bool isOn;
	private bool isDimmed;
	private int count;

	/// <summary>Whether a group lets an item through: none lit, or the item's own chip lit (<see cref="Core.Display.ChipRule"/>).</summary>
	public static bool Lets(IEnumerable<TriageFilterChipVm> group, string key) =>
		Core.Display.ChipRule.Lets(group.Select(c => (c.Key, c.IsOn)), key);

	/// <summary>Dims the chips of a group that are not lit while another one is.</summary>
	public static void Dim(IReadOnlyCollection<TriageFilterChipVm> group)
	{
		var lit = group.Select(c => c.IsOn).ToList();
		foreach (var chip in group)
		{
			chip.IsDimmed = Core.Display.ChipRule.Dimmed(lit, chip.IsOn);
		}
	}

	/// <summary>Another chip of its group is lit and this one is not: its items are not shown.</summary>
	public bool IsDimmed { get => isDimmed; private set => Set(ref isDimmed, value); }

	public TriageFilterChipVm(string key, string label, PageSourceRole role, bool isOn, Action changed)
	{
		Key = key;
		Label = label;
		Role = role;
		this.isOn = isOn;
		this.changed = changed;
	}

	/// <summary>What the chip matches (the source text, or the decision name).</summary>
	public string Key { get; }

	public string Label { get; }

	/// <summary>The chip's colour, as in the table (decision chips are neutral).</summary>
	public PageSourceRole Role { get; }

	/// <summary>Pages in this group with the current switches, shown or not.</summary>
	public int Count
	{
		get => count;
		set
		{
			if (Set(ref count, value))
			{
				Raise(nameof(Text));
			}
		}
	}

	/// <summary>The label and the page count, e.g. "Open  123".</summary>
	public string Text => $"{Label}  {Format.Count(count)}";

	public bool IsOn
	{
		get => isOn;
		set
		{
			if (Set(ref isOn, value))
			{
				changed();
			}
		}
	}
}
