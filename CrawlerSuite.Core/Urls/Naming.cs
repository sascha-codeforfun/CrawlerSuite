namespace CrawlerSuite.Core.Urls
{
	using System;
	using System.Collections.Generic;
	using System.Security.Cryptography;
	using System.Text;

	/// <summary>
	/// Derives the on-disk filename for a downloaded asset from its URL.
	/// The result is a load-bearing contract: it determines body/header sidecar
	/// pairing and cross-archive correlation, so the mapping is deliberately
	/// deterministic. <see cref="Hash"/> is the SHA-256 helper backing the
	/// generated name and is internal — exercised directly by the tests.
	/// </summary>
	public static class Naming
	{
		public static string GenerateFileName(string url)
		{
			var uri = new Uri(url);

			// [KEEP] Security boundary — a download name must be a SINGLE path
			// segment that cannot escape the capture root. Hash the full
			// AbsoluteUri (query included, so distinct query variants stay distinct
			// and never collide onto one file) and append the path-derived
			// extension. A name taken from uri.AbsolutePath would be a rooted,
			// separator-bearing path that Path.Combine resolves OUTSIDE the download
			// dir; PathContainmentCheck would then block it and silently drop
			// legitimate query-bearing assets (cache-busted fonts, counter beacons).
			// Hashing unconditionally removes the escape at the source; containment
			// remains the backstop.
			var extension = ".htm";

			// Only a final path segment can carry the file suffix. A dotted folder
			// ("/v1.2/intro", "/a.b/") is a directory name, not a suffix: taking it would
			// make the name end in "/" and the write fail.
			var segments = uri.Segments;
			if (segments.Length > 0)
			{
				var last = segments[^1];
				if (!last.EndsWith('/') && last.Contains('.'))
				{
					extension = Safe(last);
				}
			}

			return Hash(uri.AbsoluteUri) + extension;
		}

		/// <summary>The longest the URL's last segment may make a name (the hash adds 64).</summary>
		internal const int MaxSegment = 120;

		/// <summary>
		/// The last segment made a plain file name: a character Windows does not allow in a name
		/// becomes "_" — a ":" would make NTFS write an alternate stream of a file named only by the hash — and
		/// so does a trailing dot or space, which Windows drops (the log would name a file the disk does
		/// not have). Longer than <see cref="MaxSegment"/>: the front is cut, the ending kept.
		/// </summary>
		internal static string Safe(string segment)
		{
			var chars = segment.ToCharArray();
			for (int i = 0; i < chars.Length; i++)
			{
				if (chars[i] < 32 || chars[i] is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
				{
					chars[i] = '_';
				}
			}

			for (int i = chars.Length - 1; i >= 0 && chars[i] is '.' or ' '; i--)
			{
				chars[i] = '_';
			}

			var safe = new string(chars);
			if (safe.Length <= MaxSegment)
			{
				return safe;
			}

			int dot = safe.LastIndexOf('.');
			var ending = dot >= 0 && safe.Length - dot <= 16 ? safe[dot..] : string.Empty;
			return safe[..(MaxSegment - ending.Length)] + ending;
		}

		/// <summary>
		/// The endings an asset (an image, script, stylesheet, font, document or media file a page
		/// uses) actually has. Any other ending a URL gives an asset is the site's choice of how
		/// Windows treats the file (a ".url" or ".library-ms" Explorer reads when it shows the
		/// folder): such an asset is saved under <see cref="AssetQuarantine"/>, until the settle
		/// step identifies it. Server handler endings Windows gives no meaning to (".axd", ".ashx" …)
		/// are on the list: scripts and styles served through a handler keep their name.
		/// </summary>
		private static readonly HashSet<string> AssetEndings = new(StringComparer.OrdinalIgnoreCase)
		{
			".htm", ".html", ".xhtml", ".php", ".asp", ".aspx", ".jsp",
			".axd", ".ashx", ".cfm", ".do", ".action", ".jspx", ".shtml",
			".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".webp", ".avif", ".bmp", ".dib", ".tif", ".tiff",
			".ico", ".cur", ".svg", ".svgz", ".jxr", ".wdp", ".heic", ".heif",
			".css", ".js", ".mjs", ".cjs", ".map", ".json", ".jsonld", ".xml", ".txt", ".webmanifest", ".wasm",
			".woff", ".woff2", ".ttf", ".otf", ".eot",
			".pdf",
			".mp4", ".webm", ".ogg", ".ogv", ".oga", ".mp3", ".wav", ".m4a", ".m4v", ".mov", ".vtt",
		};

		/// <summary>
		/// The quarantine ending of an asset: a name of its own, so a URL fetched both as a
		/// page (".unverified") and as an asset never shares one file — or one header sidecar, which
		/// is named by everything before the last ending ("x.asset.header", "x.header"). It still
		/// ends in ".unverified": the index and the settle steps treat it as any quarantined file.
		/// </summary>
		public const string AssetQuarantine = ".asset.unverified";

		/// <summary>
		/// The name of an asset (a src, a stylesheet link, a CSS url()): as <see cref="GenerateFileName"/>,
		/// but with an ending assets do not have quarantined as <see cref="AssetQuarantine"/> (see <see cref="AssetEndings"/>).
		/// </summary>
		public static string GenerateAssetFileName(string url)
		{
			var name = GenerateFileName(url);
			return AssetEndings.Contains(System.IO.Path.GetExtension(name))
				? name
				: System.IO.Path.ChangeExtension(name, AssetQuarantine);
		}

		internal static string Hash(string filename)
		{
			if (string.IsNullOrEmpty(filename))
			{
				throw new ArgumentException("Filename cannot be null or empty.", nameof(filename));
			}

			byte[] bytes = Encoding.UTF8.GetBytes(filename);
			byte[] hashBytes = SHA256.HashData(bytes);
			StringBuilder hashString = new();
			foreach (byte b in hashBytes)
			{
				hashString.Append(b.ToString("x2"));
			}
			return hashString.ToString();
		}
	}
}
