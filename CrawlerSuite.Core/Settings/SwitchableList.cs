namespace CrawlerSuite.Core.Settings
{
	using System.Text.Json;
	using System.Text.Json.Serialization;

	/// <summary>
	/// A list of text entries where each entry can be switched off without deleting it. In the
	/// file an active entry is plain text; a switched-off one is <c>{"Value": "…", "Enabled": false}</c>.
	/// The list itself holds the active entries (what the engine applies); <see cref="Disabled"/>
	/// the switched-off ones, still counted in the usage numbers so their need can be judged.
	/// </summary>
	[JsonConverter(typeof(SwitchableListConverter))]
	public sealed class SwitchableList : List<string>
	{
		/// <summary>Entries kept in the file but switched off.</summary>
		public List<string> Disabled { get; } = [];
	}

	/// <summary>Reads text or <c>{"Value", "Enabled"}</c> entries; writes the same shapes.</summary>
	public sealed class SwitchableListConverter : JsonConverter<SwitchableList>
	{
		public override SwitchableList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartArray)
			{
				throw new JsonException("A list is needed.");
			}

			var list = new SwitchableList();
			while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
			{
				switch (reader.TokenType)
				{
					case JsonTokenType.String:
						list.Add(reader.GetString() ?? string.Empty);
						break;
					case JsonTokenType.StartObject:
						string? value = null;
						bool enabled = true;
						while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
						{
							var name = reader.GetString();
							reader.Read();
							if (string.Equals(name, "Value", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String)
							{
								value = reader.GetString();
							}
							else if (string.Equals(name, "Enabled", StringComparison.OrdinalIgnoreCase)
								&& reader.TokenType is JsonTokenType.True or JsonTokenType.False)
							{
								enabled = reader.GetBoolean();
							}
							else
							{
								reader.Skip();
							}
						}

						if (value is null)
						{
							throw new JsonException("An entry written as an object needs a \"Value\".");
						}

						(enabled ? (List<string>)list : list.Disabled).Add(value);
						break;
					default:
						throw new JsonException("Each entry must be a text or {\"Value\": …, \"Enabled\": …}.");
				}
			}

			return list;
		}

		public override void Write(Utf8JsonWriter writer, SwitchableList value, JsonSerializerOptions options)
		{
			writer.WriteStartArray();
			foreach (var entry in value)
			{
				writer.WriteStringValue(entry);
			}

			foreach (var entry in value.Disabled)
			{
				writer.WriteStartObject();
				writer.WriteString("Value", entry);
				writer.WriteBoolean("Enabled", false);
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		}
	}
}
