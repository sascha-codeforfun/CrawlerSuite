using CrawlerSuite.Core.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// A hashtag's case says nothing: "#auto" passes because the dictionary knows "Auto"; a typo in a
	/// hashtag still surfaces, and only a "#" glued to the word, at the start or after a space or an
	/// opening bracket, makes a hashtag. Invented words only.
	/// </summary>
	public class RunCheckerHashtagTests
	{
		private static readonly HashSet<string> Dict = new(StringComparer.Ordinal) { "Auto", "Wort", "mit", "dem", "C", "Tag" };

		private static RunCheck Checker() => (text, language) =>
			SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text))
				.Select(t => t.Text)
				.Where(w => w.Any(char.IsLetter) && !Dict.Contains(w))
				.Distinct()
				.Select(w => new CheckMiss(w, string.Empty));

		private static List<string> Words(string text) =>
			RunChecker.Check(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text), "de", Checker())
				.Select(f => f.Word).ToList();

		[Theory]
		[InlineData("Tag mit #auto")]
		[InlineData("#auto")]
		[InlineData("Tag (#auto)")]
		[InlineData("Tag „#wort“")]
		[InlineData("Tag #AUTO")]
		public void AHashtag_PassesWhenTheWordIsKnownInAnotherCase(string text) =>
			Assert.Empty(Words(text));

		[Theory]
		[InlineData("Tag mit #autto", "autto")]       // a typo stays
		[InlineData("Tag mit auto", "auto")]          // no hashtag: the case counts
		[InlineData("Tag mit # auto", "auto")]        // "#" not glued to the word
		[InlineData("Tag mit C#auto", "auto")]        // "#" inside a word (C#)
		public void NotAHashtag_OrATypo_StillSurfaces(string text, string word) =>
			Assert.Equal([word], Words(text));
	}
}
