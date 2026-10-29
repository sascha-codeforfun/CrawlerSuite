using System.Text;
using CrawlerSuite.Core.Links;
using Xunit;

namespace CrawlerSuite.Core.Tests.Links
{
	/// <summary>
	/// Where each 404 comes from, found in the saved snapshot without a request: links, images,
	/// canonicals, redirects, stylesheet references (graded by when a browser asks for them).
	/// </summary>
	public class BrokenLinkOriginsTests : IDisposable
	{
		private const string Site = "https://site.test/";
		private readonly string snapshot;

		public BrokenLinkOriginsTests()
		{
			snapshot = Path.Combine(Path.GetTempPath(), "cs-404-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path.Combine(snapshot, "download"));
			Write("02-crawler-index.log",
				"a.html|https://site.test/shop/a.html|discovery",
				"water.html|https://site.test/water.html|discovery",
				"calc.css|https://site.test/lib/calc.css|discovery",
				"calc2.css|https://site.test/lib2/calc.css|discovery",
				"calc.js|https://site.test/lib/calc.js|discovery");
			Download("a.html",
				"<html><head><link rel=\"stylesheet\" href=\"/lib/calc.css\"><link rel=\"canonical\" href=\"/shop/a.html.html\"></head>" +
				"<body><div class=\"calc\"><span class=\"item\">H2O</span></div><a href=\"water.html\">Water</a>" +
				"<img src=\"/img/missing.png\"></body></html>");
			Download("water.html", "<html><body><p>H2O</p></body></html>");
			const string css =
				".calc .item{background:url(\"img/onload.png\")}" +
				".calc .row--delete{background:url(img/script.png)}" +
				".calc--old .x{background:url('img/dead.png')}" +
				".lock:checked ~ label::after{content:\"\";background:url(img/state.png)}" +
				".calc:has(.item){background:url(img/unread.png)}";
			Download("calc.css", css);
			Download("calc2.css", css);
			Download("calc.js", "var t='<div class=\"row--delete\"></div><input class=\"lock\" type=\"checkbox\"><label></label>';var m=\"calcmode--older\";");
			Write("01-crawler.log",
				"t|https://site.test/old|Found|https://site.test/gone.html|",
				"t|https://site.test/notfound-page.html|crawled|info|");
		}

		public void Dispose()
		{
			try { Directory.Delete(snapshot, recursive: true); } catch (IOException) { }
		}

		private void Write(string name, params string[] lines) =>
			File.WriteAllText(Path.Combine(snapshot, name), string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));

		private void Download(string name, string content) =>
			File.WriteAllText(Path.Combine(snapshot, "download", name), content, new UTF8Encoding(false));

		private static readonly string[] Broken =
		[
			"https://site.test/shop/water.html",
			"https://site.test/shop/a.html.html",
			"https://site.test/lib/img/onload.png",
			"https://site.test/lib/img/script.png",
			"https://site.test/lib/img/dead.png",
			"https://site.test/lib/img/state.png",
			"https://site.test/lib/img/unread.png",
			"https://site.test/gone.html",
			"https://site.test/img/missing.png",
			"https://site.test/ghost.html",
		];

		private BrokenLinkOrigins.Result Find() => BrokenLinkOrigins.Find(snapshot, Broken, Site, "*.html", [], null);

		private static BrokenReference Row(BrokenLinkOrigins.Result result, string broken) =>
			Assert.Single(result.Rows, r => r.Broken == broken);

		[Fact]
		public void ALinkWithoutLeadingSlash_OnAPageInAFolder_IsExplained()
		{
			var row = Row(Find(), "https://site.test/shop/water.html");
			Assert.Equal("link", row.Kind);
			Assert.Equal("https://site.test/shop/a.html", row.Referrer);
			Assert.Equal("water.html", row.AsWritten);
			Assert.Equal(BrokenLinkOrigins.RelativeLink, row.Grade);
			Assert.Contains("/shop/", row.Evidence);
		}

		[Fact]
		public void AnAddressOnlyACanonicalPointsTo_IsTheCanonicalsFinding()
		{
			var result = Find();
			Assert.Equal(BrokenLinkOrigins.CanonicalOnly, Row(result, "https://site.test/shop/a.html.html").Grade);
			Assert.Equal(1, result.OnlyCanonical);
			Assert.Equal(9, result.Addresses);
			Assert.Equal(3, result.Pages);
			Assert.Equal(6, result.Files);
		}

		[Fact]
		public void AStylesheetReference_IsGradedByWhenABrowserAsksForIt()
		{
			var result = Find();
			var onLoad = Row(result, "https://site.test/lib/img/onload.png");
			Assert.Equal("stylesheet url()", onLoad.Kind);
			Assert.Equal("https://site.test/lib/calc.css", onLoad.Referrer);
			Assert.Equal(BrokenLinkOrigins.OnLoad, onLoad.Grade);
			Assert.Contains("https://site.test/lib2/calc.css", onLoad.Evidence); // the same stylesheet deployed twice

			var script = Row(result, "https://site.test/lib/img/script.png");
			Assert.Equal(BrokenLinkOrigins.WhenScriptRuns, script.Grade);
			Assert.Contains("calc.js", script.Evidence);

			var dead = Row(result, "https://site.test/lib/img/dead.png");
			Assert.Equal(BrokenLinkOrigins.Dead, dead.Grade);
			Assert.Contains(".calc--old", dead.Evidence); // "calcmode--older" in the script is another name

			Assert.Equal(BrokenLinkOrigins.WhenScriptRuns, Row(result, "https://site.test/lib/img/state.png").Grade);

			var unread = Row(result, "https://site.test/lib/img/unread.png");
			Assert.Equal(BrokenLinkOrigins.Undecided, unread.Grade);
			Assert.Contains("cannot read", unread.Evidence);
			Assert.Contains(":has(", unread.Evidence);
		}

		[Fact]
		public void ManyReferencesOfOneKind_AreOneRowInTheTable_TheLogKeepsAll()
		{
			var rows = Enumerable.Range(1, 9).Select(i => new[] { "https://site.test/base", "data attribute", $"https://site.test/p{i}.html", "/base", "", "" })
				.Append(["https://site.test/x.png", "image", "https://site.test/p1.html", "x.png", "", ""]);

			var shown = CrawlerSuite.Core.Findings.FindingsCatalog.CollapsedReferences(rows).ToList();

			Assert.Equal(2, shown.Count);
			Assert.Equal("https://site.test/p1.html", shown[0][2]);
			Assert.Contains("from 9 places", shown[0][5]);
		}

		[Fact]
		public void Redirects_Images_AndUnreferencedAddresses_AreNamed()
		{
			var result = Find();
			var gone = Row(result, "https://site.test/gone.html");
			Assert.Equal("redirect", gone.Kind);
			Assert.Equal("https://site.test/old", gone.Referrer);
			Assert.Equal("image", Row(result, "https://site.test/img/missing.png").Kind);
			Assert.Equal(BrokenLinkOrigins.NoReference, Row(result, "https://site.test/ghost.html").Kind);
		}

		[Theory]
		[InlineData("water.html", "https://site.test/shop/a.html", true)]
		[InlineData("/water.html", "https://site.test/shop/a.html", false)]   // already from the top
		[InlineData("water.html", "https://site.test/a.html", false)]         // a top-level page resolves it right
		[InlineData("https://site.test/water.html", "https://site.test/shop/a.html", false)]
		[InlineData("mailto:x@site.test", "https://site.test/shop/a.html", false)]
		public void ARelativeLink_IsSaidOnlyWhenItsTopLevelReadingExists(string href, string page, bool relative) =>
			Assert.Equal(relative, BrokenLinkOrigins.Relative(href, new Uri(page), url => url == "https://site.test/water.html").Grade.Length > 0);

		[Theory]
		[InlineData("https://site.test/a/b.png", true)]
		[InlineData("https://site.test/a/b.html", false)]
		[InlineData("https://site.test/a/b", false)]
		public void FilesAndPages_AreToldApart(string address, bool file) =>
			Assert.Equal(file, BrokenLinkOrigins.IsFile(address));

		[Theory]
		[InlineData("ice")]
		[InlineData("el.classList.add('ice'); var steam-ice = 1; x.ice2 = ice_cube;")]
		[InlineData("-ice ice- water_ice ice.vapour (ice)\"ice\" Eis-ice iceice ice")]
		[InlineData("vapour-3 vapour vapour-3x _vapour -vapour")]
		[InlineData("Ünice ice\u00e9 iceé éice ice")]
		[InlineData("")]
		public void WholeWords_FindTheSameNamesAsTheWholeWordPattern(string text)
		{
			var names = new HashSet<string>(["ice", "vapour-3", "-vapour", "ice_cube"], StringComparer.Ordinal);
			var pattern = new System.Text.RegularExpressions.Regex(
				@"(?<![\w-])(?:" + string.Join('|', names.OrderByDescending(n => n.Length).Select(System.Text.RegularExpressions.Regex.Escape)) + @")(?![\w-])",
				System.Text.RegularExpressions.RegexOptions.CultureInvariant);
			var expected = pattern.Matches(text).Select(m => m.Value).ToList();
			Assert.Equal(expected, BrokenLinkOrigins.WholeWordsIn(text, names));
		}

		[Fact]
		public void PreCheck_FindsAnEncodedAndAPlainAddress()
		{
			var page = new Uri("https://site.test/shop/a.html");
			bool Broken(string a) => a == "https://site.test/drop.png?a=1&b=2";
			Assert.True(BrokenLinkOrigins.NamesBroken("<img src=\"/drop.png?a=1&amp;b=2\">", page, Broken));
			Assert.True(BrokenLinkOrigins.NamesBroken("<img src='/drop.png?a=1&b=2'>", page, Broken));
			Assert.False(BrokenLinkOrigins.NamesBroken("<img src=/rain.png>", page, Broken));
		}
	}
}
