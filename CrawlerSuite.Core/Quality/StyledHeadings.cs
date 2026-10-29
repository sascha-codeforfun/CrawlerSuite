using System.Net;
using HtmlAgilityPack;

namespace CrawlerSuite.Core.Quality
{
	/// <summary>
	/// Headings that are only styled: an element that is not h1–h6 but carries a class the site
	/// uses to make text look like a heading (&lt;div class="h2"&gt;, &lt;span class="card-title"&gt;).
	/// Sighted readers see a heading; assistive technology sees plain text, so heading navigation
	/// skips it (WCAG 1.3.1 Info and Relationships). The fix sits in the template, once.
	/// </summary>
	internal static class StyledHeadings
	{
		internal const string IssueType = "STYLED_HEADING";

		/// <summary>The heading class an element carries (first in configuration order), or null.</summary>
		internal static string? HeadingClassOf(HtmlNode node, IReadOnlyList<string> headingClasses)
		{
			if (headingClasses.Count == 0 || node.NodeType != HtmlNodeType.Element)
			{
				return null;
			}

			var classAttr = node.GetAttributeValue("class", string.Empty);
			if (classAttr.Length == 0)
			{
				return null;
			}

			var tokens = classAttr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			foreach (var heading in headingClasses)
			{
				if (!string.IsNullOrEmpty(heading) && tokens.Contains(heading, StringComparer.Ordinal))
				{
					return heading;
				}
			}

			return null;
		}

		/// <summary>
		/// The label of a nested list: the element sits in a list item and its own list follows it
		/// ("&lt;li&gt;&lt;div class="h4"&gt;Group&lt;/div&gt;&lt;ul&gt;…"). The nesting already tells assistive
		/// technology that the links belong to the label (WCAG 1.3.1 is met); a heading would add
		/// heading navigation, which is good practice, not a failure.
		/// </summary>
		internal static bool LabelsNestedList(HtmlNode node)
		{
			if (!string.Equals(node.ParentNode?.Name, "li", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			var next = node.NextSibling;
			while (next is not null && next.NodeType != HtmlNodeType.Element)
			{
				if (next.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(next.InnerText))
				{
					return false; // text between label and list: not a plain label
				}

				next = next.NextSibling;
			}

			return next is not null && (next.Name.Equals("ul", StringComparison.OrdinalIgnoreCase) || next.Name.Equals("ol", StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>True for h1–h6.</summary>
		internal static bool IsRealHeading(HtmlNode node) =>
			node.Name.Length == 2 && node.Name[0] is 'h' or 'H' && node.Name[1] is >= '1' and <= '6';

		/// <summary>
		/// A heading for assistive technology by ARIA: role="heading" (the first role token, as
		/// browsers take it), with or without aria-level — without one the level is 2 (WAI-ARIA 1.2).
		/// </summary>
		internal static bool IsAriaHeading(HtmlNode node)
		{
			var roles = node.GetAttributeValue("role", string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			return roles.Length > 0 && roles[0].Equals("heading", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Hidden from everyone (hidden attribute, aria-hidden="true") here or above: no heading to miss.</summary>
		private static bool IsHidden(HtmlNode node)
		{
			for (var n = node; n is not null && n.NodeType == HtmlNodeType.Element; n = n.ParentNode)
			{
				if (n.Attributes.Contains("hidden")
					|| n.GetAttributeValue("aria-hidden", string.Empty).Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// True when <paramref name="node"/> or an ancestor is an element styled as a heading by one
		/// of <paramref name="headingClasses"/> — text there belongs to the styled heading.
		/// </summary>
		internal static bool IsInsideStyledHeading(HtmlNode node, IReadOnlyList<string> headingClasses)
		{
			for (var n = node; n is not null && n.NodeType != HtmlNodeType.Document; n = n.ParentNode)
			{
				if (HeadingClassOf(n, headingClasses) is not null)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// One finding per page and pattern (tag + heading class): Detail is the pattern, the same on
		/// every page, so the Results view can group the pages behind it; Context lists the class
		/// attributes as the page writes them (when they hold more than the heading class) and every
		/// heading text in page order, a repeated one once with "×n", joined by <see cref="Separator"/>.
		/// </summary>
		internal static IEnumerable<QualityIssue> Check(string filename, HtmlDocument doc, IReadOnlyList<string> headingClasses, int excerptRadius)
		{
			if (headingClasses.Count == 0)
			{
				yield break;
			}

			// Per pattern: every heading text in page order with how often it occurs, and the class
			// attributes as the page writes them (the template owner searches for those).
			var byPattern = new Dictionary<string, (List<string> Texts, Dictionary<string, int> Counts, List<string> Classes)>(StringComparer.Ordinal);
			var order = new List<string>();
			foreach (var node in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
			{
				if (IsRealHeading(node) || IsAriaHeading(node) || DefectTemplateAuthoring.IsInsideArchitectScopeSkipAncestor(node)
					|| IsHidden(node) || LabelsNestedList(node))
				{
					continue;
				}

				var heading = HeadingClassOf(node, headingClasses);
				if (heading is null)
				{
					continue;
				}

				var text = WebUtility.HtmlDecode(node.InnerText ?? string.Empty);
				text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
				if (!text.Any(char.IsLetterOrDigit))
				{
					continue; // nothing shown as a heading
				}

				text = Cut(text, excerptRadius);
				var classes = Cut(string.Join(' ', node.GetAttributeValue("class", string.Empty)
					.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), excerptRadius);
				var pattern = $"<{node.Name} class=\"{heading}\">";
				if (!byPattern.TryGetValue(pattern, out var found))
				{
					found = ([], new Dictionary<string, int>(StringComparer.Ordinal), []);
					byPattern[pattern] = found;
					order.Add(pattern);
				}

				if (found.Counts.TryGetValue(text, out var n))
				{
					found.Counts[text] = n + 1;
				}
				else
				{
					found.Counts[text] = 1;
					found.Texts.Add(text);
				}

				if (!found.Classes.Contains(classes, StringComparer.Ordinal))
				{
					found.Classes.Add(classes);
				}
			}

			foreach (var pattern in order)
			{
				var (texts, counts, classes) = byPattern[pattern];
				var heading = pattern[(pattern.IndexOf('"') + 1)..pattern.LastIndexOf('"')];
				var parts = new List<string>();
				if (classes.Any(c => c != heading))
				{
					parts.Add(AsFound + string.Join(" / ", classes.Select(c => $"class=\"{c}\"")));
				}

				parts.AddRange(texts.Select(t => counts[t] > 1 ? $"{t} ×{counts[t]}" : t));
				yield return new QualityIssue(filename, IssueType, pattern, string.Join(Separator, parts));
			}
		}

		/// <summary>Between the parts of a finding's text: one line each when shown (a log row has no line breaks).</summary>
		internal const string Separator = " ¦ ";

		/// <summary>Starts the part naming the class attributes as the page writes them.</summary>
		internal const string AsFound = "as found: ";

		/// <summary>The finding's text as lines: the class attributes as found, then each heading text.</summary>
		internal static IReadOnlyList<string> Lines(string context) =>
			context.Split(Separator, StringSplitOptions.None);

		private static string Cut(string text, int radius) =>
			radius > 0 && text.Length > radius ? text[..radius] + "…" : text;
	}
}
