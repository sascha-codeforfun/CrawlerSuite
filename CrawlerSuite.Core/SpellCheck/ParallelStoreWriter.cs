namespace CrawlerSuite.Core.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Text;

	/// <summary>
	/// Serializes the <see cref="WordTicket"/>s into the three text views of the spell findings
	/// (logs 11, 12 and 13).
	///
	///   * SourcesView  — log 12, one line per page: "{url}|{word} ({lang})[ ({metaLabel})]|…", one
	///                    entry per DISTINCT word per page, meta-label suffix only for meta-sourced
	///                    words.
	///
	///   * UniqueView   — log 11, globally unique "{word} ({lang})" lines, keyed by word+lang (the
	///                    same word flagged in two languages is listed under each), sorted.
	///
	///   * LocatedView  — log 13, per (word, url), EVERY occurrence with its source label and
	///                    excerpt: where exactly each occurrence is.
	///
	/// Only meta sources carry a label suffix in SourcesView (text-node and ordinary attribute
	/// occurrences carry none).
	/// </summary>
	public static class ParallelStoreWriter
	{
		/// <summary>Log 12: per page, distinct words, meta-labelled only.</summary>
		public static string SourcesView(IEnumerable<WordTicket> tickets)
		{
			var sb = new StringBuilder();
			foreach (var t in tickets)
			{
				sb.Append(t.Url);

				// Distinct words already (one ticket per word+url). Within a ticket, surface a
				// meta label iff the word's occurrences include a meta source.
				string langs = string.IsNullOrEmpty(t.Languages) ? string.Empty : t.Languages;
				sb.Append('|').Append(t.Word).Append(" (").Append(langs).Append(')');

				var metaOccurrence = t.Occurrences.FirstOrDefault(o => o.Source == RunSource.Meta);
				if (metaOccurrence != null)
				{
					sb.Append(" (").Append(metaOccurrence.SourcePath).Append(')');
				}

				sb.Append('\n');
			}

			return sb.ToString();
		}

		/// <summary>Separates a word's line from the languages it is valid in ("Meeting (de) · valid en-GB, en-US").</summary>
		public const string ValidInSeparator = " · valid ";

		/// <summary>A log 11 line split into "word (langs)" and the languages it is valid in (empty when none).</summary>
		public static (string Line, IReadOnlyList<string> ValidIn) SplitValidIn(string line)
		{
			int at = line.LastIndexOf(ValidInSeparator, StringComparison.Ordinal);
			if (at < 0 || !line.AsSpan(0, at).EndsWith(")", StringComparison.Ordinal))
			{
				return (line, []);
			}

			var valid = line[(at + ValidInSeparator.Length)..]
				.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			return (line[..at], valid);
		}

		/// <summary>
		/// Globally unique word+lang, sorted. With <paramref name="validIn"/>, a word some other
		/// reference dictionary knows gets " · valid en-GB, en-US" (information for the reader;
		/// the word stays flagged).
		/// </summary>
		public static string UniqueView(IEnumerable<WordTicket> tickets, Func<string, string, IReadOnlyList<string>>? validIn = null)
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var lines = new List<string>();
			foreach (var t in tickets)
			{
				string line = $"{t.Word} ({t.Languages})";
				if (validIn?.Invoke(t.Word, t.Languages) is { Count: > 0 } valid)
				{
					line += ValidInSeparator + string.Join(", ", valid);
				}

				if (seen.Add(line))
				{
					lines.Add(line);
				}
			}

			lines.Sort(WordListOrder.Comparer);
			return string.Join("\n", lines) + (lines.Count > 0 ? "\n" : string.Empty);
		}

		/// <summary>Log 13: every occurrence with label + excerpt.</summary>
		public static string LocatedView(IEnumerable<WordTicket> tickets)
		{
			var sb = new StringBuilder();
			foreach (var t in tickets)
			{
				sb.Append(t.Word).Append(" (").Append(t.Languages).Append(") @ ").Append(t.Url).Append('\n');
				foreach (var o in t.Occurrences)
				{
					sb.Append("    ").Append(o.SourcePath).Append("  ").Append(o.Excerpt).Append('\n');
				}
			}

			return sb.ToString();
		}
	}
}
