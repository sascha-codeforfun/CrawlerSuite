using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Comparison;
using CrawlerSuite.Core.Settings;
using CrawlerSuite.Core.Snapshots;

namespace CrawlerSuite.Run;

/// <summary>
/// A snapshot of the site, as the pickers offer it: its name ("yyyy-MM-dd-HH-mm-ss") in parts, so
/// the screen can colour month and day — what tells two snapshots apart at a glance.
/// </summary>
public sealed record SnapshotPick(string Path, string Name, string Label, bool IsLatest = false)
{
	public string Year => Part(0, 4);

	public string Month => Part(5, 2);

	public string Day => Part(8, 2);

	/// <summary>"12:49:16"; empty for a name of another form.</summary>
	public string Time => Name.Length == 19 ? $"{Name[11..13]}:{Name[14..16]}:{Name[17..19]}" : string.Empty;

	/// <summary>A name of another form is shown whole, in <see cref="Year"/>.</summary>
	public bool IsDated => Name.Length == 19;

	public string Latest => IsLatest ? "  (latest)" : string.Empty;

	/// <summary>The pick for a snapshot folder.</summary>
	public static SnapshotPick Of(string path, bool isLatest = false)
	{
		var name = System.IO.Path.GetFileName(path);
		return new SnapshotPick(path, name, ComparisonModel.Label(name) + (isLatest ? "  (latest)" : string.Empty), isLatest);
	}

	public override string ToString() => Label;

	private string Part(int start, int length) => !IsDated ? (start == 0 ? Name : string.Empty) : Name.Substring(start, length);
}

/// <summary>
/// The Comparison page: two snapshots of the scoped site, A and B, compared file by file; the
/// files that are not identical, filtered by state and kind; and one file's two sides next to
/// each other. Read-only — nothing in either snapshot is written, and findings are not compared.
/// </summary>
public sealed class ComparisonModel : Observable
{
	private SnapshotPick? selectedA;
	private SnapshotPick? selectedB;
	private string pairProblem = string.Empty;
	private string pairHint = string.Empty;
	private string pruneHint = string.Empty;
	private Config? config;
	private readonly Dictionary<string, IReadOnlyDictionary<string, CrawlOutcome>> crawlLogs = new(StringComparer.OrdinalIgnoreCase);
	private bool isComparing;
	private double progressPercent;
	private string progressText = string.Empty;
	private ComparisonResult? result;
	private (DateTime A, DateTime B) prunedAt;
	private CancellationTokenSource? running;
	private string summary = string.Empty;
	private string notice = string.Empty;
	private FileRowVm? selectedFile;
	private int detailVersion;
	private bool refilling;
	private int parallelism;

	public ComparisonModel()
	{
		StateChips =
		[
			new FilterChipVm("Differ", "Pill.Warn", this),
			new FilterChipVm("Only in A", "Pill.Error", this),
			new FilterChipVm("Only in B", "Pill.Ok", this),
		];
		KindChips =
		[
			new FilterChipVm("Pages", "Pill.Accent", this),
			new FilterChipVm("Files", "Pill", this),
		];
		Detail = new FileDetailVm(this);
	}

	/// <summary>Raised when the page asks for the Comparison tab of the settings.</summary>
	public event EventHandler? SettingsRequested;

	public ObservableCollection<SnapshotPick> Snapshots { get; } = [];

	public SnapshotPick? SelectedA
	{
		get => selectedA;
		set
		{
			// While the list is refilled, the pickers' passing nulls are not choices.
			if (!refilling && Set(ref selectedA, value))
			{
				PairChanged();
			}
		}
	}

	public SnapshotPick? SelectedB
	{
		get => selectedB;
		set
		{
			if (!refilling && Set(ref selectedB, value))
			{
				PairChanged();
			}
		}
	}

	/// <summary>Why A and B cannot be compared; empty when they can.</summary>
	public string PairProblem { get => pairProblem; private set => Set(ref pairProblem, value); }

	/// <summary>Something to know about a pair that can be compared (A is the later one); empty for nothing.</summary>
	public string PairHint { get => pairHint; private set => Set(ref pairHint, value); }

	public bool CanCompare => PairProblem.Length == 0 && !IsComparing && SelectedA is not null && SelectedB is not null;

	/// <summary>What the prune stamps of A and B say (pruned differently, or not known); empty when both were pruned with the groups in effect.</summary>
	public string PruneHint { get => pruneHint; private set => Set(ref pruneHint, value); }

	/// <summary>A re-prune can start: not busy, a site with snapshots and its settings known.</summary>
	public bool CanReprune => !IsComparing && config is not null && Snapshots.Count > 0;

	public bool CanReprunePair => CanReprune && PairProblem.Length == 0 && SelectedA is not null && SelectedB is not null;

	/// <summary>How pages are compared, in a line.</summary>
	public string PagesText => "Pages are compared as each snapshot's last run pruned them: the boilerplate groups applied, reference pages whole but for their attribute parts (Settings → Spelling → Boilerplate groups). Blank lines — empty or only spaces and tabs, as a removed part leaves them — are set aside, and so are cache busters and per-delivery tokens. PDFs are compared by what they carry; the site's own patterns (Settings → Comparison) are set aside in their text.";

	public bool IsComparing
	{
		get => isComparing;
		private set
		{
			if (Set(ref isComparing, value))
			{
				Raise(nameof(CanCompare));
				Raise(nameof(CanChangePair));
				Raise(nameof(CanReprune));
				Raise(nameof(CanReprunePair));
			}
		}
	}

	public bool CanChangePair => !IsComparing;

	public double ProgressPercent { get => progressPercent; private set => Set(ref progressPercent, value); }

	public string ProgressText { get => progressText; private set => Set(ref progressText, value); }

	public bool HasResult => result is not null;

	/// <summary>What the compare found, in a line.</summary>
	public string Summary { get => summary; private set => Set(ref summary, value); }

	/// <summary>A message instead of a result (no snapshots, the compare failed).</summary>
	public string Notice { get => notice; private set => Set(ref notice, value); }

	public IReadOnlyList<FilterChipVm> StateChips { get; }

	public IReadOnlyList<FilterChipVm> KindChips { get; }

	/// <summary>One chip per file type of the listed files (pages have none), the most frequent first.</summary>
	public ObservableCollection<FilterChipVm> TypeChips { get; } = [];

	/// <summary>The type chips apply to files: hidden when there are none, or when only Pages is lit.</summary>
	public bool ShowTypeChips => TypeChips.Count > 0 && !(KindChips[0].IsOn && !KindChips[1].IsOn);

	/// <summary>Every file the compare listed (not identical).</summary>
	private List<FileRowVm> all = [];

	/// <summary>The listed files the lit chips let through.</summary>
	public ObservableCollection<FileRowVm> Shown { get; } = [];

	public string ShownText => result is null ? string.Empty
		: Shown.Count == all.Count ? $"{Format.Count(all.Count)} {(all.Count == 1 ? "file" : "files")} not identical"
		: $"{Format.Count(Shown.Count)} of {Format.Count(all.Count)} files not identical";

	public bool HasListed => all.Count > 0;

	/// <summary>The Files tab's header, with the files the chips let through.</summary>
	public string FilesTabHeader => result is null ? "Files" : $"Files ({Format.Count(Shown.Count)})";

	public bool NothingListed => result is not null && all.Count == 0;

	public FileRowVm? SelectedFile
	{
		get => selectedFile;
		set
		{
			if (Set(ref selectedFile, value))
			{
				RaisePosition();
				_ = LoadDetailAsync();
			}
		}
	}

	public FileDetailVm Detail { get; }

	/// <summary>The labels of the two sides, for the column heads of the view.</summary>
	public string LabelA => result is null ? "A" : $"A · {Label(Path.GetFileName(result.SnapshotA))}";

	/// <summary>The compared snapshots, for the coloured dates over the two sides.</summary>
	public SnapshotPick? ResultA => result is null ? null : SnapshotPick.Of(result.SnapshotA);

	public SnapshotPick? ResultB => result is null ? null : SnapshotPick.Of(result.SnapshotB);

	public string LabelB => result is null ? "B" : $"B · {Label(Path.GetFileName(result.SnapshotB))}";

	// ── Snapshots and the pair ───────────────────────────────────────────────

	/// <summary>
	/// The scoped site changed or the page opened: the site's snapshots are listed again. A and B
	/// stay where they are when both are still there; otherwise B is the latest and A the one before.
	/// </summary>
	public void ShowSite(Config? config, string? siteFolder)
	{
		var keepA = SelectedA?.Path;
		var keepB = SelectedB?.Path;
		var sameSite = Snapshots.Count > 0 && siteFolder is not null
			&& string.Equals(Path.GetFullPath(Path.GetDirectoryName(Snapshots[0].Path) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar),
				Path.GetFullPath(siteFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

		parallelism = config?.MaxDegreeOfParallelism ?? 0;
		this.config = config;

		var paths = siteFolder is null ? [] : SnapshotComparison.SnapshotsOf(siteFolder);
		if (!paths.SequenceEqual(Snapshots.Select(p => p.Path), StringComparer.Ordinal))
		{
			refilling = true;
			try
			{
				Snapshots.Clear();
				for (int i = 0; i < paths.Count; i++)
				{
					Snapshots.Add(SnapshotPick.Of(paths[i], isLatest: i == paths.Count - 1));
				}
			}
			finally
			{
				refilling = false;
			}
		}

		if (!sameSite)
		{
			ClearResult();
			lock (crawlLogs)
			{
				crawlLogs.Clear();
			}
		}

		selectedA = Snapshots.FirstOrDefault(s => s.Path == keepA) is { } a && sameSite ? a
			: Snapshots.Count >= 2 ? Snapshots[^2] : null;
		selectedB = Snapshots.FirstOrDefault(s => s.Path == keepB) is { } b && sameSite ? b
			: Snapshots.Count >= 1 ? Snapshots[^1] : null;
		Raise(nameof(SelectedA));
		Raise(nameof(SelectedB));

		// A result whose snapshots are gone, or that was made for another pair, goes.
		if (result is not null && (result.SnapshotA != selectedA?.Path || result.SnapshotB != selectedB?.Path))
		{
			ClearResult();
		}

		// So does one whose pruned pages were written again since (a snapshot re-run): the list and the view would disagree.
		if (result is not null && PrunedAt(result.SnapshotA, result.SnapshotB) != prunedAt)
		{
			ClearResult();
			ProgressText = "A snapshot was run again since this comparison: compare again.";
		}

		CheckPair();
		Notice = siteFolder is null ? "No site is selected."
			: Snapshots.Count == 0 ? "The site has no snapshots yet."
			: Snapshots.Count == 1 ? "The site has one snapshot. A comparison needs two."
			: string.Empty;
	}

	public void Swap()
	{
		if (!CanChangePair)
		{
			return;
		}

		(selectedA, selectedB) = (selectedB, selectedA);
		Raise(nameof(SelectedA));
		Raise(nameof(SelectedB));
		PairChanged();
	}

	public void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

	private void PairChanged()
	{
		// A result belongs to the pair it was made for.
		if (result is not null && (result.SnapshotA != SelectedA?.Path || result.SnapshotB != SelectedB?.Path))
		{
			ClearResult();
		}

		CheckPair();
	}

	private void CheckPair()
	{
		PairProblem = SelectedA is null || SelectedB is null ? (Snapshots.Count < 2 ? string.Empty : "Choose a snapshot for A and one for B.")
			: SnapshotComparison.CheckPair(SelectedA.Path, SelectedB.Path) ?? string.Empty;

		// Allowed, but said: with A the later one, "only in A" are files B had and A no longer has.
		PairHint = PairProblem.Length == 0 && SelectedA is not null && SelectedB is not null
			&& string.CompareOrdinal(SelectedA.Name, SelectedB.Name) > 0
			? "A is the later snapshot here: \"only in A\" are files that came after B. Swap to read it the usual way."
			: string.Empty;
		PruneHint = PairProblem.Length == 0 && SelectedA is not null && SelectedB is not null && config is not null
			? Reprune.Hint(Reprune.ReadStamp(SelectedA.Path), Reprune.ReadStamp(SelectedB.Path),
				Reprune.Fingerprint(config.SpellCheckEngine.BoilerplateGroups))
			: string.Empty;
		Raise(nameof(CanCompare));
		Raise(nameof(CanReprune));
		Raise(nameof(CanReprunePair));
	}

	// ── Compare ──────────────────────────────────────────────────────────────

	public async Task CompareAsync()
	{
		if (!CanCompare || SelectedA is null || SelectedB is null)
		{
			return;
		}

		ClearResult();
		var a = SelectedA.Path;
		var b = SelectedB.Path;
		var threads = parallelism;
		var setAside = CrawlerSuite.Core.Comparison.PdfSetAside.From(config?.ComparisonPdfSetAside);
		var cancel = new CancellationTokenSource();
		running = cancel;
		IsComparing = true;
		ProgressPercent = 0;
		ProgressText = "Listing the files …";
		Notice = string.Empty;

		var progress = new Progress<(int Done, int Total)>(p =>
		{
			if (running != cancel)
			{
				return;
			}

			ProgressPercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
			ProgressText = $"{Format.Count(p.Done)} of {Format.Count(p.Total)} files compared";
		});

		try
		{
			var before = PrunedAt(a, b);
			var outcome = await Task.Run(() => SnapshotComparison.Run(a, b, progress, cancel.Token, threads, setAside), cancel.Token);
			if (running == cancel)
			{
				prunedAt = before;
				ShowResult(outcome);
				ProgressText = $"Compared in {outcome.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s.";
			}
		}
		catch (OperationCanceledException)
		{
			ProgressText = "Stopped. Nothing was kept.";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			Logger.LogWarning($"Comparison failed — {ex.Message}");
			Notice = $"The comparison failed: {ex.Message}";
			ProgressText = string.Empty;
		}
		finally
		{
			if (running == cancel)
			{
				running = null;
				IsComparing = false;
				ProgressPercent = 0;
				CheckPair();
			}

			cancel.Dispose();
		}
	}

	public void Stop() => running?.Cancel();

	// ── Re-prune ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Prunes A and B — or every snapshot of the site — again with the boilerplate groups in
	/// effect, then compares the pair again when it had been compared. Writes only each
	/// snapshot's pruned pages and prune stamp; the triage ledger is never touched.
	/// </summary>
	public async Task RepruneAsync(bool all)
	{
		if (!(all ? CanReprune : CanReprunePair) || config is not { } settings)
		{
			return;
		}

		var targets = all ? Snapshots.Select(s => s.Path).ToList() : [SelectedA!.Path, SelectedB!.Path];
		bool compareAfter = result is not null || !all;
		ClearResult();
		var cancel = new CancellationTokenSource();
		running = cancel;
		IsComparing = true;
		ProgressPercent = 0;
		Notice = string.Empty;
		var skipped = new List<string>();
		int done = 0;
		try
		{
			for (int i = 0; i < targets.Count; i++)
			{
				var snapshot = targets[i];
				var label = SnapshotPick.Of(snapshot).Label;
				var total = Math.Max(1, Reprune.PagesOf(snapshot, settings.FilePattern));
				int pages = 0;
				ProgressText = $"Re-pruning {label} ({i + 1} of {targets.Count}) …";
				var lead = $"Re-pruning {label} ({i + 1} of {targets.Count})";
				var progress = new Progress<int>(n =>
				{
					if (running == cancel)
					{
						ProgressPercent = 100.0 * (i + Math.Min(1.0, (double)n / total)) / targets.Count;
						ProgressText = $"{lead} · {Format.Count(n)} of {Format.Count(total)} pages";
					}
				});
				var outcome = await Task.Run(() => Reprune.Run(snapshot, settings,
					() => ((IProgress<int>)progress).Report(Interlocked.Increment(ref pages)), cancel.Token), cancel.Token);
				if (outcome.Skipped is { } why)
				{
					skipped.Add($"{label}: {why}");
				}
				else
				{
					done++;
				}
			}

			ProgressText = $"Re-pruned {Format.Count(done)} {(done == 1 ? "snapshot" : "snapshots")} with the current boilerplate groups."
				+ (skipped.Count > 0 ? $" Left as they were: {string.Join("; ", skipped)}." : string.Empty);
		}
		catch (OperationCanceledException)
		{
			ProgressText = $"Stopped after {Format.Count(done)} {(done == 1 ? "snapshot" : "snapshots")}; the one in progress kept its pruned pages.";
			compareAfter = false;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.LogWarning($"Re-prune failed — {ex.Message}");
			Notice = $"The re-prune failed: {ex.Message}";
			compareAfter = false;
		}
		finally
		{
			if (running == cancel)
			{
				running = null;
				IsComparing = false;
				ProgressPercent = 0;
				CheckPair();
			}

			cancel.Dispose();
		}

		if (compareAfter && CanCompare)
		{
			var note = ProgressText;
			await CompareAsync();
			ProgressText = note + " " + ProgressText;
		}
	}

	private void ShowResult(ComparisonResult outcome)
	{
		result = outcome;
		// Reference pages by the groups in effect, the rule the prune follows.
		var groups = config?.SpellCheckEngine?.BoilerplateGroups ?? [];
		var resolver = groups.Count > 0 ? new CrawlerSuite.Core.Boilerplate.BoilerplateResolver(groups) : null;
		all = outcome.Listed.Select(f => new FileRowVm(f,
			resolver is not null && f.IsPage && f.Url is { } url && resolver.ResolveSelectors(url).IsReferencePage)).ToList();

		var differ = outcome.Count(CompareState.Differs);
		var onlyA = outcome.Count(CompareState.OnlyInA);
		var onlyB = outcome.Count(CompareState.OnlyInB);
		foreach (var chip in StateChips.Concat(KindChips))
		{
			chip.Reset();
		}

		KindChips[0].SetCount(all.Count(f => f.File.IsPage));
		KindChips[1].SetCount(all.Count(f => !f.File.IsPage));
		TypeChips.Clear();
		foreach (var (label, kinds, _) in TypeGroups.Plan(all.Where(f => !f.File.IsPage).Select(f => f.Kind)))
		{
			TypeChips.Add(new FilterChipVm(label, "Pill", this, kinds));
		}

		var identical = $"{Format.Count(outcome.Identical)} identical";
		var why = new List<string>();
		if (outcome.IdenticalOnlyPruned > 0)
		{
			why.Add($"{Format.Count(outcome.IdenticalOnlyPruned)} {(outcome.IdenticalOnlyPruned == 1 ? "page" : "pages")} only as pruned");
		}

		if (outcome.IdenticalOnlyBlankLines > 0)
		{
			why.Add($"{Format.Count(outcome.IdenticalOnlyBlankLines)} {(outcome.IdenticalOnlyBlankLines == 1 ? "page" : "pages")} but for blank lines");
		}

		if (outcome.IdenticalOnlyCacheBusters > 0)
		{
			why.Add($"{Format.Count(outcome.IdenticalOnlyCacheBusters)} {(outcome.IdenticalOnlyCacheBusters == 1 ? "page" : "pages")} but for cache busters");
		}

		if (outcome.IdenticalOnlyTokens > 0)
		{
			why.Add($"{Format.Count(outcome.IdenticalOnlyTokens)} {(outcome.IdenticalOnlyTokens == 1 ? "page" : "pages")} but for per-delivery tokens");
		}

		if (outcome.IdenticalOnlyPatterns > 0)
		{
			var each = outcome.PatternHits is { Count: > 0 } hits
				? " (" + string.Join(", ", hits.Select(h => $"{h.Key}: {Format.Count(h.Value)}")) + ")"
				: string.Empty;
			why.Add($"{Format.Count(outcome.IdenticalOnlyPatterns)} {(outcome.IdenticalOnlyPatterns == 1 ? "PDF" : "PDFs")} but for configured patterns{each}");
		}

		if (outcome.RegeneratedPdfs > 0)
		{
			why.Add($"{Format.Count(outcome.RegeneratedPdfs)} {(outcome.RegeneratedPdfs == 1 ? "PDF" : "PDFs")} written out again with the same content");
		}

		if (outcome.Resaved > 0)
		{
			why.Add($"{Format.Count(outcome.Resaved)} {(outcome.Resaved == 1 ? "file" : "files")} saved again under a new cache buster");
		}

		if (why.Count > 0)
		{
			identical += $" ({string.Join(", ", why)})";
		}

		Summary = $"{Format.Count(outcome.FilesA)} files in A · {Format.Count(outcome.FilesB)} in B · {identical} · "
			+ $"{Format.Count(differ)} differ · {Format.Count(onlyA)} only in A · {Format.Count(onlyB)} only in B. "
			+ $"Header sidecars left out: {Format.Count(outcome.Sidecars)}.";
		Raise(nameof(HasResult));
		Raise(nameof(HasListed));
		Raise(nameof(NothingListed));
		Raise(nameof(LabelA));
		Raise(nameof(LabelB));
		Raise(nameof(ResultA));
		Raise(nameof(ResultB));
		Filter();
	}

	private void ClearResult()
	{
		running?.Cancel();
		result = null;
		all = [];
		Shown.Clear();
		TypeChips.Clear();
		Raise(nameof(ShowTypeChips));
		ProgressText = string.Empty;
		Summary = string.Empty;
		SelectedFile = null;
		Detail.Clear();
		Raise(nameof(HasResult));
		Raise(nameof(HasListed));
		Raise(nameof(NothingListed));
		Raise(nameof(ShownText));
		Raise(nameof(FilesTabHeader));
		Raise(nameof(LabelA));
		Raise(nameof(LabelB));
		Raise(nameof(ResultA));
		Raise(nameof(ResultB));
	}

	/// <summary>
	/// A chip was clicked: a lit chip shows its files. In each group (kind, state, type) the lit
	/// chips add up; a group with none lit lets everything through; the groups combine. The state
	/// chips count the files the kind and type chips let through, the type chips those the kind and
	/// state chips let through. A lit type chip lets no page through; the type chips do not filter
	/// while they are hidden (only Pages lit).
	/// </summary>
	internal void Filter()
	{
		var keep = SelectedFile;
		Shown.Clear();
		var rows = all.Select(r => new ChipFilter.Row(r.File.IsPage, StateIndex(r.File.State), r.File.IsPage ? -1 : TypeIndex(r.Kind))).ToList();
		var (shown, states, types) = ChipFilter.Run(rows,
			[.. KindChips.Select(c => c.IsOn)], [.. StateChips.Select(c => c.IsOn)], [.. TypeChips.Select(c => c.IsOn)], ShowTypeChips);
		for (int i = 0; i < all.Count; i++)
		{
			if (shown[i])
			{
				Shown.Add(all[i]);
			}
		}

		for (int i = 0; i < StateChips.Count; i++)
		{
			StateChips[i].SetCount(states[i]);
		}

		for (int i = 0; i < TypeChips.Count; i++)
		{
			TypeChips[i].SetCount(types[i]);
			TypeChips[i].SetVisible(types[i] > 0 || TypeChips[i].IsOn);
		}

		Raise(nameof(ShowTypeChips));
		foreach (var group in new IReadOnlyList<FilterChipVm>[] { StateChips, KindChips, TypeChips })
		{
			bool anyOn = group.Any(c => c.IsOn);
			foreach (var chip in group)
			{
				chip.SetDimmed(anyOn && !chip.IsOn);
			}
		}

		Raise(nameof(ShownText));
		Raise(nameof(FilesTabHeader));
		SelectedFile = keep is not null && Shown.Contains(keep) ? keep : null;
		RaisePosition();
	}

	/// <summary>The type chip that stands for a kind; -1 when none does.</summary>
	private int TypeIndex(string kind)
	{
		for (int i = 0; i < TypeChips.Count; i++)
		{
			if (TypeChips[i].Covers?.Contains(kind) == true)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>Whether a group lets a row through: no chip lit, or the row's own chip lit.</summary>
	internal static bool Passes(IReadOnlyList<FilterChipVm> group, int index) => !group.Any(c => c.IsOn) || group[index].IsOn;

	internal static int StateIndex(CompareState state) => state switch
	{
		CompareState.Differs => 0,
		CompareState.OnlyInA => 1,
		_ => 2,
	};

	// ── One file ─────────────────────────────────────────────────────────────

	/// <summary>Moves to the next (or previous) file of the list shown.</summary>
	/// <summary>Where the selected file stands in the list shown: "12 of 112"; empty without one.</summary>
	public string Position => SelectedFile is { } row && Shown.IndexOf(row) is var at && at >= 0
		? $"{Format.Count(at + 1)} of {Format.Count(Shown.Count)}"
		: string.Empty;

	public bool CanPreviousFile => SelectedFile is { } row && Shown.IndexOf(row) > 0;

	public bool CanNextFile => SelectedFile is { } row && Shown.IndexOf(row) is var at && at >= 0 && at < Shown.Count - 1;

	private void RaisePosition()
	{
		Raise(nameof(Position));
		Raise(nameof(CanPreviousFile));
		Raise(nameof(CanNextFile));
	}

	public void Step(int by)
	{
		if (Shown.Count == 0)
		{
			return;
		}

		int at = SelectedFile is null ? -1 : Shown.IndexOf(SelectedFile);
		int next = at < 0 ? (by > 0 ? 0 : Shown.Count - 1) : Math.Clamp(at + by, 0, Shown.Count - 1);
		SelectedFile = Shown[next];
	}

	/// <summary>The file's two sides again: the switches (all lines, as saved) changed.</summary>
	internal Task ReloadDetailAsync() => LoadDetailAsync();

	private async Task LoadDetailAsync()
	{
		int version = ++detailVersion;
		var row = SelectedFile;
		if (row is null || result is null)
		{
			Detail.Clear();
			return;
		}

		var a = result.SnapshotA;
		var b = result.SnapshotB;
		bool raw = Detail.AsSaved;
		bool allLines = Detail.AllLines;
		Detail.ShowLoading(row);
		try
		{
			var (sides, view, imageA, imageB, pdf) = await Task.Run(() =>
			{
				var s = SnapshotComparison.Load(a, b, row.File, raw);
				var v = s.IsText ? SideBySide.Build(s.TextA, s.TextB, allLines, setCacheBustersAside: row.File.IsPage) : null;
				if (s.IsText)
				{
					return (s, v, (ImageSide?)null, (ImageSide?)null, (PdfDifference?)null);
				}

				var pathA = SavedPath(a, row.File.FileA);
				var pathB = SavedPath(b, row.Name);
				if (ImageKinds.IsImage(row.Name))
				{
					return (s, v, ImagePreview.Load(pathA), ImagePreview.Load(pathB), (PdfDifference?)null);
				}

				// A PDF: what differs inside it — pages, content, stamps.
				return (s, v, (ImageSide?)null, (ImageSide?)null, PdfContent.Compare(File.Exists(pathA) ? pathA : null, File.Exists(pathB) ? pathB : null));
			});

			// A file in one snapshot only: what the other snapshot's crawl did with its address.
			var crawlNote = row.File.State switch
			{
				CompareState.OnlyInA => await Task.Run(() => CrawlOutcomes.Describe("B", CrawlLogOf(b), row.File.Url)),
				CompareState.OnlyInB => await Task.Run(() => CrawlOutcomes.Describe("A", CrawlLogOf(a), row.File.Url)),
				_ => string.Empty,
			};
			if (version == detailVersion)
			{
				Detail.Show(row, sides, view, a, b, imageA, imageB, pdf, crawlNote);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			if (version == detailVersion)
			{
				Detail.ShowProblem(row, $"Could not be shown: {ex.Message}");
			}
		}
	}

	/// <summary>A snapshot's crawl log, read once per page visit.</summary>
	private IReadOnlyDictionary<string, CrawlOutcome> CrawlLogOf(string snapshot)
	{
		lock (crawlLogs)
		{
			if (!crawlLogs.TryGetValue(snapshot, out var log))
			{
				crawlLogs[snapshot] = log = CrawlOutcomes.Read(snapshot);
			}

			return log;
		}
	}

	private static string SavedPath(string snapshot, string name) =>
		Path.Combine(snapshot, SnapshotComparison.DownloadFolder, name.Replace('/', Path.DirectorySeparatorChar));

	/// <summary>When the two snapshots' pruned pages were last written (a re-run writes them again).</summary>
	private static (DateTime A, DateTime B) PrunedAt(string a, string b)
	{
		static DateTime At(string snapshot)
		{
			try
			{
				// The folder's own times change when a run recreates it; the newest page's when one is written again.
				var folder = new DirectoryInfo(Path.Combine(snapshot, SnapshotComparison.PrunedFolder));
				if (!folder.Exists)
				{
					return DateTime.MinValue;
				}

				var newest = folder.CreationTimeUtc > folder.LastWriteTimeUtc ? folder.CreationTimeUtc : folder.LastWriteTimeUtc;
				foreach (var file in folder.EnumerateFiles())
				{
					if (file.LastWriteTimeUtc > newest)
					{
						newest = file.LastWriteTimeUtc;
					}
				}

				return newest;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return DateTime.MinValue;
			}
		}

		return (At(a), At(b));
	}

	/// <summary>"2026-01-02-10-00-00" → "2026-01-02 10:00:00".</summary>
	internal static string Label(string name) =>
		name.Length == 19 ? $"{name[..10]} {name[11..13]}:{name[14..16]}:{name[17..19]}" : name;
}

/// <summary>A filter chip with its count: a click lights it and shows its files; another click puts it out.</summary>
public sealed class FilterChipVm : Observable
{
	private readonly ComparisonModel owner;
	private bool isOn;
	private bool isDimmed;
	private bool isVisible = true;
	private int count;

	public FilterChipVm(string name, string style, ComparisonModel owner, IReadOnlySet<string>? covers = null)
	{
		Name = name;
		Style = style;
		this.owner = owner;
		Covers = covers;
	}

	public string Name { get; }

	/// <summary>A type chip: the file kinds it stands for ("other" stands for several).</summary>
	public IReadOnlySet<string>? Covers { get; }

	/// <summary>Shown: a type chip with no file among the files the other chips let through is hidden, unless lit.</summary>
	public bool IsVisible { get => isVisible; private set => Set(ref isVisible, value); }

	internal void SetVisible(bool value) => IsVisible = value;

	/// <summary>The pill style's name, after "Hue.".</summary>
	public string Style { get; }

	public string Text => $"{Name}  {Format.Count(count)}";

	public bool IsOn
	{
		get => isOn;
		set
		{
			if (Set(ref isOn, value))
			{
				owner.Filter();
			}
		}
	}

	/// <summary>Another chip of its group is lit and this one is not: its files are not shown.</summary>
	public bool IsDimmed { get => isDimmed; private set => Set(ref isDimmed, value); }

	internal void SetDimmed(bool value) => IsDimmed = value;

	internal void SetCount(int newCount)
	{
		count = newCount;
		Raise(nameof(Text));
	}

	internal void Reset()
	{
		isOn = false;
		Raise(nameof(IsOn));
	}
}

/// <summary>A listed file: its state, kind, address and sizes.</summary>
public sealed class FileRowVm
{
	public FileRowVm(ComparedFile file, bool isReferencePage = false)
	{
		File = file;
		IsReferencePage = isReferencePage && file.IsPage;
	}

	public ComparedFile File { get; }

	/// <summary>A reference page of a boilerplate group: its template is checked there and kept whole, so it is compared whole.</summary>
	public bool IsReferencePage { get; }

	public string State => File.State switch
	{
		CompareState.Differs => "Differs",
		CompareState.OnlyInA => "Only in A",
		CompareState.OnlyInB => "Only in B",
		_ => "Identical",
	};

	/// <summary>The pill style's name, after "Hue.".</summary>
	public string StateStyle => File.State switch
	{
		CompareState.Differs => "Pill.Warn",
		CompareState.OnlyInA => "Pill.Error",
		_ => "Pill.Ok",
	};

	public string Kind => IsReferencePage ? "Reference page" : File.IsPage ? "Page" : KindOf(File.Name);

	/// <summary>The address it was saved from; the file name when no index names it.</summary>
	public string Address => File.Url ?? File.Name;

	public string Name => File.Name;

	public string SizeA => File.State == CompareState.OnlyInB ? "–" : Format.Bytes(File.BytesA);

	public string SizeB => File.State == CompareState.OnlyInA ? "–" : Format.Bytes(File.BytesB);

	/// <summary>Sorts the size columns by bytes.</summary>
	public long BytesA => File.BytesA;

	public long BytesB => File.BytesB;

	public string Note => File.Note;

	private static string KindOf(string name)
	{
		var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
		return extension.Length == 0 ? "File" : extension;
	}
}
