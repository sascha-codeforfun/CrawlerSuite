using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CrawlerSuite.Core.Tests.Text
{
	/// <summary>
	/// One name app-wide for the page a boilerplate group's parts are checked on and kept whole:
	/// "reference page". The names keeper page, check page and entry page must not appear,
	/// in visible text, documents or code. The config key PagesToCheckBoiler stays.
	/// </summary>
	public class ReferencePageNameTests
	{
		private static readonly Regex OldName = new(@"\b(keeper|check|entry)[ -]pages?\b|\b(Is|is)(Check|Entry|Keeper)Page|\bKeeperVm\b", RegexOptions.IgnoreCase);

		[Fact]
		public void No_old_name_is_left()
		{
			var root = DocumentKeys.Root();
			var offenders = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
				.Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".md"))
				.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
					&& !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
					&& !f.EndsWith(nameof(ReferencePageNameTests) + ".cs"))
				.SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
				.Where(l => OldName.IsMatch(l.Text))
				.Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
				.ToList();
			Assert.True(offenders.Count == 0, string.Join("\n", offenders));
		}
	}
}
