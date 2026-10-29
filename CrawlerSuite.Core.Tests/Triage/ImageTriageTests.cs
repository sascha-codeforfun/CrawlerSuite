using System.Text;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>
	/// Image findings (log 25) in triage, placed by who uses the image (log 38): which findings are
	/// taken, where an item goes, size only where shown, and keys that hold when the users change.
	/// </summary>
	public class ImageTriageTests : IDisposable
	{
		private const string Wave = "https://site.test/img/wave.jpg";
		private const string Drop = "https://site.test/img/drop.jpg";
		private const string Ice = "https://site.test/img/ice.png";
		private const string Mist = "https://site.test/img/mist.gif";
		private const string Water = "https://site.test/water.html";
		private const string Press = "https://site.test/press.html";
		private const string Sea = "https://site.test/css/sea.css";

		private readonly string snapshot = Path.Combine(Path.GetTempPath(), "cs-imt-" + Guid.NewGuid().ToString("N"));

		public ImageTriageTests() => Directory.CreateDirectory(Path.Combine(snapshot, "download"));

		public void Dispose()
		{
			try { Directory.Delete(snapshot, recursive: true); } catch (IOException) { }
		}

		private void Write(string name, params string[] lines) =>
			File.WriteAllText(Path.Combine(snapshot, name), string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));

		private const string Findings = "Filename,Url,IssueType,Detail,Context,Exif,Iptc,Xmp";
		private const string Uses = "Image,Kind,Referrer,AsWritten,WhenRequested,Evidence";

		private void WriteLogs(bool withUses = true)
		{
			Write("25-asset-quality_comma.csv", Findings,
				$"wave.jpg,{Wave},ASSET_SIZE,OVERSIZE,2000000 bytes exceeds threshold 1000000,,,",
				$"drop.jpg,{Drop},ASSET_SIZE,OVERSIZE,3000000 bytes exceeds threshold 1000000,,,",
				$"drop.jpg,{Drop},ASSET_METADATA,GPS+Make+Artist,\"GPS, camera, author present (3 fields)\",GPSLatitude=1; Make=Tide; Artist=Rain Maker,,",
				$"ice.png,{Ice},ASSET_DIMENSIONS,DEGENERATE,1x1 px (tracking-pixel or degenerate image),,,",
				$"mist.gif,{Mist},ASSET_SIZE,LENGTH_MISMATCH,declared Content-Length 10 != actual 5 bytes (truncated or corrupt download),,,",
				$"mist.gif,{Mist},ASSET_METADATA,Software+Copyright,\"copyright, software present (2 fields)\",Software=Ripple,,");
			if (withUses)
			{
				Write("38-image-references_comma.csv", Uses,
					$"{Wave},image,{Water},/img/wave.jpg,,",
					$"{Wave},image,{Press},/img/wave.jpg,,",
					$"{Drop},link,{Press},/img/drop.jpg,,",
					$"{Ice},stylesheet url(),{Sea},../img/ice.png,fires on load,.sea applies on {Water}.",
					$"{Mist},no reference found,,,,Not referenced");
			}
		}

		private IReadOnlyList<TriageItem> Read() => ImageSource.Read(snapshot, url => url == Sea ? "sea.css" : null);

		[Fact]
		public void TheFindingsTaken_AndTheTypesTheyFallUnder()
		{
			Assert.Equal([ImageSource.Truncated], ImageSource.TypesOf("ASSET_SIZE", "LENGTH_MISMATCH"));
			Assert.Equal([ImageSource.FileOversize], ImageSource.TypesOf("ASSET_SIZE", "OVERSIZE"));
			Assert.Equal([ImageSource.DimensionsOversize], ImageSource.TypesOf("ASSET_DIMENSIONS", "OVERSIZE"));
			Assert.Equal([ImageSource.Tiny], ImageSource.TypesOf("ASSET_DIMENSIONS", "DEGENERATE"));
			Assert.Equal([ImageSource.LocationOrDevice, ImageSource.People], ImageSource.TypesOf("ASSET_METADATA", "GPS+Make+Artist"));
			Assert.Equal([ImageSource.LocationOrDevice], ImageSource.TypesOf("ASSET_METADATA", "BodySerialNumber"));
			Assert.Equal([ImageSource.People], ImageSource.TypesOf("ASSET_METADATA", "Copyright+XmpCreator"));
			Assert.Empty(ImageSource.TypesOf("ASSET_METADATA", "Make+Model+Software+Copyright+DateTimeOriginal+IptcCredit+XmpRights"));
		}

		[Fact]
		public void OneUser_TheItemIsOnThatPageOrStylesheet_SeveralUsers_OnTheImage()
		{
			WriteLogs();
			var items = Read();

			var wave = Assert.Single(items, i => i.Detail == Wave);
			Assert.Equal(Wave, wave.Page); // two pages show it
			Assert.Equal([Press, Water], wave.Pages);
			Assert.Equal("shown", wave.PageNotes![Water]);
			Assert.Contains("Used by 2 pages or stylesheets", wave.Context);
			Assert.Null(wave.LocalFile);

			var ice = Assert.Single(items, i => i.Detail == Ice);
			Assert.Equal(Sea, ice.Page);
			Assert.Equal("sea.css", ice.LocalFile);
			Assert.Contains("When requested: fires on load", ice.Context);
			Assert.Equal(ImageSource.Family, ice.Family);

			var mist = Assert.Single(items, i => i.Detail == Mist); // the copyright and software row is not taken
			Assert.Equal(ImageSource.Truncated, mist.Type);
			Assert.Equal(Mist, mist.Page);
			Assert.Contains("No saved page or stylesheet references it", mist.Context);
		}

		[Fact]
		public void AnImageOnlyOfferedAsALink_IsNotTriagedForItsSize_ButForItsMetadata()
		{
			WriteLogs();
			var drop = Read().Where(i => i.Detail == Drop).ToList();

			Assert.Equal([ImageSource.LocationOrDevice, ImageSource.People], drop.Select(i => i.Type));
			Assert.All(drop, i => Assert.Equal(Press, i.Page));
			var location = drop[0];
			Assert.StartsWith("Fields: GPS\n", location.Context);
			Assert.Contains("EXIF: GPSLatitude=1", location.Context);
			Assert.Contains("Used only here (linked)", location.Context);
			Assert.Equal("Fields: Artist", drop[1].Context.Split('\n')[0]);
		}

		[Fact]
		public void WithoutLog38_ItemsStayOnTheImage_KeysUnchanged()
		{
			WriteLogs();
			var placed = Read();
			File.Delete(Path.Combine(snapshot, "38-image-references_comma.csv"));
			var unplaced = Read();

			Assert.Equal(placed.Count + 1, unplaced.Count); // the linked-only oversize is kept when nothing is known
			Assert.All(unplaced, i => Assert.Equal(i.Detail, i.Page));
			Assert.All(unplaced, i => Assert.Contains("this snapshot has no log 38", i.Context));
			Assert.Subset(unplaced.SelectMany(i => i.Keys).ToHashSet(), placed.SelectMany(i => i.Keys).ToHashSet());
		}

		[Fact]
		public void Key_IsTheFindingAndTheImage_CacheBusterAside()
		{
			Assert.Equal(
				ImageSource.KeyOf(ImageSource.FileOversize, "https://site.test/img/wave.0123456789abcdef0123.jpg"),
				ImageSource.KeyOf(ImageSource.FileOversize, "https://site.test/img/wave.fedcba9876543210fedc.jpg"));
			Assert.NotEqual(ImageSource.KeyOf(ImageSource.FileOversize, Wave), ImageSource.KeyOf(ImageSource.Truncated, Wave));
		}

		[Fact]
		public void WithoutLog25_NothingIsRead_AndVerdictsAreKept()
		{
			Assert.Empty(ImageSource.Read(snapshot, _ => null));
			Assert.False(ImageSource.HasLogs(snapshot));
			Write("25-asset-quality_comma.csv", Findings);
			Assert.True(ImageSource.HasLogs(snapshot));
			Assert.Empty(ImageSource.Read(snapshot, _ => null));
		}

		[Fact]
		public void Switches_Defaults_FamilyAndCards()
		{
			var modes = TriageSwitches.Defaults();
			Assert.Equal(TriageMode.On, modes[ImageSource.Truncated]);
			Assert.Equal(TriageMode.On, modes[ImageSource.FileOversize]);
			Assert.Equal(TriageMode.On, modes[ImageSource.DimensionsOversize]);
			Assert.Equal(TriageMode.On, modes[ImageSource.LocationOrDevice]);
			Assert.Equal(TriageMode.Off, modes[ImageSource.Tiny]);
			Assert.Equal(TriageMode.Off, modes[ImageSource.People]);
			Assert.All(ImageSource.Types, t => Assert.Equal(ImageSource.Family, TriageSwitches.Describe(t.Type).Family));
			Assert.All(ImageSource.Types, t => Assert.NotNull(FindingsCatalog.BasisFor(t.Type, string.Empty)));
		}

		[Fact]
		public void TriageSource_ReadsThem_AndTheBoardListsOnlySwitchedOn()
		{
			Write("02-crawler-index.log", "water.html|https://site.test/water.html|discovery");
			File.WriteAllText(Path.Combine(snapshot, "download", "water.html"), "<p>water</p>");
			WriteLogs();

			var items = TriageSource.Read(snapshot).Where(i => ImageSource.IsImage(i.Type)).ToList();
			Assert.Equal(5, items.Count);

			var pages = TriageBoard.Pages(items, TriageSwitches.Defaults(), new TriageLedger());
			Assert.DoesNotContain(pages, p => p.Url == Sea); // tiny is off
			Assert.Contains(pages, p => p.Url == Press); // location data, on
			Assert.DoesNotContain(pages.Single(p => p.Url == Press).Triaged, i => i.Type == ImageSource.People);
		}
	}
}
