using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using CrawlerSuite.Core.Settings;

namespace CrawlerSuite.Run;

/// <summary>A switchable chip: a file kind, or a heading level.</summary>
public sealed class ToggleChipVm : Observable
{
	private readonly Action changed;
	private bool isOn;

	public ToggleChipVm(string name, string display, string small, bool isOn, Func<bool> canEdit, Action changed)
	{
		Name = name;
		Display = display;
		Small = small;
		this.isOn = isOn;
		CanEditFunc = canEdit;
		this.changed = changed;
	}

	public string Name { get; }

	/// <summary>What the chip shows: a kind's name, or a class with its dot.</summary>
	public string Display { get; }

	/// <summary>The small print under the name: the extensions it stands for.</summary>
	public string Small { get; }

	public bool HasSmall => Small.Length > 0;

	internal Func<bool> CanEditFunc { get; }

	public bool CanEdit => CanEditFunc();

	public bool IsOn
	{
		get => isOn;
		set
		{
			if (Set(ref isOn, value))
			{
				changed();
			}
		}
	}

	internal void RaiseEditable() => Raise(nameof(CanEdit));
}

/// <summary>An editable chip: an extension typed by hand, or a class name.</summary>
public sealed class FreeChipVm : Observable
{
	private readonly Action<FreeChipVm> changed;
	private readonly Func<string, string?> check;
	private string value;
	private bool isEditing;

	public FreeChipVm(string value, string prefix, Func<string, string?> check, Func<bool> canEdit, Action<FreeChipVm> changed)
	{
		this.value = value;
		Prefix = prefix;
		this.check = check;
		CanEditFunc = canEdit;
		this.changed = changed;
	}

	public string Prefix { get; }

	/// <summary>Takes the chip out of its list.</summary>
	internal Action<FreeChipVm>? Removing { get; init; }

	public void Remove() => Removing?.Invoke(this);

	internal Func<bool> CanEditFunc { get; }

	public bool CanEdit => CanEditFunc();

	public string Display => Prefix + value;

	public string Value
	{
		get => value;
		set
		{
			this.value = (value ?? string.Empty).Trim().TrimStart(Prefix.Length > 0 ? Prefix[0] : '\0');
			IsEditing = false;
			Raise();
			Raise(nameof(Display));
			Raise(nameof(Problem));
			Raise(nameof(HasProblem));
			changed(this);
		}
	}

	public bool IsEditing
	{
		get => isEditing;
		set
		{
			if (Set(ref isEditing, value))
			{
				Raise(nameof(IsShowing));
			}
		}
	}

	public bool IsShowing => !isEditing;

	public string Problem => value.Length == 0 ? string.Empty : check(value) ?? string.Empty;

	public bool HasProblem => Problem.Length > 0;

	internal void RaiseEditable() => Raise(nameof(CanEdit));
}

/// <summary>
/// Fixed choices as switchable chips plus free chips for anything else — the embedded-data
/// file kinds (Pages, Scripts …) and the heading classes (h1–h6 and the site's own).
/// </summary>
public sealed class ChipSetVm : Observable
{
	private readonly SettingRowVm row;
	private readonly bool kinds;

	public ChipSetVm(SettingEditor editor, JsonNode? value, SettingRowVm row)
	{
		this.row = row;
		kinds = editor == SettingEditor.FileKinds;
		var entries = SettingEditors.ReadEntries(value).Select(e => e.Value.Trim()).Where(e => e.Length > 0).ToList();
		var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (kinds)
		{
			foreach (var kind in SettingEditors.FileKinds)
			{
				bool on = kind.Extensions.Any(x => entries.Contains(x, StringComparer.OrdinalIgnoreCase));
				Fixed.Add(new ToggleChipVm(kind.Name, kind.Name, string.Join(" ", kind.Extensions), on, () => CanEdit, Changed));
				known.UnionWith(kind.Extensions);
			}
		}
		else
		{
			foreach (var level in SettingEditors.HeadingLevels)
			{
				Fixed.Add(new ToggleChipVm(level, "." + level, string.Empty, entries.Contains(level, StringComparer.Ordinal), () => CanEdit, Changed));
				known.Add(level);
			}
		}

		foreach (var other in entries.Where(e => !known.Contains(e)))
		{
			Others.Add(NewFree(kinds ? other.TrimStart('.') : other));
		}

		Adder = new ChipAddVm(AddText, this);
		foreach (var item in Fixed.Cast<object>().Concat(Others).Append(Adder))
		{
			Items.Add(item);
		}
	}

	/// <summary>Everything in one row of equal-width chips: the fixed ones, the free ones, then the add button.</summary>
	public ObservableCollection<object> Items { get; } = [];

	public ChipAddVm Adder { get; }

	public ObservableCollection<ToggleChipVm> Fixed { get; } = [];

	public ObservableCollection<FreeChipVm> Others { get; } = [];

	public bool IsKinds => kinds;

	public bool CanEdit => row.IsEditable;

	public string AddText => kinds ? "+ Other" : "+ Add class";

	/// <summary>The explanation beside the kinds.</summary>
	public string Explanation => kinds
		? "Files are typed by their content when they are downloaded, not by their address: a page from .aspx, .jsp or an address without an extension counts as Pages."
		: "Elements that carry one of these classes (class=\"h2\") but are not real headings are reported as styled headings. Classes match case-sensitively, as in browsers.";

	public bool HasExplanation => Explanation.Length > 0;

	/// <summary>Amber when nothing is selected: the scan or check is off.</summary>
	public string OffNote => Fixed.Any(f => f.IsOn) || Others.Any(o => o.Value.Length > 0)
		? string.Empty
		: kinds ? "The embedded-data scan is off." : "The styled-heading check is off.";

	public bool HasProblems => Others.Any(o => o.HasProblem);

	public IReadOnlyList<string> ProblemLines => Others.Where(o => o.HasProblem).Select(o => $"{o.Display}: {o.Problem}").ToList();

	public void Add()
	{
		if (CanEdit)
		{
			var chip = new FreeChipVm(string.Empty, ".", Check, () => CanEdit, OtherChanged) { IsEditing = true, Removing = Remove };
			Others.Add(chip);
			Items.Insert(Items.IndexOf(Adder), chip);
		}
	}

	public void Remove(FreeChipVm chip)
	{
		if (CanEdit)
		{
			Others.Remove(chip);
			Items.Remove(chip);
			Changed();
		}
	}

	internal void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		Adder.RaiseEditable();
		foreach (var f in Fixed)
		{
			f.RaiseEditable();
		}

		foreach (var o in Others)
		{
			o.RaiseEditable();
		}
	}

	private FreeChipVm NewFree(string value) => new(value, ".", Check, () => CanEdit, OtherChanged) { Removing = Remove };

	private string? Check(string value) => kinds
		? SettingEditors.CheckExtension("." + value.TrimStart('.'))
		: SettingEditors.CheckClassName(value);

	private void OtherChanged(FreeChipVm chip)
	{
		if (chip.Value.Length == 0)
		{
			Others.Remove(chip);
			Items.Remove(chip);
		}

		Changed();
	}

	private void Changed()
	{
		var list = new List<string>();
		if (kinds)
		{
			foreach (var (kind, chip) in SettingEditors.FileKinds.Zip(Fixed))
			{
				if (chip.IsOn)
				{
					list.AddRange(kind.Extensions);
				}
			}

			list.AddRange(Others.Where(o => o.Value.Length > 0 && !o.HasProblem).Select(o => "." + o.Value.TrimStart('.').ToLowerInvariant()));
		}
		else
		{
			list.AddRange(Fixed.Where(f => f.IsOn).Select(f => f.Name));
			list.AddRange(Others.Where(o => o.Value.Length > 0 && !o.HasProblem).Select(o => o.Value));
		}

		Raise(nameof(OffNote));
		Raise(nameof(HasProblems));
		Raise(nameof(ProblemLines));
		row.ApplyEditor(new JsonArray(list.Distinct(StringComparer.OrdinalIgnoreCase).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));
	}
}

/// <summary>The add button at the end of a chip row.</summary>
public sealed class ChipAddVm : Observable
{
	public ChipAddVm(string text, ChipSetVm set)
	{
		Text = text;
		Set = set;
	}

	public string Text { get; }

	public ChipSetVm Set { get; }

	public bool CanEdit => Set.CanEdit;

	internal void RaiseEditable() => Raise(nameof(CanEdit));
}

/// <summary>Suppression rules as cards.</summary>
public sealed class SuppressionsVm : Observable
{
	private readonly SettingRowVm row;

	public SuppressionsVm(JsonNode? value, SettingRowVm row)
	{
		this.row = row;
		foreach (var rule in (value as JsonArray ?? []).OfType<JsonObject>())
		{
			Rules.Add(new SuppressionVm(rule, this));
		}
	}

	public ObservableCollection<SuppressionVm> Rules { get; } = [];

	public bool CanEdit => row.IsEditable;

	public bool HasProblems => Rules.Any(r => r.HasProblem);

	internal SettingsModel Owner => row.Owner;

	public void Add()
	{
		if (CanEdit)
		{
			Rules.Add(new SuppressionVm(new JsonObject { ["Type"] = SettingEditors.SuppressionTypes[0].Code, ["Value"] = string.Empty, ["Comment"] = string.Empty }, this));
			Changed();
		}
	}

	public void Remove(SuppressionVm rule)
	{
		if (CanEdit)
		{
			Rules.Remove(rule);
			Changed();
		}
	}

	internal void Changed()
	{
		Raise(nameof(HasProblems));
		row.ApplyEditor(new JsonArray(Rules.Select(r => (JsonNode?)r.ToJson()).ToArray()));
	}

	internal void RefreshUsage()
	{
		foreach (var rule in Rules)
		{
			rule.RefreshUsage();
		}
	}

	internal void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		foreach (var rule in Rules)
		{
			rule.RaiseEditable();
		}
	}
}

/// <summary>One suppression rule.</summary>
public sealed class SuppressionVm : Observable
{
	private readonly JsonObject original;
	private readonly SuppressionsVm owner;
	private string code;
	private string value;
	private string comment;
	private bool enabled;

	public SuppressionVm(JsonObject original, SuppressionsVm owner)
	{
		this.original = (JsonObject)original.DeepClone();
		this.owner = owner;
		code = Text("Type");
		value = Text("Value");
		comment = Text("Comment");
		enabled = !(original["Enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && !on);
		HasPages = original["Pages"] is JsonArray { Count: > 0 };
		RefreshUsage();
	}

	public SuppressionsVm Owner => owner;

	/// <summary>The plain names; an unknown code from the file is listed as itself, so it shows.</summary>
	public IReadOnlyList<string> TypeNames => SettingEditors.SuppressionName(code) is null
		? [.. SettingEditors.SuppressionTypes.Select(t => t.Name), code]
		: SettingEditors.SuppressionTypes.Select(t => t.Name).ToList();

	public string TypeName
	{
		get => SettingEditors.SuppressionName(code) ?? code;
		set
		{
			var chosen = SettingEditors.SuppressionTypes.FirstOrDefault(t => t.Name == value).Code;
			if (chosen is not null && chosen != code)
			{
				code = chosen;
				Raise();
				Changed();
			}
		}
	}

	/// <summary>The card beside the dropdown: the code the file stores.</summary>
	public string CodeCard => IsKnown
		? $"Stored as {code} — the type as log 10 and the Results page show it."
		: $"{code} is not a finding type this app knows — choose one from the list.";

	public bool IsKnown => SettingEditors.SuppressionName(code) is not null;

	public string Value
	{
		get => value;
		set
		{
			if (Set(ref this.value, value ?? string.Empty))
			{
				Changed();
			}
		}
	}

	/// <summary>Amber when the value is empty: the whole type is suppressed.</summary>
	public string WholeTypeNote => IsKnown && value.Length == 0
		? $"All {SettingEditors.SuppressionName(code) ?? code} findings are suppressed — the type is fully off."
		: string.Empty;

	public string Comment
	{
		get => comment;
		set
		{
			if (Set(ref comment, (value ?? string.Empty).Trim()))
			{
				Changed();
			}
		}
	}

	public bool Enabled
	{
		get => enabled;
		set
		{
			if (Set(ref enabled, value))
			{
				Changed();
			}
		}
	}

	/// <summary>The rule carries page patterns from an older file: kept and applied, not editable here.</summary>
	public bool HasPages { get; }

	public string PagesNote => HasPages
		? "This rule has page patterns from an older file, matched against the end of the page address. They stay in effect but are not edited here; remove the rule and add it again to make it site-wide."
		: string.Empty;

	public string Problem => !IsKnown ? "Choose a finding type." : comment.Length == 0 ? "A comment is needed: why this rule exists." : string.Empty;

	public bool HasProblem => Problem.Length > 0;

	public UsageLine Usage { get; private set; } = UsageLine.None;

	public bool HasUsage => Usage.Kind != UsageKind.None;

	public bool CanEdit => owner.CanEdit;

	internal JsonObject ToJson()
	{
		var obj = (JsonObject)original.DeepClone();
		obj["Type"] = code;
		obj["Value"] = value;
		obj["Comment"] = comment;
		if (enabled)
		{
			obj.Remove("Enabled");
		}
		else
		{
			obj["Enabled"] = false;
		}

		return obj;
	}

	internal void RefreshUsage()
	{
		Usage = enabled
			? UsageText.ForFindings(owner.Owner.Usage, SettingsUsage.Suppressions, SettingsUsage.RuleKey(code, value), suppression: true)
			: UsageLine.Note("Switched off — not applied, so nothing was suppressed.");
		Raise(nameof(Usage));
		Raise(nameof(HasUsage));
	}

	internal void RaiseEditable() => Raise(nameof(CanEdit));

	private void Changed()
	{
		Raise(nameof(CodeCard));
		Raise(nameof(IsKnown));
		Raise(nameof(TypeNames));
		Raise(nameof(WholeTypeNote));
		Raise(nameof(Problem));
		Raise(nameof(HasProblem));
		RefreshUsage();
		owner.Changed();
	}

	private string Text(string key) => original[key] is JsonValue v && v.TryGetValue<string>(out var t) ? t : string.Empty;
}

/// <summary>Unwanted-pattern groups as cards.</summary>
public sealed class PatternsVm : Observable
{
	private readonly SettingRowVm row;

	public PatternsVm(JsonNode? value, SettingRowVm row)
	{
		this.row = row;
		foreach (var group in (value as JsonArray ?? []).OfType<JsonObject>())
		{
			Groups.Add(new PatternGroupVm(group, this));
		}

		RefreshLinks();
	}

	public ObservableCollection<PatternGroupVm> Groups { get; } = [];

	public IReadOnlyList<string> Purposes => SettingEditors.PatternPurposes().Select(p => p.Name).ToList();

	public bool CanEdit => row.IsEditable;

	public bool HasProblems => Groups.Any(g => g.HasProblem);

	internal SettingsModel Owner => row.Owner;

	public void Add(string purpose)
	{
		if (!CanEdit || SettingEditors.PatternPurposes().FirstOrDefault(p => p.Name == purpose) is not { } chosen)
		{
			return;
		}

		var group = (JsonObject)chosen.Group.DeepClone();
		group["Category"] = chosen.Category;
		group["Name"] = UniqueName(chosen.Name);
		if (chosen.Security)
		{
			group["SecurityRelevant"] = true;
		}

		Groups.Add(new PatternGroupVm(group, this) { IsNew = true });
		Changed();
	}

	public void Remove(PatternGroupVm group)
	{
		if (CanEdit)
		{
			Groups.Remove(group);
			Changed();
		}
	}

	internal void Changed()
	{
		RefreshLinks();
		Raise(nameof(HasProblems));
		row.ApplyEditor(new JsonArray(Groups.Select(g => (JsonNode?)g.ToJson()).ToArray()));
	}

	internal IReadOnlyList<string> NamesOtherThan(PatternGroupVm group) =>
		Groups.Where(g => g != group && g.Name.Length > 0).Select(g => g.Name).ToList();

	internal void RefreshUsage()
	{
		foreach (var group in Groups)
		{
			group.RefreshUsage();
		}
	}

	internal void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		foreach (var group in Groups)
		{
			group.RaiseEditable();
		}
	}

	private void RefreshLinks()
	{
		foreach (var group in Groups)
		{
			group.RefreshLinks(Groups.Where(g => g != group && g.Reference == group.Name && group.Name.Length > 0).Select(g => g.Name).ToList());
		}
	}

	private string UniqueName(string name)
	{
		var taken = new HashSet<string>(Groups.Select(g => g.Name), StringComparer.Ordinal);
		if (!taken.Contains(name))
		{
			return name;
		}

		int n = 2;
		while (taken.Contains($"{name} {n}"))
		{
			n++;
		}

		return $"{name} {n}";
	}
}

/// <summary>One unwanted-pattern group, shown by its shape.</summary>
public sealed class PatternGroupVm : Observable
{
	private readonly JsonObject group;
	private readonly PatternsVm owner;
	private IReadOnlyList<string> foldedBy = [];
	private bool showMore;

	public PatternGroupVm(JsonObject original, PatternsVm owner)
	{
		group = (JsonObject)original.DeepClone();
		this.owner = owner;
		foreach (var pattern in Patterns())
		{
			Markers.Add(NewMarker(pattern));
		}

		showMore = ExcludeHref || ExcludeUrl || !CheckScript || !CheckStyle;
		RefreshUsage();
	}

	/// <summary>Added in this session.</summary>
	public bool IsNew { get; init; }

	public PatternsVm Owner => owner;

	public bool CanEdit => owner.CanEdit;

	// ── Shape ───────────────────────────────────────────────────────────────

	private static readonly IReadOnlyList<string> ShapeNames = ["Words and markers", "Placeholder", "Template binding"];

	public IReadOnlyList<string> Shapes => ShapeNames;

	public SettingEditors.PatternShape ShapeValue => SettingEditors.ShapeOf(group);

	public string Shape
	{
		get => Shapes[(int)ShapeValue];
		set
		{
			var target = (SettingEditors.PatternShape)Math.Max(0, Shapes.ToList().IndexOf(value ?? string.Empty));
			if (target == ShapeValue)
			{
				return;
			}

			switch (target)
			{
				case SettingEditors.PatternShape.Words:
					group["GroupPatterns"] = false;
					group.Remove("OnlyFlagUnbalanced");
					group.Remove("Reference");
					break;
				case SettingEditors.PatternShape.Placeholder:
					group["GroupPatterns"] = true;
					group.Remove("OnlyFlagUnbalanced");
					EnsurePair();
					break;
				default:
					group["GroupPatterns"] = true;
					group["OnlyFlagUnbalanced"] = true;
					group.Remove("Reference");
					EnsurePair();
					break;
			}

			ReloadMarkers();
			Changed();
		}
	}

	public bool IsWords => ShapeValue == SettingEditors.PatternShape.Words;

	public bool IsPair => !IsWords;

	public bool IsPlaceholder => ShapeValue == SettingEditors.PatternShape.Placeholder;

	// ── Fields ──────────────────────────────────────────────────────────────

	public string Name
	{
		get => Text("Name");
		set
		{
			var old = Name;
			var renamed = (value ?? string.Empty).Trim();
			if (old.Length > 0 && renamed != old)
			{
				// Groups that fold this one follow the new name, so the link does not break.
				foreach (var other in owner.Groups.Where(g => g != this && g.Reference == old))
				{
					other.FollowRename(renamed);
				}
			}

			SetText("Name", renamed);
		}
	}

	public string Category
	{
		get => Text("Category");
		set => SetText("Category", value);
	}

	public IReadOnlyList<string> Categories =>
		SettingEditors.PatternCategories.Concat(owner.Groups.Select(g => g.Category)).Where(c => c.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

	public ObservableCollection<FreeChipVm> Markers { get; } = [];

	public string Opener
	{
		get => Patterns().ElementAtOrDefault(0) ?? string.Empty;
		set => SetPair(0, value);
	}

	public string Closer
	{
		get => Patterns().ElementAtOrDefault(1) ?? string.Empty;
		set => SetPair(1, value);
	}

	public bool CaseSensitive { get => Bool("CaseSensitive"); set => SetBool("CaseSensitive", value, false); }

	/// <summary>All markers on a page are one finding (GroupPatterns, words shape).</summary>
	public bool OneFindingPerPage { get => Bool("GroupPatterns"); set => SetBool("GroupPatterns", value, false); }

	public bool ExcludeHref { get => Bool("ExcludeHref"); set => SetBool("ExcludeHref", value, false); }

	public bool ExcludeUrl { get => Bool("ExcludeUrl"); set => SetBool("ExcludeUrl", value, false); }

	public bool CheckScript { get => Bool("CheckScript", true); set => SetBool("CheckScript", value, true); }

	public bool CheckStyle { get => Bool("CheckStyle", true); set => SetBool("CheckStyle", value, true); }

	public bool Enabled { get => Bool("Enabled", true); set => SetBool("Enabled", value, true); }

	public bool SecurityRelevant { get => Bool("SecurityRelevant"); set => SetBool("SecurityRelevant", value, false); }

	public bool ShowMore { get => showMore; set => Set(ref showMore, value); }

	/// <summary>The fold reference: "(none)" or another group's name.</summary>
	public IReadOnlyList<string> ReferenceChoices
	{
		get
		{
			var names = owner.NamesOtherThan(this);
			// A broken reference stays listed, so the dropdown shows it (red) rather than going blank.
			return Reference.Length > 0 && !names.Contains(Reference) ? ["(none)", .. names, Reference] : ["(none)", .. names];
		}
	}

	public string Reference
	{
		get => Text("Reference");
		set
		{
			var v = value == "(none)" ? string.Empty : value ?? string.Empty;
			if (v.Length == 0)
			{
				group.Remove("Reference");
			}
			else
			{
				group["Reference"] = v;
			}

			Changed();
		}
	}

	internal void FollowRename(string renamed)
	{
		group["Reference"] = renamed;
		Raise(nameof(Reference));
		Raise(nameof(ReferenceShown));
	}

	/// <summary>The dropdown's value: "(none)" or the group to fold.</summary>
	public string ReferenceShown
	{
		get => Reference.Length == 0 ? "(none)" : Reference;
		set
		{
			if (value is not null && value != ReferenceShown)
			{
				Reference = value;
			}
		}
	}

	public bool ShowsFoldField => IsPlaceholder && (Reference.Length > 0 || foldRequested);

	private bool foldRequested;

	public void RequestFold()
	{
		foldRequested = true;
		Raise(nameof(ShowsFoldField));
		Raise(nameof(ShowsFoldButton));
	}

	public bool ShowsFoldButton => IsPlaceholder && !ShowsFoldField;

	// ── What it does ───────────────────────────────────────────────────────

	public string Explanation => SettingEditors.ExplainPattern(group, foldedBy);

	/// <summary>What stands in the way of saving, or empty.</summary>
	public string Problem
	{
		get
		{
			var patterns = Patterns();
			if (Name.Trim().Length == 0)
			{
				return "A name is needed.";
			}

			if (owner.Groups.Count(g => g.Name == Name) > 1)
			{
				return "Another group has this name.";
			}

			if (IsPair && (patterns.Count != 2 || patterns.Any(p => p.Length == 0)))
			{
				return "An opener and a closer are needed.";
			}

			if (IsWords && patterns.Count == 0)
			{
				return "At least one marker is needed.";
			}

			if (Reference.Length > 0 && !owner.Groups.Any(g => g != this && g.Name == Reference))
			{
				return $"The group to fold, {Reference}, does not exist (renamed or removed).";
			}

			return string.Empty;
		}
	}

	public bool HasProblem => Problem.Length > 0;

	public UsageLine Usage { get; private set; } = UsageLine.None;

	public bool HasUsage => Usage.Kind != UsageKind.None;

	internal JsonObject ToJson() => (JsonObject)group.DeepClone();

	internal void RefreshLinks(IReadOnlyList<string> folders)
	{
		foldedBy = folders;
		Raise(nameof(Explanation));
		Raise(nameof(ReferenceChoices));
		Raise(nameof(Problem));
		Raise(nameof(HasProblem));
	}

	internal void RefreshUsage()
	{
		Usage = Enabled
			? UsageText.ForFindings(owner.Owner.Usage, SettingsUsage.Patterns, Name, suppression: false)
			: UsageLine.Note("Switched off — not searched, so not counted.");
		Raise(nameof(Usage));
		Raise(nameof(HasUsage));
	}

	public void AddMarker()
	{
		if (CanEdit)
		{
			var marker = NewMarker(string.Empty);
			marker.IsEditing = true;
			Markers.Add(marker);
		}
	}

	public void RemoveMarker(FreeChipVm marker)
	{
		if (CanEdit)
		{
			Markers.Remove(marker);
			WriteMarkers();
		}
	}

	internal void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		foreach (var m in Markers)
		{
			m.RaiseEditable();
		}
	}

	private FreeChipVm NewMarker(string pattern) =>
		new(pattern, string.Empty, p => p.Length == 0 ? "A marker is needed." : null, () => CanEdit, MarkerChanged) { Removing = RemoveMarker };

	private void MarkerChanged(FreeChipVm marker)
	{
		if (marker.Value.Length == 0)
		{
			Markers.Remove(marker);
		}

		WriteMarkers();
	}

	private void WriteMarkers()
	{
		group["Patterns"] = new JsonArray(Markers.Where(m => m.Value.Length > 0).Select(m => (JsonNode?)JsonValue.Create(m.Value)).ToArray());
		Changed();
	}

	private void SetPair(int index, string? value)
	{
		var patterns = Patterns().ToList();
		while (patterns.Count < 2)
		{
			patterns.Add(string.Empty);
		}

		patterns[index] = value ?? string.Empty;
		group["Patterns"] = new JsonArray(patterns.Take(2).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
		ReloadMarkers();
		Changed();
	}

	private void EnsurePair()
	{
		var patterns = Patterns().Take(2).ToList();
		while (patterns.Count < 2)
		{
			patterns.Add(string.Empty);
		}

		group["Patterns"] = new JsonArray(patterns.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
	}

	private void ReloadMarkers()
	{
		Markers.Clear();
		foreach (var pattern in Patterns())
		{
			Markers.Add(NewMarker(pattern));
		}
	}

	private IReadOnlyList<string> Patterns() =>
		(group["Patterns"] as JsonArray ?? []).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var t) ? t : string.Empty).ToList();

	private string Text(string key) => group[key] is JsonValue v && v.TryGetValue<string>(out var t) ? t : string.Empty;

	private bool Bool(string key, bool absent = false) => group[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : absent;

	private void SetText(string key, string? value)
	{
		group[key] = (value ?? string.Empty).Trim();
		Changed();
	}

	/// <summary>Writes a switch; at its default it leaves the file, so the entry only says what differs.</summary>
	private void SetBool(string key, bool value, bool absent)
	{
		if (value == absent)
		{
			group.Remove(key);
		}
		else
		{
			group[key] = value;
		}

		Changed();
	}

	private void Changed()
	{
		foreach (var name in new[]
		{
			nameof(Name), nameof(Category), nameof(Shape), nameof(IsWords), nameof(IsPair), nameof(IsPlaceholder), nameof(Opener), nameof(Closer),
			nameof(CaseSensitive), nameof(OneFindingPerPage), nameof(ExcludeHref), nameof(ExcludeUrl), nameof(CheckScript), nameof(CheckStyle),
			nameof(Enabled), nameof(SecurityRelevant), nameof(Reference), nameof(ReferenceShown), nameof(ShowsFoldField), nameof(ShowsFoldButton),
			nameof(Explanation), nameof(Problem), nameof(HasProblem),
		})
		{
			Raise(name);
		}

		RefreshUsage();
		owner.Changed();
	}
}
