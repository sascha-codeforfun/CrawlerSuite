using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Settings;
using CrawlerSuite.Run;
using HueThemeKit;
using Microsoft.Win32;

namespace CrawlerSuite.Views;

/// <summary>
/// The settings screen. The model holds the changes; this view asks the questions: the
/// confirmation before a setting goes back to its default or a site is removed, and on Save the
/// restated defaults one by one, the keys that will be dropped and changed site addresses.
/// </summary>
public partial class SettingsView : UserControl
{
	public SettingsView()
	{
		InitializeComponent();
		DataContextChanged += (_, e) =>
		{
			if (e.NewValue is SettingsModel model)
			{
				model.ConfirmClearSubdomains = site => HueConfirm.Ask(Owner, "Remove the allowed subdomains?",
					Mask.Apply($"Switching this off removes the allowed subdomains of {site.Name}:\n\n{site.SubdomainsText}"),
					"Remove", destructive: true);
			}
		};
	}

	/// <summary>The file was written: the main window reads the configuration again.</summary>
	public event EventHandler? Saved;

	private SettingsModel? Model => DataContext as SettingsModel;

	private Window? Owner => Window.GetWindow(this);

	// ── Session lock ─────────────────────────────────────────────────────────

	private void OnUnlock(object sender, RoutedEventArgs e)
	{
		if (Model is { IsRunning: false } model && TrustBox.IsChecked == true)
		{
			model.Unlock();
		}

		TrustBox.IsChecked = false; // asked again the next time the settings are unlocked
	}

	private void OnLockAll(object sender, RoutedEventArgs e) => Model?.Lock();

	// ── Search and folding (per tab) ─────────────────────────────────────────

	private static SettingsTabVm? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as SettingsTabVm;

	private void OnToggleSection(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is SettingRowVm { Tab: { } tab } row)
		{
			tab.ToggleSection(row.Info.Section);
		}
	}

	private void OnFoldAll(object sender, RoutedEventArgs e) => TabOf(sender)?.FoldAll();

	private void OnUnfoldAll(object sender, RoutedEventArgs e) => TabOf(sender)?.UnfoldAll();

	private void OnClearSearch(object sender, RoutedEventArgs e)
	{
		if (TabOf(sender) is { } tab)
		{
			tab.SearchText = string.Empty;
		}
	}

	private void OnSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == System.Windows.Input.Key.Escape && TabOf(sender) is { } tab)
		{
			tab.SearchText = string.Empty;
			if (sender is TextBox box)
			{
				box.Text = string.Empty;
			}

			e.Handled = true;
		}
		else if (e.Key == System.Windows.Input.Key.Enter && sender is TextBox enterBox)
		{
			// Enter does not wait for the typing pause.
			System.Windows.Data.BindingOperations.GetBindingExpression(enterBox, TextBox.TextProperty)?.UpdateSource();
			e.Handled = true;
		}
	}

	private void OnResetMemory(object sender, RoutedEventArgs e)
	{
		if (Model is { CanChangeLevers: true } model)
		{
			model.Levers.ResetMemory();
		}
	}

	/// <summary>
	/// Starts the app again with the chosen memory manager. The window closes first, through its
	/// usual questions (a run going, unsaved settings); only when it has closed is the new process
	/// started, so a cancelled close never leaves two windows.
	/// </summary>
	private void OnRestartWithMemory(object sender, RoutedEventArgs e)
	{
		if (Model is not { CanChangeLevers: true } || Owner is not { } window || Environment.ProcessPath is not { } program)
		{
			return;
		}

		bool closed = false;
		void OnClosed(object? s, EventArgs a) => closed = true;
		window.Closed += OnClosed;
		window.Close();
		window.Closed -= OnClosed;
		if (!closed)
		{
			return;
		}

		try
		{
			using var started = System.Diagnostics.Process.Start(Core.Host.MemorySetup.Chosen.RestartInfo(program));
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			Logger.LogError($"Restart with another memory manager failed: {ex.Message}");
		}
	}

	private void OnResetLevers(object sender, RoutedEventArgs e)
	{
		if (Model is { CanChangeLevers: true } model)
		{
			model.Levers.Reset();
		}
	}

	// ── Settings ─────────────────────────────────────────────────────────────

	private void OnLock(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not SettingRowVm row || Model is not { CanEdit: true })
		{
			return;
		}

		if (!row.IsSet)
		{
			row.Unlock();
			return;
		}

		if (HueConfirm.Ask(Owner, "Use the default again?",
			$"{row.Label} ({row.Key}) is removed from the file when you save, and the default applies again: {row.DefaultText}.",
			"Use default", destructive: true))
		{
			row.Lock();
		}
	}

	private void OnBrowse(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not SettingRowVm { IsEditable: true } row)
		{
			return;
		}

		if (Choose(row.Label, row.Text, folder: row.Info.Kind == SettingKind.Folder) is { } chosen)
		{
			row.Text = chosen;
		}
	}

	private void OnBrowseCell(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is TableCellVm { CanEdit: true } cell
			&& Choose(cell.Header, cell.Text, folder: false) is { } chosen)
		{
			cell.Text = chosen;
		}
	}

	/// <summary>A file or folder picked in the Windows dialog, relative when it lies in the program's folder; null when cancelled.</summary>
	private string? Choose(string title, string current, bool folder)
	{
		var start = StartFolder(current);
		if (folder)
		{
			var dialog = new OpenFolderDialog { Title = title, InitialDirectory = start };
			return dialog.ShowDialog(Owner) == true ? AppPaths.Relativize(dialog.FolderName) : null;
		}

		var files = new OpenFileDialog { Title = title, InitialDirectory = start, CheckFileExists = false };
		return files.ShowDialog(Owner) == true ? AppPaths.Relativize(files.FileName) : null;
	}

	/// <summary>Where a path dialog opens: the folder of the current value, if it exists. The Windows dialogs cannot be masked, so none while the screen is.</summary>
	private static string StartFolder(string current)
	{
		if (Mask.Instance.IsActive || string.IsNullOrWhiteSpace(current))
		{
			return string.Empty;
		}

		try
		{
			var full = Path.GetFullPath(current, AppPaths.BaseDirectory);
			return Directory.Exists(full) ? full
				: Path.GetDirectoryName(full) is { } parent && Directory.Exists(parent) ? parent
				: string.Empty;
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return string.Empty;
		}
	}

	// ── Tables ───────────────────────────────────────────────────────────────

	private void OnAddRow(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is SettingTableVm table)
		{
			table.AddRow();
		}
	}

	private void OnRemoveRow(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is TableRowVm row)
		{
			row.Table.RemoveRow(row);
		}
	}

	// ── Entry lists, chips, boilerplate groups ───────────────────────────────

	private static T? Context<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

	private void OnAddEntry(object sender, RoutedEventArgs e) => Context<EntryListVm>(sender)?.Add();

	private void OnRemoveEntry(object sender, RoutedEventArgs e)
	{
		if (Context<EntryVm>(sender) is { } entry)
		{
			entry.List.Remove(entry);
		}
	}

	private void OnEditEntry(object sender, RoutedEventArgs e)
	{
		if (Context<EntryVm>(sender) is { CanEdit: true } entry)
		{
			entry.IsEditing = true;
		}
	}

	private void OnAddGroup(object sender, RoutedEventArgs e) => Context<BoilerplateVm>(sender)?.AddGroup();

	private void OnRemoveGroup(object sender, RoutedEventArgs e)
	{
		if (Context<BoilerplateGroupVm>(sender) is { } group
			&& HueConfirm.Ask(Owner, "Remove this boilerplate group?",
				"Its reference pages and parts are removed from the settings when you save. Pages it governed are then checked in full.",
				"Remove", destructive: true))
		{
			group.Owner.RemoveGroup(group);
		}
	}

	private void OnAddReferencePage(object sender, RoutedEventArgs e) => Context<BoilerplateGroupVm>(sender)?.AddReferencePage();

	private void OnEditReferencePage(object sender, RoutedEventArgs e)
	{
		if (Context<ReferencePageVm>(sender) is { CanEdit: true } referencePage)
		{
			referencePage.IsEditing = true;
		}
	}

	private void OnRemoveReferencePage(object sender, RoutedEventArgs e)
	{
		if (Context<ReferencePageVm>(sender) is { } referencePage)
		{
			referencePage.Group.RemoveReferencePage(referencePage);
		}
	}

	private void OnAddPart(object sender, RoutedEventArgs e) => Context<BoilerplateGroupVm>(sender)?.AddPart();

	private void OnRemovePart(object sender, RoutedEventArgs e)
	{
		if (Context<PartVm>(sender) is { } part)
		{
			part.Group.RemovePart(part);
		}
	}

	// ── Chip sets, suppression rules, pattern groups ─────────────────────────

	private void OnAddChip(object sender, RoutedEventArgs e) => Context<ChipAddVm>(sender)?.Set.Add();

	private void OnEditChip(object sender, RoutedEventArgs e)
	{
		if (Context<FreeChipVm>(sender) is { CanEdit: true } chip)
		{
			chip.IsEditing = true;
		}
	}

	private void OnRemoveChip(object sender, RoutedEventArgs e)
	{
		if (Context<FreeChipVm>(sender) is { CanEdit: true } chip)
		{
			chip.Remove();
		}
	}

	private void OnAddSuppression(object sender, RoutedEventArgs e) => Context<SuppressionsVm>(sender)?.Add();

	private void OnRemoveSuppression(object sender, RoutedEventArgs e)
	{
		if (Context<SuppressionVm>(sender) is { } rule)
		{
			rule.Owner.Remove(rule);
		}
	}

	/// <summary>"+ Add pattern group": the purposes as a menu; the chosen one is the starting point.</summary>
	private void OnAddPatternGroup(object sender, RoutedEventArgs e)
	{
		if (sender is not Button button || Context<PatternsVm>(sender) is not { CanEdit: true } patterns)
		{
			return;
		}

		var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
		foreach (var purpose in patterns.Purposes)
		{
			var item = new MenuItem { Header = purpose };
			item.Click += (_, _) => patterns.Add(purpose);
			menu.Items.Add(item);
		}

		menu.IsOpen = true;
	}

	private void OnRemovePatternGroup(object sender, RoutedEventArgs e)
	{
		if (Context<PatternGroupVm>(sender) is { } group
			&& (group.IsNew || HueConfirm.Ask(Owner, "Remove this pattern group?",
				"It is removed from the settings when you save; its findings no longer appear.", "Remove", destructive: true)))
		{
			group.Owner.Remove(group);
		}
	}

	private void OnAddMarker(object sender, RoutedEventArgs e) => Context<PatternGroupVm>(sender)?.AddMarker();

	private void OnRequestFold(object sender, RoutedEventArgs e) => Context<PatternGroupVm>(sender)?.RequestFold();

	/// <summary>An edit box that appears takes the keyboard at once, its text selected.</summary>
	private void OnEditBoxShown(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (sender is TextBox box && e.NewValue is true)
		{
			Dispatcher.InvokeAsync(() =>
			{
				box.Focus();
				Keyboard.Focus(box);
				box.SelectAll();
			}, DispatcherPriority.Input);
		}
	}

	/// <summary>Enter takes the text, as leaving the box does.</summary>
	private void OnEditBoxKey(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && sender is TextBox box)
		{
			box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
			e.Handled = true;
		}
	}

	// ── Scroll position per tab ──────────────────────────────────────────────
	// WPF keeps one page for all tabs and swaps the data underneath, so the scroll position
	// would carry over. Each tab remembers its own and gets it back when shown again.

	private bool restoringScroll;

	private void OnTabScrollerContextChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (sender is not ScrollViewer scroller || e.NewValue is not SettingsTabVm tab)
		{
			return;
		}

		restoringScroll = true;
		Dispatcher.InvokeAsync(() =>
		{
			scroller.ScrollToVerticalOffset(tab.ScrollOffset);
			restoringScroll = false;
		}, DispatcherPriority.Loaded);
	}

	private void OnTabScrolled(object sender, ScrollChangedEventArgs e)
	{
		if (!restoringScroll && sender is ScrollViewer { DataContext: SettingsTabVm tab } scroller)
		{
			tab.ScrollOffset = scroller.VerticalOffset;
		}
	}

	// ── Sites ────────────────────────────────────────────────────────────────

	private void OnAddSite(object sender, RoutedEventArgs e)
	{
		if (Model is { CanEdit: true } model)
		{
			model.AddSite();
		}
	}

	private void OnRemoveSite(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not SiteRowVm site || Model is not { CanEdit: true } model)
		{
			return;
		}

		var message = site.IsNew
			? $"{site.Name} is removed from the list."
			: $"{site.Name} is removed from the settings when you save. Its snapshots, triage ledger and site dictionary stay on disk; adding the site again with the same address picks them up.";
		if (HueConfirm.Ask(Owner, "Remove this site?", Mask.Apply(message), "Remove", destructive: true))
		{
			model.RemoveSite(site);
		}
	}

	// ── Save ─────────────────────────────────────────────────────────────────

	private void OnDiscard(object sender, RoutedEventArgs e)
	{
		if (Model is not { IsDirty: true } model)
		{
			return;
		}

		if (HueConfirm.Ask(Owner, "Discard the changes?",
			"Everything changed since the last save is dropped and the file is read again.", "Discard", destructive: true))
		{
			model.Load(model.FilePath);
		}
	}

	private void OnSave(object sender, RoutedEventArgs e)
	{
		if (Model is not { CanSave: true } model)
		{
			return;
		}

		// Values the file states that equal the default: asked one by one, once per session.
		var restated = model.RestatedDefaultsToAsk();
		for (int i = 0; i < restated.Count; i++)
		{
			var setting = restated[i];
			var choice = HueMessage.Choose(Owner, $"This setting repeats its default — {i + 1} of {restated.Count}",
				$"{setting.Label} ({setting.Path}) is in the file with the same value as the built-in default.\n\n"
				+ "Use default: the entry leaves the file, and a future change of the default applies.\n"
				+ "Keep in file: the value stays fixed, even if the default changes later.\n\n"
				+ "Your answer is remembered while the app runs.",
				"Use default", "Keep in file", "Cancel save");
			if (choice == HueChoice.Cancel)
			{
				return;
			}

			model.Answer(setting, useDefault: choice == HueChoice.Yes);
		}

		var unknown = model.UnknownKeys();
		if (unknown.Count > 0 && !HueConfirm.Ask(Owner, "Entries the app does not know",
			"The file has entries this version has no setting for — left over from an older version, or mistyped. They are not kept when the file is saved:\n\n"
			+ string.Join("\n", unknown.Select(k => "• " + k)),
			"Save without them"))
		{
			return;
		}

		var moved = model.ChangedSiteUrls();
		if (moved.Count > 0 && !HueConfirm.Ask(Owner, "A site address changed",
			Mask.Apply("Snapshots, ledger and site dictionary are kept in a folder named after the address. After this change the site starts a new history; the old one stays on disk under the old name:\n\n"
				+ string.Join("\n", moved.Select(m => $"• {m.Old} → {m.New}"))),
			"Save"))
		{
			return;
		}

		if (model.CheckBeforeSave() is { } problem)
		{
			HueMessage.Show(Owner, "The settings cannot be saved yet", Mask.Apply(problem));
			return;
		}

		try
		{
			model.Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			HueMessage.Show(Owner, "The file could not be written", Mask.Apply(ex.Message));
			return;
		}

		Saved?.Invoke(this, EventArgs.Empty);
	}
}
