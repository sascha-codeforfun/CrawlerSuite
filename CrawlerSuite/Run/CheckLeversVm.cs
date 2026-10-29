using CrawlerSuite.Core.Host;
using CrawlerSuite.Core.Lexicon;

namespace CrawlerSuite.Run;

/// <summary>
/// The test levers panel (Settings → Spelling). Every change goes straight to
/// <see cref="CheckLevers.Session"/>: memory only, never saved, back to the defaults at the
/// next start. The next run takes them as they are.
/// </summary>
public sealed class CheckLeversVm : Observable
{
	private CheckLevers Levers
	{
		get => CheckLevers.Session;
		set
		{
			if (value == CheckLevers.Session)
			{
				return;
			}

			CheckLevers.Session = value;
			RaiseAll();
		}
	}

	public bool CompoundCheckClock { get => Levers.CompoundCheckClock; set => Levers = Levers with { CompoundCheckClock = value }; }

	public bool CompoundSuggestClock { get => Levers.CompoundSuggestClock; set => Levers = Levers with { CompoundSuggestClock = value }; }

	public bool SuggestStepClock { get => Levers.SuggestStepClock; set => Levers = Levers with { SuggestStepClock = value }; }

	public bool SuggestGlobalClock { get => Levers.SuggestGlobalClock; set => Levers = Levers with { SuggestGlobalClock = value }; }

	public bool CharacterMap { get => Levers.CharacterMap; set => Levers = Levers with { CharacterMap = value }; }

	public bool TurnsNone { get => Levers.TakeTurns == TakeTurns.None; set { if (value) { Levers = Levers with { TakeTurns = TakeTurns.None }; } } }

	public bool TurnsPerLanguage { get => Levers.TakeTurns == TakeTurns.PerLanguage; set { if (value) { Levers = Levers with { TakeTurns = TakeTurns.PerLanguage }; } } }

	public bool TurnsWholeProgram { get => Levers.TakeTurns == TakeTurns.WholeProgram; set { if (value) { Levers = Levers with { TakeTurns = TakeTurns.WholeProgram }; } } }

	public string CompoundCheckLabel => Label("Compound check clock", nameof(CheckLevers.CompoundCheckClock));

	public string CompoundSuggestLabel => Label("Compound suggestion clock", nameof(CheckLevers.CompoundSuggestClock));

	public string SuggestStepLabel => Label("Suggestion step clock", nameof(CheckLevers.SuggestStepClock));

	public string SuggestGlobalLabel => Label("Overall suggestion clock", nameof(CheckLevers.SuggestGlobalClock));

	public bool IsDefault => Levers.IsDefault;

	/// <summary>What the next run will use, as its run-steps row will say.</summary>
	public string Summary => Levers.Describe();

	public void Reset() => Levers = CheckLevers.Default;

	// ── Memory manager: fixed when the process starts, so a choice needs a restart ──

	private static MemorySetup Memory
	{
		get => MemorySetup.Chosen;
		set => MemorySetup.Chosen = value;
	}

	/// <summary>What this process runs with (read once: it cannot change while the process lives).</summary>
	public MemorySetup MemoryInEffect { get; } = MemorySetup.InEffect();

	public bool CollectorWorkstation { get => Memory.Collector == Collector.Workstation; set => SetCollector(value, Collector.Workstation); }

	public bool CollectorServerDatas { get => Memory.Collector == Collector.ServerDatas; set => SetCollector(value, Collector.ServerDatas); }

	public bool CollectorServerClassic { get => Memory.Collector == Collector.ServerClassic; set => SetCollector(value, Collector.ServerClassic); }

	/// <summary>The conserve-memory levels offered: 0 (the default) to 9.</summary>
	public IReadOnlyList<int> ConserveLevels { get; } = Enumerable.Range(0, MemorySetup.MaxConserveMemory + 1).ToList();

	public int ConserveMemory
	{
		get => Memory.ConserveMemory;
		set
		{
			if (value != Memory.ConserveMemory)
			{
				Memory = Memory with { ConserveMemory = Math.Clamp(value, 0, MemorySetup.MaxConserveMemory) };
				RaiseMemory();
			}
		}
	}

	/// <summary>The memory caps offered: none, or 2, 3 or 4 GB.</summary>
	public IReadOnlyList<MemoryCapOption> MemoryCaps { get; } =
		[.. MemorySetup.MemoryCaps.Select(gb => new MemoryCapOption(gb, gb == 0 ? "none" : $"{gb} GB"))];

	public int MemoryCapGigabytes
	{
		get => Memory.MemoryCapGigabytes;
		set
		{
			if (value != Memory.MemoryCapGigabytes)
			{
				Memory = Memory with { MemoryCapGigabytes = Math.Max(0, value) };
				RaiseMemory();
			}
		}
	}

	/// <summary>The chosen setup differs from the one this process runs with.</summary>
	public bool MemoryNeedsRestart => Memory != MemoryInEffect;

	public string MemoryInEffectText => $"Running now: {MemoryInEffect.Describe()}";

	public string MemoryChosenText => MemoryNeedsRestart
		? $"After a restart: {Memory.Describe()}"
		: "No restart needed: this is what the app runs with.";

	public bool MemoryIsDefault => Memory.IsDefault;

	public void ResetMemory()
	{
		Memory = MemorySetup.Default;
		RaiseMemory();
	}

	private void SetCollector(bool on, Collector collector)
	{
		if (on && Memory.Collector != collector)
		{
			Memory = Memory with { Collector = collector };
			RaiseMemory();
		}
	}

	private void RaiseMemory()
	{
		Raise(nameof(CollectorWorkstation));
		Raise(nameof(CollectorServerDatas));
		Raise(nameof(CollectorServerClassic));
		Raise(nameof(ConserveMemory));
		Raise(nameof(MemoryCapGigabytes));
		Raise(nameof(MemoryNeedsRestart));
		Raise(nameof(MemoryChosenText));
		Raise(nameof(MemoryIsDefault));
	}

	private static string Label(string name, string clock) => $"{name} (on = {CheckLevers.LibraryMilliseconds(clock)} ms, the library's limit)";

	private void RaiseAll()
	{
		Raise(nameof(CompoundCheckClock));
		Raise(nameof(CompoundSuggestClock));
		Raise(nameof(SuggestStepClock));
		Raise(nameof(SuggestGlobalClock));
		Raise(nameof(TurnsNone));
		Raise(nameof(TurnsPerLanguage));
		Raise(nameof(TurnsWholeProgram));
		Raise(nameof(CharacterMap));
		Raise(nameof(IsDefault));
		Raise(nameof(Summary));
	}
}

/// <summary>One choice in the memory cap list: the cap in GB (0 = none) and how it reads.</summary>
public sealed record MemoryCapOption(int Gigabytes, string Label);
