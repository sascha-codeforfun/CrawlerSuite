using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>
/// Wraps chips in rows at one width, so they line up as a grid. With <see cref="AllEqual"/> every
/// chip takes the width of the widest; otherwise the fixed choices set the width and a longer free
/// chip grows on its own rather than widening all of them.
/// </summary>
public sealed class UniformWrapPanel : Panel
{
	public static readonly DependencyProperty AllEqualProperty =
		DependencyProperty.Register(nameof(AllEqual), typeof(bool), typeof(UniformWrapPanel),
			new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

	public bool AllEqual
	{
		get => (bool)GetValue(AllEqualProperty);
		set => SetValue(AllEqualProperty, value);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		foreach (UIElement child in InternalChildren)
		{
			child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		}

		var widths = Widths();
		double maxRow = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
		double x = 0, y = 0, rowHeight = 0, widest = 0;
		for (int i = 0; i < InternalChildren.Count; i++)
		{
			var size = InternalChildren[i].DesiredSize;
			if (x > 0 && x + widths[i] > maxRow)
			{
				y += rowHeight;
				x = 0;
				rowHeight = 0;
			}

			x += widths[i];
			widest = Math.Max(widest, x);
			rowHeight = Math.Max(rowHeight, size.Height);
		}

		return new Size(widest, y + rowHeight);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		var widths = Widths();
		double x = 0, y = 0, rowHeight = 0;
		int rowStart = 0;
		for (int i = 0; i <= InternalChildren.Count; i++)
		{
			bool last = i == InternalChildren.Count;
			if (last || (x > 0 && x + widths[i] > finalSize.Width))
			{
				// Place the finished row; its chips share the row's height.
				double cx = 0;
				for (int j = rowStart; j < i; j++)
				{
					InternalChildren[j].Arrange(new Rect(cx, y, widths[j], rowHeight));
					cx += widths[j];
				}

				if (last)
				{
					break;
				}

				y += rowHeight;
				x = 0;
				rowHeight = 0;
				rowStart = i;
			}

			rowHeight = Math.Max(rowHeight, InternalChildren[i].DesiredSize.Height);
			x += widths[i];
		}

		return finalSize;
	}

	private double[] Widths()
	{
		var children = InternalChildren.Cast<UIElement>().ToList();
		var setters = AllEqual
			? children
			: children.Where(c => (c as FrameworkElement)?.DataContext is ToggleChipVm).ToList();
		double uniform = setters.Count == 0 ? 0 : setters.Max(c => c.DesiredSize.Width);
		return children.Select(c => Math.Max(uniform, c.DesiredSize.Width)).ToArray();
	}
}
