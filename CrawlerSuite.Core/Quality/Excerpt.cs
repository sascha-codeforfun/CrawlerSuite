namespace CrawlerSuite.Core.Quality
{
	internal static class Excerpt
	{
		/// <summary>
		/// Returns up to <paramref name="maxLength"/> characters from
		/// <paramref name="source"/> centred on the first occurrence of
		/// <paramref name="needle"/>. When the needle is not found, returns
		/// the start of source truncated to maxLength.
		/// Whitespace is collapsed to single spaces so the excerpt reads as one line.
		/// </summary>
		internal static string Centred(string source, string needle, int maxLength)
		{
			var half = maxLength / 2;
			var hitIdx = source.IndexOf(needle, StringComparison.Ordinal);
			var centre = hitIdx >= 0 ? hitIdx + needle.Length / 2 : 0;
			var start = Math.Max(0, centre - half);
			var end = Math.Min(source.Length, start + maxLength);
			// Adjust start if end was clamped so we still show maxLength chars where possible.
			start = Math.Max(0, end - maxLength);
			return source[start..end].Replace('\n', ' ').Replace('\r', ' ');
		}

		// Position-centred variant. Windows maxLength chars centred on an
		// explicit character offset rather than on the location of a needle string.
		// Used by ADJACENT_ANCHOR to centre on the </a><a boundary: centring on
		// the first anchor's OuterHtml would start the window before the (often
		// SVG-laden) anchor body, fill it with SVG path data and push the actual
		// split out of it. Centring on the boundary offset keeps the split in
		// frame and clips the SVG to the leading edge instead.
		// Adds conditional horizontal-ellipsis (…) markers: a leading … iff the
		// window was clipped on the left, a trailing … iff clipped on the right, so
		// truncation is honest (mirrors Around's markers, but only where actually
		// clipped rather than unconditionally). The needle-based overload above
		// serves the MISPLACED_ANCHOR_EMPTY caller.
		internal static string Centred(string source, int centrePos, int maxLength)
		{
			if (string.IsNullOrEmpty(source))
			{
				return string.Empty;
			}

			if (centrePos < 0)
			{
				centrePos = 0;
			}

			if (centrePos > source.Length)
			{
				centrePos = source.Length;
			}

			var half = maxLength / 2;
			var start = Math.Max(0, centrePos - half);
			var end = Math.Min(source.Length, start + maxLength);
			// Adjust start if end was clamped so we still show maxLength chars where possible.
			start = Math.Max(0, end - maxLength);

			var body = source[start..end].Replace('\n', ' ').Replace('\r', ' ');
			var lead = start > 0 ? "\u2026" : string.Empty;          // … iff clipped on the left
			var tail = end < source.Length ? "\u2026" : string.Empty; // … iff clipped on the right
			return $"{lead}{body}{tail}";
		}

		internal static string Around(string text, int pos, int radius)
		{
			// A position outside the text is clamped to it, never a reason to throw.
			pos = Math.Clamp(pos, 0, text.Length);
			radius = Math.Max(radius, 0);
			int start = Math.Max(0, pos - radius / 2);
			int end = Math.Min(text.Length, pos + radius / 2);
			var excerpt = text[start..end].Replace('\n', ' ').Replace('\r', ' ');
			return $"...{excerpt}...";
		}
	}
}
