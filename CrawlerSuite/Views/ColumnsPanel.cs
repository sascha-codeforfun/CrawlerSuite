using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>
/// Lays out a table row's cells side by side: each at its column's fixed width, the ones with
/// width 0 sharing what is left. Headings and cells use the same widths, so columns line up
/// without a grid shared across rows. The row never gets wider than the space it has.
/// </summary>
public sealed class ColumnsPanel : Panel
{
	public static readonly DependencyProperty GapProperty =
		DependencyProperty.Register(nameof(Gap), typeof(double), typeof(ColumnsPanel),
			new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

	/// <summary>The space between cells.</summary>
	public double Gap
	{
		get => (double)GetValue(GapProperty);
		set => SetValue(GapProperty, value);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		var widths = Widths(availableSize.Width);
		double height = 0;
		for (int i = 0; i < InternalChildren.Count; i++)
		{
			var child = InternalChildren[i];
			child.Measure(new Size(widths[i], availableSize.Height));
			height = Math.Max(height, child.DesiredSize.Height);
		}

		var total = widths.Sum() + Gap * Math.Max(0, InternalChildren.Count - 1);
		return new Size(double.IsInfinity(availableSize.Width) ? total : availableSize.Width, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		var widths = Widths(finalSize.Width);
		double x = 0;
		for (int i = 0; i < InternalChildren.Count; i++)
		{
			InternalChildren[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
			x += widths[i] + Gap;
		}

		return finalSize;
	}

	private double[] Widths(double available)
	{
		var fixedWidths = InternalChildren.Cast<UIElement>()
			.Select(c => (c as FrameworkElement)?.DataContext is ITableCellWidth cell ? cell.Width : 0)
			.ToArray();
		const double MinStar = 120;
		int stars = fixedWidths.Count(w => w <= 0);
		double gaps = Gap * Math.Max(0, fixedWidths.Length - 1);
		double fixedSum = fixedWidths.Where(w => w > 0).Sum();
		if (double.IsInfinity(available))
		{
			return fixedWidths.Select(w => w > 0 ? w : 240).ToArray();
		}

		// Never wider than the space there is: when the fixed columns leave too little for the
		// flexible ones, the fixed columns shrink proportionally.
		double scale = 1;
		double room = available - gaps - MinStar * stars;
		if (fixedSum > 0 && fixedSum > room)
		{
			scale = Math.Max(0, room) / fixedSum;
		}

		double star = stars == 0 ? 0 : Math.Max(0, (available - gaps - fixedSum * scale) / stars);
		return fixedWidths.Select(w => w > 0 ? w * scale : star).ToArray();
	}
}
