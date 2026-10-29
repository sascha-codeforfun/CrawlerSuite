using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CrawlerSuite.Run;
using HueThemeKit;

namespace CrawlerSuite.Views;

/// <summary>
/// A picture a page carries as base64, in a window of its own: at full size (scroll bars when it
/// is larger than the window) or fitted to the window; an SVG as its text. Save as… writes the
/// embedded bytes exactly as the source holds them — nothing re-encoded — to a place the operator
/// picks; nothing is written into the snapshot.
/// </summary>
internal static class PictureWindow
{
	private static string? lastFolder;

	/// <summary>A drawing (an SVG) is shown this large on its longer side at "full size".</summary>
	private const double VectorSide = 512;

	public static void Show(Window? owner, EmbeddedPictureVm picture)
	{
		var window = new Window
		{
			Title = picture.Title,
			Owner = owner,
			ShowInTaskbar = false,
			WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
		};
		window.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Window");
		HueTitleBar.Attach(window);

		var label = new TextBlock { Text = picture.Label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
		label.SetResourceReference(FrameworkElement.StyleProperty, "Text.Caption");
		var verdict = new TextBlock { Text = picture.Note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
		verdict.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
		var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		words.Children.Add(label);
		if (picture.Note.Length > 0)
		{
			words.Children.Add(verdict);
		}

		var buttons = new StackPanel { Orientation = Orientation.Horizontal };
		ToggleButton? fit = null;
		if (picture.HasImage)
		{
			fit = new ToggleButton { Content = "Fit to window", MinWidth = 120, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Fitted to the window, or at its full size with scroll bars." };
			fit.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Button.Tonal");
			buttons.Children.Add(fit);
		}

		var save = new Button { Content = "Save as…", MinWidth = 110, Margin = new Thickness(12, 0, 0, 0), IsEnabled = picture.CanSave,
			ToolTip = "Writes the embedded bytes exactly as the source holds them, to a place you choose." };
		save.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Button.Primary");
		save.Click += (_, _) => Save(window, picture);
		buttons.Children.Add(save);

		var close = new Button { Content = "Close", MinWidth = 100, Margin = new Thickness(12, 0, 0, 0), IsCancel = true };
		close.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Button.Outline");
		close.Click += (_, _) => window.Close();
		buttons.Children.Add(close);

		var bar = new DockPanel { Margin = new Thickness(20, 12, 20, 12), LastChildFill = true };
		DockPanel.SetDock(buttons, Dock.Right);
		bar.Children.Add(buttons);
		bar.Children.Add(words);

		// A drawing scales without loss: its full size is larger than its preview.
		double fullScale = picture.IsVector ? VectorSide / Math.Max(1, Math.Max(picture.PixelWidth, picture.PixelHeight)) : 1;
		double fullWidth = picture.PixelWidth * fullScale, fullHeight = picture.PixelHeight * fullScale;

		FrameworkElement body;
		if (picture.HasImage)
		{
			var image = new Image
			{
				Source = picture.Image,
				Width = fullWidth,
				Height = fullHeight,
				Stretch = Stretch.Fill,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			var scroll = new ScrollViewer
			{
				Content = image,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			};
			fit!.Checked += (_, _) =>
			{
				fit.Content = "Full size";
				image.Width = double.NaN;
				image.Height = double.NaN;
				image.Stretch = Stretch.Uniform;
				image.StretchDirection = picture.IsVector ? StretchDirection.Both : StretchDirection.DownOnly;
				scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
				scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
			};
			fit.Unchecked += (_, _) =>
			{
				fit.Content = "Fit to window";
				image.Width = fullWidth;
				image.Height = fullHeight;
				image.Stretch = Stretch.Fill;
				scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
				scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
			};
			body = scroll;
		}
		else
		{
			var text = new TextBox
			{
				Text = picture.SvgText ?? picture.Problem,
				IsReadOnly = true,
				TextWrapping = TextWrapping.Wrap,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				BorderThickness = new Thickness(0),
				Padding = new Thickness(12),
			};
			text.SetResourceReference(Control.FontFamilyProperty, "Font.Mono");
			body = text;
		}

		var frame = new Border { Child = body, Margin = new Thickness(20, 0, 20, 20) };
		frame.SetResourceReference(Border.BackgroundProperty, "Brush.Raised");

		var root = new DockPanel();
		DockPanel.SetDock(bar, Dock.Top);
		root.Children.Add(bar);
		root.Children.Add(frame);
		window.Content = root;

		// As large as the picture needs, within most of the screen.
		var area = SystemParameters.WorkArea;
		window.Width = Math.Min(area.Width * 0.9, Math.Max(640, (picture.HasImage ? fullWidth : 800) + 80));
		window.Height = Math.Min(area.Height * 0.9, Math.Max(420, (picture.HasImage ? fullHeight : 600) + 150));
		window.Show();
	}

	private static void Save(Window owner, EmbeddedPictureVm picture)
	{
		if (picture.Bytes is not { } bytes)
		{
			return;
		}

		var dialog = new Microsoft.Win32.SaveFileDialog
		{
			Title = "Save the embedded picture",
			FileName = picture.SuggestedName,
			DefaultExt = picture.Extension,
			Filter = $"{picture.KindName} (*{picture.Extension})|*{picture.Extension}|All files (*.*)|*.*",
			InitialDirectory = lastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
			OverwritePrompt = true,
		};
		if (dialog.ShowDialog(owner) != true)
		{
			return;
		}

		try
		{
			File.WriteAllBytes(dialog.FileName, bytes);
			lastFolder = Path.GetDirectoryName(dialog.FileName);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Core.Logger.LogWarning($"Comparison: could not save an embedded picture — {ex.Message}");
			HueMessage.Show(owner, "The picture could not be saved", Mask.Apply(ex.Message));
		}
	}
}
