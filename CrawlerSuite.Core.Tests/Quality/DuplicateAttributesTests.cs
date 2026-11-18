using HtmlAgilityPack;
using CrawlerSuite.Core.Display;
using CrawlerSuite.Core.Quality;
using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// An attribute written twice in one tag is reported once per page for each tag, attribute and
	/// values, listing the values in the order written (which one counts is not assumed), or that the value
	/// is the same; the start tag is shown with every copy marked. Invented markup only.
	/// </summary>
	public class DuplicateAttributesTests
	{
		private static List<QualityIssue> Check(string body)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html><body>" + body + "</body></html>");
			return DuplicateAttributes.Check("f.html", doc).ToList();
		}

		[Fact]
		public void DifferentValues_AreListed_InTheOrderWritten()
		{
			var issue = Assert.Single(Check("<a href=\"/kettle\" rel=\"noopener noreferrer\" rel=\"noopener\">Kettle</a>"));
			Assert.Equal(DuplicateAttributes.IssueType, issue.IssueType);
			Assert.Equal("Attribute “rel” written 2 times in <a>: first “noopener noreferrer”, then “noopener”", issue.Detail);
			Assert.Contains("rel=\"noopener noreferrer\"", issue.Context);
			Assert.Contains("rel=\"noopener\"", issue.Context);
		}

		[Fact]
		public void SameValue_IsReported_AsWrittenTwice()
		{
			var issue = Assert.Single(Check("<span class=\"tea\" class=\"tea\">Tea</span>"));
			Assert.Equal("Attribute “class” written 2 times in <span>, the same value each time: “tea”", issue.Detail);
		}

		[Fact]
		public void NameCase_DoesNotHideTheDuplicate()
		{
			var issue = Assert.Single(Check("<img src=\"/cup.png\" ALT=\"Cup\" alt=\"Mug\">"));
			Assert.StartsWith("Attribute “alt” written 2 times in <img>: first “Cup”", issue.Detail);
		}

		[Fact]
		public void ThreeCopies_ListEveryValue()
		{
			var issue = Assert.Single(Check("<div id=\"a\" id=\"b\" id=\"c\"></div>"));
			Assert.EndsWith("first “a”, then “b”, “c”", issue.Detail);
		}

		[Fact]
		public void TheSameDoubling_OnSeveralTags_IsOneItem_Counted()
		{
			var issues = Check(string.Concat(Enumerable.Repeat("<a href=\"/x\" rel=\"nofollow\" rel=\"ugc\">x</a>", 3))
				+ "<a href=\"/y\" rel=\"nofollow\" rel=\"sponsored\">y</a>");
			Assert.Equal(2, issues.Count);
			Assert.EndsWith("(×3 on this page)", issues[0].Detail);
			Assert.DoesNotContain("×", issues[1].Detail);
		}

		[Theory]
		[InlineData("<a href=\"/x\" rel=\"nofollow\">x</a>")]
		[InlineData("<input type=\"checkbox\" checked>")]
		[InlineData("<div data-a=\"1\" data-b=\"1\"></div>")]
		public void NoDuplicate_NothingReported(string body) => Assert.Empty(Check(body));

		[Fact]
		public void LongValues_AreShortened_InTheDetail()
		{
			var longValue = new string('w', 200);
			var issue = Assert.Single(Check($"<p title=\"{longValue}\" title=\"short\">p</p>"));
			Assert.Contains("…", issue.Detail);
			Assert.True(issue.Detail.Length < 300);
		}

		[Fact]
		public void EveryCopy_IsMarked_InTheStartTag()
		{
			var issue = Assert.Single(Check("<a href=\"/kettle\" rel=\"noopener noreferrer\" rel=\"noopener\">Kettle</a>"));
			var marked = QualityMarks.For(issue.IssueType, issue.Context, issue.Detail, null);
			var shown = marked.Marks.Where(m => m.Role == MarkRole.Offender).Select(m => marked.Text.Substring(m.Start, m.Length)).ToList();
			Assert.Equal(["rel=\"noopener noreferrer\"", "rel=\"noopener\""], shown);
		}

		[Theory]
		[InlineData("Attribute “rel” written 2 times in <a>, the same value each time: “x”", "rel")]
		[InlineData("Something else", null)]
		[InlineData(null, null)]
		public void NameOf_ReadsTheAttribute_FromTheDetail(string? detail, string? name) =>
			Assert.Equal(name, DuplicateAttributes.NameOf(detail));
	}
}
