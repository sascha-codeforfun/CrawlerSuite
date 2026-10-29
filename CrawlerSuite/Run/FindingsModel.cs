using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CrawlerSuite.Core.Findings;

namespace CrawlerSuite.Run;

/// <summary>A tab on the Results page: a group of further tabs, a findings table, or the file list.</summary>
public abstract class FindingTabVm : Observable
{
	protected FindingTabVm(string title, string note, int count, bool written)
	{
		Title = title;
		Note = note;
		Count = count;
		IsWritten = written;
	}

	public string Title { get; }

	/// <summary>Where the rows come from, or a hint for the group.</summary>
	public string Note { get; }

	public int Count { get; }

	public bool IsWritten { get; }

	/// <summary>The number on the tab; a dash when the source was not written.</summary>
	public virtual string CountText => IsWritten ? Format.Count(Count) : "–";

	/// <summary>Empty tabs stay in place but are dimmed.</summary>
	public bool IsEmpty => Count == 0;

	/// <summary>
	/// Builds the tab tree from the engine's findings. <paramref name="sources"/> gives each page
	/// its source from the content list (null: no content list). Safe off the UI thread.
	/// </summary>
	public static FindingTabVm From(FindingNode node, Func<string, PageSource?>? sources = null) =>
		node.Table is not null || node.Children.Count == 0
			? new FindingLeafVm(node, sources)
			: new FindingBranchVm(node, sources);
}

/// <summary>A tab whose content is a row of further tabs.</summary>
public sealed class FindingBranchVm : FindingTabVm
{
	public FindingBranchVm(FindingNode node, Func<string, PageSource?>? sources = null)
		: base(node.Title, node.Note, node.Count, node.IsWritten)
	{
		Children = node.Children.Select(c => From(c, sources)).ToList();
	}

	public IReadOnlyList<FindingTabVm> Children { get; }

	private FindingTabVm? selectedChild;

	/// <summary>The tab of this group on screen.</summary>
	public FindingTabVm? SelectedChild { get => selectedChild; set => Set(ref selectedChild, value); }
}

/// <summary>A tab whose content is one findings table, with a filter.</summary>
public sealed class FindingLeafVm : FindingTabVm
{
	private ListCollectionView? view;
	private string filterText = string.Empty;

	public FindingLeafVm(FindingNode node, Func<string, PageSource?>? sources = null)
		: base(node.Title, node.Note, node.Count, node.IsWritten)
	{
		Columns = node.Table?.Columns ?? [];
		int page = sources is null ? -1 : Columns.ToList().FindIndex(c => c.IsPage);
		Rows = node.Table?.Rows.Select(r => new FindingRowVm(r,
			page >= 0 && page < r.Cells.Length ? sources!(r.Cells[page]) : null,
			page >= 0 && page < r.Cells.Length ? r.Cells[page] : string.Empty)).ToList() ?? [];
		Problem = node.Table?.Problem;
		Basis = node.Basis;
		HasSources = Rows.Any(r => r.Source is not null);
	}

	/// <summary>
	/// True when at least one row's page has a source in the content list: the table then starts
	/// with the Source column. A site without that information shows no empty column.
	/// </summary>
	public bool HasSources { get; }

	/// <summary>The standard the check is based on (accessibility checks), shown above the table.</summary>
	public FindingBasis? Basis { get; }

	/// <summary>Why the source file could not be read; null when it was.</summary>
	public string? Problem { get; }

	public IReadOnlyList<FindingColumn> Columns { get; }

	public List<FindingRowVm> Rows { get; }

	/// <summary>The rows as the grid shows them (sorted and filtered). Created on the UI thread, on first use.</summary>
	public ICollectionView View
	{
		get
		{
			if (view is null)
			{
				view = new ListCollectionView(Rows);
			}

			return view;
		}
	}

	/// <summary>Rows whose cells contain this text (any column, ignoring case) are shown.</summary>
	public string FilterText
	{
		get => filterText;
		set
		{
			if (Set(ref filterText, value ?? string.Empty))
			{
				// No filter at all when empty: a filtered view keeps its own copy of every row.
				View.Filter = filterText.Length == 0 ? null : Matches;
				Raise(nameof(Summary));
			}
		}
	}

	/// <summary>"1,234 rows", or "56 of 1,234 rows" while filtered.</summary>
	public string Summary
	{
		get
		{
			if (!IsWritten)
			{
				return "Not written in this snapshot";
			}

			if (Problem is not null)
			{
				return Mask.Apply(Problem);
			}

			var shown = filterText.Length == 0 || view is null ? Rows.Count : view.Count;
			var rows = shown == Rows.Count ? $"{Format.Count(Rows.Count)} row(s)" : $"{Format.Count(shown)} of {Format.Count(Rows.Count)} row(s)";
			return Note.Length == 0 ? rows : $"{rows} · {Note}";
		}
	}

	private bool Matches(object item)
	{
		if (filterText.Length == 0)
		{
			return true;
		}

		// While the screen is masked, match what is shown — the row count must not reveal hidden text.
		var masked = Mask.Instance.IsActive;
		var row = (FindingRowVm)item;
		if (row.SourceText.Contains(filterText, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		var cells = row.Cells;
		for (int i = 0; i < cells.Length; i++)
		{
			var text = masked ? Mask.Apply(cells[i]) : cells[i];
			if (text.Contains(filterText, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}
}

/// <summary>The last tab: every output file of the snapshot, as before the findings views.</summary>
public sealed class FilesTabVm : FindingTabVm
{
	public FilesTabVm(ObservableCollection<ResultRow> rows, int written)
		: base("Files", "Every output file of the snapshot. Double-click a written file to open it.", written, written > 0)
	{
		Rows = rows;
	}

	public ObservableCollection<ResultRow> Rows { get; }
}

/// <summary>
/// One row of a findings table. The grid binds to C0…C15 (DataGrid columns need properties to
/// sort by); <see cref="LocalFile"/> is the downloaded file the row is about, when known.
/// </summary>
public sealed class FindingRowVm : INotifyPropertyChanged
{
	// Rows never change. Implementing the interface keeps each cell binding from falling back to
	// the costly property-descriptor change tracking.
	public event PropertyChangedEventHandler? PropertyChanged
	{
		add { }
		remove { }
	}

	public FindingRowVm(FindingRow row, PageSource? source = null, string page = "")
	{
		Cells = row.Cells;
		LocalFile = row.LocalFile;
		Source = source;
		NoSourceAddress = source is null ? page : string.Empty;
	}

	/// <summary>The row's page when it has no source: the Source column names its host instead of staying empty.</summary>
	public string NoSourceAddress { get; }

	public string[] Cells { get; }

	/// <summary>Where the row's page comes from (the content list's source value and label, and the CMS link); null when unknown.</summary>
	public PageSource? Source { get; }

	/// <summary>The source value and label as the content list has them; empty when unknown.</summary>
	public string SourceText => Source?.Text ?? string.Empty;

	/// <summary>The CMS editor link for the row's page; empty when it cannot be built.</summary>
	public string CmsLink => Source?.CmsLink ?? string.Empty;

	/// <summary>Which configured key the row's page source matched (the chip's colour).</summary>
	public PageSourceRole SourceRole => Source?.Role ?? PageSourceRole.Other;

	public string? LocalFile { get; }

	public string C0 => Cell(0);
	public string C1 => Cell(1);
	public string C2 => Cell(2);
	public string C3 => Cell(3);
	public string C4 => Cell(4);
	public string C5 => Cell(5);
	public string C6 => Cell(6);
	public string C7 => Cell(7);
	public string C8 => Cell(8);
	public string C9 => Cell(9);
	public string C10 => Cell(10);
	public string C11 => Cell(11);
	public string C12 => Cell(12);
	public string C13 => Cell(13);
	public string C14 => Cell(14);
	public string C15 => Cell(15);

	/// <summary>The most columns a findings table may have.</summary>
	public const int MaxColumns = 16;

	private string Cell(int index) => index < Cells.Length ? Cells[index] : string.Empty;
}
