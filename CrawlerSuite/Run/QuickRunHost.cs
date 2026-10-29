using System.Collections.Generic;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Host;

namespace CrawlerSuite.Run;

/// <summary>
/// The host for "Re-run latest": everything the run shows goes to the window as usual; the
/// questions a replay asks get their safe answer without a dialog — latest snapshot, clean
/// sweep, no files renamed, no assets promoted. A locked file still asks (there is no safe
/// answer), and an incomplete or empty snapshot stops the run with a message instead of guessing.
/// </summary>
public sealed class QuickRunHost : IRunHost
{
	private readonly IRunHost inner;

	public QuickRunHost(IRunHost inner) => this.inner = inner;

	// ── Output: passed on unchanged (the members with a default body too) ───────

	public void Phase(string title) => inner.Phase(title);

	public void Step(StepRow row) => inner.Step(row);

	public void Message(MessageTone tone, string text) => inner.Message(tone, text);

	public void Notice(Notice notice) => inner.Notice(notice);

	public void ConfigCheck(string subtitle, IReadOnlyList<CheckBlock> blocks) => inner.ConfigCheck(subtitle, blocks);

	public void Progress(CrawlProgress progress) => inner.Progress(progress);

	public void Plan(IReadOnlyList<PlannedPhase> phases) => inner.Plan(phases);

	public void StepStarted(int id, string label) => inner.StepStarted(id, label);

	public void StepProgress(int id, int done, int total) => inner.StepProgress(id, done, total);

	public void StepFindings(string label, int count) => inner.StepFindings(label, count);

	public void StepFacts(string label, StepFacts facts) => inner.StepFacts(label, facts);

	public void WorkTotals(StepWork totals) => inner.WorkTotals(totals);

	// ── Questions: the safe answer, said in the messages ─────────────────────

	public SnapshotChoice AskSnapshotChoice(SnapshotPrompt prompt)
	{
		inner.Message(MessageTone.Info, $"Quick re-run: replaying the latest snapshot ({prompt.LastSnapshotName}).");
		return SnapshotChoice.UseLatest;
	}

	public IncompleteSnapshotChoice AskIncompleteSnapshot(IncompleteSnapshotPrompt prompt)
	{
		inner.Message(MessageTone.Warning,
			$"Quick re-run stopped: the latest snapshot {prompt.SnapshotName} did not finish its crawl. Use Start crawl to decide what to do with it.");
		return IncompleteSnapshotChoice.Abort;
	}

	public bool AskCleanSweep(string snapshotPath)
	{
		inner.Message(MessageTone.Info, "Quick re-run: clean sweep — derived files deleted, the crawl itself kept.");
		return true;
	}

	public bool AskEmptyDownloadStartFresh(string downloadDirectory)
	{
		inner.Message(MessageTone.Warning,
			"Quick re-run stopped: the latest snapshot has no downloaded pages. Use Start crawl for a fresh crawl.");
		return false;
	}

	// Not asked on a replay (the content list only matters for a crawl); declining is the safe side.
	public bool AskProceedWithStaleContentList(CmsContentListFreshness freshness) => false;

	public IReadOnlyCollection<string> AskOrphansToRename(IReadOnlyList<string> orphans, string backupExtension)
	{
		inner.Message(MessageTone.Info, $"Quick re-run: {orphans.Count} file(s) without an index entry left as they are.");
		return [];
	}

	public bool AskReSettle(ReSettlePrompt prompt)
	{
		inner.Message(MessageTone.Info, $"Quick re-run: {prompt.UnverifiedCount} unverified asset(s) left unverified.");
		return false;
	}

	public bool AskRetryLockedFile(string displayName, string error, bool isDelete) => inner.AskRetryLockedFile(displayName, error, isDelete);

	public bool AskRetryLockedCsv(string path, string error) => inner.AskRetryLockedCsv(path, error);

	// The quick re-run skips the connectivity check, so neither is asked; a replay sends no request.
	public (string User, string Password)? AskProxySignIn(string proxyUrl, bool typedSignInRefused) => null;

	public bool AskContinueAfterConnectivityFailure(IReadOnlyList<string> details, bool proxyTip) => true;

	public CrawlLimitChoice AskCrawlLimitExceeded(CrawlLimitReport report) => inner.AskCrawlLimitExceeded(report);
}
