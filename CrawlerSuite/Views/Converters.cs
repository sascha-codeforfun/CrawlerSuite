using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CrawlerSuite.Core.Host;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>True → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
		value is true ? Visibility.Collapsed : Visibility.Visible;

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>Non-empty string → Visible.</summary>
public sealed class TextToVisibility : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
		value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>Picks the theme's status-pill style for a message tone (status colour always with its word).</summary>
public sealed class ToneToPillStyle : IValueConverter
{
	public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		var key = value switch
		{
			MessageTone.Success => "Hue.Pill.Ok",
			MessageTone.Warning => "Hue.Pill.Warn",
			MessageTone.ActionRequired => "Hue.Pill.Limit",
			MessageTone.Error => "Hue.Pill.Error",
			MessageTone.Skipped => "Hue.Pill.Stale",
			_ => "Hue.Pill",
		};
		return Application.Current.TryFindResource(key);
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>Picks the pill style for "present / not written" in the results table.</summary>
public sealed class PresentToPillStyle : IValueConverter
{
	public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
		Application.Current.TryFindResource(value is true ? "Hue.Pill.Ok" : "Hue.Pill.Stale");

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>Negates a bool (for IsEnabled bound to "is running").</summary>
public sealed class Negate : IValueConverter
{
	public static readonly Negate Instance = new();

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>A count above zero → Visible.</summary>
public sealed class CountToVisibility : IValueConverter
{
	public static readonly CountToVisibility Instance = new();

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
		value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>true → "Yes", false → "No".</summary>
public sealed class YesNo : IValueConverter
{
	public static readonly YesNo Instance = new();

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "Yes" : "No";

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>
/// A binding whose text is shown through the screen mask, and shown again whenever the mask
/// level changes: <c>Text="{views:Masked Heading}"</c>, or <c>{views:Masked}</c> for the item itself.
/// </summary>
public sealed class Masked : MultiBinding
{
	public Masked()
		: this(".")
	{
	}

	public Masked(string path)
	{
		Mode = BindingMode.OneWay;
		Converter = MaskedText.Instance;
		Bindings.Add(new Binding(path));
		Bindings.Add(new Binding(nameof(Mask.Level)) { Source = Mask.Instance });
	}
}

/// <summary>A site's name through the screen mask — its label while masked: <c>Text="{views:MaskedSite Site}"</c>.</summary>
public sealed class MaskedSite : MultiBinding
{
	public MaskedSite(string path)
	{
		Mode = BindingMode.OneWay;
		Converter = MaskedSiteText.Instance;
		Bindings.Add(new Binding(path));
		Bindings.Add(new Binding(nameof(Mask.Level)) { Source = Mask.Instance });
	}
}

/// <summary>The value of a <see cref="MaskedSite"/> binding.</summary>
public sealed class MaskedSiteText : IMultiValueConverter
{
	public static readonly MaskedSiteText Instance = new();

	public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
		values.Length == 0 || values[0] == DependencyProperty.UnsetValue ? string.Empty : Mask.SiteName(values[0]?.ToString());

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>The value of a <see cref="Masked"/> binding: its first value, masked.</summary>
public sealed class MaskedText : IMultiValueConverter
{
	public static readonly MaskedText Instance = new();

	public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
		values.Length == 0 || values[0] == DependencyProperty.UnsetValue ? string.Empty : Mask.Display(values[0]);

	public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>A page-source role → the chip style of that colour (the same as the table's source chips).</summary>
public sealed class RoleToChipStyle : IValueConverter
{
	public static readonly RoleToChipStyle Instance = new();

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
		Application.Current?.TryFindResource(SourceCell.StyleOf(value is Core.Findings.PageSourceRole role ? role : Core.Findings.PageSourceRole.Other))
			?? DependencyProperty.UnsetValue;

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>A choice's text in a dropdown: the empty choice reads "(default)".</summary>
public sealed class ChoiceText : IValueConverter
{
	public static readonly ChoiceText Instance = new();

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
		value is string s && s.Length > 0 ? s : "(default)";

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

/// <summary>A length less the parameter (never below 60): a picture kept within the list it stands in.</summary>
public sealed class LessBy : IValueConverter
{
	public static readonly LessBy Instance = new();

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		double by = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
		return value is double length && length > 0 ? Math.Max(60, length - by) : double.PositiveInfinity;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
