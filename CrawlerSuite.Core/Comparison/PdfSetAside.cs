namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.RegularExpressions;

	/// <summary>
	/// The site's own patterns the comparison sets aside in the text of PDFs (the setting
	/// <see cref="Config.ComparisonPdfSetAside"/>): what a generator prints anew on every
	/// regeneration — a date in a form number, a dated line — and the code cannot know. Each
	/// match in a page's content is replaced by a placeholder before the PDFs are compared; a PDF
	/// that is then the same is identical "but for configured patterns", counted per pattern.
	/// Patterns run without backtracking (<see cref="RegexOptions.NonBacktracking"/>): no pattern
	/// can take unbounded time, so none needs a time limit; lookarounds and back-references are
	/// therefore not available.
	/// </summary>
	public sealed class PdfSetAside
	{
		/// <summary>What stands in for a match.</summary>
		internal const string Placeholder = "\u0001";

		private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

		private PdfSetAside(IReadOnlyList<(string Name, Regex Pattern)> entries) => Entries = entries;

		/// <summary>The active patterns, by name, in the order configured.</summary>
		public IReadOnlyList<(string Name, Regex Pattern)> Entries { get; }

		/// <summary>The active, valid patterns of the setting; null when there are none (the comparison then sets nothing aside).</summary>
		public static PdfSetAside? From(IEnumerable<ComparisonPattern>? patterns)
		{
			var entries = new List<(string, Regex)>();
			foreach (var p in patterns ?? [])
			{
				if (p is null || !p.Enabled || Problem(p) is not null)
				{
					continue;
				}

				entries.Add((p.Name.Trim(), new Regex(p.Pattern, Options)));
			}

			return entries.Count == 0 ? null : new PdfSetAside(entries);
		}

		/// <summary>What is wrong with an active entry; null when nothing is.</summary>
		public static string? Problem(ComparisonPattern entry)
		{
			if (string.IsNullOrWhiteSpace(entry.Name))
			{
				return "An active pattern needs a name: the comparison counts its matches under it.";
			}

			return PatternProblem(entry.Pattern);
		}

		/// <summary>What is wrong with a pattern; null when it compiles.</summary>
		public static string? PatternProblem(string? pattern)
		{
			if (string.IsNullOrEmpty(pattern))
			{
				return "An active pattern needs a regular expression.";
			}

			try
			{
				var regex = new Regex(pattern, Options);
				return regex.IsMatch(string.Empty) ? "The pattern matches nothing at all (an empty text) — it would set aside everywhere; make it need at least one sign." : null;
			}
			catch (ArgumentException ex)
			{
				return "Not a valid regular expression: " + ex.Message;
			}
			catch (NotSupportedException ex)
			{
				return "Not supported here (patterns run without backtracking: no lookarounds, no back-references): " + ex.Message;
			}
		}

		/// <summary>The text with every match replaced by the placeholder, and the names of the patterns that matched.</summary>
		internal string Apply(string text, ISet<string> hits)
		{
			foreach (var (name, pattern) in Entries)
			{
				if (pattern.IsMatch(text))
				{
					hits.Add(name);
					text = pattern.Replace(text, Placeholder);
				}
			}

			return text;
		}

		/// <summary>The names, for the log.</summary>
		public override string ToString() => string.Join(", ", Entries.Select(e => e.Name));
	}
}
