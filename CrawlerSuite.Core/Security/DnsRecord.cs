namespace CrawlerSuite.Core.Security
{
	using System.Collections.Concurrent;
	using System.Globalization;
	using System.Net;
	using System.Net.Sockets;
	using System.Text;
	using System.Text.Json;
	using System.Text.Json.Nodes;

	/// <summary>
	/// Where the site's host names pointed during a crawl: what each allowed host resolved to
	/// at crawl start, and — without a proxy — the addresses the crawl's connections actually went
	/// to. Recorded, compared with the site's earlier snapshots and reported as information; nothing
	/// is refused. The admission gate checks a URL's text (no IP literals, allowed hosts only); where
	/// a name resolves is its owner's matter, and a change is worth knowing, not stopping (an
	/// intranet site or split DNS resolves to private addresses by design).
	/// Written as <see cref="FileName"/> into the snapshot, which a re-run keeps; the site's
	/// <see cref="DnsHistory"/> keeps every address of every crawl.
	/// </summary>
	public sealed class DnsRecord
	{
		public const string FileName = "dns.json";

		/// <summary>An address's class, as plain information.</summary>
		public enum AddressClass
		{
			Public,
			Private,
			Loopback,
			LinkLocal,
			SharedNat,

			/// <summary>0.0.0.0/8: a connection to it reaches this machine on Windows and Linux.</summary>
			ThisHost,
		}

		/// <summary>How long one host's lookup may take at crawl start; the lookups run side by side.</summary>
		internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(10);

		private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> connected = new(StringComparer.OrdinalIgnoreCase);

		/// <param name="hosts">The hosts the crawl may request (the site and its allowed subdomains).</param>
		/// <param name="throughProxy">A proxy resolves the names itself: the crawl sees only the proxy's address.</param>
		public DnsRecord(IEnumerable<string> hosts, bool throughProxy)
		{
			Hosts = hosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToList();
			ThroughProxy = throughProxy;
		}

		public IReadOnlyList<string> Hosts { get; }

		public bool ThroughProxy { get; }

		/// <summary>Each host's addresses at crawl start (as this machine resolves them); the error when the lookup failed.</summary>
		public IReadOnlyDictionary<string, (IReadOnlyList<string> Addresses, string? Error)> Resolved { get; private set; } =
			new Dictionary<string, (IReadOnlyList<string>, string?)>();

		/// <summary>The hosts of the site and its allowed subdomains of a configuration.</summary>
		public static IEnumerable<string> HostsOf(string siteUrl, IEnumerable<string>? subdomainBaseUrls)
		{
			foreach (var url in new[] { siteUrl }.Concat(subdomainBaseUrls ?? []))
			{
				if (Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.IdnHost.Length > 0)
				{
					yield return uri.IdnHost;
				}
			}
		}

		/// <summary>
		/// Looks every host up once, as this machine resolves it: side by side, each within
		/// <see cref="LookupTimeout"/>, so a resolver that answers slowly (a proxy network often
		/// cannot resolve outside names) holds the crawl's start for that long at most.
		/// </summary>
		public async Task ResolveAsync(CancellationToken cancel = default)
		{
			var lookups = Hosts.Select(async host =>
			{
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
				timeout.CancelAfter(LookupTimeout);
				try
				{
					var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
					return new HostAddresses(host, addresses.Select(Text).Distinct().Order(StringComparer.Ordinal).ToList(), null);
				}
				catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
				{
					return new HostAddresses(host, [], string.Create(CultureInfo.InvariantCulture, $"no answer within {LookupTimeout.TotalSeconds:0} s"));
				}
				catch (Exception ex) when (ex is SocketException or ArgumentException)
				{
					return new HostAddresses(host, [], ex.Message);
				}
			}).ToList();

			var done = await Task.WhenAll(lookups).ConfigureAwait(false);
			Resolved = done.ToDictionary(d => d.Host, d => (d.Addresses, d.Error), StringComparer.OrdinalIgnoreCase);
		}

		/// <summary>This crawl's record as a snapshot's (what the history takes in).</summary>
		public Snapshot ToSnapshot(string name) => new(name, ThroughProxy,
			Resolved.Select(r => new HostAddresses(r.Key, r.Value.Addresses, r.Value.Error)).ToList(),
			ConnectedAddresses.Select(c => new HostAddresses(c.Key, c.Value, null)).ToList());

		/// <summary>One connection of the crawl: the host it was for and the address it went to.</summary>
		public void Connected(string host, IPAddress address) =>
			connected.GetOrAdd(host.ToLowerInvariant(), _ => new(StringComparer.Ordinal)).TryAdd(Text(address), 0);

		/// <summary>The addresses the crawl connected to, per host.</summary>
		public IReadOnlyDictionary<string, IReadOnlyList<string>> ConnectedAddresses =>
			connected.ToDictionary(c => c.Key, c => (IReadOnlyList<string>)c.Value.Keys.Order(StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// The crawl's connect step, recording where each connection went: the same as the
		/// handler's own (a dual-mode socket, every address of the name tried in turn, no Nagle).
		/// Installed only without a proxy — through one, every connection goes to the proxy.
		/// </summary>
		public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
		{
			var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
			try
			{
				await socket.ConnectAsync(context.DnsEndPoint, cancel).ConfigureAwait(false);
				if (socket.RemoteEndPoint is IPEndPoint remote)
				{
					Connected(context.DnsEndPoint.Host, remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address);
				}

				return new NetworkStream(socket, ownsSocket: true);
			}
			catch
			{
				socket.Dispose();
				throw;
			}
		}

		private static string Text(IPAddress address) =>
			(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

		/// <summary>Loopback, private, link-local, shared (carrier-grade) NAT or public — information, not a verdict.</summary>
		public static AddressClass ClassOf(string address)
		{
			if (!IPAddress.TryParse(address, out var ip))
			{
				return AddressClass.Public;
			}

			if (IPAddress.IsLoopback(ip))
			{
				return AddressClass.Loopback;
			}

			if (ip.AddressFamily == AddressFamily.InterNetwork)
			{
				var b = ip.GetAddressBytes();
				return b[0] switch
				{
					0 => AddressClass.ThisHost,
					10 => AddressClass.Private,
					172 when b[1] is >= 16 and <= 31 => AddressClass.Private,
					192 when b[1] == 168 => AddressClass.Private,
					169 when b[1] == 254 => AddressClass.LinkLocal,
					100 when b[1] is >= 64 and <= 127 => AddressClass.SharedNat,
					_ => AddressClass.Public,
				};
			}

			if (ip.IsIPv6LinkLocal)
			{
				return AddressClass.LinkLocal;
			}

			return ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal ? AddressClass.Private : AddressClass.Public;
		}

		/// <summary>The class in words.</summary>
		public static string Describe(AddressClass c) => c switch
		{
			AddressClass.Private => "private",
			AddressClass.Loopback => "loopback",
			AddressClass.LinkLocal => "link-local",
			AddressClass.SharedNat => "shared NAT",
			AddressClass.ThisHost => "this host",
			_ => "public",
		};

		// ── The file ─────────────────────────────────────────────────────────────

		/// <summary>One host's addresses in one snapshot.</summary>
		public sealed record HostAddresses(string Host, IReadOnlyList<string> Addresses, string? Error);

		/// <summary>What one snapshot recorded.</summary>
		public sealed record Snapshot(string Name, bool ThroughProxy, IReadOnlyList<HostAddresses> Resolved, IReadOnlyList<HostAddresses> Connected)
		{
			/// <summary>Every address a host had in this snapshot, resolved or connected to.</summary>
			public IReadOnlySet<string> AddressesOf(string host) =>
				Resolved.Concat(Connected).Where(h => h.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
					.SelectMany(h => h.Addresses).ToHashSet(StringComparer.Ordinal);
		}

		public void Write(string snapshotDirectory)
		{
			var root = new JsonObject
			{
				["ThroughProxy"] = ThroughProxy,
				["Resolved"] = Array(Resolved.Select(r => new HostAddresses(r.Key, r.Value.Addresses, r.Value.Error))),
				["Connected"] = Array(ConnectedAddresses.Select(c => new HostAddresses(c.Key, c.Value, null))),
			};
			File.WriteAllText(Path.Combine(snapshotDirectory, FileName),
				root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

			static JsonArray Array(IEnumerable<HostAddresses> hosts) => new(hosts.OrderBy(h => h.Host, StringComparer.Ordinal).Select(h =>
			{
				var o = new JsonObject
				{
					["Host"] = h.Host,
					["Addresses"] = new JsonArray(h.Addresses.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
				};
				if (h.Error is not null)
				{
					o["Error"] = h.Error;
				}

				return (JsonNode?)o;
			}).ToArray());
		}

		/// <summary>A snapshot's record, or null when it has none (older, a replay of one without, unreadable).</summary>
		public static Snapshot? Read(string snapshotDirectory)
		{
			var path = Path.Combine(snapshotDirectory, FileName);
			if (!File.Exists(path))
			{
				return null;
			}

			try
			{
				if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
				{
					return null;
				}

				static List<HostAddresses> Hosts(JsonNode? node) => (node as JsonArray ?? []).OfType<JsonObject>().Select(o => new HostAddresses(
					o["Host"]?.GetValue<string>() ?? string.Empty,
					(o["Addresses"] as JsonArray ?? []).Select(a => a?.GetValue<string>() ?? string.Empty).Where(a => a.Length > 0).ToList(),
					o["Error"]?.GetValue<string>())).Where(h => h.Host.Length > 0).ToList();

				return new Snapshot(Path.GetFileName(snapshotDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
					root["ThroughProxy"]?.GetValue<bool>() ?? false, Hosts(root["Resolved"]), Hosts(root["Connected"]));
			}
			catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
			{
				return null;
			}
		}
	}
}
