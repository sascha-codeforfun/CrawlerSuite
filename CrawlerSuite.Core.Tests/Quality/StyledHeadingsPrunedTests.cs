using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// Styled headings are read from the pruned copy of a page, like the content checks: a heading
	/// in pruned chrome is reported on the group's reference page (which keeps the chrome), not on
	/// every page. A page without a pruned copy is read raw. Invented markup.
	/// </summary>
	[Collection("Logger")]
	public sealed class StyledHeadingsPrunedTests : IDisposable
	{
		private readonly string root = Path.Combine(Path.GetTempPath(), $"sh-{Guid.NewGuid():N}");
		private string Download => Path.Combine(root, "download");
		private string Pruned => Path.Combine(root, "pruned");

		public StyledHeadingsPrunedTests()
		{
			Directory.CreateDirectory(Download);
			Directory.CreateDirectory(Pruned);
		}

		public void Dispose() => Directory.Delete(root, recursive: true);

		private const string Chrome = "<nav><p class=\"h2\">Departments</p><ul><li><a href=\"/a\">A</a></li></ul></nav>";
		private const string Content = "<main><div class=\"h2\">Garden tools</div><p>Rakes and spades.</p></main>";

		private static string Page(string body) => $"<html><head><title>T</title></head><body>{body}</body></html>";

		private List<string> Run()
		{
			var csvBase = Path.Combine(root, "22-template");
			ContentQuality.Analyse(Pruned, Download, Path.Combine(root, "10.log"), new ContentQualityConfig(),
				1, "*.html", architectCsvBasePath: csvBase);
			var csv = csvBase + IssueLogWriter.CsvCommaSuffix;
			return File.Exists(csv) ? File.ReadAllLines(csv).Skip(1).ToList() : [];
		}

		[Fact]
		public void ChromeHeadings_AreReportedOnTheCheckPageOnly()
		{
			// An ordinary page: chrome pruned away. The reference page: chrome kept.
			File.WriteAllText(Path.Combine(Download, "ordinary.html"), Page(Chrome + Content));
			File.WriteAllText(Path.Combine(Pruned, "ordinary.html"), Page(Content));
			File.WriteAllText(Path.Combine(Download, "check.html"), Page(Chrome + Content));
			File.WriteAllText(Path.Combine(Pruned, "check.html"), Page(Chrome + Content));

			var rows = Run();
			var chrome = rows.Where(r => r.Contains("<p class=\"\"h2\"\">", StringComparison.Ordinal)).ToList();
			Assert.Single(chrome);
			Assert.StartsWith("check.html", chrome[0]);
			Assert.Equal(2, rows.Count(r => r.Contains("<div class=\"\"h2\"\">", StringComparison.Ordinal)));
		}

		[Fact]
		public void APageWithoutAPrunedCopy_IsReadRaw()
		{
			File.WriteAllText(Path.Combine(Download, "alone.html"), Page(Chrome + Content));
			var rows = Run();
			Assert.Equal(2, rows.Count(r => r.StartsWith("alone.html", StringComparison.Ordinal) && r.Contains("STYLED_HEADING", StringComparison.Ordinal)));
		}
	}
}
