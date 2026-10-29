namespace CrawlerSuite.Core.Settings
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Text.Json.Nodes;

	/// <summary>Where a search text was found in a setting.</summary>
	/// <param name="Value">The first entry of the value that holds the text (shortened), or null when the value does not.</param>
	public sealed record SettingHit(bool InLabel, bool InKey, bool InHelp, string? Value)
	{
		public bool Any => InLabel || InKey || InHelp || Value is not null;
	}

	/// <summary>A piece of a text, and whether it is (part of) a search hit.</summary>
	public sealed record TextPiece(string Text, bool Hit);

	/// <summary>
	/// The settings screen's search: a plain text, ignoring case, looked for in a setting's name,
	/// key, help text and value (every string, number and switch of it, however deeply nested —
	/// list entries, table cells, chips, boilerplate parts).
	/// </summary>
	public static class SettingSearch
	{
		private const int ValueAround = 30;

		/// <summary>Where <paramref name="query"/> occurs in the setting; null for an empty query.</summary>
		public static SettingHit? Find(SettingInfo info, string key, string help, JsonNode? value, string? query)
		{
			var q = query?.Trim() ?? string.Empty;
			if (q.Length == 0)
			{
				return null;
			}

			string? inValue = null;
			foreach (var text in Strings(value))
			{
				int at = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
				if (at >= 0)
				{
					inValue = Around(text, at, q.Length);
					break;
				}
			}

			return new SettingHit(Contains(info.Label, q), Contains(key, q), Contains(help, q), inValue);
		}

		/// <summary>True when <paramref name="text"/> contains <paramref name="query"/>, ignoring case; false for an empty query.</summary>
		public static bool Contains(string? text, string? query)
		{
			var q = query?.Trim() ?? string.Empty;
			return q.Length > 0 && text is not null && text.Contains(q, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Every string, number and switch of a JSON value, depth first, in document order.</summary>
		public static IEnumerable<string> Strings(JsonNode? node)
		{
			switch (node)
			{
				case null:
					yield break;
				case JsonArray array:
					foreach (var item in array)
					{
						foreach (var s in Strings(item))
						{
							yield return s;
						}
					}

					break;
				case JsonObject obj:
					foreach (var (_, item) in obj)
					{
						foreach (var s in Strings(item))
						{
							yield return s;
						}
					}

					break;
				case JsonValue v when v.TryGetValue<string>(out var text):
					yield return text;
					break;
				case JsonValue v when v.TryGetValue<bool>(out var flag):
					yield return flag ? "true" : "false";
					break;
				case JsonValue v when v.TryGetValue<double>(out var number):
					yield return number.ToString(CultureInfo.InvariantCulture);
					break;
				default:
					yield return node.ToJsonString();
					break;
			}
		}

		/// <summary>The text cut into pieces at each occurrence of the query (ignoring case), the hits marked.</summary>
		public static IReadOnlyList<TextPiece> Split(string? text, string? query)
		{
			text ??= string.Empty;
			var q = query?.Trim() ?? string.Empty;
			if (q.Length == 0 || text.Length == 0)
			{
				return [new TextPiece(text, false)];
			}

			var pieces = new List<TextPiece>();
			int from = 0;
			while (from < text.Length)
			{
				int at = text.IndexOf(q, from, StringComparison.OrdinalIgnoreCase);
				if (at < 0)
				{
					break;
				}

				if (at > from)
				{
					pieces.Add(new TextPiece(text[from..at], false));
				}

				pieces.Add(new TextPiece(text.Substring(at, q.Length), true));
				from = at + q.Length;
			}

			if (from < text.Length)
			{
				pieces.Add(new TextPiece(text[from..], false));
			}

			return pieces;
		}

		private static string Around(string text, int at, int length)
		{
			int from = Math.Max(0, at - ValueAround);
			int to = Math.Min(text.Length, at + length + ValueAround);
			var cut = (from > 0 ? "…" : string.Empty) + text[from..to] + (to < text.Length ? "…" : string.Empty);
			return string.Join(' ', cut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		}
	}
}
