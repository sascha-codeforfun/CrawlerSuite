namespace CrawlerSuite.Core.Security
{
	using System.Globalization;
	using System.Text;
	using System.Text.Json;
	using System.Text.Json.Nodes;

	/// <summary>
	/// Every address a site's host names had across its crawls: one file in the site folder,
	/// next to the triage ledger but apart from it, so a crawl compares against one file instead of
	/// walking every snapshot, and deleting a snapshot does not erase what it saw. A crawl reports an
	/// address as new only when the host never had it: a pool that rotates (a CDN, round-robin DNS)
	/// settles after a few crawls. An address of a class the host never had (public before, private
	/// now) is reported prominently. Written by a crawl only; a replay never touches it.
	/// </summary>
	public sealed class DnsHistory
	{
		public const string FileName = "dns-history.json";

		/// <summary>The ways an address was seen.</summary>
		public const string LookedUp = "looked up";

		public const string LookedUpThroughProxy = "looked up (proxy in between)";

		public const string ConnectedTo = "connected";

		/// <summary>One address of one host: the first and last snapshot it was seen in, and how.</summary>
		public sealed record Entry(string Host, string Address, string FirstSeen, string LastSeen, IReadOnlyList<string> SeenAs)
		{
			public DnsRecord.AddressClass Class => DnsRecord.ClassOf(Address);
		}

		/// <summary>An address a host never had before; prominent when its class is new to the host too.</summary>
		public sealed record Change(string Host, string Address, DnsRecord.AddressClass Class, bool Prominent, IReadOnlyList<DnsRecord.AddressClass> ClassesBefore)
		{
			public string Text => Prominent
				? string.Create(CultureInfo.InvariantCulture, $"{Host}: {Address} ({DnsRecord.Describe(Class)}) — before only {string.Join(", ", ClassesBefore.Select(DnsRecord.Describe))}")
				: string.Create(CultureInfo.InvariantCulture, $"{Host}: new {Address} ({DnsRecord.Describe(Class)})");
		}

		private sealed class Seen
		{
			public required string First;
			public required string Last;
			public readonly SortedSet<string> How = new(StringComparer.Ordinal);
		}

		private readonly Dictionary<(string Host, string Address), Seen> entries = new();

		public bool IsEmpty => entries.Count == 0;

		/// <summary>Newest last seen first, then by host and address.</summary>
		public IReadOnlyList<Entry> Entries => entries
			.OrderByDescending(e => e.Value.Last, StringComparer.Ordinal)
			.ThenBy(e => e.Key.Host, StringComparer.Ordinal)
			.ThenBy(e => e.Key.Address, StringComparer.Ordinal)
			.Select(e => new Entry(e.Key.Host, e.Key.Address, e.Value.First, e.Value.Last, e.Value.How.ToList()))
			.ToList();

		/// <summary>The addresses of a snapshot no host had before. Empty while the history is empty (a first record) and for a host seen the first time.</summary>
		public IReadOnlyList<Change> Changes(DnsRecord.Snapshot current)
		{
			var changes = new List<Change>();
			foreach (var host in current.Resolved.Concat(current.Connected).Select(h => h.Host.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal))
			{
				var before = entries.Keys.Where(k => k.Host == host).Select(k => k.Address).ToHashSet(StringComparer.Ordinal);
				if (before.Count == 0)
				{
					continue;
				}

				var classesBefore = before.Select(DnsRecord.ClassOf).Distinct().Order().ToList();
				foreach (var address in current.AddressesOf(host).Where(a => !before.Contains(a)).Order(StringComparer.Ordinal))
				{
					var cls = DnsRecord.ClassOf(address);
					changes.Add(new Change(host, address, cls, !classesBefore.Contains(cls), classesBefore));
				}
			}

			return changes;
		}

		/// <summary>
		/// The addresses of a snapshot no website has — loopback, link-local, this host — for every
		/// host, seen before or not, so a first crawl or a newly allowed subdomain is covered too.
		/// Private addresses are not among them: an intranet site is private by design.
		/// </summary>
		public static IReadOnlyList<(string Host, string Address, DnsRecord.AddressClass Class)> NeverAWebsite(DnsRecord.Snapshot snapshot) =>
			snapshot.Resolved.Concat(snapshot.Connected)
				.SelectMany(h => h.Addresses.Select(a => (Host: h.Host.ToLowerInvariant(), Address: a, Class: DnsRecord.ClassOf(a))))
				.Where(x => x.Class is DnsRecord.AddressClass.Loopback or DnsRecord.AddressClass.LinkLocal or DnsRecord.AddressClass.ThisHost)
				.Distinct()
				.OrderBy(x => x.Host, StringComparer.Ordinal).ThenBy(x => x.Address, StringComparer.Ordinal)
				.ToList();

		/// <summary>The warning for an address no website has.</summary>
		public static string NeverAWebsiteText((string Host, string Address, DnsRecord.AddressClass Class) x) =>
			$"{x.Host}: {x.Address} ({DnsRecord.Describe(x.Class)}) — no website's address";

		/// <summary>Takes a snapshot's addresses in.</summary>
		public void Add(DnsRecord.Snapshot snapshot)
		{
			foreach (var (list, how) in new[]
			{
				(snapshot.Resolved, snapshot.ThroughProxy ? LookedUpThroughProxy : LookedUp),
				(snapshot.Connected, ConnectedTo),
			})
			{
				foreach (var host in list)
				{
					foreach (var address in host.Addresses)
					{
						Note(host.Host.ToLowerInvariant(), address, snapshot.Name, snapshot.Name, [how]);
					}
				}
			}
		}

		private void Note(string host, string address, string first, string last, IEnumerable<string> how)
		{
			if (!entries.TryGetValue((host, address), out var seen))
			{
				seen = new Seen { First = first, Last = last };
				entries[(host, address)] = seen;
			}

			if (string.CompareOrdinal(first, seen.First) < 0)
			{
				seen.First = first;
			}

			if (string.CompareOrdinal(last, seen.Last) > 0)
			{
				seen.Last = last;
			}

			seen.How.UnionWith(how);
		}

		/// <summary>The site's history: empty when there is none yet; null when the file cannot be read (then it is neither compared nor overwritten).</summary>
		public static DnsHistory? Read(string siteFolder)
		{
			var history = new DnsHistory();
			var path = Path.Combine(siteFolder, FileName);
			if (!File.Exists(path))
			{
				return history;
			}

			try
			{
				if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || root["Addresses"] is not JsonArray rows)
				{
					return null;
				}

				foreach (var o in rows.OfType<JsonObject>())
				{
					var host = o["Host"]?.GetValue<string>() ?? string.Empty;
					var address = o["Address"]?.GetValue<string>() ?? string.Empty;
					var first = o["FirstSeen"]?.GetValue<string>() ?? string.Empty;
					var last = o["LastSeen"]?.GetValue<string>() ?? first;
					if (host.Length == 0 || address.Length == 0 || first.Length == 0)
					{
						continue;
					}

					var how = (o["SeenAs"] as JsonArray ?? []).Select(h => h?.GetValue<string>() ?? string.Empty).Where(h => h.Length > 0);
					history.Note(host.ToLowerInvariant(), address, first, last, how);
				}

				return history;
			}
			catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
			{
				return null;
			}
		}

		/// <summary>Writes the history into the site folder, replacing the file in one step.</summary>
		public void Write(string siteFolder)
		{
			var root = new JsonObject
			{
				["Addresses"] = new JsonArray(Entries.Select(e => (JsonNode?)new JsonObject
				{
					["Host"] = e.Host,
					["Address"] = e.Address,
					["FirstSeen"] = e.FirstSeen,
					["LastSeen"] = e.LastSeen,
					["SeenAs"] = new JsonArray(e.SeenAs.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
				}).ToArray()),
			};

			var path = Path.Combine(siteFolder, FileName);
			var temp = path + ".tmp";
			File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
			File.Move(temp, path, overwrite: true);
		}
	}
}
