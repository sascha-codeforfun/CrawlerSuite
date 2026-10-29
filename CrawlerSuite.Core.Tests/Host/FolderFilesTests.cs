using CrawlerSuite.Core.Host;
using CrawlerSuite.Core.Html;
using Xunit;

namespace CrawlerSuite.Core.Tests.Host
{
	/// <summary>File lists come out in character order, whatever order the disk returns them in.</summary>
	public sealed class FolderFilesTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), $"folderfiles-{Guid.NewGuid():N}");

		public FolderFilesTests()
		{
			Directory.CreateDirectory(dir);
			// Created out of order on purpose.
			foreach (var name in new[] { "b.html", "_x.html", "a_b.html", "A.html", "a-b.html", "9.html" })
				File.WriteAllText(Path.Combine(dir, name), "needle");
		}

		public void Dispose() => Directory.Delete(dir, recursive: true);

		[Fact]
		public void Sorted_IsCharacterOrder()
		{
			var names = FolderFiles.Sorted(dir, "*.html").Select(Path.GetFileName).ToArray();
			Assert.Equal(new[] { "9.html", "A.html", "_x.html", "a-b.html", "a_b.html", "b.html" }, names);
		}

		[Fact]
		public void FirstPageContaining_IsTheFirstInCharacterOrder()
		{
			var found = HtmlSearch.FindFilesContaining(["needle"], dir, "*.html");
			Assert.Equal("9.html", Path.GetFileName(found["needle"]));
		}
	}
}
