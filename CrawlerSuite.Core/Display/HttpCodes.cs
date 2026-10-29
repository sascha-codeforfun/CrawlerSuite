using System.Globalization;
using System.Net;

namespace CrawlerSuite.Core.Display
{
	/// <summary>What a status code is, for chips and their tooltips.</summary>
	public enum HttpCodeKind
	{
		Success,
		PermanentRedirect,
		TemporaryRedirect,
		ClientError,
		ServerError,
		Other,
	}

	/// <summary>
	/// HTTP status codes for display: the number (the crawl log writes .NET names such as
	/// "Found"), its official name (RFC 9110), and a short explanation for the codes that are
	/// easy to confuse.
	/// </summary>
	public static class HttpCodes
	{
		private static readonly Dictionary<int, (string Name, string? Explanation)> Known = new()
		{
			[200] = ("OK", null),
			[204] = ("No Content", "The request succeeded, but there is no page body."),
			[301] = ("Moved Permanently", "The page has moved for good. Search engines move its ranking to the new URL. Browsers may turn a form submission (POST) into a GET."),
			[302] = ("Found", "A temporary move: the old URL stays the one to use. Browsers may turn a form submission (POST) into a GET — which is why 307 exists."),
			[303] = ("See Other", "Go to another URL with an ordinary GET, typically after a form was sent. Not a move of the page."),
			[304] = ("Not Modified", "The cached copy is still current; nothing was sent."),
			[307] = ("Temporary Redirect", "Like 302, but the request method is kept: a POST stays a POST."),
			[308] = ("Permanent Redirect", "Like 301, but the request method is kept: a POST stays a POST."),
			[400] = ("Bad Request", "The server could not understand the request."),
			[401] = ("Unauthorized", "Sign-in required."),
			[403] = ("Forbidden", "The server refuses access, signed in or not."),
			[404] = ("Not Found", "There is nothing at this URL."),
			[405] = ("Method Not Allowed", "The URL exists, but not for this kind of request."),
			[407] = ("Proxy Authentication Required", "The proxy needs a sign-in before it passes the request on."),
			[408] = ("Request Timeout", "The server gave up waiting for the request."),
			[410] = ("Gone", "Removed on purpose and not coming back — unlike 404, which says nothing about why."),
			[429] = ("Too Many Requests", "The server is rate-limiting the crawler."),
			[500] = ("Internal Server Error", "The server failed while building the answer."),
			[502] = ("Bad Gateway", "A server in front (proxy, load balancer) got a bad answer from the one behind it."),
			[503] = ("Service Unavailable", "Overloaded or down for maintenance; usually temporary."),
			[504] = ("Gateway Timeout", "A server in front gave up waiting for the one behind it."),
		};

		/// <summary>"302", "Found", "Redirect" or "MovedPermanently" → the number; null when it is not a status.</summary>
		public static int? Parse(string? value)
		{
			var text = value?.Trim();
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}

			if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
			{
				return number is >= 100 and <= 599 ? number : null;
			}

			// Enum names, including the aliases (Moved, Redirect, RedirectMethod, RedirectKeepVerb).
			return Enum.TryParse<HttpStatusCode>(text, ignoreCase: true, out var code) && !int.TryParse(text, out _)
				? (int)code
				: null;
		}

		/// <summary>The official name, e.g. 302 → "Found"; a generic class name for codes not listed.</summary>
		public static string Name(int code) =>
			Known.TryGetValue(code, out var known) ? known.Name
			: Enum.IsDefined(typeof(HttpStatusCode), code) ? Spaced(((HttpStatusCode)code).ToString())
			: $"HTTP {code}";

		/// <summary>"302 Found — A temporary move: …" for the tooltip.</summary>
		public static string Describe(int code)
		{
			var name = $"{code} {Name(code)}";
			return Known.TryGetValue(code, out var known) && known.Explanation is { } explanation ? $"{name} — {explanation}" : name;
		}

		public static HttpCodeKind Kind(int code) => code switch
		{
			301 or 308 => HttpCodeKind.PermanentRedirect,
			302 or 303 or 307 => HttpCodeKind.TemporaryRedirect,
			>= 200 and < 300 => HttpCodeKind.Success,
			>= 400 and < 500 => HttpCodeKind.ClientError,
			>= 500 and < 600 => HttpCodeKind.ServerError,
			_ => HttpCodeKind.Other,
		};

		/// <summary>The word shown next to a redirect code: "permanent" or "temporary"; empty for others.</summary>
		public static string KindWord(int code) => Kind(code) switch
		{
			HttpCodeKind.PermanentRedirect => "permanent",
			HttpCodeKind.TemporaryRedirect => "temporary",
			_ => string.Empty,
		};

		private static string Spaced(string pascal) =>
			string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
	}
}
