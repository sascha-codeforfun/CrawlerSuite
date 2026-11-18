using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>Next and previous page in the page view: list order, going round at either end.</summary>
	public class TriageNeighbourTests
	{
		private static readonly string[] Open = ["https://site.test/a.html", "https://site.test/b.html", "https://site.test/c.html"];

		[Theory]
		[InlineData("https://site.test/a.html", 1, "https://site.test/b.html")]
		[InlineData("https://site.test/c.html", 1, "https://site.test/a.html")]   // round at the end
		[InlineData("https://site.test/b.html", -1, "https://site.test/a.html")]
		[InlineData("https://site.test/a.html", -1, "https://site.test/c.html")]  // round at the start
		[InlineData("https://site.test/B.HTML", 1, "https://site.test/c.html")]   // address case ignored
		public void StepsThroughTheOpenPages(string from, int step, string expected) =>
			Assert.Equal(expected, TriageBoard.Neighbour(Open, from, step));

		[Fact]
		public void APageNotInTheList_StartsFromTheEdge()
		{
			// The page just filed has left the open list.
			Assert.Equal("https://site.test/a.html", TriageBoard.Neighbour(Open, "https://site.test/filed.html", 1));
			Assert.Equal("https://site.test/c.html", TriageBoard.Neighbour(Open, "https://site.test/filed.html", -1));
		}

		[Fact]
		public void NoOtherPage_IsNull()
		{
			Assert.Null(TriageBoard.Neighbour(["https://site.test/a.html"], "https://site.test/a.html", 1));
			Assert.Null(TriageBoard.Neighbour(["https://site.test/a.html"], "https://site.test/a.html", -1));
			Assert.Null(TriageBoard.Neighbour([], "https://site.test/a.html", 1));
		}

		[Theory]
		[InlineData("/a", "Page 1 of 3")]
		[InlineData("/C", "Page 3 of 3")]
		[InlineData("/elsewhere", "Not in the list (3 pages)")]
		public void Position_CountsFromOne_InTheListGiven(string url, string expected) =>
			Assert.Equal(expected, TriageBoard.Position(["/a", "/b", "/c"], url));

		[Fact]
		public void Position_OfAnEmptyOrSingleList()
		{
			Assert.Equal("Not in the list (0 pages)", TriageBoard.Position([], "/a"));
			Assert.Equal("Not in the list (1 page)", TriageBoard.Position(["/b"], "/a"));
			Assert.Equal("Page 1 of 1", TriageBoard.Position(["/a"], "/a"));
		}
	}
}
