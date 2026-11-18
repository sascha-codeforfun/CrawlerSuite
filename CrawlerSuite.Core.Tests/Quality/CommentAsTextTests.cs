using HtmlAgilityPack;
using CrawlerSuite.Core.Display;
using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// An HTML comment whose opener is broken is text the page shows: reported with what is wrong,
	/// only with an opener and a closer in the same visible text, never in code samples or scripts,
	/// and never for a real comment. Invented markup only.
	/// </summary>
	public class CommentAsTextTests
	{
		private static List<QualityIssue> Check(string body)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html><body>" + body + "</body></html>");
			return CommentAsText.Check("f.html", doc, new ContentQualityConfig()).ToList();
		}

		[Theory]
		[InlineData("<div>text <-- vapour note --> more</div>", "<-- vapour note -->", "“!” is missing")]
		[InlineData("<p>< !-- vapour note --></p>", "< !-- vapour note -->", "a space after")]
		[InlineData("<p>&lt;!-- vapour note --&gt;</p>", "<!-- vapour note -->", "written as text")]
		[InlineData("<p>&lt;!— vapour note —&gt;</p>", "<!— vapour note —>", "written as text")]
		public void BrokenComment_IsReported_WithWhatIsWrong(string body, string shown, string why)
		{
			var issue = Assert.Single(Check(body));
			Assert.Equal(CommentAsText.IssueType, issue.IssueType);
			Assert.Contains("“" + shown + "”", issue.Detail);
			Assert.Contains(why, issue.Detail);
			Assert.Contains("vapour note", issue.Context);
		}

		[Theory]
		[InlineData("<p>Back <-- to the start</p>")]                 // an arrow, no closer
		[InlineData("<p>Next page --></p>")]                         // an arrow, no opener
		[InlineData("<p>ok<!-- a real comment -->ok</p>")]           // a comment: no text at all
		[InlineData("<pre>&lt;-- sample note --&gt;</pre>")]         // a code sample means to show it
		[InlineData("<p><code>&lt;!-- sample --&gt;</code></p>")]
		[InlineData("<script>var a = '<-- not prose -->';</script>")]
		[InlineData("<p>1 <- 2 and 3 -> 4</p>")]                     // arrows of one dash
		[InlineData("<p>a<!- bogus note -->b</p>")]                  // "<!" opens a bogus comment: hidden
		[InlineData("<p>a<!— bogus note —>b</p>")]
		public void ArrowsCodeAndRealComments_AreNotReported(string body) =>
			Assert.Empty(Check(body));

		[Fact]
		public void TheHighlight_MarksTheBrokenCommentWhole()
		{
			const string context = "text <-- vapour note --> more";
			var marked = QualityMarks.For(CommentAsText.IssueType, context, "Comment shown as text", null);

			var mark = Assert.Single(marked.Marks, m => m.Role == MarkRole.Offender);
			Assert.Equal("<-- vapour note -->", context.Substring(mark.Start, mark.Length));
		}
	}
}
