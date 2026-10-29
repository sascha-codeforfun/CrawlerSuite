using System.Collections.Generic;
using System.Globalization;
using CrawlerSuite.Core.Settings;

namespace CrawlerSuite.Run;

/// <summary>What a usage line says.</summary>
public enum UsageKind
{
	/// <summary>Nothing to show.</summary>
	None,

	/// <summary>A plain note: not run yet, changed since, switched off.</summary>
	Note,

	/// <summary>Numbers from the last run.</summary>
	Counts,

	/// <summary>The entry did nothing in the last run: an amber card.</summary>
	Nothing,
}

/// <summary>A piece of a usage sentence; the numbers are bold.</summary>
public sealed record UsageRun(string Text, bool Bold);

/// <summary>
/// How much a setting entry was needed in the primary site's last run, shown under the entry:
/// the numbers in bold, a bar for the share of pages where that makes sense, the date apart, and
/// an amber card when the entry did nothing.
/// </summary>
public sealed record UsageLine(UsageKind Kind, IReadOnlyList<UsageRun> Runs, string Text, double? Share, string Day)
{
	public static readonly UsageLine None = new(UsageKind.None, [], string.Empty, null, string.Empty);

	public static readonly UsageLine NotRun = Note("Not run yet: the primary site has no analysed snapshot with these numbers.");

	public static readonly UsageLine ChangedSince = Note("Added or changed since the primary site's last run — no numbers yet.");

	public bool IsNote => Kind == UsageKind.Note;

	public bool IsCounts => Kind == UsageKind.Counts;

	public bool IsNothing => Kind == UsageKind.Nothing;

	public bool HasShare => Share is not null;

	public string DayText => Day.Length == 0 ? string.Empty : "snapshot " + Day;

	public static UsageLine Note(string text) => new(UsageKind.Note, [], text, null, string.Empty);

	public static UsageLine Nothing(string text, UsageReport report) => new(UsageKind.Nothing, [], text, null, DayOf(report));

	public static UsageLine Counts(UsageReport report, double? share, params (string Text, bool Bold)[] runs)
	{
		var list = new List<UsageRun>();
		foreach (var (text, bold) in runs)
		{
			list.Add(new UsageRun(text, bold));
		}

		return new(UsageKind.Counts, list, string.Empty, share, DayOf(report));
	}

	/// <summary>A number as a bold run.</summary>
	public static (string, bool) N(int value) => (value.ToString("N0", CultureInfo.InvariantCulture), true);

	/// <summary>Plain words.</summary>
	public static (string, bool) W(string text) => (text, false);

	public static string Plural(int n, string one, string many) => n == 1 ? one : many;

	private static string DayOf(UsageReport report) =>
		report.Day is { } d ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "date unknown";
}

/// <summary>Usage lines for the rows of a table setting, for the tables that have counts.</summary>
internal static class TableUsage
{
	public static UsageLine For(TableSpec spec, UsageReport? report, IReadOnlyDictionary<string, string> cells) =>
		ReferenceEquals(spec, TableSpecs.DownloadExclusions) ? DownloadExclusion(report, cells) : UsageLine.None;

	/// <summary>What a download exclusion kept out in the latest crawl, main crawl and extra download apart.</summary>
	internal static UsageLine DownloadExclusion(UsageReport? report, IReadOnlyDictionary<string, string> cells)
	{
		var key = cells.GetValueOrDefault("Value", string.Empty).Trim();
		if (key.Length == 0)
		{
			return UsageLine.None;
		}

		if (report is null)
		{
			return UsageLine.NotRun;
		}

		if (!report.Entries.ContainsKey(DownloadExclusionUsage.MainCrawl))
		{
			return UsageLine.Note("No numbers yet: they are counted while crawling, from the next crawl on (a re-run does not crawl).");
		}

		var main = report.Of(DownloadExclusionUsage.MainCrawl, key);
		var extra = report.Of(DownloadExclusionUsage.ExtraDownload, key);
		if (main is null && extra is null)
		{
			return UsageLine.ChangedSince;
		}

		int mainAddresses = main?.Pages ?? 0, extraAddresses = extra?.Pages ?? 0;
		int addresses = mainAddresses + extraAddresses;
		int times = (main?.Matches ?? 0) + (extra?.Matches ?? 0);
		bool on = cells.GetValueOrDefault("Enabled", "true") == "true";
		if (addresses == 0)
		{
			return UsageLine.Nothing(on
				? "Kept out nothing in the latest crawl — this entry may no longer be needed."
				: "Switched off, and would have kept out nothing in the latest crawl.", report);
		}

		// An address kept out in both phases is counted in each: the parts say where.
		return UsageLine.Counts(report, null,
			UsageLine.W(on ? "Kept out " : "Switched off — would have kept out "),
			UsageLine.N(addresses), UsageLine.W($" {UsageLine.Plural(addresses, "address", "addresses")} ("),
			UsageLine.N(times), UsageLine.W($" {UsageLine.Plural(times, "time", "times")}) — main crawl "),
			UsageLine.N(mainAddresses), UsageLine.W(", extra download "), UsageLine.N(extraAddresses));
	}
}
