using System.Text.RegularExpressions;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// One defect repeated on several pages becomes one entry: decided once, filed once, with its
	/// pages listed — instead of an item on every page. Only exact repetition counts, the evidence
	/// that one source (a template, a component, a shared fragment) produced it: the same type, the
	/// same detail and the same context, numbers aside, on two or more pages. A styled heading is
	/// matched by its pattern (tag and heading class), whatever its texts: the template defect is how
	/// the component renders its titles, so one entry per pattern, each page listed with its texts —
	/// matching per text would make an entry of every title that happens to repeat (an accordion's
	/// box titles). A pattern on one page only stays with that page.
	/// </summary>
	public static class SharedEntries
	{
		/// <summary>Starts the entry of a shared defect; never a page address.</summary>
		public const string Prefix = "shared:";

		/// <summary>The filter chip and section of shared entries.</summary>
		public const string Label = "shared across pages";

		/// <summary>Pages named in the item before "… and N more"; the ticket names them all.</summary>
		public const int ListedInItem = 10;

		/// <summary>The title shown for a shared entry, at most this long.</summary>
		internal const int TitleMax = 140;

		// A visible separator (␟) that page text does not carry; the entry is never shown as is.
		private const char Part = '\u241F';

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex DigitsRegex = new(@"\d+", RegexOptions.Compiled);
		private static readonly Regex SpaceRegex = new(@"\s+", RegexOptions.Compiled);
		private static readonly Regex CountRegex = new(@" ×(?<n>\d+)$", RegexOptions.Compiled);

		public static bool IsShared(string entry) => entry.StartsWith(Prefix, StringComparison.Ordinal);

		/// <summary>The entry of a defect: its type and what identifies it.</summary>
		public static string EntryOf(string type, string first, string second) => $"{Prefix}{type}{Part}{first}{Part}{second}";

		/// <summary>"Styled headings — &lt;div class="x"&gt; · Water and ice": the defect's label and what identifies it; the entry itself when it is none.</summary>
		public static string TitleOf(string entry) => TitleOf(entry, Text.DocumentLanguage.English);

		/// <summary>The title in a document language: the defect's name translated, what identifies it as found.</summary>
		public static string TitleOf(string entry, Text.DocumentLanguage language)
		{
			if (!IsShared(entry))
			{
				return entry;
			}

			var parts = entry[Prefix.Length..].Split(Part);
			if (parts.Length < 3)
			{
				return entry;
			}

			var (type, first, second) = (parts[0], parts[1], parts[2]);
			var label = TriageSwitches.Describe(TriageSwitches.IdOf(type, first)).Label;
			var what = second.Length > 0 && type == Quality.StyledHeadings.IssueType ? $"{first} · {second}" : first;
			var title = $"{Text.DocumentText.T(language, label)} — {what}";
			return title.Length <= TitleMax ? title : title[..TitleMax] + "…";
		}

		/// <summary>Numbers neutralised, whitespace collapsed: what may differ between two copies of one defect.</summary>
		internal static string Normal(string text) => SpaceRegex.Replace(DigitsRegex.Replace(text.Trim(), "#"), " ");

		/// <summary>The types matched across pages: content quality, read from logs 10 and 22.</summary>
		private static bool Takes(TriageItem item) =>
			item.Keys.Count == 1
			&& item.Type is not ("ADJACENT_ANCHOR" or BrokenLinkSource.Type or SpellingSource.Type or SelfLinkSource.Type)
			&& !CanonicalSource.IsCanonical(item.Type)
			&& !ForwardingLinkSource.IsForwarding(item.Type)
			&& !IsShared(item.Page);

		/// <summary>
		/// The items with every defect repeated on two or more pages taken off those pages and
		/// gathered into one shared item each; everything else as it was, in its order.
		/// </summary>
		public static IReadOnlyList<TriageItem> Apply(IReadOnlyList<TriageItem> items)
		{
			// Styled headings: per pattern (tag + heading class), whatever the texts. Everything else:
			// the whole finding.
			var headingPages = new Dictionary<string, List<TriageItem>>(StringComparer.Ordinal);
			var findingPages = new Dictionary<(string Type, string Detail, string Context), List<TriageItem>>();
			foreach (var item in items.Where(Takes))
			{
				if (item.Type == Quality.StyledHeadings.IssueType)
				{
					if (!headingPages.TryGetValue(item.Detail, out var list))
					{
						headingPages[item.Detail] = list = [];
					}

					list.Add(item);
					continue;
				}

				var id = (item.Type, Normal(item.Detail), Normal(item.Context));
				if (!findingPages.TryGetValue(id, out var pages))
				{
					findingPages[id] = pages = [];
				}

				pages.Add(item);
			}

			static int PageCount(IEnumerable<TriageItem> of) => of.Select(i => i.Page).Distinct(StringComparer.OrdinalIgnoreCase).Count();

			var sharedHeadings = headingPages.Where(kv => PageCount(kv.Value) > 1).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
			var sharedFindings = findingPages.Where(kv => PageCount(kv.Value) > 1).ToDictionary(kv => kv.Key, kv => kv.Value);
			if (sharedHeadings.Count == 0 && sharedFindings.Count == 0)
			{
				return items;
			}

			var taken = new HashSet<TriageItem>(sharedFindings.Values.Concat(sharedHeadings.Values).SelectMany(v => v), ReferenceEqualityComparer.Instance);
			var result = new List<TriageItem>(items.Count);
			result.AddRange(items.Where(i => !taken.Contains(i)));

			foreach (var (pattern, occurrences) in sharedHeadings)
			{
				// The class attributes as the pages write them, gathered; each page with its texts.
				var asFound = occurrences.Select(o => HeadingTexts(o.Context).AsFound).OfType<string>()
					.SelectMany(a => a[Quality.StyledHeadings.AsFound.Length..].Split(" / "))
					.Distinct(StringComparer.Ordinal).ToList();
				var head = asFound.Count > 0 ? Quality.StyledHeadings.AsFound + string.Join(" / ", asFound) : string.Empty;
				var notes = occurrences
					.GroupBy(o => o.Page, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(g => g.Key, g => string.Join(" · ", g.SelectMany(o => HeadingTexts(o.Context).Texts).Select(Line).Distinct(StringComparer.Ordinal)),
						StringComparer.OrdinalIgnoreCase);
				var first = occurrences[0];
				result.Add(Shared(first, EntryOf(first.Type, pattern, string.Empty), KeyOf(first.Type, pattern, string.Empty), head,
					occurrences.Select(o => o.Page), notes));
			}

			foreach (var ((type, detail, context), occurrences) in sharedFindings)
			{
				var first = occurrences[0];
				result.Add(Shared(first, EntryOf(type, detail, context), KeyOf(type, detail, context), first.Context, occurrences.Select(o => o.Page), null));
			}

			return result;
		}

		/// <summary>The ledger key of a shared defect: what identifies it, never its pages — a page added later joins its verdict.</summary>
		public static string KeyOf(string type, string first, string second) => $"{type}|shared|{first}|{second}";

		private static TriageItem Shared(TriageItem first, string entry, string key, string context, IEnumerable<string> pageList,
			IReadOnlyDictionary<string, string>? notes)
		{
			var pages = pageList.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
			var lines = new List<string> { context, $"The same on {pages.Count} pages — one fix where they share it:" };
			lines.AddRange(pages.Take(ListedInItem).Select(p => "  " + p + (notes?.GetValueOrDefault(p) is { Length: > 0 } note ? " — " + note : string.Empty)));
			if (pages.Count > ListedInItem)
			{
				lines.Add($"  … and {pages.Count - ListedInItem} more (all in the ticket)");
			}

			return first with
			{
				ItemKey = key,
				Keys = [key],
				Page = entry,
				Context = string.Join("\n", lines.Where(l => l.Length > 0)),
				LocalFile = null,
				Pages = pages,
				PageNotes = notes,
				Found = context,
			};
		}

		private static string Line((string Text, int Count) t) => t.Count > 1 ? $"{t.Text} ×{t.Count}" : t.Text;

		/// <summary>A styled heading's lines: the "as found" line, if any, and each heading text with how often it occurs.</summary>
		internal static (string? AsFound, IReadOnlyList<(string Text, int Count)> Texts) HeadingTexts(string context)
		{
			string? asFound = null;
			var texts = new List<(string, int)>();
			foreach (var raw in context.Split('\n'))
			{
				var line = raw.Trim();
				if (line.Length == 0)
				{
					continue;
				}

				if (line.StartsWith(Quality.StyledHeadings.AsFound, StringComparison.Ordinal))
				{
					asFound = line;
					continue;
				}

				var m = CountRegex.Match(line);
				texts.Add(m.Success ? (line[..m.Index], int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)) : (line, 1));
			}

			return (asFound, texts);
		}
	}
}
