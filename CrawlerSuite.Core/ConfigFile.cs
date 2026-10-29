namespace CrawlerSuite.Core
{
	using System.Text;
	using System.Text.Encodings.Web;
	using System.Text.Json;
	using System.Text.Json.Nodes;

	/// <summary>
	/// Reads and writes crawlersuite.config.json as plain data. Reading accepts comments and
	/// trailing commas (a file written by hand or copied from an older one); writing never emits
	/// comments — every explanation lives in the app. The layout keeps the file short and easy
	/// to compare:
	/// <list type="bullet">
	/// <item>objects indented, one key per line, in the order they have;</item>
	/// <item>a list of plain values on one line: <c>["de","en"]</c>;</item>
	/// <item>a list of objects with one object per line, each written compactly.</item>
	/// </list>
	/// Keys are kept as they are, including ones the app does not know; what to keep is decided
	/// before writing, not here.
	/// </summary>
	public static class ConfigFile
	{
		private static readonly JsonDocumentOptions ReadOptions = new()
		{
			CommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true,
		};

		private static readonly JsonSerializerOptions Compact = new()
		{
			WriteIndented = false,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // umlauts and other letters stay readable
		};

		private const string Indent = "  ";

		private static readonly HashSet<string> BackedUp = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>The file's content as a node tree; null when the file does not exist. Throws <see cref="JsonException"/> on broken JSON.</summary>
		public static JsonObject? Read(string path)
		{
			if (!File.Exists(path))
			{
				return null;
			}

			var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8), documentOptions: ReadOptions);
			return node as JsonObject ?? throw new JsonException("The configuration must be a JSON object.");
		}

		/// <summary>
		/// Writes the tree in the file's layout: through a temporary file, so a failure never
		/// leaves half a configuration, UTF-8 without BOM. A numbered backup of the existing file
		/// (<c>…json.1</c>, <c>.2</c> …) is made before the first write while the app runs.
		/// </summary>
		public static void Save(string path, JsonObject root)
		{
			lock (BackedUp)
			{
				if (File.Exists(path) && BackedUp.Add(Path.GetFullPath(path)))
				{
					int number = 1;
					while (File.Exists($"{path}.{number}"))
					{
						number++;
					}

					// The backup is the file as it was, except a proxy password: that is never kept.
					File.WriteAllText($"{path}.{number}", WithoutPassword(File.ReadAllText(path)), new UTF8Encoding(false));
				}
			}

			var temp = path + ".tmp";
			File.WriteAllText(temp, Format(root), new UTF8Encoding(false));
			File.Move(temp, path, overwrite: true);
		}

		/// <summary>
		/// The text with the value of every "ProxyPassword" emptied, everything else untouched
		/// (comments and layout included, and a file that does not parse).
		/// </summary>
		internal static string WithoutPassword(string text) =>
			System.Text.RegularExpressions.Regex.Replace(
				text,
				@"(""ProxyPassword""\s*:\s*)""(?:[^""\\]|\\.)*""",
				"$1\"\"");

		/// <summary>The file text for a tree: the layout described on the class.</summary>
		public static string Format(JsonObject root)
		{
			var sb = new StringBuilder();
			WriteObject(sb, root, 0);
			sb.Append(FileFormat.NewLine);
			return sb.ToString();
		}

		private static void WriteObject(StringBuilder sb, JsonObject obj, int depth)
		{
			if (obj.Count == 0)
			{
				sb.Append("{}");
				return;
			}

			sb.Append('{').Append(FileFormat.NewLine);
			int i = 0;
			foreach (var (key, value) in obj)
			{
				Pad(sb, depth + 1);
				sb.Append(JsonSerializer.Serialize(key, Compact)).Append(": ");
				WriteValue(sb, value, depth + 1);
				if (++i < obj.Count)
				{
					sb.Append(',');
				}

				sb.Append(FileFormat.NewLine);
			}

			Pad(sb, depth);
			sb.Append('}');
		}

		private static void WriteValue(StringBuilder sb, JsonNode? value, int depth)
		{
			switch (value)
			{
				case JsonObject obj:
					WriteObject(sb, obj, depth);
					break;
				case JsonArray array when array.Count == 0:
					sb.Append("[]");
					break;
				case JsonArray array when array.All(e => e is JsonObject):
					// One object per line, each compact.
					sb.Append('[').Append(FileFormat.NewLine);
					for (int i = 0; i < array.Count; i++)
					{
						Pad(sb, depth + 1);
						sb.Append(array[i]!.ToJsonString(Compact));
						if (i < array.Count - 1)
						{
							sb.Append(',');
						}

						sb.Append(FileFormat.NewLine);
					}

					Pad(sb, depth);
					sb.Append(']');
					break;
				case null:
					sb.Append("null");
					break;
				default:
					// Plain values, and a list of plain values, on one line.
					sb.Append(value.ToJsonString(Compact));
					break;
			}
		}

		private static void Pad(StringBuilder sb, int depth)
		{
			for (int i = 0; i < depth; i++)
			{
				sb.Append(Indent);
			}
		}
	}
}
