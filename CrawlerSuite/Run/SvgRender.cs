using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CrawlerSuite.Core.Comparison;

namespace CrawlerSuite.Run;

/// <summary>
/// Draws a simple SVG (<see cref="SimpleSvg"/>) with WPF's own shapes: each shape a geometry from
/// its rewritten path, filled and stroked as the SVG says, in a group carrying its transform; the
/// view box clipped. A fill or stroke the SVG leaves open is drawn in the app's text colour, so a
/// plain black icon shows on the dark background. No library.
/// </summary>
public static class SvgRender
{
	/// <summary>Drawings are shown at their own size, but never smaller than this nor larger than <see cref="LargestShown"/> on their longer side.</summary>
	public const double SmallestShown = 96;

	public const double LargestShown = 320;

	/// <summary>The size both sides are drawn at to compare their pixels.</summary>
	public const int CompareSide = 256;

	/// <summary>The drawing as an image source (frozen), or null with the reason when WPF cannot draw a part of it.</summary>
	public static (ImageSource? Image, string Problem) Draw(SimpleSvg.Drawing drawing)
	{
		var text = (Application.Current?.TryFindResource("Brush.Text") as SolidColorBrush)?.Color ?? Color.FromRgb(0xE0, 0xE0, 0xE0);
		var group = new DrawingGroup();
		try
		{
			foreach (var shape in drawing.Shapes)
			{
				var geometry = Geometry.Parse((shape.EvenOdd ? "F0 " : "F1 ") + shape.Path);
				var fill = Brush(shape.Fill, shape.FillOpacity, text);
				var stroke = Brush(shape.Stroke, shape.StrokeOpacity, text);
				var pen = stroke is null ? null : new Pen(stroke, shape.StrokeWidth) { LineJoin = PenLineJoin.Miter };
				var t = shape.Transform;
				var one = new DrawingGroup { Transform = new MatrixTransform(t[0], t[1], t[2], t[3], t[4], t[5]) };
				one.Children.Add(new GeometryDrawing(fill, pen, geometry));
				group.Children.Add(one);
			}
		}
		catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
		{
			return (null, "Not drawn: a shape's path or colour could not be read (" + ex.Message + ")");
		}

		var box = new RectangleGeometry(new Rect(drawing.MinX, drawing.MinY, drawing.Width, drawing.Height));
		group.ClipGeometry = box;

		// An empty corner of the view box still counts: a transparent frame keeps the drawing in its box.
		var framed = new DrawingGroup();
		framed.Children.Add(new GeometryDrawing(Brushes.Transparent, null, box));
		framed.Children.Add(group);
		var image = new DrawingImage(framed);
		image.Freeze();
		return (image, string.Empty);
	}

	/// <summary>The size a drawing is shown at: its view box, scaled up or down to fit the shown range.</summary>
	public static (int Width, int Height) ShownSize(SimpleSvg.Drawing drawing)
	{
		double longer = Math.Max(drawing.Width, drawing.Height);
		double scale = Math.Clamp(longer, SmallestShown, LargestShown) / longer;
		return ((int)Math.Round(drawing.Width * scale), (int)Math.Round(drawing.Height * scale));
	}

	/// <summary>The drawing rendered at <see cref="CompareSide"/> on its longer side: for comparing two drawings by their pixels.</summary>
	public static BitmapSource? Rendered(ImageSource? image, double width, double height)
	{
		if (image is null || width <= 0 || height <= 0)
		{
			return null;
		}

		double scale = CompareSide / Math.Max(width, height);
		int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
		var visual = new DrawingVisual();
		using (var context = visual.RenderOpen())
		{
			context.DrawImage(image, new Rect(0, 0, w, h));
		}

		var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		bitmap.Freeze();
		return bitmap;
	}

	private static Brush? Brush(string? paint, double opacity, Color text)
	{
		if (paint is null)
		{
			return null;
		}

		var colour = paint == SimpleSvg.Current ? text : (Color)ColorConverter.ConvertFromString(paint);
		var brush = new SolidColorBrush(colour) { Opacity = Math.Clamp(opacity, 0, 1) };
		brush.Freeze();
		return brush;
	}
}
