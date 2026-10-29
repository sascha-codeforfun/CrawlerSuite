using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CrawlerSuite.Core.Display
{
	/// <summary>How much of what the window shows is hidden, for screenshots and shared screens.</summary>
	public enum MaskLevel
	{
		/// <summary>Everything in clear text.</summary>
		Clear,

		/// <summary>URLs keep scheme and path; the host is hidden.</summary>
		Host,

		/// <summary>URLs keep scheme and the last path segment (the file name).</summary>
		HostAndPath,

		/// <summary>URLs keep only their scheme.</summary>
		SchemeOnly,
	}

	/// <summary>
	/// Masks display text: URLs to the chosen level, and — at every level except
	/// <see cref="MaskLevel.Clear"/> — what the configuration names: site names (shown as
	/// "Primary site", "Site 2", …), host names, IP addresses, folder and file paths, and the proxy account.
	/// Only for the screen; files and logs are never masked.
	/// <para>
	/// Learning is additive: names learnt from an earlier configuration stay masked for the
	/// rest of the session, so a reload never lets an old name slip through.
	/// </para>
	/// </summary>
	public sealed partial class ScreenMask
	{
		/// <summary>Stands in for hidden folders, path segments and words.</summary>
		public const string Dots = "•••";

		/// <summary>Stands in for a hidden host.</summary>
		public const string HostDots = "•••••";

		/// <summary>The label of the primary site while masked.</summary>
		public const string PrimaryLabel = "Primary site";

		// No match timeouts: what is shown must not depend on how busy the machine is. Every
		// pattern here runs in linear time — each match can only start at a word or path boundary
		// (lookbehind), the configured names are escaped literals in one alternation, and no
		// pattern has a required part after an open-ended repetition that could force retries.

		private readonly object gate = new();
		private readonly Dictionary<string, string> siteLabels = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> siteFolders = new(StringComparer.OrdinalIgnoreCase);
		private Rules rules = Rules.Empty;

		// scheme://rest — rest stops at whitespace, quotes, brackets and the log separator.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex UrlPatternRegex = new(@"\b([A-Za-z][A-Za-z0-9+.\-]{1,15})://([^\s""'<>|\[\]{}()]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static Regex UrlPattern() => UrlPatternRegex;

		// An absolute Windows path: drive or UNC. Stops at whitespace and characters a path cannot hold.
		// Segments may hold single spaces ("E:\Ice Floes\x.csv"); words after the path then join
		// its last segment, which errs on the side of hiding.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex AbsolutePathPatternRegex = new(@"(?<![\w•])(?:[A-Za-z]:[\\/]|\\\\)[^\s""'<>|*?]+(?: [^\s""'<>|*?\\/]+)*(?:[\\/][^\s""'<>|*?]+(?: [^\s""'<>|*?\\/]+)*)*", RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static Regex AbsolutePathPattern() => AbsolutePathPatternRegex;

		// IP addresses: the site's addresses lead back to it as surely as its host name. A
		// candidate is four dotted numbers, or hex groups with at least two colons; only one that
		// parses as an address is hidden, so a time ("21:38:00") stays. Bounded repetitions: linear.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex AddressPatternRegex = new(
			@"(?<![\w.:%])(?:\d{1,3}(?:\.\d{1,3}){3}(?![\w]|\.\d)|[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?:\.\d{1,3}){0,3}(?![\w:]))",
			RegexOptions.CultureInvariant | RegexOptions.Compiled);

		// name=value inside any text (a detail quoting "<a href=…>", an excerpt cut off before its '>').
		// The value: double- or single-quoted (the closing quote may be cut off), or unquoted.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex AttributePatternRegex = new(@"(?<![\w:\-])([A-Za-z][\w:.\-]*)(\s*=\s*)(""[^""]*""?|'[^']*'?|[^\s""'<>`=]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static Regex AttributePattern() => AttributePatternRegex;

		/// <summary>Learns everything the configuration names. Call again after every reload.</summary>
		public void Learn(Config? config, string? configPath)
		{
			lock (gate)
			{
				if (!string.IsNullOrWhiteSpace(configPath))
				{
					AddFolder(DirectoryOf(configPath));
				}

				if (config is not null)
				{
					LearnSites(config);
					AddHostOf(config.ProxyUrl);
					// The Windows user: the proxy sign-in is filled in with it and the connectivity log names it.
					// A very short name could be an everyday word: masked only from four letters.
					if (Environment.UserName.Trim().Length >= 4)
					{
						AddWord(Environment.UserName);
					}
					AddFolder(Resolve(config.BaseDirectory));
					foreach (var value in StringsIn(config))
					{
						Classify(value);
					}
				}

				Rebuild();
			}
		}

		/// <summary>Learns a folder whose path is hidden entirely (program folder, user profile, temp).</summary>
		public void LearnFolder(string? folder)
		{
			lock (gate)
			{
				AddFolder(folder);
				Rebuild();
			}
		}

		/// <summary>The label a site is shown with while masked.</summary>
		public string SiteLabel(string siteName)
		{
			var current = rules;
			return current.SiteLabels.TryGetValue(siteName.Trim(), out var label) ? label : Dots;
		}

		/// <summary>Masks <paramref name="text"/> for display at <paramref name="level"/>.</summary>
		public string Apply(string? text, MaskLevel level)
		{
			if (string.IsNullOrEmpty(text) || level == MaskLevel.Clear)
			{
				return text ?? string.Empty;
			}

			var current = rules;
			// URL attributes first, wherever they appear: a relative path in href or src gives as
			// much away as a full URL. Other attributes are left to the rules below.
			var result = text.Contains('=')
				? AttributePattern().Replace(text, m => HtmlTokens.IsUrlAttribute(m.Groups[1].Value)
					? m.Groups[1].Value + m.Groups[2].Value + ApplyToAttributeValue(m.Groups[3].Value, level)
					: m.Value)
				: text;
			result = UrlPattern().Replace(result, m => MaskUrl(m, level));
			result = AddressPatternRegex.Replace(result, m => IsAddress(m.Value) ? Dots : m.Value);

			// First, before the path rules: a path keeps its last segment visible, and a site's
			// folder ("https___www_example_com") or its dictionary ("….dic") can be that segment.
			result = current.SiteFolders?.Replace(result, Dots) ?? result;
			result = current.Files?.Replace(result, m => FileOnly(m.Value)) ?? result;
			result = current.Roots?.Replace(result, Dots) ?? result;
			result = AbsolutePathPattern().Replace(result, m => FileOnly(m.Value));
			result = current.Hosts?.Replace(result, HostDots) ?? result;
			result = current.Names?.Replace(result, m => current.SiteLabels.TryGetValue(m.Value, out var label) ? label : Dots) ?? result;
			result = current.Words?.Replace(result, Dots) ?? result;
			return result;
		}

		/// <summary>
		/// Masks a site-relative URL path such as "/en/products/" (no scheme, no host) to
		/// <paramref name="level"/>: unchanged at Host, segments hidden at HostAndPath, all hidden at SchemeOnly.
		/// </summary>
		public string ApplyToUrlPath(string? path, MaskLevel level)
		{
			if (string.IsNullOrEmpty(path) || level is MaskLevel.Clear)
			{
				return path ?? string.Empty;
			}

			return level switch
			{
				MaskLevel.Host => Apply(path, level),
				MaskLevel.HostAndPath => Apply(MaskPathSegments(path), level),
				_ => "/" + Dots,
			};
		}

		/// <summary>
		/// Masks the value of a URL attribute (quotes included, as <see cref="HtmlTokens.UrlValueSpans"/>
		/// finds it) to <paramref name="level"/>. Relative paths are masked like the path of a full
		/// URL; a srcset's URLs one by one, their size descriptors kept.
		/// </summary>
		public string ApplyToAttributeValue(string? value, MaskLevel level)
		{
			if (string.IsNullOrEmpty(value) || level == MaskLevel.Clear)
			{
				return value ?? string.Empty;
			}

			char quote = value[0] is '"' or '\'' ? value[0] : '\0';
			bool closed = quote != '\0' && value.Length > 1 && value[^1] == quote;
			var inner = quote == '\0' ? value : value[1..(closed ? value.Length - 1 : value.Length)];

			// srcset / ping: comma-separated; each item is a URL, optionally followed by a descriptor.
			// A data: URI holds its own comma and is one item.
			var items = inner.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? [inner] : inner.Split(',');
			for (int i = 0; i < items.Length; i++)
			{
				var item = items[i];
				int lead = item.Length - item.TrimStart().Length;
				var rest = item[lead..];
				int space = rest.IndexOfAny([' ', '\t', '\n', '\r']);
				var url = space < 0 ? rest : rest[..space];
				var descriptor = space < 0 ? string.Empty : rest[space..];
				items[i] = item[..lead] + MaskAttributeUrl(url, level) + Apply(descriptor, level);
			}

			var masked = string.Join(',', items);
			return quote == '\0' ? masked : quote + masked + (closed ? quote.ToString() : string.Empty);
		}

		private string MaskAttributeUrl(string url, MaskLevel level)
		{
			if (url.Length == 0)
			{
				return url;
			}

			if (url[0] == '#')
			{
				return level == MaskLevel.Host ? Apply(url, level) : "#" + Dots;
			}

			if (url.Contains("://", StringComparison.Ordinal))
			{
				return Apply(url, level);
			}

			if (url.StartsWith("//", StringComparison.Ordinal))
			{
				// Protocol-relative: masked as a full URL, then the stand-in scheme is dropped.
				return Apply("xx:" + url, level)[3..];
			}

			int colon = url.IndexOf(':');
			if (colon > 0 && url[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '.' or '-') && char.IsAsciiLetter(url[0]))
			{
				// mailto:, tel:, javascript:, data: — the scheme stays, what follows is hidden.
				return level == MaskLevel.Host ? Apply(url, level) : url[..(colon + 1)] + Dots;
			}

			return ApplyToUrlPath(url, level);
		}

		/// <summary>Whether a candidate is an IP address: four parts for IPv4 (not "1.2", which parses too), any valid IPv6.</summary>
		internal static bool IsAddress(string text) =>
			System.Net.IPAddress.TryParse(text, out var ip)
			&& (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? text.Contains(':') : text.Count(c => c == '.') == 3);

		// ── URL masking ──────────────────────────────────────────────────────────

		private static string MaskUrl(Match m, MaskLevel level)
		{
			var scheme = m.Groups[1].Value;
			var rest = m.Groups[2].Value;

			// Sentence punctuation after a URL is not part of it.
			var trail = string.Empty;
			int end = rest.Length;
			while (end > 0 && ".,;:!?".Contains(rest[end - 1]))
			{
				end--;
			}

			if (end < rest.Length)
			{
				trail = rest[end..];
				rest = rest[..end];
			}

			int pathStart = rest.IndexOfAny(['/', '?', '#']);
			var tail = pathStart < 0 ? string.Empty : rest[pathStart..];

			var masked = level switch
			{
				MaskLevel.Host => tail,
				MaskLevel.HostAndPath => MaskPathSegments(tail),
				_ => string.Empty,
			};

			return scheme + "://" + HostDots + masked + trail;
		}

		/// <summary>"/a/b/c.html?x=1#y" → "/•••/•••/c.html?•••#•••".</summary>
		private static string MaskPathSegments(string tail)
		{
			if (tail.Length == 0)
			{
				return tail;
			}

			int q = tail.IndexOfAny(['?', '#']);
			var path = q < 0 ? tail : tail[..q];
			var after = q < 0 ? string.Empty : tail[q..];

			var segments = path.Split('/');
			for (int i = 0; i < segments.Length - 1; i++)
			{
				if (segments[i].Length > 0)
				{
					segments[i] = Dots;
				}
			}

			var sb = new StringBuilder(string.Join('/', segments));
			if (after.Length > 0)
			{
				int hash = after.IndexOf('#');
				if (after[0] == '?')
				{
					sb.Append('?').Append(Dots);
				}

				if (hash >= 0)
				{
					sb.Append('#').Append(Dots);
				}
			}

			return sb.ToString();
		}

		/// <summary>"D:\Crawls\a\file.dic" → "•••\file.dic"; a trailing separator is kept.</summary>
		private static string FileOnly(string path)
		{
			var trimmed = path.TrimEnd('\\', '/');
			int cut = trimmed.LastIndexOfAny(['\\', '/']);
			var last = cut < 0 ? trimmed : trimmed[(cut + 1)..];
			var separator = path.Contains('\\') ? "\\" : "/";
			var suffix = trimmed.Length < path.Length ? separator : string.Empty;
			return last.Length == 0 || last.EndsWith(':') ? Dots + suffix : Dots + separator + last + suffix;
		}

		// ── Learning ─────────────────────────────────────────────────────────────

		private void LearnSites(Config config)
		{
			int next = 2;
			foreach (var site in config.Sites)
			{
				AddHostOf(site.Url);
				AddSiteFolder(site.Url);

				// The site's CMS tenant and production group name the organisation as clearly as its host.
				if (!string.Equals(site.Tenant?.Trim(), Config.DefaultTenant, StringComparison.OrdinalIgnoreCase))
				{
					AddWord(site.Tenant);
				}

				AddWord(site.ProductionGroup);
				var name = site.Name?.Trim() ?? string.Empty;
				if (name.Length == 0)
				{
					continue;
				}

				siteLabels[name] = site.IsPrimary ? PrimaryLabel : $"Site {next++}";
			}
		}

		private void Classify(string value)
		{
			var s = value.Trim();
			if (s.Length < 3)
			{
				return;
			}

			if (s.Contains("://", StringComparison.Ordinal))
			{
				AddHostOf(s);
				return;
			}

			bool rooted = s.Length > 2 && char.IsAsciiLetter(s[0]) && s[1] == ':' && s[2] is '\\' or '/'
				|| s.StartsWith(@"\\", StringComparison.Ordinal);
			bool relativeFile = !rooted && s.IndexOfAny(['\\', '/']) > 0 && s.IndexOfAny(['[', '@', '*', '(', '<', ' ', '|', '{']) < 0
				&& LooksLikeFileName(s);

			if (rooted)
			{
				if (LooksLikeFileName(s))
				{
					AddFile(s);
				}
				else
				{
					AddFolder(s);
				}
			}
			else if (relativeFile)
			{
				AddFile(s);
				AddFile(Resolve(s));
			}
		}

		/// <summary>The folder part of a path, with either separator.</summary>
		private static string DirectoryOf(string path)
		{
			int cut = path.TrimEnd('\\', '/').LastIndexOfAny(['\\', '/']);
			return cut <= 0 ? string.Empty : path[..cut];
		}

		private static bool LooksLikeFileName(string path)
		{
			var ext = Path.GetExtension(path.TrimEnd('\\', '/'));
			return ext.Length is >= 2 and <= 7 && ext.Skip(1).All(char.IsAsciiLetterOrDigit);
		}

		private void AddHostOf(string? url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return;
			}

			var candidate = url.Contains("://", StringComparison.Ordinal) ? url.Trim() : "http://" + url.Trim();
			if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Host.Length < 3)
			{
				return;
			}

			// A host without a dot ("proxy") is an ordinary word too; inside URLs it is masked anyway.
			if (!uri.Host.Contains('.') && uri.HostNameType != UriHostNameType.IPv6)
			{
				return;
			}

			hosts.Add(uri.Host);
			hosts.Add(uri.IdnHost);
			if (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && uri.Host.Length > 8)
			{
				hosts.Add(uri.Host[4..]);
			}
		}

		/// <summary>
		/// The folder a crawl keeps the site's snapshots in, named after its URL
		/// ("https://www.example.com" → "https___www_example_com"), with and without a trailing slash.
		/// </summary>
		private void AddSiteFolder(string? url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return;
			}

			var trimmed = url.Trim();
			foreach (var form in new[] { trimmed, trimmed.TrimEnd('/') })
			{
				var name = Findings.SnapshotLocator.FolderName(form);
				if (name.Length >= 6)
				{
					siteFolders.Add(name);
				}
			}
		}

		private void AddWord(string? word)
		{
			if (!string.IsNullOrWhiteSpace(word) && word.Trim().Length >= 2)
			{
				words.Add(word.Trim());
			}
		}

		private void AddFile(string? path)
		{
			if (!string.IsNullOrWhiteSpace(path))
			{
				files.Add(path.Trim());
			}
		}

		private void AddFolder(string? folder)
		{
			if (string.IsNullOrWhiteSpace(folder))
			{
				return;
			}

			var trimmed = folder.Trim().TrimEnd('\\', '/');
			if (trimmed.Length >= 3)
			{
				roots.Add(trimmed);
			}
		}

		private static string? Resolve(string? path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return null;
			}

			try
			{
				return AppPaths.Resolve(path);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
			{
				return null;
			}
		}

		/// <summary>Every string the configuration holds, however deeply nested.</summary>
		private static IEnumerable<string> StringsIn(object root)
		{
			var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
			var stack = new Stack<(object Value, int Depth)>();
			stack.Push((root, 0));
			while (stack.Count > 0)
			{
				var (value, depth) = stack.Pop();
				if (value is string s)
				{
					yield return s;
					continue;
				}

				if (depth > 8 || !seen.Add(value))
				{
					continue;
				}

				if (value is IDictionary dictionary)
				{
					stack.Push((dictionary.Keys, depth + 1));
					stack.Push((dictionary.Values, depth + 1));
					continue;
				}

				if (value is IEnumerable sequence)
				{
					foreach (var item in sequence)
					{
						if (item is not null)
						{
							stack.Push((item, depth + 1));
						}
					}

					continue;
				}

				var type = value.GetType();
				if (type.Assembly != typeof(Config).Assembly)
				{
					continue;
				}

				foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
				{
					if (property.GetIndexParameters().Length > 0 || !property.CanRead)
					{
						continue;
					}

					object? child;
					try
					{
						child = property.GetValue(value);
					}
					catch (TargetInvocationException)
					{
						continue;
					}

					if (child is not null)
					{
						stack.Push((child, depth + 1));
					}
				}
			}
		}

		// ── Compiled rules ───────────────────────────────────────────────────────

		private void Rebuild()
		{
			rules = new Rules(
				Literal(files, path: true),
				Literal(roots, path: true),
				Bounded(hosts, @"(?<![\w.\-])", @"(?![\w\-]|\.[\w])"),
				Bounded(siteLabels.Keys, @"(?<![\p{L}\p{N}_])", @"(?![\p{L}\p{N}_])"),
				Bounded(words, @"(?<![\p{L}\p{N}_])", @"(?![\p{L}\p{N}_])"),
				// The folder name is whole: not inside a longer name ("…_com" in "…_community").
				Bounded(siteFolders, @"(?<![\w\-])", @"(?![\w\-])"),
				new Dictionary<string, string>(siteLabels, StringComparer.OrdinalIgnoreCase));
		}

		/// <summary>Longest first, and with "\" and "/" interchangeable for paths.</summary>
		private static Regex? Literal(IEnumerable<string> values, bool path)
		{
			var parts = values.OrderByDescending(v => v.Length)
				.Select(v => path ? string.Join(@"[\\/]", v.Split('\\', '/').Select(Regex.Escape)) : Regex.Escape(v))
				.ToList();
			return parts.Count == 0 ? null
				// Not when the match is only the start of a longer name ("D:\Crawls" in "D:\CrawlsOld").
				: new Regex("(?:" + string.Join('|', parts) + @")(?![\w\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}

		private static Regex? Bounded(IEnumerable<string> values, string before, string after)
		{
			var parts = values.OrderByDescending(v => v.Length).Select(Regex.Escape).ToList();
			return parts.Count == 0 ? null
				: new Regex(before + "(?:" + string.Join('|', parts) + ")" + after, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}

		private sealed record Rules(Regex? Files, Regex? Roots, Regex? Hosts, Regex? Names, Regex? Words, Regex? SiteFolders, IReadOnlyDictionary<string, string> SiteLabels)
		{
			public static readonly Rules Empty = new(null, null, null, null, null, null, new Dictionary<string, string>());
		}
	}
}
