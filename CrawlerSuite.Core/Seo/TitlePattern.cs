namespace CrawlerSuite.Core.Seo
{
	/// <summary>
	/// The site's title patterns (Seo.TitleTemplates): each has one <c>{title}</c> where the page's
	/// own title goes; the text around it is what the system adds to every title.
	/// </summary>
	public static class TitlePattern
	{
		public const string Placeholder = "{title}";

		/// <summary>
		/// The page's own part of <paramref name="title"/>, the added text before it, and whether
		/// the title matches a pattern: the first pattern whose text before and after the
		/// placeholder the title carries exactly. No pattern: the whole title, matching. No match:
		/// the whole title, not matching.
		/// </summary>
		public static (string Own, string Before, bool Matches) Of(string title, IReadOnlyList<string>? patterns)
		{
			if (!Any(patterns))
			{
				return (title, string.Empty, true);
			}

			foreach (var pattern in patterns)
			{
				int at = string.IsNullOrEmpty(pattern) ? -1 : pattern.IndexOf(Placeholder, StringComparison.Ordinal);
				if (at < 0)
				{
					continue;
				}

				// The title is read with white space trimmed at its ends: so are the pattern's ends.
				var before = pattern[..at].TrimStart();
				var after = pattern[(at + Placeholder.Length)..].TrimEnd();
				if (title == (before + after).Trim())
				{
					return (string.Empty, before, true); // only the added text
				}

				if (title.Length >= before.Length + after.Length
					&& title.StartsWith(before, StringComparison.Ordinal)
					&& title.EndsWith(after, StringComparison.Ordinal))
				{
					return (title.Substring(before.Length, title.Length - before.Length - after.Length).Trim(), before, true);
				}
			}

			return (title, string.Empty, false);
		}

		/// <summary>Whether any pattern is set.</summary>
		public static bool Any([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] IReadOnlyList<string>? patterns) => patterns is not null && patterns.Any(p => !string.IsNullOrEmpty(p));
	}
}
