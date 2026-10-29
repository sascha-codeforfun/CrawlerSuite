namespace CrawlerSuite.Core.Settings
{
	using System.Globalization;
	using System.Text;
	using System.Text.Encodings.Web;
	using System.Text.Json;
	using System.Text.Json.Nodes;

	/// <summary>
	/// Turns a setting's value into the text of its field and back. A field's text that is not a
	/// valid value comes back with what is wrong, so the screen can say it at the field.
	/// </summary>
	public static class SettingValues
	{
		private static readonly JsonSerializerOptions Compact = new()
		{
			WriteIndented = false,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};

		private static readonly JsonDocumentOptions Lenient = new()
		{
			CommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true,
		};

		/// <summary>The text a field shows for a value (null: the built-in default is decided by the app).</summary>
		[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reads the defaults by reflection.")]
		public static string ToText(SettingInfo setting, JsonNode? value)
		{
			if (value is null)
			{
				return string.Empty;
			}

			switch (setting.Kind)
			{
				case SettingKind.Lines when value is JsonArray list:
					return string.Join(Environment.NewLine, list.Select(e => e?.GetValue<string>() is { } s && s.Length > 0 ? s : "\"\""));
				case SettingKind.Json:
					return FormatJson(value);
				case SettingKind.Switch:
					return value.GetValue<bool>() ? "on" : "off";
				case SettingKind.Choice when value is JsonValue c && c.TryGetValue<string>(out var chosen) && setting.Choices is { } choices:
					// Older files write a choice in other casing ("off"), or empty for the default: shown as the choice it means.
					return choices.FirstOrDefault(x => string.Equals(x, chosen, StringComparison.OrdinalIgnoreCase))
						?? (chosen.Trim().Length == 0 && SettingsCatalog.DefaultOf(setting.Path) is JsonValue d && d.TryGetValue<string>(out var named)
							? named
							: chosen);
				default:
					return value is JsonValue v && v.TryGetValue<string>(out var text)
						? text
						: value.ToJsonString(Compact);
			}
		}

		/// <summary>
		/// Reads a field's text as a value. Returns the value and null, or null and what is wrong.
		/// </summary>
		[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reads the defaults by reflection.")]
		public static (JsonNode? Value, string? Error) FromText(SettingInfo setting, string text)
		{
			text ??= string.Empty;
			switch (setting.Kind)
			{
				case SettingKind.Number:
					if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
					{
						return (null, "A whole number is needed.");
					}

					if (OutOfRange(setting, whole) is { } wholeRange)
					{
						return (null, wholeRange);
					}

					return (whole is >= int.MinValue and <= int.MaxValue && setting.Max is not > int.MaxValue
						? JsonValue.Create((int)whole)
						: JsonValue.Create(whole), null);

				case SettingKind.Decimal:
					var normal = text.Trim().Replace(',', '.');
					if (!double.TryParse(normal, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
					{
						return (null, "A number is needed, e.g. 0.6.");
					}

					return OutOfRange(setting, number) is { } range ? (null, range) : (JsonValue.Create(number), null);

				case SettingKind.Choice:
					var choice = text.Trim();
					return setting.Choices is { } choices && !choices.Contains(choice, StringComparer.Ordinal)
						? (null, "Choose one of the listed values.")
						: (JsonValue.Create(choice), null);

				case SettingKind.Lines:
					var lines = new JsonArray();
					foreach (var raw in text.Split('\n'))
					{
						var line = raw.Trim().TrimEnd('\r').Trim();
						if (line.Length == 0)
						{
							continue;
						}

						lines.Add(line == "\"\"" ? string.Empty : line);
					}

					return (lines, null);

				case SettingKind.Json:
					try
					{
						var node = JsonNode.Parse(text.Trim().Length == 0 ? EmptyJson(setting) : text, documentOptions: Lenient);
						var expected = SettingsCatalog.DefaultOf(setting.Path);
						if (expected is JsonArray && node is not JsonArray)
						{
							return (null, "A list is needed: [ … ] with one entry per line.");
						}

						if (expected is JsonObject && node is not JsonObject)
						{
							return (null, "A map is needed: { … } with one key per line.");
						}

						return (node, null);
					}
					catch (JsonException ex)
					{
						return (null, "Not valid JSON: " + ex.Message);
					}

				case SettingKind.Switch:
					return (JsonValue.Create(text.Trim().Equals("on", StringComparison.OrdinalIgnoreCase)
						|| text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)), null);

				default:
					return (JsonValue.Create(text), null);
			}
		}

		/// <summary>A list or map as the file writes it: one entry per line, each compact.</summary>
		public static string FormatJson(JsonNode value)
		{
			var nl = Environment.NewLine;
			switch (value)
			{
				case JsonArray list when list.Count == 0:
					return "[]";
				case JsonArray list when list.Any(e => e is JsonObject or JsonArray):
					return "[" + nl + string.Join("," + nl, list.Select(e => "  " + (e?.ToJsonString(Compact) ?? "null"))) + nl + "]";
				case JsonObject map when map.Count == 0:
					return "{}";
				case JsonObject map:
					var sb = new StringBuilder("{" + nl);
					int i = 0;
					foreach (var (key, item) in map)
					{
						sb.Append("  ").Append(JsonSerializer.Serialize(key, Compact)).Append(": ").Append(item?.ToJsonString(Compact) ?? "null");
						sb.Append(++i < map.Count ? "," : string.Empty).Append(nl);
					}

					return sb.Append('}').ToString();
				default:
					return value.ToJsonString(Compact);
			}
		}

		[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reads the defaults by reflection.")]
		private static string EmptyJson(SettingInfo setting) =>
			SettingsCatalog.DefaultOf(setting.Path) is JsonObject ? "{}" : "[]";

		private static string? OutOfRange(SettingInfo setting, double value) =>
			(setting.Min is { } min && value < min) || (setting.Max is { } max && value > max)
				? $"Allowed: {Show(setting.Min)} to {Show(setting.Max)}."
				: null;

		private static string Show(double? n) => n?.ToString("0.##", CultureInfo.InvariantCulture) ?? "any";
	}
}
