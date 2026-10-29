namespace CrawlerSuite.Core.Security
{
	using System;
	using System.Net;

	/// <summary>
	/// Pure check: refuse hosts that are IP literals. A crawl is scoped to named
	/// hosts in the allowlist, so an IP-literal host is never a declared FQDN — the
	/// exact-host allowlist already denies it. This is the explicit, named,
	/// separately-tested backstop that blocks the localhost / cloud-metadata /
	/// SSRF-by-IP surface regardless of what the allowlist happens to contain.
	///
	/// Note: this does not resolve DNS, and nothing refuses a hostname that
	/// *resolves* to a private address: the site's names are trusted to point where
	/// their owner points them (an intranet site or split DNS resolves privately by
	/// design). Where they pointed is recorded instead — DnsRecord, dns.json per
	/// snapshot, a change reported as information.
	/// </summary>
	public static class IpLiteralCheck
	{
		/// <summary>True if the host is an IP literal and therefore must not be crawled.</summary>
		public static bool IsIpLiteral(Uri uri)
		{
			if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
			{
				return true;
			}

			// Backstop for any encoding that classifies as Dns but still parses as
			// an address. The allowlist denies these anyway (not a declared FQDN);
			// this keeps the refusal explicit.
			return IPAddress.TryParse(uri.Host, out _) || IPAddress.TryParse(uri.IdnHost, out _);
		}
	}
}
