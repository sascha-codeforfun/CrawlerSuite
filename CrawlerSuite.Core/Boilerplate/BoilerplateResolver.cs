namespace CrawlerSuite.Core.Boilerplate
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// Resolves, for a given page URL path, which boilerplate group governs it and whether the
	/// page is a reference page (where boilerplate is checked rather than pruned). Pure lookups over
	/// the config; no I/O.
	///
	/// Rules:
	///   * Governing group = the one whose PathPrefix is the LONGEST prefix of the page path.
	///   * A page is a reference page iff it appears in ANY group's PagesToCheckBoiler. On a check
	///     page nothing is pruned (boilerplate is checked there). The selectors used on a check
	///     page are that group's own selectors (it validates that group's boilerplate).
	///   * No PathPrefix matches and not a reference page → no governing group → caller checks
	///     everything (fail-loud default).
	///
	/// Path comparison is ordinal, case-insensitive (URLs/paths are treated case-insensitively
	/// here, matching the existing CrawlIndex/override conventions).
	/// </summary>
	public sealed class BoilerplateResolver
	{
		private readonly List<BoilerplateGroupConfig> _groups;
		private readonly Dictionary<string, BoilerplateGroupConfig> _referencePageToGroup;
		private readonly Dictionary<BoilerplateGroupConfig, BoilerplateMatcher> _matchers;

		public BoilerplateResolver(IEnumerable<BoilerplateGroupConfig>? groups)
		{
			_groups = (groups ?? Enumerable.Empty<BoilerplateGroupConfig>())
				.Where(g => g != null)
				.ToList();

			_matchers = _groups.ToDictionary(
				g => g,
				g => new BoilerplateMatcher(
					g.BoilerplateSelectors.Where(s => s.Enabled).Select(s => new BoilerplateSelector(s.Type, s.Value))));

			_referencePageToGroup = new Dictionary<string, BoilerplateGroupConfig>(StringComparer.OrdinalIgnoreCase);
			foreach (var g in _groups)
			{
				foreach (var page in g.PagesToCheckBoiler)
				{
					if (!string.IsNullOrWhiteSpace(page))
					{
						// First declaration wins if a page is listed in multiple groups.
						_referencePageToGroup.TryAdd(NormalizePath(page), g);
					}
				}
			}
		}

		/// <summary>
		/// Resolution for one page: the matcher to prune with, and whether this is a reference page
		/// (so the caller passes isReferencePage = true and prunes nothing). When no group governs the
		/// page and it is not a reference page, returns a null matcher → caller checks everything.
		/// </summary>
		public (BoilerplateMatcher? Matcher, bool IsReferencePage) Resolve(string pageUrlOrPath)
		{
			string path = NormalizePath(pageUrlOrPath);

			// Reference page? Use that group's matcher, flagged as a reference page (prune nothing).
			if (_referencePageToGroup.TryGetValue(path, out var checkGroup))
			{
				return (_matchers[checkGroup], true);
			}

			// Otherwise the governing group = longest matching PathPrefix.
			BoilerplateGroupConfig? best = null;
			int bestLen = -1;
			foreach (var g in _groups)
			{
				string prefix = NormalizePath(g.PathPrefix);
				if (prefix.Length > bestLen
					&& path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				{
					best = g;
					bestLen = prefix.Length;
				}
			}

			if (best == null)
			{
				// No group governs → check everything (fail-loud).
				return (null, false);
			}

			return (_matchers[best], false);
		}

		/// <summary>
		/// Selector-list counterpart of <see cref="Resolve"/> for the on-disk pruner: yields the
		/// governing group's raw typed selectors (not a built matcher) plus whether this is a check
		/// page. Same governance — reference page → that group's selectors, IsReferencePage true (caller keeps
		/// boilerplate); else longest-PathPrefix group's selectors, false; no group → empty, false
		/// (caller prunes nothing → checks everything). Resolve() serves the in-memory spell
		/// prune; this is the parallel entry point for disk pruning.
		/// </summary>
		public (IReadOnlyList<BoilerplateSelectorConfig> Selectors, bool IsReferencePage) ResolveSelectors(string pageUrlOrPath)
		{
			string path = NormalizePath(pageUrlOrPath);

			// Reference page → keep boilerplate; caller skips removals. Selectors returned for symmetry.
			if (_referencePageToGroup.TryGetValue(path, out var checkGroup))
			{
				return (checkGroup.BoilerplateSelectors.Where(s => s.Enabled).ToList(), true);
			}

			// Governing group = longest matching PathPrefix (same rule as Resolve).
			BoilerplateGroupConfig? best = null;
			int bestLen = -1;
			foreach (var g in _groups)
			{
				string prefix = NormalizePath(g.PathPrefix);
				if (prefix.Length > bestLen
					&& path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				{
					best = g;
					bestLen = prefix.Length;
				}
			}

			if (best == null)
			{
				return (Array.Empty<BoilerplateSelectorConfig>(), false);
			}

			return (best.BoilerplateSelectors.Where(s => s.Enabled).ToList(), false);
		}

		/// <summary>
		/// The group governing a page (a reference page's own group, else the longest matching
		/// PathPrefix), with all its parts — switched-off ones too — for counting how often each
		/// part occurs. Null when no group governs the page.
		/// </summary>
		public (BoilerplateGroupConfig? Group, bool IsReferencePage, string Path) Govern(string pageUrlOrPath)
		{
			string path = NormalizePath(pageUrlOrPath);
			if (_referencePageToGroup.TryGetValue(path, out var checkGroup))
			{
				return (checkGroup, true, path);
			}

			BoilerplateGroupConfig? best = null;
			int bestLen = -1;
			foreach (var g in _groups)
			{
				string prefix = NormalizePath(g.PathPrefix);
				if (prefix.Length > bestLen && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				{
					best = g;
					bestLen = prefix.Length;
				}
			}

			return (best, false, path);
		}

		/// <summary>
		/// Reduce a URL or path to a comparable path string: strip scheme/host if a full URL, keep
		/// the leading-slash path, lowercase-invariant for comparison stability.
		/// </summary>
		internal static string NormalizePath(string urlOrPath)
		{
			if (string.IsNullOrWhiteSpace(urlOrPath))
			{
				return string.Empty;
			}

			string s = urlOrPath.Trim();
			if (Uri.TryCreate(s, UriKind.Absolute, out var uri))
			{
				s = uri.AbsolutePath;
			}

			if (!s.StartsWith('/'))
			{
				s = "/" + s;
			}

			return s;
		}
	}
}
