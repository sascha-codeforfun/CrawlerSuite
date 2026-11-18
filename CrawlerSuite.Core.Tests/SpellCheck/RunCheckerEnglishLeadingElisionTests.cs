using System;
using System.Collections.Generic;
using System.Linq;
using CrawlerSuite.Core.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// An apostrophe that opens an English word ("’tis", "’twas") stands for its first letters: the
	/// tokenizer drops it, and the bare stem is no dictionary word. On an English page, with the
	/// apostrophe right before the stem, the stem is not a misspelling. Without the apostrophe, or on
	/// a page in another language, it still is. Invented sentences only.
	/// </summary>
	public class RunCheckerEnglishLeadingElisionTests
	{
		private static readonly HashSet<string> EnglishDict = new(StringComparer.Ordinal)
		{
			"the", "season", "for", "a", "new", "start", "late", "when", "we", "left",
		};

		private static RunCheck Checker() => (text, language) =>
			SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text))
				.Select(t => t.Text)
				.Where(w => w.Any(char.IsLetter) && !EnglishDict.Contains(w))
				.Distinct()
				.Select(w => new CheckMiss(w, string.Empty));

		private static List<string> Words(string text, string language = "en") =>
			RunChecker.Check(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text), language, Checker())
				.Select(f => f.Word).ToList();

		[Theory]
		[InlineData("’Tis the season for a new start.")]
		[InlineData("'Tis the season for a new start.")]
		[InlineData("’twas late when we left.")]
		public void StemAfterAnOpeningApostrophe_IsNoMisspelling(string text) =>
			Assert.Empty(Words(text));

		[Fact]
		public void StemWithoutTheApostrophe_StillSurfaces() =>
			Assert.Equal(["tis"], Words("tis the season"));

		[Fact]
		public void OnAPageInAnotherLanguage_StillSurfaces() =>
			Assert.Equal(["Tis"], Words("’Tis the season", "de"));

		[Fact]
		public void AFlavouredEnglishTag_CountsAsEnglish() =>
			Assert.Empty(Words("’Tis the season", "en-gb"));
	}
}
