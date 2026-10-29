using System.Text.Json.Nodes;
using CrawlerSuite.Core.Settings;
using Xunit;

namespace CrawlerSuite.Core.Tests.Settings
{
	/// <summary>The settings search: name, key, help and value, ignoring case.</summary>
	public class SettingSearchTests
	{
		private static SettingInfo Info => SettingsCatalog.Find("SpellCheckEngine.IgnoreAcronyms")!;

		[Fact]
		public void Find_LooksInNameKeyAndHelp_IgnoringCase()
		{
			var hit = SettingSearch.Find(Info, Info.Path, Info.Help, null, "ACRONYM")!;
			Assert.True(hit.InLabel);
			Assert.True(hit.InKey);
			Assert.False(hit.InHelp); // the help speaks of capitals and abbreviations
			Assert.True(SettingSearch.Find(Info, Info.Path, Info.Help, null, "CAPITALS")!.InHelp);
			Assert.Null(hit.Value);
			Assert.True(hit.Any);

			Assert.False(SettingSearch.Find(Info, Info.Path, Info.Help, null, "zzzqqq")!.Any);
			Assert.Null(SettingSearch.Find(Info, Info.Path, Info.Help, null, "   "));
		}

		[Fact]
		public void Find_LooksDeepIntoTheValue_AndQuotesTheEntry()
		{
			var value = JsonNode.Parse("""[ { "Name": "first", "Parts": [ "alpha", "a much longer entry with Needle inside it, and then quite a bit more text after it" ] }, "b" ]""");
			var hit = SettingSearch.Find(Info, Info.Path, Info.Help, value, "needle")!;
			Assert.False(hit.InLabel);
			Assert.NotNull(hit.Value);
			Assert.Contains("Needle", hit.Value);
			Assert.EndsWith("…", hit.Value);
		}

		[Fact]
		public void Strings_TakesTextNumbersAndSwitches()
		{
			var value = JsonNode.Parse("""{ "a": "x", "b": 3, "c": true, "d": [ "y", 2.5 ] }""");
			Assert.Equal(["x", "3", "true", "y", "2.5"], SettingSearch.Strings(value));
		}

		[Fact]
		public void Split_MarksEveryHit_IgnoringCase()
		{
			var pieces = SettingSearch.Split("Abc and abc", "ABC");
			Assert.Equal([("Abc", true), (" and ", false), ("abc", true)], pieces.Select(p => (p.Text, p.Hit)));
			Assert.Equal([("plain", false)], SettingSearch.Split("plain", "").Select(p => (p.Text, p.Hit)));
		}

		[Fact]
		public void Contains_EmptyQuery_MatchesNothing()
		{
			Assert.False(SettingSearch.Contains("anything", ""));
			Assert.True(SettingSearch.Contains("Example Site", "site"));
		}
	}
}
