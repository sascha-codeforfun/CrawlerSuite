using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CrawlerSuite.Core.Triage;

namespace CrawlerSuite.Run;

/// <summary>
/// Settings → Triage: the triage switches by family, as three-state switches (off, ride-along,
/// on). The configuration keeps only the switches set apart from their built-in default.
/// </summary>
public sealed class TriageModesEditorVm : Observable
{
	private readonly SettingRowVm row;
	private readonly Dictionary<string, TriageMode> modes;

	public TriageModesEditorVm(JsonNode? value, SettingRowVm row)
	{
		this.row = row;
		var configured = new Dictionary<string, string>(StringComparer.Ordinal);
		if (value is JsonObject map)
		{
			foreach (var (id, node) in map)
			{
				if (node is JsonValue v && v.TryGetValue<string>(out var text))
				{
					configured[id] = text;
				}
			}
		}

		modes = TriageSwitches.ModesFrom(configured);
		foreach (var family in TriageSwitches.All.GroupBy(s => s.Family))
		{
			var vm = new TriageSwitchFamilyVm(family.Key);
			foreach (var s in family)
			{
				vm.Switches.Add(new TriageSwitchVm(s, modes[s.Id], TriageSwitchVm.NoCount, Changed) { CanChange = () => CanEdit });
			}

			vm.Refresh();
			Families.Add(vm);
		}
	}

	public ObservableCollection<TriageSwitchFamilyVm> Families { get; } = [];

	public bool CanEdit => row.IsEditable;

	internal void RaiseEditable()
	{
		Raise(nameof(CanEdit));
		foreach (var s in Families.SelectMany(f => f.Switches))
		{
			s.RaiseEnabled();
		}
	}

	private void Changed(TriageSwitchVm changed)
	{
		modes[changed.Id] = changed.Mode;
		foreach (var family in Families)
		{
			family.Refresh();
		}

		var kept = new JsonObject();
		foreach (var (id, mode) in TriageSwitches.ToConfigured(modes).OrderBy(kv => kv.Key, StringComparer.Ordinal))
		{
			kept[id] = mode;
		}

		row.ApplyTable(kept);
	}
}
