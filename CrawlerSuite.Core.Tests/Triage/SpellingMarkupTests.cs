using System.Text;
using CrawlerSuite.Core.Text;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>
	/// A spelling finding shown as the page's own markup: the start tag of an element whose attribute
	/// holds the word, the markup around the word in page text, the plain line kept wherever the saved
	/// page does not show the word. Invented pages and words only.
	/// </summary>
	public class SpellingMarkupTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), "cs-smk-" + Guid.NewGuid().ToString("N"));

		public SpellingMarkupTests() => Directory.CreateDirectory(dir);

		public void Dispose()
		{
			try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
		}

		private SpellingMarkup.Page Page(string html)
		{
			var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".html");
			File.WriteAllText(path, html, new UTF8Encoding(true));
			return SpellingMarkup.Load(path)!;
		}

		private static string? Markup(SpellingMarkup.Page page, string word, params (string, string)[] occurrences) =>
			SpellingMarkup.For(page, word, occurrences, DocumentLanguage.English);

		[Fact]
		public void Attribute_IsShownAsTheElementsStartTag_OneLineForSeveralAttributes()
		{
			var page = Page("<html><body><div class=\"tile wide\" data-pic=\"Blorptank\" title=\"Blorptank\" data-src=\"/media/images/2024/very/long/path/to/a/picture-file-name.jpg\"><img src=\"x.png\"></div></body></html>");

			var markup = Markup(page, "Blorptank", ("div[@data-pic]", "Blorptank"), ("div[@title]", "Blorptank"));

			Assert.Equal("<div class=\"tile wide\" data-pic=\"Blorptank\" title=\"Blorptank\" data-src=\"/media/images/2024/very/long/p…\">", markup);
		}

		[Fact]
		public void MetaField_IsShownAsTheMetaTag()
		{
			var page = Page("<html><head><meta name=\"description\" content=\"All about Blorptank and more.\"></head><body></body></html>");

			Assert.Equal("<meta name=\"description\" content=\"All about Blorptank and more.\">",
				Markup(page, "Blorptank", ("meta[@name=description]", "All about Blorptank and more.")));
		}

		[Fact]
		public void PageText_IsShownAsTheMarkupAroundTheWord_EntitiesDecoded()
		{
			var page = Page("<html><body><p>The <b>Blorptank</b> is open f&uuml;r all.</p><p>Other text.</p></body></html>");

			Assert.Equal("<p>The <b>Blorptank</b> is open für all.</p>",
				Markup(page, "Blorptank", ("p[#text]", "The Blorptank is open für all.")));
		}

		[Fact]
		public void LongText_IsCut_NeverInsideATag()
		{
			var filler = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 20));
			var page = Page($"<html><body><p>{filler} <a href=\"/some/where/far\">Blorptank</a> {filler}</p></body></html>");

			var markup = Markup(page, "Blorptank", ("p[#text]", "dolor Blorptank lorem"))!;

			Assert.StartsWith("…", markup);
			Assert.EndsWith("…", markup);
			Assert.Contains("<a href=\"/some/where/far\">Blorptank</a>", markup);
			var body = markup.Trim('…');
			Assert.Equal(body.Count(c => c == '<'), body.Count(c => c == '>'));
		}

		[Fact]
		public void TheWordInOtherCase_IsFoundToo_WhereNoElementHasItAsFlagged()
		{
			// The spell check lists "blorptank" in a sentence under the finding for "Blorptank".
			var page = Page("<html><head><title>Blorptank notes</title></head><body><p>The blorptank below is new.</p></body></html>");

			var markup = Markup(page, "Blorptank", ("title[#text]", "Blorptank notes"), ("p[#text]", "The blorptank below is new."));

			Assert.Equal("<title>Blorptank notes</title>\n<p>The blorptank below is new.</p>", markup);
		}

		[Fact]
		public void TheWordAsFlagged_IsPreferred_OverOtherCase()
		{
			var page = Page("<html><body><p>Some blorptank here.</p><p>The Blorptank there.</p></body></html>");

			Assert.Equal("<p>The Blorptank there.</p>", Markup(page, "Blorptank", ("p[#text]", "The Blorptank there.")));
		}

		[Fact]
		public void WordSplitByMarkup_KeepsThePlainLine()
		{
			var page = Page("<html><body><p>A Blorp<b>tank</b> here.</p></body></html>");
			Assert.Null(Markup(page, "Blorptank", ("p[#text]", "A Blorptank here.")));
		}

		[Fact]
		public void ScriptString_KeepsItsPlainLine_NextToMarkup()
		{
			var page = Page("<html><body><span title=\"Blorptank\">x</span></body></html>");

			var markup = Markup(page, "Blorptank", ("span[@title]", "Blorptank"), ("script[L3:7]", "say Blorptank"));

			Assert.Equal("<span title=\"Blorptank\">\nIn the page's script: say Blorptank", markup);
		}

		[Fact]
		public void NotInTheSavedPage_OrNoPage_NoMarkup()
		{
			var page = Page("<html><body><p>Nothing here.</p></body></html>");
			Assert.Null(Markup(page, "Blorptank", ("p[#text]", "Blorptank")));
			Assert.Null(SpellingMarkup.For(null, "Blorptank", [("p[#text]", "Blorptank")], DocumentLanguage.English));
			Assert.Null(SpellingMarkup.Load(Path.Combine(dir, "missing.html")));
		}

		[Fact]
		public void ManyElements_AreCapped()
		{
			var page = Page("<html><body>" + string.Concat(Enumerable.Range(1, 5).Select(i => $"<img alt=\"Blorptank {i}\">")) + "</body></html>");

			var lines = Markup(page, "Blorptank", ("img[@alt]", "Blorptank 1"))!.Split('\n');

			Assert.Equal(4, lines.Length);
			Assert.Equal("<img alt=\"Blorptank 1\">", lines[0]);
			Assert.Equal("… +2", lines[3]);
		}

		[Fact]
		public void TriageItems_CarryTheMarkup_AndKeepThePlainContext()
		{
			var snapshot = Path.Combine(dir, "snap");
			Directory.CreateDirectory(Path.Combine(snapshot, "download"));
			File.WriteAllText(Path.Combine(snapshot, "download", "p1.html"), "<html><body><div title=\"Blorptank\">x</div></body></html>", new UTF8Encoding(true));
			File.WriteAllText(Path.Combine(snapshot, LogFileNames.CrawlerIndex), "p1.html|https://site.test/p1.html|discovery\n", new UTF8Encoding(false));
			File.WriteAllText(Path.Combine(snapshot, LogFileNames.SpellErrorsSources), "https://site.test/p1.html|Blorptank (de)\n", new UTF8Encoding(true));
			File.WriteAllText(Path.Combine(snapshot, LogFileNames.SpellErrorsSourceLocation),
				"Blorptank (de) @ https://site.test/p1.html\n  div[@title]  Blorptank\n", new UTF8Encoding(true));

			var item = Assert.Single(TriageSource.Read(snapshot), i => i.Type == SpellingSource.Type);

			Assert.Equal("title of <div>: Blorptank", item.Context);
			Assert.Equal("<div title=\"Blorptank\">", item.Markup);
			Assert.Equal("<div title=\"Blorptank\">", item.ShownIn(DocumentLanguage.German));
		}
	}
}
