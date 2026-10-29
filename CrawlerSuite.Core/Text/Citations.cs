using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CrawlerSuite.Core.Text
{
	/// <summary>
	/// The rules a card's text cites, where they stand: "UAX #14" is the Unicode report, "LB12a" its
	/// rule, "2.4.4" the WCAG success criterion's Understanding page, "Technique H58" the technique,
	/// "RFC 9110" the RFC. A card shows each citation as a link in place, so a reader who has never heard
	/// of the rule sees where it comes from. Cited rules are found in English and German text alike: the
	/// tokens are the same in both. A success criterion is linked only when it is listed here — a number
	/// alone could be anything — and the guard test makes sure every one a card cites is.
	/// </summary>
	public static class Citations
	{
		/// <summary>One piece of a text: plain, or a citation with the address it stands for.</summary>
		public readonly record struct Piece(string Text, string? Url);

		private const string Understanding = "https://www.w3.org/WAI/WCAG22/Understanding/";
		private const string Techniques = "https://www.w3.org/WAI/WCAG22/Techniques/";

		/// <summary>The WCAG success criteria cards cite, by number: the Understanding page's name.</summary>
		public static readonly IReadOnlyDictionary<string, string> SuccessCriteria = new Dictionary<string, string>
		{
			["1.3.1"] = "info-and-relationships",
			["2.4.2"] = "page-titled",
			["2.4.4"] = "link-purpose-in-context",
			["3.1.1"] = "language-of-page",
			["3.1.2"] = "language-of-parts",
			["4.1.1"] = "parsing",
			["4.1.2"] = "name-role-value",
		};

		// Each alternative is one kind of citation. A success criterion's number stands on its own
		// (not inside "v3.2.1" or "2.0.170"); a technique needs its word, since "H2" alone is a heading level.
		private static readonly Regex Cited = new(
			@"\bUAX #(?<uax>\d{1,3})\b" +
			@"|\bUTR #(?<utr>\d{1,3})\b" +
			@"|\b(?<lb>LB\d{1,2}[a-z]?)\b" +
			@"|\bRFC ?(?<rfc>\d{3,5})\b" +
			@"|\bCWE-(?<cwe>\d{1,4})\b" +
			@"|(?<![\w.])(?<sc>[1-4]\.\d\.\d{1,2})(?![\w]|\.\d)" +
			@"|\b(?:Technique|Technik|Failure|Fehlerbild) (?<tech>(?:ARIA|SCR|H|F|G|C)\d{1,3})\b",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		/// <summary>The citations in a text, with their addresses (null: a success criterion not listed in <see cref="SuccessCriteria"/>).</summary>
		public static IEnumerable<(string Token, string? Url)> In(string text)
		{
			foreach (Match match in Cited.Each(text ?? string.Empty))
			{
				yield return (match.Value, UrlOf(match));
			}
		}

		/// <summary>The address one citation stands for, or null when it is not known.</summary>
		public static string? UrlOf(string token)
		{
			var match = Cited.Match(token ?? string.Empty);
			return match.Success && match.Length == token!.Length ? UrlOf(match) : null;
		}

		/// <summary>The text in pieces, plain and cited, in order; joined they are the text again.</summary>
		public static IReadOnlyList<Piece> Split(string text)
		{
			text ??= string.Empty;
			var pieces = new List<Piece>();
			int at = 0;
			foreach (Match match in Cited.Each(text))
			{
				if (UrlOf(match) is not { } url)
				{
					continue;
				}

				if (match.Index > at)
				{
					pieces.Add(new Piece(text[at..match.Index], null));
				}

				pieces.Add(new Piece(match.Value, url));
				at = match.Index + match.Length;
			}

			if (at < text.Length || pieces.Count == 0)
			{
				pieces.Add(new Piece(text[at..], null));
			}

			return pieces;
		}

		private static string? UrlOf(Match match)
		{
			if (match.Groups["uax"].Success)
			{
				return $"https://www.unicode.org/reports/tr{match.Groups["uax"].Value}/";
			}

			if (match.Groups["utr"].Success)
			{
				return $"https://www.unicode.org/reports/tr{match.Groups["utr"].Value}/";
			}

			if (match.Groups["lb"].Success)
			{
				// Rules LBn are the line breaking algorithm's (UAX #14); each has an anchor of its name.
				return "https://www.unicode.org/reports/tr14/#" + match.Groups["lb"].Value;
			}

			if (match.Groups["rfc"].Success)
			{
				return "https://www.rfc-editor.org/rfc/rfc" + match.Groups["rfc"].Value;
			}

			if (match.Groups["cwe"].Success)
			{
				return $"https://cwe.mitre.org/data/definitions/{match.Groups["cwe"].Value}.html";
			}

			if (match.Groups["sc"].Success)
			{
				return SuccessCriteria.TryGetValue(match.Groups["sc"].Value, out var page) ? Understanding + page + ".html" : null;
			}

			var id = match.Groups["tech"].Value;
			var family = id.StartsWith("ARIA", System.StringComparison.Ordinal) ? "aria"
				: id.StartsWith("SCR", System.StringComparison.Ordinal) ? "client-side-script"
				: id[0] switch { 'H' => "html", 'F' => "failures", 'G' => "general", _ => "css" };
			return Techniques + family + "/" + id;
		}
	}
}
