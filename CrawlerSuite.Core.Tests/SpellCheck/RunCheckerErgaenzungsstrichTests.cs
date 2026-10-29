using System;
using System.Collections.Generic;
using System.Linq;
using CrawlerSuite.Core.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Pins the German Ergänzungsstrich (suspended-compound) rescue in <see cref="RunChecker"/>.
	/// A lowercase tail like the "-lampe" in "Straßenlaterne/-lampe" tokenizes to a bare "lampe"
	/// (head elided) and fails the dictionary on case alone. When German is in the language set and
	/// the tail sits in a valid suspension context (X/-tail or X, -tail), the CAPITALIZED tail is the
	/// real noun ("Lampe"); if German accepts it, the lowercase flag is a false positive and dropped.
	///
	/// Runs on the GENERAL path (textnode runs here, plus one script run), so it covers HTML prose,
	/// not just JS. Additive and per-occurrence: it can only ever remove a finding, and only when the
	/// suspension marker is present AND the capitalized form is a real German word — so a tail typo
	/// and a markerless lowercase noun (a real case error) still surface, and a non-German page is
	/// untouched. All fixtures are invented generic German (no site content).
	/// </summary>
	public class RunCheckerErgaenzungsstrichTests
	{
		// A stub German dictionary: the compound heads and the CAPITALIZED tails are valid words; the
		// lowercase tails and a deliberate tail typo are not. Only letter-bearing tokens can miss
		// (punctuation is never a miss), mirroring the real checker.
		private static readonly HashSet<string> GermanDict = new(StringComparer.Ordinal)
		{
			"Straßenlaterne", "Bildgröße", "Firmenschriftzüge", "Gletscher", "die",
			"Auflösung", "Lampe", "Handel", "Logos",

			// Conjunction marker: the rebuilt compounds are words, the bare capitalized tails are not
			// (except "Berater", which only the fallback can rescue).
			"Wasserversorger", "Wasserentsorger", "Hausbesitzer", "Hausmieter", "Fachkräfte", "Berater",
			"Gartenplaner", "Gartenbauer", "und", "oder", "bzw", "sowie", "Hund",
		};

		private static RunCheck Checker() => (text, language) =>
			SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text))
				.Select(t => t.Text)
				.Where(w => w.Any(char.IsLetter) && !GermanDict.Contains(w))
				.Distinct()
				.Select(w => new CheckMiss(w, string.Empty));

		private static TextRun TextNode(string text) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text);

		private static TextRun ScriptRun(string text) =>
			new(HtmlNode.CreateNode("<script>x</script>"), RunSource.Script, "script[L1:1]", text);

		private static List<string> Words(TextRun run, string language = "de") =>
			RunChecker.Check(run, language, Checker()).Select(f => f.Word).ToList();

		[Theory]
		[InlineData("Straßenlaterne/-lampe")]       // X/-tail
		[InlineData("Bildgröße/-auflösung")]        // X/-tail
		[InlineData("Firmenschriftzüge, -logos")]   // X, -tail (enumeration)
		[InlineData("Gletscher/-handel")]           // X/-tail
		public void ValidSuspensionTail_IsRescued(string text)
		{
			Assert.Empty(Words(TextNode(text)));
		}

		[Fact]
		public void SlashWithoutHyphen_Surfaces()
		{
			// "X/tail" — the elision hyphen is missing, so it is not a suspension; flag it.
			Assert.Equal(new[] { "lampe" }, Words(TextNode("Straßenlaterne/lampe")));
		}

		[Fact]
		public void TailTypo_StillSurfaces()
		{
			// Valid suspension context, but the capitalized tail is NOT a real word → stays flagged.
			Assert.Equal(new[] { "auflsung" }, Words(TextNode("Bildgröße/-auflsung")));
		}

		[Fact]
		public void StandaloneLowercaseNoun_StillSurfaces()
		{
			// No suspension marker — a lowercase noun in prose is a genuine case error; keep flagging.
			Assert.Equal(new[] { "auflösung" }, Words(TextNode("die auflösung")));
		}

		[Fact]
		public void NonGermanPage_IsNotRescued()
		{
			// The rescue is German-gated: with no German in the set, the tail surfaces as an ordinary miss.
			Assert.Equal(new[] { "lampe" }, Words(TextNode("Straßenlaterne/-lampe"), "en"));
		}

		[Fact]
		public void Rescue_IsSourceAgnostic_AppliesToScriptToo()
		{
			Assert.Empty(Words(ScriptRun("Bildgröße/-auflösung")));
		}

		[Theory]
		[InlineData("Wasserversorger und -entsorger")]  // rebuilt: Wasser + entsorger
		[InlineData("Wasserversorger oder -entsorger")] // oder
		[InlineData("Hausbesitzer bzw. -mieter")]       // bzw. keeps its dot
		[InlineData("Gartenplaner sowie -bauer")]       // sowie
		[InlineData("Fachkräfte sowie -berater")]       // no rebuild is a word: the capitalized tail is
		public void ConjunctionTail_IsRescued(string text)
		{
			Assert.Empty(Words(TextNode(text)));
		}

		[Fact]
		public void ConjunctionTail_Typo_StillSurfaces()
		{
			// Neither a rebuilt compound nor the capitalized tail is a word.
			Assert.Equal(new[] { "entsorgr" }, Words(TextNode("Wasserversorger und -entsorgr")));
		}

		[Fact]
		public void ConjunctionTail_OnANonGermanPage_StillSurfaces()
		{
			Assert.Equal(new[] { "entsorger" }, Words(TextNode("Wasserversorger und -entsorger"), "en"));
		}

		[Fact]
		public void ConjunctionTail_NeedsTheConjunctionToStandAlone()
		{
			// "Hund" ends in "und" but is no conjunction: no suspension, the lowercase tail is flagged.
			Assert.Contains("entsorger", Words(TextNode("Wasserversorger Hund -entsorger")));
		}
	}
}
