namespace CrawlerSuite.Core.Snapshots
{
	using System;
	using System.IO;
	using System.Linq;

	/// <summary>
	/// The snapshot-folder identifier. Each crawl run lives in a folder named
	/// yyyy-MM-dd-HH-mm-ss; <see cref="NewName"/> produces that name for a run,
	/// and <see cref="Matches"/>
	/// recognises such folders on disk.
	/// </summary>
	public static class SnapshotFolder
	{
		/// <summary>The name of a new snapshot: the current time as yyyy-MM-dd-HH-mm-ss.</summary>
		public static string NewName() => $"{DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)}";

		/// <summary>
		/// Returns true when the folder name matches the crawler timestamp format
		/// yyyy-MM-dd-HH-mm-ss (e.g. "2026-04-25-14-30-00").
		/// </summary>
		public static bool Matches(string name) =>
			name.Length == 19
			&& name[4] == '-' && name[7] == '-' && name[10] == '-'
			&& name[13] == '-' && name[16] == '-'
			&& name.Replace("-", "").All(char.IsDigit);
	}
}
