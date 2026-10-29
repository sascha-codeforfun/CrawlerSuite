using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CrawlerSuite.Run;
using HueThemeKit;
using Microsoft.Win32;

namespace CrawlerSuite.Views;

/// <summary>The Results page: the output files of a snapshot, grouped by topic.</summary>
public partial class ResultsView : UserControl
{
	public ResultsView()
	{
		InitializeComponent();
		DataContextChanged += (_, _) =>
		{
			// Group the table by topic (crawl, links, SEO, …) in the order the files are numbered.
			if (Model is { } model)
			{
				var view = CollectionViewSource.GetDefaultView(model.Rows);
				if (view.GroupDescriptions.Count == 0)
				{
					view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ResultRow.Group)));
				}
			}
		};
	}

	private ResultsModel? Model => DataContext as ResultsModel;

	private void OnCopyCountsForAi(object sender, RoutedEventArgs e)
	{
		if (Model is { HasSnapshot: true } model)
		{
			CopyForAi.Copy(CopyCountsButton, model.CountsForAi(CopyForAi.AppVersion()));
		}
	}

	private void OnChooseSnapshot(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFolderDialog
		{
			Title = "Choose a snapshot folder (yyyy-MM-dd-HH-mm-ss)",
			// Starts in the scoped site's folder. The Windows folder picker cannot be masked, so it
			// starts without a folder while the screen is.
			InitialDirectory = !Mask.Instance.IsActive && Window.GetWindow(this) is MainWindow owner ? owner.ScopeFolder() ?? string.Empty : string.Empty,
		};
		if (dialog.ShowDialog(Window.GetWindow(this)) != true || Model is null)
		{
			return;
		}

		if (Window.GetWindow(this) is MainWindow main)
		{
			_ = main.LoadResultsAsync(dialog.FolderName, string.Empty);
		}
	}

	private void OnOpenFolder(object sender, RoutedEventArgs e)
	{
		if (Model is { HasSnapshot: true } m && Directory.Exists(m.SnapshotPath))
		{
			Shell.Open(m.SnapshotPath);
		}
	}

	private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
	{
		// Only a double-click on a row opens its file, not one on a column header.
		if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement((DataGrid)sender, source) is not DataGridRow)
		{
			return;
		}

		if (sender is DataGrid { SelectedItem: ResultRow row } && row.Present)
		{
			Shell.Open(row.Path);
		}
	}
}

/// <summary>Opens a file or folder with the program Windows associates with it.</summary>
internal static class Shell
{
	public static void Open(string path)
	{
		try
		{
			Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			HueMessage.Show(Application.Current.MainWindow, "Could not open", Mask.Apply($"{path}{Environment.NewLine}{ex.Message}"));
		}
	}

	/// <summary>
	/// Shows a file selected in Explorer, without opening it: a downloaded page opened in a
	/// browser would load its resources from the live site.
	/// </summary>
	public static void Reveal(string path)
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			HueMessage.Show(Application.Current.MainWindow, "Could not show the file", Mask.Apply($"{path}{Environment.NewLine}{ex.Message}"));
		}
	}
}
