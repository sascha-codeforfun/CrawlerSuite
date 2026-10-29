using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Run;
using DocRun = System.Windows.Documents.Run; // "Run" alone is the CrawlerSuite.Run namespace here

namespace CrawlerSuite.Views;

/// <summary>
/// The explanation of a kind of defect in triage: the Results card for it (warning colours for a
/// likely WCAG issue), folded to one line — its name and verdict, or "likely WCAG issue" — that
/// opens to the full text and the links to the standard.
/// </summary>
public sealed class BasisCard : ContentControl
{
	public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
		nameof(Card), typeof(TriageCardVm), typeof(BasisCard), new PropertyMetadata(null, (d, e) => ((BasisCard)d).OnCardChanged(e)));

	public BasisCard()
	{
		Focusable = false;
		IsTabStop = false;
		Visibility = Visibility.Collapsed; // until a card arrives: most items have none
	}

	public TriageCardVm? Card
	{
		get => (TriageCardVm?)GetValue(CardProperty);
		set => SetValue(CardProperty, value);
	}

	private void OnCardChanged(DependencyPropertyChangedEventArgs e)
	{
		if (e.OldValue is TriageCardVm old)
		{
			PropertyChangedEventManager.RemoveHandler(old, OnExpandedChanged, nameof(TriageCardVm.IsExpanded));
		}

		if (e.NewValue is TriageCardVm card)
		{
			PropertyChangedEventManager.AddHandler(card, OnExpandedChanged, nameof(TriageCardVm.IsExpanded));
		}

		Render();
	}

	private void OnExpandedChanged(object? sender, PropertyChangedEventArgs e) => Render();

	private void Render()
	{
		var card = Card;
		if (card is null)
		{
			Content = null;
			Visibility = Visibility.Collapsed;
			return;
		}

		Visibility = Visibility.Visible;
		var callout = new ContentControl();
		callout.SetResourceReference(StyleProperty, card.Basis.IsViolation ? "Hue.Callout.Warn" : "Hue.Callout");

		var toggle = new Button
		{
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Left,
			Padding = new Thickness(0),
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0),
			Cursor = Cursors.Hand,
			ToolTip = card.IsExpanded ? "Hide the explanation" : "Why it is reported, and how to fix it",
		};
		toggle.SetResourceReference(StyleProperty, "Hue.Button.Ghost");
		toggle.SetBinding(ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = callout });
		var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
		line.Inlines.Add(new DocRun(card.Headline) { FontWeight = FontWeights.SemiBold });
		line.Inlines.Add(new DocRun(card.IsExpanded ? "   Hide ▴" : "   Why, and how to fix ▾"));
		toggle.Content = line;
		AutomationProperties.SetName(toggle, card.Headline + (card.IsExpanded ? " — hide the explanation" : " — show why, and how to fix"));
		toggle.Click += (_, _) => card.IsExpanded = !card.IsExpanded;

		var panel = new StackPanel();
		panel.Children.Add(toggle);
		if (card.IsExpanded)
		{
			var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
			BasisLinkList.Cited(text, card.Basis.FullText);
			panel.Children.Add(text);
			var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
			BasisLinkList.Fill(links, card.Basis);
			panel.Children.Add(links);
		}

		callout.Content = panel;
		Content = callout;
	}
}

/// <summary>The links of a finding's basis: each opens in the browser, with a copy button beside it.</summary>
internal static class BasisLinkList
{
	/// <summary>A card's text with each rule it cites ("UAX #14", "LB12a", "2.4.4") as a link to it, in place.</summary>
	public static void Cited(TextBlock target, string text)
	{
		target.Inlines.Clear();
		foreach (var piece in Core.Text.Citations.Split(text))
		{
			if (piece.Url is not { } url)
			{
				target.Inlines.Add(new DocRun(piece.Text));
				continue;
			}

			var link = new Hyperlink(new DocRun(piece.Text)) { Cursor = Cursors.Hand, ToolTip = url };
			link.SetResourceReference(TextElement.ForegroundProperty, "Brush.AccentText");
			link.Click += (_, _) => UrlCell.Open(url);
			AutomationProperties.SetName(link, piece.Text + " — opens " + url);
			target.Inlines.Add(link);
		}
	}

	public static void Fill(Panel target, FindingBasis basis)
	{
		target.Children.Clear();
		foreach (var (label, url) in basis.AllLinks)
		{
			var link = new Hyperlink(new DocRun(label)) { Cursor = Cursors.Hand, ToolTip = url };
			link.SetResourceReference(TextElement.ForegroundProperty, "Brush.AccentText");
			link.Click += (_, _) => UrlCell.Open(url);

			var copy = new Button
			{
				Content = "", // Copy
				FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
				FontSize = 12,
				Width = 24,
				Height = 22,
				Margin = new Thickness(4, 0, 16, 0),
				ToolTip = "Copy the link",
			};
			copy.SetResourceReference(FrameworkElement.StyleProperty, "Hue.Button.Icon");
			AutomationProperties.SetName(copy, "Copy the link to " + label);
			copy.Click += (_, _) => UrlCell.Copy(url);

			var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
			item.Children.Add(new TextBlock(link) { VerticalAlignment = VerticalAlignment.Center });
			item.Children.Add(copy);
			target.Children.Add(item);
		}
	}
}
