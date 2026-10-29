namespace CrawlerSuite.Core
{
	using System;
	using System.IO;

	/// <summary>
	/// Where the app's own files live. Everything sits next to the executable — the
	/// configuration, the dictionaries folder, the licence notices and the application log —
	/// so the app folder can be moved by plain copy and nothing is written anywhere else.
	///
	/// Paths taken from the configuration may use Windows backslashes; they are converted to
	/// the running platform's separator here, so the same configuration works on any OS.
	/// </summary>
	public static class AppPaths
	{
		/// <summary>The folder holding the executable (the bundle directory under single-file publish).</summary>
		public static string BaseDirectory => AppContext.BaseDirectory;

		/// <summary>
		/// The configuration: crawlersuite.config.json next to the executable, the only file the
		/// app reads its settings from. It holds what differs from the built-in defaults.
		/// </summary>
		public static string ConfigFile => Path.Combine(BaseDirectory, "crawlersuite.config.json");

		/// <summary>The dictionaries folder.</summary>
		public static string DictionariesFolder => Path.Combine(BaseDirectory, "dictionaries");

		/// <summary>A file in the dictionaries folder.</summary>
		public static string Dictionary(string fileName) => Path.Combine(DictionariesFolder, Native(fileName));

		/// <summary>
		/// Resolves a path from the configuration: relative paths are taken from the program
		/// folder, whatever the current working directory is.
		/// </summary>
		public static string Resolve(string? configuredPath)
		{
			if (string.IsNullOrWhiteSpace(configuredPath))
			{
				return string.Empty;
			}

			var native = Native(configuredPath.Trim());
			return Path.IsPathRooted(native) ? native : Path.Combine(BaseDirectory, native);
		}

		/// <summary>
		/// A chosen path as the configuration keeps it: relative when it lies inside the program's
		/// folder (so the folder can be moved or copied as a whole), else as chosen.
		/// </summary>
		public static string Relativize(string chosenPath)
		{
			if (string.IsNullOrWhiteSpace(chosenPath))
			{
				return string.Empty;
			}

			var full = Path.GetFullPath(chosenPath);
			var relative = Path.GetRelativePath(BaseDirectory, full);
			return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
				? full
				: relative;
		}

		/// <summary>Converts backslash separators to the running platform's separator.</summary>
		public static string Native(string path) =>
			Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', Path.DirectorySeparatorChar);
	}
}
