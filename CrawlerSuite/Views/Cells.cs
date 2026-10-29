using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using CrawlerSuite.Core.Display;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Run;
using HueThemeKit;
using DocRun = System.Windows.Documents.Run; // "Run" alone is the CrawlerSuite.Run namespace here

namespace CrawlerSuite.Views;

/// <summary>
/// Base for the findings cells: holds the raw value, draws it through the screen mask, and
/// draws again when the mask level changes (a weak subscription, so recycled cells are not kept alive).
/// </summary>
public abstract class MaskedCell : ContentControl
{
	public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
		nameof(Value), typeof(string), typeof(MaskedCell), new PropertyMetadata(null, (d, _) => ((MaskedCell)d).Render()));

	protected MaskedCell()
	{
		Focusable = false;
		IsTabStop = false;
		VerticalContentAlignment = VerticalAlignment.Center;
		PropertyChangedEventManager.AddHandler(Mask.Instance, (_, _) => Render(), nameof(Mask.Level));
	}

	/// <summary>The raw value from the output file (never masked; the mask is for the screen only).</summary>
	public string? Value
	{
		get => (string?)GetValue(ValueProperty);
		set => SetValue(ValueProperty, value);
	}

	protected abstract void Render();

	/// <summary>Draws again: the inherited site root (<see cref="UrlDisplay"/>) changed.</summary>
	internal void Redraw() => Render();

	/// <summary>The site root this cell shows addresses against (inherited; empty when none is set).</summary>
	protected string Root => UrlDisplay.GetRoot(this);

	/// <summary>A text block that wraps, with its foreground from a theme brush.</summary>
	protected static TextBlock Wrapping(string brushKey = "Brush.Text")
	{
		var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
		text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
		return text;
	}
}

/// <summary>
/// A URL: a link that opens in the default browser, a copy button, and amber when the URL has an
/// obvious defect. While masked the text is masked, but open and copy still use the real URL.
/// </summary>
public sealed class UrlCell : MaskedCell
{
	/// <summary>An address wraps to at most this many lines.</summary>
	internal const int UrlMaxLines = 3;

	internal const double UrlLineHeight = 18;

	protected override void Render()
	{
		var url = Value?.Trim() ?? string.Empty;
		if (url.Length == 0)
		{
			Content = null;
			return;
		}

		// An entry for one defect shared by several pages is no address: its title, nothing to open or copy.
		if (Core.Triage.SharedEntries.IsShared(url))
		{
			var title = Wrapping("Brush.Text");
			title.Inlines.Add(new DocRun(Core.Triage.SharedEntries.TitleOf(url)));
			title.ToolTip = "One defect repeated on several pages: decided and filed once. The pages are listed in the item.";
			Content = title;
			return;
		}

		var defects = UrlDefects.Find(Value);
		var text = Wrapping(defects.Count > 0 ? "Brush.WarnText" : "Brush.Text");

		// At most three lines, so a narrow address column leaves room for the others; the full
		// address is in the tooltip, and open and copy always use it whole.
		text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
		text.LineHeight = UrlLineHeight;
		text.MaxHeight = UrlLineHeight * UrlMaxLines;
		text.TextTrimming = TextTrimming.CharacterEllipsis;
		// On the page's site root the address is shown from its path; the whole address stays in
		// the tooltip and is what open and copy use.
		var shown = new DocRun(UrlDisplay.Shown(url, Root));

		if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
		{
			var link = new Hyperlink(shown) { Cursor = System.Windows.Input.Cursors.Hand };
			link.SetResourceReference(TextElement.ForegroundProperty, defects.Count > 0 ? "Brush.WarnText" : "Brush.AccentText");
			link.Click += (_, _) => Open(url);
			text.Inlines.Add(link);
		}
		else
		{
			text.Inlines.Add(shown);
		}

		text.ToolTip = defects.Count > 0 ? Mask.Apply(url) + Environment.NewLine + string.Join(" · ", defects) : Mask.Apply(url);

		var copy = new Button
		{
			Content = "", // Copy
			FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
			FontSize = 12,
			Width = 24,
			Height = 22,
			Margin = new Thickness(6, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Top,
			ToolTip = "Copy the URL",
		};
		copy.SetResourceReference(StyleProperty, "Hue.Button.Icon");
		AutomationProperties.SetName(copy, "Copy the URL");
		copy.Click += (_, _) => Copy(url);

		var grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.Children.Add(text);
		Grid.SetColumn(copy, 1);
		grid.Children.Add(copy);
		Content = grid;
	}

	internal static void Open(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			HueMessage.Show(Application.Current.MainWindow, "The link could not be opened",
				Mask.Apply(ex.Message) + Environment.NewLine + Environment.NewLine + "Use the copy button to paste it into a browser elsewhere.");
		}
	}

	internal static void Copy(string url)
	{
		try
		{
			Clipboard.SetText(url);
		}
		catch (System.Runtime.InteropServices.ExternalException)
		{
			// The clipboard is briefly held by another program: one retry is enough in practice.
			try
			{
				Clipboard.SetText(url);
			}
			catch (System.Runtime.InteropServices.ExternalException ex)
			{
				HueMessage.Show(Application.Current.MainWindow, "The URL could not be copied", ex.Message);
			}
		}
	}
}

/// <summary>
/// A page's source from the CMS content list: the value and label as a chip, exactly as the
/// export has them (nothing is interpreted), coloured by which configured key it matched, and a
/// button that copies the CMS editor link. The chip fills the column and the button has a fixed
/// place (kept free when there is no link), so chips and buttons line up row by row. The link is
/// copied, never opened: the CMS is often out of reach from the crawl machine, so the link is
/// taken to a system that can open it. The copy is the real link whatever the screen mask says;
/// the link is never shown as text.
/// </summary>
public sealed class SourceCell : ContentControl
{
	public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
		nameof(Text), typeof(string), typeof(SourceCell), new PropertyMetadata(null, (d, _) => ((SourceCell)d).Render()));

	public static readonly DependencyProperty LinkProperty = DependencyProperty.Register(
		nameof(Link), typeof(string), typeof(SourceCell), new PropertyMetadata(null, (d, _) => ((SourceCell)d).Render()));

	public static readonly DependencyProperty RoleProperty = DependencyProperty.Register(
		nameof(Role), typeof(PageSourceRole), typeof(SourceCell), new PropertyMetadata(PageSourceRole.Other, (d, _) => ((SourceCell)d).Render()));

	public SourceCell()
	{
		Focusable = false;
		IsTabStop = false;
		VerticalContentAlignment = VerticalAlignment.Center;
		HorizontalContentAlignment = HorizontalAlignment.Stretch;

		// A file named from its address follows the screen mask (a weak subscription, as the findings cells).
		PropertyChangedEventManager.AddHandler(Mask.Instance, (_, _) => Render(), nameof(Mask.Level));
	}

	/// <summary>The source value and label ("value label"); empty for none.</summary>
	public string? Text
	{
		get => (string?)GetValue(TextProperty);
		set => SetValue(TextProperty, value);
	}

	/// <summary>The CMS editor link; empty for none (the button's place stays free then).</summary>
	public string? Link
	{
		get => (string?)GetValue(LinkProperty);
		set => SetValue(LinkProperty, value);
	}

	/// <summary>Which configured key the source matched: the chip's colour.</summary>
	public PageSourceRole Role
	{
		get => (PageSourceRole)GetValue(RoleProperty);
		set => SetValue(RoleProperty, value);
	}

	public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(
		nameof(Address), typeof(string), typeof(SourceCell), new PropertyMetadata(null, (d, _) => ((SourceCell)d).Render()));

	/// <summary>
	/// The entry's address when it has no source: the chip then names a stylesheet or script by its
	/// file ("water.min.css", when <see cref="NameFiles"/>), anything else by its host
	/// ("ice.site.test") — both masked like an address; the tooltip holds the whole address.
	/// </summary>
	public string? Address
	{
		get => (string?)GetValue(AddressProperty);
		set => SetValue(AddressProperty, value);
	}

	public static readonly DependencyProperty NameFilesProperty = DependencyProperty.Register(
		nameof(NameFiles), typeof(bool), typeof(SourceCell), new PropertyMetadata(true, (d, _) => ((SourceCell)d).Render()));

	/// <summary>A stylesheet or script without a source is named by its file (Triage); off, it is named by its host like any page.</summary>
	public bool NameFiles
	{
		get => (bool)GetValue(NameFilesProperty);
		set => SetValue(NameFilesProperty, value);
	}

	/// <summary>
	/// The neutral chip for an entry without a source: a stylesheet or script by its file name (the way
	/// the screen mask shows an address's last part), anything else by its host (masked like the host
	/// of an address); null for an address it cannot read.
	/// </summary>
	private ContentControl? NoSourceChip(string address)
	{
		string name;
		string tip;
		if (NameFiles && Core.Urls.FileKind.Of(address) is { } kind)
		{
			name = Mask.ApplyToUrlPath("/" + Core.Urls.FileKind.ShortName(address)).TrimStart('/');
			tip = $"{kind} — {Mask.Apply(address)}";
		}
		else if (Core.Urls.SiteRoot.RootOf(address) is { Length: > 0 } root)
		{
			var masked = Mask.Apply(root);
			int scheme = masked.IndexOf("://", StringComparison.Ordinal);
			name = scheme >= 0 ? masked[(scheme + 3)..] : masked;
			tip = $"No CMS source — {Mask.Apply(address)}";
		}
		else
		{
			return null;
		}

		var chip = new ContentControl
		{
			Content = name,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Left,
			ToolTip = tip,
		};
		chip.SetResourceReference(StyleProperty, StyleOf(PageSourceRole.Other));
		AutomationProperties.SetName(chip, tip);
		return chip;
	}

	/// <summary>The chip style for a role.</summary>
	internal static string StyleOf(PageSourceRole role) => role switch
	{
		PageSourceRole.Mapped => "Source.Chip.Mapped",
		PageSourceRole.External => "Source.Chip.External",
		PageSourceRole.Local => "Source.Chip.Local",
		_ => "Source.Chip",
	};

	private void Render()
	{
		var text = Text ?? string.Empty;
		var link = Link?.Trim() ?? string.Empty;
		if (text.Length == 0 && link.Length == 0 && Address is { Length: > 0 } address && NoSourceChip(address) is { } named)
		{
			Content = named;
			return;
		}

		if (text.Length == 0 && link.Length == 0)
		{
			Content = null;
			return;
		}

		var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) }); // the copy button's place, always
		if (text.Length > 0)
		{
			// The full text as a tooltip too, for a window too narrow even for the column's minimum.
			var chip = new ContentControl { Content = text, VerticalAlignment = VerticalAlignment.Center, ToolTip = text };
			chip.SetResourceReference(StyleProperty, StyleOf(Role));
			grid.Children.Add(chip);
		}

		if (link.Length > 0)
		{
			var copy = new Button
			{
				Content = "\uE8C8", // Copy
				FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
				FontSize = 12,
				Width = 24,
				Height = 22,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Center,
				ToolTip = "Copy CMS link",
			};
			copy.SetResourceReference(StyleProperty, "Hue.Button.Icon");
			AutomationProperties.SetName(copy, "Copy CMS link");
			copy.Click += (_, _) => UrlCell.Copy(link);
			Grid.SetColumn(copy, 1);
			grid.Children.Add(copy);
		}

		Content = grid;
	}
}

/// <summary>Free text, wrapped; HTML in it is highlighted in the Dark+ colours (the app's Syntax.* brushes).</summary>
public sealed class CodeCell : MaskedCell
{
	protected override void Render()
	{
		var value = Value ?? string.Empty;
		var text = Wrapping();
		if (!HtmlTokens.LooksLikeHtml(value))
		{
			text.Text = Mask.Apply(value);
			Content = text;
			return;
		}

		// Tokenized in clear text, masked part by part: URL attribute values (src, href, srcset …)
		// are masked like URLs, relative paths included; everything else as text.
		var urlValues = HtmlTokens.UrlValueSpans(value).Select(s => s.Start).ToHashSet();
		text.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
		int at = 0;
		foreach (var (kind, clear) in HtmlTokens.Tokenize(value))
		{
			var part = urlValues.Contains(at) ? Mask.ApplyToAttributeValue(clear) : Mask.Apply(clear);
			at += clear.Length;
			var run = new DocRun(part);
			run.SetResourceReference(TextElement.ForegroundProperty, kind switch
			{
				HtmlTokenKind.Punctuation => "Syntax.Punctuation",
				HtmlTokenKind.Tag => "Syntax.Tag",
				HtmlTokenKind.AttributeName => "Syntax.AttributeName",
				HtmlTokenKind.AttributeValue => "Syntax.AttributeValue",
				HtmlTokenKind.Comment => "Syntax.Comment",
				HtmlTokenKind.Entity => "Syntax.Entity",
				_ => "Syntax.Text",
			});
			text.Inlines.Add(run);
		}

		Content = text;
	}
}

/// <summary>
/// A list of pages, one address per line: on the site root from the path (<see cref="UrlDisplay"/>),
/// other hosts whole; a line that is not an address ("… and 3 more") as written. The tooltip holds
/// the whole addresses.
/// </summary>
public sealed class PagesCell : MaskedCell
{
	protected override void Render()
	{
		var lines = (Value ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
		var text = Wrapping();
		for (int i = 0; i < lines.Count; i++)
		{
			if (i > 0)
			{
				text.Inlines.Add(new LineBreak());
			}

			var line = lines[i].Trim();
			text.Inlines.Add(new DocRun(Core.Urls.SiteRoot.RootOf(line).Length > 0 ? UrlDisplay.Shown(line, Root) : Mask.Apply(line)));
		}

		var whole = Mask.Apply(Value ?? string.Empty);
		text.ToolTip = whole.Length > 0 ? whole : null;
		Content = text;
	}
}

/// <summary>
/// An HTTP status as a code chip: the number, "permanent"/"temporary" for redirects, and the
/// official name with an explanation in the tooltip. Anything else is shown as written.
/// </summary>
public sealed class StatusCell : MaskedCell
{
	protected override void Render() => Content = Chip(Value);

	/// <summary>The chip for a status, or plain text when the value is not one.</summary>
	public static FrameworkElement Chip(string? value)
	{
		if (HttpCodes.Parse(value) is not { } code)
		{
			var text = Wrapping();
			text.Text = Mask.Apply(value);
			return text;
		}

		// A compact chip for table rows, in the colours of the theme's HTTP-code chips
		// (Hue.Code.* is sized for standalone use and would be cut off in a row).
		var (fill, edge) = HttpCodes.Kind(code) switch
		{
			HttpCodeKind.Success => ("Brush.OkSoft", "Brush.OkText"),
			HttpCodeKind.ClientError => ("Brush.LimitSoft", "Brush.LimitText"),
			HttpCodeKind.ServerError => ("Brush.ErrorSoft", "Brush.ErrorText"),
			_ => ("Brush.AccentSoft", "Brush.AccentText"),
		};

		var word = HttpCodes.KindWord(code);
		var label = new TextBlock
		{
			Text = word.Length == 0 ? code.ToString(CultureInfo.InvariantCulture) : $"{code} {word}",
			FontSize = 12,
			VerticalAlignment = VerticalAlignment.Center,
		};
		label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");

		var chip = new Border
		{
			Child = label,
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(5),
			Padding = new Thickness(7, 1, 8, 1),
			MinHeight = 20,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center,
			SnapsToDevicePixels = true,
			ToolTip = HttpCodes.Describe(code),
		};
		chip.SetResourceReference(Border.BackgroundProperty, fill);
		chip.SetResourceReference(Border.BorderBrushProperty, edge);
		return chip;
	}
}

/// <summary>A mark such as "Security": a red chip when the value is set, nothing when it is empty.</summary>
public sealed class FlagCell : MaskedCell
{
	protected override void Render()
	{
		if (string.IsNullOrEmpty(Value))
		{
			Content = null;
			return;
		}

		var label = new TextBlock { Text = Value, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
		label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.ErrorText");
		var chip = new Border
		{
			Child = label,
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(5),
			Padding = new Thickness(7, 1, 8, 1),
			MinHeight = 20,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center,
			SnapsToDevicePixels = true,
			ToolTip = "From a pattern group marked security-relevant in Settings",
		};
		chip.SetResourceReference(Border.BackgroundProperty, "Brush.ErrorSoft");
		chip.SetResourceReference(Border.BorderBrushProperty, "Brush.ErrorEdge");
		Content = chip;
	}
}

/// <summary>A redirect chain: one hop per line — status chip, URL, and the ending (Loop, Not crawled) in amber.</summary>
public sealed class ChainCell : MaskedCell
{
	protected override void Render()
	{
		var lines = new StackPanel();
		foreach (var hop in RedirectChain.Parse(Value))
		{
			var line = new Grid { Margin = new Thickness(0, 1, 0, 1) };
			line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) }); // fits "301 permanent"
			line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

			if (hop.Status is { } status)
			{
				line.Children.Add(StatusCell.Chip(status.ToString(CultureInfo.InvariantCulture)));
			}
			else
			{
				var arrow = Wrapping("Brush.TextMuted");
				arrow.Text = "→";
				line.Children.Add(arrow);
			}

			var url = new UrlCell { Value = hop.Url };
			Grid.SetColumn(url, 1);
			line.Children.Add(url);

			if (hop.Ending is { } ending)
			{
				var end = Wrapping("Brush.WarnText");
				end.Text = RedirectChain.EndingText(ending);
				end.Margin = new Thickness(10, 0, 0, 0);
				Grid.SetColumn(end, 2);
				line.Children.Add(end);
			}

			lines.Children.Add(line);
		}

		Content = lines;
	}
}

/// <summary>A number cell's text: ISO-grouped when it is a whole number.</summary>
public sealed class NumberCellText : IValueConverter
{
	public static readonly NumberCellText Instance = new();

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => NumberText.Cell(value as string);

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>
/// Column-header templates. The theme's header keeps its title in an auto-width column, so
/// right alignment needs the title itself to span the header (less padding and the sort arrow).
/// </summary>
public static class Headers
{
	public static DataTemplate RightAligned { get; } = CreateRightAligned();

	private static DataTemplate CreateRightAligned()
	{
		var text = new FrameworkElementFactory(typeof(TextBlock));
		text.SetBinding(TextBlock.TextProperty, new Binding());
		text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
		text.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(FrameworkElement.ActualWidth))
		{
			RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), 1),
			Converter = HeaderTitleWidth.Instance,
		});
		var template = new DataTemplate { VisualTree = text };
		template.Seal();
		return template;
	}

	/// <summary>Header width less its 12 + 12 padding and the room the sort arrow takes.</summary>
	private sealed class HeaderTitleWidth : IValueConverter
	{
		public static readonly HeaderTitleWidth Instance = new();

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
			value is double width ? Math.Max(0, width - 40) : DependencyProperty.UnsetValue;

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
			throw new NotSupportedException();
	}
}

/// <summary>
/// A content-quality context with the triage's marks for its issue type (the offending quote,
/// the ligature, the empty anchor, …) drawn over the HTML highlighting. A quote finding adds an
/// "Offender: U+…" line naming the exact character.
/// </summary>
public sealed class QualityContextCell : MaskedCell
{
	public static readonly DependencyProperty IssueTypeProperty = DependencyProperty.Register(
		nameof(IssueType), typeof(string), typeof(QualityContextCell), new PropertyMetadata(null, (d, _) => ((QualityContextCell)d).Render()));

	public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
		nameof(Detail), typeof(string), typeof(QualityContextCell), new PropertyMetadata(null, (d, _) => ((QualityContextCell)d).Render()));

	/// <summary>The content-quality settings of the loaded configuration (quote rules depend on them).</summary>
	public static Core.ContentQualityConfig? Settings { get; set; }

	public string? IssueType
	{
		get => (string?)GetValue(IssueTypeProperty);
		set => SetValue(IssueTypeProperty, value);
	}

	public string? Detail
	{
		get => (string?)GetValue(DetailProperty);
		set => SetValue(DetailProperty, value);
	}

	/// <summary>Which mark wins where two overlap: the offender always shows.</summary>
	private static int Priority(MarkRole role) => role switch
	{
		MarkRole.Highlight => -1, // under everything
		MarkRole.Dim => 0,
		MarkRole.Tint => 1,
		MarkRole.Ok => 2,
		MarkRole.Partner => 3,
		MarkRole.Warn => 3,
		MarkRole.Fill => 4,
		_ => 5,
	};

	protected override void Render()
	{
		MarkedContext marked;
		try
		{
			marked = QualityMarks.For(IssueType ?? string.Empty, Value ?? string.Empty, Detail ?? string.Empty, Settings);
		}
		catch (Exception ex)
		{
			// A highlight that fails must never cost the row: show the context unmarked.
			Core.Logger.LogWarning($"Findings: no highlight for a {IssueType} context — {ex.Message}");
			marked = new MarkedContext(Value ?? string.Empty, [], null);
		}

		var text = marked.Text;
		var html = HtmlTokens.LooksLikeHtml(text);

		// Per character: the HTML colour underneath, the mark on top.
		var syntax = new HtmlTokenKind?[text.Length];
		if (html)
		{
			int at = 0;
			foreach (var (kind, part) in HtmlTokens.Tokenize(text))
			{
				for (int i = 0; i < part.Length && at < text.Length; i++)
				{
					syntax[at++] = kind;
				}
			}
		}

		// URL attribute values are masked whole (a relative path gives as much away as a URL);
		// each value is one stretch, so the mask never cuts across it.
		var urlValue = new int[text.Length];
		Array.Fill(urlValue, -1);
		foreach (var (start, length) in HtmlTokens.UrlValueSpans(text))
		{
			for (int i = start; i < start + length && i < text.Length; i++)
			{
				urlValue[i] = start;
			}
		}

		var role = new MarkRole?[text.Length];
		foreach (var mark in marked.Marks.OrderBy(m => Priority(m.Role)))
		{
			for (int i = mark.Start; i < mark.Start + mark.Length && i < text.Length; i++)
			{
				role[i] = mark.Role;
			}
		}

		var block = Wrapping();
		if (html)
		{
			block.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
		}

		// In clear text the stretches stay exactly as marked; only a masked screen joins a value.
		bool masking = Mask.Instance.IsActive;
		for (int start = 0; start < text.Length;)
		{
			int end = start + 1;
			if (masking && urlValue[start] >= 0)
			{
				// The whole value, shown with the strongest mark on any part of it.
				while (end < text.Length && urlValue[end] == urlValue[start])
				{
					end++;
				}

				var strongest = Enumerable.Range(start, end - start).Select(i => role[i]).Where(r => r is not null)
					.OrderBy(r => Priority(r!.Value)).FirstOrDefault();
				block.Inlines.Add(Styled(Mask.ApplyToAttributeValue(text[start..end]), syntax[start], strongest, faded: marked.Marks.Count > 0, html));
				start = end;
				continue;
			}

			while (end < text.Length && syntax[end] == syntax[start] && role[end] == role[start] && !(masking && urlValue[end] >= 0))
			{
				end++;
			}

			block.Inlines.Add(Styled(Mask.Apply(text[start..end]), syntax[start], role[start], faded: marked.Marks.Count > 0, html));
			start = end;
		}

		if (marked.Note is null)
		{
			Content = block;
			return;
		}

		var note = Wrapping("Brush.TextDim");
		note.Text = marked.Note;
		note.FontSize = 11.5;
		note.Margin = new Thickness(0, 3, 0, 0);
		var panel = new StackPanel();
		panel.Children.Add(block);
		panel.Children.Add(note);
		Content = panel;
	}

	/// <summary>
	/// One stretch of a context. Marks are filled: the offender solid red with white
	/// text, the others tinted fills in their colour. Behind marks, the HTML colours are faded and
	/// plain text is muted, so the defect is what the eye lands on.
	/// </summary>
	private static DocRun Styled(string part, HtmlTokenKind? syntax, MarkRole? role, bool faded, bool html)
	{
		var run = new DocRun(part);
		void Paint(string foreground, string? background = null, FontWeight? weight = null)
		{
			run.SetResourceReference(TextElement.ForegroundProperty, foreground);
			if (background is not null)
			{
				run.SetResourceReference(TextElement.BackgroundProperty, background);
			}

			if (weight is { } w)
			{
				run.FontWeight = w;
			}
		}

		switch (role)
		{
			case MarkRole.Offender:
				Paint("Brush.OnError", "Brush.ErrorSolid", FontWeights.Bold);
				return run;
			case MarkRole.Fill:
				Paint("Brush.OnError", "Brush.ErrorSolid", FontWeights.SemiBold);
				return run;
			case MarkRole.Warn:
				Paint("Brush.WarnText", "Brush.WarnSoftHover");
				return run;
			case MarkRole.Tint:
				Paint("Brush.AccentText", "Brush.AccentSoftHover");
				return run;
			case MarkRole.Partner:
				Paint("Brush.ErrorText", "Brush.ErrorSoft", FontWeights.Bold);
				return run;
			case MarkRole.Ok:
				Paint("Brush.OkText", "Brush.OkSoftHover");
				return run;
			case MarkRole.Dim:
				Paint("Brush.TextMuted");
				return run;
			case MarkRole.Highlight:
				Paint("Brush.Text", "Brush.Highlighter");
				return run;
		}

		if (!html)
		{
			Paint(faded ? "Brush.TextMuted" : "Brush.Text");
			return run;
		}

		var key = syntax switch
		{
			HtmlTokenKind.Punctuation => "Syntax.Punctuation",
			HtmlTokenKind.Tag => "Syntax.Tag",
			HtmlTokenKind.AttributeName => "Syntax.AttributeName",
			HtmlTokenKind.AttributeValue => "Syntax.AttributeValue",
			HtmlTokenKind.Comment => "Syntax.Comment",
			HtmlTokenKind.Entity => "Syntax.Entity",
			_ => "Syntax.Text",
		};
		Paint(faded ? key + ".Faded" : key);
		return run;
	}
}
