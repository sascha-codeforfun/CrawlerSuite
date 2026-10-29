using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Formats;
using CrawlerSuite.Core.Security;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// Self-links (log 09) as triage items: a link in a page's content that points back to the page
	/// itself. The entry is the page; one item per link address, its excerpts together.
	/// </summary>
	public static class SelfLinkSource
	{
		public const string Type = "SELF_LINK";
		public const string Family = "Self-links";
		public const string Label = "Link to the page itself";

		/// <summary>Whether the snapshot holds log 09; without it "no self-link" means "not looked".</summary>
		public static bool HasLogs(string snapshotDirectory) =>
			OutputFormats.ByNumber("09") is { } f && FindingFiles.PairPath(snapshotDirectory, f.PrimaryFileName) is not null;

		/// <summary>Self-link verdicts a match against this snapshot must keep: all when it has no log 09.</summary>
		public static IEnumerable<string> KeptWithoutLogs(string snapshotDirectory, TriageLedger ledger) =>
			HasLogs(snapshotDirectory) ? [] : ledger.Findings.Values.Where(f => f.Type == Type).Select(f => f.Key).ToList();

		/// <summary>The ledger key: the page and the link exactly as written.</summary>
		public static string KeyOf(string page, string link) => $"{Type}|{page}|{link}";

		/// <summary>The self-links of a snapshot. <paramref name="localOf"/> gives a page's saved file (null when none).</summary>
		public static IReadOnlyList<TriageItem> Read(string snapshotDirectory, Func<string, string?> localOf)
		{
			if (OutputFormats.ByNumber("09") is not { } format || FindingFiles.PairPath(snapshotDirectory, format.PrimaryFileName) is not { } path)
			{
				return [];
			}

			// File, FileUrl, LinkFound, ContextSnippet
			var rows = FindingFiles.Csv(path, 4, header: true)
				.Select(c => (Page: c[1].Length > 0 ? c[1] : c[0], Link: CsvInjectionGuard.Denormalize(c[2]), Context: CsvInjectionGuard.Denormalize(c[3])))
				.Where(r => r.Page.Length > 0 && r.Link.Length > 0);
			return Items(rows, localOf);
		}

		internal static IReadOnlyList<TriageItem> Items(IEnumerable<(string Page, string Link, string Context)> rows, Func<string, string?> localOf)
		{
			var (family, label) = TriageSwitches.Describe(Type);
			var items = new List<TriageItem>();
			foreach (var same in rows.GroupBy(r => (r.Page, r.Link)))
			{
				var key = KeyOf(same.Key.Page, same.Key.Link);
				var context = string.Join("\n", same.Select(r => r.Context).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal));
				items.Add(new TriageItem(key, [key], same.Key.Page, Type, Type, family, label, same.Key.Link, context, localOf(same.Key.Page)));
			}

			return items;
		}
	}

	/// <summary>
	/// Canonical issues (log 16) as triage items: a chain (the canonical names a page that names
	/// another), several canonical tags on one page, a canonical naming an address that redirects,
	/// or one naming a page kept out of search. The entry is the page. Left out: a canonical
	/// whose target was not found — Broken links holds it (reach "Error elsewhere on site"), so it is
	/// decided once — and one naming another host, which log 16 marks as not for tracking.
	/// </summary>
	public static class CanonicalSource
	{
		public const string Family = "Canonical issues";
		public const string Chain = "CANONICAL_CHAIN";
		public const string Conflict = "CANONICAL_CONFLICT";
		public const string Redirect = "CANONICAL_REDIRECT";
		public const string Noindex = "CANONICAL_NOINDEX";

		/// <summary>The issue types triage takes from log 16, with their switch labels.</summary>
		public static IReadOnlyList<(string Type, string Label)> Types { get; } =
		[
			(Chain, "Canonical names a page with its own canonical"),
			(Conflict, "Several canonical tags"),
			(Redirect, "Canonical names an address that redirects"),
			(Noindex, "Canonical names a page kept out of search"),
		];

		public static bool IsCanonical(string type) => type is Chain or Conflict or Redirect or Noindex;

		/// <summary>Whether the snapshot holds log 16; without it "no issue" means "not looked".</summary>
		public static bool HasLogs(string snapshotDirectory) =>
			OutputFormats.ByNumber("16") is { } f && FindingFiles.PairPath(snapshotDirectory, f.PrimaryFileName) is not null;

		/// <summary>Canonical verdicts a match against this snapshot must keep: all when it has no log 16.</summary>
		public static IEnumerable<string> KeptWithoutLogs(string snapshotDirectory, TriageLedger ledger) =>
			HasLogs(snapshotDirectory) ? [] : ledger.Findings.Values.Where(f => IsCanonical(f.Type)).Select(f => f.Key).ToList();

		/// <summary>The ledger key: the issue, the page and the canonical it names (the detail lists more and may change wording).</summary>
		public static string KeyOf(string type, string page, string canonical) => $"{type}|{page}|{canonical}";

		/// <summary>The canonical issues of a snapshot. <paramref name="localOf"/> gives a page's saved file (null when none).</summary>
		public static IReadOnlyList<TriageItem> Read(string snapshotDirectory, Func<string, string?> localOf)
		{
			if (OutputFormats.ByNumber("16") is not { } format || FindingFiles.PairPath(snapshotDirectory, format.PrimaryFileName) is not { } path)
			{
				return [];
			}

			// PageUrl, IssueType, CanonicalUrl, Detail
			var rows = FindingFiles.Csv(path, 4, header: true)
				.Select(c => (Page: c[0], Type: c[1].Trim(), Canonical: CsvInjectionGuard.Denormalize(c[2]), Detail: CsvInjectionGuard.Denormalize(c[3])));
			return Items(rows, localOf);
		}

		internal static IReadOnlyList<TriageItem> Items(IEnumerable<(string Page, string Type, string Canonical, string Detail)> rows, Func<string, string?> localOf)
		{
			var items = new List<TriageItem>();
			foreach (var same in rows.Where(r => r.Page.Length > 0 && IsCanonical(r.Type)).GroupBy(r => (r.Type, r.Page, r.Canonical)))
			{
				var (family, label) = TriageSwitches.Describe(same.Key.Type);
				var key = KeyOf(same.Key.Type, same.Key.Page, same.Key.Canonical);
				var detail = same.First().Detail;
				items.Add(new TriageItem(key, [key], same.Key.Page, same.Key.Type, same.Key.Type, family, label, detail,
					Text.DocumentText.F(Text.DocumentLanguage.English, "Canonical: {0}", same.Key.Canonical), localOf(same.Key.Page),
					ContextDe: Text.DocumentText.F(Text.DocumentLanguage.German, "Canonical: {0}", same.Key.Canonical),
					DetailDe: Text.LogSentences.CanonicalDetail(Text.DocumentLanguage.German, detail)));
			}

			return items;
		}
	}

	/// <summary>
	/// Forwarding links (log 40) as triage items: links to the site's own server that hand it a full
	/// address in their query. Always a finding, decided once: one item per page, parameter and set of
	/// target hosts — a host that turns up later brings the item back. External targets are security
	/// relevant. The app never requests these links; the item says what to check by hand, and the
	/// targets are written out in full.
	/// </summary>
	public static class ForwardingLinkSource
	{
		public const string External = "FORWARDING_LINK_EXTERNAL";
		public const string SameHost = "FORWARDING_LINK_SAME_HOST";
		public const string Family = "Forwarding links";

		/// <summary>The types with their switch labels.</summary>
		public static IReadOnlyList<(string Type, string Label)> Types { get; } =
		[
			(External, "Hands the server another site's address"),
			(SameHost, "Hands the server an address of the site"),
		];

		public static bool IsForwarding(string type) => type is External or SameHost;

		/// <summary>Whether the snapshot holds log 40; without it "no forwarding link" means "not looked".</summary>
		public static bool HasLogs(string snapshotDirectory) =>
			OutputFormats.ByNumber("40") is { } f && FindingFiles.PairPath(snapshotDirectory, f.PrimaryFileName) is not null;

		/// <summary>Forwarding verdicts a match against this snapshot must keep: all when it has no log 40.</summary>
		public static IEnumerable<string> KeptWithoutLogs(string snapshotDirectory, TriageLedger ledger) =>
			HasLogs(snapshotDirectory) ? [] : ledger.Findings.Values.Where(f => IsForwarding(f.Type)).Select(f => f.Key).ToList();

		/// <summary>The ledger key: the type, the page, the parameter and the target hosts (lower case, sorted).</summary>
		public static string KeyOf(string type, string page, string parameter, IEnumerable<string> hosts) =>
			$"{type}|{page}|{parameter}|{string.Join(",", hosts.Select(h => h.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}";

		/// <summary>One row of log 40.</summary>
		internal sealed record Row(string Page, string Link, string Parameter, string Target, string Host, bool IsExternal);

		/// <summary>The forwarding links of a snapshot. <paramref name="localOf"/> gives a page's saved file (null when none).</summary>
		public static IReadOnlyList<TriageItem> Read(string snapshotDirectory, Func<string, string?> localOf)
		{
			if (OutputFormats.ByNumber("40") is not { } format || FindingFiles.PairPath(snapshotDirectory, format.PrimaryFileName) is not { } path)
			{
				return [];
			}

			// Page, Link, Parameter, Target, TargetHost, Scope
			var rows = FindingFiles.Csv(path, 6, header: true)
				.Select(c => new Row(c[0], CsvInjectionGuard.Denormalize(c[1]), CsvInjectionGuard.Denormalize(c[2]), CsvInjectionGuard.Denormalize(c[3]),
					c[4], c[5] == Links.ForwardingLinks.ExternalScope))
				.Where(r => r.Page.Length > 0 && r.Target.Length > 0)
				.ToList();
			return Items(rows, localOf);
		}

		internal static IReadOnlyList<TriageItem> Items(IReadOnlyList<Row> rows, Func<string, string?> localOf)
		{
			var items = new List<TriageItem>();
			foreach (var same in rows.GroupBy(r => (r.Page, r.Parameter, r.IsExternal)).OrderBy(g => g.Key.IsExternal ? 0 : 1))
			{
				var type = same.Key.IsExternal ? External : SameHost;
				var (family, label) = TriageSwitches.Describe(type);
				var hosts = same.Select(r => r.Host).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
				var key = KeyOf(type, same.Key.Page, same.Key.Parameter, hosts);
				var detail = $"{same.Key.Parameter} → {string.Join(", ", hosts)}";
				var list = same.ToList();
				items.Add(new TriageItem(key, [key], same.Key.Page, type, type, family, label, detail,
					ContextOf(list, Text.DocumentLanguage.English), localOf(same.Key.Page),
					ContextDe: ContextOf(list, Text.DocumentLanguage.German)));
			}

			return items;
		}

		/// <summary>Every target written out in full, the links as written, and what is left to check by hand.</summary>
		internal static string ContextOf(IReadOnlyList<Row> rows, Text.DocumentLanguage language)
		{
			string F(string format, params object?[] args) => Text.DocumentText.F(language, format, args);
			var lines = new List<string>();
			foreach (var target in rows.Select(r => r.Target).Distinct(StringComparer.Ordinal))
			{
				lines.Add(F("Forwards to: {0}", target));
			}

			foreach (var link in rows.Select(r => r.Link).Distinct(StringComparer.Ordinal))
			{
				lines.Add(F("Link as written: {0}", link));
			}

			lines.Add(Text.DocumentText.T(language,
				"Not requested by the app: check by hand that the forwarding accepts only its intended destinations."));
			return string.Join("\n", lines);
		}
	}
}
