using System.Text;
using System.Text.Json;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.Quality;
using CrawlerSuite.Core.SpellCheck;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Proper names from the configuration: a word of a name passes only where the whole name
	/// stands in the same text; the acronym passes anywhere with its exact capitals. Invented names.
	/// </summary>
	public class ProperNamesTests
	{
		private static WordList List(params string[] words) =>
			WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes(words.Length + "\n" + string.Join("\n", words) + "\n")),
				new MemoryStream(Encoding.UTF8.GetBytes("SET UTF-8\n")));

		private static Bundle English() => new()
		{
			System = List("the", "board", "of", "met", "today", "and", "agreed", "alone", "is", "here"),
			Language = "en",
		};

		private static readonly ProperNames Names = new([
			new ProperName { Name = "Xyzzy- und Hausverband", Acronym = "XHVB" },
			new ProperName { Name = "Frotz-Stiftung" },
		]);

		private static List<string> Misses(string text, ProperNames? names)
		{
			var english = English();
			var checker = new ToolsSpellChecker(english, new Dictionary<string, Bundle> { ["en"] = english }, [], ["s"], properNames: names);
			return checker.Check(text, "en").Select(m => m.Word).ToList();
		}

		[Fact]
		public void WordsOfAName_PassWhereTheWholeNameStands()
		{
			const string text = "the board of the Xyzzy- und Hausverband met today";
			Assert.Contains("Xyzzy", Misses(text, null));
			Assert.Empty(Misses(text, Names));
		}

		[Fact]
		public void TheNameAcrossALineBreak_AndInOtherCase_StillCounts()
		{
			Assert.Empty(Misses("the board of the XYZZY-\nund   hausverband met today", Names));
		}

		[Fact]
		public void AWordOfAName_Alone_IsStillFlagged()
		{
			Assert.Equal(["Xyzzy"], Misses("Xyzzy alone is here", Names));
			Assert.Equal(["Frotz"], Misses("Frotz alone is here", Names));
		}

		[Fact]
		public void TheAcronym_PassesAnywhere_WithItsCapitalsOnly()
		{
			Assert.Empty(Misses("XHVB is here", Names));
			Assert.Equal(["Xhvb"], Misses("Xhvb is here", Names));
		}

		[Fact]
		public void HyphenCheck_TakesAPartOfTheName_OnlyWhereTheWholeNameStands()
		{
			const string withName = "the board of the Xyzzy- und\u00AD Hausverband met";
			Assert.True(Names.Accepts("xyzzy- und hausverband", withName));
			Assert.True(Names.Accepts("Xyzzy- und", withName));
			Assert.False(Names.Accepts("Xyzzy- oder", withName));

			// Starts like the name, but the name is not there: no excuse for the space.
			Assert.False(Names.Accepts("Xyzzy- und", "die Xyzzy- und Gartenamt"));

			Assert.True(Names.Has("Xyzzy-  und Hausverband"));
			Assert.False(Names.Has("Xyzzy- und"));
		}

		[Fact]
		public void SettingsTable_LeavesAnEmptyAcronymOut_AndNamesBadRows()
		{
			var spec = CrawlerSuite.Core.Settings.TableSpecs.ProperNames;
			var value = System.Text.Json.Nodes.JsonNode.Parse("""[ { "Name": "Frotz-Stiftung", "Acronym": "FRS" }, { "Name": "Plugh-Amt" } ]""");
			Assert.True(CrawlerSuite.Core.Settings.SettingTables.Fits(spec, value));
			var rows = CrawlerSuite.Core.Settings.SettingTables.Read(spec, value);
			Assert.Equal("", rows[1]["Acronym"]);

			var written = CrawlerSuite.Core.Settings.SettingTables.Write(spec, rows).ToJsonString();
			Assert.Equal("""[{"Name":"Frotz-Stiftung","Acronym":"FRS"},{"Name":"Plugh-Amt"}]""", written);

			rows.Add(new CrawlerSuite.Core.Settings.TableRow(new() { ["Name"] = "", ["Acronym"] = "GW" }));
			rows.Add(new CrawlerSuite.Core.Settings.TableRow(new() { ["Name"] = "Gnusto-Werk", ["Acronym"] = "Gw" }));
			rows.Add(new CrawlerSuite.Core.Settings.TableRow(new() { ["Name"] = "frotz-stiftung", ["Acronym"] = "" }));
			var problems = CrawlerSuite.Core.Settings.SettingTables.Problems(spec, rows);
			Assert.Null(problems[0]);
			Assert.Null(problems[1]);
			Assert.Equal("A name is needed.", problems[2]);
			Assert.Contains("must be one word of capitals", problems[3]);
			Assert.Contains("already in row 1", problems[4]);
		}

		[Theory]
		[InlineData("""{ "Name": "" }""", "has no Name")]
		[InlineData("""{ "Name": "Plugh-Amt", "Acronym": "Pa" }""", "must be one word of capitals")]
		[InlineData("""{ "Name": "Plugh-Amt", "Acronym": "P A" }""", "must be one word of capitals")]
		public void Validation_NamesEachMistake(string entry, string expected)
		{
			var names = JsonSerializer.Deserialize<List<ProperName>>("[" + entry + "]")!;
			var errors = new List<string>();
			Config.ValidateProperNames(names, errors);
			Assert.Contains(errors, e => e.Contains(expected));
		}

		[Fact]
		public void Validation_DoublesAreNamed()
		{
			var errors = new List<string>();
			Config.ValidateProperNames([
				new ProperName { Name = "Plugh-Amt", Acronym = "PLA1" },
				new ProperName { Name = "plugh-amt" },
				new ProperName { Name = "Gnusto-Werk", Acronym = "PLA1" },
			], errors);
			Assert.Contains(errors, e => e.Contains("listed twice") && e.Contains("plugh-amt"));
			Assert.Contains(errors, e => e.Contains("acronym \"PLA1\" is listed twice"));
		}

		[Fact]
		public void AMisspelledField_StopsTheLoad()
		{
			Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<List<ProperName>>("""[ { "Name": "Plugh-Amt", "Acronm": "PLA" } ]"""));
		}

		[Fact]
		public void TheKeeper_AddsToTheConfiguration_KeepsEverythingElse_AndUndoesOnlyItsOwn()
		{
			var dir = Path.Combine(Path.GetTempPath(), "pn-keeper-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				var path = Path.Combine(dir, "config.json");
				File.WriteAllText(path, """{ "SomethingElse": 3, "ProperNames": [ { "Name": "Frotz-Stiftung", "Acronym": "FRS" } ] }""");
				var keeper = new ProperNameKeeper(path);

				Assert.True(keeper.IsKept("frotz-stiftung"));
				Assert.False(keeper.CanUndo("Frotz-Stiftung"));

				Assert.Null(keeper.Keep("Xyzzy- und  Hausverband"));
				Assert.True(keeper.IsKept("Xyzzy- und Hausverband"));
				Assert.True(keeper.CanUndo("Xyzzy- und Hausverband"));
				var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path))!;
				Assert.Equal(["Frotz-Stiftung", "Xyzzy- und Hausverband"], config.ProperNames.Select(n => n.Name));
				Assert.Contains("\"SomethingElse\": 3", File.ReadAllText(path));

				Assert.Null(keeper.Undo("Xyzzy- und Hausverband"));
				Assert.False(keeper.IsKept("Xyzzy- und Hausverband"));
				config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path))!;
				Assert.Equal(["Frotz-Stiftung"], config.ProperNames.Select(n => n.Name));

				var blocked = new ProperNameKeeper(path, () => "not now");
				Assert.Equal("not now", blocked.Keep("Gnusto-Werk"));
				Assert.NotNull(ProperNameKeeper.None.Keep("Gnusto-Werk"));
			}
			finally
			{
				try { Directory.Delete(dir, recursive: true); } catch { }
			}
		}
	}
}
