namespace CrawlerSuite.Core.Settings
{
	using System.Globalization;

	/// <summary>One site as the settings form edits it.</summary>
	public sealed class SiteDraft
	{
		// In the order the file keeps them.
		public string Name { get; set; } = string.Empty;
		public string Tenant { get; set; } = string.Empty;
		public string ProductionGroup { get; set; } = string.Empty;
		public bool IsPrimary { get; set; }
		public string Url { get; set; } = string.Empty;
		public List<string> UrlSubdomainsAllowed { get; set; } = [];
		public bool PostCrawlPass { get; set; }

		/// <summary>The site uses the CMS content list (null in older files: decided as the crawl decides it).</summary>
		[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
		public bool? UsesContentList { get; set; }

		/// <summary><see cref="UsesContentList"/>, with the older files' rule when absent.</summary>
		[System.Text.Json.Serialization.JsonIgnore]
		public bool UsesContentListResolved => UsesContentList
			?? (!string.IsNullOrWhiteSpace(Tenant) || !string.IsNullOrWhiteSpace(ProductionGroup) || PostCrawlPass);

		public SiteDraft Clone() => new()
		{
			Name = Name, Tenant = Tenant, ProductionGroup = ProductionGroup, IsPrimary = IsPrimary,
			Url = Url, UrlSubdomainsAllowed = [.. UrlSubdomainsAllowed], PostCrawlPass = PostCrawlPass,
			UsesContentList = UsesContentList,
		};
	}

	/// <summary>
	/// The checks a site entry must pass before it is saved. Addresses follow their spec, so a
	/// typo that breaks it is refused at the field instead of failing at the first crawl: an
	/// absolute http or https address, a valid host name, no credentials, no spaces, no query or
	/// fragment. Allowed subdomains are base addresses (scheme and host) checked the same way.
	/// </summary>
	public static class SiteRules
	{
		private static readonly IdnMapping Idn = new();

		/// <summary>What is wrong with a site address, or null when it is valid.</summary>
		public static string? CheckUrl(string url, bool baseOnly = false)
		{
			url ??= string.Empty;
			if (url.Trim().Length == 0)
			{
				return "An address is needed.";
			}

			if (url != url.Trim() || url.Any(char.IsWhiteSpace))
			{
				return "The address contains a space.";
			}

			if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
			{
				return "The address must start with https:// (or http://).";
			}

			if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
			{
				return "This is not a valid address.";
			}

			if (uri.UserInfo.Length > 0)
			{
				return "The address must not contain a user name or password.";
			}

			if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || url.Contains('?') || url.Contains('#'))
			{
				return "The address must not contain ? or #.";
			}

			if (CheckHost(uri.IdnHost) is { } hostProblem)
			{
				return hostProblem;
			}

			if (baseOnly && uri.AbsolutePath.Trim('/').Length > 0)
			{
				return "Only scheme and host, without a path, e.g. https://help.example.com";
			}

			return null;
		}

		/// <summary>The host as it is requested, when it differs from what was typed (letters beyond ASCII); else null.</summary>
		public static string? AsciiHost(string url)
		{
			if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			{
				return null;
			}

			try
			{
				var ascii = Idn.GetAscii(uri.Host);
				return string.Equals(ascii, uri.Host, StringComparison.OrdinalIgnoreCase) ? null : ascii;
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		/// <summary>What is wrong with a host name (in its ASCII form), by the host-name rules; null when valid.</summary>
		public static string? CheckHost(string host)
		{
			if (host.Length == 0 || host.Length > 253)
			{
				return "The host name is empty or too long.";
			}

			if (Uri.CheckHostName(host) is UriHostNameType.IPv4 or UriHostNameType.IPv6)
			{
				return null;
			}

			var labels = host.TrimEnd('.').Split('.');
			if (labels.Length < 2)
			{
				return "The host name needs a domain, e.g. www.example.com.";
			}

			foreach (var label in labels)
			{
				if (label.Length is 0 or > 63)
				{
					return "A part of the host name is empty or longer than 63 characters.";
				}

				if (label[0] == '-' || label[^1] == '-')
				{
					return "A part of the host name starts or ends with a hyphen.";
				}

				if (!label.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
				{
					return "The host name contains a character that is not allowed.";
				}
			}

			return null;
		}

		/// <summary>
		/// The problems of a list of sites, each prefixed with the site it is about; empty when all
		/// pass. Covers each address, each allowed subdomain, names, duplicates and the one primary site.
		/// An empty subdomain entry is no subdomain — the crawl drops it — so it is not reported.
		/// </summary>
		public static IReadOnlyList<string> Check(IReadOnlyList<SiteDraft> sites)
		{
			var problems = new List<string>();
			for (int i = 0; i < sites.Count; i++)
			{
				var site = sites[i];
				var label = site.Name.Trim().Length > 0 ? site.Name.Trim() : $"Site {i + 1}";
				if (site.Name.Trim().Length == 0)
				{
					problems.Add($"{label}: a name is needed.");
				}

				if (CheckUrl(site.Url) is { } urlProblem)
				{
					problems.Add($"{label}: {urlProblem}");
				}

				if (site.UsesContentListResolved)
				{
					if (site.Tenant.Trim().Length == 0)
					{
						problems.Add($"{label}: a tenant is needed while the site uses the CMS content list (or switch that off).");
					}

					if (site.ProductionGroup.Trim().Length == 0)
					{
						problems.Add($"{label}: a production group is needed while the site uses the CMS content list (or switch that off).");
					}
				}

				foreach (var sub in site.UrlSubdomainsAllowed.Where(s => !string.IsNullOrWhiteSpace(s)))
				{
					if (CheckUrl(sub, baseOnly: true) is { } subProblem)
					{
						problems.Add($"{label}, allowed subdomain {sub}: {subProblem}");
					}
				}
			}

			foreach (var dup in sites.GroupBy(s => s.Url.Trim().TrimEnd('/'), StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0 && g.Count() > 1))
			{
				problems.Add($"{dup.Key} is listed {dup.Count()} times.");
			}

			if (sites.Count > 0 && sites.Count(s => s.IsPrimary) != 1)
			{
				problems.Add("Exactly one site must be the primary site.");
			}

			return problems;
		}
	}
}
