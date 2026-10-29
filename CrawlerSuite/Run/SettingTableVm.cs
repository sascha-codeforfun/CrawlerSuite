using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using CrawlerSuite.Core.Settings;

namespace CrawlerSuite.Run;

/// <summary>
/// A list or map setting as a table: a heading per column, a row per entry, add and remove.
/// Every change is written back to the setting at once; rows with a problem are marked, and
/// Save waits until they are fixed.
/// </summary>
public sealed class SettingTableVm : Observable
{
	private readonly SettingRowVm row;
	private bool hasProblems;

	public SettingTableVm(TableSpec spec, JsonNode? value, SettingRowVm row)
	{
		Spec = spec;
		this.row = row;
		Headers = spec.Columns.Select(c => new TableHeaderVm(c.Header, c.Width)).ToList();
		foreach (var data in SettingTables.Read(spec, value))
		{
			Rows.Add(new TableRowVm(data, this));
		}

		Check();
	}

	public TableSpec Spec { get; }

	internal SettingRowVm Row => row;

	public IReadOnlyList<TableHeaderVm> Headers { get; }

	public ObservableCollection<TableRowVm> Rows { get; } = [];

	public bool CanEdit => row.IsEditable;

	public bool HasProblems { get => hasProblems; private set => Set(ref hasProblems, value); }

	/// <summary>The table shows a single column without a heading (a plain list).</summary>
	public bool ShowHeaders => Spec.Shape != TableShape.Values;

	public string EmptyText => "No entries.";

	public bool IsEmpty => Rows.Count == 0;

	public void AddRow()
	{
		if (!CanEdit)
		{
			return;
		}

		Rows.Add(new TableRowVm(SettingTables.NewRow(Spec), this));
		Raise(nameof(IsEmpty));
		Changed();
	}

	public void RemoveRow(TableRowVm target)
	{
		if (!CanEdit)
		{
			return;
		}

		Rows.Remove(target);
		Raise(nameof(IsEmpty));
		Changed();
	}

	internal void Changed()
	{
		Check();
		row.ApplyTable(SettingTables.Write(Spec, Rows.Select(r => r.Data)));
	}

	internal void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		foreach (var r in Rows)
		{
			r.RaiseEditable();
		}
	}

	/// <summary>The latest run's numbers changed: each row's usage line follows.</summary>
	internal void RefreshUsage()
	{
		foreach (var r in Rows)
		{
			r.RefreshUsage();
		}
	}

	/// <summary>The primary site's latest counts, for the rows' usage lines.</summary>
	internal UsageReport? Usage => row.Owner.Usage;

	/// <summary>The installed languages changed (the Dictionaries tab): checkboxes and notes follow.</summary>
	internal void RefreshLanguages()
	{
		foreach (var r in Rows)
		{
			foreach (var cell in r.Cells.Where(c => c.IsLanguages))
			{
				cell.BuildOptions();
			}
		}

		Check();
	}

	private void Check()
	{
		var problems = SettingTables.Problems(Spec, Rows.Select(r => r.Data).ToList());
		for (int i = 0; i < Rows.Count; i++)
		{
			Rows[i].SetProblem(problems[i]);
			Rows[i].SetNote(row.NoteFor(Spec, Rows[i].Data));
		}

		HasProblems = problems.Any(p => p is not null);
		Raise(nameof(IsEmpty));
	}
}

/// <summary>A column heading.</summary>
public sealed record TableHeaderVm(string Header, double Width) : ITableCellWidth;

/// <summary>Something laid out by <c>ColumnsPanel</c>: a fixed width, or 0 for the space left.</summary>
public interface ITableCellWidth
{
	double Width { get; }
}

/// <summary>One row of a table.</summary>
public sealed class TableRowVm : Observable
{
	private readonly SettingTableVm table;
	private string problem = string.Empty;

	public TableRowVm(TableRow data, SettingTableVm table)
	{
		Data = data;
		this.table = table;
		Cells = table.Spec.Columns.Select(c => new TableCellVm(c, this)).ToList();
		Usage = TableUsage.For(table.Spec, table.Usage, data.Cells);
	}

	/// <summary>How much the entry was needed in the latest run, where the setting has counts.</summary>
	public UsageLine Usage { get; private set; }

	public bool HasUsage => Usage.Kind != UsageKind.None;

	internal void RefreshUsage()
	{
		Usage = TableUsage.For(table.Spec, table.Usage, Data.Cells);
		Raise(nameof(Usage));
		Raise(nameof(HasUsage));
	}

	internal TableRow Data { get; }

	public IReadOnlyList<TableCellVm> Cells { get; }

	public SettingTableVm Table => table;

	public bool CanEdit => table.CanEdit;

	public string Problem => problem;

	public bool HasProblem => problem.Length > 0;

	/// <summary>What the row does, in one line, where the setting has an example.</summary>
	public string Example => table.Spec.Example?.Invoke(Data.Cells) ?? string.Empty;

	public bool HasExample => Example.Length > 0;

	/// <summary>An amber note: what the row does that may not be intended. It does not stop saving.</summary>
	public string Note { get; private set; } = string.Empty;

	public bool HasNote => Note.Length > 0;

	internal void SetNote(string? value)
	{
		Note = value ?? string.Empty;
		Raise(nameof(Note));
		Raise(nameof(HasNote));
	}

	internal void SetProblem(string? value)
	{
		problem = value ?? string.Empty;
		Raise(nameof(Problem));
		Raise(nameof(HasProblem));
	}

	internal void CellChanged()
	{
		Raise(nameof(Example));
		Raise(nameof(HasExample));
		RefreshUsage();
		table.Changed();
	}

	internal void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		foreach (var cell in Cells)
		{
			cell.RaiseEditable();
		}
	}
}

/// <summary>One cell: a text, a switch, a file with a picker, or languages as checkboxes.</summary>
public sealed class TableCellVm : Observable, ITableCellWidth
{
	private readonly TableColumn column;
	private readonly TableRowVm row;

	public TableCellVm(TableColumn column, TableRowVm row)
	{
		this.column = column;
		this.row = row;
		if (IsLanguages)
		{
			BuildOptions();
		}
	}

	public bool IsLanguages => column.Kind == CellKind.Languages;

	/// <summary>One checkbox per installed language, plus any named here that is not installed (greyed).</summary>
	public ObservableCollection<LanguageOptionVm> Options { get; } = [];

	internal void BuildOptions()
	{
		var ticked = SettingTables.SplitValues(row.Table.Spec, Text);
		var installed = row.Table.Row.Owner.BundleLanguages;
		Options.Clear();
		foreach (var code in installed)
		{
			Options.Add(new LanguageOptionVm(code, ticked.Contains(code, System.StringComparer.OrdinalIgnoreCase), true, this));
		}

		foreach (var code in ticked.Where(t => !installed.Contains(t, System.StringComparer.OrdinalIgnoreCase)))
		{
			Options.Add(new LanguageOptionVm(code, true, false, this));
		}
	}

	internal void OptionsChanged() =>
		Text = string.Join(", ", Options.Where(o => o.IsChecked).Select(o => o.Code));

	public double Width => column.Width;

	public string Header => column.Header;

	public bool IsText => column.Kind == CellKind.Text && !IsMultiline;

	public bool CanEditOptions => CanEdit;

	public bool IsMultiline => column.Kind == CellKind.Text && row.Table.Spec is { Shape: TableShape.MapOfLists, Separator: "\n" }
		&& column == row.Table.Spec.Columns[1];

	public bool IsSwitch => column.Kind == CellKind.Switch;

	public bool IsFile => column.Kind == CellKind.File;

	public bool CanEdit => row.CanEdit;

	public string Text
	{
		get => row.Data[column.Field];
		set
		{
			value ??= string.Empty;
			if (value == row.Data[column.Field])
			{
				return;
			}

			row.Data[column.Field] = value;
			Raise();
			row.CellChanged();
		}
	}

	public bool IsOn
	{
		get => row.Data[column.Field] == "true";
		set
		{
			if (value == IsOn)
			{
				return;
			}

			row.Data[column.Field] = value ? "true" : "false";
			Raise();
			row.CellChanged();
		}
	}

	internal void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		foreach (var option in Options)
		{
			option.RaiseEditable();
		}
	}
}

/// <summary>One language checkbox. A language without a dictionary bundle is greyed out but stays ticked in the file.</summary>
public sealed class LanguageOptionVm : Observable
{
	private readonly TableCellVm cell;
	private bool isChecked;

	public LanguageOptionVm(string code, bool isChecked, bool isInstalled, TableCellVm cell)
	{
		Code = code;
		this.isChecked = isChecked;
		IsInstalled = isInstalled;
		this.cell = cell;
	}

	public string Code { get; }

	public bool IsInstalled { get; }

	public string ToolTip => IsInstalled ? Code : $"{Code}: no dictionary bundle — install to use";

	public bool CanEdit => cell.CanEdit;

	public bool IsChecked
	{
		get => isChecked;
		set
		{
			if (Set(ref isChecked, value))
			{
				cell.OptionsChanged();
			}
		}
	}

	internal void RaiseEditable() => Raise(nameof(CanEdit));
}
