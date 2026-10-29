namespace CrawlerSuite.Core.Settings
{
	using System.Text.Json.Nodes;
	using System.Text.RegularExpressions;
	using System.Xml.XPath;

	/// <summary>One entry of a switchable list: its text, and whether it is applied.</summary>
	public sealed record SwitchEntry(string Value, bool Enabled = true);

	/// <summary>
	/// Reading and writing the settings that have editors of their own: switchable lists (an
	/// active entry is plain text, a switched-off one <c>{"Value", "Enabled": false}</c>), tag
	/// names, XPaths and boilerplate groups. Everything a value holds that the editor does not
	/// show is written back unchanged.
	/// </summary>
	public static partial class SettingEditors
	{
		// ── Switchable lists ────────────────────────────────────────────────

		/// <summary>The entries of a list, in file order.</summary>
		public static List<SwitchEntry> ReadEntries(JsonNode? value)
		{
			var entries = new List<SwitchEntry>();
			foreach (var item in value as JsonArray ?? [])
			{
				switch (item)
				{
					case JsonValue v when v.TryGetValue<string>(out var text):
						entries.Add(new SwitchEntry(text));
						break;
					case JsonObject o when o["Value"] is JsonValue ov && ov.TryGetValue<string>(out var text):
						entries.Add(new SwitchEntry(text, !(o["Enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && !on)));
						break;
				}
			}

			return entries;
		}

		/// <summary>Whether every item is a text or a <c>{"Value": …}</c> object (else the field stays JSON).</summary>
		public static bool FitsEntries(JsonNode? value) =>
			value is null || (value is JsonArray list && list.All(i =>
				i is JsonValue v && v.TryGetValue<string>(out _)
				|| i is JsonObject o && o["Value"] is JsonValue ov && ov.TryGetValue<string>(out _)));

		/// <summary>Whether the value is a plain list of texts (the chip editors), or absent.</summary>
		public static bool FitsPlainList(JsonNode? value) =>
			value is null || (value is JsonArray list && list.All(i => i is JsonValue v && v.TryGetValue<string>(out _)));

		/// <summary>Whether the value is a map of text values (the triage switches), or absent.</summary>
		public static bool FitsTextMap(JsonNode? value) =>
			value is null || (value is JsonObject map && map.All(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out _)));

		/// <summary>Whether the value is a list of objects (the card editors), or absent.</summary>
		public static bool FitsObjectList(JsonNode? value) =>
			value is null || (value is JsonArray list && list.All(i => i is JsonObject));

		/// <summary>The list as the file keeps it; empty entries are left out.</summary>
		public static JsonArray WriteEntries(IEnumerable<SwitchEntry> entries)
		{
			var list = new JsonArray();
			foreach (var entry in entries.Where(e => e.Value.Trim().Length > 0))
			{
				list.Add(entry.Enabled
					? JsonValue.Create(entry.Value.Trim())
					: new JsonObject { ["Value"] = entry.Value.Trim(), ["Enabled"] = false });
			}

			return list;
		}

		// ── Tag names ───────────────────────────────────────────────────────

		/// <summary>What is wrong with a tag name, or null.</summary>
		public static string? CheckTagName(string name) =>
			TagName().IsMatch(name.Trim()) ? null : "A tag name has letters, digits and hyphens, starting with a letter.";

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex TagNameRegex = new("^[A-Za-z][A-Za-z0-9-]*$", RegexOptions.Compiled);

		private static Regex TagName() => TagNameRegex;

		// ── XPaths ──────────────────────────────────────────────────────────

		/// <summary>What is wrong with an XPath, by the parser the run uses; null when it is valid.</summary>
		public static string? CheckXpath(string xpath)
		{
			if (string.IsNullOrWhiteSpace(xpath))
			{
				return "An XPath is needed.";
			}

			try
			{
				XPathExpression.Compile(xpath.Trim());
				return null;
			}
			catch (XPathException ex)
			{
				return "Not a valid XPath: " + ex.Message;
			}
		}

		/// <summary>The kind of a piece of an XPath, for colouring.</summary>
		public enum XpathPart
		{
			Axis,
			Name,
			Attribute,
			Function,
			Text,
			Punctuation,
		}

		/// <summary>An XPath in pieces: axes and slashes, names, @attributes, functions, quoted text.</summary>
		public static IReadOnlyList<(XpathPart Part, string Text)> SplitXpath(string xpath)
		{
			var parts = new List<(XpathPart, string)>();
			foreach (Match m in XpathPieces().Each(xpath ?? string.Empty))
			{
				var t = m.Value;
				var part = m.Groups["text"].Success ? XpathPart.Text
					: m.Groups["attr"].Success ? XpathPart.Attribute
					: m.Groups["func"].Success ? XpathPart.Function
					: m.Groups["name"].Success ? XpathPart.Name
					: m.Groups["axis"].Success ? XpathPart.Axis
					: XpathPart.Punctuation;
				parts.Add((part, t));
			}

			return parts;
		}

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex XpathPiecesRegex = new(@"(?<text>'[^']*'?|""[^""]*""?)|(?<attr>@[\w\-:*]+)|(?<func>[\w\-]+(?=\s*\())|(?<axis>//|/|::|\.\.?)|(?<name>[\w\-:*]+)|(?<other>\s+|.)", RegexOptions.Compiled);

		private static Regex XpathPieces() => XpathPiecesRegex;

		/// <summary>An XPath in plain words, for the common shapes; null for anything else.</summary>
		public static string? DescribeXpath(string xpath)
		{
			var m = SimpleXpath().Match((xpath ?? string.Empty).Trim());
			if (!m.Success)
			{
				return null;
			}

			var element = m.Groups["el"].Value == "*" ? "any element" : $"any {m.Groups["el"].Value} element";
			if (!m.Groups["attr"].Success)
			{
				return $"Matches {element}, anywhere on the page.";
			}

			var attribute = m.Groups["attr"].Value;
			var value = m.Groups["value"].Value;
			var how = m.Groups["fn"].Value switch
			{
				"contains" => $"contains '{value}'",
				"starts-with" => $"starts with '{value}'",
				_ => $"is exactly '{value}'",
			};
			return $"Matches {element} whose {attribute} {how}.";
		}

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex SimpleXpathRegex = new(@"^//(?<el>[A-Za-z][\w\-]*|\*)(?:\[(?:@(?<attr>[\w\-:]+)\s*=\s*'(?<value>[^']*)'|(?<fn>contains|starts-with)\(\s*@(?<attr>[\w\-:]+)\s*,\s*'(?<value>[^']*)'\s*\))\])?$", RegexOptions.Compiled);

		private static Regex SimpleXpath() => SimpleXpathRegex;

		// ── Boilerplate groups ──────────────────────────────────────────────

		/// <summary>Whether a value has the shape of boilerplate groups (else the field stays JSON).</summary>
		public static bool FitsBoilerplate(JsonNode? value) =>
			value is null || (value is JsonArray groups && groups.All(g => g is JsonObject o
				&& (o["PathPrefix"] is null or JsonValue)
				&& (o["PagesToCheckBoiler"] is null || o["PagesToCheckBoiler"] is JsonArray pages && pages.All(p => p is JsonValue))
				&& (o["BoilerplateSelectors"] is null || o["BoilerplateSelectors"] is JsonArray parts && parts.All(p => p is JsonObject))));

		/// <summary>The type names a boilerplate part can have.</summary>
		public static readonly IReadOnlyList<string> PartTypes = ["class", "xpath", CrawlerSuite.Core.Boilerplate.BoilerplateAttribute.Type];

		/// <summary>
		/// An xpath part that selects attributes would remove their elements (the parser hands back
		/// the element of a selected attribute): refused, pointing to the attribute type.
		/// </summary>
		public static string? CheckBoilerplateXpath(string xpath) =>
			CrawlerSuite.Core.Boilerplate.XpathShape.EndsInAttribute(xpath)
				? "This XPath selects attributes: as an xpath part it would remove their elements. Use the type attribute (e.g. script/@data-x)."
				: null;

		/// <summary>What is wrong with a boilerplate part, or null.</summary>
		public static string? CheckPart(string type, string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "A value is needed.";
			}

			return type.Trim().ToLowerInvariant() switch
			{
				"class" => value.Contains("/@", StringComparison.Ordinal) || value.TrimStart().StartsWith('@') && value.Contains('-')
						&& CrawlerSuite.Core.Boilerplate.BoilerplateAttribute.TryParse(value, out _, out _)
					? "Looks like an attribute (tag/@name): choose the type attribute."
					: value.Contains("//", StringComparison.Ordinal)
						? "Looks like an XPath: choose the type xpath."
					: value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(c => !c.StartsWith('.'))
						? null
						: "Class names without the leading dot.",
				"xpath" => CheckXpath(value) ?? CheckBoilerplateXpath(value),
				CrawlerSuite.Core.Boilerplate.BoilerplateAttribute.Type => CrawlerSuite.Core.Boilerplate.BoilerplateAttribute.Check(value),
				_ => "The type is class, xpath or attribute; any other matches nothing.",
			};
		}

		// ── File kinds (embedded-data scan) ─────────────────────────────────

		/// <summary>A kind of file the embedded-data scan can cover, and the extensions it stands for.</summary>
		public sealed record FileKind(string Name, IReadOnlyList<string> Extensions);

		/// <summary>
		/// The kinds a website's snapshot really holds. Files are typed by their content when they
		/// are downloaded: a page from .aspx, .jsp or an address without an extension is stored as
		/// .html, so .html covers every page.
		/// </summary>
		public static readonly IReadOnlyList<FileKind> FileKinds =
		[
			new("Pages", [".html"]),
			new("Scripts", [".js", ".mjs"]),
			new("Style sheets", [".css"]),
			new("Vector images", [".svg"]),
			new("Data", [".json"]),
			new("App manifests", [".webmanifest"]),
			new("XML and feeds", [".xml"]),
		];

		/// <summary>What is wrong with a file extension typed by hand, or null.</summary>
		public static string? CheckExtension(string extension) =>
			Extension().IsMatch(extension.Trim()) ? null : "An extension is a dot and letters or digits, e.g. .txt";

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex ExtensionRegex = new("^\\.[A-Za-z0-9]{1,15}$", RegexOptions.Compiled);

		private static Regex Extension() => ExtensionRegex;

		// ── Heading classes ─────────────────────────────────────────────────

		/// <summary>The classes offered as checkboxes; any other class is a chip.</summary>
		public static readonly IReadOnlyList<string> HeadingLevels = ["h1", "h2", "h3", "h4", "h5", "h6"];

		/// <summary>What is wrong with a class name, or null.</summary>
		public static string? CheckClassName(string name)
		{
			var n = name.Trim();
			return n.Length == 0 ? "A class name is needed."
				: n.StartsWith('.') ? "Class names without the leading dot."
				: n.Any(char.IsWhiteSpace) ? "One class name, without spaces."
				: null;
		}

		// ── Suppressions ────────────────────────────────────────────────────

		/// <summary>
		/// The finding types a suppression can name, with a plain name each: those written to log
		/// 10. "Content before doctype" is a Malformed HTML finding there; the template defects go
		/// to log 22, which suppressions do not touch.
		/// </summary>
		public static readonly IReadOnlyList<(string Code, string Name)> SuppressionTypes =
			Findings.FindingsCatalog.ContentQualityFamilies
				.Where(f => f.Family != "Template defects")
				.SelectMany(f => f.Types
					.Where(t => t.Type != "CONTENT_BEFORE_DOCTYPE")
					.Select(t => (t.Type, t.Type == "MALFORMED_HTML"
						? $"{f.Family}: {t.Label} (also content before doctype)"
						: $"{f.Family}: {t.Label}")))
				.ToList();

		/// <summary>The plain name of a finding type, or null for a code the app does not know.</summary>
		public static string? SuppressionName(string code) =>
			SuppressionTypes.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.Ordinal)).Name;

		// ── Unwanted patterns ───────────────────────────────────────────────

		/// <summary>How a pattern group matches, which decides the fields its card shows.</summary>
		public enum PatternShape
		{
			/// <summary>Words or markers: each occurrence, or all on a page together, is a finding.</summary>
			Words,

			/// <summary>An opener/closer pair that must never reach the reader, even complete.</summary>
			Placeholder,

			/// <summary>An opener/closer pair that is fine complete and wrong only when broken.</summary>
			Binding,
		}

		/// <summary>
		/// The shape of a group as the file has it. A grouped pair is a placeholder when both
		/// patterns are delimiters (no letter or digit) or it folds another group; else words.
		/// </summary>
		public static PatternShape ShapeOf(JsonObject group)
		{
			var patterns = Texts(group["Patterns"]);
			bool grouped = Bool(group, "GroupPatterns");
			if (grouped && patterns.Count == 2 && Bool(group, "OnlyFlagUnbalanced"))
			{
				return PatternShape.Binding;
			}

			return grouped && patterns.Count == 2
				&& (Text(group, "Reference").Length > 0 || patterns.All(p => !p.Any(char.IsLetterOrDigit)))
				? PatternShape.Placeholder
				: PatternShape.Words;
		}

		/// <summary>What a group does, in words, from its actual switches (and the groups that fold it).</summary>
		public static string ExplainPattern(JsonObject group, IEnumerable<string> foldedBy)
		{
			var patterns = Texts(group["Patterns"]);
			var lines = new List<string>();
			string opener = patterns.ElementAtOrDefault(0) ?? "…", closer = patterns.ElementAtOrDefault(1) ?? "…";
			switch (ShapeOf(group))
			{
				case PatternShape.Binding:
					lines.Add($"A complete {opener}name{closer} is fine and stays silent; only a broken one — one side of the fence missing — is reported, once per page.");
					break;
				case PatternShape.Placeholder:
					lines.Add($"Every {opener} or {closer} on a page is one finding per page, even when the pair is complete.");
					if (Text(group, "Reference") is { Length: > 0 } reference)
					{
						lines.Add($"When {opener} is open (no {closer} after it), findings of {reference} after it are folded into this one, not reported on their own. Elsewhere {reference} reports as usual.");
					}

					break;
				default:
					lines.Add(Bool(group, "GroupPatterns")
						? "All markers found on a page together are one finding, under the group's name."
						: "Each marker found on a page is its own finding.");
					break;
			}

			lines.Add(Bool(group, "CaseSensitive") ? "Case must match." : "Case is ignored.");
			if (Bool(group, "ExcludeHref"))
			{
				lines.Add("Link targets are skipped, so a slug in the navigation does not repeat on every page; the page whose own address carries it is still reported.");
			}

			if (Bool(group, "ExcludeUrl"))
			{
				lines.Add("Occurrences inside other URLs (images, canonical, scripts) become one finding per page that lists every place.");
			}

			if (!Bool(group, "CheckScript", true))
			{
				lines.Add("Inline scripts are not searched.");
			}

			if (!Bool(group, "CheckStyle", true))
			{
				lines.Add("Inline styles are not searched.");
			}

			foreach (var other in foldedBy)
			{
				lines.Add($"Inside an open {other} placeholder these findings are folded into that one.");
			}

			return string.Join(" ", lines);
		}

		/// <summary>A starting point for a new group: a purpose with its shape and switches set.</summary>
		public sealed record PatternPurpose(string Name, string Category, bool Security, JsonObject Group);

		/// <summary>The purposes "+ Add pattern group" offers. After adding, every switch can be changed.</summary>
		public static IReadOnlyList<PatternPurpose> PatternPurposes() =>
		[
			new("Leaked CMS placeholder", "Security", true, new JsonObject { ["GroupPatterns"] = true, ["CaseSensitive"] = true, ["Patterns"] = new JsonArray("", "") }),
			new("CMS editor error markers", "Security", true, new JsonObject { ["CaseSensitive"] = true, ["Patterns"] = new JsonArray() }),
			new("Retired product name", "Legacy", false, new JsonObject { ["ExcludeHref"] = true, ["ExcludeUrl"] = true, ["Patterns"] = new JsonArray() }),
			new("Banned or outdated wording", "Style", false, new JsonObject { ["Patterns"] = new JsonArray() }),
			new("Template binding", "Functional", false, new JsonObject
			{
				["GroupPatterns"] = true, ["OnlyFlagUnbalanced"] = true, ["CheckScript"] = false, ["CheckStyle"] = false, ["Patterns"] = new JsonArray("{{", "}}"),
			}),
			new("Other", string.Empty, false, new JsonObject { ["Patterns"] = new JsonArray() }),
		];

		/// <summary>The categories offered (and any the file already uses).</summary>
		public static readonly IReadOnlyList<string> PatternCategories = ["Security", "Functional", "Style", "Deprecated", "Legacy"];

		private static IReadOnlyList<string> Texts(JsonNode? node) =>
			(node as JsonArray ?? []).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var t) ? t : string.Empty).ToList();

		private static string Text(JsonObject o, string key) =>
			o[key] is JsonValue v && v.TryGetValue<string>(out var t) ? t : string.Empty;

		private static bool Bool(JsonObject o, string key, bool absent = false) =>
			o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : absent;

		// ── Languages ───────────────────────────────────────────────────────

		/// <summary>
		/// The language codes of the dictionary bundles a value of DictionaryBundles names. A language
		/// with several flavours is also offered on its own ("en" before "en-GB", "en-US"): it means
		/// the flavour marked Primary.
		/// </summary>
		public static IReadOnlyList<string> BundleLanguages(JsonNode? bundles)
		{
			var codes = (bundles as JsonArray ?? [])
				.OfType<JsonObject>()
				.Select(b => b["LanguageCode"] is JsonValue v && v.TryGetValue<string>(out var code) ? code.Trim() : string.Empty)
				.Where(c => c.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			var flavoured = codes.Where(Lexicon.LanguageTags.IsTag)
				.GroupBy(Lexicon.LanguageTags.BaseOf)
				.Where(g => g.Count() > 1)
				.Select(g => g.Key)
				.ToHashSet(StringComparer.Ordinal);
			var result = new List<string>();
			foreach (var code in codes)
			{
				var language = Lexicon.LanguageTags.BaseOf(code);
				if (flavoured.Contains(language) && !result.Contains(language, StringComparer.OrdinalIgnoreCase))
				{
					result.Add(language);
				}

				if (!result.Contains(code, StringComparer.OrdinalIgnoreCase))
				{
					result.Add(code);
				}
			}

			return result;
		}

		/// <summary>The files of a bundle entry that are not on disk (relative paths from the program's folder).</summary>
		public static IReadOnlyList<string> MissingBundleFiles(string dicFile, string affFile) =>
			new[] { dicFile, affFile }
				.Where(f => !string.IsNullOrWhiteSpace(f) && !File.Exists(AppPaths.Resolve(f)))
				.ToList();
	}
}
