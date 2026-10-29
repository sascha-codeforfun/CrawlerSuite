using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;

namespace CrawlerSuite.Run;

/// <summary>
/// The Spell findings page: every unique misspelling of the page text the Results page's
/// snapshot holds, decided once per word — User (right, for every site), Site (right, for this
/// site) or Triage (a typo, or unclear: looked at page by page later). A dictionary choice
/// takes effect on the next run. Triage is remembered only until the next run starts, so a
/// changed configuration or dictionary never meets an old decision. Words already in a
/// dictionary (added here, or by hand) show as decided.
/// </summary>
public sealed class SpellFindingsModel : Observable
{
	/// <summary>Words sent to triage, per snapshot; forgotten when a run starts.</summary>
	private static readonly HashSet<(string Snapshot, string Word)> SentToTriage = [];

	/// <summary>
	/// Decisions made on this page while the app runs, with what each added: Undo stays possible
	/// when the page is read again. Forgotten when a run starts, like triage.
	/// </summary>
	private static readonly Dictionary<(string Snapshot, string Word), (string? Path, IReadOnlyList<string> Entries)> DecidedHere = [];

	private readonly List<SpellWordVm> words = [];
	// Observable: the page is read again on every visit, and the screen must show the chips the
	// filter uses — a plain list raised under the same reference keeps the old chips on screen.
	private readonly ObservableCollection<TriageFilterChipVm> languageChips = [];
	private string heading = "Spell findings";
	private string snapshotPath = string.Empty;
	private string notice = string.Empty;
	private bool hasSnapshot;
	private string listSummary = string.Empty;

	/// <summary>The words the lit language chips let through, in the order of the log.</summary>
	public ObservableCollection<SpellWordVm> Shown { get; } = [];

	public ObservableCollection<TriageFilterChipVm> LanguageChips => languageChips;

	public string Heading { get => heading; private set => Set(ref heading, value); }

	public string SnapshotPath { get => snapshotPath; private set => Set(ref snapshotPath, value); }

	public bool HasSnapshot { get => hasSnapshot; private set => Set(ref hasSnapshot, value); }

	public string Notice { get => notice; private set => Set(ref notice, value); }

	public string ListSummary { get => listSummary; private set => Set(ref listSummary, value); }

	public bool HasWords => words.Count > 0;

	/// <summary>A snapshot is shown and its page text has no misspellings.</summary>
	public bool IsEmpty => HasSnapshot && words.Count == 0;

	/// <summary>The user dictionary of the loaded configuration, to know which change asks first.</summary>
	public string UserPath { get; private set; } = string.Empty;

	/// <summary>The scoped site's dictionary.</summary>
	public string SitePath { get; private set; } = string.Empty;

	/// <summary>A run starts: what was sent to triage is offered again on its new list.</summary>
	public static void ForgetTriage()
	{
		lock (SentToTriage)
		{
			SentToTriage.Clear();
			DecidedHere.Clear();
		}
	}

	public void ShowNone(string message)
	{
		HasSnapshot = false;
		SnapshotPath = string.Empty;
		Heading = "Spell findings";
		Notice = message;
		words.Clear();
		languageChips.Clear();
		Raise(nameof(LanguageChips));
		Raise(nameof(HasWords));
		Raise(nameof(IsEmpty));
		Refilter();
	}

	public void Show(string snapshot, string siteLabel, SpellFindingsData data, string userPath, string sitePath)
	{
		SnapshotPath = snapshot;
		Heading = siteLabel.Length > 0 ? siteLabel : Path.GetFileName(snapshot);
		HasSnapshot = true;
		UserPath = userPath;
		SitePath = sitePath;
		Notice = data.Problems.Count > 0 ? string.Join(Environment.NewLine, data.Problems) : string.Empty;

		words.Clear();
		foreach (var word in data.Words)
		{
			bool triaged;
			lock (SentToTriage)
			{
				triaged = SentToTriage.Contains((snapshot, word.Word));
			}

			var parts = data.Parts.TryGetValue(word.Word, out var found) ? found : [];
			var missing = parts.Where(p => p.IsUnknown).Select(p => p.Text).ToList();

			// A hyphenated word is decided once each part that kept it flagged has an entry.
			bool partsInUser = missing.Count > 0 && missing.All(p => data.InUser.Contains(p) || data.InSite.Contains(p)) && missing.Any(data.InUser.Contains);
			bool partsInSite = missing.Count > 0 && missing.All(data.InSite.Contains);
			var state = data.InUser.Contains(word.Word) ? WordState.User
				: data.InSite.Contains(word.Word) ? WordState.Site
				: partsInUser ? WordState.User
				: partsInSite ? WordState.Site
				: triaged ? WordState.Triage
				: WordState.Open;
			var vm = new SpellWordVm(word, parts, state, Recount);
			if (state is WordState.User or WordState.Site && !data.InUser.Contains(word.Word) && !data.InSite.Contains(word.Word))
			{
				vm.AddedParts = missing;
			}

			lock (SentToTriage)
			{
				if (state != WordState.Open && DecidedHere.TryGetValue((snapshot, word.Word), out var decided))
				{
					vm.Decided(decided.Path, decided.Entries);
				}
			}

			words.Add(vm);
		}

		languageChips.Clear();
		foreach (var language in words.SelectMany(w => w.Languages).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
		{
			languageChips.Add(new TriageFilterChipVm(language, language, PageSourceRole.Other, false, Refilter));
		}

		Raise(nameof(LanguageChips));
		Raise(nameof(HasWords));
		Raise(nameof(IsEmpty));
		Refilter();
	}

	/// <summary>
	/// Reads the snapshot's unique misspellings and which of them the dictionaries already hold
	/// (plain or pinned, regardless of case, as the lists match). Off the UI thread.
	/// </summary>
	public static SpellFindingsData Read(string snapshot, string userPath, string sitePath)
	{
		var problems = new List<string>();
		var list = SpellWordList.Read(snapshot);
		if (list.Count == 0 && !SpellWordList.Exists(snapshot))
		{
			problems.Add("This snapshot has no list of unique misspellings — the spell check did not run for it.");
		}

		return new SpellFindingsData(list, DictionaryCoverage.EntriesOf(userPath), DictionaryCoverage.EntriesOf(sitePath), problems, HyphenParts.Read(snapshot));
	}

	/// <summary>Adds the word to the user dictionary.</summary>
	/// <returns>Null when done or declined; else what went wrong.</returns>
	public string? AddToUser(SpellWordVm word, Func<bool> confirmShared)
	{
		if (!word.IsOpen || !SharedDictionaryConsent.Ask(confirmShared))
		{
			return null;
		}

		return Add(word, UserPath, WordState.User);
	}

	/// <summary>Adds the word to the site's dictionary.</summary>
	public string? AddToSite(SpellWordVm word) =>
		word.IsOpen ? Add(word, SitePath, WordState.Site) : null;

	/// <summary>Leaves the word for triage until the next run.</summary>
	public void SendToTriage(SpellWordVm word)
	{
		if (!word.IsOpen)
		{
			return;
		}

		lock (SentToTriage)
		{
			SentToTriage.Add((SnapshotPath, word.Word));
		}

		Remember(word, null, []);
		word.State = WordState.Triage;
	}

	/// <summary>A word added to a dictionary from triage: remembered as if decided here, so this page shows it decided and can undo it.</summary>
	internal static void RememberDecision(string snapshot, string word, string path, IReadOnlyList<string> added)
	{
		lock (SentToTriage)
		{
			DecidedHere[(snapshot, word)] = (path, added);
		}
	}

	/// <summary>Taken back from triage: open again here too.</summary>
	internal static void ForgetDecision(string snapshot, string word)
	{
		lock (SentToTriage)
		{
			DecidedHere.Remove((snapshot, word));
		}
	}

	private void Remember(SpellWordVm word, string? path, IReadOnlyList<string> added)
	{
		lock (SentToTriage)
		{
			DecidedHere[(SnapshotPath, word.Word)] = (path, added);
		}

		word.Decided(path, added);
	}

	/// <summary>
	/// Takes back a decision made on this page: removes exactly the entries that click added (not
	/// one that was in the file before) and opens the word again, to be decided anew.
	/// </summary>
	/// <returns>Null when done; else what went wrong (the word stays decided).</returns>
	public string? Undo(SpellWordVm word)
	{
		if (!word.CanUndo)
		{
			return null;
		}

		if (word.State == WordState.Triage)
		{
			lock (SentToTriage)
			{
				SentToTriage.Remove((SnapshotPath, word.Word));
			}
		}
		else if (word.UndoPath is { } path)
		{
			try
			{
				foreach (var entry in word.UndoEntries)
				{
					DictionaryEditor.Remove(path, entry);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Logger.LogWarning($"Spell findings: could not undo in {Path.GetFileName(path)} — {ex.Message}");
				return ex.Message;
			}
		}

		word.AddedParts = [];
		lock (SentToTriage)
		{
			DecidedHere.Remove((SnapshotPath, word.Word));
		}

		word.ClearUndo();
		word.State = WordState.Open;
		return null;
	}

	private string? Add(SpellWordVm word, string path, WordState done)
	{
		if (string.IsNullOrEmpty(path))
		{
			return "No dictionary file is known for this.";
		}

		// A hyphenated word with parts that kept it flagged: only the chosen parts are added.
		var entries = word.HasPartsToAdd ? word.SelectedParts : [word.Word];
		if (entries.Count == 0)
		{
			return "Choose the part or parts to add first: the highlighted parts are the ones no dictionary knows.";
		}

		var added = new List<string>();
		try
		{
			foreach (var entry in entries)
			{
				// False: already there, which is the same outcome — but not this click's to undo.
				if (DictionaryEditor.Add(path, entry))
				{
					added.Add(entry.Trim());
				}
			}

			word.AddedParts = word.HasPartsToAdd ? entries : [];
			Remember(word, path, added);
			word.State = done;
			return null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.LogWarning($"Spell findings: could not add to {Path.GetFileName(path)} — {ex.Message}");
			return ex.Message;
		}
	}

	/// <summary>
	/// The rows shown (the lit language chips), as tab-separated text with a header line: word,
	/// languages, valid in, pages, done, parts to add. Masked at the strictest level, like every copy.
	/// </summary>
	public string ShownAsText()
	{
		static string Cell(string text) => Mask.ApplyStrict(text).Replace('\t', ' ').Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
		var nl = Environment.NewLine;
		var sb = new StringBuilder("Word\tLanguages\tValid in\tPages\tDone\tParts to add").Append(nl);
		foreach (var word in Shown)
		{
			sb.Append(Cell(word.Word)).Append('\t')
				.Append(Cell(word.LanguagesText)).Append('\t')
				.Append(Cell(string.Join(", ", word.ValidIn))).Append('\t')
				.Append(word.Pages).Append('\t')
				.Append(Cell(word.StateText)).Append('\t')
				.Append(Cell(word.PartsToAddText)).Append(nl);
		}

		return sb.ToString();
	}

	private void Refilter()
	{
		Recount();
		TriageFilterChipVm.Dim(languageChips);
		try
		{
			Shown.Clear();
			foreach (var word in words.Where(IsShown))
			{
				Shown.Add(word);
			}
		}
		catch (InvalidOperationException ex)
		{
			// The table refused the change (it was in the middle of an edit): say so instead of
			// leaving a filter that silently does nothing.
			Logger.LogWarning($"Spell findings: the list could not be filtered — {ex.Message}");
		}
	}

	/// <summary>None lit: every word; else the words in a lit language (a word in several languages under each of them).</summary>
	private bool IsShown(SpellWordVm word) =>
		!languageChips.Any(c => c.IsOn) || word.Languages.Any(l => TriageFilterChipVm.Lets(languageChips, l));

	private void Recount()
	{
		foreach (var chip in languageChips)
		{
			chip.Count = words.Count(w => w.IsOpen && w.Languages.Contains(chip.Key, StringComparer.OrdinalIgnoreCase));
		}

		int open = words.Count(w => w.IsOpen);
		int user = words.Count(w => w.State == WordState.User);
		int site = words.Count(w => w.State == WordState.Site);
		int triage = words.Count(w => w.State == WordState.Triage);
		ListSummary = words.Count == 0
			? "No misspellings in the page text."
			: $"{Format.Count(open)} to decide · {Format.Count(user)} user · {Format.Count(site)} site · {Format.Count(triage)} triage";
	}
}

/// <summary>What the page read for a snapshot.</summary>
public sealed record SpellFindingsData(
	IReadOnlyList<SpellWord> Words,
	IReadOnlySet<string> InUser,
	IReadOnlySet<string> InSite,
	IReadOnlyList<string> Problems,
	IReadOnlyDictionary<string, IReadOnlyList<PartFinding>> Parts);

public enum WordState
{
	Open,
	User,
	Site,
	Triage,
}

/// <summary>One row of the Spell findings page.</summary>
public sealed class SpellWordVm : Observable
{
	private readonly Action changed;
	private WordState state;
	private IReadOnlyList<string> addedParts = [];

	public SpellWordVm(SpellWord word, IReadOnlyList<PartFinding> parts, WordState state, Action changed)
	{
		this.state = state;
		Word = word.Word;
		Languages = word.Languages;
		ValidIn = word.ValidIn;
		Pages = word.Pages;
		this.changed = changed;
		Parts = parts.Select(p => new WordPartVm(p, this)).ToList();
	}

	public string Word { get; }

	public IReadOnlyList<string> Languages { get; }

	public string LanguagesText => string.Join(", ", Languages);

	/// <summary>Other reference dictionaries that know the word; information only, the word stays flagged.</summary>
	public IReadOnlyList<string> ValidIn { get; }

	public bool HasValidIn => ValidIn.Count > 0;

	/// <summary>The pill: "valid en-US" or "valid en-GB, en-US".</summary>
	public string ValidInText => HasValidIn ? "valid " + string.Join(", ", ValidIn) : string.Empty;

	public int Pages { get; }

	public string PagesText => Format.Count(Pages);

	/// <summary>A hyphenated word's parts as the check placed them (log 36); empty for any other word.</summary>
	public IReadOnlyList<WordPartVm> Parts { get; }

	public bool HasParts => Parts.Count > 0;

	/// <summary>Some part kept the word flagged: User and Site add the chosen parts, not the word.</summary>
	public bool HasPartsToAdd => Parts.Any(p => p.IsUnknown);

	/// <summary>The parts chosen to be added (preset: every part no dictionary knows).</summary>
	public IReadOnlyList<string> SelectedParts => Parts.Where(p => p.IsUnknown && p.IsSelected).Select(p => p.Text).ToList();

	/// <summary>The parts added for this word, when it was decided part by part.</summary>
	public IReadOnlyList<string> AddedParts
	{
		get => addedParts;
		set
		{
			addedParts = value;
			Raise(nameof(StateText));
		}
	}

	/// <summary>The parts still to add, for the copied list: "Xyzzy" or "" for a plain word.</summary>
	public string PartsToAddText => string.Join(" · ", Parts.Where(p => p.IsUnknown).Select(p => p.Text));

	public WordState State
	{
		get => state;
		set
		{
			if (Set(ref state, value))
			{
				if (value == WordState.Open)
				{
					decidedHere = false;
				}

				Raise(nameof(IsOpen));
				Raise(nameof(CanUndo));
				Raise(nameof(StateText));
				foreach (var part in Parts)
				{
					part.RaiseOpen();
				}

				changed();
			}
		}
	}

	public bool IsOpen => state == WordState.Open;

	private string? undoPath;
	private IReadOnlyList<string> undoEntries = [];
	private bool decidedHere;

	/// <summary>The dictionary file this page's click wrote to (null for triage or no click here).</summary>
	internal string? UndoPath => undoPath;

	/// <summary>The entries that click added (not those already in the file).</summary>
	internal IReadOnlyList<string> UndoEntries => undoEntries;

	/// <summary>Decided on this page while the app runs: Undo takes it back.</summary>
	public bool CanUndo => decidedHere && state != WordState.Open;

	internal void Decided(string? path, IReadOnlyList<string> added)
	{
		undoPath = path;
		undoEntries = added;
		decidedHere = true;
		Raise(nameof(CanUndo));
	}

	/// <summary>The decision was taken back: nothing left to undo.</summary>
	internal void ClearUndo()
	{
		undoPath = null;
		undoEntries = [];
		decidedHere = false;
		Raise(nameof(CanUndo));
	}

	public string StateText => (state switch
	{
		WordState.User => "User dictionary",
		WordState.Site => "Site dictionary",
		WordState.Triage => "Triage",
		_ => string.Empty,
	}) + (addedParts.Count > 0 && state is WordState.User or WordState.Site ? $" · part {string.Join(", ", addedParts)}" : string.Empty);
}

/// <summary>One part of a hyphenated finding: chosen to be added when no dictionary knows it.</summary>
public sealed class WordPartVm : Observable
{
	private readonly SpellWordVm word;
	private bool isSelected;

	public WordPartVm(PartFinding part, SpellWordVm word)
	{
		this.word = word;
		Text = part.Text;
		Summary = part.Summary;
		IsUnknown = part.IsUnknown;
		isSelected = part.IsUnknown;
	}

	public string Text { get; }

	/// <summary>How the check placed the part: "unknown in de", "known word part", "system dictionary (de)".</summary>
	public string Summary { get; }

	/// <summary>No dictionary knows the part in a language the word was flagged in.</summary>
	public bool IsUnknown { get; }

	public bool IsKnown => !IsUnknown;

	public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }

	/// <summary>The choice can change while the word is undecided.</summary>
	public bool CanChoose => IsUnknown && word.IsOpen;

	internal void RaiseOpen() => Raise(nameof(CanChoose));
}
