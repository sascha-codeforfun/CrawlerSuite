using System;
using System.IO;

namespace CrawlerSuite.Core.Host
{
	/// <summary>
	/// File lists in a fixed order. Windows returns names in whatever order the file system keeps
	/// them (NTFS sorts by its own upper-case rules; a network share or exFAT need not sort at all),
	/// so anything that reports "the first page containing …" or writes rows in the order it read
	/// the files would depend on the disk. Sorting character by character makes it the same everywhere.
	/// </summary>
	internal static class FolderFiles
	{
		public static string[] Sorted(string directory, string pattern,
			SearchOption option = SearchOption.TopDirectoryOnly)
		{
			var files = Directory.GetFiles(directory, pattern, option);
			Array.Sort(files, StringComparer.Ordinal);
			return files;
		}
	}
}
