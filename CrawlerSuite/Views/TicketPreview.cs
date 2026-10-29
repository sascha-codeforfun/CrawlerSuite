using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Automation;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
using CrawlerSuite.Run;
using HueThemeKit;

namespace CrawlerSuite.Views;

/// <summary>
/// The preview of a page's ticket document, with Save as PDF (the app's own writer: links stay
/// clickable, also where they wrap) and Save as XPS. The preview follows the screen mask; what is saved is always the real
/// document. Saving starts in the user's Documents folder, then the last folder used while the
/// app runs — never the snapshot folder, which holds only what crawl and analysis write. Nothing
/// is written anywhere else, and nothing is remembered after the app closes.
/// </summary>
internal static class TicketPreview
{
	/// <summary>The folder the last save went to, for this run of the app only.</summary>
	private static string? lastFolder;

	/// <param name="language">The document's language (Settings › Triage › Ticket language); the window stays English.</param>
	public static void Show(Window? owner, TriagePageVm page, string snapshotName, Core.Text.DocumentLanguage language = Core.Text.DocumentLanguage.English)
	{
		var version = CopyForAi.AppVersion();
		var shown = OnScreen(new TicketDocument.FooterPaginator(TicketDocument.Build(page, snapshotName, Mask.Apply, language), version,
			Mask.Apply(snapshotName), language));

		var viewer = new DocumentViewer { Document = shown, Background = System.Windows.Media.Brushes.DimGray, Template = PagesOnly() };
		var window = new Window
		{
			Title = "Ticket document",
			Owner = owner,
			Width = 960,
			Height = Math.Min(1000, SystemParameters.WorkArea.Height * 0.92),
			ShowInTaskbar = false,
			WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
		};
		window.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Window");
		HueTitleBar.Attach(window);

		var note = new TextBlock
		{
			Text = Mask.Instance.IsActive
				? "The preview follows the screen mask. The saved document shows the real addresses."
				: "Check the document, then save it to attach it to the ticket.",
			TextWrapping = TextWrapping.Wrap,
			VerticalAlignment = VerticalAlignment.Center,
		};
		note.SetResourceReference(FrameworkElement.StyleProperty, "Text.Caption");

		var pdf = new Button { Content = "Save as PDF…", MinWidth = 130, Margin = new Thickness(12, 0, 0, 0) };
		pdf.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Button.Primary");
		pdf.ToolTip = "Saves the document as a PDF; every link in it stays clickable, also where it wraps.";

		var xps = new Button { Content = "Save as XPS…", MinWidth = 130, Margin = new Thickness(12, 0, 0, 0) };
		xps.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Button.Tonal");

		var close = new Button { Content = "Close", MinWidth = 100, Margin = new Thickness(12, 0, 0, 0), IsCancel = true };
		close.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Button.Outline");
		close.Click += (_, _) => window.Close();

		pdf.Click += (_, _) => SavePdf(window, page, snapshotName, version, language);
		xps.Click += (_, _) => SaveXps(window, page, snapshotName, version, language);

		var bar = new DockPanel { Margin = new Thickness(20, 12, 20, 12), LastChildFill = true };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal };
		buttons.Children.Add(pdf);
		buttons.Children.Add(xps);
		buttons.Children.Add(close);
		DockPanel.SetDock(buttons, Dock.Right);
		bar.Children.Add(buttons);
		bar.Children.Add(note);

		var layout = new DockPanel();
		DockPanel.SetDock(bar, Dock.Bottom);
		layout.Children.Add(bar);
		var tools = Tools(viewer);
		DockPanel.SetDock(tools, Dock.Top);
		layout.Children.Add(tools);
		layout.Children.Add(viewer);
		var root = new Border { Child = layout };
		root.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
		window.Content = root;

		window.ShowDialog();
	}

	/// <summary>
	/// The viewer without its built-in tool and search bars — light Windows parts the app's dark theme
	/// cannot reach — only its pages: the template's one required part, a scroll viewer named
	/// PART_ContentHost, which the viewer fills with the pages itself.
	/// </summary>
	private static ControlTemplate PagesOnly() =>
		(ControlTemplate)XamlReader.Parse(
			"<ControlTemplate TargetType=\"DocumentViewer\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
			"xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
			"<Border Background=\"{TemplateBinding Background}\">" +
			"<ScrollViewer x:Name=\"PART_ContentHost\" CanContentScroll=\"True\" HorizontalScrollBarVisibility=\"Auto\" " +
			"VerticalScrollBarVisibility=\"Auto\" Focusable=\"False\" />" +
			"</Border></ControlTemplate>");

	/// <summary>The preview's own tool bar, in the app's theme: zoom, fit to width, a whole page, two pages side by side.</summary>
	private static FrameworkElement Tools(DocumentViewer viewer)
	{
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 8, 16, 8) };
		void Add(string label, string tip, Action act)
		{
			var button = new Button { Content = label, MinWidth = 40, Margin = new Thickness(0, 0, 8, 0), ToolTip = tip };
			button.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Button.Outline");
			button.Click += (_, _) => act();
			AutomationProperties.SetName(button, tip);
			bar.Children.Add(button);
		}

		Add("−", "Zoom out", viewer.DecreaseZoom);
		Add("+", "Zoom in", viewer.IncreaseZoom);
		Add("Fit width", "Fit the page to the window's width", viewer.FitToWidth);
		Add("Whole page", "Show one whole page", viewer.FitToHeight);
		Add("Two pages", "Show two pages side by side", () => viewer.FitToMaxPagesAcross(2));
		var root = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
		root.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
		root.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
		return root;
	}

	/// <summary>The saved document: always the real texts, whatever the screen mask shows.</summary>
	private static DocumentPaginator Real(TriagePageVm page, string snapshotName, string version, Core.Text.DocumentLanguage language) =>
		RealDocument(page, snapshotName, version, language).Pages;

	/// <summary>The real document with its footed pages; the PDF writer reads the links from the document.</summary>
	private static (FlowDocument Document, DocumentPaginator Pages) RealDocument(TriagePageVm page, string snapshotName, string version,
		Core.Text.DocumentLanguage language)
	{
		var document = TicketDocument.Build(page, snapshotName, text => text, language);
		return (document, new TicketDocument.FooterPaginator(document, version, snapshotName, language));
	}

	private static void SaveXps(Window owner, TriagePageVm page, string snapshotName, string version, Core.Text.DocumentLanguage language)
	{
		var dialog = new Microsoft.Win32.SaveFileDialog
		{
			Title = "Save the ticket document as XPS",
			FileName = TicketDocument.SuggestedName(page.Url) + ".xps",
			DefaultExt = ".xps",
			Filter = "XPS document (*.xps)|*.xps",
			InitialDirectory = StartFolder(),
			OverwritePrompt = true,
		};
		if (dialog.ShowDialog(owner) != true)
		{
			return;
		}

		try
		{
			if (File.Exists(dialog.FileName))
			{
				File.Delete(dialog.FileName); // the overwrite was confirmed in the dialog
			}

			using (var file = new XpsDocument(dialog.FileName, FileAccess.ReadWrite))
			{
				XpsDocument.CreateXpsDocumentWriter(file).Write(Real(page, snapshotName, version, language));
			}

			lastFolder = Path.GetDirectoryName(dialog.FileName);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
		{
			Core.Logger.LogWarning($"Ticket document: could not save {dialog.FileName} — {ex.Message}");
			HueMessage.Show(owner, "The document could not be saved", Mask.Apply(ex.Message));
		}
	}

	private static void SavePdf(Window owner, TriagePageVm page, string snapshotName, string version, Core.Text.DocumentLanguage language)
	{
		var dialog = new Microsoft.Win32.SaveFileDialog
		{
			Title = "Save the ticket document as PDF",
			FileName = TicketDocument.SuggestedName(page.Url) + ".pdf",
			DefaultExt = ".pdf",
			Filter = "PDF document (*.pdf)|*.pdf",
			InitialDirectory = StartFolder(),
			OverwritePrompt = true,
		};
		if (dialog.ShowDialog(owner) != true)
		{
			return;
		}

		try
		{
			var (document, pages) = RealDocument(page, snapshotName, version, language);
			var report = Pdf.FlowDocumentPdf.Save(document, pages, dialog.FileName);
			lastFolder = Path.GetDirectoryName(dialog.FileName);
			Core.Logger.LogInfo($"Ticket document: PDF written — {report.Pages} page(s), {report.Fonts} font(s), " +
				$"{report.GlyphRuns} text run(s), {report.Links} link(s){(report.Skipped.Count > 0 ? "; not exact: " + string.Join("; ", report.Skipped) : string.Empty)}");
			if (report.Skipped.Count > 0)
			{
				HueMessage.Show(owner, "The PDF was saved — with notes",
					Mask.Apply(string.Join(Environment.NewLine, report.Skipped.Select(s => "• " + s))));
			}
		}
		catch (Pdf.PdfExportException ex)
		{
			Core.Logger.LogWarning($"Ticket document: the PDF could not be made — {ex.Message}");
			HueMessage.Show(owner, "The PDF could not be made", Mask.Apply(ex.Message) + Environment.NewLine + Environment.NewLine + "Save as XPS instead.");
		}
	}

	private static string StartFolder() =>
		lastFolder is { } folder && Directory.Exists(folder)
			? folder
			: Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

	/// <summary>
	/// The laid-out pages for the screen: each page the printer and the XPS file get, shown as it
	/// is drawn. Nothing is serialised, so no font is packed and read back for the preview — reading
	/// an XPS package back from memory fails on embedded fonts ("invalid format").
	/// </summary>
	private static FixedDocument OnScreen(DocumentPaginator paginator)
	{
		var document = new FixedDocument();
		document.DocumentPaginator.PageSize = paginator.PageSize;
		for (int i = 0; i < paginator.PageCount; i++)
		{
			var page = paginator.GetPage(i);
			var fixedPage = new FixedPage { Width = page.Size.Width, Height = page.Size.Height, Background = Brushes.White };
			fixedPage.Children.Add(new PageVisualHost(page.Visual, page.Size));
			var content = new PageContent();
			((IAddChild)content).AddChild(fixedPage);
			document.Pages.Add(content);
		}

		return document;
	}

	/// <summary>Hosts a page's drawing as an element, so a fixed page can show it.</summary>
	private sealed class PageVisualHost : FrameworkElement
	{
		private readonly Visual visual;
		private readonly Size size;

		public PageVisualHost(Visual visual, Size size)
		{
			this.visual = visual;
			this.size = size;
			AddVisualChild(visual);
		}

		protected override int VisualChildrenCount => 1;

		protected override Visual GetVisualChild(int index) =>
			index == 0 ? visual : throw new ArgumentOutOfRangeException(nameof(index));

		protected override Size MeasureOverride(Size availableSize) => size;
	}
}
