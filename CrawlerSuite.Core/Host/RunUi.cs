namespace CrawlerSuite.Core
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Threading;
	using CrawlerSuite.Core.Host;

	/// <summary>
	/// The engine's single route to the operator. Engine code reports through these
	/// static members; they forward to the <see cref="IRunHost"/> attached for the
	/// current run. With no host attached (unit tests), output is dropped and any
	/// question throws, so a test can never silently take a decision.
	///
	/// One run at a time per process: the host is attached at the start of a run and
	/// detached at its end.
	/// </summary>
	public static class RunUi
	{
		private static readonly object Gate = new();
		private static IRunHost? host;
		private static string currentPhase = string.Empty;
		private static long lastStepTicks;
		private static GcMark lastStepGc;
		private static StepWork lastStepWork = StepWork.None;

		/// <summary>
		/// The memory manager's counters at one moment; a step's cost is the difference of two.
		/// Memory allocated is the precise count: the quick estimate lags by the blocks the threads
		/// have been handed but not yet filled, which with several heaps (server collector) can shift hundreds of
		/// megabytes from one step's row to the next. Taken once per step, so its cost does not matter.
		/// </summary>
		private readonly record struct GcMark(int Gen0, int Gen1, int Gen2, TimeSpan Pause, long Allocated)
		{
			public static GcMark Now() => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
				GC.GetTotalPauseDuration(), GC.GetTotalAllocatedBytes(precise: true));

			public StepCost Since(GcMark start) => new(Gen0 - start.Gen0, Gen1 - start.Gen1, Gen2 - start.Gen2,
				Pause - start.Pause, Math.Max(0, Allocated - start.Allocated));
		}

		// ── Memory held: sampled by the app itself, per run ─────────────────
		//
		// Windows keeps one peak for the whole process and it cannot be reset, so a second run in the
		// same process would show the first run's peak. The app therefore samples the memory it holds
		// now (working set) every half second and at every step end, and keeps the highest value per
		// step and per run; both start again when a run is attached. In the first run of a process
		// Windows' own peak is passed along as well, as a check on the sampling.

		/// <summary>How often the memory held is sampled while a run is attached.</summary>
		private static readonly TimeSpan MemorySampleInterval = TimeSpan.FromMilliseconds(500);

		private static Timer? memorySampler;
		private static long stepPeakBytes;
		private static long runPeakBytes;
		private static long stepAfterCleanUpBytes;
		private static long runAfterCleanUpBytes;
		private static bool processHadRun;
		private static bool firstRunOfProcess;

		/// <summary>The memory the process holds now (working set); 0 when unavailable. Cheap: no process snapshot.</summary>
		private static long MemoryHeldNow() => MemoryReading.HeldNow();

		/// <summary>Windows' peak for the whole process (since it started); 0 when unavailable.</summary>
		private static long ProcessPeakMemory()
		{
			try
			{
				using var process = Process.GetCurrentProcess();
				return process.PeakWorkingSet64;
			}
			catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
			{
				return 0;
			}
		}

		/// <summary>Takes one sample of both figures into the step's and the run's highest values. Safe from any thread.</summary>
		private static void SampleMemory()
		{
			var now = MemoryReading.Now();
			RaiseTo(ref stepPeakBytes, now.HeldBytes);
			RaiseTo(ref runPeakBytes, now.HeldBytes);
			RaiseTo(ref stepAfterCleanUpBytes, now.AfterCleanUpBytes);
			RaiseTo(ref runAfterCleanUpBytes, now.AfterCleanUpBytes);
		}

		private static void RaiseTo(ref long peak, long value)
		{
			long seen = Interlocked.Read(ref peak);
			while (value > seen)
			{
				long was = Interlocked.CompareExchange(ref peak, value, seen);
				if (was == seen)
				{
					return;
				}

				seen = was;
			}
		}

		/// <summary>The highest memory held in this run so far (sampled); 0 before any sample.</summary>
		internal static long RunPeakMemory => Interlocked.Read(ref runPeakBytes);

		/// <summary>Stops the sampler and, for a new run, starts it again with both peaks cleared (caller holds <see cref="Gate"/>).</summary>
		private static void RestartMemorySampler(bool running)
		{
			if (memorySampler is { } sampler)
			{
				// Waits for a sample still running, so it cannot land in the next run's peaks.
				// The sample takes no lock, so waiting here under the gate cannot deadlock.
				using var stopped = new ManualResetEvent(false);
				if (sampler.Dispose(stopped))
				{
					stopped.WaitOne();
				}

				memorySampler = null;
			}

			Interlocked.Exchange(ref stepPeakBytes, 0);
			Interlocked.Exchange(ref runPeakBytes, 0);
			Interlocked.Exchange(ref stepAfterCleanUpBytes, 0);
			Interlocked.Exchange(ref runAfterCleanUpBytes, 0);
			if (!running)
			{
				return;
			}

			firstRunOfProcess = !processHadRun;
			processHadRun = true;
			SampleMemory();
			memorySampler = new Timer(_ => SampleMemory(), null, MemorySampleInterval, MemorySampleInterval);
		}

		/// <summary>Starts the step clock and the memory counters together (caller holds <see cref="Gate"/> or runs alone).</summary>
		private static void RestartStepClock()
		{
			lastStepTicks = Stopwatch.GetTimestamp();
			lastStepGc = GcMark.Now();
			lastStepWork = WorkCounters.Mark();
			var now = MemoryReading.Now();
			Interlocked.Exchange(ref stepPeakBytes, now.HeldBytes);
			Interlocked.Exchange(ref stepAfterCleanUpBytes, now.AfterCleanUpBytes);
		}

		/// <summary>Highest TriageUrlHighlight palette index config validation accepts.</summary>
		internal const int UrlHighlightSlotCount = 5;

		/// <summary>
		/// Attaches the host (null detaches). <paramref name="run"/> marks an actual run: only a run
		/// samples the memory it holds and counts towards the first run of the process — the
		/// start-up checks also report through a host, but are no run.
		/// </summary>
		public static void Attach(IRunHost? runHost, bool run = false)
		{
			lock (Gate)
			{
				host = runHost;
				currentPhase = string.Empty;
				lastStepTicks = 0;
				WorkCounters.Reset();
				lastStepWork = StepWork.None;
				stepId = 0;
				currentStep = null;
				snapshotDirectory = string.Empty;
				stepDone = 0;
				stepTotal = 0;
				lastProgressTicks = 0;
				RestartMemorySampler(runHost is not null && run);
			}
		}

		/// <summary>The attached host, for questions. Throws when none is attached.</summary>
		internal static IOperatorPrompts Prompts =>
			host ?? throw new InvalidOperationException("No run host is attached; the engine cannot ask the operator.");

		internal static bool HasHost => host is not null;

		/// <summary>Resets the step timer so the next step row is not billed for unrelated work.</summary>
		internal static void MarkStep()
		{
			lock (Gate)
			{
				RestartStepClock();
			}
		}

		/// <summary>Starts a new phase. A null or empty title is a visual frame only and is ignored.</summary>
		internal static void WriteHeader(string? title = null)
		{
			if (string.IsNullOrWhiteSpace(title))
			{
				return;
			}

			lock (Gate)
			{
				currentPhase = title;
				RestartStepClock();
			}

			FinishStep();
			host?.Phase(title);
		}

		// Framing helpers have no meaning in a window; they are no-ops so the engine's
		// reporting code can mark its sections without knowing the host.
		internal static void WriteFooter() { }
		internal static void WriteDivider() { }
		internal static void WriteBlank() { }

		internal static void WriteLine(string message) => Say(MessageTone.Plain, message);
		internal static void WriteInfo(string message) => Say(MessageTone.Info, message);
		internal static void WriteSuccess(string message) => Say(MessageTone.Success, message);
		internal static void WriteWarning(string message) => Say(MessageTone.Warning, message);
		internal static void WriteActionRequired(string message) => Say(MessageTone.ActionRequired, message);
		internal static void WriteError(string message) => Say(MessageTone.Error, message);
		internal static void WriteSkipped(string message) => Say(MessageTone.Skipped, message);
		internal static void WriteEmphasis(string message) => Say(MessageTone.Emphasis, message);
		internal static void WriteSubItem(string message) => Say(MessageTone.Plain, message);
		internal static void WriteField(string label, string value) => Say(MessageTone.Plain, $"{label}: {value}");

		/// <summary>One finished step of the current phase.</summary>
		internal static void WriteStepRow(string label, string detail, bool dimmed = false) =>
			WriteStepRow(null, label, detail, dimmed);

		/// <summary>
		/// One finished step, filed under <paramref name="phase"/> when it runs before its phase
		/// starts (the clean sweep, done right after the snapshot choice); null files it under the
		/// current phase.
		/// </summary>
		internal static void WriteStepRow(string? phase, string label, string detail, bool dimmed = false)
		{
			TimeSpan? elapsed = null;
			StepCost? cost = null;
			StepWork? work = null;
			lock (Gate)
			{
				// Every step gets its time and memory cost; the screen hides the short ones, the copy for AI keeps them.
				if (lastStepTicks != 0)
				{
					elapsed = Stopwatch.GetElapsedTime(lastStepTicks);
					SampleMemory();
					cost = GcMark.Now().Since(lastStepGc) with
					{
						PeakMemoryBytes = Interlocked.Read(ref stepPeakBytes),
						AfterCleanUpBytes = Interlocked.Read(ref stepAfterCleanUpBytes),
						ProcessPeakBytes = firstRunOfProcess ? ProcessPeakMemory() : 0,
					};
					work = WorkCounters.Mark().Since(lastStepWork);
				}

				RestartStepClock();
				phase ??= currentPhase;
			}

			host?.Step(new StepRow(phase, label, detail, dimmed, elapsed, cost, work));
		}

		/// <summary>Reports the run's work totals (all threads, whole run) to the host.</summary>
		internal static void ReportWorkTotals() => host?.WorkTotals(WorkCounters.Mark());

		/// <summary>A step's facts for the overview's neutral chip and its overlay.</summary>
		internal static void WriteStepFacts(string label, StepFacts facts) => host?.StepFacts(label, facts);

		internal static void WriteActionBlock(string heading, IEnumerable<string> steps) =>
			host?.Notice(new Notice(MessageTone.ActionRequired, heading, steps.ToList()));

		internal static void WriteWarningBlock(string heading, IEnumerable<string>? items = null) =>
			host?.Notice(new Notice(MessageTone.Warning, heading, items?.ToList() ?? []));

		internal static void WriteErrorBlock(string heading, IEnumerable<string>? items = null) =>
			host?.Notice(new Notice(MessageTone.Error, heading, items?.ToList() ?? []));

		internal static void WriteConfigCheck(string subtitle, IReadOnlyList<CheckBlock> blocks) =>
			host?.ConfigCheck(subtitle, blocks);

		internal static void Progress(CrawlProgress progress) => host?.Progress(progress);

		// ── Plan and step progress ───────────────────────────────────────────

		/// <summary>Step progress is sent at most this often (the last file of a step always is).</summary>
		private static readonly long ProgressInterval = Stopwatch.Frequency / 6;

		private static int stepId;
		private static string? currentStep;
		private static string snapshotDirectory = string.Empty;
		private static int stepDone;
		private static int stepTotal;
		private static long lastProgressTicks;

		/// <summary>Sends the phases and steps of the run (again, when the plan has changed).</summary>
		internal static void Plan(IReadOnlyList<PlannedPhase> phases) => host?.Plan(phases);

		/// <summary>The snapshot this run writes into, read to count each finished step's findings.</summary>
		internal static void SetSnapshot(string directory)
		{
			lock (Gate)
			{
				snapshotDirectory = directory ?? string.Empty;
			}
		}

		/// <summary>A step of the current phase starts; its progress is unknown until <see cref="StepTotal"/>.</summary>
		internal static void BeginStep(string label)
		{
			FinishStep();
			int id;
			lock (Gate)
			{
				currentStep = label;
				id = ++stepId;
				Volatile.Write(ref stepDone, 0);
				Volatile.Write(ref stepTotal, 0);
				lastProgressTicks = 0;
			}

			host?.StepStarted(id, label);
		}

		/// <summary>The current step will go through this many files.</summary>
		internal static void StepTotal(int total)
		{
			Volatile.Write(ref stepDone, 0);
			Volatile.Write(ref stepTotal, Math.Max(0, total));
			Interlocked.Exchange(ref lastProgressTicks, 0);
			host?.StepProgress(Volatile.Read(ref stepId), 0, Math.Max(0, total));
		}

		/// <summary>
		/// The current step has ended (the next step or phase starts): when it feeds a Results tab,
		/// its count is read from the snapshot and sent.
		/// </summary>
		private static void FinishStep()
		{
			string? label;
			string directory;
			lock (Gate)
			{
				label = currentStep;
				currentStep = null;
				directory = snapshotDirectory;
			}

			var current = host;
			if (current is null || label is null || Findings.StepFindings.TargetOf(label) is null)
			{
				return;
			}

			if (Findings.StepFindings.Count(directory, label) is { } count)
			{
				current.StepFindings(label, count);
			}
		}

		/// <summary>The current step has finished one more file. Safe from parallel loops.</summary>
		internal static void StepAdvance()
		{
			var current = host;
			int done = Interlocked.Increment(ref stepDone);
			if (current is null)
			{
				return;
			}

			int total = Volatile.Read(ref stepTotal);
			long now = Stopwatch.GetTimestamp();
			long last = Interlocked.Read(ref lastProgressTicks);
			bool final = total > 0 && done >= total;
			if (!final && now - last < ProgressInterval)
			{
				return;
			}

			// One thread wins the slot; the others skip this report.
			if (!final && Interlocked.CompareExchange(ref lastProgressTicks, now, last) != last)
			{
				return;
			}

			current.StepProgress(Volatile.Read(ref stepId), total > 0 ? Math.Min(done, total) : done, total);
		}

		private static void Say(MessageTone tone, string message)
		{
			if (string.IsNullOrWhiteSpace(message))
			{
				return;
			}

			host?.Message(tone, message.Trim());
		}
	}
}
