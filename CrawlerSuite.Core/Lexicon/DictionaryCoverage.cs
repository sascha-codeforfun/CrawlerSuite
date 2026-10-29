using System.Text;
using CrawlerSuite.Core.SpellCheck;

namespace CrawlerSuite.Core.Lexicon
{
	/// <summary>
	/// Which flagged words the user and site dictionaries hold by now — added after the run, on the
	/// Spell findings page or by hand. Such a word is decided: Spell findings shows it so, and
	/// triage leaves it out.
	/// </summary>
	public static class DictionaryCoverage
	{
		/// <summary>The entries of a dictionary file (plain or pinned with '!', regardless of case); empty when there is none.</summary>
		public static HashSet<string> EntriesOf(string path)
		{
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
			{
				foreach (var line in File.ReadLines(path, Encoding.UTF8))
				{
					var entry = line.Trim().TrimStart('﻿').TrimStart('!');
					if (entry.Length > 0)
					{
						set.Add(entry);
					}
				}
			}

			return set;
		}

		/// <summary>
		/// True when a dictionary holds the word, or — for a hyphenated word — every part that kept
		/// it flagged.
		/// </summary>
		public static bool Covers(string word, IReadOnlyList<PartFinding> parts, IReadOnlySet<string> inUser, IReadOnlySet<string> inSite)
		{
			if (inUser.Contains(word) || inSite.Contains(word))
			{
				return true;
			}

			var missing = parts.Where(p => p.IsUnknown).Select(p => p.Text).ToList();
			return missing.Count > 0 && missing.All(p => inUser.Contains(p) || inSite.Contains(p));
		}

		/// <summary>The test for a snapshot's words against the dictionaries as they are now. Touches the disk.</summary>
		public static Func<string, bool> For(string snapshot, string userPath, string sitePath)
		{
			var user = EntriesOf(userPath);
			var site = EntriesOf(sitePath);
			var parts = HyphenParts.Read(snapshot);
			return word => Covers(word, parts.TryGetValue(word, out var found) ? found : [], user, site);
		}
	}
}
