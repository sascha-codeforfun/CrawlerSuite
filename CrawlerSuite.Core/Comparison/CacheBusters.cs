namespace CrawlerSuite.Core.Comparison
{
	using System.Text.RegularExpressions;

	/// <summary>
	/// The cache busters the comparison sets aside: parts of an address that a site regenerates
	/// without the content changing. Only these forms, nothing broader:
	///   * a time in milliseconds as a file name — 13 digits after a slash, then an extension
	///     (".../image.img.png/1712345678901.png"): written when a component is saved again;
	///   * 16 or more hex digits standing between dots or slashes ("lib.min.&lt;hash&gt;.js"): a build
	///     fingerprint — the form the triage ledger also sets aside.
	/// For the comparison only: the ledger, the file names shown elsewhere and the analysis keep
	/// their own rules. A file whose cache buster changed is still compared by its bytes.
	/// </summary>
	public static class CacheBusters
	{
		/// <summary>What a set-aside cache buster becomes.</summary>
		public const string Placeholder = "#";

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex Milliseconds = new(
			@"(?<=/)[0-9]{13}(?=\.[A-Za-z][A-Za-z0-9]{0,5}(?![A-Za-z0-9]))",
			RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static readonly Regex Fingerprint = new(
			@"(?<=[./])[0-9A-Fa-f]{16,}(?=[./]|$)",
			RegexOptions.CultureInvariant | RegexOptions.Compiled);

		/// <summary>The text with every cache buster replaced by <see cref="Placeholder"/>.</summary>
		public static string Neutral(string text) =>
			Fingerprint.Replace(Milliseconds.Replace(text, Placeholder), Placeholder);

		/// <summary>True when <paramref name="token"/>, standing between <paramref name="before"/> and <paramref name="after"/>, is a cache buster.</summary>
		internal static bool IsOne(string token, char before, string after)
		{
			if (token.Length == 13 && before == '/' && IsDigits(token) && after.Length >= 2 && after[0] == '.' && char.IsAsciiLetter(after[1]))
			{
				return true;
			}

			return token.Length >= 16 && before is '.' or '/' && (after.Length == 0 || after[0] is '.' or '/') && IsHex(token);
		}

		private static bool IsDigits(string text)
		{
			foreach (var c in text)
			{
				if (c is < '0' or > '9')
				{
					return false;
				}
			}

			return true;
		}

		private static bool IsHex(string text)
		{
			foreach (var c in text)
			{
				if (!char.IsAsciiHexDigit(c))
				{
					return false;
				}
			}

			return true;
		}
	}
}
