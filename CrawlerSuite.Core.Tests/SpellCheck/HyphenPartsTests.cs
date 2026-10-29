using System.Text;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Log 36: the parts of a hyphenated finding as the check placed them. The explanation must
	/// walk the parts exactly as the check does, and must not change dictionary usage.
	/// </summary>
	public class HyphenPartsTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), "hyphenparts-" + Guid.NewGuid().ToString("N"));

		public HyphenPartsTests() => Directory.CreateDirectory(dir);

		public void Dispose()
		{
			try
			{
				Directory.Delete(dir, recursive: true);
			}
			catch (IOException)
			{
			}
		}

		private static WordList List(params string[] words) =>
			WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes(words.Length + "\n" + string.Join("\n", words) + "\n")),
				new MemoryStream(Encoding.UTF8.GetBytes("SET UTF-8\n")));

		// Invented generic words only.
		private static Bundle German() => new()
		{
			System = List("Haus", "Garten", "Zeitung"),
			Language = "xx",
			SharedUser = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Qwzuserpart" },
			SharedSite = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Qwzsitepart" },
		};

		private static Bundle English() => new() { System = List("shop", "online"), Language = "en" };

		private static readonly string[] Known = ["Brand", "Brand-Line"];
		private static readonly string[] Fugen = ["s"];

		[Fact]
		public void Explain_PlacesEveryPart_AndNamesTheOnesThatKeepTheWordFlagged()
		{
			var parts = HyphenParts.Explain("Brand-Xyzzy-Haus", German(), English(), Known, Fugen)!;
			Assert.Equal(
				[("Brand", HyphenPartStatus.KnownWordPart), ("Xyzzy", HyphenPartStatus.Unknown), ("Haus", HyphenPartStatus.System)],
				parts.Select(p => (p.Text, p.Status)));
		}

		[Fact]
		public void Explain_TellsTheKindsOfKnownApart()
		{
			var parts = HyphenParts.Explain("Brand-Line-U-Qwzuserpart-Qwzsitepart-online-Zeitungs-Garten", German(), English(), Known, Fugen)!;
			Assert.Equal(
				[
					("Brand-Line", HyphenPartStatus.KnownWordPart), // the longest known part, over two parts
					("U", HyphenPartStatus.Capital),
					("Qwzuserpart", HyphenPartStatus.User),
					("Qwzsitepart", HyphenPartStatus.Site),
					("online", HyphenPartStatus.English),
					("Zeitungs", HyphenPartStatus.CompoundHead), // a head with its linking element
					("Garten", HyphenPartStatus.System),
				],
				parts.Select(p => (p.Text, p.Status)));
		}

		[Theory]
		[InlineData("Haus-Garten")]
		[InlineData("Brand-Haus")]
		[InlineData("Brand-Xyzzy-Haus")]
		[InlineData("Xyzzy-Haus")]
		[InlineData("Haus-Xyzzy")]
		[InlineData("U-Haus")]
		[InlineData("online-Garten")]
		[InlineData("Zeitungs-Haus")]
		[InlineData("Haus-Zeitungs")] // a head is not allowed last
		[InlineData("Brand-Line-Garten")]
		[InlineData("Qwzuserpart-Haus")]
		[InlineData("Haus--Garten")]
		[InlineData("-Haus")]
		[InlineData("Haus-")]
		[InlineData("Haus")]
		public void Explain_AgreesWithTheCheck(string word)
		{
			var german = German();
			var english = English();
			bool accepted = SpellChecker.HyphenatedPartsAccepted(word, german, english, Known, Fugen);
			var parts = HyphenParts.Explain(word, german, english, Known, Fugen);
			bool explainedAccepted = parts is not null && parts.All(p => p.Status != HyphenPartStatus.Unknown);
			Assert.Equal(accepted, explainedAccepted);
			Assert.Equal(SpellChecker.HasHyphenatedShape(word), parts is not null);
		}

		[Fact]
		public void Explain_RecordsNoDictionaryUsage_WhileTheCheckDoes()
		{
			const string word = "Qwzrecordtest-Haus";
			var bundle = German();
			bundle.SharedUser.Add("Qwzrecordtest");

			HyphenParts.Explain(word, bundle, null, Known, Fugen);
			Assert.DoesNotContain("Qwzrecordtest", UsageTracker.SnapshotUser());

			Assert.True(SpellChecker.HyphenatedPartsAccepted(word, bundle, null, Known, Fugen));
			Assert.Contains("Qwzrecordtest", UsageTracker.SnapshotUser());
		}

		[Fact]
		public void Log_RoundTrips_PerLanguage()
		{
			var bundles = new Dictionary<string, Bundle> { ["de"] = German(), ["en"] = English() };
			var path = Path.Combine(dir, LogFileNames.HyphenatedParts);
			int listed = HyphenParts.WriteLog(path,
				[
					new SpellWord("Brand-Xyzzy-Haus", ["de", "en"], 2),
					new SpellWord("plainword", ["de"], 1),
					new SpellWord("Shop-Xyzzy", ["en"], 1),
				],
				bundles, Known, Fugen);
			Assert.Equal(2, listed);

			var read = HyphenParts.Read(dir);
			Assert.Equal(2, read.Count);
			var parts = read["Brand-Xyzzy-Haus"];
			Assert.Equal(["Brand", "Xyzzy", "Haus"], parts.Select(p => p.Text));
			Assert.False(parts[0].IsUnknown);
			Assert.Equal("known word part", parts[0].Summary);
			Assert.True(parts[1].IsUnknown);
			Assert.Equal("unknown in de, en", parts[1].Summary);

			// "Haus" is German, unknown in the English dictionary: still a part to add for "en".
			Assert.True(parts[2].IsUnknown);
			Assert.Equal("system dictionary (de), unknown in en", parts[2].Summary);

			Assert.False(read.ContainsKey("plainword"));
		}

		[Fact]
		public void Log_Missing_ReadsAsEmpty()
		{
			Assert.Empty(HyphenParts.Read(dir));
		}

		[Fact]
		public void StatusTokens_RoundTrip()
		{
			foreach (var status in Enum.GetValues<HyphenPartStatus>())
			{
				Assert.Equal(status, HyphenParts.StatusOf(HyphenParts.TokenOf(status)));
			}
		}
	}
}
