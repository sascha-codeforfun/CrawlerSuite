using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Host;

namespace CrawlerSuite.Run;

/// <summary>Where a phase or a step of the run stands.</summary>
public enum RunItemStatus
{
	/// <summary>Still to come (shown dimmed).</summary>
	Ahead,
	Running,
	Done,
	/// <summary>Not part of this run (a replay has no crawl; a check switched off).</summary>
	Skipped,
	/// <summary>The run ended while this was running (cancelled, halted or failed).</summary>
	Stopped,
}

/// <summary>One step of a phase, as listed in the run overview.</summary>
public sealed class PlanStepItem : Observable
{
	private RunItemStatus status;
	private int? findings;
	private bool canOpen;

	public PlanStepItem(string label, RunItemStatus status = RunItemStatus.Ahead)
	{
		Label = label;
		this.status = status;
		Target = StepFindings.TargetOf(label);
		OpenTarget = StepFindings.OpenTargetOf(label);
	}

	private StepFacts? facts;

	/// <summary>Where the chip opens on Results (its own tab, or the tab with its data); null when it opens no tab.</summary>
	public FindingsTarget? OpenTarget { get; }

	/// <summary>The sitemap's chip opens Output files, where the sitemap is.</summary>
	public bool OpensOutputFiles => Label == RunSteps.Sitemap;

	/// <summary>The chip opens something.</summary>
	public bool Opens => OpenTarget is not null || OpensOutputFiles;

	/// <summary>A fact for the overview (redirect kinds, eligible pages …): the chip shows it, neutral, with its overlay.</summary>
	public StepFacts? Facts
	{
		get => facts;
		set
		{
			if (Set(ref facts, value))
			{
				RaiseChip();
			}
		}
	}

	/// <summary>Information, not findings: a neutral chip.</summary>
	public bool IsInfo => facts is { IsFinding: false };

	/// <summary>Facts that break findings down (broken links by reach): the chip keeps its findings colour and count.</summary>
	private bool FactsAreFindings => facts is null || facts.IsFinding;

	/// <summary>Broken links: red while there are any — a 404 is never a matter of taste.</summary>
	public bool IsError => FactsAreFindings && findings is > 0 && Label == RunSteps.BrokenLinks;

	public string OverlayHeading => facts?.Heading ?? string.Empty;

	/// <summary>Facts carry their own heading, also when they break findings down.</summary>
	public bool HasOverlayHeading => facts is not null;

	public IReadOnlyList<string> OverlayLines => facts?.Lines ?? [];

	public string OverlayNote => facts?.Note ?? string.Empty;

	public bool HasOverlayNote => OverlayNote.Length > 0;

	/// <summary>The overlay's last line: what a click does.</summary>
	public string OpenHint => OpenTarget is { } t
		? "Click: Results › " + t.Tab + (t.SubTab is null ? string.Empty : " › " + t.SubTab)
		: OpensOutputFiles ? "Click: Output files" : string.Empty;

	public bool HasOpenHint => OpenHint.Length > 0;

	private void RaiseChip()
	{
		Raise(nameof(HasChip));
		Raise(nameof(HasFindings));
		Raise(nameof(IsInfo));
		Raise(nameof(IsError));
		Raise(nameof(ChipText));
		Raise(nameof(ChipToolTip));
		Raise(nameof(OverlayHeading));
		Raise(nameof(HasOverlayHeading));
		Raise(nameof(OverlayLines));
		Raise(nameof(OverlayNote));
		Raise(nameof(HasOverlayNote));
	}

	public string Label { get; }

	public RunItemStatus Status { get => status; set => Set(ref status, value); }

	/// <summary>The Results tab this step feeds; null for a step without findings of its own.</summary>
	public FindingsTarget? Target { get; }

	/// <summary>The rows on <see cref="Target"/>, counted by the Results page's rule; null until known.</summary>
	public int? Findings
	{
		get => findings;
		set
		{
			if (Set(ref findings, value))
			{
				RaiseChip();
			}
		}
	}

	public bool HasChip => findings is not null || IsInfo;

	/// <summary>Findings above zero: the chip is amber (red for broken links; neutral for a fact).</summary>
	public bool HasFindings => FactsAreFindings && findings is > 0 && !IsError;

	public string ChipText => IsInfo ? facts!.Chip : findings is { } n ? (n == 0 ? "none" : Format.Count(n)) : string.Empty;

	/// <summary>True once the run's results are loaded: the chip opens its Results tab.</summary>
	public bool CanOpen { get => canOpen; set => Set(ref canOpen, value); }

	/// <summary>The chip's name for assistive technology (the overlay is its visual tooltip).</summary>
	public string ChipToolTip => IsInfo ? $"{Label}: {facts!.Heading}"
		: Target is null ? string.Empty
		: $"Findings on Results › {Target.Tab}" + (Target.SubTab is null ? string.Empty : $" › {Target.SubTab}");
}

/// <summary>One phase of the run in the phase strip (Connectivity, Crawl, Prepare, Analysis …).</summary>
public sealed class PhaseItem : Observable
{
	private RunItemStatus status;
	private string skipReason;

	public PhaseItem(string title, IEnumerable<string> steps, RunItemStatus status = RunItemStatus.Ahead, string skipReason = "")
	{
		Title = title;
		this.status = status;
		this.skipReason = skipReason;
		foreach (var step in steps)
		{
			Steps.Add(new PlanStepItem(step));
		}
	}

	public string Title { get; }

	public ObservableCollection<PlanStepItem> Steps { get; } = [];

	/// <summary>A phase without steps ("Complete") is left out of the overview, unless it is marked skipped.</summary>
	public bool ShowsInOverview => Steps.Count > 0 || status == RunItemStatus.Skipped;

	public DateTime StartedAt { get; set; }

	public RunItemStatus Status
	{
		get => status;
		set
		{
			if (Set(ref status, value))
			{
				Raise(nameof(StateText));
				Raise(nameof(ToolTipText));
				Raise(nameof(ShowsInOverview));
			}
		}
	}

	/// <summary>Why the phase is skipped ("replay of a snapshot"); shown as the tooltip.</summary>
	public string SkipReason
	{
		get => skipReason;
		set
		{
			if (Set(ref skipReason, value))
			{
				Raise(nameof(ToolTipText));
			}
		}
	}

	/// <summary>The word after the title in the strip; nothing while the phase is ahead.</summary>
	public string StateText => status switch
	{
		RunItemStatus.Running => " — running",
		RunItemStatus.Done => " — done",
		RunItemStatus.Skipped => " — skipped",
		RunItemStatus.Stopped => " — stopped",
		_ => string.Empty,
	};

	public string? ToolTipText => status switch
	{
		RunItemStatus.Skipped when skipReason.Length > 0 => $"Skipped: {skipReason}",
		RunItemStatus.Ahead => "Still to come",
		_ => null,
	};
}

/// <summary>A finished step: "Sitemap — 123 entries".</summary>
public sealed record StepItem(string Phase, string Label, string Detail, bool Dimmed, string ElapsedText, TimeSpan? Elapsed, StepCost? Cost = null, StepWork? Work = null)
{
	/// <summary>What the step worked through ("1,234 files · 123 MB · …"); empty when it counted nothing.</summary>
	public string WorkText => global::CrawlerSuite.Core.Host.WorkText.Compact(Work);
}

/// <summary>A message or notice from the engine.</summary>
public sealed record MessageItem(MessageTone Tone, string Heading, string Text)
{
	public bool HasHeading => Heading.Length > 0;

	public bool HasText => Text.Length > 0;

	/// <summary>The word shown in the tone pill (status colour always paired with a word).</summary>
	public string ToneWord => Tone switch
	{
		MessageTone.Success => "Done",
		MessageTone.Warning => "Warning",
		MessageTone.ActionRequired => "Action",
		MessageTone.Error => "Error",
		MessageTone.Skipped => "Skipped",
		_ => "Info",
	};
}

/// <summary>A configuration check that halted a step (Problem / Why / Fix).</summary>
public sealed record ConfigCheckItem(string Subtitle, IReadOnlyList<CheckBlock> Blocks);

/// <summary>
/// Everything the Run page shows while and after a run. Updated on the UI thread only
/// (<see cref="WpfRunHost"/> marshals engine callbacks here).
/// </summary>
public sealed class RunState : Observable
{
	private readonly System.Windows.Threading.DispatcherTimer clock;
	private readonly System.Windows.Threading.DispatcherTimer afterRun;
	private MemoryReading? afterRunMemory;
	private bool isRunning;
	private bool hasPlan;
	private bool canOpenFindings;
	private StepWork? workTotals;
	private string statusText = "Ready";
	private PhaseItem? current;

	public RunState()
	{
		// Ticks while a run is on, so the times in the progress card move between reports.
		clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		clock.Tick += (_, _) => Card.Tick();

		// One reading a minute after a run ends: what the app holds while it waits for the next
		// run — memory the host of a virtual machine cannot give to anyone else meanwhile.
		afterRun = new System.Windows.Threading.DispatcherTimer { Interval = AfterRunDelay };
		afterRun.Tick += (_, _) =>
		{
			afterRun.Stop();
			AfterRunMemory = MemoryReading.Now();
		};
	}

	/// <summary>How long after a run ends the memory the app holds is read.</summary>
	public static readonly TimeSpan AfterRunDelay = TimeSpan.FromMinutes(1);

	/// <summary>The memory reading taken a minute after the last run ended; null before it (or while a run goes on).</summary>
	public MemoryReading? AfterRunMemory
	{
		get => afterRunMemory;
		private set
		{
			if (Set(ref afterRunMemory, value))
			{
				Raise(nameof(AfterRunMemoryText));
			}
		}
	}

	/// <summary>"Memory held a minute after the run: 1234 MB · after clean-up 567 MB"; empty until read.</summary>
	public string AfterRunMemoryText => afterRunMemory is { } m
		? $"Memory held a minute after the run: {m.HeldBytes / 1048576.0:0} MB · after clean-up {m.AfterCleanUpBytes / 1048576.0:0} MB"
		: string.Empty;

	public ObservableCollection<PhaseItem> Phases { get; } = [];

	/// <summary>The wall clocks in the window header (Crawl, Analysis, Total).</summary>
	public RunClocks Clocks { get; } = new();

	public ObservableCollection<StepItem> Steps { get; } = [];

	public ObservableCollection<MessageItem> Messages { get; } = [];

	public ObservableCollection<ConfigCheckItem> ConfigChecks { get; } = [];

	public ProgressState Progress { get; } = new();

	/// <summary>The card for the running phase: its current step, the step's progress and the phase's steps.</summary>
	public StepCardState Card { get; } = new();

	public bool IsRunning
	{
		get => isRunning;
		set
		{
			if (Set(ref isRunning, value))
			{
				if (value)
				{
					clock.Start();
					afterRun.Stop();
					AfterRunMemory = null;
				}
				else
				{
					clock.Stop();
					afterRun.Start();
				}

				Card.Visible = value && current is not null;
			}
		}
	}

	public string StatusText { get => statusText; set => Set(ref statusText, value); }

	/// <summary>True once the run has sent its plan: the run overview is shown (and stays after the run).</summary>
	public bool HasPlan { get => hasPlan; private set => Set(ref hasPlan, value); }

	/// <summary>The run's work totals, sent when the analysis has finished; null before that.</summary>
	public StepWork? WorkTotals
	{
		get => workTotals;
		set
		{
			if (Set(ref workTotals, value))
			{
				Raise(nameof(WorkSummary));
			}
		}
	}

	/// <summary>"Read 1234 MB in 4,567 files · …" for the top of the run overview; empty until the analysis has finished.</summary>
	public string WorkSummary => global::CrawlerSuite.Core.Host.WorkText.Summary(workTotals);

	/// <summary>True once the finished run's results are loaded: the findings chips open their Results tab.</summary>
	public bool CanOpenFindings
	{
		get => canOpenFindings;
		set
		{
			if (Set(ref canOpenFindings, value))
			{
				foreach (var step in Phases.SelectMany(p => p.Steps))
				{
					step.CanOpen = value && step.HasChip && step.Opens;
				}
			}
		}
	}

	/// <summary>A finished step's facts: the chip turns neutral and shows them, with the overlay.</summary>
	public void SetStepFacts(string label, StepFacts facts)
	{
		var step = current?.Steps.FirstOrDefault(s => s.Label == label)
			?? Phases.SelectMany(p => p.Steps).FirstOrDefault(s => s.Label == label);
		if (step is not null)
		{
			step.Facts = facts;
			step.CanOpen = canOpenFindings && step.Opens;
		}
	}

	/// <summary>A finished step's findings count, as the engine read it from the snapshot.</summary>
	public void SetStepFindings(string label, int count)
	{
		var step = current?.Steps.FirstOrDefault(s => s.Label == label)
			?? Phases.SelectMany(p => p.Steps).FirstOrDefault(s => s.Label == label && s.Target is not null);
		if (step is not null)
		{
			step.Findings = count;
		}
	}

	/// <summary>
	/// After the run: every finished step takes its count from the findings as the Results page
	/// read them, so the overview and the tabs agree to the row (the extra download adds to the
	/// crawl's failures after the main crawl was counted, for example).
	/// </summary>
	public void ApplyFindingCounts(IReadOnlyList<FindingNode> tabs)
	{
		foreach (var step in Phases.SelectMany(p => p.Steps))
		{
			if (step.Target is { } target && step.Status == RunItemStatus.Done)
			{
				step.Findings = StepFindings.CountIn(tabs, target);
			}
		}
	}

	public void Reset()
	{
		Phases.Clear();
		Steps.Clear();
		Messages.Clear();
		ConfigChecks.Clear();
		Progress.Clear();
		current = null;
		Card.Clear();
		HasPlan = false;
		CanOpenFindings = false;
		WorkTotals = null;
	}

	/// <summary>
	/// Shows the phases the run will go through. Sent again when the plan changes: phases that
	/// have started keep their state; phases entered outside the plan stay where they are.
	/// </summary>
	public void ApplyPlan(IReadOnlyList<PlannedPhase> plan)
	{
		var started = Phases.Where(p => p.Status != RunItemStatus.Ahead && p.Status != RunItemStatus.Skipped)
			.ToDictionary(p => p.Title, StringComparer.Ordinal);

		// A crawl stopped at the download limit and started over sends its plan again from the
		// top: the strip starts afresh (the finished steps and messages stay below).
		bool restart = plan.Count > 0 && current is not null && current.Title != plan[0].Title
			&& started.ContainsKey(plan[0].Title);
		if (restart)
		{
			current = null;
			started.Clear();
			Card.Clear();
			Card.Visible = false;
			Progress.Clear();
		}
		var planned = new HashSet<string>(plan.Select(p => p.Title), StringComparer.Ordinal);
		var next = new List<PhaseItem>();
		foreach (var phase in plan)
		{
			next.Add(started.TryGetValue(phase.Title, out var kept)
				? kept
				: new PhaseItem(phase.Title, phase.Steps,
					phase.Skipped ? RunItemStatus.Skipped : RunItemStatus.Ahead, phase.SkipReason));
		}

		// A started phase the new plan does not name (an unplanned one) goes after the last
		// started phase before it, so the strip keeps the order the run went through.
		foreach (var extra in started.Values.Where(p => !planned.Contains(p.Title)))
		{
			next.Insert(Math.Min(next.Count, Phases.IndexOf(extra)), extra);
		}

		Phases.Clear();
		foreach (var p in next)
		{
			Phases.Add(p);
		}

		HasPlan = Phases.Count > 0;
	}

	public void EnterPhase(string title)
	{
		Clocks.EnterPhase(title);
		var previous = current;
		if (previous is not null)
		{
			Close(previous, RunItemStatus.Done);
		}

		var phase = Phases.FirstOrDefault(p => p.Title == title);
		if (phase is null)
		{
			// Not in the plan ("Stopped"): placed right after the phase that ran before it.
			phase = new PhaseItem(title, []);
			var at = previous is null ? Phases.Count : Phases.IndexOf(previous) + 1;
			Phases.Insert(at, phase);
		}

		// Phases of the plan passed over on the way here did not run in this run.
		var index = Phases.IndexOf(phase);
		for (int i = 0; i < index; i++)
		{
			if (Phases[i].Status == RunItemStatus.Ahead)
			{
				Phases[i].Status = RunItemStatus.Skipped;
				if (Phases[i].SkipReason.Length == 0)
				{
					Phases[i].SkipReason = "not needed in this run";
				}
			}
		}

		phase.Status = RunItemStatus.Running;
		phase.StartedAt = DateTime.UtcNow;
		current = phase;
		StatusText = title;
		Card.Enter(phase);
		Card.Visible = isRunning;
		ShowDownload();
	}

	/// <summary>A step of the running phase starts.</summary>
	public void StartStep(int id, string label)
	{
		if (current is null)
		{
			return;
		}

		var steps = current.Steps;
		var running = steps.FirstOrDefault(s => s.Status == RunItemStatus.Running);
		var from = running is null ? 0 : steps.IndexOf(running) + 1;
		if (running is not null)
		{
			running.Status = RunItemStatus.Done;
		}

		PlanStepItem? step = null;
		for (int i = from; i < steps.Count; i++)
		{
			if (steps[i].Status == RunItemStatus.Ahead && steps[i].Label == label)
			{
				step = steps[i];
				break;
			}
		}

		if (step is null)
		{
			// A step the plan did not list: shown where it runs.
			step = new PlanStepItem(label);
			steps.Insert(from, step);
		}

		// Steps of the plan passed over did not run (a check found nothing to do).
		for (int i = 0; i < steps.IndexOf(step); i++)
		{
			if (steps[i].Status == RunItemStatus.Ahead)
			{
				steps[i].Status = RunItemStatus.Skipped;
			}
		}

		step.Status = RunItemStatus.Running;
		Card.StartStep(id, step, steps.IndexOf(step) + 1, steps.Count);
	}

	public void StepProgress(int id, int done, int total) => Card.Report(id, done, total);

	public void UpdateProgress(CrawlProgress progress)
	{
		Progress.Update(progress);
		ShowDownload();
	}

	/// <summary>The download counters show in the card while their download's phase runs.</summary>
	private void ShowDownload() =>
		Progress.Visible = Progress.Last is { } last && current is not null
			&& (current.Title == RunPhases.Crawl && last == DownloadPhase.MainCrawl
				|| current.Title == RunPhases.ExtraDownload && last == DownloadPhase.ExtraDownload);

	/// <summary>The run has ended: the running phase is done when the run completed, stopped otherwise.</summary>
	public void FinishPhases(bool completed)
	{
		Clocks.Finish();
		if (current is not null)
		{
			Close(current, completed ? RunItemStatus.Done : RunItemStatus.Stopped);
		}

		current = null;
		Card.Visible = false;
		Progress.Visible = false;
	}

	private static void Close(PhaseItem phase, RunItemStatus status)
	{
		phase.Status = status;
		foreach (var step in phase.Steps)
		{
			if (step.Status == RunItemStatus.Running)
			{
				step.Status = status;
			}
			else if (step.Status == RunItemStatus.Ahead && status == RunItemStatus.Done)
			{
				step.Status = RunItemStatus.Skipped;
			}
		}
	}

	public void AddStep(StepRow row) =>
		Steps.Add(new StepItem(row.Phase, row.Label, row.Detail, row.Dimmed,
			row.Elapsed is { TotalSeconds: >= 0.5 } e ? $"{e.TotalSeconds:0.0} s" : string.Empty, row.Elapsed, row.Cost, row.Work));

	/// <summary>
	/// The steps table as plain text for sharing with an AI: always masked at the strictest level,
	/// whatever the screen shows; every step with its time; version, run type, processors, runtime.
	/// No user, machine name or path.
	/// </summary>
	public string StepsForAi(string version)
	{
		var crawl = Phases.FirstOrDefault(p => p.Title == RunPhases.Crawl);
		var runType = crawl is { Status: RunItemStatus.Skipped } ? "replay" : crawl is null ? "unknown" : "crawl";
		var nl = Environment.NewLine;
		var sb = new System.Text.StringBuilder();
		sb.Append($"CrawlerSuite {version} · run steps · {runType} · {DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}").Append(nl);
		AppendInProgress(sb, nl);
		sb.Append($"Machine: {Environment.ProcessorCount} logical processors · .NET {Environment.Version}").Append(nl);
		// The memory manager this process runs with (fixed at start; Settings → test levers can restart with another).
		sb.Append($"Memory manager: {MemorySetup.InEffect().Describe()} · {System.Runtime.GCSettings.LatencyMode}").Append(nl);
		var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
		var peak = Steps.Where(s => s.Cost is not null).Select(s => s.Cost!.PeakMemoryBytes).DefaultIfEmpty(0).Max();
		sb.Append($"Memory available to the app: {available / 1073741824.0:0.0} GB · peak held in this run: {peak / 1048576.0:0} MB").Append(nl);
		var afterCleanUp = Steps.Where(s => s.Cost is not null).Select(s => s.Cost!.AfterCleanUpBytes).DefaultIfEmpty(0).Max();
		sb.Append($"Most in use after a clean-up: {afterCleanUp / 1048576.0:0} MB (an upper bound for what the work needs)").Append(nl);
		if (afterRunMemory is { } after)
		{
			sb.Append($"A minute after the run: held {after.HeldBytes / 1048576.0:0} MB · after clean-up {after.AfterCleanUpBytes / 1048576.0:0} MB").Append(nl);
		}
		else if (!IsRunning)
		{
			sb.Append("A minute after the run: not read yet (copy again a minute after the run ends)").Append(nl);
		}

		var processPeak = Steps.Where(s => s.Cost is not null).Select(s => s.Cost!.ProcessPeakBytes).DefaultIfEmpty(0).Max();
		if (processPeak > 0)
		{
			// First run of the process only: Windows' peak covers the process since it started, which
			// here is this run (plus starting the app), so it should sit at or a little above the sampled one.
			sb.Append($"Check, first run of this process: Windows' peak for the process {processPeak / 1048576.0:0} MB").Append(nl);
		}
		var total = Steps.Where(s => s.Elapsed is not null).Aggregate(TimeSpan.Zero, (t, s) => t + s.Elapsed!.Value);
		sb.Append($"Steps: {Steps.Count} · sum of step times {total.TotalSeconds:0.0} s").Append(nl);

		// The header's wall clocks, as the screen shows them: a time quoted from the screen can be
		// found in the copy. Debug mode's archive and compare are not counted there; their time is
		// named apart, so a step sum above the clock is explained.
		if (Clocks.Visible)
		{
			var debug = Steps.Where(s => s.Phase == RunPhases.Debug && s.Elapsed is not null).Aggregate(TimeSpan.Zero, (t, s) => t + s.Elapsed!.Value);
			var wall = $"Wall clock: {Clocks.CrawlText} · {Clocks.AnalysisText} · {Clocks.TotalText}";
			sb.Append(debug > TimeSpan.Zero ? $"{wall} · Debug (not counted) {debug.TotalSeconds:0.0} s" : wall).Append(nl);
		}

		sb.Append(nl);
		var pause = Steps.Where(s => s.Cost is not null).Aggregate(TimeSpan.Zero, (t, s) => t + s.Cost!.Pause);
		sb.Append($"Paused for memory clean-up: {pause.TotalSeconds:0.0} s in total").Append(nl);
		if (WorkSummary.Length > 0)
		{
			sb.Append($"Work done: {WorkSummary}").Append(nl);
		}

		sb.Append(nl);
		sb.Append("| Phase | Step | Result | Time | Volume | Paused | Clean-ups gen 0/1/2 | Allocated | Peak held | After clean-up |").Append(nl);
		sb.Append("|---|---|---|---:|---|---:|---:|---:|---:|---:|").Append(nl);
		foreach (var s in Steps)
		{
			var time = s.Elapsed is { } e ? $"{e.TotalSeconds:0.00} s" : "–";
			var (paused, counts, allocated, peakHeld, afterCleanUpStep) = s.Cost is { } c
				? ($"{c.Pause.TotalSeconds:0.00} s", $"{c.Gen0}/{c.Gen1}/{c.Gen2}", $"{c.AllocatedBytes / 1048576.0:0} MB",
					c.PeakMemoryBytes > 0 ? $"{c.PeakMemoryBytes / 1048576.0:0} MB" : "–",
					c.AfterCleanUpBytes > 0 ? $"{c.AfterCleanUpBytes / 1048576.0:0} MB" : "–")
				: ("–", "–", "–", "–", "–");
			var volume = s.WorkText.Length > 0 ? s.WorkText : "–";
			sb.Append($"| {Cell(s.Phase)} | {Cell(s.Label)} | {Cell(s.Detail)} | {time} | {volume} | {paused} | {counts} | {allocated} | {peakHeld} | {afterCleanUpStep} |").Append(nl);
		}

		return sb.ToString();
	}

	/// <summary>A copy taken while the run goes on says so at the top: its numbers are not final.</summary>
	private void AppendInProgress(System.Text.StringBuilder sb, string nl)
	{
		if (IsRunning)
		{
			var done = Steps.Count(s => s.Elapsed is not null);
			sb.Append($"IN PROGRESS — the run is not finished: {done} {(done == 1 ? "step" : "steps")} done so far; numbers below are not final.").Append(nl);
		}
	}

	/// <summary>A table cell for a copy: masked at the strictest level, on one line, no column separators.</summary>
	private static string Cell(string text) =>
		Mask.ApplyStrict(text).Replace("|", "/", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

	/// <summary>
	/// The run overview as plain text for sharing with an AI: every phase and step with its state
	/// and, for a step that feeds a Results tab, that tab's count (the number on the chip). Masked
	/// at the strictest level, like <see cref="StepsForAi"/>.
	/// </summary>
	public string OverviewForAi(string version)
	{
		var crawl = Phases.FirstOrDefault(p => p.Title == RunPhases.Crawl);
		var runType = crawl is { Status: RunItemStatus.Skipped } ? "replay" : crawl is null ? "unknown" : "crawl";
		var nl = Environment.NewLine;
		var sb = new System.Text.StringBuilder();
		sb.Append($"CrawlerSuite {version} · run overview · {runType} · {DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}").Append(nl);
		AppendInProgress(sb, nl);
		if (WorkSummary.Length > 0)
		{
			sb.Append($"Work done: {WorkSummary}").Append(nl);
		}

		sb.Append("Findings: the rows of the Results tab the step feeds (the number on its chip); \"none\" = 0, \"–\" = the step has no tab of its own.").Append(nl).Append(nl);
		sb.Append("| Phase | Step | State | Findings | Results tab |").Append(nl);
		sb.Append("|---|---|---|---:|---|").Append(nl);
		static string StateOf(RunItemStatus status) => status.ToString().ToLowerInvariant();
		foreach (var phase in Phases.Where(p => p.ShowsInOverview))
		{
			if (phase.Steps.Count == 0)
			{
				var why = phase.SkipReason.Length > 0 ? $" ({phase.SkipReason})" : string.Empty;
				sb.Append($"| {Cell(phase.Title)} | – | {StateOf(phase.Status)}{Cell(why)} | – | – |").Append(nl);
				continue;
			}

			foreach (var step in phase.Steps)
			{
				// Facts that break findings down (broken links by reach) keep the findings count.
				var findings = step.Facts is { IsFinding: false } f ? $"{f.Chip} (information)"
					: step.Findings is { } n ? (n == 0 ? "none" : n.ToString(System.Globalization.CultureInfo.InvariantCulture)) : "–";
				var tab = step.Target is { } t ? t.Tab + (t.SubTab is null ? string.Empty : " › " + t.SubTab) : "–";
				sb.Append($"| {Cell(phase.Title)} | {Cell(step.Label)} | {StateOf(step.Status)} | {findings} | {Cell(tab)} |").Append(nl);
			}
		}

		return sb.ToString();
	}
}

/// <summary>
/// The progress card for the running phase: "Step 4 of 9 · Content quality", a bar by file
/// count (or a moving bar when the step has no file count), the times, and the phase's steps.
/// </summary>
public sealed class StepCardState : Observable
{
	private bool visible;
	private string title = string.Empty;
	private string subText = string.Empty;
	private bool hasTotal;
	private double percent;
	private string countText = string.Empty;
	private string timeText = string.Empty;
	private ObservableCollection<PlanStepItem> steps = [];
	private PhaseItem? phase;
	private int stepId = -1;
	private DateTime stepStartedAt;

	public bool Visible { get => visible; set => Set(ref visible, value); }

	/// <summary>The running phase.</summary>
	public string Title { get => title; private set => Set(ref title, value); }

	/// <summary>"Step 4 of 9 · Content quality".</summary>
	public string SubText { get => subText; private set => Set(ref subText, value); }

	/// <summary>True when the step reports a file count; otherwise the bar just moves.</summary>
	public bool HasTotal { get => hasTotal; private set => Set(ref hasTotal, value); }

	public double Percent { get => percent; private set => Set(ref percent, value); }

	/// <summary>"1 234 of 4 567 files", or "Working …" without a count.</summary>
	public string CountText { get => countText; private set => Set(ref countText, value); }

	/// <summary>"0:38 in this step · 2:51 in Analysis".</summary>
	public string TimeText { get => timeText; private set => Set(ref timeText, value); }

	/// <summary>Every step of the running phase with its state.</summary>
	public ObservableCollection<PlanStepItem> Steps { get => steps; private set => Set(ref steps, value); }

	public bool HasSteps => steps.Count > 0;

	public void Clear()
	{
		phase = null;
		stepId = -1;
		Visible = false;
		Title = string.Empty;
		SubText = string.Empty;
		Steps = [];
		Raise(nameof(HasSteps));
		Idle();
	}

	public void Enter(PhaseItem entered)
	{
		phase = entered;
		stepId = -1;
		stepStartedAt = DateTime.UtcNow;
		Title = entered.Title;
		SubText = string.Empty;
		Steps = entered.Steps;
		Raise(nameof(HasSteps));
		Idle();
		Tick();
	}

	public void StartStep(int id, PlanStepItem step, int number, int count)
	{
		stepId = id;
		stepStartedAt = DateTime.UtcNow;
		SubText = $"Step {number} of {count} · {step.Label}";
		Raise(nameof(HasSteps));
		Idle();
		Tick();
	}

	public void Report(int id, int done, int total)
	{
		if (id != stepId)
		{
			return; // a late report of a step that has already finished
		}

		HasTotal = total > 0;
		Percent = total > 0 ? Math.Min(100.0, 100.0 * done / total) : 0;
		CountText = total > 0 ? $"{Format.Count(done)} of {Format.Count(total)} files" : $"{Format.Count(done)} files";
	}

	/// <summary>Refreshes the times (called every second while a run is on).</summary>
	public void Tick()
	{
		if (phase is null)
		{
			TimeText = string.Empty;
			return;
		}

		var now = DateTime.UtcNow;
		var inPhase = $"{Format.Duration(now - phase.StartedAt)} in {phase.Title}";
		TimeText = stepId < 0 ? inPhase : $"{Format.Duration(now - stepStartedAt)} in this step · {inPhase}";
	}

	private void Idle()
	{
		HasTotal = false;
		Percent = 0;
		CountText = "Working …";
	}
}

/// <summary>The live download counters and the bars derived from them.</summary>
public sealed class ProgressState : Observable
{
	/// <summary>"Taking longer than usual" starts once the crawl is this far past the last one.</summary>
	public const double OverUsualFactor = 1.10;

	private bool visible;
	private string title = string.Empty;
	private string pages = "0";
	private string assets = "0";
	private string queued = "0";
	private string active = "0";
	private string stalled = "0";
	private string errors = "0";
	private string redirects = "0";
	private string oversized = "0";
	private string toRetry = "0";
	private string errorKinds = string.Empty;
	private bool isPaused;
	private string pauseText = string.Empty;
	private string downloaded = "0 B";
	private string speed = "–";
	private string elapsed = "0:00";
	private bool hasExpected;
	private double expectedPercent;
	private string expectedText = string.Empty;
	private string timeText = string.Empty;
	private bool isOverUsual;
	private string overUsualText = string.Empty;
	private bool hasLimit;
	private double limitPercent;
	private string limitText = string.Empty;
	private bool hasSplit;
	private CrawlProgress? mainFinal;
	private string pagesSplit = string.Empty;
	private string assetsSplit = string.Empty;
	private string stalledSplit = string.Empty;
	private string errorsSplit = string.Empty;
	private string redirectsSplit = string.Empty;
	private string oversizedSplit = string.Empty;
	private string downloadedSplit = string.Empty;

	/// <summary>Shown in the progress card while the phase of the last reported download runs (set by <see cref="RunState"/>).</summary>
	public bool Visible { get => visible; set => Set(ref visible, value); }

	/// <summary>Which download the last report came from; null before the first.</summary>
	public DownloadPhase? Last { get; private set; }
	public string Title { get => title; private set => Set(ref title, value); }
	public string Pages { get => pages; private set => Set(ref pages, value); }
	public string Assets { get => assets; private set => Set(ref assets, value); }
	public string Queued { get => queued; private set => Set(ref queued, value); }
	public string Active { get => active; private set => Set(ref active, value); }
	public string Stalled { get => stalled; private set => Set(ref stalled, value); }
	public string Errors { get => errors; private set => Set(ref errors, value); }
	public string Redirects { get => redirects; private set => Set(ref redirects, value); }
	public string Oversized { get => oversized; private set => Set(ref oversized, value); }

	/// <summary>The errors by kind, in a line ("404 · 12 · 5xx · 3"); empty while there are none.</summary>
	public string ErrorKinds { get => errorKinds; private set => Set(ref errorKinds, value); }

	/// <summary>Downloads that failed for a passing reason, waiting for (or in) the retry round.</summary>
	public string ToRetry { get => toRetry; private set => Set(ref toRetry, value); }

	/// <summary>New requests are held: a server stopped answering properly.</summary>
	public bool IsPaused { get => isPaused; private set => Set(ref isPaused, value); }

	public string PauseText { get => pauseText; private set => Set(ref pauseText, value); }
	public string Downloaded { get => downloaded; private set => Set(ref downloaded, value); }
	public string Speed { get => speed; private set => Set(ref speed, value); }
	public string Elapsed { get => elapsed; private set => Set(ref elapsed, value); }

	/// <summary>True when there is a real or expected total to draw a bar against.</summary>
	public bool HasExpected { get => hasExpected; private set => Set(ref hasExpected, value); }
	public double ExpectedPercent { get => expectedPercent; private set => Set(ref expectedPercent, value); }
	public string ExpectedText { get => expectedText; private set => Set(ref expectedText, value); }
	public string TimeText { get => timeText; private set => Set(ref timeText, value); }

	/// <summary>Files or time more than 10 % over the previous crawl of the site.</summary>
	public bool IsOverUsual { get => isOverUsual; private set => Set(ref isOverUsual, value); }
	public string OverUsualText { get => overUsualText; private set => Set(ref overUsualText, value); }

	public bool HasLimit { get => hasLimit; private set => Set(ref hasLimit, value); }
	public double LimitPercent { get => limitPercent; private set => Set(ref limitPercent, value); }
	public string LimitText { get => limitText; private set => Set(ref limitText, value); }

	/// <summary>True once the extra download runs: the tiles then show totals with "main · extra" beneath.</summary>
	public bool HasSplit { get => hasSplit; private set => Set(ref hasSplit, value); }
	public string PagesSplit { get => pagesSplit; private set => Set(ref pagesSplit, value); }
	public string AssetsSplit { get => assetsSplit; private set => Set(ref assetsSplit, value); }
	public string StalledSplit { get => stalledSplit; private set => Set(ref stalledSplit, value); }
	public string ErrorsSplit { get => errorsSplit; private set => Set(ref errorsSplit, value); }
	public string RedirectsSplit { get => redirectsSplit; private set => Set(ref redirectsSplit, value); }
	public string OversizedSplit { get => oversizedSplit; private set => Set(ref oversizedSplit, value); }
	public string DownloadedSplit { get => downloadedSplit; private set => Set(ref downloadedSplit, value); }

	/// <summary>The tile value: the total once the extra download runs, with "main · extra" passed to <paramref name="split"/>.</summary>
	private static string Total(int? main, int current, Action<string> split)
	{
		if (main is not { } m)
		{
			split(string.Empty);
			return Format.Count(current);
		}

		split($"main {Format.Count(m)} · extra {Format.Count(current)}");
		return Format.Count(m + current);
	}

	public void Clear()
	{
		mainFinal = null;
		Last = null;
		HasSplit = false;
		Visible = false;
		HasExpected = false;
		IsOverUsual = false;
		HasLimit = false;
	}

	public void Update(CrawlProgress p)
	{
		Last = p.Phase;
		Title = (p.Phase == DownloadPhase.MainCrawl ? "Main crawl" : "Extra download (list 05)") + (p.RetryRound ? " — retry round" : string.Empty);
		ToRetry = Format.Count(p.ToRetry);
		IsPaused = p.PausedFor is { } held && held > TimeSpan.Zero;
		PauseText = p.PausedFor is { } left && left > TimeSpan.Zero
			? $"A server is not answering properly: new requests are held for {Math.Ceiling(left.TotalSeconds):0} s more (pause {p.Pauses}), then one goes out to see whether it is back. Nothing is lost: what failed is tried again in the retry round."
			: string.Empty;
		Queued = Format.Count(p.Queued);
		Active = Format.Count(p.Active);

		// The session restarts its counters for the extra download; the main crawl's last
		// report is kept, so the tiles show totals and the split instead of losing the main numbers.
		var main = p.Phase == DownloadPhase.MainCrawl ? null : mainFinal;
		if (p.Phase == DownloadPhase.MainCrawl)
		{
			mainFinal = p;
		}

		HasSplit = main is not null;
		Pages = Total(main?.PagesSaved, p.PagesSaved, s => PagesSplit = s);
		Assets = Total(main?.AssetsSaved, p.AssetsSaved, s => AssetsSplit = s);
		Stalled = Total(main?.Stalled, p.Stalled, s => StalledSplit = s);
		Errors = Total(main?.Errors, p.Errors, s => ErrorsSplit = s);
		var kinds = new List<string>();
		void Kind(string label, int? before, int now)
		{
			int n = (before ?? 0) + now;
			if (n > 0)
			{
				kinds.Add($"{label} {Format.Count(n)}");
			}
		}

		Kind("404", main?.NotFound, p.NotFound);
		Kind("4xx", main?.ClientErrors, p.ClientErrors);
		Kind("5xx", main?.ServerErrors, p.ServerErrors);
		Kind("connection", main?.ConnectionErrors, p.ConnectionErrors);
		ErrorKinds = string.Join(" · ", kinds);
		Redirects = Total(main?.Redirects, p.Redirects, s => RedirectsSplit = s);
		Oversized = Total(main?.Oversized, p.Oversized, s => OversizedSplit = s);

		// Downloaded bytes run on across both downloads.
		Downloaded = Format.Bytes(p.DownloadedBytes);
		DownloadedSplit = main is null ? string.Empty
			: $"main {Format.Bytes(main.DownloadedBytes)} · extra {Format.Bytes(Math.Max(0, p.DownloadedBytes - main.DownloadedBytes))}";
		Speed = p.BytesPerSecond > 0 ? Format.Bytes((long)p.BytesPerSecond) + "/s" : "–";
		Elapsed = Format.Duration(p.Elapsed);

		HasLimit = p.LimitBytes > 0;
		if (HasLimit)
		{
			LimitPercent = Math.Min(100.0, 100.0 * p.DownloadedBytes / p.LimitBytes);
			LimitText = $"{Format.Bytes(p.DownloadedBytes)} of the {Format.Bytes(p.LimitBytes)} total download limit";
		}

		if (p.Phase == DownloadPhase.ExtraDownload)
		{
			HasExpected = p.ListTotal > 0;
			ExpectedPercent = p.ListTotal > 0 ? 100.0 * p.ListDone / p.ListTotal : 0;
			ExpectedText = $"{Format.Count(p.ListDone)} of {Format.Count(p.ListTotal)} URLs from list 05";
			TimeText = Elapsed;
			IsOverUsual = false;
			return;
		}

		var prev = p.Previous;
		if (prev is null || prev.FileCount <= 0)
		{
			// First crawl of the site: nothing to compare with, so no bar — only counts.
			HasExpected = false;
			IsOverUsual = false;
			TimeText = Elapsed;
			return;
		}

		HasExpected = true;
		var files = p.FilesSaved;
		ExpectedPercent = Math.Min(100.0, 100.0 * files / prev.FileCount);
		ExpectedText = files <= prev.FileCount
			? $"{Format.Count(files)} of ≈{Format.Count(prev.FileCount)} files (last crawl)"
			: $"+{Format.Count(files - prev.FileCount)} files beyond last crawl";
		TimeText = prev.Duration is { } d
			? $"{Elapsed} · last crawl took {Format.Duration(d)}"
			: Elapsed;

		bool overFiles = files > prev.FileCount * OverUsualFactor;
		bool overTime = prev.Duration is { } dur && dur > TimeSpan.Zero && p.Elapsed > dur * OverUsualFactor;
		IsOverUsual = overFiles || overTime;
		OverUsualText = IsOverUsual
			? $"Taking longer than usual · {Format.Count(files)} files · last crawl {Format.Count(prev.FileCount)} files"
				+ (prev.Duration is { } pd ? $" in {Format.Duration(pd)}" : string.Empty)
			: string.Empty;
	}
}
