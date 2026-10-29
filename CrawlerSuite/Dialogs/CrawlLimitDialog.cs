using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Core.Host;
using CrawlerSuite.Run;

namespace CrawlerSuite.Dialogs;

/// <summary>
/// The crawl passed the total download limit and has stopped. Shows what filled it — the
/// largest files and the URL folders taking the most space — then the snapshot is deleted,
/// optionally followed by a fresh crawl. Closing the window deletes without restarting.
/// </summary>
internal static class CrawlLimitDialog
{
	private sealed record FileRow(string Size, string Url, string File);

	private sealed record FolderRow(string Size, string Files, string Folder);

	public static CrawlLimitChoice Show(Window owner, CrawlLimitReport report)
	{
		var shell = new DialogShell(owner, "The crawl was stopped at the download limit",
			$"{Format.Bytes(report.DownloadedBytes)} downloaded in {Format.Count(report.FileCount)} file(s) — past the " +
			$"{Format.Bytes(report.LimitBytes)} limit. Something on the site is producing far more than it should " +
			"(a URL loop, generated pages, broken bundling). The partial snapshot is deleted either way.",
			width: 820);

		var panel = new StackPanel();

		AddHeading(panel, "LARGEST FILES", first: true);
		panel.Children.Add(Grid(report.LargestFiles.Select(f => new FileRow(Format.Bytes(f.Bytes), Mask.Apply(f.Url), Mask.Apply(f.Name))).ToList(),
			("Size", nameof(FileRow.Size)), ("URL", nameof(FileRow.Url)), ("File", nameof(FileRow.File))));

		AddHeading(panel, "FOLDERS TAKING THE MOST SPACE", first: false);
		panel.Children.Add(Grid(report.LargestFolders.Select(f => new FolderRow(Format.Bytes(f.Bytes), Format.Count(f.Files), Mask.ApplyToUrlPath(f.Folder))).ToList(),
			("Size", nameof(FolderRow.Size)), ("Files", nameof(FolderRow.Files)), ("URL folder", nameof(FolderRow.Folder))));

		shell.Content = new ScrollViewer { Content = panel, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

		var delete = shell.AddButton("Delete snapshot", "Hue.Button.Danger", "delete");
		delete.IsCancel = true;
		var restart = shell.AddButton("Delete and start a fresh crawl", "Hue.Button.Primary", "restart");

		return shell.Show(delete) == "restart" ? CrawlLimitChoice.DeleteAndRecrawl : CrawlLimitChoice.Delete;
	}

	private static void AddHeading(StackPanel panel, string text, bool first)
	{
		var heading = new TextBlock { Text = text, Margin = new Thickness(0, first ? 0 : 16, 0, 6) };
		heading.SetResourceReference(FrameworkElement.StyleProperty, "Text.Section");
		panel.Children.Add(heading);
	}

	private static DataGrid Grid<T>(System.Collections.Generic.IReadOnlyList<T> rows, params (string Header, string Path)[] columns)
	{
		var grid = new DataGrid
		{
			ItemsSource = rows,
			AutoGenerateColumns = false,
			IsReadOnly = true,
			HeadersVisibility = DataGridHeadersVisibility.Column,
			CanUserAddRows = false,
		};
		// The dialog scrolls as a whole; each table shows all its (at most ten) rows.
		ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
		grid.PreviewMouseWheel += Views.WheelPassThrough.ToParent;
		grid.SetResourceReference(FrameworkElement.StyleProperty, "Hue.DataGrid.Compact");
		foreach (var (header, path) in columns)
		{
			grid.Columns.Add(new DataGridTextColumn
			{
				Header = header,
				Binding = new System.Windows.Data.Binding(path),
				Width = header is "Size" or "Files" ? DataGridLength.Auto : new DataGridLength(1, DataGridLengthUnitType.Star),
			});
		}

		return grid;
	}
}
