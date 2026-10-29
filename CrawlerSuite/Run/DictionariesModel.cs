using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Lexicon;

namespace CrawlerSuite.Run;

/// <summary>
/// The Dictionaries page: the entries of the user and site dictionaries the app no longer needs,
/// read from the snapshot the Results page shows — log 15 (not used, redundant) and log 35 (not
/// needed, from the second pass the page's button starts). One click per entry: Remove takes it
/// out of the file, Pin marks it "!" so it is never offered again, Keep leaves it for this
/// session. Entries already gone from the file, or pinned since, are not shown.
/// </summary>
public sealed class DictionariesModel : Observable
{
	private readonly ObservableCollection<DictionaryEntryVm> entries = [];
	private string heading = "Dictionaries";
	private string snapshotPath = string.Empty;
	private string notice = string.Empty;
	private bool hasSnapshot;
	private string reportText = string.Empty;
	private string notNeededText = string.Empty;
	private bool canFind;
	private string findHint = string.Empty;
	private string listSummary = string.Empty;

	public DictionariesModel()
	{
		ReasonChips =
		[
			new(nameof(CleanupReason.NotUsed), "Not used", PageSourceRole.Other, false, Refilter),
			new(nameof(CleanupReason.Redundant), "Redundant", PageSourceRole.Other, false, Refilter),
			new(nameof(CleanupReason.NotNeeded), "Not needed", PageSourceRole.Other, false, Refilter),
		];
		DictionaryChips =
		[
			new(nameof(DictionaryKind.User), "User dictionary", PageSourceRole.Other, false, Refilter),
			new(nameof(DictionaryKind.Site), "Site dictionary", PageSourceRole.Other, false, Refilter),
			new(nameof(DictionaryKind.Both), "In both", PageSourceRole.Other, false, Refilter),
		];
	}

	/// <summary>The entries the lit chips let through, by word.</summary>
	public ObservableCollection<DictionaryEntryVm> Shown { get; } = [];

	public IReadOnlyList<TriageFilterChipVm> ReasonChips { get; }

	public IReadOnlyList<TriageFilterChipVm> DictionaryChips { get; }

	public string Heading { get => heading; private set => Set(ref heading, value); }

	public string SnapshotPath { get => snapshotPath; private set => Set(ref snapshotPath, value); }

	public bool HasSnapshot { get => hasSnapshot; private set => Set(ref hasSnapshot, value); }

	/// <summary>Why nothing (or not everything) can be shown; empty when all is well.</summary>
	public string Notice { get => notice; private set => Set(ref notice, value); }

	/// <summary>Where the not-used and redundant entries come from.</summary>
	public string ReportText { get => reportText; private set => Set(ref reportText, value); }

	/// <summary>When the not-needed entries were looked for, or that they were not yet.</summary>
	public string NotNeededText { get => notNeededText; private set => Set(ref notNeededText, value); }

	/// <summary>Whether "Find entries no longer needed" can run now.</summary>
	public bool CanFind { get => canFind; private set => Set(ref canFind, value); }

	/// <summary>What the button does, or why it cannot run.</summary>
	public string FindHint { get => findHint; private set => Set(ref findHint, value); }

	public string ListSummary { get => listSummary; private set => Set(ref listSummary, value); }

	public bool HasEntries => entries.Count > 0;

	/// <summary>A snapshot is shown and nothing in it is left to clean up.</summary>
	public bool IsEmpty => HasSnapshot && entries.Count == 0;

	/// <summary>Nothing to show: no snapshot, or a crawl is running.</summary>
	public void ShowNone(string message)
	{
		HasSnapshot = false;
		SnapshotPath = string.Empty;
		Heading = "Dictionaries";
		Notice = message;
		ReportText = string.Empty;
		NotNeededText = string.Empty;
		entries.Clear();
		Raise(nameof(HasEntries));
		Raise(nameof(IsEmpty));
		Refilter();
	}

	/// <summary>Shows what <see cref="Read"/> found for a snapshot.</summary>
	public void Show(string snapshot, string siteLabel, DictionaryPageData data)
	{
		SnapshotPath = snapshot;
		Heading = siteLabel.Length > 0 ? siteLabel : Path.GetFileName(snapshot);
		HasSnapshot = true;
		entries.Clear();
		foreach (var row in data.Rows)
		{
			entries.Add(new DictionaryEntryVm(row.Entry, row.Paths, Recount)); // a decided row stays where it is
		}

		ReportText = data.HasReport
			? "Not used and redundant: from this snapshot's dictionary report (log 15)."
			: "This snapshot has no dictionary report (log 15): dictionary maintenance was off for its run. Find entries no longer needed makes one as well.";
		NotNeededText = data.NotNeededFoundAt is { } at
			? $"Not needed: looked for on {at.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} (log 35)."
			: data.HasNotNeededLog
				? "Not needed: from log 35."
				: "Not needed: not looked for on this snapshot yet.";
		Notice = data.Problems.Count > 0 ? string.Join(Environment.NewLine, data.Problems) : string.Empty;
		Raise(nameof(HasEntries));
		Raise(nameof(IsEmpty));
		Refilter();
	}

	/// <summary>Whether the button can run, and the line that says what it does or why not.</summary>
	public void SetFindState(bool can, string hint)
	{
		CanFind = can;
		FindHint = hint;
	}

	/// <summary>
	/// Reads a snapshot's logs 15 and 35 and keeps the entries that are still in their dictionary
	/// as plain lines (not removed or pinned since). Off the UI thread.
	/// </summary>
	public static DictionaryPageData Read(string snapshot, string userPath, string sitePath)
	{
		var report = DictionaryReport.ReportPath(snapshot);
		var notNeeded = DictionaryReport.NotNeededPath(snapshot);
		var problems = new List<string>();
		var lines = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

		HashSet<string> LinesOf(string path)
		{
			if (lines.TryGetValue(path, out var known))
			{
				return known;
			}

			var set = new HashSet<string>(StringComparer.Ordinal);
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
			{
				foreach (var line in File.ReadLines(path, Encoding.UTF8))
				{
					set.Add(line.Trim().TrimStart('﻿'));
				}
			}

			lines[path] = set;
			return set;
		}

		var rows = new List<DictionaryPageRow>();
		var seen = new HashSet<(string, DictionaryKind)>();
		var all = DictionaryReport.Read(report).Concat(DictionaryReport.Read(notNeeded));
		foreach (var entry in all)
		{
			if (!seen.Add((entry.Word, entry.Dictionary)))
			{
				continue; // listed twice (not used and redundant): the first reason is shown
			}

			var targets = entry.Dictionary switch
			{
				DictionaryKind.User => new[] { userPath },
				DictionaryKind.Site => new[] { sitePath },
				_ => new[] { userPath, sitePath },
			};
			var present = targets
				.Where(p => !string.IsNullOrEmpty(p))
				.Where(p => LinesOf(p).Contains(entry.Word) && !LinesOf(p).Contains("!" + entry.Word))
				.ToList();
			if (present.Count > 0)
			{
				rows.Add(new DictionaryPageRow(entry, present));
			}
		}

		foreach (var (path, label) in new[] { (userPath, "user"), (sitePath, "site") })
		{
			if (!string.IsNullOrEmpty(path) && !File.Exists(path))
			{
				problems.Add($"The {label} dictionary {Path.GetFileName(path)} was not found — its entries are not shown.");
			}
		}

		return new DictionaryPageData(rows, File.Exists(report), File.Exists(notNeeded), DictionaryReport.FoundAt(notNeeded), problems);
	}

	/// <summary>Removes the entry from its dictionary (both, for an entry in both).</summary>
	/// <param name="confirmShared">Asked before the first change to the shared user dictionary in this app run.</param>
	/// <returns>Null when done or declined; else what went wrong.</returns>
	public string? Remove(DictionaryEntryVm entry, Func<bool> confirmShared) =>
		Change(entry, confirmShared, DictionaryEditor.Remove, EntryState.Removed);

	/// <summary>Pins the entry ("!word"): kept, and never offered again.</summary>
	public string? Pin(DictionaryEntryVm entry, Func<bool> confirmShared) =>
		Change(entry, confirmShared, DictionaryEditor.Pin, EntryState.Pinned);

	/// <summary>Leaves the entry as it is; it is offered again on a later report.</summary>
	public void Keep(DictionaryEntryVm entry)
	{
		if (entry.IsOpen)
		{
			entry.State = EntryState.Kept;
		}
	}

	/// <summary>The path of the user dictionary of the loaded configuration, to know which change asks first.</summary>
	public string UserPath { get; set; } = string.Empty;

	private string? Change(DictionaryEntryVm entry, Func<bool> confirmShared, Func<string, string, bool> edit, EntryState done)
	{
		if (!entry.IsOpen)
		{
			return null;
		}

		bool touchesUser = entry.Paths.Any(p => string.Equals(p, UserPath, StringComparison.OrdinalIgnoreCase));
		if (touchesUser && !SharedDictionaryConsent.Ask(confirmShared))
		{
			return null;
		}

		try
		{
			bool changed = false;
			foreach (var path in entry.Paths)
			{
				changed |= edit(path, entry.Word);
			}

			entry.State = changed ? done : EntryState.Gone;
			return null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.LogWarning($"Dictionaries: could not change {string.Join(", ", entry.Paths.Select(Path.GetFileName))} — {ex.Message}");
			return ex.Message;
		}
	}

	/// <summary>The rows shown, tab-separated with a header line, masked at the strictest level.</summary>
	public string ShownAsText()
	{
		static string Cell(string text) => Mask.ApplyStrict(text).Replace('\t', ' ').Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
		var nl = Environment.NewLine;
		var sb = new StringBuilder("Entry\tDictionary\tReason\tDone").Append(nl);
		foreach (var entry in Shown)
		{
			sb.Append(Cell(entry.Word)).Append('\t')
				.Append(Cell(entry.DictionaryText)).Append('\t')
				.Append(Cell(entry.ReasonText)).Append('\t')
				.Append(Cell(entry.StateText)).Append(nl);
		}

		return sb.ToString();
	}

	private bool IsShown(DictionaryEntryVm e) =>
		TriageFilterChipVm.Lets(ReasonChips, e.Reason.ToString())
		&& TriageFilterChipVm.Lets(DictionaryChips, e.Dictionary.ToString());

	private void Refilter()
	{
		Recount();
		TriageFilterChipVm.Dim(ReasonChips);
		TriageFilterChipVm.Dim(DictionaryChips);
		Shown.Clear();
		foreach (var e in entries.Where(IsShown).OrderBy(e => e.Word, StringComparer.OrdinalIgnoreCase))
		{
			Shown.Add(e);
		}
	}

	private void Recount()
	{
		var open = entries.Where(e => e.IsOpen).ToList();
		foreach (var chip in ReasonChips)
		{
			chip.Count = open.Count(e => e.Reason.ToString() == chip.Key);
		}

		foreach (var chip in DictionaryChips)
		{
			chip.Count = open.Count(e => e.Dictionary.ToString() == chip.Key);
		}

		int removed = entries.Count(e => e.State == EntryState.Removed);
		int pinned = entries.Count(e => e.State == EntryState.Pinned);
		int kept = entries.Count(e => e.State == EntryState.Kept);
		ListSummary = entries.Count == 0
			? "Nothing to clean up."
			: $"{Format.Count(open.Count)} to decide · {Format.Count(removed)} removed · {Format.Count(pinned)} pinned · {Format.Count(kept)} kept this session";
	}
}

/// <summary>What the page read for a snapshot.</summary>
public sealed record DictionaryPageData(
	IReadOnlyList<DictionaryPageRow> Rows,
	bool HasReport,
	bool HasNotNeededLog,
	DateTime? NotNeededFoundAt,
	IReadOnlyList<string> Problems);

/// <summary>An entry and the dictionary files that still hold it.</summary>
public sealed record DictionaryPageRow(CleanupEntry Entry, IReadOnlyList<string> Paths);

public enum EntryState
{
	Open,
	Removed,
	Pinned,
	Kept,

	/// <summary>No longer in the file when the click came (changed elsewhere meanwhile).</summary>
	Gone,
}

/// <summary>One row of the Dictionaries page.</summary>
public sealed class DictionaryEntryVm : Observable
{
	private readonly Action changed;
	private EntryState state;

	public DictionaryEntryVm(CleanupEntry entry, IReadOnlyList<string> paths, Action changed)
	{
		Word = entry.Word;
		Dictionary = entry.Dictionary;
		Reason = entry.Reason;
		Paths = paths;
		this.changed = changed;
	}

	public string Word { get; }

	public DictionaryKind Dictionary { get; }

	public CleanupReason Reason { get; }

	/// <summary>The files the entry is in (two for an entry in both dictionaries).</summary>
	public IReadOnlyList<string> Paths { get; }

	public string DictionaryText => Dictionary switch
	{
		DictionaryKind.User => "User",
		DictionaryKind.Site => "Site",
		_ => "Both",
	};

	public string DictionaryHint => Dictionary switch
	{
		DictionaryKind.User => "The user dictionary — shared by every site",
		DictionaryKind.Site => "This site's dictionary",
		_ => "In the user dictionary and this site's dictionary — a click changes both",
	};

	public string ReasonText => Reason switch
	{
		CleanupReason.NotUsed => "Not used",
		CleanupReason.Redundant => "Redundant",
		_ => "Not needed",
	};

	public string ReasonHint => DictionaryReasons.Hint(Reason);

	public EntryState State
	{
		get => state;
		set
		{
			if (Set(ref state, value))
			{
				Raise(nameof(IsOpen));
				Raise(nameof(StateText));
				changed();
			}
		}
	}

	public bool IsOpen => state == EntryState.Open;

	public string StateText => state switch
	{
		EntryState.Removed => "Removed",
		EntryState.Pinned => "Pinned",
		EntryState.Kept => "Kept",
		EntryState.Gone => "Already gone",
		_ => string.Empty,
	};
}

/// <summary>The page's words for the three reasons.</summary>
public static class DictionaryReasons
{
	public static string Hint(CleanupReason reason) => reason switch
	{
		CleanupReason.NotUsed =>
			"No page of the run needed this entry: the system dictionary knows the word, or no page has it.",
		CleanupReason.Redundant =>
			"Covered by Known word parts (made only of them, or one of them plus a word the system dictionary knows) or by the acronym rule (not checked at all) — the app accepts it without the entry.",
		_ =>
			"Pages have the word, but a spell check without the user and site dictionaries accepts it too — the rules now cover it.",
	};
}

/// <summary>
/// The user dictionary is shared by every site: the first change to it while the app runs asks
/// once, from whichever page makes it (Dictionaries or Spell findings).
/// </summary>
public static class SharedDictionaryConsent
{
	private static bool given;

	/// <summary>True when the change may go ahead: asked before, or <paramref name="confirm"/> says yes now.</summary>
	public static bool Ask(Func<bool> confirm)
	{
		if (!given)
		{
			given = confirm();
		}

		return given;
	}
}
