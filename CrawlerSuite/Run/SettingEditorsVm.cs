using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using CrawlerSuite.Core.Settings;
using CrawlerSuite.Core.SpellCheck;

namespace CrawlerSuite.Run;

/// <summary>How an entry list shows its entries.</summary>
public enum EntryStyle
{
	/// <summary>&lt;tag&gt; chips in a wrapping row.</summary>
	Tags,

	/// <summary>XPaths, one per line, coloured, with a description and usage.</summary>
	Xpaths,

	/// <summary>Attribute names, one per line, with usage.</summary>
	Attributes,

	/// <summary>Whole script strings, one per line, with how often each was skipped.</summary>
	ScriptStrings,

	/// <summary>Known word parts, one per line, switchable, with how many words each let pass (or, switched off, would).</summary>
	KnownParts,

	/// <summary>Content-list paths to leave out, one per line, switchable, with how many rows of the export each matched.</summary>
	ContentPaths,
}

/// <summary>
/// A list setting with an editor of its own: tag chips, or XPaths and attributes that can each
/// be switched off and show how often they matched in the primary site's last run.
/// </summary>
public sealed class EntryListVm : Observable
{
	private readonly SettingRowVm row;

	public EntryListVm(EntryStyle style, JsonNode? value, SettingRowVm row)
	{
		Style = style;
		this.row = row;
		foreach (var entry in SettingEditors.ReadEntries(value))
		{
			Entries.Add(new EntryVm(entry.Value, entry.Enabled, this));
		}

		Check();
	}

	public EntryStyle Style { get; }

	public bool IsTags => Style == EntryStyle.Tags;

	public bool IsLines => Style != EntryStyle.Tags;

	public bool IsXpaths => Style == EntryStyle.Xpaths;

	/// <summary>Entries can be switched off (the list keeps <c>{"Value", "Enabled"}</c> entries).</summary>
	public bool CanSwitch => true;

	public ObservableCollection<EntryVm> Entries { get; } = [];

	public bool CanEdit => row.IsEditable;

	public bool HasProblems => Entries.Any(e => e.HasProblem);

	/// <summary>The problems of the entries, one line each ("&lt;x&gt;: …") — for the chips, which have no room for them.</summary>
	public IReadOnlyList<string> ProblemLines => Entries.Where(e => e.HasProblem).Select(e => $"{e.Display}: {e.Problem}").ToList();

	public bool IsEmpty => Entries.Count == 0;

	public string AddText => IsTags ? "+ New" : "Add entry";

	internal SettingsModel Owner => row.Owner;

	public void Add()
	{
		if (!CanEdit)
		{
			return;
		}

		var entry = new EntryVm(string.Empty, true, this) { IsEditing = true };
		Entries.Add(entry);
		Raise(nameof(IsEmpty));
	}

	public void Remove(EntryVm entry)
	{
		if (!CanEdit)
		{
			return;
		}

		Entries.Remove(entry);
		Changed();
	}

	/// <summary>Usage numbers or the screen mask changed: every line is redrawn.</summary>
	internal void RefreshUsage()
	{
		foreach (var entry in Entries)
		{
			entry.RefreshUsage();
		}
	}

	internal void Changed()
	{
		// An entry added and left empty is no entry.
		foreach (var empty in Entries.Where(e => !e.IsEditing && e.Value.Trim().Length == 0).ToList())
		{
			Entries.Remove(empty);
		}

		Check();
		Raise(nameof(IsEmpty));
		row.ApplyEditor(SettingEditors.WriteEntries(Entries.Select(e => new SwitchEntry(e.Value, e.Enabled))));
	}

	internal void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		foreach (var entry in Entries)
		{
			entry.RaiseEditable();
		}
	}

	internal string? CheckValue(string value) => Style switch
	{
		EntryStyle.Tags => SettingEditors.CheckTagName(value),
		EntryStyle.Xpaths => SettingEditors.CheckXpath(value),
		EntryStyle.ScriptStrings => null,
		EntryStyle.KnownParts => value.Trim().Any(char.IsWhiteSpace) ? "A word part has no spaces." : null,
		EntryStyle.ContentPaths => null,
		_ => value.Trim().Length == 0 || value.Trim().Any(char.IsWhiteSpace) ? "An attribute name has no spaces." : null,
	};

	internal string UsageSetting => Style == EntryStyle.Xpaths ? SettingsUsage.Xpaths : SettingsUsage.Attributes;

	private void Check()
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in Entries)
		{
			var value = entry.Value.Trim();
			entry.SetProblem(value.Length == 0 ? null
				: CheckValue(value) ?? (seen.Add(value) ? null : "Listed twice."));
		}

		Raise(nameof(HasProblems));
		Raise(nameof(ProblemLines));
	}
}

/// <summary>One entry: its text, whether it applies, what is wrong, and how often it matched.</summary>
public sealed class EntryVm : Observable
{
	private readonly EntryListVm list;
	private string value;
	private bool enabled;
	private bool isEditing;
	private string problem = string.Empty;

	public EntryVm(string value, bool enabled, EntryListVm list)
	{
		this.value = value;
		this.enabled = enabled;
		this.list = list;
		RefreshUsage();
	}

	public EntryListVm List => list;

	public string Value
	{
		get => value;
		set
		{
			value = (value ?? string.Empty).Trim();
			if (value == this.value)
			{
				IsEditing = false;
				list.Changed();
				return;
			}

			this.value = value;
			IsEditing = false;
			Raise();
			Raise(nameof(Display));
			Raise(nameof(Pieces));
			Raise(nameof(Description));
			Raise(nameof(HasDescription));
			RefreshUsage();
			list.Changed();
		}
	}

	/// <summary>Switched off: kept in the file, not applied, still counted.</summary>
	public bool Enabled
	{
		get => enabled;
		set
		{
			if (Set(ref enabled, value))
			{
				RefreshUsage();
				list.Changed();
			}
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

	public bool CanEdit => list.CanEdit;

	/// <summary>The entry as shown: &lt;tag&gt; for tags, else as written.</summary>
	public string Display => list.IsTags ? $"<{value}>" : value;

	/// <summary>An XPath in coloured pieces.</summary>
	public IReadOnlyList<XpathPieceVm> Pieces => list.IsXpaths
		? SettingEditors.SplitXpath(value).Select(p => new XpathPieceVm(p.Text, p.Part.ToString())).ToList()
		: [];

	/// <summary>An XPath in plain words, for the common shapes.</summary>
	public string Description => list.IsXpaths ? SettingEditors.DescribeXpath(value) ?? string.Empty : string.Empty;

	public bool HasDescription => Description.Length > 0;

	public string Problem => problem;

	public bool HasProblem => problem.Length > 0;

	public UsageLine Usage { get; private set; } = UsageLine.None;

	public bool HasUsage => Usage.Kind != UsageKind.None;

	internal void SetProblem(string? text)
	{
		problem = text ?? string.Empty;
		Raise(nameof(Problem));
		Raise(nameof(HasProblem));
	}

	internal void RefreshUsage()
	{
		if (list.IsTags)
		{
			return;
		}

		Usage = list.Style switch
		{
			EntryStyle.ScriptStrings => UsageText.ForScriptString(list.Owner.Usage, value),
			EntryStyle.KnownParts => UsageText.ForKnownPart(list.Owner.Usage, value, enabled),
			EntryStyle.ContentPaths => UsageText.ForContentPath(list.Owner.Usage, value, enabled),
			_ => UsageText.For(list.Owner.Usage, list.UsageSetting, value, list.Style == EntryStyle.Xpaths ? "matched" : "carrying it"),
		};
		Raise(nameof(Usage));
		Raise(nameof(HasUsage));
	}

	internal void RaiseEditable() => Raise(nameof(CanEdit));
}

/// <summary>A piece of an XPath and its kind (Axis, Name, Attribute, Function, Text, Punctuation).</summary>
public sealed record XpathPieceVm(string Text, string Kind);

/// <summary>The usage line of an entry, from the primary site's last run.</summary>
internal static class UsageText
{
	/// <param name="what">What the elements did: "matched", "carrying it".</param>
	public static UsageLine For(UsageReport? report, string setting, string key, string what)
	{
		if (key.Trim().Length == 0)
		{
			return UsageLine.None;
		}

		if (report is null)
		{
			return UsageLine.NotRun;
		}

		if (report.Of(setting, key.Trim()) is not { } count)
		{
			return UsageLine.ChangedSince;
		}

		if (count.Pages == 0)
		{
			return UsageLine.Nothing("No match in the last run — this entry may no longer be needed.", report);
		}

		double? share = report.Pages > 0 ? Math.Clamp((double)count.Pages / report.Pages, 0, 1) : null;
		return UsageLine.Counts(report, share,
			UsageLine.N(count.Matches), UsageLine.W($" {UsageLine.Plural(count.Matches, "element", "elements")} {what} on "),
			UsageLine.N(count.Pages), UsageLine.W(" of "), UsageLine.N(report.Pages), UsageLine.W(" pages"));
	}

	/// <summary>A script string to skip: how often it kept a string from the check, in pages and in script files.</summary>
	public static UsageLine ForScriptString(UsageReport? report, string key)
	{
		key = key.Trim();
		if (key.Length == 0)
		{
			return UsageLine.None;
		}

		if (report is null)
		{
			return UsageLine.NotRun;
		}

		var inPages = report.Of(SettingsUsage.ScriptStrings, key);
		var inFiles = report.Of(SettingsUsage.ScriptStringsInFiles, key);
		if (inPages is null && inFiles is null)
		{
			return UsageLine.ChangedSince;
		}

		int total = (inPages?.Matches ?? 0) + (inFiles?.Matches ?? 0);
		if (total == 0)
		{
			return UsageLine.Nothing("Skipped nothing in the last run — this entry may no longer be needed.", report);
		}

		var runs = new List<(string, bool)> { UsageLine.W("Skipped "), UsageLine.N(total), UsageLine.W(UsageLine.Plural(total, " time", " times")) };
		bool both = inPages is { Matches: > 0 } && inFiles is { Matches: > 0 };
		if (both)
		{
			runs.Add(UsageLine.W(": "));
		}

		if (inPages is { Matches: > 0 } p)
		{
			if (both)
			{
				runs.Add(UsageLine.N(p.Matches));
			}

			runs.AddRange([UsageLine.W(" in inline scripts on "), UsageLine.N(p.Pages), UsageLine.W(UsageLine.Plural(p.Pages, " page", " pages"))]);
		}

		if (both)
		{
			runs.Add(UsageLine.W(", "));
		}

		if (inFiles is { Matches: > 0 } f)
		{
			if (both)
			{
				runs.Add(UsageLine.N(f.Matches));
			}

			runs.AddRange([UsageLine.W(" in "), UsageLine.N(f.Pages), UsageLine.W(UsageLine.Plural(f.Pages, " script file", " script files"))]);
		}

		return UsageLine.Counts(report, null, [.. runs]);
	}

	/// <summary>
	/// A Known word part: how many words it let pass, on how many pages, and the first words that
	/// need it — or that it let nothing pass, or that every word it let pass would pass without it.
	/// Switched off: how many of the run's flagged words it would let pass.
	/// </summary>
	/// <summary>A content-list path to leave out: how many rows of the export it matched (switched off: would leave out).</summary>
	public static UsageLine ForContentPath(UsageReport? report, string key, bool enabled)
	{
		key = key.Trim();
		if (key.Length == 0)
		{
			return UsageLine.None;
		}

		if (report is null)
		{
			return UsageLine.NotRun;
		}

		if (ContentPathUsage.RowsRead(report) is not { } rows)
		{
			return UsageLine.Note("No numbers yet: they are counted when the export is read — the next crawl or re-run brings them.");
		}

		if (report.Of(ContentPathUsage.Setting, key) is not { } count)
		{
			return UsageLine.ChangedSince;
		}

		if (count.Pages == 0)
		{
			return UsageLine.Nothing(enabled
				? "Matched no row of the export in the last run — this entry may no longer be needed."
				: "Switched off, and would leave out no row of the export in the last run.", report);
		}

		double? share = rows > 0 ? Math.Clamp((double)count.Pages / rows, 0, 1) : null;
		return UsageLine.Counts(report, share,
			UsageLine.W(enabled ? string.Empty : "Switched off: would leave out "),
			UsageLine.N(count.Pages), UsageLine.W(" of "),
			UsageLine.N(rows), UsageLine.W(enabled ? " rows left out" : " rows"));
	}

	public static UsageLine ForKnownPart(UsageReport? report, string key, bool enabled)
	{
		key = key.Trim();
		if (key.Length == 0)
		{
			return UsageLine.None;
		}

		if (report is null)
		{
			return UsageLine.NotRun;
		}

		if (!enabled)
		{
			if (report.Of(KnownPartTally.SwitchedOffSetting, key) is not { } off)
			{
				return UsageLine.Note("Switched off since the last run — the next run shows what it would let pass.");
			}

			return off.Matches == 0
				? UsageLine.Nothing("Switched off: it would let none of the last run's flagged words pass — it can go.", report)
				: UsageLine.Counts(report, null,
					UsageLine.W("Switched off: it would let "), UsageLine.N(off.Matches),
					UsageLine.W(UsageLine.Plural(off.Matches, " flagged word pass", " flagged words pass")));
		}

		if (report.Of(KnownPartTally.Setting, key) is not { } count)
		{
			return UsageLine.ChangedSince;
		}

		if (count.Pages == 0)
		{
			return UsageLine.Nothing("Let no word pass in the last run — this entry may no longer be needed.", report);
		}

		var words = $"{count.Matches.ToString("N0", CultureInfo.InvariantCulture)} {UsageLine.Plural(count.Matches, "word", "words")}";
		var pages = $"{count.Pages.ToString("N0", CultureInfo.InvariantCulture)} {UsageLine.Plural(count.Pages, "page", "pages")}";
		if (report.Of(KnownPartTally.NotNeededSetting, key) is not null)
		{
			return UsageLine.Nothing($"Let {words} pass on {pages}, but each would pass without it — its parts are known anyway: not needed.", report);
		}

		double? share = report.Pages > 0 ? Math.Clamp((double)count.Pages / report.Pages, 0, 1) : null;
		var neededFor = report.ExamplesOf(KnownPartTally.Setting, key);
		return UsageLine.Counts(report, share,
			UsageLine.W("Let "), UsageLine.N(count.Matches), UsageLine.W($" {UsageLine.Plural(count.Matches, "word", "words")} pass on "),
			UsageLine.N(count.Pages), UsageLine.W(" of "), UsageLine.N(report.Pages), UsageLine.W(" pages"),
			UsageLine.W(neededFor.Count == 0 ? string.Empty : " · needed for: " + string.Join(", ", neededFor)));
	}

	/// <summary>Findings on pages: a suppression rule ("Suppressed …") or a pattern group.</summary>
	public static UsageLine ForFindings(UsageReport? report, string setting, string key, bool suppression)
	{
		if (report is null)
		{
			return UsageLine.NotRun;
		}

		if (report.Of(setting, key) is not { } count)
		{
			return UsageLine.ChangedSince;
		}

		if (count.Matches == 0)
		{
			return UsageLine.Nothing(suppression
				? "Suppressed nothing in the last run — this rule may no longer be needed."
				: "No finding in the last run — this group may no longer be needed.", report);
		}

		return UsageLine.Counts(report, null,
			UsageLine.W(suppression ? "Suppressed " : string.Empty), UsageLine.N(count.Matches),
			UsageLine.W(UsageLine.Plural(count.Matches, " finding on ", " findings on ")), UsageLine.N(count.Pages),
			UsageLine.W(UsageLine.Plural(count.Pages, " page", " pages")));
	}
}

/// <summary>Boilerplate groups as cards.</summary>
public sealed class BoilerplateVm : Observable
{
	private readonly SettingRowVm row;

	public BoilerplateVm(JsonNode? value, SettingRowVm row)
	{
		this.row = row;
		foreach (var group in (value as JsonArray ?? []).OfType<JsonObject>())
		{
			Groups.Add(new BoilerplateGroupVm(group, this));
		}
	}

	public ObservableCollection<BoilerplateGroupVm> Groups { get; } = [];

	public bool CanEdit => row.IsEditable;

	public bool HasProblems => Groups.Any(g => g.Parts.Any(p => p.HasProblem));

	internal SettingsModel Owner => row.Owner;

	public void AddGroup()
	{
		if (CanEdit)
		{
			Groups.Add(new BoilerplateGroupVm(new JsonObject { ["PathPrefix"] = "/" }, this));
			Changed();
		}
	}

	public void RemoveGroup(BoilerplateGroupVm group)
	{
		if (CanEdit)
		{
			Groups.Remove(group);
			Changed();
		}
	}

	internal void Changed()
	{
		Raise(nameof(HasProblems));
		row.ApplyEditor(new JsonArray(Groups.Select(g => (JsonNode?)g.ToJson()).ToArray()));
	}

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
}

/// <summary>One boilerplate group: where it applies, its reference pages and its parts.</summary>
public sealed class BoilerplateGroupVm : Observable
{
	private readonly JsonObject original;
	private readonly BoilerplateVm owner;
	private string pathPrefix;

	public BoilerplateGroupVm(JsonObject original, BoilerplateVm owner)
	{
		this.original = (JsonObject)original.DeepClone();
		this.owner = owner;
		pathPrefix = original["PathPrefix"] is JsonValue p && p.TryGetValue<string>(out var prefix) ? prefix : string.Empty;
		foreach (var page in (original["PagesToCheckBoiler"] as JsonArray ?? []).OfType<JsonValue>())
		{
			if (page.TryGetValue<string>(out var path))
			{
				ReferencePages.Add(new ReferencePageVm(path, this));
			}
		}

		foreach (var part in (original["BoilerplateSelectors"] as JsonArray ?? []).OfType<JsonObject>())
		{
			Parts.Add(new PartVm(part, this));
		}

		RefreshUsage();
	}

	public BoilerplateVm Owner => owner;

	public string PathPrefix
	{
		get => pathPrefix;
		set
		{
			if (Set(ref pathPrefix, (value ?? string.Empty).Trim()))
			{
				RefreshUsage();
				owner.Changed();
			}
		}
	}

	public ObservableCollection<ReferencePageVm> ReferencePages { get; } = [];

	public ObservableCollection<PartVm> Parts { get; } = [];

	public bool CanEdit => owner.CanEdit;

	/// <summary>Amber when no reference page was in the last run: then these parts were spell-checked nowhere.</summary>
	public string ReferenceWarning { get; private set; } = string.Empty;

	public bool HasReferenceWarning => ReferenceWarning.Length > 0;

	public void AddReferencePage()
	{
		if (CanEdit)
		{
			ReferencePages.Add(new ReferencePageVm(string.Empty, this) { IsEditing = true });
		}
	}

	public void RemoveReferencePage(ReferencePageVm referencePage)
	{
		if (CanEdit)
		{
			ReferencePages.Remove(referencePage);
			ReferencePagesChanged();
		}
	}

	public void AddPart()
	{
		if (CanEdit)
		{
			Parts.Add(new PartVm(new JsonObject { ["Type"] = "class", ["Value"] = string.Empty }, this));
			owner.Changed();
		}
	}

	public void RemovePart(PartVm part)
	{
		if (CanEdit)
		{
			Parts.Remove(part);
			owner.Changed();
		}
	}

	internal void ReferencePagesChanged()
	{
		foreach (var empty in ReferencePages.Where(k => !k.IsEditing && k.Path.Length == 0).ToList())
		{
			ReferencePages.Remove(empty);
		}

		RefreshUsage();
		owner.Changed();
	}

	internal void PartChanged() => owner.Changed();

	internal JsonObject ToJson()
	{
		var obj = (JsonObject)original.DeepClone();
		obj["PathPrefix"] = pathPrefix;
		obj["PagesToCheckBoiler"] = new JsonArray(ReferencePages.Where(k => k.Path.Length > 0).Select(k => (JsonNode?)JsonValue.Create(k.Path)).ToArray());
		obj["BoilerplateSelectors"] = new JsonArray(Parts.Select(p => (JsonNode?)p.ToJson()).ToArray());
		return obj;
	}

	internal void RefreshUsage()
	{
		var report = owner.Owner.Usage;
		foreach (var referencePage in ReferencePages)
		{
			referencePage.Seen = report is null || referencePage.Path.Length == 0 || report.ReferencePagesSeen.Contains(SettingsUsage.ReferenceKey(referencePage.Path));
		}

		ReferenceWarning = report is not null && ReferencePages.Count > 0 && ReferencePages.All(k => !k.Seen)
			? "None of these pages was in the primary site's last run — this group's parts were spell-checked on no page."
			: report is not null && ReferencePages.Count == 0
				? "No reference page — this group's parts are spell-checked on no page."
				: string.Empty;
		Raise(nameof(ReferenceWarning));
		Raise(nameof(HasReferenceWarning));
		foreach (var part in Parts)
		{
			part.RefreshUsage();
		}
	}

	internal void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		foreach (var referencePage in ReferencePages)
		{
			referencePage.RaiseEditable();
		}

		foreach (var part in Parts)
		{
			part.RaiseEditable();
		}
	}
}

/// <summary>A reference page: the page on which the group's parts are spell-checked.</summary>
public sealed class ReferencePageVm : Observable
{
	private readonly BoilerplateGroupVm group;
	private string path;
	private bool isEditing;
	private bool seen = true;

	public ReferencePageVm(string path, BoilerplateGroupVm group)
	{
		this.path = path;
		this.group = group;
	}

	public BoilerplateGroupVm Group => group;

	public string Path
	{
		get => path;
		set
		{
			path = (value ?? string.Empty).Trim();
			IsEditing = false;
			Raise();
			group.ReferencePagesChanged();
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

	/// <summary>The page was in the primary site's last run (true when there is no run to compare with).</summary>
	public bool Seen
	{
		get => seen;
		set
		{
			if (Set(ref seen, value))
			{
				Raise(nameof(ToolTip));
			}
		}
	}

	public string ToolTip => seen ? "Click to edit" : "Not in the primary site's last run";

	public bool CanEdit => group.CanEdit;

	internal void RaiseEditable() => Raise(nameof(CanEdit));
}

/// <summary>A repeated part: a class (all names must be present) or an XPath, switchable, with usage.</summary>
public sealed class PartVm : Observable
{
	private readonly JsonObject original;
	private readonly BoilerplateGroupVm group;
	private string type;
	private string value;
	private bool enabled;

	public PartVm(JsonObject original, BoilerplateGroupVm group)
	{
		this.original = (JsonObject)original.DeepClone();
		this.group = group;
		type = original["Type"] is JsonValue t && t.TryGetValue<string>(out var ty) ? ty.Trim().ToLowerInvariant() : string.Empty;
		value = original["Value"] is JsonValue v && v.TryGetValue<string>(out var va) ? va : string.Empty;
		enabled = !(original["Enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && !on);
		Check();
	}

	public BoilerplateGroupVm Group => group;

	/// <summary>The types offered; an unknown type from the file is listed too, so it shows as it is.</summary>
	public IReadOnlyList<string> Types => SettingEditors.PartTypes.Contains(type) ? SettingEditors.PartTypes : [.. SettingEditors.PartTypes, type];

	public string Type
	{
		get => type;
		set
		{
			if (Set(ref type, (value ?? string.Empty).Trim().ToLowerInvariant()))
			{
				Changed();
			}
		}
	}

	public string Value
	{
		get => value;
		set
		{
			if (Set(ref this.value, (value ?? string.Empty).Trim()))
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
				group.PartChanged();
			}
		}
	}

	public bool IsClass => type == "class";

	public bool IsXpath => type == "xpath";

	/// <summary>An attribute part in plain words; empty for other types or a value that does not parse.</summary>
	public string AttributeText => type == CrawlerSuite.Core.Boilerplate.BoilerplateAttribute.Type
		&& CrawlerSuite.Core.Boilerplate.BoilerplateAttribute.TryParse(value, out var tag, out var name)
			? tag is null ? $"Removes the {name} attribute from every element; the elements stay."
				: $"Removes the {name} attribute of every <{tag}>; the element stays."
			: string.Empty;

	/// <summary>The class names as .name chips (one chip: all must be present).</summary>
	public string ClassChip => IsClass ? string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(c => "." + c)) : string.Empty;

	public IReadOnlyList<XpathPieceVm> Pieces => IsXpath
		? SettingEditors.SplitXpath(value).Select(p => new XpathPieceVm(p.Text, p.Part.ToString())).ToList()
		: [];

	public string Problem { get; private set; } = string.Empty;

	public bool HasProblem => Problem.Length > 0;

	public UsageLine Usage { get; private set; } = UsageLine.None;

	public bool HasUsage => Usage.Kind != UsageKind.None;

	public bool CanEdit => group.CanEdit;

	internal JsonObject ToJson()
	{
		var obj = (JsonObject)original.DeepClone();
		obj["Type"] = type;
		obj["Value"] = value;
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
		Usage = UsageText.For(group.Owner.Owner.Usage, SettingsUsage.Boilerplate,
			SettingsUsage.PartKey(group.PathPrefix, type, value), "matched");
		Raise(nameof(Usage));
		Raise(nameof(HasUsage));
	}

	internal void RaiseEditable() => Raise(nameof(CanEdit));

	private void Changed()
	{
		Check();
		Raise(nameof(IsClass));
		Raise(nameof(IsXpath));
		Raise(nameof(AttributeText));
		Raise(nameof(ClassChip));
		Raise(nameof(Pieces));
		RefreshUsage();
		group.PartChanged();
	}

	private void Check()
	{
		Problem = SettingEditors.CheckPart(type, value) ?? string.Empty;
		Raise(nameof(Problem));
		Raise(nameof(HasProblem));
	}
}
