using System;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Run;
using HueThemeKit;

namespace CrawlerSuite.Dialogs;

/// <summary>
/// A themed dialog window built in code: heading, message, a content area and buttons on the
/// right — the same shape as the kit's own dialogs, with room for a list or a table. Every
/// colour comes from the theme through resource references.
/// </summary>
internal sealed class DialogShell
{
	private readonly StackPanel buttons;
	private readonly ContentControl contentHost;
	private string result = string.Empty;

	public DialogShell(Window? owner, string title, string message, double width = 560)
	{
		// Dialogs are modal, so the mask level cannot change while one is open.
		title = Mask.Apply(title);
		message = Mask.Apply(message);
		Window = new Window
		{
			Title = title,
			Owner = owner,
			SizeToContent = SizeToContent.Height,
			Width = width + 56,
			MaxHeight = SystemParameters.WorkArea.Height * 0.9,
			ResizeMode = ResizeMode.CanResizeWithGrip,
			ShowInTaskbar = false,
			WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
		};
		Window.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Window");
		HueTitleBar.Attach(Window);

		var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
		heading.SetResourceReference(FrameworkElement.StyleProperty, "Text.Heading");

		var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
		body.SetResourceReference(FrameworkElement.StyleProperty, "Text.Body");

		contentHost = new ContentControl { Margin = new Thickness(0, 16, 0, 0), Focusable = false };

		buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 24, 0, 0),
		};

		var layout = new DockPanel { LastChildFill = true };
		DockPanel.SetDock(heading, Dock.Top);
		DockPanel.SetDock(body, Dock.Top);
		DockPanel.SetDock(buttons, Dock.Bottom);
		layout.Children.Add(heading);
		layout.Children.Add(body);
		layout.Children.Add(buttons);
		layout.Children.Add(contentHost);

		var root = new Border { Padding = new Thickness(28, 24, 28, 24), Child = layout };
		root.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
		Window.Content = root;
	}

	public Window Window { get; }

	/// <summary>The custom content between message and buttons (a list, a table).</summary>
	public UIElement? Content
	{
		get => contentHost.Content as UIElement;
		set => contentHost.Content = value;
	}

	/// <summary>Adds a button; <paramref name="styleKey"/> null means the default button.</summary>
	public Button AddButton(string text, string? styleKey, string resultKey)
	{
		var button = new Button
		{
			Content = text,
			MinWidth = 96,
			Margin = new Thickness(buttons.Children.Count == 0 ? 0 : 10, 0, 0, 0),
		};
		if (styleKey is not null)
		{
			button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
		}

		button.Click += (_, _) =>
		{
			result = resultKey;
			if (!button.IsCancel)
			{
				Window.DialogResult = true;
			}
		};
		buttons.Children.Add(button);
		return button;
	}

	/// <summary>Shows the dialog and returns the result key of the button used ("" when closed).</summary>
	public string Show(UIElement focus)
	{
		Window.Loaded += (_, _) => focus.Focus();
		Window.ShowDialog();
		return result;
	}
}

/// <summary>A two-button question: a confirm action (primary) and a decline that is also the safe default.</summary>
internal static class TwoChoice
{
	/// <summary>True only when the operator chose the confirm action.</summary>
	public static bool Show(Window? owner, string title, string message, string confirmText, string declineText)
	{
		var shell = new DialogShell(owner, title, message, width: 460);
		var decline = shell.AddButton(declineText, null, "no");
		decline.IsCancel = true;
		decline.IsDefault = true;
		shell.AddButton(confirmText, "Hue.Button.Primary", "yes");
		return shell.Show(decline) == "yes";
	}
}
