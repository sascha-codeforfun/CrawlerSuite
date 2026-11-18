using System.Text;
using CrawlerSuite.Core.SpellCheck;
using CrawlerSuite.Core.Text;
using CrawlerSuite.Core.Triage;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// HTML comments are spell-checked only when switched on: each comment its own run, never glued
	/// to the text around it; conditional comments and code-shaped ones left out; a finding says it
	/// stands in an HTML comment. Invented comments only.
	/// </summary>
	public class HtmlCommentSpellCheckTests
	{
		private static List<TextRun> Runs(string body, bool checkComments) =>
			DomTraverser.Traverse(DomTraverser.Parse(Encoding.UTF8.GetBytes("<html lang=\"en\"><body>" + body + "</body></html>")), checkComments: checkComments).ToList();

		[Fact]
		public void Off_ByDefault_NoCommentIsChecked()
		{
			Assert.Equal(false, new SpellCheckEngineConfig().CheckHtmlComments);
			Assert.DoesNotContain(Runs("<p>Ice<!-- vapour notte -->berg</p>", checkComments: false), r => r.Source == RunSource.Comment);
		}

		[Fact]
		public void On_EachCommentIsItsOwnRun_AndTheTextAroundStillGlues()
		{
			var runs = Runs("<p>Ice<!-- vapour notte -->berg</p>", checkComments: true);

			var comment = Assert.Single(runs, r => r.Source == RunSource.Comment);
			Assert.Equal("vapour notte", comment.RawText);
			Assert.Equal(DomTraverser.CommentPath, comment.SourcePath);
			Assert.Contains(runs, r => r.Source == RunSource.TextNode && r.RawText.Trim() == "Iceberg");
		}

		[Theory]
		[InlineData("<!--[if lt IE 9]><p>old</p><![endif]-->")]  // conditional
		[InlineData("<!-- <div class=\"x\">old</div> -->")]      // commented-out markup
		[InlineData("<!-- .tile { color: red; } -->")]           // commented-out CSS
		[InlineData("<!---->")]                                  // empty
		public void ConditionalAndCodeShaped_AreLeftOut(string comment) =>
			Assert.DoesNotContain(Runs("<p>x</p>" + comment, checkComments: true), r => r.Source == RunSource.Comment);

		[Fact]
		public void AFinding_CarriesTheCommentAsItsExcerpt_AndSaysWhereItStands()
		{
			var run = Assert.Single(Runs("<p>x</p><!-- the vapour notte for editors -->", checkComments: true), r => r.Source == RunSource.Comment);
			RunCheck check = (text, _) => SpellTokenizer.Tokenize(run with { RawText = text })
				.Select(t => t.Text).Where(w => w == "notte").Distinct().Select(w => new CheckMiss(w, string.Empty));

			var finding = Assert.Single(RunChecker.Check(run, "en", check));

			Assert.Equal("notte", finding.Word);
			Assert.Equal("the vapour notte for editors", ExcerptBuilder.Build(finding));
			Assert.Equal("In an HTML comment: the vapour notte for editors", SpellingSource.Shown(DomTraverser.CommentPath, "the vapour notte for editors", DocumentLanguage.English));
			Assert.StartsWith("In einem HTML-Kommentar: ", SpellingSource.Shown(DomTraverser.CommentPath, "x", DocumentLanguage.German));
		}
	}
}
