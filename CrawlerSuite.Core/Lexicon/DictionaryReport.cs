namespace CrawlerSuite.Core.Lexicon
{
	using System.Text;

	/// <summary>Which of the operator's dictionaries an entry is in.</summary>
	public enum DictionaryKind
	{
		User,
		Site,

		/// <summary>The same entry in both lists.</summary>
		Both,
	}

	/// <summary>Why an entry is offered for removal.</summary>
	public enum CleanupReason
	{
		/// <summary>No page of the run asked for the word (log 15) — the system dictionary handles it, or no page has it.</summary>
		NotUsed,

		/// <summary>Covered by Known word parts (made only of them, or one plus a dictionary word) or by the acronym rule (log 15).</summary>
		Redundant,

		/// <summary>Pages asked for it, but the app accepts the word without the entry (log 35, from the second pass).</summary>
		NotNeeded,
	}

	/// <summary>One entry the Dictionaries page offers.</summary>
	public sealed record CleanupEntry(string Word, DictionaryKind Dictionary, CleanupReason Reason);

	/// <summary>
	/// Reads and writes the dictionary clean-up reports a snapshot holds: log 15 (not used,
	/// redundant) and log 35 (not needed). Both
	/// are sections of one word per line under a "// " header naming the dictionary and the reason.
	/// </summary>
	public static class DictionaryReport
	{
		/// <summary>Log 15 of a snapshot: not used and redundant entries.</summary>
		public static string ReportPath(string snapshot) => Path.Combine(snapshot, LogFileNames.UserDictionaryOrphanWords);

		/// <summary>Log 35 of a snapshot: entries no longer needed.</summary>
		public static string NotNeededPath(string snapshot) => Path.Combine(snapshot, LogFileNames.DictionaryNotNeeded);

		/// <summary>The entries of a log-15 or log-35 file; empty when it does not exist.</summary>
		public static IReadOnlyList<CleanupEntry> Read(string path)
		{
			var entries = new List<CleanupEntry>();
			if (!File.Exists(path))
			{
				return entries;
			}

			DictionaryKind? kind = null;
			CleanupReason reason = CleanupReason.NotUsed;
			foreach (var raw in File.ReadLines(path, Encoding.UTF8))
			{
				var line = raw.Trim().TrimStart('﻿');
				if (line.Length == 0)
				{
					continue;
				}

				if (line.StartsWith("//", StringComparison.Ordinal))
				{
					var header = line[2..].Trim();
					kind = header.StartsWith("User dictionary", StringComparison.OrdinalIgnoreCase) ? DictionaryKind.User
						: header.StartsWith("Site dictionary", StringComparison.OrdinalIgnoreCase) ? DictionaryKind.Site
						: header.StartsWith("In BOTH", StringComparison.OrdinalIgnoreCase) ? DictionaryKind.Both
						: null;
					reason = header.Contains("redundant", StringComparison.OrdinalIgnoreCase) ? CleanupReason.Redundant
						: header.Contains("not needed", StringComparison.OrdinalIgnoreCase) ? CleanupReason.NotNeeded
						: CleanupReason.NotUsed;
					continue;
				}

				if (kind is { } k)
				{
					entries.Add(new CleanupEntry(line, k, reason));
				}
			}

			return entries;
		}

		/// <summary>
		/// Writes log 35: the entries pages asked for that the app no longer needs, per dictionary.
		/// The first line records when and how it was found, so the page can say how current it is.
		/// </summary>
		public static void WriteNotNeeded(string path, string userPath, IEnumerable<string> user, string sitePath, IEnumerable<string> site, DateTime when)
		{
			var userList = user.ToList();
			var siteList = site.ToList();
			var both = new HashSet<string>(userList, StringComparer.Ordinal);
			both.IntersectWith(siteList);

			using var w = new StreamWriter(path, false, Encoding.UTF8) { NewLine = FileFormat.NewLine };
			w.WriteLine($"# 35 — dictionary entries no longer needed · found {when.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} by a second spell-check pass without the user and site dictionaries");
			bool anything = false;
			void Section(string header, IEnumerable<string> words)
			{
				var list = words.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
				if (list.Count == 0)
				{
					return;
				}

				if (anything)
				{
					w.WriteLine();
				}

				w.WriteLine($"// {header}");
				foreach (var word in list)
				{
					w.WriteLine(word);
				}

				anything = true;
			}

			Section($"User dictionary ({Path.GetFileName(userPath)}) — not needed: the app accepts these words without the entry", userList.Except(both));
			Section($"Site dictionary ({Path.GetFileName(sitePath)}) — not needed: the app accepts these words without the entry", siteList.Except(both));
			Section("In BOTH dictionaries — not needed: the app accepts these words without the entry", both);
		}

		/// <summary>When log 35 was written ("found yyyy-MM-dd HH:mm" in its first line); null when unknown.</summary>
		public static DateTime? FoundAt(string path)
		{
			if (!File.Exists(path))
			{
				return null;
			}

			var first = File.ReadLines(path, Encoding.UTF8).FirstOrDefault() ?? string.Empty;
			int at = first.IndexOf("found ", StringComparison.Ordinal);
			return at >= 0 && first.Length >= at + 6 + 16
				&& DateTime.TryParseExact(first.Substring(at + 6, 16), "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture,
					System.Globalization.DateTimeStyles.None, out var when)
				? when
				: null;
		}

		/// <summary>
		/// The entries of <paramref name="dictionaryPath"/> that pages asked for (not pinned, not
		/// orphaned) and whose word the pass without the lists did not flag. <paramref name="missed"/>
		/// holds that pass's flagged words, lowercased, with the parts of hyphenated words and the
		/// stems of compound heads added — so an entry still needed as a word part is kept.
		/// </summary>
		public static IReadOnlyList<string> NotNeeded(string dictionaryPath, IReadOnlySet<string> orphaned, IReadOnlySet<string> missed)
		{
			if (string.IsNullOrEmpty(dictionaryPath) || !File.Exists(dictionaryPath))
			{
				return [];
			}

			return File.ReadAllLines(dictionaryPath, Encoding.UTF8)
				.Select(l => l.Trim().TrimStart('﻿'))
				.Where(l => l.Length > 0 && !l.StartsWith('!') && !l.StartsWith("//", StringComparison.Ordinal) && !l.Contains('/') && !l.All(char.IsDigit))
				// A phrase with a space ("Xyzzy- und Hausverband") is a name for the hyphen check,
				// not a word the spell check asks for: a spell pass cannot judge it.
				.Where(l => !l.Any(char.IsWhiteSpace))
				.Distinct(StringComparer.Ordinal)
				.Where(w => !orphaned.Contains(w) && !missed.Contains(w.ToLowerInvariant()))
				.ToList();
		}

		/// <summary>
		/// The words a pass flagged, from its unique-word logs (11, 32: "word (langs)" or "word"
		/// per line) and inline-script findings (29: word first, tab-separated): lowercased, plus
		/// the parts of hyphenated words and the stems of compound heads ("Schul-" → "schul").
		/// </summary>
		public static HashSet<string> MissedWords(IEnumerable<string> uniqueLogs, IEnumerable<string> tabbedLogs)
		{
			var missed = new HashSet<string>(StringComparer.Ordinal);
			void Add(string word)
			{
				word = word.Trim();
				if (word.Length == 0)
				{
					return;
				}

				var lower = word.ToLowerInvariant();
				missed.Add(lower);
				if (lower.EndsWith('-'))
				{
					missed.Add(lower.TrimEnd('-'));
				}

				foreach (var part in lower.Split('-', StringSplitOptions.RemoveEmptyEntries))
				{
					missed.Add(part);
				}
			}

			foreach (var path in uniqueLogs.Where(File.Exists))
			{
				foreach (var raw in File.ReadLines(path, Encoding.UTF8))
				{
					var line = raw.Trim().TrimStart('﻿');
					if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
					{
						continue;
					}

					int paren = line.IndexOf(" (", StringComparison.Ordinal);
					Add(paren > 0 ? line[..paren] : line);
				}
			}

			foreach (var path in tabbedLogs.Where(File.Exists))
			{
				foreach (var raw in File.ReadLines(path, Encoding.UTF8))
				{
					var line = raw.TrimStart('﻿');
					var trimmed = line.TrimStart();
					if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("(no findings)", StringComparison.Ordinal))
					{
						continue;
					}

					Add(line.Split('\t')[0]);
				}
			}

			return missed;
		}
	}
}

namespace CrawlerSuite.Core.Lexicon
{
	using System.Text;

	/// <summary>
	/// The Dictionaries and Spell findings pages' edits: removes an entry, pins it ("!word", never
	/// offered again) or adds one, one click at a time, in place — every other line, comment and the order stay as they are.
	/// Before the first change to a file in this run of the app, a numbered backup is made next
	/// to it (<see cref="Audit.BackupDictionary"/>).
	/// </summary>
	public static class DictionaryEditor
	{
		private static readonly HashSet<string> BackedUp = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Removes the entry <paramref name="word"/> (exactly as written, pinned or not); true when a line was removed.</summary>
		public static bool Remove(string path, string word) =>
			Change(path, word, _ => null);

		/// <summary>Pins the entry <paramref name="word"/>; true when it was changed.</summary>
		public static bool Pin(string path, string word) =>
			Change(path, word, _ => "!" + word);

		/// <summary>
		/// Adds <paramref name="word"/> as a plain entry at the end of the file (created when it does
		/// not exist yet); false when the file already has it, plain or pinned. The lists match
		/// without regard to case, so an entry differing only in case counts as there.
		/// </summary>
		public static bool Add(string path, string word)
		{
			word = word.Trim();
			if (string.IsNullOrEmpty(path) || word.Length == 0)
			{
				return false;
			}

			var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : [];
			if (lines.Select(l => l.Trim().TrimStart('\uFEFF').TrimStart('!'))
				.Any(l => string.Equals(l, word, StringComparison.OrdinalIgnoreCase)))
			{
				return false;
			}

			BackUpOnce(path);
			lines.Add(word);
			File.WriteAllLines(path, lines, Encoding.UTF8);
			Logger.LogInfo($"Dictionary {Path.GetFileName(path)}: added an entry.");
			return true;
		}

		private static void BackUpOnce(string path)
		{
			lock (BackedUp)
			{
				if (BackedUp.Add(Path.GetFullPath(path)) && File.Exists(path))
				{
					Audit.BackupDictionary(path);
				}
			}
		}

		private static bool Change(string path, string word, Func<string, string?> replace)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path) || string.IsNullOrWhiteSpace(word))
			{
				return false;
			}

			var lines = File.ReadAllLines(path, Encoding.UTF8);
			var result = new List<string>(lines.Length);
			bool changed = false;
			foreach (var line in lines)
			{
				if (!changed && string.Equals(line.Trim().TrimStart('﻿'), word, StringComparison.Ordinal))
				{
					var replacement = replace(line);
					if (replacement is not null)
					{
						result.Add(replacement);
					}

					changed = true;
					continue;
				}

				result.Add(line);
			}

			if (!changed)
			{
				return false;
			}

			BackUpOnce(path);
			File.WriteAllLines(path, result, Encoding.UTF8);
			Logger.LogInfo($"Dictionary {Path.GetFileName(path)}: {(result.Count < lines.Length ? "removed" : "pinned")} an entry.");
			return true;
		}
	}
}
