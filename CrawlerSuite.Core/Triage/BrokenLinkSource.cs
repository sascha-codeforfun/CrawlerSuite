using CrawlerSuite.Core.Text;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Formats;
using CrawlerSuite.Core.Links;
using CrawlerSuite.Core.Security;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// Broken links (log 07) as triage items. An entry is where the fix is made: a stylesheet for
	/// its url() references; the broken address itself when the same reference is written on more
	/// than one page (a template or component); otherwise the one page that holds it. One item per
	/// broken address of an entry, badged with its most serious reach. Nothing becomes a ticket
	/// without a verdict.
	/// </summary>
	public static class BrokenLinkSource
	{
		public const string Type = "BROKEN_LINK";
		public const string Family = "Broken links";

		/// <summary>Pages named on a shared entry before "… and N more".</summary>
		internal const int PagesListed = 10;

		/// <summary>The switch of a reach: one per reach, all on by default.</summary>
		public static string SwitchIdOf(string reach) => $"{Type}:{reach}";

		/// <summary>Whether the snapshot holds log 07; without it "no 404" means "not looked".</summary>
		public static bool HasLogs(string snapshotDirectory) =>
			OutputFormats.ByNumber("07") is { } f && FindingFiles.PairPath(snapshotDirectory, f.PrimaryFileName) is not null;

		/// <summary>Broken-link verdicts a match against this snapshot must keep: all when it has no log 07.</summary>
		public static IEnumerable<string> KeptWithoutLogs(string snapshotDirectory, TriageLedger ledger) =>
			HasLogs(snapshotDirectory)
				? []
				: ledger.Findings.Values.Where(f => f.Type == Type).Select(f => f.Key).ToList();

		// A cache buster: 16 or more hex digits standing alone between dots or slashes (lib.min.<hash>.css).
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex CacheBuster = new(@"(?<=[./])[0-9A-Fa-f]{16,}(?=[./]|$)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

		/// <summary>The address with its cache buster as "#": a rebuild that keeps the file keeps the verdict.</summary>
		public static string CacheNeutral(string address) => CacheBuster.Replace(address, "#");

		/// <summary>The ledger key: the entry and the broken address, exact but for cache busters.</summary>
		public static string KeyOf(string entry, string broken) => $"{Type}|{CacheNeutral(entry)}|{CacheNeutral(broken)}";

		/// <summary>One reference as log 07 has it.</summary>
		internal sealed record Row(string Broken, string Kind, string Referrer, string AsWritten, string Grade, string Evidence, string Reach);

		/// <summary>
		/// The broken links of a snapshot as triage items. <paramref name="localOf"/> gives the saved
		/// file of a page or stylesheet address (null when there is none).
		/// </summary>
		public static IReadOnlyList<TriageItem> Read(string snapshotDirectory, Func<string, string?> localOf)
		{
			if (OutputFormats.ByNumber("07") is not { } format || FindingFiles.PairPath(snapshotDirectory, format.PrimaryFileName) is not { } path)
			{
				return [];
			}

			var rows = FindingFiles.Csv(path, 7, header: true)
				.Select(c => new Row(c[0], c[1], c[2], CsvInjectionGuard.Denormalize(c[3]), c[4], CsvInjectionGuard.Denormalize(c[5]),
					c[6].Length > 0 ? BrokenLinkReach.Current(c[6]) : BrokenLinkReach.Of(c[1], c[4])))
				.Where(r => r.Broken.Length > 0)
				.ToList();
			return Items(rows, localOf, NotOnlinePaths(snapshotDirectory));
		}

		/// <summary>The paths of log 39: in the CMS export, not online by its configured column (empty without the file).</summary>
		internal static IReadOnlySet<string> NotOnlinePaths(string snapshotDirectory)
		{
			var path = Path.Combine(snapshotDirectory, LogFileNames.ContentListNotOnline);
			return File.Exists(path)
				? FindingFiles.Lines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
				: new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}

		/// <summary>Whether a broken address is a page the CMS has but not online: its path and query, as the content list writes them.</summary>
		internal static bool IsNotOnline(string broken, IReadOnlySet<string>? notOnline) =>
			notOnline is { Count: > 0 } && Uri.TryCreate(broken, UriKind.Absolute, out var uri) && notOnline.Contains(uri.PathAndQuery);

		private static bool IsAddress(string referrer) =>
			Uri.TryCreate(referrer, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

		/// <summary>Where the fix of a reference is made (see the class summary).</summary>
		internal static string EntryOf(Row row, IReadOnlyDictionary<(string, string, string), int> referrersOfSame)
		{
			if (row.Kind == "stylesheet url()" && IsAddress(row.Referrer))
			{
				return row.Referrer;
			}

			if (!IsAddress(row.Referrer))
			{
				return row.Broken;
			}

			return referrersOfSame.GetValueOrDefault((row.Broken, row.Kind, row.AsWritten)) > 1 ? row.Broken : row.Referrer;
		}

		internal static IReadOnlyList<TriageItem> Items(IReadOnlyList<Row> rows, Func<string, string?> localOf, IReadOnlySet<string>? notOnline = null)
		{
			var referrersOfSame = rows
				.Where(r => IsAddress(r.Referrer))
				.GroupBy(r => (r.Broken, r.Kind, r.AsWritten))
				.ToDictionary(g => g.Key, g => g.Select(r => r.Referrer).Distinct(StringComparer.OrdinalIgnoreCase).Count());

			var items = new List<TriageItem>();
			foreach (var group in rows.GroupBy(r => (Entry: EntryOf(r, referrersOfSame), r.Broken)))
			{
				var list = group.ToList();
				var reach = list.MinBy(r => BrokenLinkReach.Rank(r.Reach))!.Reach;
				var entry = group.Key.Entry;
				bool shared = entry.Equals(group.Key.Broken, StringComparison.OrdinalIgnoreCase);
				var switchId = SwitchIdOf(reach);
				var (family, label) = TriageSwitches.Describe(switchId);
				var key = KeyOf(entry, group.Key.Broken);
				bool offline = IsNotOnline(group.Key.Broken, notOnline);
				items.Add(new TriageItem(key, [key], entry, Type, switchId, family, label, group.Key.Broken,
					ContextOf(list, reach, shared, notOnline: offline), shared ? null : localOf(entry),
					ContextDe: ContextOf(list, reach, shared, DocumentLanguage.German, offline)));
			}

			return items;
		}

		/// <summary>What the operator decides on: how it is referenced, the evidence, and the check in a browser.</summary>
		internal static string ContextOf(IReadOnlyList<Row> rows, string reach, bool shared, DocumentLanguage language = DocumentLanguage.English,
			bool notOnline = false)
		{
			string T(string english) => DocumentText.T(language, english);
			string F(string format, params object?[] args) => DocumentText.F(language, format, args);
			var lines = new List<string>();
			if (shared)
			{
				// Said by kind: redirecting addresses are not pages sharing a template.
				foreach (var kind in rows.Where(r => IsAddress(r.Referrer)).GroupBy(r => r.Kind, StringComparer.Ordinal))
				{
					var referrers = kind.Select(r => r.Referrer).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
					lines.Add(SharedLine(kind.Key, referrers.Count, language));
					lines.AddRange(referrers.Take(PagesListed).Select(p => "  " + p));
					if (referrers.Count > PagesListed)
					{
						lines.Add(F("  … and {0} more (all in 07-404-sources)", referrers.Count - PagesListed));
					}
				}
			}

			foreach (var r in rows.DistinctBy(r => (r.Kind, r.AsWritten, r.Grade, r.Evidence)))
			{
				lines.Add(F("Referenced as: {0}", T(r.Kind)) + (r.AsWritten.Length > 0 ? $" — {r.AsWritten}" : string.Empty));
				if (r.Grade.Length > 0)
				{
					lines.Add(BrokenLinkOrigins.IsTime(r.Grade) ? F("When requested: {0}", T(r.Grade)) : F("Kind: {0}", T(r.Grade)));
				}

				if (r.Evidence.Length > 0)
				{
					lines.Add(F("Evidence: {0}", LogSentences.Evidence(language, r.Evidence)));
				}
			}

			if (notOnline)
			{
				// The export's own say (log 39): the link may be right and the page not published yet.
				lines.Add(T("The CMS content list has this page, but not as online (log 39): it may not be published yet — publish it, or link elsewhere until it is."));
			}

			var first = rows.FirstOrDefault(r => r.Reach == reach) ?? rows[0];
			lines.Add(F("Check: {0}", BrokenLinkReach.BrowserCheck(reach, first.Broken, first.Kind, language)));
			return string.Join("\n", lines);
		}

		/// <summary>The heading of a shared entry's referrers, by the kind of reference.</summary>
		internal static string SharedLine(string kind, int count, DocumentLanguage language = DocumentLanguage.English) => kind switch
		{
			"redirect" => DocumentText.F(language, "{0} address(es) redirect here — one fix per redirect, where it is configured:", count),
			_ => DocumentText.F(language, "Referenced the same way on {0} page(s) — one fix in the shared template or component:", count),
		};
	}
}
