using CrawlerSuite.Core.Findings;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>How triage treats a defect type (the three-state switch).</summary>
	public enum TriageMode
	{
		/// <summary>Not triaged: not on the page list, not in tickets. Still shown in Results.</summary>
		Off,

		/// <summary>Rides along in a page's ticket when the page has other defects; never lists a page by itself.</summary>
		RideAlong,

		/// <summary>Always triaged as an issue.</summary>
		On,
	}

	/// <summary>One switch of the triage filter: a content-quality issue type, or a cause of invisible characters.</summary>
	/// <param name="Id">The issue type, or "INVISIBLE_CHAR_IN_BODY:&lt;cause&gt;".</param>
	/// <param name="Locked">True when the switch cannot be changed (bidi controls are always triaged).</param>
	public sealed record TriageSwitch(string Id, string Family, string Label, TriageMode Default, bool Locked = false);

	/// <summary>
	/// The triage filter: one switch per content-quality defect type, in the order and with the
	/// names of the Results tabs; invisible characters get one switch per cause. The defaults
	/// come back on every load — they are not configuration and are never saved.
	/// </summary>
	public static class TriageSwitches
	{
		public const string Invisible = "INVISIBLE_CHAR_IN_BODY";

		/// <summary>The family row the invisible-character causes are shown under.</summary>
		public const string InvisibleFamily = "Invisible characters";

		// Declared before All: static fields initialise in order, and Build reads this set.
		/// <summary>
		/// Types that ride along by default: what no reader sees, or what the page's editor cannot
		/// fix in the page's own text (template and markup defects, accessibility of components).
		/// They go into the ticket of a page listed for something else and never list a page by
		/// themselves, so typos and visible text errors are not buried under them. Every switch
		/// can still be turned on, e.g. for a targeted accessibility pass.
		/// </summary>
		private static readonly HashSet<string> RideAlongByDefault = new(StringComparer.Ordinal)
		{
			"QUOTE_MIXED_KIND",
			"QUOTE_SYSTEM_MIX",
			"CONTROL_CHARS_IN_CONTENT",
			"DECOMPOSED_TEXT_IN_CONTENT",
			"MIXED_NORMALIZATION_IN_CONTENT",
			"LINE_BREAK_GLUE_INERT",
			"LINE_BREAK_GLUE_DOUBLE_SPACE",
			"BARE_TEXT_IN_CONTAINER",
			"MISPLACED_ANCHOR_EMPTY",
			"ARIA_HIDDEN_FOCUSABLE",
			"ARIA_REFERENCE_BROKEN",
			"MALFORMED_HTML",
			"CONTENT_BEFORE_DOCTYPE",
			// Embedded BOM and styled headings are not here: template defects arrive grouped — one entry
			// per pattern or repeat (SharedEntries) — so on does not flood the list with page items.
			"PUNCTUATION_WEAK",
			"ARIA_HIDDEN_FOCUSABLE_CONDITIONAL",
			"ARIA_HIDDEN_FOCUSABLE_UNDETERMINED",
			"MISPLACED_ANCHOR_EMPTY_CONDITIONAL",
			"MISPLACED_ANCHOR_EMPTY_UNDETERMINED",
		};

		// Declared before All, like the set above.
		/// <summary>Records, not work: findings that say why something was not reported, with their evidence. Off by default.</summary>
		private static readonly HashSet<string> OffByDefault = new(StringComparer.Ordinal)
		{
			"ARIA_HIDDEN_FOCUSABLE_CLEARED",
			"MISPLACED_ANCHOR_EMPTY_CLEARED",
		};

		/// <summary>Every switch, in Results order.</summary>
		public static IReadOnlyList<TriageSwitch> All { get; } = Build();

		private static List<TriageSwitch> Build()
		{
			var list = new List<TriageSwitch>();
			foreach (var (family, _, types) in FindingsCatalog.ContentQualityFamilies)
			{
				foreach (var (type, label) in types)
				{
					if (type != Invisible)
					{
						list.Add(new TriageSwitch(type, family, label, DefaultOf(type)));
						continue;
					}

					foreach (var cause in Enum.GetValues<FindingsCatalog.InvisibleCause>())
					{
						var title = FindingsCatalog.InvisibleCauses[cause].Title;
						var (mode, locked) = cause switch
						{
							FindingsCatalog.InvisibleCause.Bidi => (TriageMode.On, true),
							FindingsCatalog.InvisibleCause.SoftHyphens => (TriageMode.Off, false),
							FindingsCatalog.InvisibleCause.PasteDebris => (TriageMode.RideAlong, false),
							FindingsCatalog.InvisibleCause.Controls => (TriageMode.RideAlong, false),
							_ => (TriageMode.On, false),
						};
						// Their own row, as the fourth tab row in Results: the cause names alone.
						list.Add(new TriageSwitch(IdOf(cause), InvisibleFamily, title, mode, locked));
					}
				}
			}

			// Broken links: one switch per reach. On where a visitor or a search engine runs into it; ride-along
			// where none may ever do (only a script requests it, nothing requests it, the reach is undecided):
			// it goes into the ticket of a page open anyway and never lists a page by itself.
			foreach (var reach in Links.BrokenLinkReach.All)
			{
				var mode = reach is Links.BrokenLinkReach.Script or Links.BrokenLinkReach.Never or Links.BrokenLinkReach.Undecided
					? TriageMode.RideAlong
					: TriageMode.On;
				list.Add(new TriageSwitch(BrokenLinkSource.SwitchIdOf(reach), BrokenLinkSource.Family, reach, mode));
			}

			// Self-links and canonical issues: things the page's editor fixes, so on — a page with only
			// these is listed.
			list.Add(new TriageSwitch(SelfLinkSource.Type, SelfLinkSource.Family, SelfLinkSource.Label, TriageMode.On));
			foreach (var (type, label) in CanonicalSource.Types)
			{
				list.Add(new TriageSwitch(type, CanonicalSource.Family, label, TriageMode.On));
			}

			// Forwarding links: always a finding, decided once — the ledger keeps a verdict, so a link
			// known and fine stays out of the next triage. Locked on.
			foreach (var (type, label) in ForwardingLinkSource.Types)
			{
				list.Add(new TriageSwitch(type, ForwardingLinkSource.Family, label, TriageMode.On, Locked: true));
			}

			// Search appearance: ride-along. What a page's editor fixes in the page's settings, but across a
			// site it lists a large share of the pages at once; it goes deeper once a page is open anyway.
			// Switched on for an SEO pass.
			foreach (var (type, label, mode) in SeoSource.Types)
			{
				list.Add(new TriageSwitch(type, SeoSource.Family, label, mode));
			}

			// Main heading: no H1 or several different ones ride along, like search appearance; one heading
			// written several times off — a layout per screen width that hides the copies is right, and
			// the HTML cannot tell.
			foreach (var (type, label, mode) in SeoSource.HeadingTypes)
			{
				list.Add(new TriageSwitch(type, SeoSource.HeadingFamily, label, mode));
			}

			// Images: a broken or oversized file where it is shown, and metadata that places a picture or
			// identifies a device, on; tiny images (mostly counters) and names of people (often a credit
			// meant to be there) off — whether they matter depends on the site.
			foreach (var (type, label, mode) in ImageSource.Types)
			{
				list.Add(new TriageSwitch(type, ImageSource.Family, label, mode));
			}

			// Spelling last, as the Spelling tab follows the content-quality tabs in Results. On: a typo
			// is what an editor fixes in the page's text, so a page with only typos is listed.
			list.Add(new TriageSwitch(SpellingSource.Type, SpellingSource.Family, SpellingSource.Label, TriageMode.On));
			return list;
		}

		/// <summary>True for a spelling item: decided only once the page's content quality is.</summary>
		public static bool IsSpelling(string type) => type == SpellingSource.Type;

		/// <summary>A broken link (log 07): its own section, decided without waiting for anything.</summary>
		public static bool IsBrokenLink(string type) => type == BrokenLinkSource.Type;

		public static string IdOf(FindingsCatalog.InvisibleCause cause) => $"{Invisible}:{cause}";

		/// <summary>The default of a content-quality type's switch: on for what an editor can fix in the page's text, else ride-along.</summary>
		private static TriageMode DefaultOf(string type) =>
			OffByDefault.Contains(type) ? TriageMode.Off
			: RideAlongByDefault.Contains(type) ? TriageMode.RideAlong
			: TriageMode.On;

		/// <summary>The control-character type: title, meta and text findings (log 10).</summary>
		public const string ControlChars = "CONTROL_CHARS_IN_CONTENT";

		/// <summary>The soft-hyphen switch: it covers soft hyphens from both finding types.</summary>
		public static string SoftHyphens => IdOf(FindingsCatalog.InvisibleCause.SoftHyphens);

		/// <summary>
		/// The switch a finding falls under. Soft hyphens fall under their own switch whichever
		/// type reported them (invisible characters in template text, or control characters in
		/// title, meta and text), so switching soft hyphens off takes them all out of triage.
		/// </summary>
		public static string IdOf(string type, string detail)
		{
			if (type == Invisible)
			{
				return IdOf(FindingsCatalog.CauseOfDetail(detail));
			}

			if (type == ControlChars && FindingsCatalog.CauseOfDetail(detail) == FindingsCatalog.InvisibleCause.SoftHyphens)
			{
				return SoftHyphens;
			}

			// Content before the doctype is written as MALFORMED_HTML; its switch has its own name.
			return FindingsCatalog.LeafType(type, detail);
		}

		/// <summary>True for the switch of bidi controls, whose findings always come first.</summary>
		public static bool IsBidi(string switchId) => switchId == IdOf(FindingsCatalog.InvisibleCause.Bidi);

		/// <summary>The family and label a switch is shown under; unknown types land under Other.</summary>
		public static (string Family, string Label) Describe(string switchId)
		{
			var known = All.FirstOrDefault(s => s.Id == switchId);
			return known is null ? ("Other", switchId) : (known.Family, known.Label);
		}

		/// <summary>Where a family stands in Results order (unknown families last).</summary>
		public static int FamilyOrder(string family)
		{
			int at = All.Select(s => s.Family).Distinct().ToList().IndexOf(family);
			return at < 0 ? int.MaxValue : at;
		}

		/// <summary>
		/// The modes the configuration sets (switch id → "On", "RideAlong" or "Off", case ignored)
		/// over the defaults. Unknown ids and values are left out; a locked switch keeps its mode.
		/// </summary>
		public static Dictionary<string, TriageMode> ModesFrom(IReadOnlyDictionary<string, string>? configured)
		{
			var modes = Defaults();
			foreach (var (id, value) in configured ?? new Dictionary<string, string>())
			{
				// A reach renamed since the setting was saved keeps its mode under its new name.
				var current = id.StartsWith(BrokenLinkSource.Type + ":", StringComparison.Ordinal)
					? BrokenLinkSource.SwitchIdOf(Links.BrokenLinkReach.Current(id[(BrokenLinkSource.Type.Length + 1)..]))
					: id;
				if (All.FirstOrDefault(s => s.Id == current) is { Locked: false } && Enum.TryParse<TriageMode>(value, ignoreCase: true, out var mode)
					&& Enum.IsDefined(mode))
				{
					modes[current] = mode;
				}
			}

			return modes;
		}

		/// <summary>The map the configuration keeps: only the switches set apart from their default.</summary>
		public static Dictionary<string, string> ToConfigured(IReadOnlyDictionary<string, TriageMode> modes) =>
			All.Where(s => !s.Locked && modes.TryGetValue(s.Id, out var m) && m != s.Default)
				.ToDictionary(s => s.Id, s => modes[s.Id].ToString(), StringComparer.Ordinal);

		/// <summary>The defaults, as a fresh, editable map (switch id → mode).</summary>
		public static Dictionary<string, TriageMode> Defaults() =>
			All.ToDictionary(s => s.Id, s => s.Default, StringComparer.Ordinal);

		/// <summary>The mode of a switch in <paramref name="modes"/>; types without a switch are triaged.</summary>
		public static TriageMode ModeOf(IReadOnlyDictionary<string, TriageMode> modes, string switchId) =>
			IsBidi(switchId) ? TriageMode.On : modes.TryGetValue(switchId, out var mode) ? mode : TriageMode.On;

		/// <summary>Types where a "Locale" verdict (an intentional language choice) applies.</summary>
		public static bool AllowsLocale(string type) => type is "LANGUAGE_MISMATCH" or "POTENTIAL_TRANSLATION" or SpellingSource.Type;
	}
}
