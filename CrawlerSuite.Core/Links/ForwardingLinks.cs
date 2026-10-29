using System.Net;

namespace CrawlerSuite.Core.Links
{
	/// <summary>
	/// Links that hand the site's own server an address to forward to: a link to the site's host
	/// whose query holds a full address as a value (<c>?next=https://other.test/…</c>, URL-encoded or
	/// not, <c>//host/…</c>). Found by the shape of the value only — no parameter names, so the app
	/// knows nothing of a site. The app never requests such a link: whether the server forwards, and
	/// to which addresses it agrees, is for a person to check (an open redirect, CWE-601).
	/// </summary>
	public static class ForwardingLinks
	{
		/// <summary>One address a link hands over: the parameter, the address as decoded, its host, and whether that is another host.</summary>
		public sealed record Target(string Parameter, string Address, string Host, bool External);

		/// <summary>The scope words of log 40.</summary>
		public const string ExternalScope = "external";

		public const string SameHostScope = "same host";

		/// <summary>The addresses a link (already resolved, entities decoded) hands its own server in its query.</summary>
		public static IReadOnlyList<Target> In(Uri link)
		{
			if (!link.IsAbsoluteUri || string.IsNullOrEmpty(link.Query) || link.Query.Length < 2)
			{
				return [];
			}

			var targets = new List<Target>();
			foreach (var pair in link.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
			{
				int equals = pair.IndexOf('=');
				if (equals <= 0)
				{
					continue;
				}

				var name = Decode(pair[..equals]);
				var value = Decode(pair[(equals + 1)..]);
				if (TargetOf(value) is { } target)
				{
					// The address as the link hands it over (decoded), so a ticket writes it out as it is.
					targets.Add(new Target(name, value.Trim(), target.Host, !string.Equals(target.Host, link.Host, StringComparison.OrdinalIgnoreCase)));
				}
			}

			return targets;
		}

		/// <summary>A query value decoded; once more when it was encoded twice (<c>%253A</c>).</summary>
		internal static string Decode(string raw)
		{
			var once = WebUtility.UrlDecode(raw) ?? string.Empty;
			return once.Contains("%3A", StringComparison.OrdinalIgnoreCase) || once.Contains("%2F", StringComparison.OrdinalIgnoreCase)
				? WebUtility.UrlDecode(once) ?? once
				: once;
		}

		/// <summary>The value as an address of a host, or null: http(s)://host… or //host….</summary>
		internal static Uri? TargetOf(string value)
		{
			var v = value.Trim();
			bool schemeless = v.StartsWith("//", StringComparison.Ordinal);
			if (!schemeless && !v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			return Uri.TryCreate(schemeless ? "https:" + v : v, UriKind.Absolute, out var uri) && uri.Host.Contains('.') ? uri : null;
		}
	}
}
