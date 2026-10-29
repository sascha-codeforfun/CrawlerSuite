namespace CrawlerSuite.Core.Quality
{
	using System.Text.Json;
	using System.Text.Json.Nodes;

	/// <summary>
	/// Keeps a name from a hyphen-spacing finding in <see cref="Config.ProperNames"/>, for the Triage
	/// page: the click writes the phrase as the finding has it, so nothing is typed by hand. Knows
	/// which names the configuration already holds (as the checks read them), and undoes only what
	/// it added itself — an entry that was already there is never removed. The file is read and
	/// written as it stands, every other key untouched.
	/// </summary>
	public sealed class ProperNameKeeper
	{
		private readonly string configPath;
		private readonly Func<string?> blocked;
		private ProperNames known;

		// Key → entry as written, for what this keeper added: what Undo may take back.
		private readonly Dictionary<string, string> added = new(StringComparer.Ordinal);

		/// <param name="configPath">The configuration file.</param>
		/// <param name="blocked">Why the file may not be written right now (a run, unsaved settings); null when it may.</param>
		public ProperNameKeeper(string configPath, Func<string?>? blocked = null)
		{
			this.configPath = configPath ?? string.Empty;
			this.blocked = blocked ?? (() => null);
			known = new ProperNames(ReadEntries(Read()));
		}

		/// <summary>No configuration known: nothing can be kept.</summary>
		public static ProperNameKeeper None { get; } = new(string.Empty);

		public bool CanWrite => configPath.Length > 0;

		/// <summary>True when the configuration holds <paramref name="phrase"/> as a name.</summary>
		public bool IsKept(string phrase) => known.Has(phrase);

		/// <summary>True when this keeper added <paramref name="phrase"/> and can take it back.</summary>
		public bool CanUndo(string phrase) => added.ContainsKey(ProperNames.Key(phrase));

		/// <summary>Adds <paramref name="phrase"/> to the proper names; null when done, else the reason it was not.</summary>
		public string? Keep(string phrase)
		{
			if (!CanWrite)
			{
				return "No configuration file is known.";
			}

			if (blocked() is { } reason)
			{
				return reason;
			}

			var entry = string.Join(' ', phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
			var key = ProperNames.Key(entry);
			try
			{
				var root = Read() ?? [];
				var list = root["ProperNames"] as JsonArray ?? [];
				if (list.Parent is null)
				{
					root["ProperNames"] = list;
				}

				// Already there: the same outcome, but not this click's to undo.
				if (!ReadEntries(root).Any(e => ProperNames.Key(e.Name) == key))
				{
					list.Add(new JsonObject { ["Name"] = entry });
					ConfigFile.Save(configPath, root);
					added[key] = entry;
				}

				known = new ProperNames(ReadEntries(root));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				Logger.LogWarning($"Triage: could not add a proper name to {Path.GetFileName(configPath)} — {ex.Message}");
				return ex.Message;
			}

			return null;
		}

		/// <summary>Removes what <see cref="Keep"/> added for <paramref name="phrase"/>; null when done, else the reason it was not.</summary>
		public string? Undo(string phrase)
		{
			var key = ProperNames.Key(phrase);
			if (!added.ContainsKey(key))
			{
				return null;
			}

			if (blocked() is { } reason)
			{
				return reason;
			}

			try
			{
				var root = Read() ?? [];
				if (root["ProperNames"] is JsonArray list)
				{
					// The entry this keeper wrote: a name and nothing else. One given an acronym since is left alone.
					var mine = list.FirstOrDefault(n => n is JsonObject o && o.Count == 1
						&& o["Name"] is JsonValue v && v.TryGetValue<string>(out var name) && ProperNames.Key(name) == key);
					if (mine is not null)
					{
						list.Remove(mine);
						ConfigFile.Save(configPath, root);
					}
				}

				added.Remove(key);
				known = new ProperNames(ReadEntries(root));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				Logger.LogWarning($"Triage: could not remove a proper name from {Path.GetFileName(configPath)} — {ex.Message}");
				return ex.Message;
			}

			return null;
		}

		private JsonObject? Read()
		{
			if (!CanWrite)
			{
				return null;
			}

			try
			{
				return ConfigFile.Read(configPath);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				return null;
			}
		}

		private static List<ProperName> ReadEntries(JsonObject? root) =>
			(root?["ProperNames"] as JsonArray ?? [])
				.Select(n => n is JsonObject o && o["Name"] is JsonValue v && v.TryGetValue<string>(out var name)
					? new ProperName { Name = name }
					: null)
				.Where(e => e is not null)
				.Select(e => e!)
				.ToList();
	}
}
