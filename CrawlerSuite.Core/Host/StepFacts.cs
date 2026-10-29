using System.Globalization;

namespace CrawlerSuite.Core.Host
{
	/// <summary>
	/// A step's facts for the run overview: a neutral chip (information, not findings — "512",
	/// "−40%", "640 MB") and the small overlay shown on hover or keyboard focus: a heading, a few
	/// lines and an optional sentence. Lines whose count is 0 are left out by the steps.
	/// </summary>
	public sealed record StepFacts(string Chip, string Heading, IReadOnlyList<string> Lines, string Note = "", bool IsFinding = false)
	{
		/// <summary>A count, grouped the same way everywhere ("1,234").</summary>
		public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

		/// <summary>A size in B, KB, MB, GB ("640.5 MB").</summary>
		public static string Bytes(long bytes)
		{
			string[] units = ["B", "KB", "MB", "GB", "TB"];
			double size = bytes;
			int u = 0;
			while (size >= 1024 && u < units.Length - 1)
			{
				size /= 1024;
				u++;
			}

			return size.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[u];
		}

		/// <summary>"label · count" for each count above 0, in order.</summary>
		public static IEnumerable<string> NonZero(params (string Label, long Count)[] lines) =>
			lines.Where(l => l.Count > 0).Select(l => $"{l.Label} · {Count(l.Count)}");
	}
}
