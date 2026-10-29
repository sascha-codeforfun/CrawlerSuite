using System.Text;
using CrawlerSuite.Core.Links;
using Xunit;

namespace CrawlerSuite.Core.Tests.Links
{
	/// <summary>
	/// Who uses an image (log 38), found in the broken-links pass: shown as an image, a srcset
	/// candidate or a stylesheet url(); offered as a link; or not referenced at all. The 404 rows
	/// stay as they are without the images.
	/// </summary>
	public class ImageReferencesTests : IDisposable
	{
		private const string Site = "https://site.test/";
		private readonly string snapshot = Path.Combine(Path.GetTempPath(), "cs-img-" + Guid.NewGuid().ToString("N"));

		private const string Wave = "https://site.test/img/wave.jpg";
		private const string Tide = "https://site.test/img/tide.png";
		private const string Drop = "https://site.test/img/drop.jpg";
		private const string Ice = "https://site.test/img/ice.png";
		private const string Mist = "https://site.test/img/mist.gif";

		public ImageReferencesTests()
		{
			Directory.CreateDirectory(Path.Combine(snapshot, "download"));
			Write("02-crawler-index.log",
				"water.html|https://site.test/water.html|discovery",
				"press.html|https://site.test/press.html|discovery",
				"sea.css|https://site.test/css/sea.css|discovery",
				"wave.jpg|" + Wave + "|discovery");
			Download("water.html",
				"<html><head><link rel=\"stylesheet\" href=\"/css/sea.css\"></head><body><div class=\"sea\">" +
				"<img src=\"/img/wave.jpg\" alt=\"Wave\">" +
				"<picture><source srcset=\"/img/tide.png 1x, /img/tide@2x.png 2x\"><img src=\"/img/small.png\" alt=\"\"></picture>" +
				"<a href=\"/gone.html\">gone</a></div></body></html>");
			Download("press.html",
				"<html><body><a href=\"/img/drop.jpg\">Download (high resolution)</a><img src=\"/img/wave.jpg?w=300\" alt=\"Wave\"></body></html>");
			Download("sea.css", ".sea{background:url(../img/ice.png)}");
			Download("wave.jpg", "not an image here");
		}

		public void Dispose()
		{
			try { Directory.Delete(snapshot, recursive: true); } catch (IOException) { }
		}

		private void Write(string name, params string[] lines) =>
			File.WriteAllText(Path.Combine(snapshot, name), string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));

		private void Download(string name, string content) =>
			File.WriteAllText(Path.Combine(snapshot, "download", name), content, new UTF8Encoding(false));

		private BrokenLinkOrigins.Result Find(IReadOnlyList<string> broken, IReadOnlyList<string>? images) =>
			BrokenLinkOrigins.Find(snapshot, broken, Site, "*.html", [], null, images);

		[Fact]
		public void EachKindOfUse_IsFound()
		{
			var rows = Find([], [Wave, Tide, Drop, Ice, Mist]).ImageRows;

			Assert.Equal(["https://site.test/press.html", "https://site.test/water.html"],
				rows.Where(r => r.Broken == Wave).Select(r => r.Referrer).Order(StringComparer.Ordinal));
			Assert.All(rows.Where(r => r.Broken == Wave), r => Assert.Equal("image", r.Kind));
			Assert.Contains(rows, r => r.Broken == Wave && r.AsWritten == "/img/wave.jpg?w=300"); // a query added to the saved file still matches

			var tide = Assert.Single(rows, r => r.Broken == Tide);
			Assert.Equal(BrokenLinkOrigins.SrcSet, tide.Kind);
			Assert.Equal("/img/tide.png", tide.AsWritten);

			var drop = Assert.Single(rows, r => r.Broken == Drop);
			Assert.Equal("link", drop.Kind);
			Assert.Equal("https://site.test/press.html", drop.Referrer);

			var ice = Assert.Single(rows, r => r.Broken == Ice);
			Assert.Equal("stylesheet url()", ice.Kind);
			Assert.Equal("https://site.test/css/sea.css", ice.Referrer);
			Assert.Equal(BrokenLinkOrigins.OnLoad, ice.Grade);

			var mist = Assert.Single(rows, r => r.Broken == Mist);
			Assert.Equal(BrokenLinkOrigins.NoReference, mist.Kind);
			Assert.Empty(mist.Referrer);
		}

		[Fact]
		public void TheRowsOfThe404s_AreTheSame_WithOrWithoutImages()
		{
			string[] broken = ["https://site.test/gone.html"];
			var without = Find(broken, null);
			var with = Find(broken, [Wave, Tide, Drop, Ice]);

			Assert.Equal(without.Rows, with.Rows);
			Assert.Equal((without.Addresses, without.Pages, without.Files), (with.Addresses, with.Pages, with.Files));
			Assert.Empty(without.ImageRows);
		}

		[Fact]
		public void SrcSet_CandidatesAreReadWithoutTheirDescriptors()
		{
			Assert.Equal(["a.jpg", "b.jpg", "c.jpg"],
				BrokenLinkOrigins.SrcSetCandidates("a.jpg 1x,  b.jpg 480w , c.jpg"));
			Assert.True(BrokenLinkOrigins.NamesInSrcSet("<img srcset='x/tide.png 2x'>", new Uri(Site), a => a == Site + "x/tide.png"));
			Assert.False(BrokenLinkOrigins.NamesInSrcSet("<img src='x/tide.png'>", new Uri(Site), _ => true));
		}

		[Fact]
		public void PreCheck_TellsABrokenAddressFromAnImage_InOneScan()
		{
			var page = new Uri(Site);
			const string content = "<a href='/gone.html'>x</a><img src='/img/wave.jpg'>";
			bool Gone(string a) => a == Site + "gone.html";
			bool IsWave(string a) => a == Wave;

			Assert.Equal((true, true), BrokenLinkOrigins.Names(content, page, Gone, IsWave));
			Assert.Equal((false, true), BrokenLinkOrigins.Names(content, page, _ => false, IsWave));
			Assert.Equal((true, false), BrokenLinkOrigins.Names(content, page, Gone, null));
			Assert.True(BrokenLinkOrigins.NamesBroken(content, page, Gone));
		}

		[Fact]
		public void ManyPages_TheRowsFollowThePageOrder_WhicheverThreadParsesThem()
		{
			var lines = new List<string>();
			for (int n = 0; n < 40; n++)
			{
				var name = $"drop{n:00}.html";
				lines.Add($"{name}|https://site.test/{name}|discovery");
				// Every second page also names the broken address: parsed in the page loop, the rest in parallel.
				var gone = n % 2 == 0 ? "<a href=\"/gone.html\">gone</a>" : string.Empty;
				Download(name, $"<html><body><div data-src=\"/img/wave.jpg\"></div>{gone}<a href=\"/img/wave.jpg\">big</a><img src=\"/img/wave.jpg\"></body></html>");
			}

			Write("02-crawler-index.log", [.. lines]);
			var rows = Find(["https://site.test/gone.html"], [Wave]).ImageRows;

			var expected = Enumerable.Range(0, 40).SelectMany(n => new[]
			{
				($"https://site.test/drop{n:00}.html", "data attribute"),
				($"https://site.test/drop{n:00}.html", "link"),
				($"https://site.test/drop{n:00}.html", "image"),
			});
			Assert.Equal(expected, rows.Select(r => (r.Referrer, r.Kind)));
		}

		[Theory]
		[InlineData("<a href=\"/img/wave.jpg\">big</a><img src=\"/img/wave.jpg\" alt=\"\"><picture><source srcset=\"/img/wave.jpg 2x\"></picture>", true)]
		[InlineData("<A HREF='/img/wave.jpg'>x</A><IMG SRC=/img/wave.jpg>", true)]
		[InlineData("<!-- <img src=\"/img/wave.jpg\"> --><p>none</p>", false)]
		[InlineData("<script>var t = '<img src=\"/img/wave.jpg\">';</script><style>a{}</style><img src=\"/img/wave.jpg\">", true)]
		[InlineData("<img title=\"x src=/img/wave.jpg\" src=\"/img/other.jpg\">", false)]
		[InlineData("<img alt=\"a > b\" src=\"/img/wave.jpg\">", true)]
		[InlineData("<a href=\"/img/wave.jpg?w=1&amp;h=2\">x</a><img src=\"/img/wave.jpg?w=1&amp;h=2\">", true)]
		[InlineData("<img src=\"/img/wave.jpg\" src=\"/img/other.jpg\"><img src src=\"/img/wave.jpg\">", true)]
		[InlineData("<abbr href=\"/img/wave.jpg\">x</abbr><image src=\"/img/wave.jpg\"><a-b href=\"/img/wave.jpg\">", false)]
		[InlineData("<img srcset=\"/img/wave.jpg 1x, /img/wave.jpg 2x\" src=\"/img/wave.jpg\"><a href=\"/img/wave.jpg\">late link</a>", true)]
		public void ImageUses_ReadFromTheText_AreThoseOfAParse(string body, bool findsAny)
		{
			var (fromText, fromParse) = BrokenLinkOrigins.ImageUsesBothWays(
				"<html><body>" + body + "</body></html>", new Uri("https://site.test/water.html"), a => a.StartsWith(Wave, StringComparison.Ordinal));
			Assert.Equal(fromParse, fromText);
			Assert.Equal(findsAny, fromText.Count > 0);
		}

		[Fact]
		public void NothingAsked_NothingFound()
		{
			var result = Find([], []);
			Assert.Empty(result.Rows);
			Assert.Empty(result.ImageRows);
		}
	}
}
