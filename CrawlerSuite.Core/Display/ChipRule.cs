namespace CrawlerSuite.Core.Display
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// The one rule every filter chip row of the app follows: a lit chip shows its items; the lit
	/// chips of a group add up; a group with none lit shows everything; while one is lit, the
	/// others are dimmed. Every row starts with none lit.
	/// </summary>
	public static class ChipRule
	{
		/// <summary>Whether a group lets an item with <paramref name="key"/> through (keys compared ignoring case).</summary>
		public static bool Lets(IEnumerable<(string Key, bool Lit)> group, string key)
		{
			bool anyLit = false;
			foreach (var (chipKey, lit) in group)
			{
				if (lit)
				{
					anyLit = true;
					if (string.Equals(chipKey, key, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}

			return !anyLit;
		}

		/// <summary>Whether a chip is dimmed: not lit while another of its group is.</summary>
		public static bool Dimmed(IEnumerable<bool> groupLit, bool lit) => !lit && groupLit.Any(l => l);
	}
}
