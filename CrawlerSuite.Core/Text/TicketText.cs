using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CrawlerSuite.Core.Text
{
	/// <summary>
	/// How a ticket document words what the engine recorded, for a reader who cannot open the page:
	/// the finding's line in words, a card's fix without its own "Fix" in front, excerpts of markup
	/// that begin and end on whole tags, and the engine's markers in the document's words.
	/// </summary>
	public static class TicketText
	{
		private static readonly Regex EmptyQuotation = new("„“|“”", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex QuotedElement = new(@"<[a-zA-Z][^<>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex NumericReference = new(@"&#(?:[xX](?<hex>[0-9a-fA-F]{1,4})|(?<dec>\d{1,5}));", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex LeadingFixWord = new(@"^(?:Fix|Lösung)\b[:,]?\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		/// <summary>
		/// The finding's line for a reader: an empty typographic quotation (a link without text) is
		/// <paramref name="noText"/>, and a character reference inside a quoted element
		/// ("id&amp;#61;de") is the character it stands for. Excerpts keep the markup as it is.
		/// </summary>
		public static string Readable(string line, string noText)
		{
			line = EmptyQuotation.Replace(line, noText);
			return QuotedElement.Replace(line, e => NumericReference.Replace(e.Value, r =>
			{
				bool hex = r.Groups["hex"].Success;
				bool parsed = int.TryParse(hex ? r.Groups["hex"].Value : r.Groups["dec"].Value,
					hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
				return parsed && code is > 0x1F and < 0xD800 ? ((char)code).ToString() : r.Value;
			}));
		}

		/// <summary>A card's fix under the "Fix" label: without the card's own "Fix:" / "Lösung," in front, which read twice.</summary>
		public static string WithoutFixWord(string fix)
		{
			var rest = LeadingFixWord.Replace(fix, string.Empty);
			return rest.Length == 0 ? fix : char.ToUpper(rest[0], CultureInfo.InvariantCulture) + rest[1..];
		}

		/// <summary>
		/// An excerpt of markup cut at a fixed width may begin or end inside a tag or a comment
		/// ("2F%2Fwww…" title="Weiter zum", "!--  --&gt;&lt;li"). The partial tag at either end is cut
		/// back to the nearest whole one (marked "…"), and a line that lies wholly inside one tag is
		/// left out when the excerpt has other lines. Text without tag syntax is untouched.
		/// </summary>
		public static string WholeTags(string context)
		{
			var lines = context.Split('\n');
			var kept = new List<string>();
			foreach (var raw in lines)
			{
				var line = raw;
				int open = line.IndexOf('<');
				int close = line.IndexOf('>');
				if (close >= 0 && (open < 0 || close < open) && LooksLikeTagPart(line[..close]))
				{
					line = "…" + line[(close + 1)..];
				}

				int lastOpen = line.LastIndexOf('<');
				int lastClose = line.LastIndexOf('>');
				if (lastOpen > lastClose && lastOpen > 0 && LooksLikeTagPart(line[lastOpen..]))
				{
					line = line[..lastOpen] + "…";
				}

				bool insideOneTag = open < 0 && close < 0 && line.Contains("=\"", StringComparison.Ordinal);
				if (insideOneTag && lines.Length > 1)
				{
					continue;
				}

				kept.Add(line);
			}

			return kept.Count == 0 ? context : string.Join("\n", kept);
		}

		// A piece of a tag or comment: attribute syntax, quotes or comment dashes, not prose.
		private static bool LooksLikeTagPart(string piece) =>
			piece.Contains('=') || piece.Contains('"') || piece.Contains("--", StringComparison.Ordinal);

		/// <summary>
		/// <paramref name="marker"/> (the empty-link marker, "[Missing link text]") replaced by
		/// <paramref name="words"/> in a context, the per-character marks moved along: the mark on
		/// the marker's first character covers the words.
		/// </summary>
		public static (string Text, T?[] Marks) MarkerInWords<T>(string text, T?[] marks, string marker, string words)
			where T : struct
		{
			if (!text.Contains(marker, StringComparison.Ordinal))
			{
				return (text, marks);
			}

			var sb = new StringBuilder(text.Length);
			var moved = new List<T?>(text.Length);
			for (int i = 0; i < text.Length;)
			{
				if (string.CompareOrdinal(text, i, marker, 0, marker.Length) == 0)
				{
					sb.Append(words);
					moved.AddRange(Enumerable.Repeat(i < marks.Length ? marks[i] : null, words.Length));
					i += marker.Length;
					continue;
				}

				sb.Append(text[i]);
				moved.Add(i < marks.Length ? marks[i] : null);
				i++;
			}

			return (sb.ToString(), moved.ToArray());
		}
	}
}
