namespace CrawlerSuite.Core.Settings
{
	using System.Text.Json.Nodes;

	/// <summary>One site's view of a setting with placeholders.</summary>
	/// <param name="MaskedLabel">The site's name under the screen mask ("Primary site", "Site 2", …), numbered as the mask numbers them.</param>
	/// <param name="NotUsed">The setting belongs to the CMS content list and this site does not use it.</param>
	public sealed record PlaceholderLine(string SiteName, string MaskedLabel, IReadOnlyList<string> Values, bool NotUsed = false);

	/// <summary>
	/// What a setting with <c>{tenant}</c> or <c>{productiongroup}</c> becomes for each site, by the
	/// same rules the crawl uses. Only some settings resolve placeholders; in the others they stay
	/// as written, which the screen says instead.
	/// </summary>
	public static class Placeholders
	{
		/// <summary>Whether the value (a text or a list of texts) holds a placeholder.</summary>
		public static bool HasAny(JsonNode? value) => Texts(value).Any(Config.HasToken);

		/// <summary>Whether placeholders are resolved in this setting.</summary>
		public static bool IsResolved(string path) => Config.SubstitutableSettings.Contains(path, StringComparer.Ordinal);

		/// <summary>Whether a setting belongs to the CMS content list (and so means nothing for a site that does not use it).</summary>
		public static bool IsContentListSetting(string path) =>
			path.StartsWith("CmsContentList.", StringComparison.Ordinal) || path.StartsWith("TicketGeneration.CmsEditor", StringComparison.Ordinal);

		/// <summary>The value for each site, in site order.</summary>
		public static IReadOnlyList<PlaceholderLine> Resolve(JsonNode? value, IReadOnlyList<SiteDraft> sites, string? path = null)
		{
			bool contentList = path is not null && IsContentListSetting(path);
			var texts = Texts(value).ToList();
			var lines = new List<PlaceholderLine>();
			int next = 2;
			foreach (var site in sites)
			{
				var label = site.IsPrimary ? Display.ScreenMask.PrimaryLabel : $"Site {next++}";
				lines.Add(contentList && !site.UsesContentListResolved
					? new PlaceholderLine(site.Name, label, [], NotUsed: true)
					: new PlaceholderLine(site.Name, label, texts.Select(t => Config.ResolveTokens(t, site.Tenant, site.ProductionGroup)).ToList()));
			}

			return lines;
		}

		private static IEnumerable<string> Texts(JsonNode? value) => value switch
		{
			JsonValue v when v.TryGetValue<string>(out var text) => [text],
			JsonArray list => list.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var t) ? t : null).OfType<string>(),
			_ => [],
		};
	}
}
