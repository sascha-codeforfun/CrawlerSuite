using System;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Run;
using HueThemeKit;

namespace CrawlerSuite.Views;

public partial class DictionariesView : UserControl
{
	public DictionariesView()
	{
		InitializeComponent();
	}

	/// <summary>"Find entries no longer needed": the main window runs the re-run with the second pass.</summary>
	public event EventHandler? FindRequested;

	private DictionariesModel? Model => DataContext as DictionariesModel;

	private void OnFind(object sender, RoutedEventArgs e) => FindRequested?.Invoke(this, EventArgs.Empty);

	private void OnCopyList(object sender, RoutedEventArgs e)
	{
		if (Model is { } model)
		{
			CopyForAi.Copy(CopyListButton, model.ShownAsText());
		}
	}

	private void OnRemove(object sender, RoutedEventArgs e)
	{
		if (Model is { } model && (sender as FrameworkElement)?.DataContext is DictionaryEntryVm entry)
		{
			Report(model.Remove(entry, ConfirmShared));
		}
	}

	private void OnPin(object sender, RoutedEventArgs e)
	{
		if (Model is { } model && (sender as FrameworkElement)?.DataContext is DictionaryEntryVm entry)
		{
			Report(model.Pin(entry, ConfirmShared));
		}
	}

	private void OnKeep(object sender, RoutedEventArgs e)
	{
		if (Model is { } model && (sender as FrameworkElement)?.DataContext is DictionaryEntryVm entry)
		{
			model.Keep(entry);
		}
	}

	private bool ConfirmShared() =>
		HueConfirm.Ask(Window.GetWindow(this), "Change the user dictionary?",
			"The user dictionary is shared by every site: what you change there applies to all of them. A backup of the file is made before the first change. You are asked once while the app runs.",
			"Change it");

	private void Report(string? problem)
	{
		if (problem is not null)
		{
			HueMessage.Show(Window.GetWindow(this), "The dictionary could not be changed", Mask.Apply(problem));
		}
	}
}
