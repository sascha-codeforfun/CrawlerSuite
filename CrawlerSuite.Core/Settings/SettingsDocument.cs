namespace CrawlerSuite.Core.Settings
{
	using System.Text.Json;
	using System.Text.Json.Nodes;

	/// <summary>
	/// crawlersuite.config.json while the settings screen edits it: the file's tree, changed in
	/// memory and written on Save. A setting present in the tree is an override (unlocked on the
	/// screen); an absent one uses its built-in default (locked). Removing the last setting of a
	/// group removes the group, so the file only ever holds what differs from the defaults.
	/// </summary>
	public sealed class SettingsDocument
	{
		public const string SitesKey = "Sites";

		private readonly JsonObject root;

		private SettingsDocument(JsonObject root, bool existed)
		{
			this.root = root;
			Existed = existed;
		}

		/// <summary>Whether the file existed when it was read.</summary>
		public bool Existed { get; }

		/// <summary>Whether anything changed since it was read or last saved.</summary>
		public bool IsDirty { get; private set; }

		/// <summary>Reads the file; a missing file is an empty document. Throws <see cref="JsonException"/> on broken JSON.</summary>
		public static SettingsDocument Load(string path)
		{
			var read = ConfigFile.Read(path);
			return new SettingsDocument(read ?? new JsonObject(), read is not null);
		}

		/// <summary>A document over a given tree (tests).</summary>
		public static SettingsDocument From(JsonObject root) => new(root, existed: true);

		/// <summary>The value at a dotted path, or null when the file does not have it.</summary>
		public JsonNode? Get(string path)
		{
			JsonNode? node = root;
			foreach (var part in path.Split('.'))
			{
				if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node))
				{
					return null;
				}
			}

			return node;
		}

		/// <summary>Whether the file has the setting (it is unlocked).</summary>
		public bool Has(string path)
		{
			JsonNode? node = root;
			foreach (var part in path.Split('.'))
			{
				if (node is not JsonObject obj || !obj.ContainsKey(part))
				{
					return false;
				}

				node = obj[part];
			}

			return true;
		}

		/// <summary>Sets a value, creating the groups on the way.</summary>
		public void Set(string path, JsonNode? value)
		{
			var parts = path.Split('.');
			var obj = root;
			foreach (var part in parts[..^1])
			{
				if (obj[part] is not JsonObject next)
				{
					next = new JsonObject();
					obj[part] = next;
				}

				obj = next;
			}

			var current = obj.ContainsKey(parts[^1]) ? obj[parts[^1]] : null;
			if (obj.ContainsKey(parts[^1]) && JsonNode.DeepEquals(current, value))
			{
				return;
			}

			obj[parts[^1]] = value?.DeepClone();
			IsDirty = true;
		}

		/// <summary>Removes a setting; groups left empty go with it.</summary>
		public void Remove(string path)
		{
			var parts = path.Split('.');
			var chain = new List<JsonObject> { root };
			foreach (var part in parts[..^1])
			{
				if (chain[^1][part] is not JsonObject next)
				{
					return;
				}

				chain.Add(next);
			}

			if (!chain[^1].Remove(parts[^1]))
			{
				return;
			}

			IsDirty = true;
			for (int i = chain.Count - 1; i > 0; i--)
			{
				if (chain[i].Count > 0)
				{
					break;
				}

				chain[i - 1].Remove(parts[i - 1]);
			}
		}

		/// <summary>The sites, as the file has them (an empty list when it has none).</summary>
		public JsonArray Sites => root[SitesKey] as JsonArray ?? [];

		/// <summary>The sites as editable entries.</summary>
		[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Sites are read with reflection-based JSON.")]
		public List<SiteDraft> ReadSites() =>
			Sites.Select(s => s is null ? new SiteDraft() : s.Deserialize<SiteDraft>() ?? new SiteDraft()).ToList();

		/// <summary>Replaces the sites.</summary>
		[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Sites are written with reflection-based JSON.")]
		public void WriteSites(IEnumerable<SiteDraft> sites) =>
			Set(SitesKey, new JsonArray(sites.Select(s => JsonSerializer.SerializeToNode(s, SiteJson)).ToArray()));

		private static readonly JsonSerializerOptions SiteJson = new()
		{
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};

		/// <summary>
		/// Keys in the file the app has no setting for — left over from older versions, or typos.
		/// They are dropped when the file is saved. The proxy password is among them: it is never
		/// kept in the file.
		/// </summary>
		public IReadOnlyList<string> UnknownKeys()
		{
			var known = new HashSet<string>(SettingsCatalog.All.Select(s => s.Path), StringComparer.Ordinal) { SitesKey };
			var groups = new HashSet<string>(StringComparer.Ordinal);
			foreach (var path in known)
			{
				var parts = path.Split('.');
				for (int i = 1; i < parts.Length; i++)
				{
					groups.Add(string.Join('.', parts[..i]));
				}
			}

			var unknown = new List<string>();
			void Walk(JsonObject obj, string prefix)
			{
				foreach (var (key, value) in obj)
				{
					var path = prefix.Length == 0 ? key : prefix + "." + key;
					if (known.Contains(path))
					{
						continue;
					}

					if (groups.Contains(path) && value is JsonObject group)
					{
						Walk(group, path);
						continue;
					}

					unknown.Add(path);
				}
			}

			Walk(root, string.Empty);
			return unknown;
		}

		/// <summary>Settings the file has with exactly their built-in default — they could go without changing anything.</summary>
		[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reads the defaults by reflection.")]
		public IReadOnlyList<SettingInfo> RestatedDefaults() =>
			SettingsCatalog.All
				.Where(s => Has(s.Path) && JsonNode.DeepEquals(Get(s.Path), SettingsCatalog.DefaultOf(s.Path)))
				.ToList();

		/// <summary>The file text as it would be saved: unknown keys dropped, in the file layout.</summary>
		public string ToSavedText()
		{
			var copy = (JsonObject)root.DeepClone();
			var doc = new SettingsDocument(copy, Existed);
			foreach (var key in UnknownKeys())
			{
				doc.Remove(key);
			}

			return ConfigFile.Format(copy);
		}

		/// <summary>
		/// Checks the document as the app will read it: the same validation as at start. Null
		/// when it is valid; else what is wrong, in the validation's own words.
		/// </summary>
		[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Configuration is read with reflection-based JSON.")]
		public string? Validate()
		{
			try
			{
				Config.Parse(ToSavedText());
				return null;
			}
			catch (JsonException ex)
			{
				return "A value has the wrong form: " + ex.Message;
			}
			catch (ConfigHaltException halt)
			{
				return halt.Heading + Environment.NewLine + string.Join(Environment.NewLine, halt.Lines);
			}
			catch (InvalidOperationException ex)
			{
				return ex.Message;
			}
		}

		/// <summary>Writes the file (unknown keys dropped, backup before the first write while the app runs).</summary>
		public void Save(string path)
		{
			foreach (var key in UnknownKeys())
			{
				Remove(key);
			}

			ConfigFile.Save(path, root);
			IsDirty = false;
		}
	}
}
