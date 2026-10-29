using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CrawlerSuite.Run;
using HueThemeKit;

namespace CrawlerSuite.Views;

/// <summary>The Triage page: the page list and the page being decided. The model does the work.</summary>
public partial class TriageView : UserControl
{
	public TriageView()
	{
		InitializeComponent();
		DataContextChanged += (_, e) =>
		{
			if (e.OldValue is TriageModel old)
			{
				old.PropertyChanged -= OnModelChanged;
			}

			if (e.NewValue is TriageModel model)
			{
				model.PropertyChanged += OnModelChanged;
			}

			ShowSourceColumn();
			ShowBadgeColumn();
		};
	}

	// A grid column is not in the visual tree, so its visibility follows the model from here.
	private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(TriageModel.HasSources))
		{
			ShowSourceColumn();
		}
		else if (e.PropertyName == nameof(TriageModel.HasBadges))
		{
			ShowBadgeColumn();
		}
	}

	private void ShowSourceColumn() =>
		SourceColumn.Visibility = Model?.HasSources == true ? Visibility.Visible : Visibility.Collapsed;

	// The security / bidi badges take a column only while a page has one.
	private void ShowBadgeColumn() =>
		BadgeColumn.Visibility = Model?.HasBadges == true ? Visibility.Visible : Visibility.Collapsed;

	private TriageModel? Model => DataContext as TriageModel;

	/// <summary>"Change in Settings": the main window opens Settings on its Triage tab.</summary>
	public event EventHandler? SettingsRequested;

	private void OnOpenTriageSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

	/// <summary>"Ticket sweep" (Debug mode): the main window runs it with the busy overlay.</summary>
	public event EventHandler? SweepRequested;

	private void OnTicketSweep(object sender, RoutedEventArgs e) => SweepRequested?.Invoke(this, EventArgs.Empty);

	private void OnCopyForAi(object sender, RoutedEventArgs e)
	{
		if (Model is { } model)
		{
			CopyForAi.Copy(CopyForAiButton, model.ForAi(CopyForAi.AppVersion()));
		}
	}

	private void OnClearSearch(object sender, RoutedEventArgs e)
	{
		if (Model is { } model)
		{
			model.SearchText = string.Empty;
		}
	}

	private void OnSearchKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape && Model is { } model)
		{
			model.SearchText = string.Empty;
			if (sender is TextBox box)
			{
				box.Text = string.Empty;
			}

			e.Handled = true;
		}
		else if (e.Key == Key.Enter && sender is TextBox enterBox)
		{
			// Enter does not wait for the typing pause.
			enterBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
			e.Handled = true;
		}
	}

	private static TriageItemVm? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as TriageItemVm;

	private void OnKeepName(object sender, RoutedEventArgs e) => ItemOf(sender)?.KeepName();

	private void OnAddUser(object sender, RoutedEventArgs e) => ItemOf(sender)?.AddToDictionary(user: true, ConfirmShared);

	private void OnAddSite(object sender, RoutedEventArgs e) => ItemOf(sender)?.AddToDictionary(user: false, ConfirmShared);

	private void OnUndoDictionary(object sender, RoutedEventArgs e) => ItemOf(sender)?.UndoDictionary();

	/// <summary>The same question as on Spell findings, asked once while the app runs.</summary>
	private bool ConfirmShared() =>
		HueConfirm.Ask(Window.GetWindow(this), "Change the user dictionary?",
			"The user dictionary is shared by every site: what you change there applies to all of them. A backup of the file is made before the first change. You are asked once while the app runs.",
			"Change it");

	private void OnUndoName(object sender, RoutedEventArgs e) => ItemOf(sender)?.UndoName();

	/// <summary>A spelling item's other pages: back to the list, searched for its word, at the top.</summary>
	private void OnOtherPages(object sender, RoutedEventArgs e)
	{
		if (ItemOf(sender) is { } item)
		{
			item.FindOtherPages();
			Scroller.ScrollToTop();
		}
	}

	/// <summary>From Spell findings: the list searched for the word, at the top.</summary>
	public void FindWord(string word)
	{
		Model?.FindWord(word);
		Scroller.ScrollToTop();
	}

	// Where the list was scrolled to when a page was opened, so going back lands there again.
	private double listOffset;

	private void Open(object? row)
	{
		if (row is TriagePageRowVm page && Model is { } model)
		{
			if (!model.IsPageOpen)
			{
				listOffset = Scroller.VerticalOffset;
			}

			model.Open(page.Url);
			Scroller.ScrollToTop();
		}
	}

	private void OnOpenPage(object sender, RoutedEventArgs e) => Open((sender as FrameworkElement)?.DataContext);

	private void OnPageDoubleClick(object sender, MouseButtonEventArgs e)
	{
		// Only a double-click on a row: not on a column header while sorting.
		if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement((DataGrid)sender, source) is not DataGridRow)
		{
			return;
		}

		Open(PageGrid.SelectedItem);
	}

	private void OnPageKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			Open(PageGrid.SelectedItem);
			e.Handled = true;
		}
	}

	private void OnBack(object sender, RoutedEventArgs e) => Back();

	/// <summary>Back to the list, scrolled to where it was: rows are updated in place, not rebuilt.</summary>
	private void Back()
	{
		Model?.Close();
		var offset = listOffset;
		Dispatcher.BeginInvoke(() => Scroller.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
	}

	private void OnUpdateList(object sender, RoutedEventArgs e) => Model?.UpdateList();

	private void OnNext(object sender, RoutedEventArgs e)
	{
		if (Model is { Page: { } page } model)
		{
			GoOn(model, page.Url);
		}
	}

	private void GoOn(TriageModel model, string url)
	{
		if (model.NextAfter(url) is { } next)
		{
			model.Open(next);
			Scroller.ScrollToTop();
		}
		else
		{
			Back();
		}
	}

	/// <summary>
	/// Files the page. When the page already has a ticket and new items go into this filing, the
	/// operator chooses: add them to that ticket, or open a new one — with what was filed before
	/// and what is new spelled out.
	/// </summary>
	private void OnAddComment(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is TriageItemVm item)
		{
			item.OpenComment();
		}
	}

		private void OnTicketDocument(object sender, RoutedEventArgs e)
	{
		if (Model is not { Page: { CanMakeDocument: true } page } model)
		{
			return;
		}

		var snapshot = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(model.SnapshotPath));
		try
		{
			TicketPreview.Show(Window.GetWindow(this), page, snapshot, Core.Text.DocumentText.Parse(model.TicketLanguageOf?.Invoke()));
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or ArgumentException
			or FormatException or System.Windows.Markup.XamlParseException)
		{
			Core.Logger.LogWarning($"Ticket document: could not be built — {ex}");
			HueMessage.Show(Window.GetWindow(this), "The ticket document could not be made",
				"Windows could not lay out the document for this page. Your verdicts are not affected. Details are in application.log next to the program."
				+ Environment.NewLine + Environment.NewLine + Mask.Apply(ex.Message));
		}
	}

	private void OnFile(object sender, RoutedEventArgs e)
	{
		if (Model is not { Page: { CanFile: true } page } model)
		{
			return;
		}

		var mode = page.ToTicket == 0 ? "none" : "new";
		var ticket = string.Empty;
		var (before, fresh) = page.TicketSplit();
		if (page.ToTicket > 0 && page.HasTickets && fresh.Count > 0)
		{
			var nl = Environment.NewLine;
			static string Line(TriageItemVm i)
			{
				var detail = i.Detail.Replace("\n", " ", StringComparison.Ordinal);
				return "• " + i.Heading + " — " + (detail.Length > 90 ? detail[..90] + "…" : detail);
			}

			var existing = page.Tickets[^1];
			var message = $"This page already has ticket {string.Join(", ", page.Tickets)}." + nl + nl
				+ (before.Count > 0
					? $"Already filed under it ({before.Count}):" + nl + string.Join(nl, before.Select(Line)) + nl + nl
					: string.Empty)
				+ $"New in this filing ({fresh.Count}):" + nl + string.Join(nl, fresh.Select(Line));

			var choice = HueMessage.Choose(Window.GetWindow(this), "Add to the existing ticket?", Mask.Apply(message),
				yesText: $"Add to {Mask.Apply(existing)}", noText: "New ticket", cancelText: "Cancel");
			if (choice == HueChoice.Cancel)
			{
				return;
			}

			if (choice == HueChoice.Yes)
			{
				mode = "addendum";
				ticket = existing;
			}
		}

		if (page.File(mode, ticket))
		{
			GoOn(model, page.Url);
		}
	}

	// The page list shows all its rows; the page scrolls, not the table.
	private void OnPassWheelToPage(object sender, MouseWheelEventArgs e) => WheelPassThrough.ToParent(sender, e);
}
