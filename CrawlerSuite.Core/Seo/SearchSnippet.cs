using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace CrawlerSuite.Core.Seo
{
	/// <summary>
	/// How a page would likely appear as a search result: its title and description as a
	/// search engine shows them on a desktop result, cut where they would likely be cut. Search
	/// engines cut by width in pixels, not by characters; the width is estimated from the
	/// advance widths of a metric-compatible face (<see cref="SnippetFontWidths"/>). An estimate:
	/// a search engine may also rewrite either text, or show another one.
	/// </summary>
	/// <param name="Title">The title as read, entities decoded and white space collapsed; empty when there is none.</param>
	/// <param name="TitleShown">The title as the result would show it: whole, or cut at a word with " …".</param>
	/// <param name="TitleWidth">The estimated width of the whole title in pixels.</param>
	public sealed record SearchSnippet(
		string Url,
		string Title,
		string TitleShown,
		bool TitleCut,
		int TitleWidth,
		string Description,
		string DescriptionShown,
		bool DescriptionCut,
		int DescriptionWidth)
	{
		/// <summary>The result title's font size in pixels (desktop).</summary>
		public const int TitleFontPixels = 20;

		/// <summary>About where a desktop result cuts its title, in pixels. Published tools put it at 580–600.</summary>
		public const int TitleLimitPixels = 580;

		/// <summary>The result description's font size in pixels (desktop).</summary>
		public const int DescriptionFontPixels = 14;

		/// <summary>About where a desktop result cuts its description, in pixels. Published tools put it at 920–990.</summary>
		public const int DescriptionLimitPixels = 920;

		public const string Ellipsis = " …";

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

		/// <summary>The snippet of a page from its title and description as found (entities not decoded).</summary>
		public static SearchSnippet Of(string url, string title, string description)
		{
			var t = Clean(title);
			var d = Clean(description);
			var (tShown, tCut, tWidth) = Fit(t, TitleFontPixels, TitleLimitPixels);
			var (dShown, dCut, dWidth) = Fit(d, DescriptionFontPixels, DescriptionLimitPixels);
			return new SearchSnippet(url, t, tShown, tCut, tWidth, d, dShown, dCut, dWidth);
		}

		/// <summary>Entities decoded, white space collapsed, trimmed: the text a reader sees.</summary>
		public static string Clean(string text) =>
			Space.Replace(HtmlEntity.DeEntitize(text ?? string.Empty) ?? string.Empty, " ").Trim();

		/// <summary>The estimated width of <paramref name="text"/> in pixels at <paramref name="fontPixels"/>.</summary>
		public static int WidthOf(string text, int fontPixels)
		{
			long units = 0;
			foreach (var c in text)
			{
				units += SnippetFontWidths.Of(c);
			}

			return (int)Math.Ceiling(units * (double)fontPixels / SnippetFontWidths.UnitsPerEm);
		}

		/// <summary>The text as shown within <paramref name="limit"/> pixels: whole, or cut after the last word that fits with the ellipsis.</summary>
		internal static (string Shown, bool Cut, int Width) Fit(string text, int fontPixels, int limit)
		{
			int width = WidthOf(text, fontPixels);
			if (width <= limit)
			{
				return (text, false, width);
			}

			var words = text.Split(' ');
			var shown = string.Empty;
			foreach (var word in words)
			{
				var next = shown.Length == 0 ? word : shown + " " + word;
				if (WidthOf(next + Ellipsis, fontPixels) > limit)
				{
					break;
				}

				shown = next;
			}

			if (shown.Length == 0)
			{
				// One word longer than the line: cut inside it.
				foreach (var c in text)
				{
					if (WidthOf(shown + c + Ellipsis, fontPixels) > limit)
					{
						break;
					}

					shown += c;
				}
			}

			return (shown.TrimEnd(' ', ',', ';', ':', '-', '–', '|') + Ellipsis, true, width);
		}
	}
}
