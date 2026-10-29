using System.Text;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests
{
	/// <summary>
	/// The allocation shortcuts must not change a single result: each shortcut is checked
	/// against the long way round.
	/// </summary>
	[Collection("Logger")]
	public class AllocationEquivalenceTests : IDisposable
	{
		private readonly string dir;

		public AllocationEquivalenceTests()
		{
			dir = Path.Combine(Path.GetTempPath(), $"AllocEq_{Guid.NewGuid():N}");
			Directory.CreateDirectory(dir);
			Logger.Initialize(Path.Combine(dir, "test.log"));
		}

		public void Dispose()
		{
			try { Directory.Delete(dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── Words prepared once, checked per language ─────────────────────────

		private static Bundle BundleOf(string[] systemWords, params string[] userWords)
		{
			var bundle = new Bundle { System = WordList.CreateFromWords(systemWords) };
			foreach (var w in userWords)
			{
				bundle.SharedUser.Add(w);
			}

			return bundle;
		}

		[Fact]
		public void ToolsSpellChecker_ReusedAcrossLanguagesAndTexts_SameAsFreshChecker()
		{
			var bundles = new Dictionary<string, Bundle>(StringComparer.OrdinalIgnoreCase)
			{
				["de"] = BundleOf(["Haus", "Garten", "und", "Tür"], "Firmenname"),
				["en"] = BundleOf(["house", "garden", "and", "door"]),
			};
			string[] texts =
			[
				"Haus und Garten house and garden Tüür Gartn",
				"Haus- und Gartentür, Haus-Garten (Haus-)Tür Gartn-",
				"BRAND-Haus BRAND Gartn Firmenname firmenname",
				"",
				"Haus­tür ; ? Gartn",
			];
			string[] prefixes = ["BRAND"];
			string[] fugen = ["s", "es"];

			var reused = new ToolsSpellChecker(bundles["de"], bundles, prefixes, fugen);
			foreach (var text in texts)
			{
				foreach (var language in new[] { "de", "en", "de" })
				{
					var fresh = new ToolsSpellChecker(bundles["de"], bundles, prefixes, fugen);
					Assert.Equal(fresh.Check(text, language).ToList(), reused.Check(text, language).ToList());
				}
			}
		}

		[Fact]
		public void Bundle_RemembersSystemVerdicts_UserTierStaysLive()
		{
			var bundle = BundleOf(["Haus"], "Firmenname");
			for (int round = 0; round < 3; round++)
			{
				Assert.True(bundle.Check("Haus"));
				Assert.False(bundle.Check("Hauss"));
				Assert.True(bundle.Check("Firmenname"));
			}

			// A user word added later is still seen: only the system tier is remembered.
			bundle.SharedUser.Add("Hauss");
			Assert.True(bundle.Check("Hauss"));
		}

		[Fact]
		public void Bundle_ReplacedWordList_DoesNotReuseOldVerdicts()
		{
			var bundle = BundleOf(["Haus"]);
			Assert.False(bundle.Check("Garten"));
			bundle.System = WordList.CreateFromWords(["Garten"]);
			Assert.True(bundle.Check("Garten"));
			Assert.False(bundle.Check("Haus"));
		}

		// ── Script literals ───────────────────────────────────────────────────

		[Fact]
		public void Extractor_BufferReuse_KeepsEveryLiteralIntact()
		{
			const string js = "var a = { 'key': \"Erster Text\", other: 'zweiter\\u00E4' };"
				+ " if (x) return /a'b/.test(y); typeof z / 2; f(`drei ${g('x')} vier`, 'fünf');";
			var texts = JsStringLiteralExtractor.Extract(js).Select(l => l.Text).ToList();
			Assert.Equal(["Erster Text", "zweiterä", "drei   vier", "fünf"], texts);
		}

		[Fact]
		public void ScanText_PreClassifiedLiterals_SameOutputAsRunCheckerAlone()
		{
			// Literals the classifier drops (ids, urls, kebab keys) and ones it keeps, mixed.
			const string js = "var a = 'hallo welt'; var b = 'https://example.test/x'; var c = 'data-foo-bar';"
				+ " var d = 'Hallo Wolrd und mehr Text'; var e = 'someId123'; var f = 'Gutn Tag';";
			var bundles = new Dictionary<string, Bundle>(StringComparer.OrdinalIgnoreCase)
			{
				["de"] = BundleOf(["hallo", "welt", "Hallo", "und", "mehr", "Text", "Tag"]),
			};
			var checker = new ToolsSpellChecker(bundles["de"], bundles, [], []);

			foreach (bool gate in new[] { true, false })
			{
				var viaScan = new StringWriter { NewLine = "\n" };
				BulkScriptScanner.ScanText(js, _ => "f.js", ["de"], checker, HtmlAgilityPack.HtmlNode.CreateNode("<script></script>"),
					viaScan, proseRatioGate: gate, proseRatioTau: 0.5);

				// The long way: every literal straight into RunChecker, which classifies itself.
				var expected = new List<string>();
				foreach (var literal in JsStringLiteralExtractor.Extract(js))
				{
					var run = new TextRun(HtmlAgilityPack.HtmlNode.CreateNode("<script></script>"), RunSource.Script, "bulk-script", literal.Text)
					{
						ScriptContext = BulkScriptScanner.ContextWindow(js, literal.RawStart, literal.RawLength),
					};
					var info = new RunChecker.ScriptGateInfo();
					var findings = RunChecker.Check(run, ["de"], checker.Check, scriptProseRatioGate: gate, scriptProseRatioTau: 0.5, scriptGateInfo: info).ToList();
					if (gate && info.Gated)
					{
						expected.Add("gated");
						continue;
					}

					expected.AddRange(findings.Select(f => f.Word));
				}

				var lines = viaScan.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
				var actual = lines.Select(l => l.StartsWith("# gated", StringComparison.Ordinal) ? "gated" : l.Trim().Split('\t')[0]).ToList();
				Assert.Equal(expected, actual);
				Assert.Contains(expected, e => e != "gated"); // something was flagged, so the test bites
			}
		}

		// ── SEO and canonical links from one parse ────────────────────────────

		[Fact]
		public async Task CanonicalMap_FromSeoHarvest_SameAsOwnParse()
		{
			var files = new Dictionary<string, string>
			{
				[$"a{Guid.NewGuid():N}.html"] = "<html><head><link rel=\"canonical\" href=\"https://site.test/a?x=1&amp;y=2\"></head></html>",
				[$"b{Guid.NewGuid():N}.html"] = "<html><head><link rel=\"canonical\" href=\"/rel/path\"><link rel=\"canonical\" href=\"  \"></head></html>",
				[$"c{Guid.NewGuid():N}.html"] = "<html><head><title>none</title></head></html>",
				[$"d{Guid.NewGuid():N}.html"] = "<html><head><link rel=\"canonical\" href=\"https://site.test/d\"><link rel=\"canonical\" href=\"https://site.test/d2\"></head></html>",
			};
			var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var (name, html) in files)
			{
				File.WriteAllText(Path.Combine(dir, name), html, new UTF8Encoding(false));
				index[name] = "https://site.test/" + Path.GetFileNameWithoutExtension(name);
			}

			var harvest = await SEO.ExtractDataAsync(dir, Path.Combine(dir, "08.csv"), "*.html");
			var own = CanonicalAnalyzer.BuildCanonicalMap(dir, index, "*.html");
			var shared = CanonicalAnalyzer.BuildCanonicalMap(dir, index, "*.html", harvest);

			Assert.Equal(own.Keys.ToList(), shared.Keys.ToList());
			foreach (var key in own.Keys)
			{
				Assert.Equal(own[key], shared[key]);
			}

			Assert.Equal(3, own.Count);
			Assert.Equal(4, harvest.Count);
			Assert.All(harvest.Keys, k => Assert.True(Path.IsPathRooted(k)));
		}
	}
}
