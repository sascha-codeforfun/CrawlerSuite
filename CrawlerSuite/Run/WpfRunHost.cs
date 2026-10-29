using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Host;
using CrawlerSuite.Dialogs;
using HueThemeKit;

namespace CrawlerSuite.Run;

/// <summary>
/// The engine's host inside the window. Engine callbacks arrive on worker threads: output is
/// posted to the UI thread and shown in <see cref="RunState"/>; questions are shown as themed
/// dialogs on the UI thread while the worker waits for the answer.
/// </summary>
public sealed class WpfRunHost : IRunHost
{
	private readonly Window owner;
	private readonly RunState state;
	private readonly Dispatcher dispatcher;

	public WpfRunHost(Window owner, RunState state)
	{
		this.owner = owner;
		this.state = state;
		dispatcher = owner.Dispatcher;
	}

	// ── Output ───────────────────────────────────────────────────────────────

	public void Phase(string title) => Post(() => state.EnterPhase(title));

	public void Step(StepRow row) => Post(() => state.AddStep(row));

	public void Message(MessageTone tone, string text) =>
		Post(() => state.Messages.Add(new MessageItem(tone, string.Empty, text)));

	public void Notice(Notice notice) =>
		Post(() => state.Messages.Add(new MessageItem(notice.Tone, notice.Heading, string.Join(Environment.NewLine, notice.Lines))));

	public void ConfigCheck(string subtitle, IReadOnlyList<CheckBlock> blocks) =>
		Post(() => state.ConfigChecks.Add(new ConfigCheckItem(subtitle, blocks)));

	public void Progress(CrawlProgress progress) => Post(() => state.UpdateProgress(progress), DispatcherPriority.Background);

	public void Plan(IReadOnlyList<PlannedPhase> phases) => Post(() => state.ApplyPlan(phases));

	public void StepStarted(int id, string label) => Post(() => state.StartStep(id, label));

	// Lower priority, like the download counters: a late report is dropped by its step id.
	public void StepFindings(string label, int count) => Post(() => state.SetStepFindings(label, count));

	public void StepFacts(string label, StepFacts facts) => Post(() => state.SetStepFacts(label, facts));

	public void WorkTotals(StepWork totals) => Post(() => state.WorkTotals = totals);

	public void StepProgress(int id, int done, int total) =>
		Post(() => state.StepProgress(id, done, total), DispatcherPriority.Background);

	private void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
	{
		if (!dispatcher.HasShutdownStarted)
		{
			dispatcher.BeginInvoke(action, priority);
		}
	}

	// Several engine threads can hit a locked file at once; questions are asked one at a time
	// so a second dialog never opens on top of the first.
	private readonly object askGate = new();

	private T Ask<T>(Func<T> question)
	{
		lock (askGate)
		{
			// Waiting for the operator's answer is not run time: the header clocks hold meanwhile.
			return dispatcher.Invoke(() =>
			{
				state.Clocks.Pause();
				try
				{
					return question();
				}
				finally
				{
					state.Clocks.Resume();
				}
			});
		}
	}

	// ── Questions ────────────────────────────────────────────────────────────

	public SnapshotChoice AskSnapshotChoice(SnapshotPrompt prompt) => Ask(() =>
	{
		var message = $"Last snapshot: {prompt.LastSnapshotName} ({prompt.AgeDescription} ago).";
		if (prompt.IsRecent)
		{
			message += Environment.NewLine + Environment.NewLine +
				$"The last snapshot is only {prompt.AgeDescription} old.";
		}

		message += Environment.NewLine + Environment.NewLine +
			"New crawl downloads the site again. Use latest snapshot analyses the existing download without contacting the site.";

		var choice = HueMessage.Choose(owner, Mask.Apply(prompt.SiteLabel), Mask.Apply(message),
			yesText: "New crawl", noText: "Use latest snapshot", cancelText: "Cancel crawl");
		return choice switch
		{
			HueChoice.Yes => SnapshotChoice.NewCrawl,
			HueChoice.No => SnapshotChoice.UseLatest,
			_ => SnapshotChoice.Abort,
		};
	});

	public IncompleteSnapshotChoice AskIncompleteSnapshot(IncompleteSnapshotPrompt prompt) => Ask(() =>
	{
		var message =
			$"The snapshot {prompt.SnapshotName} has no \"completed\" marker — its crawl did not finish." + Environment.NewLine +
			Environment.NewLine +
			$"Folder:  {prompt.SnapshotPath}" + Environment.NewLine +
			$"Files:   {Format.Count(prompt.FileCount)}" + Environment.NewLine +
			$"Size:    {prompt.TotalMb:0.0} MB";
		var choice = HueMessage.Choose(owner, "Incomplete crawl found", Mask.Apply(message),
			yesText: "Delete snapshot", noText: "Keep and continue", cancelText: "Cancel crawl", destructive: true);
		return choice switch
		{
			HueChoice.Yes => IncompleteSnapshotChoice.Delete,
			HueChoice.No => IncompleteSnapshotChoice.Keep,
			_ => IncompleteSnapshotChoice.Abort,
		};
	});

	public bool AskCleanSweep(string snapshotPath) => Ask(() => HueConfirm.Ask(owner,
		"Clean sweep before the replay?",
		"Delete every derived file in the snapshot and keep only the crawl itself " +
		"(download/, oversized/, 00-crawler.log, 01-crawler.log). Everything else is re-created by the replay." +
		Environment.NewLine + Environment.NewLine + Mask.Apply(snapshotPath),
		"Sweep", destructive: true));

	public bool AskEmptyDownloadStartFresh(string downloadDirectory) => Ask(() => HueConfirm.Ask(owner,
		"The snapshot has no downloaded pages",
		$"The download folder is empty or missing, so an analysis would find nothing:{Environment.NewLine}{Mask.Apply(downloadDirectory)}",
		"Start a fresh crawl"));

	public bool AskProceedWithStaleContentList(CmsContentListFreshness freshness) => Ask(() =>
	{
		var message =
			$"The CMS content list is {freshness.AgeDays} day(s) old; the limit is {freshness.MaxAgeDays}." + Environment.NewLine +
			$"File: {freshness.Path}" + Environment.NewLine +
			$"Date: {freshness.FileDate?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}" + Environment.NewLine + Environment.NewLine +
			"The extra download requests the URLs in this list; outdated entries show up as broken links.";
		if (!string.IsNullOrWhiteSpace(freshness.Comment))
		{
			message += Environment.NewLine + Environment.NewLine + freshness.Comment.Trim();
		}

		return HueConfirm.Ask(owner, "CMS content list is outdated", Mask.Apply(message), "Proceed with crawl");
	});

	public IReadOnlyCollection<string> AskOrphansToRename(IReadOnlyList<string> orphans, string backupExtension) =>
		Ask<IReadOnlyCollection<string>>(() => OrphansDialog.Show(owner, orphans, backupExtension));

	public bool AskReSettle(ReSettlePrompt prompt) => Ask(() => ReSettleDialog.Show(owner, prompt));

	public bool AskRetryLockedFile(string displayName, string error, bool isDelete) => Ask(() =>
		TwoChoice.Show(owner,
			isDelete ? $"Cannot delete {displayName}" : $"Cannot write {displayName}",
			"The file is probably open in another program (an editor or Excel). Close it, then retry." +
			Environment.NewLine + Environment.NewLine + error,
			confirmText: "Retry", declineText: "Skip"));

	public bool AskRetryLockedCsv(string path, string error) => Ask(() =>
		TwoChoice.Show(owner,
			"CMS content list is locked",
			$"The file is open in another program. Close it, then retry. Skipping leaves out the features that need it.{Environment.NewLine}{Environment.NewLine}{path}{Environment.NewLine}{error}",
			confirmText: "Retry", declineText: "Skip"));

	public (string User, string Password)? AskProxySignIn(string proxyUrl, bool typedSignInRefused) =>
		Ask(() => ProxyCredentialsDialog.Show(owner, proxyUrl, typedSignInRefused));

	public bool AskContinueAfterConnectivityFailure(IReadOnlyList<string> details, bool proxyTip) => Ask(() =>
	{
		var message = string.Join(Environment.NewLine, details.Where(d => !string.IsNullOrWhiteSpace(d)));
		if (proxyTip)
		{
			message += Environment.NewLine + Environment.NewLine +
				"Behind a corporate firewall? Set the proxy (UseProxy, ProxyUrl) in crawlersuite.config.json.";
		}

		return TwoChoice.Show(owner, "Connectivity check failed", message,
			confirmText: "Continue anyway", declineText: "Cancel crawl");
	});

	public CrawlLimitChoice AskCrawlLimitExceeded(CrawlLimitReport report) =>
		Ask(() => CrawlLimitDialog.Show(owner, report));
}
