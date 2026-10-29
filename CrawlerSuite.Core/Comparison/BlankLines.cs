namespace CrawlerSuite.Core.Comparison
{
	using System;
	using System.Text;

	/// <summary>
	/// The blank lines the comparison sets aside on pages compared as pruned: lines that are empty
	/// or hold only spaces and tabs, each with its line end. A boilerplate part the prune removes
	/// leaves its indentation and line breaks behind, so two pages with the same content can differ
	/// in nothing but those. Every other line is kept exactly as it is, its own line end included.
	/// </summary>
	public static class BlankLines
	{
		/// <summary>The text without its blank lines; the other lines joined by "\n", each keeping its own "\r".</summary>
		public static string Neutral(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return string.Empty;
			}

			var kept = new StringBuilder(text.Length);
			int start = 0;
			while (start <= text.Length)
			{
				int end = text.IndexOf('\n', start);
				int stop = end < 0 ? text.Length : end;
				if (!IsBlank(text, start, stop))
				{
					if (kept.Length > 0)
					{
						kept.Append('\n');
					}

					kept.Append(text, start, stop - start);
				}

				if (end < 0)
				{
					break;
				}

				start = end + 1;
			}

			return kept.ToString();
		}

		/// <summary>Whether the line from <paramref name="start"/> to <paramref name="stop"/> holds only spaces, tabs and a closing "\r".</summary>
		private static bool IsBlank(string text, int start, int stop)
		{
			for (int i = start; i < stop; i++)
			{
				char c = text[i];
				if (c != ' ' && c != '\t' && !(c == '\r' && i == stop - 1))
				{
					return false;
				}
			}

			return true;
		}
	}
}
