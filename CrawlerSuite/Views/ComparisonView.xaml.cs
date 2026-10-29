using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>The Comparison page: two snapshots of the site compared file by file, and one file's two sides.</summary>
public partial class ComparisonView : UserControl
{
	private ComparisonModel? model;

	public ComparisonView()
	{
		InitializeComponent();
		DataContextChanged += (_, _) => Attach(DataContext as ComparisonModel);
		PreviewKeyDown += OnKey;
		PreviewMouseDown += OnMouseBack;
		Tabs.SelectionChanged += OnTabChanged;
		PairRow.SizeChanged += (_, _) => FitPairRow();
		PairActions.SizeChanged += (_, _) => FitPairRow(); // Stop comes and goes
		Loaded += (_, _) => FitPairRow();
	}

	private bool actionsBelow;

	/// <summary>
	/// The actions stay beside the pickers while they fit with room for a one-line status; in a
	/// narrower window they move to a line of their own below the pickers instead of being cut off.
	/// </summary>
	private void FitPairRow()
	{
		if (PairRow.ActualWidth <= 0)
		{
			return;
		}

		PairActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		double pickers = 0;
		for (int c = 0; c < 5; c++)
		{
			pickers += PairRow.ColumnDefinitions[c].ActualWidth;
		}

		bool below = CrawlerSuite.Core.Display.ToolbarFit.Stack(PairRow.ActualWidth, pickers, PairActions.DesiredSize.Width);
		if (below == actionsBelow)
		{
			return;
		}

		actionsBelow = below;
		ActionsInline.Child = null;
		ActionsBelow.Child = null;
		(below ? ActionsBelow : ActionsInline).Child = PairActions;
		ActionsBelow.Visibility = below ? Visibility.Visible : Visibility.Collapsed;
	}

	private ComparisonModel? Model => DataContext as ComparisonModel;

	private void Attach(ComparisonModel? next)
	{
		if (model is not null)
		{
			model.Detail.ScrollRequested -= OnScrollRequested;
			model.Detail.PropertyChanged -= OnDetailChanged;
		}

		model = next;
		if (model is not null)
		{
			model.Detail.ScrollRequested += OnScrollRequested;
			model.Detail.PropertyChanged += OnDetailChanged;
		}
	}

	/// <summary>No file any more (a new comparison, a cleared result): the list is shown.</summary>
	private void OnDetailChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(FileDetailVm.HasFile) && model?.Detail.HasFile != true && Tabs.SelectedItem == FileTab)
		{
			Tabs.SelectedItem = FilesTab;
		}
	}

	private bool ShowingFile => Tabs.SelectedItem == FileTab;

	/// <summary>The File tab, with the selected file; its view scrolls to the change it stands at once it is on screen.</summary>
	private void OpenFile()
	{
		if (model?.SelectedFile is null)
		{
			return;
		}

		Tabs.SelectedItem = FileTab;
	}

	/// <summary>Back to the list, where it was: the selected row in sight and focused.</summary>
	private void BackToList()
	{
		Tabs.SelectedItem = FilesTab;
		Dispatcher.BeginInvoke(() =>
		{
			if (model?.SelectedFile is { } row)
			{
				FileGrid.ScrollIntoView(row);
				if (FileGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container)
				{
					container.Focus();
				}
			}
		}, System.Windows.Threading.DispatcherPriority.Background);
	}

	private void OnTabChanged(object sender, SelectionChangedEventArgs e)
	{
		if (e.OriginalSource == Tabs && ShowingFile)
		{
			Dispatcher.BeginInvoke(() => model?.Detail.Rescroll(), System.Windows.Threading.DispatcherPriority.Loaded);
		}
	}

	private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
	{
		// Only a double click on a row opens: not one on the header or the scroll bar.
		if (e.OriginalSource is DependencyObject source && FindRow(source) is not null)
		{
			OpenFile();
			e.Handled = true;
		}
	}

	private void OnFileGridKey(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			OpenFile();
			e.Handled = true;
		}
	}

	private void OnBackToList(object sender, RoutedEventArgs e) => BackToList();

	private static DataGridRow? FindRow(DependencyObject? node)
	{
		while (node is not null and not DataGridRow)
		{
			node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
				? System.Windows.Media.VisualTreeHelper.GetParent(node)
				: LogicalTreeHelper.GetParent(node);
		}

		return node as DataGridRow;
	}

	/// <summary>A change of the file is brought into sight, a little below the top.</summary>
	private void OnScrollRequested(object? sender, int index)
	{
		if (model is null || index < 0 || index >= model.Detail.Rows.Count)
		{
			return;
		}

		Dispatcher.BeginInvoke(() =>
		{
			RowList.UpdateLayout();
			RowList.ScrollIntoView(model.Detail.Rows[Math.Max(0, index - 2)]);
			RowList.ScrollIntoView(model.Detail.Rows[index]);
		}, System.Windows.Threading.DispatcherPriority.Background);
	}

	private void OnKey(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.F8 && Model is { } m && ShowingFile)
		{
			m.Detail.StepDifference(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
			e.Handled = true;
		}
		else if (e.Key == Key.Escape && ShowingFile)
		{
			BackToList();
			e.Handled = true;
		}
	}

	/// <summary>The mouse's back button, in the File tab: back to the list.</summary>
	private void OnMouseBack(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.XButton1 && ShowingFile)
		{
			BackToList();
			e.Handled = true;
		}
	}

	private bool syncingPictures;

	/// <summary>At 100 %, the two pictures scroll together: the same spot of both stays in sight.</summary>
	private void OnPictureScrolled(object sender, ScrollChangedEventArgs e)
	{
		// Only the side being scrolled leads: the other, clamped to a smaller picture, must not pull it back.
		if (syncingPictures || (e.HorizontalChange == 0 && e.VerticalChange == 0)
			|| sender is not ScrollViewer { IsMouseOver: true } and not ScrollViewer { IsKeyboardFocusWithin: true })
		{
			return;
		}

		var other = sender == PictureScrollA ? PictureScrollB : PictureScrollA;
		syncingPictures = true;
		try
		{
			other.ScrollToHorizontalOffset(((ScrollViewer)sender).HorizontalOffset);
			other.ScrollToVerticalOffset(((ScrollViewer)sender).VerticalOffset);
		}
		finally
		{
			syncingPictures = false;
		}
	}

	private async void OnCompare(object sender, RoutedEventArgs e)
	{
		if (Model is { } m)
		{
			await m.CompareAsync();
		}
	}

	private void OnStop(object sender, RoutedEventArgs e) => Model?.Stop();

	private async void OnReprunePair(object sender, RoutedEventArgs e)
	{
		if (Model is { } m)
		{
			await m.RepruneAsync(all: false);
		}
	}

	private async void OnRepruneAll(object sender, RoutedEventArgs e)
	{
		if (Model is { } m)
		{
			await m.RepruneAsync(all: true);
		}
	}

	private void OnSwap(object sender, RoutedEventArgs e) => Model?.Swap();

	private void OnSettings(object sender, RoutedEventArgs e) => Model?.OpenSettings();

	/// <summary>An embedded picture's card: the picture in a window of its own.</summary>
	private void OnOpenPicture(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: EmbeddedPictureVm picture })
		{
			PictureWindow.Show(Window.GetWindow(this), picture);
		}
	}

	private void OnPreviousFile(object sender, RoutedEventArgs e) => StepFile(-1);

	private void OnNextFile(object sender, RoutedEventArgs e) => StepFile(1);

	private void StepFile(int by)
	{
		if (Model is { } m)
		{
			m.Step(by);
			if (m.SelectedFile is { } row)
			{
				FileGrid.ScrollIntoView(row);
			}
		}
	}

	private void OnOpenAddress(object sender, MouseButtonEventArgs e)
	{
		if (Model?.Detail is { IsWebAddress: true } detail)
		{
			UrlCell.Open(detail.Address);
		}
	}

	private void OnPreviousChange(object sender, RoutedEventArgs e) => Model?.Detail.StepDifference(-1);

	private void OnNextChange(object sender, RoutedEventArgs e) => Model?.Detail.StepDifference(1);

	private void OnRevealA(object sender, RoutedEventArgs e) => Model?.Detail.RevealA();

	private void OnRevealB(object sender, RoutedEventArgs e) => Model?.Detail.RevealB();
}

/// <summary>A theme style by its name after "Hue." ("Pill.Warn" → Hue.Pill.Warn).</summary>
public sealed class HueStyle : IValueConverter
{
	public static readonly HueStyle Instance = new();

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
		value is string name && Application.Current?.TryFindResource("Hue." + name) is Style style ? style : DependencyProperty.UnsetValue;

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}
