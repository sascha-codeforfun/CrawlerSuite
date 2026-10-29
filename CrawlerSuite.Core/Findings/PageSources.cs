namespace CrawlerSuite.Core.Findings
{
	/// <summary>
	/// Where a page comes from, as the CMS content list says: the value of the configured source
	/// column (<see cref="TicketGenerationConfig.UrlSourceColumn"/>), the label of the first
	/// matching <see cref="TicketGenerationConfig.SpecialInfoMappings"/> entry, and the CMS editor
	/// link — the same lookup the tickets use, so the Results and the tickets always agree. The
	/// values are shown as the export has them: nothing here interprets them.
	/// </summary>
	/// <param name="Value">The source column's value; empty when the column is not configured or empty for the page.</param>
	/// <param name="Label">The matching mapping's label; empty when none matches.</param>
	/// <param name="CmsLink">The CMS editor link; empty when it cannot be built.</param>
	/// <param name="Role">Which configured key the page matched (only a text comparison; the meaning stays with the operator).</param>
	public sealed record PageSource(string Value, string Label, string CmsLink, PageSourceRole Role = PageSourceRole.Other)
	{
		/// <summary>The value and the label as one text ("value label"); empty when both are.</summary>
		public string Text => Label.Length == 0 ? Value : Value.Length == 0 ? Label : Value + " " + Label;
	}

	/// <summary>
	/// Which configured key a page's source matched. The app compares text only: the value with
	/// <see cref="TicketGenerationConfig.UrlSourceLocalName"/> and
	/// <see cref="TicketGenerationConfig.UrlSourceExternalName"/>, and whether any
	/// <see cref="TicketGenerationConfig.SpecialInfoMappings"/> label matched — what that means is
	/// the operator's to know. Used for the chip's colour.
	/// </summary>
	public enum PageSourceRole
	{
		/// <summary>None of the configured names: shown neutral.</summary>
		Other,

		/// <summary>The value equals UrlSourceLocalName.</summary>
		Local,

		/// <summary>The value equals UrlSourceExternalName.</summary>
		External,

		/// <summary>A mapping label matched. It wins over the value: the label is the stronger statement.</summary>
		Mapped,
	}

	/// <summary>Builds the page-source lookup for a snapshot.</summary>
	public static class PageSources
	{
		/// <summary>
		/// The lookup for pages of <paramref name="config"/>'s resolved site. Null when the content
		/// list is not configured or cannot be read: then no page has a source. Answers are
		/// remembered per URL; the returned function may be called from any thread.
		/// </summary>
		public static Func<string, PageSource?>? Build(Config config)
		{
			ArgumentNullException.ThrowIfNull(config);
			var list = config.CmsContentList;
			if (list is null || string.IsNullOrEmpty(list.Path) || !File.Exists(list.Path))
			{
				return null;
			}

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var names = config.TicketGeneration;
			var known = new System.Collections.Concurrent.ConcurrentDictionary<string, PageSource?>(StringComparer.OrdinalIgnoreCase);
			return url =>
			{
				if (string.IsNullOrWhiteSpace(url))
				{
					return null;
				}

				if (!known.TryGetValue(url, out var source))
				{
					var meta = lookup(url);
					source = meta.Location.Length == 0 && meta.SpecialInfo.Length == 0 && meta.CmsLink.Length == 0
						? null
						: new PageSource(meta.Location, meta.SpecialInfo, meta.CmsLink, RoleOf(meta.Location, meta.SpecialInfo, names));
					known.TryAdd(url, source);
				}

				return source;
			};
		}

		/// <summary>Which configured key a source matched (text comparison only, ignoring case like the ticket lookup).</summary>
		internal static PageSourceRole RoleOf(string value, string label, TicketGenerationConfig names)
		{
			if (label.Length > 0)
			{
				return PageSourceRole.Mapped;
			}

			if (value.Length > 0 && value.Equals(names.UrlSourceLocalName, StringComparison.OrdinalIgnoreCase))
			{
				return PageSourceRole.Local;
			}

			return value.Length > 0 && value.Equals(names.UrlSourceExternalName, StringComparison.OrdinalIgnoreCase)
				? PageSourceRole.External
				: PageSourceRole.Other;
		}

		/// <summary>
		/// The configured site a snapshot folder belongs to (&lt;BaseDirectory&gt;\&lt;site folder&gt;\&lt;timestamp&gt;),
		/// or null when it belongs to none.
		/// </summary>
		public static SiteConfig? SiteOf(Config config, string snapshotDirectory)
		{
			ArgumentNullException.ThrowIfNull(config);
			var parent = Path.GetDirectoryName(Path.GetFullPath(snapshotDirectory).TrimEnd('\\', '/'));
			if (parent is null)
			{
				return null;
			}

			return config.Sites.FirstOrDefault(site =>
				!string.IsNullOrEmpty(site.Url)
				&& string.Equals(Path.GetFullPath(SnapshotLocator.SiteFolder(config, site.Url)).TrimEnd('\\', '/'), parent, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// The lookup for a snapshot folder: <paramref name="config"/> (freshly loaded, not used
		/// elsewhere — it is resolved for the snapshot's site here) resolved for the site the
		/// folder belongs to. Null when the folder belongs to no configured site, or no content
		/// list is configured.
		/// </summary>
		public static Func<string, PageSource?>? ForSnapshot(Config config, string snapshotDirectory)
		{
			var site = SiteOf(config, snapshotDirectory);
			if (site is null)
			{
				return null;
			}

			config.ResolveForSite(site);
			return Build(config);
		}
	}
}

namespace CrawlerSuite.Core.Findings
{
	/// <summary>
	/// Neutral names for page sources in text that leaves the machine (the copies for AI): each
	/// distinct source value becomes "source value N" and each label "label N", numbered in
	/// ordinal order of the real text, so a copy shows how the rows spread without carrying the
	/// CMS's words.
	/// </summary>
	public sealed class NeutralSourceNames
	{
		private readonly Dictionary<string, int> values;
		private readonly Dictionary<string, int> labels;

		public NeutralSourceNames(IEnumerable<PageSource?> sources)
		{
			var all = sources.OfType<PageSource>().ToList();
			values = Number(all.Select(s => s.Value));
			labels = Number(all.Select(s => s.Label));
		}

		/// <summary>
		/// The neutral name of one row's source with the configured key it matched:
		/// "source value 1 (local)", "source value 2 + label 1", "no source value", or "no source".
		/// </summary>
		public string Name(PageSource? source)
		{
			if (source is null || (source.Value.Length == 0 && source.Label.Length == 0))
			{
				return "no source";
			}

			var value = source.Value.Length == 0 ? "no source value" : $"source value {values[source.Value]}";
			value += source.Role switch
			{
				PageSourceRole.Local => " (local)",
				PageSourceRole.External => " (external)",
				_ => string.Empty,
			};
			return source.Label.Length == 0 ? value : $"{value} + label {labels[source.Label]}";
		}

		/// <summary>Rows per neutral name, most rows first (then by name).</summary>
		public IReadOnlyList<(string Name, int Rows)> Count(IEnumerable<PageSource?> rows) =>
			rows.GroupBy(Name, StringComparer.Ordinal)
				.Select(g => (g.Key, g.Count()))
				.OrderByDescending(c => c.Item2)
				.ThenBy(c => c.Key, StringComparer.Ordinal)
				.ToList();

		private static Dictionary<string, int> Number(IEnumerable<string> texts)
		{
			var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var text in texts.Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
			{
				numbers[text] = numbers.Count + 1;
			}

			return numbers;
		}
	}
}
