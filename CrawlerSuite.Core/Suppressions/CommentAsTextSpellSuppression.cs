namespace CrawlerSuite.Core.Suppressions
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.RegularExpressions;
	using CrawlerSuite.Core.Quality;
	using CrawlerSuite.Core.SpellCheck;

	/// <summary>
	/// Cross-pass dedup (content quality → spelling): the words of a comment the page shows as text.
	/// COMMENT_AS_TEXT already reports the note with its cause ("the “!” is missing"); its words
	/// reported again as misspellings would only send an editor looking for typos in a note that has
	/// to go. Built from the content-quality findings after operator suppressions, so a note an
	/// operator chose to hide there is not hidden here either, and its words still surface.
	/// Keyed by the hashed filename, as both passes are. Only the words inside a reported note
	/// are muted; the same word elsewhere on the page is a word like any other only in so far as
	/// it is not also inside the note — the granularity is the word per file, as for the other
	/// cross-pass suppressions.
	/// </summary>
	public sealed class CommentAsTextSpellSuppression
	{
		private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

		// The note as the finding quotes it: “…”.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex Quoted = new("“(?<note>.*?)”", RegexOptions.Compiled);

		private readonly Dictionary<string, HashSet<string>> byFile = new(StringComparer.Ordinal);

		internal CommentAsTextSpellSuppression(IEnumerable<QualityIssue>? issues)
		{
			foreach (var issue in issues ?? [])
			{
				if (issue is null || issue.IssueType != CommentAsText.IssueType || string.IsNullOrEmpty(issue.Filename))
				{
					continue;
				}

				var m = Quoted.Match(issue.Detail ?? string.Empty);
				if (!m.Success)
				{
					continue;
				}

				if (!byFile.TryGetValue(issue.Filename, out var words))
				{
					words = new HashSet<string>(StringComparer.Ordinal);
					byFile[issue.Filename] = words;
				}

				foreach (var token in SpellTokenizer.Tokenize(new TextRun(null, RunSource.TextNode, "comment-as-text", m.Groups["note"].Value)))
				{
					if (token.Text.Any(char.IsLetter))
					{
						words.Add(token.Text);
					}
				}
			}
		}

		/// <summary>The words inside the comments a file shows as text (empty when none).</summary>
		public IReadOnlySet<string> WordsForFile(string filename) =>
			filename is not null && byFile.TryGetValue(filename, out var set) ? set : Empty;

		/// <summary>Both sets as one, without copying when either is empty.</summary>
		public static IReadOnlySet<string>? Union(IReadOnlySet<string>? a, IReadOnlySet<string>? b) =>
			b is null || b.Count == 0 ? a
			: a is null || a.Count == 0 ? b
			: a.Concat(b).ToHashSet(StringComparer.Ordinal);
	}
}
