using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>Fills a TextBlock with the pieces of a usage sentence, the numbers in bold.</summary>
public static class UsageRuns
{
	public static readonly DependencyProperty RunsProperty =
		DependencyProperty.RegisterAttached("Runs", typeof(IReadOnlyList<UsageRun>), typeof(UsageRuns),
			new PropertyMetadata(null, OnRunsChanged));

	public static IReadOnlyList<UsageRun>? GetRuns(DependencyObject target) => (IReadOnlyList<UsageRun>?)target.GetValue(RunsProperty);

	public static void SetRuns(DependencyObject target, IReadOnlyList<UsageRun>? value) => target.SetValue(RunsProperty, value);

	private static void OnRunsChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
	{
		if (target is not TextBlock block)
		{
			return;
		}

		block.Inlines.Clear();
		foreach (var piece in e.NewValue as IReadOnlyList<UsageRun> ?? [])
		{
			var run = new System.Windows.Documents.Run(piece.Text);
			if (piece.Bold)
			{
				run.FontWeight = FontWeights.SemiBold;
			}

			block.Inlines.Add(run);
		}
	}
}
