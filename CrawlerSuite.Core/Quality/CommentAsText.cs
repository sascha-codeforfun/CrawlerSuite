using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace CrawlerSuite.Core.Quality
{
	/// <summary>
	/// COMMENT_AS_TEXT: an HTML comment that is not one, so the page shows it. A comment opens with
	/// exactly "&lt;!--"; browsers read "&lt;" as markup only before a letter, "/", "!" or "?", so
	/// "&lt;-- note --&gt;" (the "!" left out) and "&lt; !-- note --&gt;" (a space after "&lt;") are text, and
	/// so is a comment written with entities ("&amp;lt;!-- note --&amp;gt;"). ("&lt;!" before anything
	/// else opens a bogus comment that runs to the next "&gt;": hidden, so no finding.) The check reads
	/// only what the parser kept as text, so whatever it finds, visitors see; screen readers read it
	/// out, and the spell check used to report its words without saying why.
	/// <para>
	/// Reported only for an opener AND a closer ("--&gt;") in the same visible text, so an arrow
	/// written as "&lt;--" or "--&gt;" on its own is not a finding. Text a reader never sees as prose
	/// is left out: scripts, styles, templates, form fields, and code samples (pre, code, kbd, samp),
	/// where a comment shown as text is what the page means to show.
	/// </para>
	/// </summary>
	internal static class CommentAsText
	{
		internal const string IssueType = "COMMENT_AS_TEXT";

		// An opener that is not "<!--" (or is, written as text), a short body on one line, a closer.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		internal static readonly Regex Broken = new(
			@"(?<open><(?:!--|!-(?!-)|--|\s+!\s*--|!\s+--|!\s*[–—]))(?<body>[^<>\r\n]{0,300}?)(?<close>(?:--|[–—])\s*>)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly HashSet<string> NotProse = new(StringComparer.OrdinalIgnoreCase)
		{
			"script", "style", "noscript", "template", "textarea", "pre", "code", "kbd", "samp",
		};

		internal static IEnumerable<QualityIssue> Check(string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			foreach (var node in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Text))
			{
				if (InsideNotProse(node))
				{
					continue;
				}

				var text = HtmlEntity.DeEntitize(((HtmlTextNode)node).Text) ?? string.Empty;
				if (text.IndexOf('<') < 0)
				{
					continue;
				}

				foreach (Match m in Broken.Each(text))
				{
					yield return new QualityIssue(
						filename,
						IssueType,
						$"Comment shown as text: “{m.Value}” — {Diagnosis(m.Groups["open"].Value)}",
						Excerpt.Around(text, m.Index, config.ContentQualityExcerptRadius));
				}
			}
		}

		/// <summary>Why the opener is no comment, in the words the card and the finding use.</summary>
		internal static string Diagnosis(string open)
		{
			var o = open.Trim();
			// Text that starts with "<!" was written with "&lt;": in the markup it would have been a comment.
			return o switch
			{
				"<--" => "the “!” is missing: a comment opens with “<!--”",
				_ when o.StartsWith("<!", StringComparison.Ordinal) => "written as text (its “<” as &lt;): remove it, or write it as a real comment",
				_ => "a space after “<”: a comment opens with “<!--”, all together",
			};
		}

		private static bool InsideNotProse(HtmlNode node)
		{
			for (var p = node.ParentNode; p != null && p.NodeType != HtmlNodeType.Document; p = p.ParentNode)
			{
				if (p.NodeType == HtmlNodeType.Element && NotProse.Contains(p.Name))
				{
					return true;
				}
			}

			return false;
		}
	}
}
