using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>The split-word excerpt stands at the split, however many entities come before it.</summary>
	public class WordSplitExcerptTests
	{
		private static readonly ContentQualityConfig Config = new() { ContentQualityExcerptRadius = 120 };

		[Fact]
		public void ManyEntitiesBefore_TheExcerptStillShowsTheSplit()
		{
			var html = "<p>" + string.Concat(Enumerable.Repeat("Haus &amp; Garten&nbsp;", 200)) + "<a href=\"/x\">Garten</a>zaun steht.</p>"
				+ string.Concat(Enumerable.Repeat("<p>Weiter im Text.</p>", 300));
			var issue = Assert.Single(WordSplits.Check("f.html", html, Config));
			Assert.Contains("Garten</a>zaun", issue.Context);
			Assert.Contains("& Garten", issue.Context);   // decoded for the reader
		}

		[Fact]
		public void ASplitNearTheEnd_AfterManyEntities_DoesNotThrow()
		{
			var html = "<p>" + string.Concat(Enumerable.Repeat("&amp;&amp;&amp;&amp;", 500)) + "<a href=\"/x\">Haus</a>tür auf</p>";
			var issues = WordSplits.Check("f.html", html, Config).ToList();
			Assert.Single(issues);
			Assert.Contains("Haus</a>tür", issues[0].Context);
		}

		[Fact]
		public void Around_ClampsAPositionOutsideTheText()
		{
			Assert.Equal("...end...", Excerpt.Around("end", 99, 10));
			Assert.Equal("...st...", Excerpt.Around("start", -5, 4));
		}
	}
}
