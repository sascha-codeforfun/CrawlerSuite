namespace CrawlerSuite.Core.Settings
{
	using System.Collections.Concurrent;
	using System.Globalization;
	using System.Text;
	using System.Text.Json;
	using System.Text.Json.Nodes;
	using System.Xml.XPath;
	using CrawlerSuite.Core.Boilerplate;
	using CrawlerSuite.Core.SpellCheck;
	using HtmlAgilityPack;

	/// <summary>How often one entry was found in a run: on how many pages, how many elements.</summary>
	public sealed record UsageCount(int Pages, int Matches);

	/// <summary>
	/// How much each entry of some settings was needed in a run — the technical XPaths, the
	/// attributes not checked and the boilerplate parts — so the settings screen can show it
	/// and an entry that matches nothing can be recognised. Switched-off entries are counted
	/// too (without being applied), so turning one off shows whether it would still have hit.
	/// Written once per run next to the logs as <see cref="FileName"/>.
	/// </summary>
	public sealed class SettingsUsage
	{
		public const string FileName = "settings-usage.json";

		/// <summary>
		/// What only a crawl can count (the download exclusions), in the same shape as
		/// <see cref="FileName"/> but a file of its own: a re-run clears the snapshot of everything but
		/// the crawl's ground truth, and this file is part of it (SnapshotFlows keeps it). Read
		/// together with <see cref="FileName"/>; its settings take precedence.
		/// </summary>
		public const string CrawlFileName = "crawl-usage.json";

		public const string Xpaths = "SpellCheckEngine.GlobalXpathToIgnore";
		public const string Attributes = "SpellCheckEngine.GlobalAttributesToIgnore";
		public const string Boilerplate = "SpellCheckEngine.BoilerplateGroups";

		private readonly IReadOnlyList<string> xpaths;
		private readonly HashSet<string> attributes;
		private readonly BoilerplateResolver resolver;
		private readonly ConcurrentDictionary<(string Setting, string Key), int[]> counts = new();
		private readonly ConcurrentDictionary<string, byte> referencePagesSeen = new(StringComparer.OrdinalIgnoreCase);
		private int pages;

		public SettingsUsage(SpellCheckEngineConfig config)
		{
			xpaths = config.GlobalXpathToIgnore.Concat(config.GlobalXpathToIgnore.Disabled)
				.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
			attributes = new HashSet<string>(
				config.GlobalAttributesToIgnore.Concat(config.GlobalAttributesToIgnore.Disabled).Where(a => !string.IsNullOrWhiteSpace(a)),
				StringComparer.OrdinalIgnoreCase);
			resolver = new BoilerplateResolver(config.BoilerplateGroups);

			// Every entry is in the file, also one that matched nothing (that is the point).
			foreach (var xpath in xpaths)
			{
				counts.TryAdd((Xpaths, xpath), new int[2]);
			}

			foreach (var attribute in attributes)
			{
				counts.TryAdd((Attributes, attribute), new int[2]);
			}

			foreach (var group in config.BoilerplateGroups)
			{
				foreach (var part in group.BoilerplateSelectors.Where(p => !string.IsNullOrWhiteSpace(p.Value)))
				{
					counts.TryAdd((Boilerplate, PartKey(group.PathPrefix, part.Type, part.Value)), new int[2]);
				}
			}
		}

		/// <summary>The key of a boilerplate part: its group and the part itself.</summary>
		public static string PartKey(string pathPrefix, string type, string value) =>
			$"{pathPrefix}\u001F{type?.Trim().ToLowerInvariant()}\u001F{value?.Trim()}";

		/// <summary>A reference page as the usage records it (its path, as the boilerplate rules compare it).</summary>
		public static string ReferenceKey(string page) => BoilerplateResolver.NormalizePath(page);

		/// <summary>Counts one page (thread-safe; called from the parallel page loop).</summary>
		public void CountPage(HtmlDocument doc, string url)
		{
			Interlocked.Increment(ref pages);

			foreach (var xpath in xpaths)
			{
				Add(Xpaths, xpath, Select(doc, xpath));
			}

			var (group, isReferencePage, path) = resolver.Govern(url);
			if (isReferencePage)
			{
				referencePagesSeen.TryAdd(path, 0);
			}

			var classParts = group?.BoilerplateSelectors
				.Where(s => string.Equals(s.Type?.Trim(), "class", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.Value))
				.Select(s => (Key: PartKey(group.PathPrefix, s.Type!, s.Value), Tokens: s.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
				.ToList() ?? [];

			if (attributes.Count > 0 || classParts.Count > 0)
			{
				var attributeHits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				var classHits = new int[classParts.Count];
				foreach (var node in doc.DocumentNode.Descendants())
				{
					if (node.NodeType != HtmlNodeType.Element)
					{
						continue;
					}

					foreach (var attribute in node.Attributes)
					{
						if (attributes.Contains(attribute.Name))
						{
							attributeHits[attribute.Name] = attributeHits.GetValueOrDefault(attribute.Name) + 1;
						}
					}

					if (classParts.Count > 0 && node.GetClasses() is var classes)
					{
						var set = new HashSet<string>(classes, StringComparer.Ordinal);
						if (set.Count > 0)
						{
							for (int i = 0; i < classParts.Count; i++)
							{
								if (classParts[i].Tokens.All(set.Contains))
								{
									classHits[i]++;
								}
							}
						}
					}
				}

				foreach (var name in attributes)
				{
					Add(Attributes, name, attributeHits.GetValueOrDefault(name));
				}

				for (int i = 0; i < classParts.Count; i++)
				{
					Add(Boilerplate, classParts[i].Key, classHits[i]);
				}
			}

			foreach (var part in group?.BoilerplateSelectors.Where(s => string.Equals(s.Type?.Trim(), "xpath", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrWhiteSpace(s.Value)) ?? [])
			{
				Add(Boilerplate, PartKey(group!.PathPrefix, part.Type, part.Value), Select(doc, part.Value.Trim()));
			}

			// Attribute parts: the elements carrying the attribute (switched-off parts too, like the others).
			foreach (var part in group?.BoilerplateSelectors.Where(s => BoilerplateAttribute.IsAttributeType(s.Type) && !string.IsNullOrWhiteSpace(s.Value)) ?? [])
			{
				int carrying = 0;
				if (BoilerplateAttribute.TryParse(part.Value, out var tag, out var name))
				{
					List<(string? Tag, string Name)> one = [(tag, name)];
					foreach (var node in doc.DocumentNode.Descendants())
					{
						if (node.NodeType == HtmlNodeType.Element && node.HasAttributes
							&& node.Attributes.Any(a => BoilerplateAttribute.Takes(one, node, a.Name)))
						{
							carrying++;
						}
					}
				}

				Add(Boilerplate, PartKey(group!.PathPrefix, part.Type, part.Value), carrying);
			}
		}

		public const string Suppressions = "ContentQuality.ContentQualityIssueSuppressions";
		public const string Patterns = "ContentUnwantedPatterns";

		/// <summary>Script strings to skip, in inline scripts on pages: (pages, strings skipped).</summary>
		public const string ScriptStrings = "SpellCheckEngine.SpellCheckJavaScript.TokensToFilter";

		/// <summary>Script strings to skip, in script files: (files, strings skipped).</summary>
		public const string ScriptStringsInFiles = "SpellCheckEngine.SpellCheckJavaScript.TokensToFilter.ScriptFiles";

		/// <summary>The key of a suppression rule: its type and value.</summary>
		public static string RuleKey(string type, string value) => $"{type?.Trim()}\u001F{value ?? string.Empty}";

		/// <summary>
		/// Writes example texts for the entries of some settings (e.g. the words that need a Known
		/// word part) under <c>Examples</c>, replacing those settings' earlier examples.
		/// </summary>
		public static void MergeExamples(string snapshotDirectory, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> settings)
		{
			var path = Path.Combine(snapshotDirectory, FileName);
			JsonObject root;
			try
			{
				root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing ? existing : new JsonObject();
			}
			catch (JsonException)
			{
				root = new JsonObject();
			}

			var examples = root["Examples"] as JsonObject ?? new JsonObject();
			foreach (var (name, map) in settings)
			{
				var setting = new JsonObject();
				foreach (var (key, texts) in map.OrderBy(c => c.Key, StringComparer.Ordinal))
				{
					setting[key] = new JsonArray(texts.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
				}

				examples[name] = setting;
			}

			root["Examples"] = examples.DeepClone();
			File.WriteAllText(
				path,
				root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
				new UTF8Encoding(false));
		}

		/// <summary>Writes the counts into the snapshot folder; the counts of other writers stay.</summary>
		public void Write(string snapshotDirectory)
		{
			var settings = new Dictionary<string, IReadOnlyDictionary<string, UsageCount>>(StringComparer.Ordinal)
			{
				[Xpaths] = new Dictionary<string, UsageCount>(),
				[Attributes] = new Dictionary<string, UsageCount>(),
				[Boilerplate] = new Dictionary<string, UsageCount>(),
			};
			foreach (var group in counts.GroupBy(c => c.Key.Setting))
			{
				settings[group.Key] = group.ToDictionary(c => c.Key.Key, c => new UsageCount(c.Value[0], c.Value[1]), StringComparer.Ordinal);
			}

			Merge(snapshotDirectory, settings, pages, referencePagesSeen.Keys);
		}

		/// <summary>
		/// Writes the counts of some settings into the snapshot's file, replacing those settings'
		/// earlier counts and keeping every other setting's (content quality and spell-check write
		/// at different steps of a run).
		/// </summary>
		public static void Merge(
			string snapshotDirectory,
			IReadOnlyDictionary<string, IReadOnlyDictionary<string, UsageCount>> settings,
			int? pageCount = null,
			IEnumerable<string>? referencePagesSeen = null,
			string fileName = FileName)
		{
			var path = Path.Combine(snapshotDirectory, fileName);
			JsonObject root;
			try
			{
				root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing ? existing : new JsonObject();
			}
			catch (JsonException)
			{
				root = new JsonObject();
			}

			var entries = root["Entries"] as JsonObject ?? new JsonObject();
			foreach (var (name, map) in settings)
			{
				var setting = new JsonObject();
				foreach (var (key, count) in map.OrderBy(c => c.Key, StringComparer.Ordinal))
				{
					setting[key] = new JsonArray(count.Pages, count.Matches);
				}

				entries[name] = setting;
			}

			root["Entries"] = entries.DeepClone();
			if (pageCount is { } p)
			{
				root["Pages"] = p;
			}

			if (referencePagesSeen is not null)
			{
				root["ReferencePagesSeen"] = new JsonArray(referencePagesSeen.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
			}

			File.WriteAllText(
				path,
				root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
				new UTF8Encoding(false));
		}

		/// <summary>The counts of a snapshot (both files); null when it has none (older, or not analysed).</summary>
		public static UsageReport? Read(string snapshotDirectory)
		{
			try
			{
				var root = RootOf(Path.Combine(snapshotDirectory, FileName));
				var crawl = RootOf(Path.Combine(snapshotDirectory, CrawlFileName));
				if (root is null && crawl is null)
				{
					return null;
				}

				root ??= new JsonObject();
				var entries = new Dictionary<string, IReadOnlyDictionary<string, UsageCount>>(StringComparer.Ordinal);
				// The crawl's own file last: its counts are the crawl's, whatever an older usage file still holds.
				foreach (var source in new[] { root, crawl })
				{
					foreach (var (setting, values) in source?["Entries"] as JsonObject ?? [])
					{
						var map = new Dictionary<string, UsageCount>(StringComparer.Ordinal);
						foreach (var (key, pair) in values as JsonObject ?? [])
						{
							if (pair is JsonArray { Count: 2 } a)
							{
								map[key] = new UsageCount(a[0]!.GetValue<int>(), a[1]!.GetValue<int>());
							}
						}

						entries[setting] = map;
					}
				}

				// "KeeperPagesSeen": an earlier name, still read from usage files written by older runs.
				var referencePages = (root["ReferencePagesSeen"] as JsonArray ?? root["KeeperPagesSeen"] as JsonArray ?? []).Select(k => k?.GetValue<string>() ?? string.Empty);
				var examples = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
				foreach (var (setting, values) in root["Examples"] as JsonObject ?? [])
				{
					var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
					foreach (var (key, list) in values as JsonObject ?? [])
					{
						map[key] = (list as JsonArray ?? []).Select(t => t?.GetValue<string>() ?? string.Empty).ToList();
					}

					examples[setting] = map;
				}

				return new UsageReport(
					root["Pages"]?.GetValue<int>() ?? 0,
					entries,
					new HashSet<string>(referencePages, StringComparer.OrdinalIgnoreCase),
					DayOf(snapshotDirectory),
					examples);
			}
			catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
			{
				return null;
			}
		}

		/// <summary>A usage file's root, or null when there is none.</summary>
		private static JsonObject? RootOf(string path) =>
			File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;

		/// <summary>The day of a snapshot, from its folder name (yyyy-MM-dd-HH-mm-ss).</summary>
		private static DateOnly? DayOf(string snapshotDirectory)
		{
			var name = Path.GetFileName(snapshotDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			return name.Length >= 10 && DateOnly.TryParseExact(name[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
				? day
				: null;
		}

		private static int Select(HtmlDocument doc, string xpath)
		{
			try
			{
				return doc.DocumentNode.SelectNodes(xpath)?.Count ?? 0;
			}
			catch (XPathException)
			{
				return 0;
			}
		}

		private void Add(string setting, string key, int matches)
		{
			var cell = counts.GetOrAdd((setting, key), _ => new int[2]);
			if (matches > 0)
			{
				Interlocked.Increment(ref cell[0]);
				Interlocked.Add(ref cell[1], matches);
			}
		}
	}

	/// <summary>The counts read back from a snapshot.</summary>
	public sealed record UsageReport(
		int Pages,
		IReadOnlyDictionary<string, IReadOnlyDictionary<string, UsageCount>> Entries,
		IReadOnlySet<string> ReferencePagesSeen,
		DateOnly? Day,
		IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>? Examples = null)
	{
		/// <summary>The example texts of one entry (empty when none were written).</summary>
		public IReadOnlyList<string> ExamplesOf(string setting, string key) =>
			Examples is not null && Examples.TryGetValue(setting, out var map) && map.TryGetValue(key, out var list) ? list : [];

		/// <summary>The count of one entry, or null when the run did not have it (added or changed since).</summary>
		public UsageCount? Of(string setting, string key) =>
			Entries.TryGetValue(setting, out var map) && map.TryGetValue(key, out var count) ? count : null;
	}
}
