namespace CrawlerSuite.Core.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using CrawlerSuite.Core.Findings;
	using CrawlerSuite.Core.Lexicon;

	/// <summary>How the part-by-part check of a hyphenated word placed one part.</summary>
	public enum HyphenPartStatus
	{
		/// <summary>No dictionary accepts it: the part that keeps the word flagged.</summary>
		Unknown,

		/// <summary>An entry of "Known word parts" (may span several parts).</summary>
		KnownWordPart,

		/// <summary>A single capital letter ("U-Bahn").</summary>
		Capital,

		/// <summary>The page language's dictionary accepts it (the check does not say which tier).</summary>
		Dictionary,

		/// <summary>The page language's system dictionary.</summary>
		System,

		/// <summary>The user dictionary.</summary>
		User,

		/// <summary>The site dictionary.</summary>
		Site,

		/// <summary>The foreign-words list.</summary>
		Foreign,

		/// <summary>The English dictionary (a loan part: "Online-Shop").</summary>
		English,

		/// <summary>A compound head before a hyphen, with a linking element or as a bare stem.</summary>
		CompoundHead,

		/// <summary>An acronym (all capitals, up to the configured length): not checked.</summary>
		Acronym,
	}

	/// <summary>One part of a hyphenated word and how it was placed.</summary>
	public sealed record HyphenPart(string Text, HyphenPartStatus Status);

	/// <summary>
	/// Which parts keep a hyphenated finding flagged (log 36), so the Spell findings page can add
	/// just those parts to a dictionary — "Xyzzy", not the whole "Brand-Xyzzy-Haus".
	/// The explanation walks the parts exactly as the check does (<see cref="SpellChecker.WalkHyphenParts"/>)
	/// but asks the dictionaries without recording usage and does not stop at the first unknown part.
	/// </summary>
	public static class HyphenParts
	{
		private static readonly (HyphenPartStatus Status, string Token)[] Tokens =
		[
			(HyphenPartStatus.Unknown, "unknown"),
			(HyphenPartStatus.KnownWordPart, "known-part"),
			(HyphenPartStatus.Capital, "capital"),
			(HyphenPartStatus.Dictionary, "dictionary"),
			(HyphenPartStatus.System, "system"),
			(HyphenPartStatus.User, "user"),
			(HyphenPartStatus.Site, "site"),
			(HyphenPartStatus.Foreign, "foreign"),
			(HyphenPartStatus.English, "english"),
			(HyphenPartStatus.CompoundHead, "compound-head"),
			(HyphenPartStatus.Acronym, "acronym"),
		];

		/// <summary>The token a status is written as in log 36.</summary>
		public static string TokenOf(HyphenPartStatus status) => Tokens.First(t => t.Status == status).Token;

		/// <summary>The status a log-36 token stands for; null for an unknown token.</summary>
		public static HyphenPartStatus? StatusOf(string token)
		{
			foreach (var (status, text) in Tokens)
			{
				if (string.Equals(text, token, StringComparison.Ordinal))
				{
					return status;
				}
			}

			return null;
		}

		/// <summary>
		/// The parts of a hyphenated word as the check sees them for one page language, each with
		/// how it was placed; null when the word has no hyphenated shape (the check does not look
		/// at its parts). Records no dictionary usage.
		/// </summary>
		public static IReadOnlyList<HyphenPart>? Explain(
			string word,
			Bundle dictionary,
			Bundle? english,
			IReadOnlyList<string> knownParts,
			IReadOnlyList<string> fugenelemente,
			AcronymRule? acronyms = null)
		{
			if (!SpellChecker.HasHyphenatedShape(word))
			{
				return null;
			}

			var other = english is not null && !ReferenceEquals(english, dictionary) ? english : null;
			var parts = new List<HyphenPart>();
			SpellChecker.WalkHyphenParts(
				word,
				knownParts,
				(part, isLast) =>
				{
					if (part.Length == 1 && char.IsUpper(part[0]))
					{
						return HyphenPartStatus.Capital;
					}

					if (acronyms is not null && acronyms.Matches(part))
					{
						return HyphenPartStatus.Acronym;
					}

					switch (dictionary.AcceptedBy(part))
					{
						case Bundle.Tier.System: return HyphenPartStatus.System;
						case Bundle.Tier.User: return HyphenPartStatus.User;
						case Bundle.Tier.Site: return HyphenPartStatus.Site;
						case Bundle.Tier.Foreign: return HyphenPartStatus.Foreign;
					}

					if (other is not null && other.AcceptedBy(part) != Bundle.Tier.None)
					{
						return HyphenPartStatus.English;
					}

					return !isLast && SpellChecker.CheckTrailingHyphenStem(part + "-", w => dictionary.AcceptedBy(w) != Bundle.Tier.None, fugenelemente)
						? HyphenPartStatus.CompoundHead
						: HyphenPartStatus.Unknown;
				},
				(part, status) => parts.Add(new HyphenPart(part, status)),
				stopAtUnknown: false);
			return parts;
		}

		/// <summary>
		/// Writes log 36 for the page-text findings (log 11): one line per hyphenated word and
		/// language it was flagged in — the word, the language, then each part and how it was
		/// placed, all separated by tabs. Words without a hyphenated shape are not listed.
		/// </summary>
		/// <returns>The number of words listed.</returns>
		public static int WriteLog(
			string path,
			IReadOnlyList<SpellWord> findings,
			IReadOnlyDictionary<string, Bundle> bundles,
			IReadOnlyList<string> knownParts,
			IReadOnlyList<string> fugenelemente,
			AcronymRule? acronyms = null)
		{
			var english = ToolsSpellChecker.EnglishOf(bundles);
			var lines = new List<string>
			{
				"# 36 — hyphenated misspellings part by part: which parts keep each word flagged",
				"# columns: word <TAB> language <TAB> part <TAB> status [<TAB> part <TAB> status …]",
				"# status: unknown · known-part (Known word parts) · capital · acronym · system · user · site · foreign · english · compound-head",
			};
			int listed = 0;
			foreach (var finding in findings)
			{
				bool any = false;
				foreach (var language in finding.Languages)
				{
					if (!bundles.TryGetValue(language, out var bundle)
						|| Explain(finding.Word, bundle, english, knownParts, fugenelemente, acronyms) is not { } parts)
					{
						continue;
					}

					lines.Add(string.Join('\t', new[] { finding.Word, language }.Concat(parts.SelectMany(p => new[] { p.Text, TokenOf(p.Status) }))));
					any = true;
				}

				if (any)
				{
					listed++;
				}
			}

			FileFormat.WriteAllLines(path, lines);
			return listed;
		}

		/// <summary>
		/// Log 36 as read back: per word, the parts in order with, per part, the languages it is
		/// unknown in and how the other languages placed it. Empty when the log is missing.
		/// </summary>
		public static IReadOnlyDictionary<string, IReadOnlyList<PartFinding>> Read(string snapshot)
		{
			var path = Path.Combine(snapshot, LogFileNames.HyphenatedParts);
			var result = new Dictionary<string, List<PartFinding>>(StringComparer.Ordinal);
			if (!File.Exists(path))
			{
				return new Dictionary<string, IReadOnlyList<PartFinding>>();
			}

			foreach (var line in FindingFiles.Lines(path))
			{
				if (line.TrimStart().StartsWith('#'))
				{
					continue;
				}

				var cells = line.Split('\t');
				if (cells.Length < 4 || cells.Length % 2 != 0)
				{
					continue;
				}

				var word = cells[0];
				var language = cells[1];
				if (!result.TryGetValue(word, out var parts))
				{
					parts = [];
					result[word] = parts;
				}

				// Parts are matched by their text: the same word splits the same way in every
				// language, except where a known word part spans parts in one of them.
				for (int c = 2; c + 1 < cells.Length; c += 2)
				{
					var part = parts.FirstOrDefault(p => p.Text == cells[c]);
					if (part is null)
					{
						part = new PartFinding(cells[c]);
						parts.Add(part);
					}

					part.Add(language, StatusOf(cells[c + 1]) ?? HyphenPartStatus.Unknown);
				}
			}

			return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<PartFinding>)kv.Value, StringComparer.Ordinal);
		}
	}

	/// <summary>One part of a hyphenated finding across the languages it was flagged in.</summary>
	public sealed class PartFinding(string text)
	{
		private readonly List<(string Language, HyphenPartStatus Status)> placed = [];

		public string Text { get; } = text;

		/// <summary>How each language placed the part.</summary>
		public IReadOnlyList<(string Language, HyphenPartStatus Status)> Placed => placed;

		/// <summary>The part keeps the word flagged in at least one language: an entry for it is what is missing.</summary>
		public bool IsUnknown => placed.Any(p => p.Status == HyphenPartStatus.Unknown);

		/// <summary>"unknown in de" · "known word part" · "system dictionary (de), English (en)".</summary>
		public string Summary
		{
			get
			{
				var groups = placed.GroupBy(p => p.Status).Select(g => (Status: g.Key, Languages: g.Select(x => x.Language).ToList())).ToList();
				if (groups.Count == 1 && groups[0].Status is HyphenPartStatus.KnownWordPart or HyphenPartStatus.Capital or HyphenPartStatus.Acronym)
				{
					return Describe(groups[0].Status);
				}

				return string.Join(", ", groups.Select(g => g.Status == HyphenPartStatus.Unknown
					? $"unknown in {string.Join(", ", g.Languages)}"
					: $"{Describe(g.Status)} ({string.Join(", ", g.Languages)})"));
			}
		}

		internal void Add(string language, HyphenPartStatus status) => placed.Add((language, status));

		private static string Describe(HyphenPartStatus status) => status switch
		{
			HyphenPartStatus.KnownWordPart => "known word part",
			HyphenPartStatus.Capital => "single capital",
			HyphenPartStatus.System => "system dictionary",
			HyphenPartStatus.User => "user dictionary",
			HyphenPartStatus.Site => "site dictionary",
			HyphenPartStatus.Foreign => "foreign words",
			HyphenPartStatus.English => "English",
			HyphenPartStatus.CompoundHead => "compound head",
			HyphenPartStatus.Acronym => "acronym, not checked",
			HyphenPartStatus.Dictionary => "dictionary",
			_ => "unknown",
		};
	}
}
