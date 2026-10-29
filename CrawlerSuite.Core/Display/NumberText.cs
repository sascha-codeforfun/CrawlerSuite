using System.Globalization;

namespace CrawlerSuite.Core.Display
{
	/// <summary>
	/// Numbers as the screen shows them: ISO digit grouping with a narrow no-break space
	/// (1 000 000), the decimal sign of the current culture. Never used for files.
	/// </summary>
	public static class NumberText
	{
		/// <summary>The group separator: U+202F NARROW NO-BREAK SPACE.</summary>
		public const string GroupSeparator = " ";

		/// <summary>The current culture's number format with ISO grouping.</summary>
		public static NumberFormatInfo Format
		{
			get
			{
				var format = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
				format.NumberGroupSeparator = GroupSeparator;
				return format;
			}
		}

		public static string Group(long value) => value.ToString("N0", Format);

		public static string Group(double value, int decimals) => value.ToString("N" + decimals, Format);

		/// <summary>A cell from an output file: grouped when it is a whole number ("123456" → "123 456"), else unchanged.</summary>
		public static string Cell(string? value)
		{
			var text = value?.Trim() ?? string.Empty;
			return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? Group(n) : text;
		}
	}
}
