using System.Text.RegularExpressions;
using CrawlerSuite.Core.Formats;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>
	/// The Output files page says who reads each log (ReadBy). This keeps it honest: every log
	/// the Results tabs read names the findings catalogue, and every class that looks a log up by
	/// its number names itself. Reads the Core sources; skipped where they are not next to the tests.
	/// </summary>
	public class OutputReadersTests
	{
		private static string? CoreSources()
		{
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			{
				foreach (var candidate in new[] { Path.Combine(dir.FullName, "CrawlerSuite.Core"), Path.Combine(dir.FullName, "CrawlerSuite", "CrawlerSuite.Core") })
				{
					if (File.Exists(Path.Combine(candidate, "Formats", "OutputFormats.cs")))
					{
						return candidate;
					}
				}
			}

			return null;
		}

		private static IReadOnlyList<string> ReadBy(string number) =>
			OutputFormats.ByNumber(number)?.ReadBy ?? throw new InvalidOperationException($"No entry for {number}");

		[Fact]
		public void EveryLogTheResultsTabsRead_NamesTheCatalogue()
		{
			if (CoreSources() is not { } core)
			{
				return;
			}

			var catalogue = File.ReadAllText(Path.Combine(core, "Findings", "FindingsCatalog.cs"));
			var numbers = Regex.Matches(catalogue, @"s\.(?:Table|Csv|CsvFile|File)\(""(\d\d)""").Select(m => m.Groups[1].Value).Distinct().ToList();
			Assert.NotEmpty(numbers);
			Assert.All(numbers, n => Assert.Contains(ReadBy(n), r => r.Contains("FindingsCatalog", StringComparison.Ordinal)));
		}

		[Fact]
		public void EveryClassThatLooksALogUpByNumber_IsNamedAsItsReader()
		{
			if (CoreSources() is not { } core)
			{
				return;
			}

			var missing = new List<string>();
			foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
				.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
					&& !f.EndsWith("OutputFormats.cs", StringComparison.Ordinal)))
			{
				var text = File.ReadAllText(file);
				foreach (Match m in Regex.Matches(text, @"OutputFormats\.ByNumber\(""(\d\d)""\)"))
				{
					// The classes declared in the file: one of them must be named on the entry.
					var classes = Regex.Matches(text, @"\b(?:class|record)\s+(\w+)").Select(c => c.Groups[1].Value).ToList();
					var number = m.Groups[1].Value;
					if (!ReadBy(number).Any(r => classes.Any(c => r.Contains(c, StringComparison.Ordinal))))
					{
						missing.Add($"{number}: {Path.GetFileName(file)}");
					}
				}
			}

			Assert.Empty(missing.Distinct());
		}
	}
}
