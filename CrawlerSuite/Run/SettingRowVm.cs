using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Settings;

namespace CrawlerSuite.Run;

/// <summary>
/// One setting on the screen: its field (a switch, a choice, a number, a text, a table or JSON),
/// and what the row shows besides it — the card of the chosen value, the free space of the
/// download drive, the processors of this computer, or what a placeholder becomes per site.
/// </summary>
public sealed class SettingRowVm : Observable
{
	private readonly SettingsModel owner;
	private string text = string.Empty;
	private bool isOn;
	private string error = string.Empty;
	private string sectionHeading = string.Empty;
	private SettingTableVm? table;
	private EntryListVm? entries;
	private BoilerplateVm? boilerplate;
	private ChipSetVm? chips;
	private TriageModesEditorVm? triageModes;
	private SuppressionsVm? suppressions;
	private PatternsVm? patterns;

	public SettingRowVm(SettingInfo info, SettingsModel owner)
	{
		Info = info;
		this.owner = owner;
		DefaultText = SettingsCatalog.DescribeDefault(info);
		LoadFromDocument();
	}

	public SettingInfo Info { get; }

	internal SettingsTabVm? Tab { get; set; }

	/// <summary>A row unlocked while defaults were hidden stays in view until the tab is refiltered.</summary>
	internal bool KeepShown { get; private set; }

	public string Label => Info.Label;

	public string Key => Info.Path;

	public string Help => Info.Help;

	public string Unit => Info.Unit ?? string.Empty;

	/// <summary>The choices; for the default language, the installed bundle languages (and the current value if it is not installed).</summary>
	public IReadOnlyList<string> Choices => Info.ChoicesFromBundles
		? (owner.BundleLanguages.Contains(Text, StringComparer.OrdinalIgnoreCase) || Text.Length == 0
			? owner.BundleLanguages
			: [.. owner.BundleLanguages, Text])
		: Info.Choices ?? [];

	/// <summary>The default language names a language without a dictionary bundle.</summary>
	public string ChoiceNote => Info.ChoicesFromBundles && Text.Length > 0 && !owner.BundleLanguages.Contains(Text, StringComparer.OrdinalIgnoreCase)
		? $"{Text}: no dictionary bundle — install to use."
		: string.Empty;

	internal SettingsModel Owner => owner;

	public string SectionHeading
	{
		get => sectionHeading;
		set
		{
			if (Set(ref sectionHeading, value))
			{
				Raise(nameof(HasSectionHeading));
				Raise(nameof(SectionHeadingText));
			}
		}
	}

	public bool HasSectionHeading => SectionHeading.Length > 0;

	// ── Search and folding ───────────────────────────────────────────────────

	private string query = string.Empty;
	private SettingHit? hit;
	private bool isFolded;
	private int sectionCount;

	/// <summary>Where the tab's search text occurs in this setting: name, key, help or value.</summary>
	internal bool Matches(string text) =>
		SettingSearch.Find(Info, Key, Help, CurrentValue, text) is { Any: true };

	/// <summary>The tab's search text (empty: no search). Sets the highlighted pieces and the value note.</summary>
	internal void SetQuery(string text)
	{
		query = text.Trim();
		hit = query.Length > 0 ? SettingSearch.Find(Info, Key, Help, CurrentValue, query) : null;
		Raise(nameof(LabelPieces));
		Raise(nameof(KeyPieces));
		Raise(nameof(HelpPieces));
		Raise(nameof(SearchNote));
		Raise(nameof(HasSearchNote));
	}

	public IReadOnlyList<TextPiece> LabelPieces => SettingSearch.Split(Label, query);

	public IReadOnlyList<TextPiece> KeyPieces => SettingSearch.Split(Key, query);

	public IReadOnlyList<TextPiece> HelpPieces => SettingSearch.Split(Help, query);

	/// <summary>"Found in its value: …entry…" when the search text is in the value.</summary>
	public string SearchNote => hit?.Value is { } found ? $"Found in its value: {found}" : string.Empty;

	public bool HasSearchNote => SearchNote.Length > 0;

	/// <summary>This row carries its section's heading and the section is folded: only the heading shows.</summary>
	public bool IsFolded
	{
		get => isFolded;
		set
		{
			if (Set(ref isFolded, value))
			{
				Raise(nameof(ShowsBody));
				Raise(nameof(SectionHeadingText));
				Raise(nameof(SectionChevron));
			}
		}
	}

	public bool ShowsBody => !isFolded;

	/// <summary>How many settings the section shows (for the folded heading).</summary>
	public int SectionCount
	{
		get => sectionCount;
		set
		{
			if (Set(ref sectionCount, value))
			{
				Raise(nameof(SectionHeadingText));
				Raise(nameof(SectionCountText));
			}
		}
	}

	/// <summary>▾ open, ▸ folded.</summary>
	public string SectionChevron => isFolded ? "\u25B8" : "\u25BE";

	/// <summary>"6 settings" — the size of the section, beside its heading.</summary>
	public string SectionCountText => $"{SectionCount} setting{(SectionCount == 1 ? string.Empty : "s")}";

	/// <summary>"▾ Words" open, "▸ Words · 4 settings" folded (for screen readers).</summary>
	public string SectionHeadingText => isFolded
		? $"\u25B8  {SectionHeading} · {SectionCount} setting{(SectionCount == 1 ? string.Empty : "s")}"
		: $"\u25BE  {SectionHeading}";

	/// <summary>In the file: an override, editable. Otherwise the default, locked. A required setting has no default: always editable.</summary>
	public bool IsSet => Info.Required || owner.Has(Info.Path);

	/// <summary>No default and no lock: the file must set it.</summary>
	public bool IsRequired => Info.Required;

	/// <summary>Red when a required setting is empty: "Required: choose a folder."</summary>
	public string RequiredProblem => Info.Required && !IsSwitch && Text.Trim().Length == 0 && table is null && entries is null
		? $"Required: {Info.RequiredText}."
		: string.Empty;

	public bool HasRequiredProblem => RequiredProblem.Length > 0;

	/// <summary>"Required." under a required setting that is filled in.</summary>
	public bool ShowsRequired => Info.Required && !HasRequiredProblem;

	public bool IsEditable => IsSet && owner.CanEdit;

	/// <summary>The default in words: "150 MB", "on", "your Windows sign-in", "built-in list of 25 entries".</summary>
	public string DefaultText { get; }

	// ── Field kind ───────────────────────────────────────────────────────────

	public bool IsSwitch => Info.Kind == SettingKind.Switch;

	public bool IsChoice => Info.Kind == SettingKind.Choice;

	public bool IsTable => table is not null;

	/// <summary>A list or map shown as JSON: one with nested parts, or a value the table cannot hold.</summary>
	public bool IsMultiline => Info.Kind is SettingKind.Lines or SettingKind.Json && table is null && entries is null && boilerplate is null
		&& chips is null && triageModes is null && suppressions is null && patterns is null;

	public bool IsEntryList => entries is not null;

	public EntryListVm? Entries => entries;

	public bool IsBoilerplate => boilerplate is not null;

	public BoilerplateVm? Boilerplate => boilerplate;

	public bool IsChipSet => chips is not null;

	/// <summary>The triage switches (Settings → Triage).</summary>
	public bool IsTriageModes => triageModes is not null;

	public TriageModesEditorVm? TriageModes => triageModes;

	public ChipSetVm? Chips => chips;

	public bool IsSuppressions => suppressions is not null;

	public SuppressionsVm? Suppressions => suppressions;

	public bool IsPatterns => patterns is not null;

	public PatternsVm? Patterns => patterns;

	public bool IsSlider => Info.Extra is RowExtra.Processors or RowExtra.Slider;

	/// <summary>The processors slider, with what Windows reports beside it.</summary>
	public bool IsProcessors => Info.Extra == RowExtra.Processors;

	/// <summary>A number: a field sized to its largest value, the unit beside it.</summary>
	public bool IsNumber => Info.Kind is SettingKind.Number or SettingKind.Decimal && !IsSlider;

	/// <summary>A text or path: a field across the row.</summary>
	public bool IsSingleLine => Info.Kind is SettingKind.Text or SettingKind.Folder or SettingKind.File;

	public bool IsPath => Info.Kind is SettingKind.Folder or SettingKind.File;

	public SettingTableVm? Table => table;

	/// <summary>The width of a number field: its largest allowed value with room to spare.</summary>
	public double FieldWidth
	{
		get
		{
			var widest = new[] { Info.Max, Info.Min }
				.Where(v => v is not null)
				.Select(v => v!.Value.ToString(Info.Kind == SettingKind.Decimal ? "0.00" : "0", CultureInfo.InvariantCulture).Length)
				.DefaultIfEmpty(6)
				.Max();
			return (Math.Max(widest, 4) + 4) * 9 + 16;
		}
	}

	// ── Value ────────────────────────────────────────────────────────────────

	/// <summary>The field's text (all kinds but switches and tables); applied when it is a valid value.</summary>
	public string Text
	{
		get => text;
		set
		{
			if (!Set(ref text, value ?? string.Empty) || !IsSet)
			{
				return;
			}

			var (parsed, problem) = SettingValues.FromText(Info, text);
			Error = problem ?? string.Empty;
			if (problem is null)
			{
				owner.SetValue(Info.Path, parsed);
			}

			Raise(nameof(RequiredProblem));
			Raise(nameof(HasRequiredProblem));
			Raise(nameof(ShowsRequired));
			Raise(nameof(HasTableProblems));
			RefreshExtras();
		}
	}

	public bool IsOn
	{
		get => isOn;
		set
		{
			if (Set(ref isOn, value) && IsSet)
			{
				owner.SetValue(Info.Path, JsonValue.Create(value));
			}
		}
	}

	public string Error
	{
		get => error;
		private set
		{
			if (Set(ref error, value))
			{
				Raise(nameof(HasError));
			}
		}
	}

	public bool HasError => Error.Length > 0;

	/// <summary>Whether a table row is marked: Save waits until it is fixed.</summary>
	public bool HasTableProblems => HasRequiredProblem || table?.HasProblems == true || entries?.HasProblems == true || boilerplate?.HasProblems == true
		|| chips?.HasProblems == true || suppressions?.HasProblems == true || patterns?.HasProblems == true;

	// ── Choice card ──────────────────────────────────────────────────────────

	private ChoiceCard? Card => IsChoice ? Info.Cards?.GetValueOrDefault(Text)
		: Info.WarnAbove is { } limit && double.TryParse(Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && number > limit
			? new ChoiceCard(CrawlerSuite.Core.Settings.CardTone.Caution, Info.WarnText)
			: null;

	public bool HasCard => Card is not null;

	public string CardText => Card?.Text ?? string.Empty;

	/// <summary>Good, Caution or Danger: the view colours the card by it.</summary>
	public string CardTone => Card?.Tone.ToString() ?? string.Empty;

	// ── Free space (the download folder) ─────────────────────────────────────

	public bool HasFreeSpace => Info.Extra == RowExtra.FreeSpace;

	public string FreeSpaceText { get; private set; } = string.Empty;

	public string FreeSpaceExplain { get; private set; } = string.Empty;

	/// <summary>Low, Some, Plenty or Unrated: the bar's colour.</summary>
	public string FreeSpaceRating { get; private set; } = nameof(SpaceRating.Unrated);

	/// <summary>The used share of the drive, 0 to 1.</summary>
	public double UsedShare { get; private set; }

	public bool HasDrive { get; private set; }

	// ── Processors ───────────────────────────────────────────────────────────

	public double SliderMaximum => IsProcessors ? Math.Max(owner.Processors.Available, CurrentNumber) : Info.Max ?? CurrentNumber;

	public double SliderMinimum => IsProcessors ? 0 : Info.Min ?? 0;

	/// <summary>The label under the slider's left end: "All" for the processors, else the minimum.</summary>
	public string SliderMinimumText => IsProcessors ? "All" : SliderMinimum.ToString(CultureInfo.InvariantCulture);

	/// <summary>The slider can move: the setting is editable and not switched off by another one.</summary>
	public bool SliderEnabled => IsEditable && InactiveNote.Length == 0;

	/// <summary>The slider: 0 (All) to the processors available, or the setting's range.</summary>
	public double SliderValue
	{
		get => CurrentNumber;
		set
		{
			var whole = (int)Math.Round(value);
			if (whole != CurrentNumber && IsSet)
			{
				Text = whole.ToString(CultureInfo.InvariantCulture);
			}
		}
	}

	public string SliderText => IsProcessors && CurrentNumber == 0 ? "All"
		: CurrentNumber.ToString(CultureInfo.InvariantCulture) + (IsProcessors || Info.Unit is null ? string.Empty : " " + Info.Unit);

	/// <summary>"Not used while “Ignore acronyms” is off." — for a setting that only matters while a switch is on.</summary>
	public string InactiveNote => Info.ActiveWhen is { } path && !owner.SwitchOn(path)
		? $"Not used while \u201C{SettingsCatalog.Find(path)?.Label ?? path}\u201D is off."
		: string.Empty;

	public bool HasInactiveNote => InactiveNote.Length > 0;

	/// <summary>The switch this setting depends on changed.</summary>
	internal void RaiseInactive()
	{
		Raise(nameof(InactiveNote));
		Raise(nameof(HasInactiveNote));
		Raise(nameof(SliderEnabled));
	}

	public IReadOnlyList<string> ProcessorLines
	{
		get
		{
			var p = owner.Processors;
			var lines = new List<string> { $"Available to this app: {p.Available}" };
			if (p.Cores > 0)
			{
				lines.Add($"Windows reports: {p.Cores} cores, {p.Logical} logical processors");
				if (p.Performance > 0)
				{
					lines.Add($"of which {p.Performance} performance and {p.Efficiency} efficiency cores");
				}
			}

			lines.Add("In a virtual machine these are the virtual processors the host assigns.");
			lines.Add("Recommended: All");
			return lines;
		}
	}

	/// <summary>A value above what this computer offers: it works, but gains nothing over All.</summary>
	public string ProcessorNote => IsProcessors && CurrentNumber > owner.Processors.Available
		? $"{CurrentNumber} is more than the {owner.Processors.Available} this computer offers; it works, but gains nothing over All."
		: string.Empty;

	private int CurrentNumber => int.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

	// ── Placeholders ─────────────────────────────────────────────────────────

	public bool HasPlaceholders { get; private set; }

	/// <summary>Whether this setting resolves placeholders; if not they stay as written.</summary>
	public bool PlaceholdersResolved => Placeholders.IsResolved(Info.Path);

	public IReadOnlyList<PlaceholderLineVm> PlaceholderLines { get; private set; } = [];

	public string PlaceholderNote => PlaceholdersResolved
		? (owner.SiteDrafts.Count == 0 ? "Add a site to see what this becomes per site." : "What this becomes for each site:")
		: "{tenant} and {productiongroup} are not resolved in this setting: they stay exactly as written.";

	// ── Lock ─────────────────────────────────────────────────────────────────

	/// <summary>Unlocks: what the app uses now goes into the file, and the field becomes editable.</summary>
	public void Unlock()
	{
		if (IsSet || !owner.CanEdit)
		{
			return;
		}

		var value = SettingsCatalog.EffectiveDefaultOf(Info.Path)
			?? (Info.Kind is SettingKind.Lines or SettingKind.Json ? new JsonArray() : null);
		owner.SetValue(Info.Path, value);
		KeepShown = true;
		LoadFromDocument();
	}

	/// <summary>Locks: the entry leaves the file and the default applies again (the view confirms first).</summary>
	public void Lock()
	{
		if (!IsSet || Info.Required || !owner.CanEdit)
		{
			return;
		}

		owner.RemoveValue(Info.Path);
		Error = string.Empty;
		LoadFromDocument();
	}

	/// <summary>Re-reads the value from the document.</summary>
	public void Refresh() => LoadFromDocument();

	internal void RaiseEditable()
	{
		Raise(nameof(IsEditable));
		Raise(nameof(SliderEnabled));
		table?.RaiseEditable();
		entries?.RaiseEditable();
		boilerplate?.RaiseEditable();
		chips?.RaiseEditable();
		triageModes?.RaiseEditable();
		suppressions?.RaiseEditable();
		patterns?.RaiseEditable();
	}

	/// <summary>An editor changed the value: stored, and the row's extras follow.</summary>
	internal void ApplyEditor(JsonNode value) => ApplyTable(value);

	/// <summary>An amber note for a table row: languages that leave a path unchecked, bundle files not on disk.</summary>
	internal string? NoteFor(TableSpec spec, TableRow data)
	{
		if (ReferenceEquals(spec, TableSpecs.PageLanguageOverrides) && !SettingTables.IsBlank(spec, data))
		{
			var installed = SettingTables.SplitValues(spec, data["Values"])
				.Where(l => owner.BundleLanguages.Contains(l, StringComparer.OrdinalIgnoreCase));
			return installed.Any() ? null : "Pages under this path are not spell-checked.";
		}

		if (ReferenceEquals(spec, TableSpecs.DictionaryBundles))
		{
			var missing = SettingEditors.MissingBundleFiles(data["DicFile"], data["AffFile"]);
			return missing.Count == 0 ? null : "File not on disk: " + string.Join(", ", missing) + " — the start-up checks will report it.";
		}

		return null;
	}

	/// <summary>The installed languages changed: language checkboxes, notes and the default-language choices follow.</summary>
	internal void RefreshLanguages()
	{
		table?.RefreshLanguages();
		Raise(nameof(Choices));
		Raise(nameof(ChoiceNote));
	}

	/// <summary>The usage numbers or the screen mask changed.</summary>
	internal void RefreshUsage()
	{
		entries?.RefreshUsage();
		table?.RefreshUsage();
		boilerplate?.RefreshUsage();
		suppressions?.RefreshUsage();
		patterns?.RefreshUsage();
	}

	/// <summary>The table changed a value: stored, and the row's extras follow.</summary>
	internal void ApplyTable(JsonNode value)
	{
		if (IsSet)
		{
			owner.SetValue(Info.Path, value);
			RefreshPlaceholders();
		}

		Raise(nameof(HasTableProblems));
	}

	/// <summary>Recomputes what depends on other settings or the sites.</summary>
	internal void RefreshExtras()
	{
		RefreshFreeSpace();
		RefreshPlaceholders();
		Raise(nameof(ChoiceNote));
		Raise(nameof(HasCard));
		Raise(nameof(CardText));
		Raise(nameof(CardTone));
		Raise(nameof(SliderValue));
		Raise(nameof(SliderText));
		Raise(nameof(SliderMaximum));
		Raise(nameof(ProcessorNote));
	}

	private JsonNode? CurrentValue => IsSet ? owner.ValueOf(Info.Path) : SettingsCatalog.EffectiveDefaultOf(Info.Path);

	private void LoadFromDocument()
	{
		var value = CurrentValue;
		table = null;
		entries = null;
		boilerplate = null;
		chips = null;
		triageModes = null;
		suppressions = null;
		patterns = null;
		if (IsSwitch)
		{
			isOn = value is JsonValue v && v.TryGetValue<bool>(out var b) && b;
			Raise(nameof(IsOn));
		}
		else if (Info.Editor is SettingEditor.FileKinds or SettingEditor.HeadingClasses && SettingEditors.FitsPlainList(value))
		{
			chips = new ChipSetVm(Info.Editor, value, this);
		}
		else if (Info.Editor == SettingEditor.TriageModes && SettingEditors.FitsTextMap(value))
		{
			triageModes = new TriageModesEditorVm(value, this);
		}
		else if (Info.Editor == SettingEditor.Suppressions && SettingEditors.FitsObjectList(value))
		{
			suppressions = new SuppressionsVm(value, this);
		}
		else if (Info.Editor == SettingEditor.Patterns && SettingEditors.FitsObjectList(value))
		{
			patterns = new PatternsVm(value, this);
		}
		else if ((Info.Editor is SettingEditor.TagChips or SettingEditor.Xpaths or SettingEditor.Attributes or SettingEditor.ScriptStrings && SettingEditors.FitsEntries(value))
			|| (Info.Editor is SettingEditor.KnownParts or SettingEditor.ContentPaths && SettingEditors.FitsEntries(value)))
		{
			entries = new EntryListVm(
				Info.Editor switch
				{
					SettingEditor.TagChips => EntryStyle.Tags,
					SettingEditor.Xpaths => EntryStyle.Xpaths,
					SettingEditor.ScriptStrings => EntryStyle.ScriptStrings,
					SettingEditor.KnownParts => EntryStyle.KnownParts,
					SettingEditor.ContentPaths => EntryStyle.ContentPaths,
					_ => EntryStyle.Attributes,
				},
				value,
				this);
		}
		else if (Info.Editor == SettingEditor.Boilerplate && SettingEditors.FitsBoilerplate(value))
		{
			boilerplate = new BoilerplateVm(value, this);
		}
		else if (Info.EffectiveTable is { } spec && SettingTables.Fits(spec, value))
		{
			table = new SettingTableVm(spec, value, this);
		}
		else
		{
			text = SettingValues.ToText(Info, value);
			Raise(nameof(Text));
		}

		Raise(nameof(Table));
		Raise(nameof(IsTable));
		Raise(nameof(Entries));
		Raise(nameof(IsEntryList));
		Raise(nameof(Boilerplate));
		Raise(nameof(IsBoilerplate));
		Raise(nameof(Chips));
		Raise(nameof(IsChipSet));
		Raise(nameof(TriageModes));
		Raise(nameof(IsTriageModes));
		Raise(nameof(Suppressions));
		Raise(nameof(IsSuppressions));
		Raise(nameof(Patterns));
		Raise(nameof(IsPatterns));
		Raise(nameof(Choices));
		Raise(nameof(ChoiceNote));
		Raise(nameof(IsMultiline));
		Raise(nameof(HasTableProblems));
		Raise(nameof(RequiredProblem));
		Raise(nameof(HasRequiredProblem));
		Raise(nameof(ShowsRequired));
		Raise(nameof(IsSet));
		Raise(nameof(IsEditable));
		RefreshExtras();
	}

	private void RefreshFreeSpace()
	{
		if (!HasFreeSpace)
		{
			return;
		}

		var folder = AppPaths.Resolve(Text);
		var space = folder.Length == 0 ? null : MachineInfo.SpaceOf(folder);
		HasDrive = space is { Total: > 0 };
		if (space is { Total: > 0 } s)
		{
			long limit = owner.IntOf("MaxCrawlDownloadGB");
			var rating = FreeSpace.Rate(s.Free, limit);
			var drive = owner.MaskActive ? "the drive" : s.Root;
			FreeSpaceText = $"{FreeSpace.Size(s.Free)} free of {FreeSpace.Size(s.Total)} on {drive}";
			FreeSpaceExplain = FreeSpace.Explain(rating, limit);
			FreeSpaceRating = rating.ToString();
			UsedShare = Math.Clamp(1.0 - (double)s.Free / s.Total, 0, 1);
		}
		else
		{
			FreeSpaceText = folder.Length == 0 ? "No folder set." : "This path points at no drive that answers.";
			FreeSpaceExplain = string.Empty;
			FreeSpaceRating = nameof(SpaceRating.Unrated);
			UsedShare = 0;
		}

		Raise(nameof(HasDrive));
		Raise(nameof(FreeSpaceText));
		Raise(nameof(FreeSpaceExplain));
		Raise(nameof(FreeSpaceRating));
		Raise(nameof(UsedShare));
	}

	private void RefreshPlaceholders()
	{
		var value = CurrentValue;
		HasPlaceholders = Placeholders.HasAny(value);
		PlaceholderLines = HasPlaceholders && PlaceholdersResolved
			? Placeholders.Resolve(value, owner.SiteDrafts, Info.Path)
				.Select(l => new PlaceholderLineVm(owner.MaskActive ? l.MaskedLabel : l.SiteName,
					l.NotUsed ? "not used — this site does not use the CMS content list" : string.Join(Environment.NewLine, l.Values)))
				.ToList()
			: [];
		Raise(nameof(HasPlaceholders));
		Raise(nameof(PlaceholderLines));
		Raise(nameof(PlaceholderNote));
	}
}

/// <summary>One site's line on a placeholder card.</summary>
public sealed record PlaceholderLineVm(string Site, string Values);
