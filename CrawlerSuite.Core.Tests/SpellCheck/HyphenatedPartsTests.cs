using CrawlerSuite.Core.Lexicon;
using CrawlerSuite.Core.SpellCheck;
using WeCantSpell.Hunspell;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// A hyphenated word that fails as a whole is checked part by part: page language, English,
	/// a single capital letter, or a configured prefix (anywhere, also spanning parts). A typo in
	/// any part keeps the full word flagged. Textbook compounds and an invented brand only.
	/// </summary>
	public class HyphenatedPartsTests
	{
		// The German stub lists bare verb stems as compound heads with their hyphen ("Wasch-"),
		// the way the German dictionary encodes them; heads with a linking s are found by
		// stripping it ("Sicherheits-" → "Sicherheit").
		private static readonly Bundle German = new()
		{
			System = WordList.CreateFromWords([
				"Bahn", "Adresse", "Haus",
				"Sicherheit", "Leistung", "Bewerbung", "Wirtschaft", "Hinweis", "Wohnung", "Suche", "Unterlagen",
				"Gesundheit", "Amt", "Anlage", "Tisch", "Pfanne", "Bad",
				"Wasch-", "Schreib-", "Brat-", "Schwimm-",
			]),
			Language = "de",
		};

		private static readonly Bundle English = new()
		{
			System = WordList.CreateFromWords(["Shirt", "Online", "Shop", "Mail", "Store", "Pro", "Check", "Update", "Coaching", "Boom", "Server", "Team"]),
			Language = "en",
		};

		private static readonly string[] Prefixes = ["Examplix", "Examplix-Haus"];

		private static IReadOnlyCollection<string> Misses(string text) =>
			SpellChecker.CheckPrepared(text, SpellChecker.Prepare(text, Prefixes), German, "de", ["s"], English, Prefixes).Keys;

		[Theory]
		[InlineData("U-Bahn")]           // single capital letter + German
		[InlineData("T-Shirt")]          // single capital letter + English loan
		[InlineData("Online-Shop")]      // English loan parts on a German page
		[InlineData("E-Mail-Adresse")]   // letter, English, German
		[InlineData("Examplix-Store")]   // configured brand first
		[InlineData("Pro-Examplix")]     // configured brand last
		[InlineData("Examplix-Haus-Pro")] // a prefix spanning two parts
		public void KnownParts_Pass(string word)
		{
			Assert.Empty(Misses(word));
		}

		[Theory]
		[InlineData("Examplix-Stroe")]   // typo after a brand: reported as the full word
		[InlineData("Online-Shpo")]      // typo in a loan part
		[InlineData("x-Bahn")]           // a lowercase single letter is not a part
		public void UnknownPart_FlagsTheFullWord(string word)
		{
			Assert.Equal(new[] { word }, Misses(word));
		}

		[Fact]
		public void WordMadeOfPrefixesOnly_IsAccepted()
		{
			Assert.Empty(Misses("Examplix-Haus"));
		}

		[Fact]
		public void SystemDictionaryFirst_UserEntryCountsOnlyWhenNeeded()
		{
			var known = "Knownword" + System.Guid.NewGuid().ToString("N");
			var userOnly = "Userword" + System.Guid.NewGuid().ToString("N");
			var bundle = new Bundle { System = WordList.CreateFromWords([known]) };
			bundle.SharedUser.Add(known);
			bundle.SharedUser.Add(userOnly);

			Assert.True(bundle.Check(known));
			Assert.True(bundle.Check(userOnly));
			Assert.DoesNotContain(known, UsageTracker.SnapshotUser()); // the system dictionary had it: not needed
			Assert.Contains(userOnly, UsageTracker.SnapshotUser());    // only the user entry saved it
		}

		[Theory]
		[InlineData("Sicherheits-Check")]          // head with linking s + English loan
		[InlineData("Leistungs-Update")]
		[InlineData("Bewerbungs-Coaching")]
		[InlineData("Wirtschafts-Boom")]
		[InlineData("Sicherheits-Hinweis")]        // head with linking s + German noun
		[InlineData("Wohnungs-Suche")]
		[InlineData("Bewerbungs-Unterlagen")]
		[InlineData("Gesundheits-Amt")]
		[InlineData("Wasch-Anlage")]               // bare verb stem + German noun
		[InlineData("Schreib-Tisch")]
		[InlineData("Brat-Pfanne")]
		[InlineData("Schwimm-Bad")]
		[InlineData("Examplix-Sicherheits-Check")] // brand + head + noun
		[InlineData("Examplix-Leistungs-Update")]
		public void CompoundHeadBeforeTheLastPart_Passes(string word)
		{
			Assert.Empty(Misses(word));
		}

		[Theory]
		[InlineData("Sicherhiets-Check")]          // typo in the head
		[InlineData("Leistnugs-Update")]
		[InlineData("Bewerbugns-Coaching")]
		[InlineData("Server-Sicherheits")]         // a head cannot be the last part
		[InlineData("Team-Leistungs")]
		public void TypoInTheHead_OrHeadAsTheLastPart_FlagsTheFullWord(string word)
		{
			Assert.Equal(new[] { word }, Misses(word));
		}
	}
}
