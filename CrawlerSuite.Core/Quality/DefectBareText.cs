using System.Net;
using HtmlAgilityPack;

namespace CrawlerSuite.Core.Quality
{
	/// <summary>
	/// Prose that has lost its paragraph structure. HTML allows text directly inside a div or
	/// section, so "text in a container" alone is not a defect (labels, widget messages, single
	/// titles are fine there) and is not reported. Two shapes are: they are what is left when an
	/// editor's paragraphs lose their markup, and screen-reader paragraph navigation skips them
	/// (WCAG 1.3.1 Info and Relationships).
	///
	///   * Beside paragraphs: bare text in a container that also holds real paragraphs, headings,
	///     lists or tables as direct children — a paragraph between paragraphs without its &lt;p&gt;.
	///   * Paragraphs made of line breaks: two or more stretches of bare text in one container,
	///     separated by two or more &lt;br&gt; in a row instead of paragraph markup.
	///
	/// Never reported: text without a letter ("?", "12:00"), and text inside an element styled as a
	/// heading (that is reported once, as STYLED_HEADING).
	/// </summary>
	internal static class DefectBareText
	{
		internal const string BesideParagraphsDetail = "Text beside paragraphs or headings, without a paragraph of its own";
		internal const string BreakParagraphsDetail = "Paragraphs separated by <br><br> instead of paragraph markup";

		// Direct children that make a container a structured text block.
		private static readonly HashSet<string> StructureElements = new(StringComparer.OrdinalIgnoreCase)
		{
			"p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "dl", "table", "blockquote", "pre",
		};

		internal static IEnumerable<QualityIssue> CheckBareText(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			var containers = config.ContentQualityContainerElements
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var headingClasses = config.ContentQualityHeadingClasses ?? [];

			foreach (var node in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element
					&& containers.Contains(n.Name)))
			{
				if (StyledHeadings.IsInsideStyledHeading(node, headingClasses))
				{
					continue;
				}

				var prose = node.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Text && IsProse(c)).ToList();
				if (prose.Count == 0)
				{
					continue;
				}

				var tag = DefectDetectionHelpers.FormatContainerStartTag(node);
				if (node.ChildNodes.Any(c => c.NodeType == HtmlNodeType.Element && StructureElements.Contains(c.Name)))
				{
					foreach (var text in prose)
					{
						yield return new QualityIssue(filename, "BARE_TEXT_IN_CONTAINER", BesideParagraphsDetail,
							$"[{tag}] {Excerpt(Visible(text), config)}");
					}

					continue;
				}

				if (HasBreakParagraphs(node))
				{
					yield return new QualityIssue(filename, "BARE_TEXT_IN_CONTAINER", BreakParagraphsDetail,
						$"[{tag}] {Excerpt(Visible(prose[0]), config)}");
				}
			}
		}

		/// <summary>
		/// True when two stretches of the container's direct content that carry prose are separated
		/// by two or more &lt;br&gt; in a row (whitespace between the breaks does not count).
		/// </summary>
		private static bool HasBreakParagraphs(HtmlNode container)
		{
			// The direct content splits into stretches at <br>. A stretch "has prose" when one of its
			// own text nodes is prose; the separator before a stretch is the number of <br> in a row.
			bool previousHasProse = false;
			bool currentHasProse = false;
			bool bigSeparator = false;
			int breaks = 0;
			foreach (var child in container.ChildNodes)
			{
				if (child.NodeType == HtmlNodeType.Element && child.Name.Equals("br", StringComparison.OrdinalIgnoreCase))
				{
					breaks++;
					continue;
				}

				if (child.NodeType == HtmlNodeType.Comment
					|| (child.NodeType == HtmlNodeType.Text && Visible(child).Length == 0))
				{
					continue; // whitespace and comments neither end a run of breaks nor start a stretch
				}

				if (breaks > 0)
				{
					// A new stretch begins after the breaks.
					previousHasProse = currentHasProse;
					bigSeparator = breaks >= 2;
					currentHasProse = false;
					breaks = 0;
				}

				if (child.NodeType == HtmlNodeType.Text && IsProse(child))
				{
					currentHasProse = true;
				}

				if (previousHasProse && bigSeparator && currentHasProse)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Visible text with at least one letter: invisibles and surrounding whitespace do not count.</summary>
		private static bool IsProse(HtmlNode textNode)
		{
			var text = Visible(textNode);
			if (text.Length == 0)
			{
				return false;
			}

			bool visible = false;
			bool letter = false;
			foreach (var ch in text)
			{
				if (char.IsWhiteSpace(ch) || DefectDetectionHelpers.IsArchitectClassInvisible(ch))
				{
					continue;
				}

				visible = true;
				if (char.IsLetter(ch))
				{
					letter = true;
					break;
				}
			}

			return visible && letter;
		}

		private static string Visible(HtmlNode textNode) => WebUtility.HtmlDecode(textNode.InnerText).Trim();

		private static string Excerpt(string text, ContentQualityConfig config) =>
			text.Length > config.ContentQualityExcerptRadius
				? text[..config.ContentQualityExcerptRadius] + "…"
				: text;
	}
}
