using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Run;

namespace CrawlerSuite.Dialogs;

/// <summary>
/// Files in the download folder that have no URL in the crawl index. The operator ticks the
/// ones to rename to the backup extension; the rest are left as they are.
/// </summary>
internal static class OrphansDialog
{
	public static IReadOnlyCollection<string> Show(Window owner, IReadOnlyList<string> orphans, string backupExtension)
	{
		var shell = new DialogShell(owner, $"{orphans.Count} orphan file(s) in the download folder",
			"These files have no URL entry in 02-crawler-index.log — typically a fragment URL download, a file placed by hand, " +
			$"or a partial crawl. Renamed files get the extension {backupExtension} and stay in place; a new crawl resolves them.");

		var boxes = orphans.Select(name => new CheckBox { Content = new TextBlock { Text = Mask.Apply(name) }, Tag = name, IsChecked = true, Margin = new Thickness(0, 2, 0, 2) }).ToList();

		var all = new CheckBox { Content = "All files", IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
		all.Click += (_, _) =>
		{
			foreach (var box in boxes)
			{
				box.IsChecked = all.IsChecked == true;
			}
		};

		var list = new StackPanel();
		foreach (var box in boxes)
		{
			list.Children.Add(box);
		}

		var scroller = new ScrollViewer
		{
			Content = list,
			MaxHeight = 320,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		};

		var panel = new StackPanel();
		panel.Children.Add(all);
		panel.Children.Add(scroller);
		shell.Content = panel;

		var skip = shell.AddButton("Leave all as they are", null, "skip");
		skip.IsCancel = true;
		var rename = shell.AddButton("Rename ticked files", "Hue.Button.Primary", "rename");
		rename.IsDefault = true;

		if (shell.Show(rename) != "rename")
		{
			return [];
		}

		return boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
	}
}
