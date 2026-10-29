namespace CrawlerSuite.Core.Quality
{
	/// <summary>
	/// Which unwanted-pattern findings are security-relevant: those of a group marked
	/// <see cref="ContentUnwantedPattern.SecurityRelevant"/> in the loaded configuration. A finding
	/// names its group in the detail ("Category: Name — …"); the group decides, never the
	/// category text. Set when the configuration loads; read by Results, Triage and tickets.
	/// </summary>
	public static class SecurityPatterns
	{
		public const string Type = "UNWANTED_PATTERN";

		private static IReadOnlySet<string> groups = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>The names of the security-relevant groups (switched-off ones included).</summary>
		public static IReadOnlySet<string> Groups => groups;

		/// <summary>Takes the groups from a loaded configuration.</summary>
		public static void Learn(IEnumerable<ContentUnwantedPattern>? patterns) =>
			groups = new HashSet<string>(
				(patterns ?? []).Where(p => p.SecurityRelevant && !string.IsNullOrEmpty(p.Name)).Select(p => p.Name),
				StringComparer.Ordinal);

		/// <summary>Whether a finding of a type with a detail is security-relevant.</summary>
		public static bool IsSecurity(string? type, string? detail) =>
			groups.Count > 0
			&& string.Equals(BaseType(type), Type, StringComparison.Ordinal)
			&& GroupOf(detail) is { } name
			&& groups.Contains(name);

		/// <summary>The group name in an unwanted-pattern detail ("Category: Name — …"), or null.</summary>
		public static string? GroupOf(string? detail)
		{
			if (string.IsNullOrEmpty(detail))
			{
				return null;
			}

			var colon = detail.IndexOf(": ", StringComparison.Ordinal);
			if (colon < 0)
			{
				return null;
			}

			var rest = detail[(colon + 2)..];
			var dash = rest.IndexOf(" — ", StringComparison.Ordinal);
			return (dash < 0 ? rest : rest[..dash]).Trim();
		}

		private static string BaseType(string? type) =>
			type is null ? string.Empty : (type.IndexOf(':') is var i && i > 0 ? type[..i] : type);
	}
}
