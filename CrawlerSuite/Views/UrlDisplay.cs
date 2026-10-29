using System.Windows;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>
/// The site root a page shows addresses against ("https://site.test"), set once on a view and
/// inherited by every address cell inside it: an address on the root is shown from its path, any
/// other host in full. Open, copy and the tooltip always use the whole address.
/// </summary>
public static class UrlDisplay
{
	public static readonly DependencyProperty RootProperty = DependencyProperty.RegisterAttached(
		"Root", typeof(string), typeof(UrlDisplay),
		new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.Inherits, (d, _) => (d as MaskedCell)?.Redraw()));

	public static string GetRoot(DependencyObject element) => (string)element.GetValue(RootProperty);

	public static void SetRoot(DependencyObject element, string value) => element.SetValue(RootProperty, value);

	/// <summary>An address as shown under <paramref name="root"/>: from its path when it stands on it, else whole — masked either way.</summary>
	public static string Shown(string url, string? root) =>
		Core.Urls.SiteRoot.PathOn(url, root) is { } path ? Mask.ApplyToUrlPath(path) : Mask.Apply(url);

	/// <summary>
	/// The heading with the root the addresses are shown against, in brackets — only when the
	/// heading does not already name it (a snapshot opened from a folder, or a configured address
	/// that differs from the root the pages share).
	/// </summary>
	public static string HeadingWithRoot(string heading, string root) =>
		root.Length == 0 || heading.Contains(root, System.StringComparison.OrdinalIgnoreCase) ? heading : $"{heading} ({root})";
}
