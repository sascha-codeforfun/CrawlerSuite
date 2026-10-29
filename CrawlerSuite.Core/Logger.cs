namespace CrawlerSuite.Core
{
	using System;
	using System.IO;

	/// <summary>One line written to the application log.</summary>
	public sealed record LogEntry(DateTime TimestampUtc, string Level, string Message);

	/// <summary>
	/// The application log (application.log next to the executable). Every entry is
	/// written to the file and raised as <see cref="Entry"/> so the app can show it.
	/// Never writes to a console.
	/// </summary>
	public static class Logger
	{
		private static string? logFilePath;
		private static readonly object logLock = new();

		/// <summary>Raised for every entry after it has been written. Raised on the writing thread.</summary>
		public static event Action<LogEntry>? Entry;

		/// <summary>
		/// Lets engine code group its log lines. A windowed app has no console echo
		/// to quiet, so the scope does nothing.
		/// </summary>
		public static IDisposable QuietConsole() => NoopScope.Instance;

		/// <summary>
		/// Starts a fresh log in <paramref name="directory"/> (default: the program folder).
		/// An existing log of the same name is replaced.
		/// </summary>
		public static void Initialize(string fileName, string? directory = null)
		{
			if (string.IsNullOrWhiteSpace(fileName))
			{
				throw new ArgumentException("Log file name cannot be null or empty", nameof(fileName));
			}

			lock (logLock)
			{
				logFilePath = Path.Combine(directory ?? AppContext.BaseDirectory, fileName);
				try
				{
					if (File.Exists(logFilePath))
					{
						File.Delete(logFilePath);
					}
				}
				catch (IOException)
				{
					// Another instance in the same folder may hold it open; append instead.
				}
				catch (UnauthorizedAccessException)
				{
				}
			}

			LogInfo("Logger initialized. Logging will be written to: " + logFilePath);
		}

		/// <summary>The resolved application.log path, or null before Initialize.</summary>
		public static string? LogFilePath => logFilePath;

		public static void LogError(string message) => Log("ERROR", message);

		public static void LogWarning(string message) => Log("WARNING", message);

		public static void LogInfo(string message) => Log("INFO", message);

		/// <summary>
		/// Writes a multi-line detail block to the log. Continuation lines are indented so
		/// the block reads as a single entry.
		/// </summary>
		public static void LogDetailToFile(string message)
		{
			var body = (message ?? string.Empty).Replace("\n", "\n    ");
			Log("ERROR", body);
		}

		/// <summary>Logs a per-file spell-check result (files without findings are not logged).</summary>
		public static void LogSpellResult(string filename, int errorCount)
		{
			if (errorCount == 0)
			{
				return;
			}

			Log("INFO", $"{errorCount} spelling error(s) found in: {filename}");
		}

		private static void Log(string logLevel, string message)
		{
			var now = DateTime.UtcNow;
			var logEntry = $"{now:O}: [{logLevel}] {message}";

			lock (logLock)
			{
				if (!string.IsNullOrEmpty(logFilePath))
				{
					try
					{
						var directoryPath = Path.GetDirectoryName(logFilePath);
						if (!string.IsNullOrWhiteSpace(directoryPath))
						{
							Directory.CreateDirectory(directoryPath);
						}

						File.AppendAllText(logFilePath, logEntry + FileFormat.NewLine);
					}
					catch (Exception)
					{
						// A log write must never break the run (read-only folder, disk full).
					}
				}
			}

			try
			{
				Entry?.Invoke(new LogEntry(now, logLevel, message));
			}
			catch (Exception)
			{
				// A failing subscriber must never break the run either.
			}
		}

		private sealed class NoopScope : IDisposable
		{
			public static readonly NoopScope Instance = new();

			public void Dispose()
			{
			}
		}
	}
}
