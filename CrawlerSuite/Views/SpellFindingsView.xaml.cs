using System;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Run;
using HueThemeKit;

namespace CrawlerSuite.Views;

public partial class SpellFindingsView : UserControl
{
	public SpellFindingsView()
	{
		InitializeComponent();
	}

	private SpellFindingsModel? Model => DataContext as SpellFindingsModel;

	private static SpellWordVm? WordOf(object sender) => (sender as FrameworkElement)?.DataContext as SpellWordVm;

	private void OnUser(object sender, RoutedEventArgs e)
	{
		if (Model is { } model && WordOf(sender) is { } word)
		{
			Report(model.AddToUser(word, ConfirmShared));
			EndRowAction();
		}
	}

	private void OnSite(object sender, RoutedEventArgs e)
	{
		if (Model is { } model && WordOf(sender) is { } word)
		{
			Report(model.AddToSite(word));
			EndRowAction();
		}
	}

	private void OnTriage(object sender, RoutedEventArgs e)
	{
		if (Model is { } model && WordOf(sender) is { } word)
		{
			model.SendToTriage(word);
			EndRowAction();
		}
	}

	/// <summary>"Find in Triage": the main window opens Triage, searched for the word.</summary>
	public event EventHandler<string>? FindInTriageRequested;

	private void OnFindInTriage(object sender, RoutedEventArgs e)
	{
		if (WordOf(sender) is { } word)
		{
			EndRowAction();
			FindInTriageRequested?.Invoke(this, word.Word);
		}
	}

	private void OnUndo(object sender, RoutedEventArgs e)
	{
		if (Model is { } model && WordOf(sender) is { } word)
		{
			Report(model.Undo(word));
			EndRowAction();
		}
	}

	/// <summary>
	/// A button inside a row may leave the table mid-edit, and a table mid-edit refuses to be
	/// refiltered (the language chips would then do nothing): end that state after every row action.
	/// </summary>
	private void EndRowAction()
	{
		WordGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
		WordGrid.CancelEdit(DataGridEditingUnit.Row);
	}

	private void OnCopyList(object sender, RoutedEventArgs e)
	{
		if (Model is { } model)
		{
			CopyForAi.Copy(CopyListButton, model.ShownAsText());
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
