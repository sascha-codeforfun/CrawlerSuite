using System.Text;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>
	/// Search appearance (log 08) in triage: pages in search only, page items for what each
	/// page's editor fixes, shared texts named on every page, and the preview on each item.
	/// </summary>
	public class SeoTriageTests : IDisposable
	{
		private readonly string snapshot = Path.Combine(Path.GetTempPath(), "cs-seo-" + Guid.NewGuid().ToString("N"));

		public SeoTriageTests() => Directory.CreateDirectory(snapshot);

		public void Dispose()
		{
			try { Directory.Delete(snapshot, recursive: true); } catch (IOException) { }
		}

		private const string Header = "url;robots;title;title_length;description;description_length;keywords;source;h1_count;search";

		private void Write(params string[] lines) =>
			File.WriteAllText(Path.Combine(snapshot, "08-seo-data.csv"), string.Join("\r\n", [Header, .. lines]) + "\r\n", new UTF8Encoding(true));

		private static string Row(string page, string title, string description, string keywords = "", string search = "in search", string robots = "index,follow") =>
			$"https://site.test/{page};{robots};{title};{title.Length};{description};{description.Length};{keywords};discovery;1;{search}";

		private IReadOnlyList<TriageItem> Read() => SeoSource.Read(snapshot, _ => null);

		private static readonly string LongTitle = string.Join(" ", Enumerable.Repeat("Vapour rising over the cold morning water", 3));

		[Fact]
		public void OnlyPagesInSearch_GetItems()
		{
			Write(
				Row("water.html", "", "", "rain, tide"),
				Row("ice.html", "", "", "rain, tide", "noindex in page", "noindex,follow"),
				Row("mist.html", "", "", "rain", "noindex by header"),
				Row("tide.html", "", "", "rain", "canonical elsewhere"));

			var items = Read();
			Assert.All(items, i => Assert.Equal("https://site.test/water.html", i.Page));
			Assert.Equal(
				[SeoSource.TitleMissing, SeoSource.DescriptionMissing, SeoSource.Keywords],
				items.Select(i => i.Type));
		}

		[Fact]
		public void ALogWithoutTheSearchColumn_FallsBackToTheRobots()
		{
			File.WriteAllText(Path.Combine(snapshot, "08-seo-data.csv"),
				"url;robots;title;title_length;description;description_length;keywords;source;h1_count\r\n" +
				"https://site.test/water.html;index,follow;Water;5;;0;;discovery;1\r\n" +
				"https://site.test/ice.html;noindex;Ice;3;;0;;discovery;1\r\n", new UTF8Encoding(true));

			var items = Read();
			Assert.Single(items);
			Assert.Equal(SeoSource.DescriptionMissing, items[0].Type);
		}

		[Fact]
		public void ASharedTitle_IsAnItemOnEveryPage_NamingTheOthers_CaseAside()
		{
			Write(
				Row("water.html", "\"Water &amp; ice\"", "Rain"), // quoted: the entity holds the delimiter, as the writer quotes it
				Row("ice.html", "WATER & ICE", "Hail"),
				Row("tide.html", "Tide", "Flow"));

			var shared = Read().Where(i => i.Type == SeoSource.TitleShared).ToList();
			Assert.Equal(["https://site.test/water.html", "https://site.test/ice.html"], shared.Select(i => i.Page));
			Assert.Contains("  https://site.test/ice.html", shared[0].Context);
			Assert.DoesNotContain("water.html", shared[0].Context);
			Assert.StartsWith("The same title on 1 other page(s) in search", shared[0].Context);
		}

		[Fact]
		public void CutTexts_RideAlong_WithTheShownText()
		{
			Write(Row("water.html", LongTitle, "Rain"));

			var cut = Assert.Single(Read(), i => i.Type == SeoSource.TitleCut);
			Assert.Equal(LongTitle, cut.Detail);
			Assert.StartsWith("Shown as: ", cut.Context);
			Assert.Equal(TriageMode.RideAlong, TriageSwitches.Defaults()[SeoSource.TitleCut]);
			Assert.Equal(TriageMode.RideAlong, TriageSwitches.Defaults()[SeoSource.DescriptionCut]);
		}

		[Fact]
		public void EveryItem_CarriesThePreview_AndAKeyByPage()
		{
			Write(Row("water.html", "", "Rain", "tide, flow"));

			var items = Read();
			Assert.All(items, i => Assert.NotNull(i.Snippet));
			Assert.All(items, i => Assert.Equal("Rain", i.Snippet!.Description));
			var keywords = Assert.Single(items, i => i.Type == SeoSource.Keywords);
			Assert.Equal("tide, flow", keywords.Detail);
			Assert.Equal(SeoSource.KeyOf(SeoSource.Keywords, "https://site.test/water.html"), Assert.Single(keywords.Keys));
		}

		[Fact]
		public void Switches_Families_AndCards()
		{
			var modes = TriageSwitches.Defaults();
			foreach (var (type, _, mode) in SeoSource.Types)
			{
				Assert.Equal(mode, modes[type]);
				Assert.Equal(SeoSource.Family, TriageSwitches.Describe(type).Family);
				Assert.NotNull(FindingsCatalog.BasisFor(type, string.Empty));
			}

			Assert.All(SeoSource.Types, t => Assert.Equal(TriageMode.RideAlong, modes[t.Type])); // the entry for going deeper, not a list to start on
			Assert.Equal(TriageMode.RideAlong, modes[SeoSource.TitleShared]); // on every page of a set: dealt with when the page is open
			Assert.Equal(TriageMode.RideAlong, modes[SeoSource.DescriptionShared]);
		}

		[Fact]
		public void TitlePatterns_OwnPartMeasured_MissesReported_OnlyTheAddedTextIsMissing()
		{
			string[] patterns = ["{title} | Water Works and the Long Name of the Company Behind All the Rain"];
			Write(
				Row("water.html", "Tide | Water Works and the Long Name of the Company Behind All the Rain", "Rain"),
				Row("ice.html", "Ice-Water Works", "Hail"),
				Row("mist.html", " | Water Works and the Long Name of the Company Behind All the Rain", "Fog"));

			var items = SeoSource.Read(snapshot, _ => null, patterns);

			Assert.DoesNotContain(items, i => i.Page.EndsWith("water.html") && i.Type is SeoSource.TitleCut or SeoSource.TitlePatternMissed);
			Assert.Single(items, i => i.Page.EndsWith("ice.html") && i.Type == SeoSource.TitlePatternMissed);
			Assert.Single(items, i => i.Page.EndsWith("mist.html") && i.Type == SeoSource.TitleMissing);

			// Without the pattern the long added text counts, and no page misses a pattern.
			var plain = SeoSource.Read(snapshot, _ => null);
			Assert.Contains(plain, i => i.Page.EndsWith("water.html") && i.Type == SeoSource.TitleCut);
			Assert.DoesNotContain(plain, i => i.Type == SeoSource.TitlePatternMissed);
		}

		private const string HeaderWithH1 = Header + ";canonical;h1";

		private void WriteWithH1(params string[] lines) =>
			File.WriteAllText(Path.Combine(snapshot, "08-seo-data.csv"), string.Join("\r\n", [HeaderWithH1, .. lines]) + "\r\n", new UTF8Encoding(true));

		private static string H1Row(string page, string search = "in search", params string[] h1s) =>
			$"https://site.test/{page};index,follow;{page};{page.Length};Rain on {page};10;;discovery;{h1s.Length};{search};;{string.Join(SEO.H1Separator, h1s)}";

		[Fact]
		public void ASharedTitle_OnThePageAtAnotherAddress_AsksToChooseOne_TheRestAskForTheirOwn()
		{
			WriteWithH1(
				"https://site.test/water.html;index,follow;Water;5;Rain on water;13;;discovery;1;in search;;Water",
				"https://help.site.test/water.html;index,follow;WATER;5;Rain on water;13;;discovery;1;in search;;water",
				"https://site.test/well.html;index,follow;Water;5;A deep well;11;;discovery;1;in search;;Well");

			var item = Assert.Single(Read(), i => i.Type == SeoSource.TitleShared && i.Page == "https://site.test/water.html");
			Assert.Equal(
				"The same title on 2 other page(s) in search.\n" +
				"Likely the same page at another address — same title, description and main heading: decide which address stays in " +
				"search and give the others a canonical to it or a noindex:\n" +
				"  https://help.site.test/water.html\n" +
				"Each needs its own title:\n" +
				"  https://site.test/well.html",
				item.Context);

			var well = Assert.Single(Read(), i => i.Type == SeoSource.TitleShared && i.Page == "https://site.test/well.html");
			Assert.DoesNotContain("Likely the same page", well.Context);
		}

		[Fact]
		public void MainHeading_None_Several_OrOneWrittenTwice()
		{
			WriteWithH1(
				H1Row("water.html"),
				H1Row("ice.html", "in search", "Ice", "Hail warning"),
				H1Row("tide.html", "in search", "Tide", "tide"),
				H1Row("wave.html", "in search", "Wave"),
				H1Row("mist.html", "in search", ""),
				H1Row("fog.html", "noindex in page"));

			var items = Read().Where(i => i.Family == SeoSource.HeadingFamily).ToList();

			Assert.Equal(
				[("water.html", SeoSource.H1Missing), ("ice.html", SeoSource.H1Several), ("tide.html", SeoSource.H1Repeated), ("mist.html", SeoSource.H1Missing)],
				items.Select(i => (i.Page.Replace("https://site.test/", string.Empty), i.Type)));
			Assert.Equal("(no H1)", items[0].Detail);
			Assert.Equal("Ice · Hail warning", items[1].Detail);
			Assert.Equal("2 H1s, in page order:\n  1. Ice\n  2. Hail warning", items[1].Context);
			Assert.Equal("Tide", items[2].Detail);
			Assert.Equal("(H1 without text)", items[3].Detail);
		}

		[Fact]
		public void MainHeading_ALogWithoutTheTexts_ReportsOnlyAMissingH1()
		{
			Write(
				"https://site.test/water.html;index,follow;Water;5;Rain;4;;discovery;0;in search",
				"https://site.test/ice.html;index,follow;Ice;3;Hail;4;;discovery;2;in search");

			var items = Read().Where(i => i.Family == SeoSource.HeadingFamily).ToList();
			var only = Assert.Single(items);
			Assert.Equal(("https://site.test/water.html", SeoSource.H1Missing), (only.Page, only.Type));
		}

		[Fact]
		public void MainHeading_Switches_Defaults_AndCards()
		{
			var modes = TriageSwitches.Defaults();
			Assert.Equal(TriageMode.RideAlong, modes[SeoSource.H1Missing]);
			Assert.Equal(TriageMode.RideAlong, modes[SeoSource.H1Several]);
			Assert.Equal(TriageMode.Off, modes[SeoSource.H1Repeated]);
			Assert.All(SeoSource.HeadingTypes, t =>
			{
				Assert.Equal(SeoSource.HeadingFamily, TriageSwitches.Describe(t.Type).Family);
				Assert.NotNull(FindingsCatalog.BasisFor(t.Type, string.Empty));
				Assert.True(SeoSource.IsSeo(t.Type));
			});
		}

		[Fact]
		public void WithoutLog08_NothingIsRead()
		{
			Assert.Empty(Read());
			Assert.False(SeoSource.HasLogs(snapshot));
			Write();
			Assert.True(SeoSource.HasLogs(snapshot));
			Assert.Empty(Read());
		}
	}
}
