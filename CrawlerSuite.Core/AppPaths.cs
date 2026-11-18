namespace CrawlerSuite.Core
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;

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

		/// <summary>The configuration's file name; also the end of every other configuration's name.</summary>
		public const string ConfigFileName = "crawlersuite.config.json";

		private static string? chosenConfig;

		/// <summary>
		/// The configuration the app reads its settings from and saves them to: crawlersuite.config.json
		/// next to the executable, unless another one there (name.crawlersuite.config.json) was picked
		/// at Reload configuration. It holds what differs from the built-in defaults.
		/// </summary>
		public static string ConfigFile => chosenConfig ?? Path.Combine(BaseDirectory, ConfigFileName);

		/// <summary>Makes <paramref name="path"/> the configuration from now on, until the app closes or another is picked.</summary>
		public static void UseConfig(string path) => chosenConfig = Path.GetFullPath(path);

		/// <summary>
		/// The configurations in <paramref name="folder"/>: crawlersuite.config.json and every
		/// name.crawlersuite.config.json, the plain one first, the others by name. Backups
		/// (….config.json.1) are not configurations.
		/// </summary>
		public static IReadOnlyList<string> ConfigFiles(string folder)
		{
			try
			{
				return Directory.EnumerateFiles(folder, "*" + ConfigFileName, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
					.Where(f => IsConfigName(Path.GetFileName(f)))
					.OrderBy(f => Path.GetFileName(f).Length == ConfigFileName.Length ? 0 : 1)
					.ThenBy(f => ConfigName(f), StringComparer.OrdinalIgnoreCase)
					.ToList();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return [];
			}
		}

		/// <summary>The configurations next to the executable.</summary>
		public static IReadOnlyList<string> ConfigFiles() => ConfigFiles(BaseDirectory);

		/// <summary>
		/// A configuration's short name: "kettles" for kettles.crawlersuite.config.json, "crawlersuite"
		/// for the plain one; any other file keeps its whole name.
		/// </summary>
		public static string ConfigName(string path)
		{
			var name = Path.GetFileName(path);
			return name.Equals(ConfigFileName, StringComparison.OrdinalIgnoreCase) ? "crawlersuite"
				: IsConfigName(name) ? name[..^(ConfigFileName.Length + 1)]
				: name;
		}

		private static bool IsConfigName(string name) =>
			name.Equals(ConfigFileName, StringComparison.OrdinalIgnoreCase)
			|| (name.Length > ConfigFileName.Length + 1
				&& name.EndsWith("." + ConfigFileName, StringComparison.OrdinalIgnoreCase));

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
