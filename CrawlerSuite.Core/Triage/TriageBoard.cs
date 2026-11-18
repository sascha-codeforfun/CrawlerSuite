namespace CrawlerSuite.Core.Triage
{
	/// <summary>Where a page stands in triage.</summary>
	public enum PageStatus
	{
		/// <summary>Nothing decided yet.</summary>
		Open,

		/// <summary>Some verdicts given (saved as drafts), some still open.</summary>
		PartlyDecided,

		/// <summary>Every item has a verdict; the page can be filed.</summary>
		ReadyToFile,

		/// <summary>Filed; nothing new since.</summary>
		Filed,

		/// <summary>Filed before, and new findings need a verdict.</summary>
		Reopened,
	}

	/// <summary>
	/// Where a page stands, in the four groups the triage filter shows: nothing decided, being
	/// decided, ticketed, or ticketed before and changed since (new findings to decide, or decided
	/// but not ticketed again yet).
	/// </summary>
	public enum PageDecision
	{
		/// <summary>No item has a verdict, and the page was never filed.</summary>
		Open,

		/// <summary>Some or all items have a verdict; never filed.</summary>
		Edited,

		/// <summary>Filed, nothing new since.</summary>
		Ticketed,

		/// <summary>Filed before, and not filed again since new findings came up.</summary>
		Changed,
	}

	/// <summary>One page of the triage list.</summary>
	/// <param name="Triaged">Items whose switch is on: they put the page on the list.</param>
	/// <param name="RideAlong">Items that travel with the page's ticket but never list a page by themselves.</param>
	/// <param name="LastFiled">The day the page was last filed; null when it never was.</param>
	/// <param name="Search">The page's search column of log 08 (<see cref="SEO.Search"/>); null when log 08 does not list it.</param>
	public sealed record PageSummary(
		string Url,
		IReadOnlyList<TriageItem> Triaged,
		IReadOnlyList<TriageItem> RideAlong,
		bool HasBidi,
		PageStatus Status,
		IReadOnlyList<(string Family, int Count)> ByFamily,
		IReadOnlyList<string> Tickets,
		bool FiledBefore = false,
		DateOnly? LastFiled = null,
		string? Search = null)
	{
		/// <summary>The page may appear in search, as far as the snapshot can tell: such pages are listed first after security and bidi.</summary>
		public bool InSearch => PageSearchStatus.IsIn(Search);

		/// <summary>The group the triage filter shows the page in.</summary>
		public PageDecision Decision => DecisionOf(Status, FiledBefore);

		/// <summary>Filed → Ticketed; filed before but not now → Changed; any verdict → Edited; else Open.</summary>
		public static PageDecision DecisionOf(PageStatus status, bool filedBefore) => status switch
		{
			PageStatus.Filed => PageDecision.Ticketed,
			_ when filedBefore => PageDecision.Changed,
			PageStatus.Open => PageDecision.Open,
			_ => PageDecision.Edited,
		};

		/// <summary>The page has a security-relevant finding: it is listed first.</summary>
		public bool HasSecurity => Triaged.Concat(RideAlong).Any(i => i.IsSecurity);

		/// <summary>Everything the page view shows and the filing covers.</summary>
		public IEnumerable<TriageItem> All => Triaged.Concat(RideAlong);
	}

	/// <summary>Builds the page list from a snapshot's items, the switches and the ledger.</summary>
	public static class TriageBoard
	{
		/// <summary>
		/// Where <paramref name="url"/> stands in <paramref name="list"/>: "Page 3 of 5", counted from one;
		/// "Not in the list" with the list's size for a page opened from elsewhere.
		/// </summary>
		public static string Position(IReadOnlyList<string> list, string url)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(list[i], url, StringComparison.OrdinalIgnoreCase))
				{
					return $"Page {i + 1:N0} of {list.Count:N0}";
				}
			}

			return list.Count == 1 ? "Not in the list (1 page)" : $"Not in the list ({list.Count:N0} pages)";
		}

		/// <summary>
		/// The page <paramref name="step"/> places from <paramref name="url"/> in <paramref name="open"/>
		/// (+1 next, -1 previous), going round at either end; null when there is no other page. A
		/// page not in the list starts from its edge: the first page forward, the last one back.
		/// </summary>
		public static string? Neighbour(IReadOnlyList<string> open, string url, int step)
		{
			int at = -1;
			for (int i = 0; i < open.Count; i++)
			{
				if (string.Equals(open[i], url, StringComparison.OrdinalIgnoreCase))
				{
					at = i;
					break;
				}
			}

			int others = at < 0 ? open.Count : open.Count - 1;
			if (others == 0)
			{
				return null;
			}

			int next = at < 0 ? (step > 0 ? 0 : open.Count - 1) : ((at + step) % open.Count + open.Count) % open.Count;
			return open[next];
		}

		/// <param name="searchByPage">Each page's search column of log 08 (<see cref="SeoSource.SearchByPage"/>); none: no page has a status.</param>
		public static IReadOnlyList<PageSummary> Pages(
			IEnumerable<TriageItem> items, IReadOnlyDictionary<string, TriageMode> modes, TriageLedger ledger,
			IReadOnlyDictionary<string, string>? searchByPage = null)
		{
			var pages = new List<PageSummary>();
			foreach (var page in items.GroupBy(i => i.Page, StringComparer.OrdinalIgnoreCase))
			{
				// A shared entry is listed when its switch is on; ride-along, it waits, as on a page —
				// it has no page to ride along with.
				bool shared = SharedEntries.IsShared(page.Key);
				var triaged = page.Where(i => TriageSwitches.ModeOf(modes, i.SwitchId) == TriageMode.On).ToList();
				if (triaged.Count == 0)
				{
					continue; // ride-along alone never lists a page
				}

				var rideAlong = shared ? new List<TriageItem>() : page.Where(i => TriageSwitches.ModeOf(modes, i.SwitchId) == TriageMode.RideAlong).ToList();
				var shown = triaged.Concat(rideAlong).ToList();
				var byFamily = triaged.GroupBy(i => i.Family, StringComparer.Ordinal)
					.OrderBy(g => TriageSwitches.FamilyOrder(g.Key))
					.Select(g => (g.Key, g.Count()))
					.ToList();
				ledger.Pages.TryGetValue(page.Key, out var entry);
				pages.Add(new PageSummary(
					page.Key,
					triaged,
					rideAlong,
					triaged.Any(i => TriageSwitches.IsBidi(i.SwitchId)),
					StatusOf(shown, ledger, entry),
					byFamily,
					entry?.Tickets ?? [],
					entry is { Filings.Count: > 0 },
					entry is { Filings.Count: > 0 } ? entry.Filings.Max(f => f.At) : null,
					searchByPage is not null && searchByPage.TryGetValue(page.Key, out var search) ? search : null));
			}

			return pages
				.OrderByDescending(p => p.HasSecurity)
				.ThenByDescending(p => p.HasBidi)
				.ThenByDescending(p => p.InSearch) // a defect a search visitor can land on comes first
				.ThenBy(p => SharedEntries.IsShared(p.Url)) // shared entries after the pages
				.ThenBy(p => p.Status == PageStatus.Filed)
				// A page ticketed before waits behind every page never ticketed — its editor has had a
				// turn — and among those, the one ticketed longest ago comes first.
				.ThenBy(p => p.FiledBefore)
				.ThenBy(p => p.LastFiled ?? DateOnly.MinValue)
				.ThenByDescending(p => p.Triaged.Count)
				.ThenBy(p => p.Url, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		public static PageStatus StatusOf(IReadOnlyCollection<TriageItem> shown, TriageLedger ledger, LedgerPage? entry)
		{
			int undecided = shown.Count(i => ledger.VerdictOf(i) is null);
			int drafts = shown.Count(i => ledger.VerdictOf(i) is not null && !ledger.IsFiled(i));
			bool filedBefore = entry is { Filings.Count: > 0 };

			if (undecided == 0)
			{
				return drafts == 0 ? PageStatus.Filed : PageStatus.ReadyToFile;
			}

			if (filedBefore)
			{
				return PageStatus.Reopened;
			}

			return undecided < shown.Count ? PageStatus.PartlyDecided : PageStatus.Open;
		}
	}
}
