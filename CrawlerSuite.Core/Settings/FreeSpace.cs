namespace CrawlerSuite.Core.Settings
{
	using System.Globalization;

	/// <summary>How the free space of the download drive compares with the crawl download limit.</summary>
	public enum SpaceRating
	{
		/// <summary>No download limit is set, so there is nothing to compare with.</summary>
		Unrated,

		/// <summary>Less than five times the limit: a few crawls until the drive is full.</summary>
		Low,

		/// <summary>Five to twenty times the limit.</summary>
		Some,

		/// <summary>Twenty times the limit or more.</summary>
		Plenty,
	}

	/// <summary>
	/// Rates the download drive's free space against the crawl download limit — the most one
	/// crawl can take: red below 5×, yellow below 20×, green from there. The number of sites is
	/// left out on purpose: sites crawled rarely would make it needlessly alarming.
	/// </summary>
	public static class FreeSpace
	{
		public const int LowFactor = 5;

		public const int PlentyFactor = 20;

		private const long Gigabyte = 1024L * 1024L * 1024L;

		public static SpaceRating Rate(long freeBytes, long limitGb)
		{
			if (limitGb <= 0)
			{
				return SpaceRating.Unrated;
			}

			var limit = limitGb * Gigabyte;
			return freeBytes < LowFactor * limit ? SpaceRating.Low
				: freeBytes < PlentyFactor * limit ? SpaceRating.Some
				: SpaceRating.Plenty;
		}

		/// <summary>A size as Windows shows it: "38 GB", "1.2 TB", "640 MB".</summary>
		public static string Size(long bytes)
		{
			string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
			double value = bytes;
			int unit = 0;
			while (value >= 1024 && unit < units.Length - 1)
			{
				value /= 1024;
				unit++;
			}

			return value.ToString(unit == 0 || value >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
		}

		/// <summary>What the rating means, in words.</summary>
		public static string Explain(SpaceRating rating, long limitGb) => rating switch
		{
			SpaceRating.Low => $"Less than {LowFactor}× the download limit of {limitGb} GB — only a few crawls until the drive is full.",
			SpaceRating.Some => $"Between {LowFactor}× and {PlentyFactor}× the download limit of {limitGb} GB — time to clear old snapshots.",
			SpaceRating.Plenty => $"At least {PlentyFactor}× the download limit of {limitGb} GB.",
			_ => "No download limit is set, so the space is not rated.",
		};
	}
}
