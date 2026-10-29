using System.Text.RegularExpressions;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.SpellCheck;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// A snapshot's spelling findings for triage: one item per flagged word per page (log 12),
	/// with its excerpts (log 13) and the other dictionaries that know it (log 11). Words the
	/// dictionaries hold by now are left out.
	/// </summary>
	public static class SpellingSource
	{
		/// <summary>The triage type, switch and ledger type of a spelling finding.</summary>
		public const string Type = "SPELLING";

		public const string Family = "Spelling";

		public const string Label = "Misspelled words";

		// "word (de) (meta[@name=description])" after the URL in log 12.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex WordLangsMeta = new(@"^(?<word>.*?) \((?<langs>[^()]*)\)(?: \((?<meta>.*)\))?$", RegexOptions.Compiled);

		// "word (de) @ https://…" — a block header in log 13.
		private static readonly Regex LocatedHeader = new(@"^(?<head>.* \([^()]*\)) @ (?<url>\S.*)$", RegexOptions.Compiled);

		/// <summary>
		/// Whether the snapshot holds the spell check's page list (log 12). Without it — spell check
		/// off or stopped — "no finding" means "not checked", so spelling verdicts must stay.
		/// </summary>
		public static bool HasLogs(string snapshotDirectory) =>
			File.Exists(Path.Combine(snapshotDirectory, LogFileNames.SpellErrorsSources));

		/// <summary>
		/// Spelling verdicts a match against this snapshot must keep: all of them when the snapshot
		/// has no spell-check logs (not checked is not fixed), none otherwise.
		/// </summary>
		public static IEnumerable<string> KeptWithoutLogs(string snapshotDirectory, TriageLedger ledger) =>
			HasLogs(snapshotDirectory)
				? []
				: ledger.Findings.Values.Where(f => f.Type == Type).Select(f => f.Key).ToList();

		/// <summary>One flagged word on one page.</summary>
		/// <param name="Detail">"word (de)": the ledger identity.</param>
		/// <param name="Context">The excerpts, one per line; the meta field when the word is in one.</param>
		/// <param name="ValidIn">Other reference dictionaries that know the word, for the operator only.</param>
		/// <param name="ContextDe">The same in German: the source labels are words of the document.</param>
		public sealed record Finding(string Page, string Word, string Detail, string Context, string ValidIn, string? ContextDe = null);

		/// <summary>The spelling findings of a snapshot. <paramref name="decided"/> names words a dictionary holds by now.</summary>
		public static IReadOnlyList<Finding> Read(string snapshotDirectory, Func<string, bool>? decided = null)
		{
			var sources = Path.Combine(snapshotDirectory, LogFileNames.SpellErrorsSources);
			if (!File.Exists(sources))
			{
				return [];
			}

			var excerpts = Excerpts(Path.Combine(snapshotDirectory, LogFileNames.SpellErrorsSourceLocation));
			var validIn = ValidIn(Path.Combine(snapshotDirectory, LogFileNames.SpellErrorsUnique));

			var order = new List<(string Page, string Detail)>();
			var found = new Dictionary<(string Page, string Detail), (string Word, List<string> Context)>();
			var metaOf = new Dictionary<(string Page, string Detail), string>();
			foreach (var line in FindingFiles.Lines(sources))
			{
				int bar = line.IndexOf('|');
				if (bar <= 0)
				{
					continue;
				}

				var page = line[..bar];
				var m = WordLangsMeta.Match(line[(bar + 1)..]);
				if (!m.Success)
				{
					continue;
				}

				var word = m.Groups["word"].Value;
				if (word.Length == 0 || decided?.Invoke(word) == true)
				{
					continue;
				}

				var detail = $"{word} ({m.Groups["langs"].Value})";
				var key = (page, detail);
				if (!found.TryGetValue(key, out var entry))
				{
					entry = (word, []);
					found[key] = entry;
					order.Add(key);
				}

				if (m.Groups["meta"].Success && m.Groups["meta"].Value.Length > 0)
				{
					metaOf.TryAdd(key, m.Groups["meta"].Value);
				}
			}

			return order.Select(key =>
			{
				var (word, _) = found[key];

				// Each occurrence where it stands, in words an editor can find: the excerpt of page text
				// as it is, any other source named ("Description: …", "alt of <img>: …", "In the page's
				// script: …"). A meta field log 12 names but log 13 has no excerpt of is named on its own.
				var occurrences = excerpts.TryGetValue(key, out var located) ? located.ToList() : [];
				if (metaOf.TryGetValue(key, out var meta) && !occurrences.Any(o => o.Source == meta))
				{
					occurrences.Add((meta, string.Empty));
				}
				string Lines(Text.DocumentLanguage language) => string.Join("\n",
					occurrences.Select(o => Shown(o.Source, o.Excerpt, language)).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal));

				return new Finding(key.Page, word, key.Detail, Lines(Text.DocumentLanguage.English),
					validIn.TryGetValue(key.Detail, out var valid) ? valid : string.Empty, Lines(Text.DocumentLanguage.German));
			}).ToList();
		}

		/// <summary>The word of a spelling detail ("word (de)" → "word").</summary>
		public static string WordOf(string detail)
		{
			int at = detail.LastIndexOf(" (", StringComparison.Ordinal);
			return at < 0 ? detail : detail[..at];
		}

		// "img[@alt]", "meta[@name=description]", "script[L12:34]", "p[#text]": where an occurrence stands.
		private static readonly Regex SourcePath = new(@"^(?<element>[\w-]+)\[(?:@(?<attribute>[\w:-]+)(?:=(?<name>[^\]]*))?|(?<script>L\d+:\d+)|#text)\]$", RegexOptions.Compiled);

		// \u00e4 and \x41 in a script's string: the letter the visitor reads.
		private static readonly Regex ScriptEscape = new(@"\\u(?<hex>[0-9a-fA-F]{4})|\\x(?<hex>[0-9a-fA-F]{2})", RegexOptions.Compiled);

		/// <summary>
		/// One occurrence as the ticket shows it: page text as it is; a meta value, an attribute or a
		/// script string with the place it stands ("Description: …", "alt of &lt;img&gt;: …",
		/// "In the page's script: …", escapes decoded); a source alone when there is no excerpt.
		/// </summary>
		internal static string Shown(string source, string excerpt, Text.DocumentLanguage language)
		{
			var m = SourcePath.Match(source);
			// A script body, or an inline event handler (onclick …): JavaScript either way.
			bool script = m.Success && (m.Groups["script"].Success || m.Groups["element"].Value == "script"
				|| m.Groups["attribute"].Value.StartsWith("on", StringComparison.OrdinalIgnoreCase));
			string? label = !m.Success ? null
				: script ? Text.DocumentText.T(language, "In the page's script")
				: m.Groups["element"].Value == "meta" && m.Groups["name"].Value is { Length: > 0 } name
					? name switch
					{
						"description" => Text.DocumentText.T(language, "Description"),
						"keywords" => Text.DocumentText.T(language, "Keywords"),
						_ => Text.DocumentText.F(language, "Meta tag {0}", name),
					}
				: m.Groups["attribute"].Success ? Text.DocumentText.F(language, "{0} of <{1}>", m.Groups["attribute"].Value, m.Groups["element"].Value)
				: null;

			if (script)
			{
				excerpt = ScriptEscape.Replace(excerpt, e => ((char)Convert.ToInt32(e.Groups["hex"].Value, 16)).ToString());
			}

			return label is null ? excerpt : excerpt.Length == 0 ? label : $"{label}: {excerpt}";
		}

		/// <summary>Log 13: "word (langs) @ url" then one indented "source  excerpt" line per occurrence.</summary>
		private static Dictionary<(string Page, string Detail), List<(string Source, string Excerpt)>> Excerpts(string path)
		{
			var map = new Dictionary<(string, string), List<(string, string)>>();
			if (!File.Exists(path))
			{
				return map;
			}

			List<(string, string)>? current = null;
			foreach (var line in FindingFiles.Lines(path))
			{
				if (!line.StartsWith(' '))
				{
					var m = LocatedHeader.Match(line);
					current = null;
					if (m.Success)
					{
						var key = (m.Groups["url"].Value, m.Groups["head"].Value);
						if (!map.TryGetValue(key, out current))
						{
							current = [];
							map[key] = current;
						}
					}

					continue;
				}

				if (current is null)
				{
					continue;
				}

				var body = line.TrimStart();
				int gap = body.IndexOf("  ", StringComparison.Ordinal);
				var source = gap < 0 ? string.Empty : body[..gap];
				var excerpt = (gap < 0 ? body : body[(gap + 2)..]).Trim();
				if (excerpt.Length > 0)
				{
					current.Add((source, excerpt));
				}
			}

			return map;
		}

		/// <summary>Log 11: "word (langs) · valid en-GB, en-US" → the languages after "valid".</summary>
		private static Dictionary<string, string> ValidIn(string path)
		{
			var map = new Dictionary<string, string>(StringComparer.Ordinal);
			if (!File.Exists(path))
			{
				return map;
			}

			foreach (var value in FindingFiles.Values(path))
			{
				var (head, valid) = ParallelStoreWriter.SplitValidIn(value[0]);
				if (valid.Count > 0)
				{
					map.TryAdd(head, string.Join(", ", valid));
				}
			}

			return map;
		}
	}
}
