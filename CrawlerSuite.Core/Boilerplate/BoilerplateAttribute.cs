namespace CrawlerSuite.Core.Boilerplate
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using HtmlAgilityPack;

	/// <summary>
	/// A boilerplate part of type "attribute": an attribute the template writes with a value that
	/// changes on every delivery (a salt, a nonce, a monitoring id). "@name" takes it on every
	/// element, "tag/@name" only on that element. On a group's pages it is removed like the
	/// group's other parts — the element stays, only the attribute goes — and kept on its check
	/// pages. Attributes the analysis itself reads are refused: removing them would change findings.
	/// </summary>
	public static class BoilerplateAttribute
	{
		public const string Type = "attribute";

		/// <summary>Attributes the analysis reads: language, structure, addresses, text it checks.</summary>
		private static readonly HashSet<string> ReadByAnalysis = new(StringComparer.OrdinalIgnoreCase)
		{
			"lang", "xml:lang", "dir", "translate",
			"class", "id", "style", "role", "hidden", "inert", "open", "disabled", "tabindex",
			"href", "src", "srcset", "rel",
			"alt", "title", "placeholder", "value", "content", "name", "type", "media", "charset", "http-equiv", "for",
		};

		/// <summary>Families the analysis reads by prefix: accessibility names, script handlers.</summary>
		private static readonly string[] ReadByAnalysisPrefixes = ["aria-", "on"];

		/// <summary>The element (null for every element) and the attribute of a part's value; false when it has another form.</summary>
		public static bool TryParse(string? value, out string? tag, out string name)
		{
			tag = null;
			name = string.Empty;
			var text = value?.Trim() ?? string.Empty;
			int at = text.IndexOf('@');
			if (at < 0 || text.IndexOf('@', at + 1) >= 0)
			{
				return false;
			}

			var element = text[..at];
			if (element.Length > 0)
			{
				if (!element.EndsWith('/'))
				{
					return false;
				}

				element = element[..^1];
				if (element == "*")
				{
					element = string.Empty;
				}
				else if (!IsName(element))
				{
					return false;
				}
			}

			var attribute = text[(at + 1)..];
			if (!IsName(attribute) && !IsName(attribute.Replace(":", string.Empty, StringComparison.Ordinal)))
			{
				return false;
			}

			// The parser keeps names in lower case.
			tag = element.Length == 0 ? null : element.ToLowerInvariant();
			name = attribute.ToLowerInvariant();
			return true;
		}

		/// <summary>What is wrong with an attribute part's value, or null.</summary>
		public static string? Check(string? value)
		{
			if (!TryParse(value, out _, out var name))
			{
				return "Write @name for every element or tag/@name for one, e.g. @data-x or script/@data-x.";
			}

			return IsReadByAnalysis(name)
				? $"The analysis reads {name}: leaving it out would change findings. Only attributes it does not read (data-* and the like) can be parts."
				: null;
		}

		/// <summary>True for an attribute the analysis reads.</summary>
		public static bool IsReadByAnalysis(string name) =>
			ReadByAnalysis.Contains(name)
			|| ReadByAnalysisPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

		/// <summary>The attribute parts of a part list, enabled ones only, as (element or null, attribute).</summary>
		public static List<(string? Tag, string Name)> PartsOf(IEnumerable<BoilerplateSelectorConfig>? selectors)
		{
			var parts = new List<(string?, string)>();
			foreach (var selector in selectors ?? [])
			{
				if (selector is { Enabled: true } && IsAttributeType(selector.Type)
					&& Check(selector.Value) is null && TryParse(selector.Value, out var tag, out var name))
				{
					parts.Add((tag, name));
				}
			}

			return parts;
		}

		public static bool IsAttributeType(string? type) => string.Equals(type?.Trim(), Type, StringComparison.OrdinalIgnoreCase);

		/// <summary>True when one of the parts takes this attribute of this element.</summary>
		public static bool Takes(IReadOnlyList<(string? Tag, string Name)> parts, HtmlNode element, string attributeName)
		{
			foreach (var (tag, name) in parts)
			{
				if (string.Equals(name, attributeName, StringComparison.OrdinalIgnoreCase)
					&& (tag is null || string.Equals(tag, element.Name, StringComparison.OrdinalIgnoreCase)))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Removes the attributes the parts take, from every element of the document; returns how many went.</summary>
		public static int RemoveFrom(HtmlDocument doc, IReadOnlyList<(string? Tag, string Name)> parts)
		{
			if (parts.Count == 0)
			{
				return 0;
			}

			int removed = 0;
			foreach (var node in doc.DocumentNode.Descendants())
			{
				if (node.NodeType != HtmlNodeType.Element || !node.HasAttributes)
				{
					continue;
				}

				for (int i = node.Attributes.Count - 1; i >= 0; i--)
				{
					if (Takes(parts, node, node.Attributes[i].Name))
					{
						node.Attributes.RemoveAt(i);
						removed++;
					}
				}
			}

			return removed;
		}

		private static bool IsName(string text) =>
			text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_')
			&& text.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
	}
}
