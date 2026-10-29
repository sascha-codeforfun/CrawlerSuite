namespace CrawlerSuite.Core.Host
{
	using System;
	using System.Collections.Generic;

	/// <summary>Tone of a single message the engine reports to the operator.</summary>
	public enum MessageTone
	{
		Plain,
		Info,
		Success,
		Warning,
		ActionRequired,
		Error,
		Emphasis,
		Skipped,
	}

	/// <summary>Colour intent of a single config-check line.</summary>
	public enum CheckTone
	{
		/// <summary>Explanatory prose.</summary>
		Prose,
		/// <summary>A value the operator reads (path, checksum) — shown neutral, never wrapped.</summary>
		Data,
		/// <summary>Setup-incomplete, benign — shown in the warning colour.</summary>
		Accent,
		/// <summary>A genuine error — shown in the error colour.</summary>
		Error,
	}

	/// <summary>One config-check line. <paramref name="SubLabel"/> names the value when set.</summary>
	public readonly record struct CheckLine(CheckTone Tone, string Text, string SubLabel = "");

	/// <summary>A labelled block of config-check lines (Problem / Why / Fix, or a named finding).</summary>
	public sealed record CheckBlock(string Label, IReadOnlyList<CheckLine> Lines, bool HeadingOnOwnLine = false);

	/// <summary>One finished step of the run (e.g. "Sitemap — 123 entries").</summary>
	public sealed record StepRow(string Phase, string Label, string Detail, bool Dimmed, TimeSpan? Elapsed, StepCost? Cost = null, StepWork? Work = null);

	/// <summary>
	/// What a step cost the memory manager: collections by generation, time the program was paused
	/// for them, bytes allocated. Process-wide, measured over the same span as the step's time.
	/// <paramref name="PeakMemoryBytes"/> is the most memory the process held during the step, sampled
	/// by the app (every half second and at the step's end); the run's peak is the highest of its steps.
	/// <paramref name="ProcessPeakBytes"/> is Windows' own peak for the whole process when the step
	/// ended, passed only in the first run of a process (later runs would show an earlier run's peak):
	/// a check on the sampling. 0 when not passed.
	/// <paramref name="AfterCleanUpBytes"/> is the most .NET still counted after a clean-up during the
	/// step (see <see cref="MemoryReading.AfterCleanUpBytes"/>): nearer to what the work needs than the
	/// memory held.
	/// </summary>
	public sealed record StepCost(int Gen0, int Gen1, int Gen2, TimeSpan Pause, long AllocatedBytes, long PeakMemoryBytes = 0, long ProcessPeakBytes = 0, long AfterCleanUpBytes = 0);

	/// <summary>
	/// How much a step (or, as a run total, the whole run) worked through: files the analysis read
	/// and their bytes, HTML documents parsed, text runs examined for spelling (page text blocks and
	/// script string literals), words checked, dictionary lookups asked of the dictionaries and
	/// lookups answered from the per-word memory. Counted, never estimated.
	/// </summary>
	public sealed record StepWork(long FilesRead, long BytesRead, long Parses, long TextRuns, long Words, long Lookups, long Remembered)
	{
		public static readonly StepWork None = new(0, 0, 0, 0, 0, 0, 0);

		public bool IsEmpty => FilesRead == 0 && Parses == 0 && TextRuns == 0 && Words == 0 && Lookups == 0 && Remembered == 0;

		public StepWork Since(StepWork start) => new(FilesRead - start.FilesRead, BytesRead - start.BytesRead,
			Parses - start.Parses, TextRuns - start.TextRuns, Words - start.Words, Lookups - start.Lookups,
			Remembered - start.Remembered);
	}

	/// <summary>A titled notice made of several lines (warning block, action block, error block).</summary>
	public sealed record Notice(MessageTone Tone, string Heading, IReadOnlyList<string> Lines);

	/// <summary>
	/// Everything the engine reports while it runs. Implementations must be thread-safe:
	/// the engine calls these from worker threads.
	/// </summary>
	public interface IRunOutput
	{
		/// <summary>A new phase of the run starts (Prepare, Crawl, Analysis, Spell-check …).</summary>
		void Phase(string title);

		/// <summary>A step of the current phase has finished.</summary>
		void Step(StepRow row);

		/// <summary>A single-line message.</summary>
		void Message(MessageTone tone, string text);

		/// <summary>A titled, multi-line notice.</summary>
		void Notice(Notice notice);

		/// <summary>A structured configuration check that halted the run.</summary>
		void ConfigCheck(string subtitle, IReadOnlyList<CheckBlock> blocks);

		/// <summary>Live download progress. Called often; implementations should throttle drawing.</summary>
		void Progress(CrawlProgress progress);

		/// <summary>
		/// The phases this run will go through, with their steps, sent before the first phase
		/// starts. Sent again when the plan changes (after the snapshot choice decides between a
		/// crawl and a replay); phases already started keep their state.
		/// </summary>
		void Plan(IReadOnlyList<PlannedPhase> phases) { }

		/// <summary>A step of the current phase starts. <paramref name="id"/> grows with every step of the run.</summary>
		void StepStarted(int id, string label) { }

		/// <summary>
		/// Progress of step <paramref name="id"/>: <paramref name="done"/> of <paramref name="total"/>
		/// files. Throttled by the engine; may arrive after the next step started (ignore it then).
		/// </summary>
		void StepProgress(int id, int done, int total) { }

		/// <summary>
		/// A finished step's findings: <paramref name="count"/> rows on the Results tab the step
		/// feeds (counted by the Results page's rule). Sent only for steps that feed a tab.
		/// </summary>
		void StepFindings(string label, int count) { }

		/// <summary>
		/// A finished step's facts for the overview: a neutral chip and its overlay (redirect kinds,
		/// pages eligible for search, sitemap entries, boilerplate pruned, snapshot size).
		/// </summary>
		void StepFacts(string label, StepFacts facts) { }

		/// <summary>
		/// The run's work totals, sent once when the analysis has finished. No default body on
		/// purpose: a host that wraps another must pass it on, and the compiler makes sure it does.
		/// </summary>
		void WorkTotals(StepWork totals);
	}

	/// <summary>
	/// One phase of the run as planned before it starts. A skipped phase is shown, marked
	/// as skipped, so the operator sees the whole road.
	/// </summary>
	public sealed record PlannedPhase(string Title, IReadOnlyList<string> Steps, bool Skipped = false, string SkipReason = "");

	/// <summary>
	/// The questions the engine asks the operator. Every method is called from a worker
	/// thread and blocks that thread until the operator has answered; implementations
	/// marshal to their UI thread themselves.
	/// </summary>
	public interface IOperatorPrompts
	{
		SnapshotChoice AskSnapshotChoice(SnapshotPrompt prompt);

		IncompleteSnapshotChoice AskIncompleteSnapshot(IncompleteSnapshotPrompt prompt);

		/// <summary>True to delete all derived files of the snapshot before the replay.</summary>
		bool AskCleanSweep(string snapshotPath);

		/// <summary>True to start a fresh crawl because the snapshot's download folder is empty.</summary>
		bool AskEmptyDownloadStartFresh(string downloadDirectory);

		/// <summary>True to proceed despite an outdated CMS content list; false aborts the run.</summary>
		bool AskProceedWithStaleContentList(CmsContentListFreshness freshness);

		/// <summary>
		/// Lets the operator choose which orphan files to rename to the backup extension.
		/// Returns the chosen file names (empty = rename none).
		/// </summary>
		IReadOnlyCollection<string> AskOrphansToRename(IReadOnlyList<string> orphans, string backupExtension);

		/// <summary>True to promote the listed unverified assets to their image extension.</summary>
		bool AskReSettle(ReSettlePrompt prompt);

		/// <summary>A file could not be written or deleted because it is locked. True = retry, false = skip.</summary>
		bool AskRetryLockedFile(string displayName, string error, bool isDelete);

		/// <summary>A CSV input file is locked. True = retry, false = skip the features that need it.</summary>
		bool AskRetryLockedCsv(string path, string error);

		/// <summary>
		/// The proxy refused the sign-in (HTTP 407): the Windows sign-in, or what was typed before
		/// (<paramref name="typedSignInRefused"/>). The user and password to try, or null to cancel the crawl.
		/// </summary>
		(string User, string Password)? AskProxySignIn(string proxyUrl, bool typedSignInRefused);

		/// <summary>The connectivity check failed. True = continue anyway, false = abort.</summary>
		bool AskContinueAfterConnectivityFailure(IReadOnlyList<string> details, bool proxyTip);

		/// <summary>The crawl passed the configured total download limit and has stopped.</summary>
		CrawlLimitChoice AskCrawlLimitExceeded(CrawlLimitReport report);
	}

	/// <summary>The host the engine runs inside (the WPF app, or a test double).</summary>
	public interface IRunHost : IRunOutput, IOperatorPrompts
	{
	}

	public enum SnapshotChoice { NewCrawl, UseLatest, Abort }

	public enum IncompleteSnapshotChoice { Delete, Keep, Abort }

	public enum CrawlLimitChoice { Delete, DeleteAndRecrawl }

	public sealed record SnapshotPrompt(
		string SiteLabel,
		string Url,
		string LastSnapshotName,
		string AgeDescription,
		bool IsRecent);

	public sealed record IncompleteSnapshotPrompt(
		string SnapshotPath,
		string SnapshotName,
		int FileCount,
		double TotalMb);

	public sealed record ReSettlePrompt(
		int UnverifiedCount,
		IReadOnlyList<(string FinalName, string Url)> Promote,
		IReadOnlyList<(string Name, string Reason)> HeldBack);

	/// <summary>What the operator sees when the crawl stops at the total download limit.</summary>
	public sealed record CrawlLimitReport(
		string SnapshotPath,
		long LimitBytes,
		long DownloadedBytes,
		int FileCount,
		IReadOnlyList<(string Name, string Url, long Bytes)> LargestFiles,
		IReadOnlyList<(string Folder, long Bytes, int Files)> LargestFolders);
}
