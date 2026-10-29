using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>The Run page. The window owns the work; this page only raises what the operator asked for.</summary>
public partial class RunView : UserControl
{
	public RunView()
	{
		InitializeComponent();
	}

	public event EventHandler? StartRequested;

	/// <summary>"Re-run latest": replay the latest snapshot without questions.</summary>
	public event EventHandler? QuickRunRequested;

	public event EventHandler? ReloadRequested;


	public event EventHandler? ResultsRequested;

	private void OnStartRun(object sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);

	private void OnQuickRun(object sender, RoutedEventArgs e) => QuickRunRequested?.Invoke(this, EventArgs.Empty);

	private void OnReloadConfiguration(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke(this, EventArgs.Empty);


	private void OnViewResults(object sender, RoutedEventArgs e) => ResultsRequested?.Invoke(this, EventArgs.Empty);

	private void OnCopyStepsForAi(object sender, RoutedEventArgs e)
	{
		if (DataContext is RunPageModel model)
		{
			CopyForAi.Copy(CopyStepsButton, model.State.StepsForAi(CopyForAi.AppVersion()));
		}
	}

	private void OnMarkBaseline(object sender, RoutedEventArgs e)
	{
		if (DataContext is RunPageModel model)
		{
			model.MarkBaseline();
		}
	}

	private void OnCopyOverviewForAi(object sender, RoutedEventArgs e)
	{
		if (DataContext is RunPageModel model)
		{
			CopyForAi.Copy(CopyOverviewButton, model.State.OverviewForAi(CopyForAi.AppVersion()));
		}
	}

	/// <summary>A findings chip in the run overview: open the Results tab it counts.</summary>
	public event EventHandler<FindingsTarget>? FindingsRequested;

	/// <summary>The sitemap chip: Output files, where the sitemap is.</summary>
	public event EventHandler? OutputFilesRequested;

	private void OnOpenFindings(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { DataContext: PlanStepItem { CanOpen: true } step })
		{
			return;
		}

		if (step.OpenTarget is { } target)
		{
			FindingsRequested?.Invoke(this, target);
		}
		else if (step.OpensOutputFiles)
		{
			OutputFilesRequested?.Invoke(this, EventArgs.Empty);
		}
	}

	// The steps table shows all its rows; the page scrolls, not the table.
	private void OnPassWheelToPage(object sender, MouseWheelEventArgs e) => WheelPassThrough.ToParent(sender, e);
}
