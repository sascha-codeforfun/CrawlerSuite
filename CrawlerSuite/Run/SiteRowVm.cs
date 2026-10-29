using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using CrawlerSuite.Core.Settings;

namespace CrawlerSuite.Run;

/// <summary>One site in the form.</summary>
public sealed class SiteRowVm : Observable
{
	private readonly SettingsModel owner;
	private string subdomainsText;
	private bool hasSubdomains;

	public SiteRowVm(SiteDraft draft, SettingsModel owner)
	{
		// An empty entry (a hand-edited file may hold [ "" ]) is no subdomain — the crawl drops it too.
		draft.UrlSubdomainsAllowed = draft.UrlSubdomainsAllowed.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
		Draft = draft;
		this.owner = owner;
		OriginalUrl = draft.Url;
		subdomainsText = string.Join(Environment.NewLine, draft.UrlSubdomainsAllowed);
		hasSubdomains = draft.UrlSubdomainsAllowed.Count > 0;
	}

	internal SiteDraft Draft { get; }

	/// <summary>The address as read from the file (empty for a new site).</summary>
	public string OriginalUrl { get; private set; }

	/// <summary>Added in this session, not saved yet.</summary>
	public bool IsNew { get; init; }

	public bool CanEdit => owner.CanEdit;

	private bool isSearchMatch = true;

	/// <summary>Shown under the Sites tab's search (always, without one).</summary>
	public bool IsSearchMatch { get => isSearchMatch; set => Set(ref isSearchMatch, value); }

	/// <summary>The name, address or an allowed subdomain contains the text, ignoring case.</summary>
	internal bool MatchesSearch(string text) =>
		SettingSearch.Contains(Draft.Name, text)
		|| SettingSearch.Contains(Draft.Url, text)
		|| Draft.UrlSubdomainsAllowed.Any(s => SettingSearch.Contains(s, text));

	public string Name
	{
		get => Draft.Name;
		set { Draft.Name = (value ?? string.Empty).Trim(); Raise(); owner.SitesChanged(); }
	}

	public string Url
	{
		get => Draft.Url;
		set
		{
			Draft.Url = (value ?? string.Empty).Trim();
			Raise();
			Raise(nameof(UrlProblem));
			Raise(nameof(AsciiHost));
			owner.SitesChanged();
		}
	}

	/// <summary>What is wrong with the address, shown at the field.</summary>
	public string UrlProblem => SiteRules.CheckUrl(Draft.Url) ?? string.Empty;

	/// <summary>The host as requested, when it has letters beyond ASCII.</summary>
	public string AsciiHost => SiteRules.AsciiHost(Draft.Url) is { } ascii ? "Requested as " + ascii : string.Empty;

	/// <summary>
	/// The site has allowed subdomains: only then is the box shown, and then it needs an entry.
	/// Switching off removes the entries; the view asks first when there are any.
	/// </summary>
	public bool HasSubdomains
	{
		get => hasSubdomains;
		set
		{
			if (value == hasSubdomains)
			{
				return;
			}

			if (!value && Draft.UrlSubdomainsAllowed.Count > 0 && owner.ConfirmClearSubdomains?.Invoke(this) != true)
			{
				// Declined: the switch goes back on once the click has finished.
				System.Threading.SynchronizationContext.Current?.Post(_ => Raise(nameof(HasSubdomains)), null);
				return;
			}

			hasSubdomains = value;
			if (!value)
			{
				subdomainsText = string.Empty;
				Draft.UrlSubdomainsAllowed = [];
				Raise(nameof(SubdomainsText));
			}

			Raise();
			owner.SitesChanged();
		}
	}

	/// <summary>Switched on with nothing entered.</summary>
	internal bool MissingSubdomain => hasSubdomains && Draft.UrlSubdomainsAllowed.Count == 0;

	public string SubdomainsText
	{
		get => subdomainsText;
		set
		{
			subdomainsText = value ?? string.Empty;
			Draft.UrlSubdomainsAllowed = subdomainsText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
			Raise();
			owner.SitesChanged();
		}
	}

	/// <summary>The site uses the CMS content list: tenant, production group and post-crawl pass are shown and needed.</summary>
	public bool UsesContentList
	{
		get => Draft.UsesContentListResolved;
		set
		{
			if (value != Draft.UsesContentListResolved || Draft.UsesContentList is null)
			{
				Draft.UsesContentList = value;
				Raise();
				owner.SitesChanged();
			}
		}
	}

	public string Tenant
	{
		get => Draft.Tenant;
		set { Draft.Tenant = (value ?? string.Empty).Trim(); Raise(); owner.SitesChanged(); }
	}

	public string ProductionGroup
	{
		get => Draft.ProductionGroup;
		set { Draft.ProductionGroup = (value ?? string.Empty).Trim(); Raise(); owner.SitesChanged(); }
	}

	public bool IsPrimary
	{
		get => Draft.IsPrimary;
		set
		{
			if (value && !Draft.IsPrimary)
			{
				owner.MakePrimary(this);
			}

			Raise();
		}
	}

	public bool PostCrawlPass
	{
		get => Draft.PostCrawlPass;
		set { Draft.PostCrawlPass = value; Raise(); owner.SitesChanged(); }
	}

	internal void SetPrimarySilently(bool primary)
	{
		Draft.IsPrimary = primary;
		Raise(nameof(IsPrimary));
	}

	internal void MarkSaved() => OriginalUrl = Draft.Url;

	internal void RaiseEditable() => Raise(nameof(CanEdit));
}
