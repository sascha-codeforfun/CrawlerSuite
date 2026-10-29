using System.Collections.Generic;
using System.Globalization;

namespace CrawlerSuite.Core.SpellCheck
{
	/// <summary>
	/// Order for word lists people read (logs 11 and 32): case-insensitive with the invariant
	/// language rules, so "apfel" and "Apfel" sit together. Where those rules call two words equal
	/// ("Abc" and "abc"), a character-by-character comparison decides, so the list comes out the same
	/// on every run and every machine, whatever order the words arrived in.
	/// </summary>
	internal static class WordListOrder
	{
		private static readonly StringComparer Reading =
			StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);

		public static readonly IComparer<string> Comparer = Comparer<string>.Create(static (a, b) =>
		{
			int c = Reading.Compare(a, b);
			return c != 0 ? c : string.CompareOrdinal(a, b);
		});
	}
}
