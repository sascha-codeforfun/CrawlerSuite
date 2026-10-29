using System.Text.RegularExpressions;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// What the ticket of a shared entry (<see cref="SharedEntries"/>) leads with: the point of the
	/// finding and its scale, read from the entry itself — where on the site it appears, the texts
	/// that repeat, a few pages to open — and the card's own sentences for what it is and how it is
	/// fixed. The pages themselves are only the appendix: one fix in the shared place fixes them all.
	/// </summary>
	/// <param name="Sections">The sections with the most pages, at most <see cref="SectionsShown"/>.</param>
	/// <param name="SectionCount">All sections, those not shown included.</param>
	/// <param name="Frequent">Texts found on at least <see cref="FrequentMin"/> pages, most pages first.</param>
	/// <param name="Examples">A few pages to open, from different sections, with what was found there.</param>
	/// <param name="ByHost">Every page, by host (scheme and host), as path and query.</param>
	public sealed record SharedSummary(
		int Pages,
		int Hosts,
		IReadOnlyList<(string Section, int Pages)> Sections,
		int SectionCount,
		IReadOnlyList<(string Text, int Pages)> Frequent,
		IReadOnlyList<(string Page, string Note)> Examples,
		IReadOnlyList<(string Host, IReadOnlyList<string> Paths)> ByHost)
	{
		public const int SectionsShown = 10;
		public const int FrequentShown = 10;
		public const int FrequentMin = 3;
		public const int ExamplesShown = 3;

		/// <summary>A path position whose one value covers this share of the pages says nothing about where: it is left out.</summary>
		internal const double Uniform = 0.9;

		// Plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex Times = new(@"\s×\d+$", RegexOptions.Compiled);
		private static readonly Regex SentenceEnd = new(@"(?<=[.!?])\s+(?=[\p{Lu}""(])", RegexOptions.Compiled);
		private static readonly Regex FixStart = new(@"(?:^|(?<=[.!?:]\s))Fix\b", RegexOptions.Compiled);
		private static readonly Regex DefaultNote = new(@"\s*(?:Rides along|Off|On) by default\b.*$", RegexOptions.Compiled | RegexOptions.Singleline);

		// The German cards name their fix with "Lösung" and their default with "Standardmäßig" (Text/de.json).
		private static readonly Regex FixStartGerman = new(@"(?:^|(?<=[.!?:]\s))Lösung\b", RegexOptions.Compiled);
		private static readonly Regex DefaultNoteGerman = new(@"\s*Standardmäßig\b.*$", RegexOptions.Compiled | RegexOptions.Singleline);

		/// <summary>The summary of a shared entry; its pages and the notes per page come from the item.</summary>
		public static SharedSummary Of(TriageItem item)
		{
			var pages = (item.Pages ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			var notes = item.PageNotes ?? new Dictionary<string, string>();
			var parsed = pages.Select(p => (Page: p, Uri: Uri.TryCreate(p, UriKind.Absolute, out var u) ? u : null)).ToList();

			var hosts = parsed.GroupBy(p => p.Uri is null ? string.Empty : p.Uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase)
				.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
			var mainHost = hosts.Count > 0 ? hosts[0].Key : string.Empty;
			var byHost = hosts
				.Select(g => (g.Key, (IReadOnlyList<string>)g.Select(p => p.Uri?.PathAndQuery ?? p.Page).Order(StringComparer.OrdinalIgnoreCase).ToList()))
				.ToList();

			var sectionOf = SectionsOf(parsed.Select(p => p.Uri).ToList(), mainHost);
			var sections = parsed.Select((p, i) => (p.Page, Section: sectionOf[i])).ToList();
			var grouped = sections.GroupBy(s => s.Section, StringComparer.OrdinalIgnoreCase)
				.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();

			var frequent = pages
				.SelectMany(p => TextsOf(notes.GetValueOrDefault(p)).Select(t => (Text: t, Page: p)))
				.GroupBy(t => t.Text, StringComparer.Ordinal)
				.Select(g => (Text: g.Key, Pages: g.Select(t => t.Page).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
				.Where(t => t.Pages >= FrequentMin)
				.OrderByDescending(t => t.Pages).ThenBy(t => t.Text, StringComparer.Ordinal)
				.Take(FrequentShown)
				.ToList();

			// One page from each of the largest sections, one with a note where there is one.
			var examples = new List<(string, string)>();
			foreach (var g in grouped)
			{
				if (examples.Count == ExamplesShown)
				{
					break;
				}

				var inSection = g.Select(s => s.Page).Order(StringComparer.OrdinalIgnoreCase).ToList();
				var page = inSection.FirstOrDefault(p => notes.GetValueOrDefault(p) is { Length: > 0 }) ?? inSection[0];
				examples.Add((page, notes.GetValueOrDefault(page) ?? string.Empty));
			}

			foreach (var page in pages.Order(StringComparer.OrdinalIgnoreCase))
			{
				if (examples.Count == ExamplesShown)
				{
					break;
				}

				if (!examples.Any(e => e.Item1.Equals(page, StringComparison.OrdinalIgnoreCase)))
				{
					examples.Add((page, notes.GetValueOrDefault(page) ?? string.Empty));
				}
			}

			return new SharedSummary(
				pages.Count,
				hosts.Count,
				grouped.Take(SectionsShown).Select(g => (g.Key, g.Count())).ToList(),
				grouped.Count,
				frequent,
				examples,
				byHost);
		}

		/// <summary>
		/// The section of each page: the first two folders of its path, leaving out a folder every page
		/// shares (a position where one value covers <see cref="Uniform"/> of the pages that have it).
		/// A page on another host than most is named with its host. "/" for a page at the top.
		/// </summary>
		internal static IReadOnlyList<string> SectionsOf(IReadOnlyList<Uri?> pages, string mainHost)
		{
			static string[] Folders(Uri? u)
			{
				if (u is null)
				{
					return [];
				}

				var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
				return u.AbsolutePath.EndsWith('/') ? parts : parts.Take(Math.Max(0, parts.Length - 1)).ToArray();
			}

			var folders = pages.Select(Folders).ToList();
			int depth = folders.Count == 0 ? 0 : folders.Max(f => f.Length);
			var uniform = new HashSet<int>();
			for (int i = 0; i < depth; i++)
			{
				var values = folders.Where(f => f.Length > i).Select(f => f[i]).ToList();
				var top = values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Max(g => g.Count());
				if (top >= Uniform * values.Count)
				{
					uniform.Add(i);
				}
			}

			return pages.Select((u, n) =>
			{
				var kept = folders[n].Where((_, i) => !uniform.Contains(i)).Take(2).ToList();
				var section = kept.Count == 0 ? "/" : string.Join("/", kept);
				var host = u?.GetLeftPart(UriPartial.Authority) ?? string.Empty;
				return host.Length > 0 && !host.Equals(mainHost, StringComparison.OrdinalIgnoreCase) ? $"{u!.Host} › {section}" : section;
			}).ToList();
		}

		/// <summary>The texts of a page note ("A ×2 · B"), counts aside.</summary>
		internal static IEnumerable<string> TextsOf(string? note) =>
			string.IsNullOrEmpty(note) ? []
				: note.Split(" · ").Select(t => Times.Replace(t.Trim(), string.Empty)).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal);

		/// <summary>What the finding is: the card's sentence after its verdict (the verdict itself when there is none).</summary>
		public static string WhatOf(string cardText, string verdict)
		{
			var rest = cardText.Length > verdict.Length ? cardText[verdict.Length..].TrimStart() : string.Empty;
			if (rest.Length == 0 || rest.StartsWith("Fix", StringComparison.Ordinal))
			{
				return verdict;
			}

			return SentenceEnd.Split(rest, 2)[0];
		}

		/// <summary>How it is fixed: the card from its "Fix" on, without a closing note on the default; null when the card names no fix.</summary>
		public static string? FixOf(string cardText, Text.DocumentLanguage language = Text.DocumentLanguage.English)
		{
			bool german = language == Text.DocumentLanguage.German;
			var match = (german ? FixStartGerman : FixStart).Match(cardText);
			if (!match.Success)
			{
				return null;
			}

			var fix = (german ? DefaultNoteGerman : DefaultNote).Replace(cardText[match.Index..], string.Empty).Trim();
			return fix.Length > 0 ? fix : null;
		}
	}
}
