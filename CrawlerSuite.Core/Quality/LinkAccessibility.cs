using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace CrawlerSuite.Core.Quality
{
	/// <summary>
	/// The accessibility rules behind MISPLACED_ANCHOR_EMPTY, ARIA_HIDDEN_FOCUSABLE and
	/// ARIA_REFERENCE_BROKEN, following the W3C ACT rules "Link has non-empty accessible name"
	/// (c487ae) and "Element with aria-hidden has no content in sequential focus navigation"
	/// (6cfa84). Shared by the detector and by the view's marker, so the two always agree.
	/// <para>
	/// What a crawler cannot see: hiding through a stylesheet (classes, CSS rules). Only the
	/// <c>hidden</c> attribute, inline <c>display:none</c> / <c>visibility:hidden</c> and
	/// <c>&lt;template&gt;</c> are recognised as "not rendered".
	/// </para>
	/// </summary>
	internal static partial class LinkAccessibility
	{
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex HiddenStyleRegex = new(@"display\s*:\s*none|visibility\s*:\s*(hidden|collapse)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static Regex HiddenStyle() => HiddenStyleRegex;

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

		private static Regex Whitespace() => WhitespaceRegex;

		/// <summary>Resolves an id to an element of the page (the pruned page first, then the raw download).</summary>
		internal delegate HtmlNode? IdLookup(string id);

		// ── What an element is ───────────────────────────────────────────────────

		/// <summary>
		/// A link for the purpose of the name check: an &lt;a&gt; with href, or one made
		/// interactive by role="link"/"button" or a tabindex of 0 or more. An &lt;a&gt; without
		/// any of these is not a link (a jump target) and is never checked.
		/// </summary>
		internal static bool IsLinkLike(HtmlNode element) =>
			element.Name == "a"
			&& (element.Attributes["href"] is not null
				|| Role(element) is "link" or "button"
				|| TabIndex(element) is >= 0);

		/// <summary>False when the element or a parent has the hidden attribute, inline display:none or visibility:hidden, or sits in a &lt;template&gt;.</summary>
		internal static bool IsRendered(HtmlNode element)
		{
			for (var node = element; node is not null && node.NodeType == HtmlNodeType.Element; node = node.ParentNode)
			{
				if (node.Name == "template"
					|| node.Attributes["hidden"] is not null
					|| HiddenStyle().IsMatch(node.GetAttributeValue("style", string.Empty)))
				{
					return false;
				}
			}

			return true;
		}

		// ── Focusable inside aria-hidden: candidates and what takes them out ─────

		private static readonly HashSet<string> FormControls = new(StringComparer.Ordinal)
		{
			"button", "input", "select", "textarea", "optgroup", "option", "fieldset",
		};

		// An inline style property with its value, the last declaration winning as in the browser.
		private static string? InlineStyle(HtmlNode node, string property)
		{
			string? value = null;
			foreach (var declaration in node.GetAttributeValue("style", string.Empty).Split(';'))
			{
				int colon = declaration.IndexOf(':');
				if (colon > 0 && declaration[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
				{
					value = declaration[(colon + 1)..].Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
				}
			}

			return value;
		}

		/// <summary>
		/// True when the element takes part in Tab navigation by what it is: a link or area with
		/// href, a button, select, textarea, summary, an input other than type="hidden", or any
		/// element with tabindex 0 or more — before anything that may take it out (see
		/// <see cref="NotReachable"/>). An element with tabindex="-1" is a candidate when it would
		/// be focusable without it, so its exclusion can be named.
		/// </summary>
		internal static bool IsFocusableByNature(HtmlNode element)
		{
			if (TabIndex(element) is >= 0)
			{
				return true;
			}

			return element.Name switch
			{
				"a" or "area" => element.Attributes["href"] is not null,
				"button" or "select" or "textarea" or "summary" => true,
				"input" => !element.GetAttributeValue("type", string.Empty).Trim().Equals("hidden", StringComparison.OrdinalIgnoreCase),
				_ => false,
			};
		}

		/// <summary>
		/// Why the element is not reachable with Tab after all, read from the markup of the element
		/// and every ancestor; null when nothing in the markup takes it out. The reason names the
		/// attribute or style and the element carrying it — the evidence of a cleared finding.
		/// Not counted as hidden: opacity:0, clip, off-screen positioning, zero size — such
		/// elements stay in the Tab order.
		/// </summary>
		internal static string? NotReachable(HtmlNode element)
		{
			if (TabIndex(element) is { } tabIndex && tabIndex < 0)
			{
				return $"tabindex=\"{element.GetAttributeValue("tabindex", string.Empty).Trim()}\" takes it out of the Tab order";
			}

			if (FormControls.Contains(element.Name) && element.Attributes["disabled"] is not null)
			{
				return "it is disabled";
			}

			// visibility is inherited, and a nearer visibility:visible overrides a farther hidden.
			bool visibilityDecided = false;
			HtmlNode? child = null;
			for (var node = element; node is not null && node.NodeType == HtmlNodeType.Element; child = node, node = node.ParentNode)
			{
				var self = ReferenceEquals(node, element);
				var where = self ? "on the element" : $"on the ancestor {Describe(node)}";

				if (!self && node.Name is "template" or "noscript")
				{
					return $"it is inside <{node.Name}>, which is not rendered";
				}

				if (node.Attributes["hidden"] is not null)
				{
					return $"hidden attribute {where}";
				}

				if (node.Attributes["inert"] is not null)
				{
					return $"inert attribute {where}";
				}

				if (InlineStyle(node, "display") == "none")
				{
					return $"inline style display:none {where}";
				}

				if (!visibilityDecided && InlineStyle(node, "visibility") is { } visibility)
				{
					visibilityDecided = true;
					if (visibility is "hidden" or "collapse")
					{
						return $"inline style visibility:{visibility} {where}";
					}
				}

				// content-visibility:hidden hides an element's contents, not the element itself.
				if (!self && InlineStyle(node, "content-visibility") == "hidden")
				{
					return $"inline style content-visibility:hidden {where}";
				}

				// A closed <details> shows only its first <summary>.
				if (!self && node.Name == "details" && node.Attributes["open"] is null
					&& !ReferenceEquals(child, node.ChildNodes.FirstOrDefault(c => c.Name == "summary")))
				{
					return $"it is inside a closed <details> ({Describe(node)}), outside its summary";
				}

				// <fieldset disabled> disables its form controls, except those in its first <legend>.
				if (!self && node.Name == "fieldset" && node.Attributes["disabled"] is not null
					&& FormControls.Contains(element.Name)
					&& !ReferenceEquals(child, node.ChildNodes.FirstOrDefault(c => c.Name == "legend")))
				{
					return $"it is inside a disabled <fieldset> ({Describe(node)})";
				}
			}

			return null;
		}

		/// <summary>True when the element or a parent has aria-hidden="true".</summary>
		internal static bool IsAriaHidden(HtmlNode element)
		{
			for (var node = element; node is not null && node.NodeType == HtmlNodeType.Element; node = node.ParentNode)
			{
				if (node.GetAttributeValue("aria-hidden", string.Empty).Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// True when the element is in the sequential focus order (reachable with Tab): a
		/// tabindex of 0 or more, or a natively focusable element (a/area with href, button,
		/// input other than hidden, select, textarea, summary) that is not disabled and has no
		/// negative tabindex.
		/// </summary>
		internal static bool IsFocusable(HtmlNode element)
		{
			if (TabIndex(element) is { } tabIndex)
			{
				return tabIndex >= 0;
			}

			if (element.Attributes["disabled"] is not null)
			{
				return false;
			}

			return element.Name switch
			{
				"a" or "area" => element.Attributes["href"] is not null,
				"button" or "select" or "textarea" or "summary" => true,
				"input" => !element.GetAttributeValue("type", string.Empty).Trim().Equals("hidden", StringComparison.OrdinalIgnoreCase),
				_ => false,
			};
		}

		// ── Accessible name ──────────────────────────────────────────────────────

		/// <summary>
		/// Why a link has no accessible name, as the Detail of a finding; null when it has one.
		/// Name order: aria-labelledby → aria-label → content (aria-hidden parts left out, image
		/// alt, SVG title or aria-label) → title. Whitespace-only counts as empty.
		/// </summary>
		internal static string? MissingName(HtmlNode link, IdLookup resolve)
		{
			var missingIds = new List<string>();
			var labelledBy = Tokens(link.GetAttributeValue("aria-labelledby", string.Empty));
			if (labelledBy.Count > 0)
			{
				var text = new StringBuilder();
				foreach (var id in labelledBy)
				{
					if (resolve(id) is { } target)
					{
						text.Append(' ').Append(PlainText(target));
					}
					else
					{
						missingIds.Add(id);
					}
				}

				if (Normalise(text.ToString()).Length > 0)
				{
					return null;
				}
			}

			if (Normalise(Decode(link.GetAttributeValue("aria-label", string.Empty))).Length > 0)
			{
				return null;
			}

			var images = new ImageTally();
			if (Normalise(Content(link, images)).Length > 0)
			{
				return null;
			}

			if (Normalise(Decode(link.GetAttributeValue("title", string.Empty))).Length > 0)
			{
				return null;
			}

			var target2 = Describe(link);
			if (missingIds.Count > 0)
			{
				return $"No accessible name — aria-labelledby points to missing id {Quoted(missingIds)} — {target2}";
			}

			if (images.Missing > 0)
			{
				return $"Image link: the image has no alt attribute — {target2}";
			}

			if (images.Decorative > 0)
			{
				return $"Image link: the image is marked decorative (alt=\"\") — {target2}";
			}

			return $"No accessible name — {target2}";
		}

		/// <summary>The text a link's content contributes to its name.</summary>
		private static string Content(HtmlNode node, ImageTally images)
		{
			var text = new StringBuilder();
			foreach (var child in node.ChildNodes)
			{
				switch (child.NodeType)
				{
					case HtmlNodeType.Text:
						text.Append(Decode(child.InnerText));
						break;
					case HtmlNodeType.Element:
						if (child.GetAttributeValue("aria-hidden", string.Empty).Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
							|| child.Attributes["hidden"] is not null
							|| HiddenStyle().IsMatch(child.GetAttributeValue("style", string.Empty)))
						{
							break;
						}

						var label = Decode(child.GetAttributeValue("aria-label", string.Empty));
						if (Normalise(label).Length > 0)
						{
							text.Append(' ').Append(label).Append(' ');
							break;
						}

						if (child.Name == "img" || (child.Name == "input" && child.GetAttributeValue("type", string.Empty).Equals("image", StringComparison.OrdinalIgnoreCase)))
						{
							if (Role(child) is "presentation" or "none")
							{
								images.Decorative++;
							}
							else if (child.Attributes["alt"] is not { } alt)
							{
								images.Missing++;
							}
							else if (Normalise(Decode(alt.Value)).Length == 0)
							{
								images.Decorative++;
							}
							else
							{
								text.Append(' ').Append(Decode(alt.Value)).Append(' ');
							}

							break;
						}

						if (child.Name == "svg")
						{
							var title = child.ChildNodes.FirstOrDefault(c => c.Name == "title");
							text.Append(' ').Append(title is null ? string.Empty : Decode(title.InnerText)).Append(' ');
							break;
						}

						if (child.Name is "script" or "style" or "template")
						{
							break;
						}

						text.Append(' ').Append(Content(child, images)).Append(' ');
						break;
				}
			}

			return text.ToString();
		}

		private sealed class ImageTally
		{
			public int Missing { get; set; }

			public int Decorative { get; set; }
		}

		// ── ARIA references ──────────────────────────────────────────────────────

		/// <summary>The id-referencing attributes the reference check covers (targets created at runtime are out of scope).</summary>
		internal static readonly string[] ReferenceAttributes = ["aria-labelledby", "aria-describedby"];

		/// <summary>The ids an attribute value names (whitespace-separated).</summary>
		internal static IReadOnlyList<string> Tokens(string value) =>
			value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

		// ── Helpers ──────────────────────────────────────────────────────────────

		/// <summary>"&lt;a href="/x"&gt;", "&lt;button&gt;", "&lt;input type="text"&gt;", "&lt;div tabindex="0"&gt;" — the element as the Detail names it.</summary>
		internal static string Describe(HtmlNode element)
		{
			var sb = new StringBuilder("<").Append(element.Name);
			void Add(string attribute)
			{
				if (element.Attributes[attribute] is { } a)
				{
					sb.Append(' ').Append(attribute).Append("=\"").Append(a.Value).Append('"');
				}
			}

			switch (element.Name)
			{
				case "a" or "area":
					Add("href");
					if (element.Attributes["href"] is null)
					{
						Add("role");
						Add("tabindex");
					}

					break;
				case "input":
					Add("type");
					Add("name");
					break;
				case "button" or "select" or "textarea" or "summary":
					Add("name");
					break;
				default:
					Add("role");
					Add("tabindex");
					break;
			}

			return sb.Append('>').ToString();
		}

		private static string? Role(HtmlNode element)
		{
			var role = element.GetAttributeValue("role", string.Empty).Trim();
			return role.Length == 0 ? null : role.Split(' ')[0].ToLowerInvariant();
		}

		private static int? TabIndex(HtmlNode element) =>
			element.Attributes["tabindex"] is { } attribute
			&& int.TryParse(attribute.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
				? value
				: null;

		private static string PlainText(HtmlNode node) => Decode(node.InnerText);

		private static string Decode(string value) => WebUtility.HtmlDecode(value ?? string.Empty);

		private static string Normalise(string value) => Whitespace().Replace(value, " ").Trim();

		private static string Quoted(IEnumerable<string> ids) => string.Join(", ", ids.Select(id => $"\"{id}\""));
	}
}
