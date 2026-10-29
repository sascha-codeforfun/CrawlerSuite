using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CrawlerSuite.Core.Text
{
	/// <summary>The language of the documents the app hands out (ticket documents); the app's screens stay English.</summary>
	public enum DocumentLanguage
	{
		English,
		German,
	}

	/// <summary>
	/// The texts of handed-out documents in their language. English is the key: every text the app
	/// writes into a document is written in English in the code, and the German table (Text/de.json,
	/// embedded) gives its German by that English text — a sentence, a name, a card, or a format with
	/// {0}-style places for the parts that come from the snapshot. A text the table lacks stays
	/// English; the tests fail on every such text, so none slips through.
	/// </summary>
	public static class DocumentText
	{
		/// <summary>The setting's values (Settings › Triage › Ticket language).</summary>
		public const string EnglishName = "English";
		public const string GermanName = "Deutsch";

		/// <summary>The embedded table's name in the assembly.</summary>
		internal const string GermanResource = "CrawlerSuite.Core.Text.de.json";

		private static readonly Lazy<IReadOnlyDictionary<string, string>> GermanTable = new(LoadGerman);

		/// <summary>English text → German text.</summary>
		public static IReadOnlyDictionary<string, string> German => GermanTable.Value;

		/// <summary>The language a setting value names; anything but German is English.</summary>
		public static DocumentLanguage Parse(string? setting) =>
			setting?.Trim().ToLowerInvariant() is "deutsch" or "german" or "de" ? DocumentLanguage.German : DocumentLanguage.English;

		/// <summary>The text in the language: the German of an English text, or the text itself.</summary>
		public static string T(DocumentLanguage language, string english) =>
			language == DocumentLanguage.German && German.TryGetValue(english, out var german) ? german : english;

		/// <summary>A format in the language, filled in: the parts that come from the snapshot stay as they are.</summary>
		public static string F(DocumentLanguage language, string englishFormat, params object?[] args) =>
			string.Format(CultureInfo.InvariantCulture, T(language, englishFormat), args);

		/// <summary>The text in every language, English first: for a sentence built once per language.</summary>
		public static IReadOnlyList<DocumentLanguage> All { get; } = [DocumentLanguage.English, DocumentLanguage.German];

		private static IReadOnlyDictionary<string, string> LoadGerman()
		{
			using var stream = typeof(DocumentText).Assembly.GetManifestResourceStream(GermanResource);
			if (stream is null)
			{
				Logger.LogWarning("German texts not found in the program: documents stay English.");
				return new Dictionary<string, string>(StringComparer.Ordinal);
			}

			var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
			return new Dictionary<string, string>(table, StringComparer.Ordinal);
		}
	}
}
