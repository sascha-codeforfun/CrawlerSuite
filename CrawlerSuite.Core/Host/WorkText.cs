namespace CrawlerSuite.Core.Host
{
	using System.Collections.Generic;
	using System.Globalization;

	/// <summary>How the work figures read on screen and in the copies for AI.</summary>
	public static class WorkText
	{
		/// <summary>
		/// A step's volume, only the figures that are not zero:
		/// "1,234 files · 120 MB · 1,234 parses · 4.2 M words · 4.1 M lookups". Empty when the step counted nothing.
		/// </summary>
		public static string Compact(StepWork? work)
		{
			if (work is null || work.IsEmpty)
			{
				return string.Empty;
			}

			var parts = new List<string>();
			if (work.FilesRead > 0)
			{
				parts.Add(Count(work.FilesRead, "file", "files"));
				parts.Add(Bytes(work.BytesRead));
			}

			if (work.Parses > 0)
			{
				parts.Add(Count(work.Parses, "parse", "parses"));
			}

			if (work.TextRuns > 0)
			{
				parts.Add(Count(work.TextRuns, "text run", "text runs"));
			}

			if (work.Words > 0)
			{
				parts.Add(Count(work.Words, "word", "words"));
			}

			long lookups = work.Lookups + work.Remembered;
			if (lookups > 0)
			{
				parts.Add(Count(lookups, "lookup", "lookups"));
			}

			return string.Join(" · ", parts);
		}

		/// <summary>
		/// The run's totals as one line: "Read 800 MB in 12,000 files · 10,000 page parses · 2.0 M text
		/// runs · 6.0 M words checked · 9.0 M dictionary lookups (1.0 M asked of the dictionaries, 8.0 M
		/// answered from memory)". Empty when nothing was counted.
		/// </summary>
		public static string Summary(StepWork? work)
		{
			if (work is null || work.IsEmpty)
			{
				return string.Empty;
			}

			var parts = new List<string>
			{
				$"Read {Bytes(work.BytesRead)} in {Count(work.FilesRead, "file", "files")}",
				Count(work.Parses, "page parse", "page parses"),
				Count(work.TextRuns, "text run", "text runs"),
				Count(work.Words, "word checked", "words checked"),
			};

			long lookups = work.Lookups + work.Remembered;
			parts.Add(Count(lookups, "dictionary lookup", "dictionary lookups")
				+ (lookups > 0 ? $" ({Number(work.Lookups)} asked of the dictionaries, {Number(work.Remembered)} answered from memory)" : string.Empty));
			return string.Join(" · ", parts);
		}

		/// <summary>1,234 below a million; 4.2 M from a million on.</summary>
		public static string Number(long n) =>
			n >= 1_000_000
				? (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " M"
				: n.ToString("#,0", CultureInfo.InvariantCulture);

		/// <summary>KB below a megabyte, MB below a gigabyte, then GB.</summary>
		public static string Bytes(long bytes) => bytes switch
		{
			>= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
			>= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
			_ => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
		};

		private static string Count(long n, string one, string many) => $"{Number(n)} {(n == 1 ? one : many)}";
	}
}
