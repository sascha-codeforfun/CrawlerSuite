using System.Text.RegularExpressions;

namespace CrawlerSuite.Core
{
	/// <summary>
	/// Regex matches one at a time. A <see cref="MatchCollection"/> keeps every match it has
	/// produced, and reading its count or last item produces them all: over a file the site chose,
	/// the number of matches is the site's choice too, and a crafted file can take gigabytes that
	/// way. Walking with <see cref="Regex.Match(string)"/> and <see cref="Match.NextMatch"/>
	/// keeps only the match in hand (LINQ's LastOrDefault on it keeps only the last). Every match over
	/// site content goes through here.
	/// </summary>
	public static class RegexWalk
	{
		/// <summary>The matches in order, produced as the caller asks for them.</summary>
		public static IEnumerable<Match> Each(this Regex regex, string input)
		{
			for (var m = regex.Match(input); m.Success; m = m.NextMatch())
			{
				yield return m;
			}
		}

		/// <summary>The matches of a pattern in order, produced as the caller asks for them.</summary>
		public static IEnumerable<Match> Each(string input, string pattern, RegexOptions options = RegexOptions.None)
		{
			for (var m = Regex.Match(input, pattern, options); m.Success; m = m.NextMatch())
			{
				yield return m;
			}
		}

	}
}
