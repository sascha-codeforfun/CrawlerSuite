using System.Text;
using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Known word parts: an entry alone passes, and each entry's use is counted — only for words
	/// the check let pass — so the settings screen can show what it does and whether it is needed.
	/// </summary>
	public class KnownPartUseTests
	{
		private static WordList List(params string[] words) =>
			WordList.CreateFromStreams(
				new MemoryStream(Encoding.UTF8.GetBytes(words.Length + "\n" + string.Join("\n", words) + "\n")),
				new MemoryStream(Encoding.UTF8.GetBytes("SET UTF-8\n")));

		private static readonly string[] Fugen = ["s"];

		// A company name alone and in its product names.
		private const string Wonder = "We at Wonder have the nicest \"Wonder-Bras\" in our \"Wonder-Land\".";

		private static Bundle English() => new()
		{
			System = List("we", "at", "have", "the", "nicest", "in", "our", "bras", "land"),
			Language = "en",
		};

		private static Bundle German() => new() { System = List("Haus", "und", "Garten"), Language = "de" };

		private static (List<string> Misses, KnownPartCounter Counter) Check(string text, Bundle bundle, string[] known, AcronymRule? acronyms = null)
		{
			var counter = new KnownPartCounter();
			var bundles = new Dictionary<string, Bundle> { [bundle.Language] = bundle };
			var checker = new ToolsSpellChecker(bundle, bundles, known, Fugen, acronyms, null, counter);
			return (checker.Check(text, bundle.Language).Select(m => m.Word).ToList(), counter);
		}

		[Fact]
		public void Wonder_AloneAndInItsProducts_Passes()
		{
			Assert.Empty(Check(Wonder, English(), ["Wonder"]).Misses);
			Assert.Equal(["Wonder", "Wonder-Bras", "Wonder-Land"], Check(Wonder, English(), []).Misses);
		}

		[Fact]
		public void Wonder_IsCountedForEveryWordItLetPass()
		{
			var counter = Check(Wonder, English(), ["Wonder"]).Counter;
			Assert.Equal(
				["Wonder", "Wonder-Bras", "Wonder-Land"],
				counter.Covered["Wonder"].Select(w => w.Word).OrderBy(w => w, StringComparer.Ordinal));
		}

		[Fact]
		public void AWordThatStaysFlagged_DoesNotCount()
		{
			var (misses, counter) = Check("Xyzzyland-Qwzz und Haus", German(), ["Xyzzyland"]);
			Assert.Equal(["Xyzzyland-Qwzz"], misses);
			Assert.False(counter.Covered.ContainsKey("Xyzzyland"));
		}

		[Fact]
		public void TheEntryIsCountedAsConfigured_WhateverItsCaseOnThePage()
		{
			var counter = Check("XYZZYLAND-Haus und xyzzyland", German(), ["Xyzzyland"]).Counter;
			Assert.Equal(["XYZZYLAND-Haus", "xyzzyland"], counter.Covered["Xyzzyland"].Select(w => w.Word).OrderBy(w => w, StringComparer.Ordinal));
		}

		[Fact]
		public void Tally_CountsPagesAndDistinctWords_AndListsEveryEntry()
		{
			var tally = new KnownPartTally(["Xyzzyland", "Qwzzland"]);
			tally.Add(Check("Xyzzyland-Haus und Xyzzyland-Haus", German(), ["Xyzzyland", "Qwzzland"]).Counter);
			tally.Add(Check("Xyzzyland-Garten", German(), ["Xyzzyland", "Qwzzland"]).Counter);
			var counts = tally.Counts();
			Assert.Equal(new CrawlerSuite.Core.Settings.UsageCount(2, 2), counts["Xyzzyland"]);
			Assert.Equal(new CrawlerSuite.Core.Settings.UsageCount(0, 0), counts["Qwzzland"]);
		}

		[Fact]
		public void NotNeeded_NamesTheEntriesWhoseWordsPassWithoutThem()
		{
			// "Q" is a single capital, "Haus" a dictionary word, "ABC" an acronym: the words they
			// let pass would pass anyway. "Xyzzyland" is the only thing that knows its part.
			// (A word whose parts are all dictionary words, "Haus-Garten", passes the dictionary
			// itself: no entry is asked, none is counted.)
			string[] known = ["Q", "Haus", "ABC", "Xyzzyland"];
			var acronyms = new AcronymRule(3, true);
			var german = German();
			var tally = new KnownPartTally(known);
			var (misses, counter) = Check("Q-Xyzzyland und Haus-Xyzzyland und ABC-Xyzzyland und Haus-Garten", german, known, acronyms);
			Assert.Empty(misses);
			Assert.DoesNotContain(counter.Covered, c => c.Value.Any(w => w.Word == "Haus-Garten"));
			tally.Add(counter);

			var (notNeeded, neededFor) = tally.Judge(new Dictionary<string, Bundle> { ["de"] = german }, Fugen, acronyms);
			Assert.Equal(["ABC", "Haus", "Q"], notNeeded.Keys.OrderBy(k => k, StringComparer.Ordinal));
			Assert.Equal(["ABC-Xyzzyland (de)", "Haus-Xyzzyland (de)", "Q-Xyzzyland (de)"], neededFor["Xyzzyland"]);
		}

		[Fact]
		public void NotNeeded_LeavesOutAnEntryAWordStillNeeds()
		{
			// "Wonder" alone is not an English word: without the entry, that word is flagged.
			string[] known = ["Wonder"];
			var english = English();
			var tally = new KnownPartTally(known);
			tally.Add(Check(Wonder, english, known).Counter);
			var (notNeeded, neededFor) = tally.Judge(new Dictionary<string, Bundle> { ["en"] = english }, Fugen, null);
			Assert.Empty(notNeeded);
			Assert.Equal(["Wonder (en)", "Wonder-Bras (en)", "Wonder-Land (en)"], neededFor["Wonder"]);
		}

		[Fact]
		public void NotNeeded_JudgesAWord_ByTheLanguagesItsTextWasCheckedInTogether()
		{
			// "Shirt" alone: unknown to German, known to English. Checked in de and en together (one
			// language set), it would pass without the entry; checked in German alone, it needs it.
			var german = German();
			var english = English();
			english.System = List("shirt", "und", "haus");
			var bundles = new Dictionary<string, Bundle> { ["de"] = german, ["en"] = english };
			string[] known = ["Shirt"];

			var together = new KnownPartCounter();
			var checker = new ToolsSpellChecker(german, new Dictionary<string, Bundle>(bundles), known, Fugen, null, null, together);
			_ = checker.Check("Shirt und Haus", "de").ToList();
			_ = checker.Check("Shirt und Haus", "en").ToList();
			var tally = new KnownPartTally(known);
			tally.Add(together);
			Assert.Equal(["Shirt"], tally.Judge(bundles, Fugen, null).NotNeeded.Keys);

			var germanOnly = new KnownPartCounter();
			_ = new ToolsSpellChecker(german, new Dictionary<string, Bundle>(bundles), known, Fugen, null, null, germanOnly).Check("Shirt und Haus", "de").ToList();
			var alone = new KnownPartTally(known);
			alone.Add(germanOnly);
			var (notNeeded, neededFor) = alone.Judge(bundles, Fugen, null);
			Assert.Empty(notNeeded);
			Assert.Equal(["Shirt (de)"], neededFor["Shirt"]);
		}

		[Fact]
		public void ASwitchedOffEntry_IsJudgedByTheFlaggedWordsItWouldLetPass()
		{
			var german = German();
			var tally = new KnownPartTally(["Xyzzyland"]);
			var findings = new List<SpellWord>
			{
				new("Qwzzland-Haus", ["de"], 3),
				new("Qwzzland", ["de"], 2),
				new("Xyzzyland-Qwzzland", ["de"], 1), // passes with both entries
				new("Ploff", ["de"], 4),
			};
			var off = tally.WouldLetPass(["Qwzzland", "Nothingland"], findings, new Dictionary<string, Bundle> { ["de"] = german }, Fugen, null);
			Assert.Equal(new CrawlerSuite.Core.Settings.UsageCount(6, 3), off["Qwzzland"]);
			Assert.Equal(new CrawlerSuite.Core.Settings.UsageCount(0, 0), off["Nothingland"]);
		}

		[Fact]
		public void Counting_ChangesNoVerdict()
		{
			string[] known = ["Wonder", "Xyzzyland"];
			foreach (var text in new[] { Wonder, "Xyzzyland-Qwzz und Haus", "Xyzzyland- und Haus" })
			{
				var bundle = text == Wonder ? English() : German();
				var bundles = new Dictionary<string, Bundle> { [bundle.Language] = bundle };
				var plain = new ToolsSpellChecker(bundle, bundles, known, Fugen).Check(text, bundle.Language).Select(m => m.Word).ToList();
				Assert.Equal(plain, Check(text, bundle, known).Misses);
			}
		}
	}
}
