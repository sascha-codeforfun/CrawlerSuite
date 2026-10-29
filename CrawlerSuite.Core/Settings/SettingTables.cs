namespace CrawlerSuite.Core.Settings
{
	using System.Text.Json.Nodes;

	/// <summary>The shape of a setting edited as a table.</summary>
	public enum TableShape
	{
		/// <summary>A list of text values: one column, one row per value.</summary>
		Values,

		/// <summary>A list of entries with the same fields: one column per field.</summary>
		Records,

		/// <summary>A map from a key to a list of text values: key column and values column.</summary>
		MapOfLists,
	}

	/// <summary>How a cell is edited.</summary>
	public enum CellKind
	{
		Text,
		Switch,
		File,

		/// <summary>Languages as checkboxes, one per installed dictionary bundle; stored as a list.</summary>
		Languages,
	}

	/// <summary>One column of a table.</summary>
	/// <param name="Field">The entry's field (Records); unused for the other shapes.</param>
	/// <param name="Header">The heading shown, in plain words.</param>
	/// <param name="Width">Width in pixels; 0 takes the space left.</param>
	/// <param name="Unique">Two rows may not have the same value here.</param>
	/// <param name="NewValue">The value of a new row's cell.</param>
	/// <param name="Optional">Records: an empty cell leaves the field out of the entry instead of writing "".</param>
	public sealed record TableColumn(
		string Field,
		string Header,
		CellKind Kind = CellKind.Text,
		double Width = 0,
		bool Unique = false,
		string NewValue = "",
		bool Optional = false);

	/// <summary>How a list or map setting is shown as a table.</summary>
	/// <param name="Separator">MapOfLists: how the values are written in their cell — "," or a line break.</param>
	/// <param name="Example">What a row does, in one line; null when it says nothing yet.</param>
	/// <param name="Check">What is wrong with a row on its own; null when it is fine.</param>
	public sealed record TableSpec(
		TableShape Shape,
		IReadOnlyList<TableColumn> Columns,
		string Separator = ",",
		Func<IReadOnlyDictionary<string, string>, string?>? Example = null,
		Func<IReadOnlyDictionary<string, string>, string?>? Check = null)
	{
		/// <summary>A plain list: one column.</summary>
		public static readonly TableSpec OneColumn = new(TableShape.Values, [new("Value", "Entry")]);
	}

	/// <summary>One row while it is edited: its cells by field, and the entry it was read from.</summary>
	public sealed class TableRow
	{
		public TableRow(Dictionary<string, string> cells, JsonObject? original = null)
		{
			Cells = cells;
			Original = original;
		}

		public Dictionary<string, string> Cells { get; }

		/// <summary>The entry as read (Records): fields the table does not show are written back unchanged.</summary>
		public JsonObject? Original { get; }

		public string this[string field]
		{
			get => Cells.GetValueOrDefault(field, string.Empty);
			set => Cells[field] = value ?? string.Empty;
		}
	}

	/// <summary>
	/// Turns a list or map setting into table rows and back. A row left completely empty is not
	/// written. In a one-column list a cell <c>""</c> stands for an empty entry.
	/// </summary>
	public static class SettingTables
	{
		public const string EmptyEntry = "\"\"";

		/// <summary>Whether the value fits the table (else it stays a JSON field so nothing is lost).</summary>
		public static bool Fits(TableSpec spec, JsonNode? value)
		{
			switch (spec.Shape)
			{
				case TableShape.Values:
					return value is null || (value is JsonArray list && list.All(IsText));
				case TableShape.Records:
					var fields = spec.Columns.ToDictionary(c => c.Field, StringComparer.Ordinal);
					return value is null || (value is JsonArray records && records.All(e => e is JsonObject obj
						&& obj.All(p => !fields.TryGetValue(p.Key, out var column)
							|| (column.Kind == CellKind.Switch ? IsBool(p.Value) : IsText(p.Value)))));
				case TableShape.MapOfLists:
					return value is null || (value is JsonObject map && map.All(p => p.Value is JsonArray items && items.All(IsText)));
				default:
					return false;
			}
		}

		/// <summary>The rows of a value that <see cref="Fits"/>.</summary>
		public static List<TableRow> Read(TableSpec spec, JsonNode? value)
		{
			var rows = new List<TableRow>();
			switch (spec.Shape)
			{
				case TableShape.Values:
					foreach (var item in value as JsonArray ?? [])
					{
						var text = item?.GetValue<string>() ?? string.Empty;
						rows.Add(new TableRow(new() { [spec.Columns[0].Field] = text.Length == 0 ? EmptyEntry : text }));
					}

					break;

				case TableShape.Records:
					foreach (var item in value as JsonArray ?? [])
					{
						var obj = (JsonObject)item!;
						var cells = new Dictionary<string, string>(StringComparer.Ordinal);
						foreach (var column in spec.Columns)
						{
							cells[column.Field] = obj.TryGetPropertyValue(column.Field, out var cell) && cell is not null
								? (column.Kind == CellKind.Switch ? (cell.GetValue<bool>() ? "true" : "false") : cell.GetValue<string>())
								: column.NewValue;
						}

						rows.Add(new TableRow(cells, obj));
					}

					break;

				case TableShape.MapOfLists:
					var joint = spec.Separator == "\n" ? Environment.NewLine : spec.Separator + " ";
					foreach (var (key, items) in value as JsonObject ?? [])
					{
						var list = ((JsonArray)items!).Select(i => i?.GetValue<string>() ?? string.Empty);
						rows.Add(new TableRow(new()
						{
							[spec.Columns[0].Field] = key,
							[spec.Columns[1].Field] = string.Join(joint, list),
						}));
					}

					break;
			}

			return rows;
		}

		/// <summary>A new, empty row.</summary>
		public static TableRow NewRow(TableSpec spec) =>
			new(spec.Columns.ToDictionary(c => c.Field, c => c.NewValue, StringComparer.Ordinal));

		/// <summary>The value the rows stand for.</summary>
		public static JsonNode Write(TableSpec spec, IEnumerable<TableRow> rows)
		{
			switch (spec.Shape)
			{
				case TableShape.Values:
					var list = new JsonArray();
					foreach (var row in rows)
					{
						var text = row[spec.Columns[0].Field].Trim();
						if (text.Length > 0)
						{
							list.Add(text == EmptyEntry ? string.Empty : text);
						}
					}

					return list;

				case TableShape.Records:
					var records = new JsonArray();
					foreach (var row in rows.Where(r => !IsBlank(spec, r)))
					{
						var obj = row.Original?.DeepClone() as JsonObject ?? new JsonObject();
						foreach (var column in spec.Columns)
						{
							if (column.Optional && column.Kind != CellKind.Switch && row[column.Field].Trim().Length == 0)
							{
								obj.Remove(column.Field);
								continue;
							}

							obj[column.Field] = column.Kind == CellKind.Switch
								? JsonValue.Create(row[column.Field] == "true")
								: JsonValue.Create(row[column.Field].Trim());
						}

						records.Add(obj);
					}

					return records;

				default:
					var map = new JsonObject();
					foreach (var row in rows.Where(r => !IsBlank(spec, r)))
					{
						map[row[spec.Columns[0].Field].Trim()] = new JsonArray(SplitValues(spec, row[spec.Columns[1].Field])
							.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
					}

					return map;
			}
		}

		/// <summary>The values of a map row's values cell.</summary>
		public static IReadOnlyList<string> SplitValues(TableSpec spec, string cell) =>
			(cell ?? string.Empty).Split(spec.Separator == "\n" ? '\n' : spec.Separator[0])
				.Select(v => v.Trim().TrimEnd('\r').Trim())
				.Where(v => v.Length > 0)
				.ToList();

		/// <summary>What is wrong with each row (null when it is fine), in row order.</summary>
		public static IReadOnlyList<string?> Problems(TableSpec spec, IReadOnlyList<TableRow> rows)
		{
			var problems = new string?[rows.Count];
			for (int i = 0; i < rows.Count; i++)
			{
				if (!IsBlank(spec, rows[i]))
				{
					problems[i] = spec.Shape == TableShape.MapOfLists && rows[i][spec.Columns[0].Field].Trim().Length == 0
						? $"{spec.Columns[0].Header} is needed."
						: spec.Check?.Invoke(rows[i].Cells);
				}
			}

			var keyed = spec.Shape == TableShape.MapOfLists ? [spec.Columns[0]] : spec.Columns.Where(c => c.Unique);
			foreach (var column in keyed)
			{
				var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < rows.Count; i++)
				{
					var key = rows[i][column.Field].Trim();
					if (key.Length == 0 || IsBlank(spec, rows[i]))
					{
						continue;
					}

					if (seen.TryGetValue(key, out var first))
					{
						problems[i] ??= $"{column.Header} \"{key}\" is already in row {first + 1}.";
					}
					else
					{
						seen[key] = i;
					}
				}
			}

			return problems;
		}

		/// <summary>A row with nothing typed in it (switches do not count).</summary>
		public static bool IsBlank(TableSpec spec, TableRow row) =>
			spec.Columns.Where(c => c.Kind != CellKind.Switch).All(c => row[c.Field].Trim().Length == 0);

		private static bool IsText(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out _);

		private static bool IsBool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out _);
	}

	/// <summary>The tables of the settings that have them.</summary>
	public static class TableSpecs
	{
		public static readonly TableSpec ModalQueryParameters = new(
			TableShape.Records,
			[
				new("Param", "Query parameter", Width: 240, Unique: true),
				new("AppendSuffix", "Append to the address", Width: 200),
			],
			Example: row => row["Param"].Trim() is { Length: > 0 } param
				? $"/path/page.html?{param}=/path/overlay-content  →  follows /path/overlay-content{row["AppendSuffix"].Trim()}"
				: null,
			Check: row => row["Param"].Trim().Length == 0 ? "A query parameter is needed." : null);

		public static readonly TableSpec ComparisonPdfSetAside = new(
			TableShape.Records,
			[
				new("Name", "Name", Width: 180),
				new("Pattern", "Regular expression", Width: 320),
				new("Enabled", "Active", CellKind.Switch, Width: 80, NewValue: "true"),
				new("Comment", "Note"),
			],
			Check: row => row["Enabled"] != "true" ? null
				: CrawlerSuite.Core.Comparison.PdfSetAside.Problem(new ComparisonPattern { Name = row["Name"], Pattern = row["Pattern"] }));

		public static readonly TableSpec DownloadExclusions = new(
			TableShape.Records,
			[
				new("Value", "Address contains", Width: 320),
				new("Enabled", "Active", CellKind.Switch, Width: 80, NewValue: "true"),
				new("Comment", "Note"),
			],
			Check: row => row["Enabled"] == "true" && row["Value"].Trim().Length == 0
				? "An active entry needs a part of an address; empty would exclude every link."
				: null);

		public static readonly TableSpec SpecialInfoMappings = new(
			TableShape.Records,
			[
				new("Column", "Column", Width: 200),
				new("Pattern", "Pattern (* matches anything)", Width: 260),
				new("Label", "Label"),
			]);

		public static readonly TableSpec DictionaryBundles = new(
			TableShape.Records,
			[
				new("DisplayName", "Name", Width: 150),
				new("LanguageCode", "Language", Width: 90, Unique: true),
				new("Primary", "Primary", CellKind.Switch, Width: 80, NewValue: "false"),
				new("DicFile", ".dic file", CellKind.File, Width: 230),
				new("AffFile", ".aff file", CellKind.File, Width: 230),
				new("DicChecksum", ".dic checksum", Width: 150),
				new("AffChecksum", ".aff checksum", Width: 150),
				new("Comment", "Note"),
			],
			Check: row => row["LanguageCode"].Trim().Length == 0 ? "A language code is needed."
				: !Lexicon.LanguageTags.IsTag(row["LanguageCode"]) ? "Write the language as pages do in lang: de, en-GB, en-US."
				: null);

		public static readonly TableSpec ProperNames = new(
			TableShape.Records,
			[
				new("Name", "Name, as pages write it", Width: 420, Unique: true),
				new("Acronym", "Acronym (optional)", Width: 160, Unique: true, Optional: true),
			],
			Example: row => row["Name"].Trim() is { Length: > 0 } name
				? $"Words of “{name}” pass where the whole name stands"
					+ (row["Acronym"].Trim() is { Length: > 0 } acronym ? $"; {acronym} passes anywhere" : string.Empty)
				: null,
			Check: row => row["Name"].Trim().Length == 0 ? "A name is needed."
				: Config.AcronymProblem(row["Acronym"].Trim()));

		public static readonly TableSpec DoubledWordExceptions = new(
			TableShape.MapOfLists,
			[
				new("Key", "Language", Width: 120),
				new("Values", "Words whose doubling is correct, separated by commas"),
			]);

		public static readonly TableSpec PageLanguageOverrides = new(
			TableShape.MapOfLists,
			[
				new("Key", "Path starts with", Width: 360),
				new("Values", "Languages", CellKind.Languages),
			]);

		public static readonly TableSpec KnownChromeLanguageDefects = new(
			TableShape.MapOfLists,
			[
				new("Key", "Source path", Width: 360),
				new("Values", "Texts, one per line"),
			],
			Separator: "\n");
	}
}
