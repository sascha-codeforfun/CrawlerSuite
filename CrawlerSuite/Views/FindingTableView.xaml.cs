using System;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>
/// One findings table: columns come from the view's definition, every cell is shown through the
/// screen mask, and the filter narrows the rows as you type. A double-click shows the row's
/// downloaded file in Explorer. Built for long tables: fixed column widths, recycled rows, and
/// sorting that reads the cells directly.
/// </summary>
public partial class FindingTableView : UserControl
{
	private readonly DispatcherTimer filterDelay;
	private FindingLeafVm? leaf;
	private bool settingFilter;

	public FindingTableView()
	{
		InitializeComponent();
		filterDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnFilterDue, Dispatcher);
		filterDelay.Stop();
		DataContextChanged += (_, _) => Show(DataContext as FindingLeafVm);

		// Follow the leaf only while on screen, so a replaced view is not kept alive by it.
		Loaded += (_, _) =>
		{
			if (leaf is not null)
			{
				leaf.PropertyChanged -= OnLeafChanged;
				leaf.PropertyChanged += OnLeafChanged;
			}
		};
		Unloaded += (_, _) =>
		{
			ApplyPendingFilter();
			if (leaf is not null)
			{
				leaf.PropertyChanged -= OnLeafChanged;
			}
		};
	}

	private void Show(FindingLeafVm? next)
	{
		ApplyPendingFilter();
		if (leaf is not null)
		{
			leaf.PropertyChanged -= OnLeafChanged;
		}

		leaf = next;
		Table.ItemsSource = null; // before the columns go, so the old rows are not laid out again
		Table.Columns.Clear();
		if (leaf is null)
		{
			return;
		}

		leaf.PropertyChanged += OnLeafChanged;
		ShowBasis(leaf.Basis);
		NotWrittenText.Visibility = leaf.IsWritten ? Visibility.Collapsed : Visibility.Visible;
		Table.Visibility = leaf.IsWritten ? Visibility.Visible : Visibility.Collapsed;
		FilterBox.IsEnabled = leaf.IsWritten && leaf.Problem is null;

		// Without a free-text column, the last URL column takes the remaining width.
		int count = Math.Min(leaf.Columns.Count, FindingRowVm.MaxColumns);
		bool hasText = leaf.Columns.Take(count).Any(c => c.Kind is ColumnKind.Text or ColumnKind.Pages or ColumnKind.Chain or ColumnKind.QualityContext);
		int stretch = hasText ? -1 : Enumerable.Range(0, count).LastOrDefault(i => leaf.Columns[i].Kind == ColumnKind.Url, -1);
		if (leaf.HasSources)
		{
			Table.Columns.Add(SourceColumn(leaf));
		}

		for (int i = 0; i < count; i++)
		{
			Table.Columns.Add(Column(leaf, i, i == stretch));
		}

		Table.ItemsSource = leaf.View;

		settingFilter = true;
		FilterBox.Text = leaf.FilterText;
		settingFilter = false;
		SummaryText.Text = leaf.Summary;
	}

	/// <summary>The check's standard: its text, then each reference as a link (opens in the browser) with a copy button.</summary>
	private void ShowBasis(FindingBasis? basis)
	{
		BasisLinks.Children.Clear();
		BasisPanel.Visibility = basis is null ? Visibility.Collapsed : Visibility.Visible;
		if (basis is null)
		{
			return;
		}

		BasisPanel.SetResourceReference(StyleProperty, basis.IsViolation ? "Hue.Callout.Warn" : "Hue.Callout");
		var (lead, body) = SplitLead(basis.FullText);
		// The operator's name for it first; the text itself opens with what it does to a person.
		BasisLead.Text = basis.IsWcag ? $"{char.ToUpperInvariant(FindingBasis.WcagLabel[0])}{FindingBasis.WcagLabel[1..]} — {lead}" : lead;
		BasisLinkList.Cited(BasisText, body);
		BasisText.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		BasisLinkList.Fill(BasisLinks, basis);
		showingBasis = true;
		BasisExpander.IsExpanded = basisExpanded;
		showingBasis = false;
	}

	// Open or folded, for every tab until the app closes: whoever folds one card wants the room on the next.
	private static bool basisExpanded = false;
	private bool showingBasis;

	private void OnBasisToggled(object sender, RoutedEventArgs e)
	{
		if (!showingBasis && sender is Expander expander)
		{
			basisExpanded = expander.IsExpanded;
		}
	}

	/// <summary>The card's first sentence (the header, also when folded) and the rest.</summary>
	internal static (string Lead, string Body) SplitLead(string text)
	{
		int end = text.IndexOf(". ", StringComparison.Ordinal);
		return end < 0 ? (text.Trim(), string.Empty) : (text[..(end + 1)].Trim(), text[(end + 2)..].Trim());
	}

	/// <summary>The widest an address column gets; it wraps to three lines within it.</summary>
	private const double UrlColumnWidth = 380;

	private static DataGridColumn Column(FindingLeafVm leaf, int index, bool stretchThisUrl)
	{
		var column = leaf.Columns[index];
		var path = $"C{index}";
		int measured = Measure(leaf, index, column.Header);

		DataGridColumn result = column.Kind switch
		{
			ColumnKind.Url => CellColumn<UrlCell>(path),
			ColumnKind.Text => CellColumn<CodeCell>(path),
			ColumnKind.Pages => CellColumn<PagesCell>(path),
			ColumnKind.Status => CellColumn<StatusCell>(path),
			ColumnKind.Flag => CellColumn<FlagCell>(path),
			ColumnKind.Chain => CellColumn<ChainCell>(path),
			ColumnKind.QualityContext => QualityColumn(leaf, path, column.Tag),
			ColumnKind.Number => NumberColumn(path),
			_ => TextColumn(path),
		};
		result.Header = column.Header;
		result.SortMemberPath = path;

		switch (column.Kind)
		{
			// An address wraps to three lines (UrlCell), so its column stays narrow and the others keep their room.
			case ColumnKind.Url when stretchThisUrl:
				result.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
				result.MinWidth = Math.Clamp(measured + 30, 200, UrlColumnWidth);
				break;
			case ColumnKind.Url:
				result.Width = new DataGridLength(Math.Clamp(measured + 30, 200, UrlColumnWidth)); // + the copy button
				result.MinWidth = 200;
				break;
			case ColumnKind.Text:
			case ColumnKind.Pages:
			case ColumnKind.Chain:
			case ColumnKind.QualityContext:
				result.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
				result.MinWidth = 260;
				break;
			// The rest keep their measured width as a floor: the table scrolls sideways rather than
			// squeezing a column down to a letter.
			case ColumnKind.Status:
				result.Width = new DataGridLength(140);
				result.MinWidth = 140;
				break;
			case ColumnKind.Flag:
				result.Width = new DataGridLength(96);
				result.MinWidth = 96;
				break;
			case ColumnKind.Number:
				result.Width = new DataGridLength(Math.Clamp(measured, 60, 160));
				result.MinWidth = Math.Clamp(measured, 60, 160);
				result.HeaderTemplate = Headers.RightAligned;
				break;
			default:
				result.Width = new DataGridLength(Math.Clamp(measured, 60, 280));
				result.MinWidth = Math.Clamp(measured, 60, 280);
				break;
		}

		return result;
	}

	/// <summary>The page's source from the content list, first: a chip as the export has it, and the CMS link's copy button.</summary>
	private static DataGridTemplateColumn SourceColumn(FindingLeafVm leaf)
	{
		var cell = new FrameworkElementFactory(typeof(SourceCell));
		cell.SetBinding(SourceCell.TextProperty, new Binding(nameof(FindingRowVm.SourceText)) { Mode = BindingMode.OneWay });
		cell.SetBinding(SourceCell.LinkProperty, new Binding(nameof(FindingRowVm.CmsLink)) { Mode = BindingMode.OneWay });
		cell.SetBinding(SourceCell.RoleProperty, new Binding(nameof(FindingRowVm.SourceRole)) { Mode = BindingMode.OneWay });
		cell.SetBinding(SourceCell.AddressProperty, new Binding(nameof(FindingRowVm.NoSourceAddress)) { Mode = BindingMode.OneWay });
		cell.SetValue(SourceCell.NameFilesProperty, false); // Results names the host only; file names are Triage's
		cell.SetValue(MarginProperty, new Thickness(0, 3, 0, 3));
		// A row without a source shows its host instead: the column is as wide as either needs.
		static int HostLength(string address) => Core.Urls.SiteRoot.RootOf(address) is { Length: > 0 } root ? root.Length - root.IndexOf("://", StringComparison.Ordinal) - 3 : 0;
		int chars = Math.Max("Source".Length, leaf.Rows.Take(200).Select(r => Math.Max(r.SourceText.Length, HostLength(r.NoSourceAddress))).DefaultIfEmpty(0).Max());
		int width = Math.Clamp(chars * 7 + 40 + 30, 110, 340); // + the chip's dot and padding, and the copy button's place
		return new DataGridTemplateColumn
		{
			Header = "Source",
			SortMemberPath = SourceSortPath,
			CellTemplate = new DataTemplate { VisualTree = cell },
			ClipboardContentBinding = new Binding(nameof(FindingRowVm.SourceText)) { Mode = BindingMode.OneWay },
			Width = new DataGridLength(width),
			// A long excerpt in a star column must not squeeze the chip: the grid shrinks columns without a minimum.
			MinWidth = width,
		};
	}

	private const string SourceSortPath = "Source";

	/// <summary>
	/// A fixed width from the header and a sample of rows (about 7 px a character): an auto width
	/// in a virtualized grid keeps changing while scrolling, and every change re-lays out the grid.
	/// </summary>
	private static int Measure(FindingLeafVm leaf, int index, string header)
	{
		int cells = leaf.Rows.Take(200).Select(r => index < r.Cells.Length ? r.Cells[index].Length : 0).DefaultIfEmpty(0).Max();

		// The header also needs room for its sort arrow, so a short header over short values is not cut off.
		return Math.Max(cells * 7 + 28, header.Length * 7 + 28 + HeaderSortRoom);
	}

	/// <summary>The sort arrow and its gap beside a column header.</summary>
	private const int HeaderSortRoom = 20;

	/// <summary>A cell drawn by one of the findings cells (they mask and re-render themselves).</summary>
	private static DataGridTemplateColumn CellColumn<TCell>(string path) where TCell : MaskedCell
	{
		var cell = new FrameworkElementFactory(typeof(TCell));
		cell.SetBinding(MaskedCell.ValueProperty, new Binding(path) { Mode = BindingMode.OneWay });
		cell.SetValue(MarginProperty, new Thickness(0, 3, 0, 3));
		return new DataGridTemplateColumn
		{
			CellTemplate = new DataTemplate { VisualTree = cell },
			ClipboardContentBinding = new Binding(path) { Mode = BindingMode.OneWay }, // Ctrl+C copies the raw value
		};
	}

	/// <summary>A content-quality context: the cell also gets the issue type and the row's Detail (for pattern marks).</summary>
	private static DataGridTemplateColumn QualityColumn(FindingLeafVm leaf, string path, string? issueType)
	{
		var cell = new FrameworkElementFactory(typeof(QualityContextCell));
		cell.SetValue(QualityContextCell.IssueTypeProperty, issueType);
		int detail = leaf.Columns.ToList().FindIndex(c => c.Header == "Detail");
		if (detail >= 0)
		{
			cell.SetBinding(QualityContextCell.DetailProperty, new Binding($"C{detail}") { Mode = BindingMode.OneWay });
		}

		cell.SetBinding(MaskedCell.ValueProperty, new Binding(path) { Mode = BindingMode.OneWay });
		cell.SetValue(MarginProperty, new Thickness(0, 3, 0, 3));
		return new DataGridTemplateColumn
		{
			CellTemplate = new DataTemplate { VisualTree = cell },
			ClipboardContentBinding = new Binding(path) { Mode = BindingMode.OneWay },
		};
	}

	/// <summary>Plain text, masked, wrapping when it does not fit.</summary>
	private static DataGridTextColumn TextColumn(string path)
	{
		var element = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
		element.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
		element.Setters.Add(new Setter(MarginProperty, new Thickness(0, 3, 0, 3)));
		return new DataGridTextColumn { Binding = new Masked(path), ElementStyle = element };
	}

	/// <summary>A whole number: ISO-grouped, right-aligned, with tabular digits so place values line up.</summary>
	private static DataGridTextColumn NumberColumn(string path)
	{
		var element = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
		element.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
		element.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
		element.Setters.Add(new Setter(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular));
		element.Setters.Add(new Setter(MarginProperty, new Thickness(0, 3, 8, 3)));
		return new DataGridTextColumn
		{
			Binding = new Binding(path) { Mode = BindingMode.OneWay, Converter = NumberCellText.Instance },
			ElementStyle = element,
		};
	}

	/// <summary>Sorts by comparing the cells directly (numbers as numbers); the default sort is slow on long tables.</summary>
	private void OnSorting(object sender, DataGridSortingEventArgs e)
	{
		e.Handled = true;
		if (leaf?.View is not ListCollectionView view || e.Column.SortMemberPath is not { Length: > 1 } path)
		{
			return;
		}

		int index;
		if (path == SourceSortPath)
		{
			index = CellComparer.SourceIndex;
		}
		else if (!int.TryParse(path.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out index))
		{
			return;
		}

		var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
		foreach (var other in Table.Columns)
		{
			other.SortDirection = null;
		}

		view.CustomSort = new CellComparer(index, direction);
		e.Column.SortDirection = direction;
	}

	private void OnLeafChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(FindingLeafVm.Summary) && leaf is not null)
		{
			SummaryText.Text = leaf.Summary;
		}
	}

	private void OnFilterChanged(object sender, TextChangedEventArgs e)
	{
		if (settingFilter)
		{
			return;
		}

		// Filter after a short pause in typing, not on every key: tables can be long.
		filterDelay.Stop();
		filterDelay.Start();
	}

	private void OnFilterDue(object? sender, EventArgs e) => ApplyPendingFilter();

	/// <summary>Applies typed filter text that is still waiting for the pause.</summary>
	private void ApplyPendingFilter()
	{
		if (filterDelay.IsEnabled && leaf is not null)
		{
			filterDelay.Stop();
			leaf.FilterText = FilterBox.Text;
		}

		filterDelay.Stop();
	}

	private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
	{
		// A double-click on a link or the copy button is theirs, not the row's.
		if (e.OriginalSource is FrameworkContentElement || IsInButton(e.OriginalSource as DependencyObject))
		{
			return;
		}

		if (e.OriginalSource is DependencyObject source
			&& ItemsControl.ContainerFromElement(Table, source) is DataGridRow { Item: FindingRowVm row }
			&& row.LocalFile is { } file)
		{
			Shell.Reveal(file);
		}
	}

	private static bool IsInButton(DependencyObject? element)
	{
		for (var current = element; current is not null and not DataGridRow; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
		{
			if (current is System.Windows.Controls.Primitives.ButtonBase)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Compares one cell of two rows: as numbers when both are numbers, else as text ignoring case.</summary>
	private sealed class CellComparer(int index, ListSortDirection direction) : IComparer
	{
		/// <summary>The index that sorts by the page source instead of a cell.</summary>
		public const int SourceIndex = -1;

		public int Compare(object? x, object? y)
		{
			var a = Cell(x);
			var b = Cell(y);
			int result = double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var na)
				&& double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var nb)
				? na.CompareTo(nb)
				: string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
			return direction == ListSortDirection.Ascending ? result : -result;
		}

		private string Cell(object? row) =>
			row is not FindingRowVm r ? string.Empty
			: index == SourceIndex ? r.SourceText
			: index < r.Cells.Length ? r.Cells[index] : string.Empty;
	}
}
