using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Core.Host;
using CrawlerSuite.Run;

namespace CrawlerSuite.Dialogs;

/// <summary>
/// Unverified assets whose URL extension, declared Content-Type and bytes all agree they are
/// images can be promoted into the index. Promotion means they are parsed by the analyzers, so
/// it is the operator's decision; the safe default leaves everything unverified.
/// </summary>
internal static class ReSettleDialog
{
	public static bool Show(Window owner, ReSettlePrompt prompt)
	{
		var shell = new DialogShell(owner, $"{prompt.UnverifiedCount} unverified asset(s)",
			$"{prompt.Promote.Count} can be promoted — all three signals agree they are images. " +
			"Promoting moves them into the index, where their content and metadata are analysed. " +
			"Only proceed if you trust their origin.");

		var panel = new StackPanel();
		AddSection(panel, "WILL BE PROMOTED", prompt.Promote.Select(p => $"{p.FinalName}  ·  {(string.IsNullOrEmpty(p.Url) ? "no URL in 01" : p.Url)}"));
		if (prompt.HeldBack.Count > 0)
		{
			AddSection(panel, "HELD BACK (left unverified)", prompt.HeldBack.Select(h => $"{h.Name}  ·  {h.Reason}"));
		}

		shell.Content = new ScrollViewer { Content = panel, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

		var leave = shell.AddButton("Leave unverified", null, "leave");
		leave.IsCancel = true;
		leave.IsDefault = true;
		shell.AddButton($"Promote {prompt.Promote.Count}", "Hue.Button.Primary", "promote");
		return shell.Show(leave) == "promote";
	}

	private static void AddSection(StackPanel panel, string title, System.Collections.Generic.IEnumerable<string> lines)
	{
		var heading = new TextBlock { Text = title, Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 12, 0, 4) };
		heading.SetResourceReference(FrameworkElement.StyleProperty, "Text.Section");
		panel.Children.Add(heading);
		foreach (var line in lines)
		{
			var text = new TextBlock { Text = Mask.Apply(line), TextWrapping = TextWrapping.Wrap };
			text.SetResourceReference(FrameworkElement.StyleProperty, "Text.Mono");
			panel.Children.Add(text);
		}
	}
}
