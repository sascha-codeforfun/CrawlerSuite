using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>How a <see cref="MaskedTextBox"/> decides what the screen mask hides.</summary>
public enum MaskHide
{
	/// <summary>Hidden when the mask changes the text (an address, a folder, a learnt name).</summary>
	Auto,

	/// <summary>A site's name: shown as its label ("Primary site", "Site 2").</summary>
	SiteName,
}

/// <summary>
/// An editable box that does not show a value the screen mask hides. While the mask hides it, the
/// box shows the masked text, cannot be focused, copied or edited, and says so in its tooltip —
/// editing a hidden value would show it. With the mask off it is an ordinary text box.
/// </summary>
public sealed class MaskedTextBox : TextBox
{
	public static readonly DependencyProperty HideProperty = DependencyProperty.Register(
		nameof(Hide), typeof(MaskHide), typeof(MaskedTextBox), new PropertyMetadata(MaskHide.Auto, (d, _) => ((MaskedTextBox)d).Refresh()));

	private const string HiddenTip = "Hidden by the screen mask. Turn the mask off to see or edit it.";

	private Overlay? overlay;
	private bool hidden;
	private object? ownToolTip;

	public MaskedTextBox()
	{
		// The theme's text-box look: an implicit style keyed by TextBox does not reach a subclass.
		SetResourceReference(StyleProperty, typeof(TextBox));
		Loaded += (_, _) =>
		{
			Mask.Instance.PropertyChanged += OnMaskChanged;
			Refresh();
		};
		Unloaded += (_, _) =>
		{
			Mask.Instance.PropertyChanged -= OnMaskChanged;
			RemoveOverlay();
		};

		// The overlay sits in an adorner layer, which does not follow a collapsed box (another tab, a
		// folded section): it is shown only while the box itself is.
		IsVisibleChanged += (_, _) => overlay?.Follow(IsVisible);
	}

	public MaskHide Hide
	{
		get => (MaskHide)GetValue(HideProperty);
		set => SetValue(HideProperty, value);
	}

	protected override void OnTextChanged(TextChangedEventArgs e)
	{
		base.OnTextChanged(e);
		if (!IsKeyboardFocusWithin)
		{
			Refresh(); // a value set from outside; what is being typed is shown while typing
		}
	}

	protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
	{
		base.OnLostKeyboardFocus(e);
		Refresh();
	}

	private void OnMaskChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is null or nameof(Mask.Level))
		{
			if (Mask.Instance.IsActive && IsKeyboardFocusWithin && Shown() is not null)
			{
				Keyboard.ClearFocus(); // the mask came on while the value was being edited
			}

			Refresh();
		}
	}

	/// <summary>The masked text to show instead, or null when nothing is hidden.</summary>
	private string? Shown()
	{
		var text = Text ?? string.Empty;
		if (!Mask.Instance.IsActive || text.Trim().Length == 0)
		{
			return null;
		}

		var masked = Hide == MaskHide.SiteName ? Mask.SiteName(text) : Mask.Apply(text);
		return string.Equals(masked, text, StringComparison.Ordinal) ? null : masked;
	}

	private void Refresh()
	{
		var shown = Shown();
		if (shown is null)
		{
			if (hidden)
			{
				Unhide();
			}

			return;
		}

		if (!hidden)
		{
			hidden = true;
			ownToolTip = ReadLocalValue(ToolTipProperty);
			SetValue(FocusableProperty, false);
			SetValue(IsTabStopProperty, false);
			SetValue(ForegroundProperty, Brushes.Transparent);
			SetValue(CaretBrushProperty, Brushes.Transparent);
			SetValue(ToolTipProperty, HiddenTip);
		}

		if (overlay is null && AdornerLayer.GetAdornerLayer(this) is { } layer)
		{
			overlay = new Overlay(this);
			overlay.Follow(IsVisible);
			layer.Add(overlay);
		}

		overlay?.Show(shown);
	}

	private void Unhide()
	{
		hidden = false;
		ClearValue(FocusableProperty);
		ClearValue(IsTabStopProperty);
		ClearValue(ForegroundProperty);
		ClearValue(CaretBrushProperty);
		if (ownToolTip == DependencyProperty.UnsetValue || ownToolTip is null)
		{
			ClearValue(ToolTipProperty);
		}
		else
		{
			SetValue(ToolTipProperty, ownToolTip);
		}

		RemoveOverlay();
	}

	private void RemoveOverlay()
	{
		if (overlay is not null)
		{
			AdornerLayer.GetAdornerLayer(this)?.Remove(overlay);
			overlay = null;
		}
	}

	/// <summary>Draws the masked text where the box's own text would be.</summary>
	private sealed class Overlay : Adorner
	{
		private readonly MaskedTextBox box;
		private string text = string.Empty;

		public Overlay(MaskedTextBox box)
			: base(box)
		{
			this.box = box;
			IsHitTestVisible = false;
		}

		public void Follow(bool visible) => Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

		public void Show(string value)
		{
			text = value;
			InvalidateVisual();
		}

		protected override void OnRender(DrawingContext dc)
		{
			var brush = box.TryFindResource("Brush.TextDim") as Brush ?? Brushes.Gray;
			double left = box.BorderThickness.Left + box.Padding.Left + 2;
			double width = box.ActualWidth - left - box.BorderThickness.Right - box.Padding.Right - 2;
			if (width <= 1)
			{
				return; // not laid out yet; drawn on the next pass
			}

			var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, box.FlowDirection,
				new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch), box.FontSize, brush,
				VisualTreeHelper.GetDpi(box).PixelsPerDip)
			{
				MaxTextWidth = width,
				Trimming = TextTrimming.CharacterEllipsis,
			};
			if (!box.AcceptsReturn && box.TextWrapping == TextWrapping.NoWrap)
			{
				formatted.MaxLineCount = 1; // a wrapping box (a table's text cell) shows its masked text wrapped too
			}

			double top = box.VerticalContentAlignment == VerticalAlignment.Center
				? Math.Max(0, (box.ActualHeight - formatted.Height) / 2)
				: box.BorderThickness.Top + box.Padding.Top + 1;
			dc.DrawText(formatted, new Point(left, top));
		}
	}
}
