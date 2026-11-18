using CrawlerSuite.Core.Quality;
using CrawlerSuite.Core.SpellCheck;
using CrawlerSuite.Core.Suppressions;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Suppressions
{
	/// <summary>
	/// The words of a comment the page shows as text are reported once, by COMMENT_AS_TEXT: the
	/// spell check leaves them out, file by file. Invented notes only.
	/// </summary>
	public class CommentAsTextSpellSuppressionTests
	{
		private static QualityIssue Issue(string file, string note) =>
			new(file, CommentAsText.IssueType, $"Comment shown as text: “{note}” — the “!” is missing", note);

		[Fact]
		public void TheNotesWords_AreMuted_InItsFileOnly()
		{
			var suppression = new CommentAsTextSpellSuppression([Issue("a.html", "<-- vapourzone mistnote -->"), new QualityIssue("a.html", "LIGATURE", "x", "y")]);

			Assert.Equal(new HashSet<string> { "vapourzone", "mistnote" }, suppression.WordsForFile("a.html").ToHashSet());
			Assert.Empty(suppression.WordsForFile("b.html"));
		}

		[Fact]
		public void TheSpellCheck_LeavesTheNotesWordsOut()
		{
			var suppression = new CommentAsTextSpellSuppression([Issue("a.html", "<-- vapourzone -->")]);
			RunCheck check = (text, _) => SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text))
				.Select(t => t.Text).Where(w => w.Any(char.IsLetter) && w != "text").Distinct().Select(w => new CheckMiss(w, string.Empty));
			var run = new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", "text <-- vapourzone --> qwertzy");

			var words = RunChecker.Check(run, "en", check,
				collisionWords: CommentAsTextSpellSuppression.Union(null, suppression.WordsForFile("a.html"))).Select(f => f.Word).ToList();

			Assert.Equal(["qwertzy"], words);
		}

		[Fact]
		public void Union_KeepsBothSets()
		{
			var a = new HashSet<string> { "one" };
			var b = new HashSet<string> { "two" };
			Assert.Equal(new HashSet<string> { "one", "two" }, CommentAsTextSpellSuppression.Union(a, b)!.ToHashSet());
			Assert.Same(a, CommentAsTextSpellSuppression.Union(a, new HashSet<string>()));
			Assert.Same(b, CommentAsTextSpellSuppression.Union(null, b));
		}
	}
}
