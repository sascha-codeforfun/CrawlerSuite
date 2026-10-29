using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CrawlerSuite.Views;

/// <summary>
/// For a grid or list that shows all its rows inside a scrolling page: its own scroll viewer
/// would swallow the mouse wheel, so the wheel is handed to the page instead.
/// </summary>
public static class WheelPassThrough
{
	public static void ToParent(object sender, MouseWheelEventArgs e)
	{
		if (e.Handled || sender is not DependencyObject element)
		{
			return;
		}

		var parent = VisualTreeHelper.GetParent(element) as UIElement;
		if (parent is null)
		{
			return;
		}

		e.Handled = true;
		parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
		{
			RoutedEvent = UIElement.MouseWheelEvent,
			Source = sender,
		});
	}
}
