using System.Text;
using CrawlerSuite.Core.Html;
using CrawlerSuite.Core.SpellCheck;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// A lang on an inline element (span, a, em …) makes its text a run of its own, checked in that
	/// language; text without one still glues to its neighbours. Invented words only.
	/// </summary>
	public class InlineLanguageTests
	{
		private static List<(string Text, string Language)> Runs(string body) =>
			DomTraverser.Traverse(DomTraverser.Parse(Encoding.UTF8.GetBytes("<html lang=\"de\"><body>" + body + "</body></html>")))
				.Where(r => r.Source == RunSource.TextNode)
				.Select(r => (r.RawText.Trim(), Language.NearestElementLanguage(r.LanguageNode ?? r.Node, "de")))
				.Where(r => r.Item1.Length > 0)
				.ToList();

		[Fact]
		public void SpanWithLang_IsItsOwnRun_InItsLanguage()
		{
			Assert.Equal(
				[("Ein Xyzzy", "de"), ("flower power", "en"), ("Haus.", "de")],
				Runs("<p>Ein Xyzzy <span lang=\"en\">flower power</span> Haus.</p>"));
		}

		[Fact]
		public void LinkAndEmphasisWithLang_CountToo()
		{
			Assert.Equal(
				[("Siehe", "de"), ("garden shed", "en"), ("und", "de"), ("bonjour", "fr")],
				Runs("<p>Siehe <a href=\"#\" lang=\"en\">garden shed</a> und <em lang=\"fr\">bonjour</em></p>"));
		}

		[Fact]
		public void WithoutLang_InlineFormattingStillGlues()
		{
			// Without a lang: a word split by formatting reads as one ("Haus" + "garten").
			Assert.Equal([("Ein Hausgarten.", "de")], Runs("<p>Ein Haus<b>garten</b>.</p>"));
		}

		[Fact]
		public void SameLanguageInside_ChangesNothing()
		{
			Assert.Equal([("Ein Hausgarten.", "de")], Runs("<p lang=\"de\">Ein Haus<span lang=\"de-DE\">garten</span>.</p>"));
		}
	}
}
