using HtmlAgilityPack;

namespace CrawlerSuite.Core.Quality
{
	/// <summary>
	/// DUPLICATE_ATTRIBUTE: one attribute written twice (or more) in one tag — rel="noopener noreferrer"
	/// rel="noopener", class="btn" class="btn-primary", or the same value twice. HTML allows each
	/// attribute once. Which copy a browser or another tool goes by is not assumed: the finding lists
	/// the values in the order they are written, or says the value is the same each time. The fix is
	/// the same either way: write the attribute once.
	/// <para>
	/// One finding per page for each tag, attribute and set of values: thirty links with the same
	/// doubled rel are one item, counted. Read from the pruned page like the other content checks, so
	/// a duplicate in the site's chrome is reported on its group's reference pages, not on every
	/// page. Kept on its page in triage, never gathered into a shared entry: whether the template or
	/// the page writes it cannot be told from outside.
	/// </para>
	/// </summary>
	internal static class DuplicateAttributes
	{
		internal const string IssueType = "DUPLICATE_ATTRIBUTE";

		/// <summary>Characters of a value shown in the finding before it is cut.</summary>
		private const int ValueShown = 80;

		internal static IEnumerable<QualityIssue> Check(string filename, HtmlDocument doc)
		{
			// (tag, attribute, values) → how many elements, and the first one's start tag.
			var found = new Dictionary<(string Tag, string Name, string Values), (int Count, string Tag, string Detail)>();
			var order = new List<(string, string, string)>();

			foreach (var node in doc.DocumentNode.Descendants())
			{
				if (node.NodeType != HtmlNodeType.Element || node.Attributes.Count < 2)
				{
					continue;
				}

				foreach (var group in node.Attributes.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
				{
					var values = group.Select(a => HtmlEntity.DeEntitize(a.Value ?? string.Empty) ?? string.Empty).ToList();
					var key = (node.Name, group.Key.ToLowerInvariant(), string.Join("\u0001", values));
					if (found.TryGetValue(key, out var seen))
					{
						found[key] = (seen.Count + 1, seen.Tag, seen.Detail);
						continue;
					}

					found[key] = (1, Triage.SpellingMarkup.StartTag(node, "\u0000"), Detail(node.Name, group.Key.ToLowerInvariant(), values));
					order.Add(key);
				}
			}

			foreach (var key in order)
			{
				var (count, tag, detail) = found[key];
				yield return new QualityIssue(filename, IssueType, count > 1 ? $"{detail} (×{count} on this page)" : detail, tag);
			}
		}

		/// <summary>What the finding says: the attribute, how often, and its values in the order written.</summary>
		internal static string Detail(string tag, string name, IReadOnlyList<string> values)
		{
			var head = $"Attribute “{name}” written {values.Count} times in <{tag}>";
			if (values.Distinct(StringComparer.Ordinal).Count() == 1)
			{
				return $"{head}, the same value each time: “{Shown(values[0])}”";
			}

			var later = string.Join(", ", values.Skip(1).Select(v => $"“{Shown(v)}”"));
			return $"{head}: first “{Shown(values[0])}”, then {later}";
		}

		/// <summary>The attribute's name as the detail gives it; null when the detail is not this finding's.</summary>
		internal static string? NameOf(string? detail)
		{
			const string start = "Attribute “";
			if (detail is null || !detail.StartsWith(start, StringComparison.Ordinal))
			{
				return null;
			}

			int end = detail.IndexOf('”', start.Length);
			return end > start.Length ? detail[start.Length..end] : null;
		}

		private static string Shown(string value)
		{
			var one = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
			return one.Length > ValueShown ? one[..(ValueShown - 1)] + "…" : one;
		}
	}
}
