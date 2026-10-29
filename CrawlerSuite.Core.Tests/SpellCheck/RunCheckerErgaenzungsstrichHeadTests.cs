using System.Collections.Generic;
using System.Linq;
using CrawlerSuite.Core.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Pins the German Ergänzungsstrich (suspended-HEAD) rescue in <see cref="RunChecker"/> — the
	/// mirror of the tail rescue. A truncated compound head ("Vereisungs-", "Schul-") borrows its
	/// tail from a coordinated sibling compound and is a real German word only once rejoined
	/// ("Vereisungs- oder Frostgefahr" = Vereisungsgefahr; "Schul- / Studienreise" = Schulreise),
	/// so standing alone it fails the dictionary. The sibling is recovered from the head's BLOCK text
	/// (run.Node), so the rescue covers BOTH the single-text-node form and the anchor-split form where
	/// each conjunct sits in its own &lt;a&gt; and the head lands in a run of its own.
	///
	/// Additive and per-occurrence: it only ever removes a finding, and only when BOTH a suspension
	/// marker (- followed by / und oder bzw. sowie) is present AND some borrowed-tail rejoin (suffix
	/// of >= 4 letters) passes the dictionary — so a head typo, a markerless non-word, a too-short
	/// shared tail, and a non-German page all still surface. All fixtures are invented generic German.
	/// </summary>
	public class RunCheckerErgaenzungsstrichHeadTests
	{
		// Stub German dictionary: the REJOINED compounds and the sibling words are valid; the bare
		// suspended heads are NOT. "Schulweg" is present on purpose — its shared tail "-weg" is three
		// letters, below the borrow floor, so it must NOT be reached (see the floor test).
		private static readonly HashSet<string> GermanDict = new(StringComparer.Ordinal)
		{
			"Vereisungsgefahr", "Frostgefahr", "Schulreise", "Studienreise", "Schulweg", "Sportweg",
			"die", "war", "gut",
			// Suspended ADJECTIVE compounds have a lowercase head: "wetter- und lichtbeständig".
			"wetterbeständig", "lichtbeständig", "und",
		};

		private static RunCheck Checker() => (text, language) =>
			SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text))
				.Select(t => t.Text)
				.Where(w => w.Any(char.IsLetter) && !GermanDict.Contains(w))
				.Distinct()
				.Select(w => new CheckMiss(w, string.Empty));

		// A run whose Node is a real block (so run.Node.InnerText carries the sibling), and whose
		// RawText is the text the harvester actually emitted for the head's run — the whole string for
		// a single text node, or just the head when an element boundary fractured it off.
		private static TextRun HeadRun(string blockHtml, string headRunText) =>
			new(HtmlNode.CreateNode(blockHtml), RunSource.TextNode, "p[#text]", headRunText);

		private static List<string> Words(TextRun run, string language = "de") =>
			RunChecker.Check(run, language, Checker()).Select(f => f.Word).ToList();

		[Fact]
		public void SingleTextNode_SlashForm_IsRescued()
		{
			// "Schul- / Studienreise" lives in one text node; the slash breaks the tokenizer join, so
			// "Schul" surfaces as a bare head and rejoins via "reise" → Schulreise.
			var run = HeadRun("<p>Schul- / Studienreise</p>", "Schul- / Studienreise");
			Assert.Empty(Words(run));
		}

		[Fact]
		public void AnchorSplit_OrForm_IsRescued()
		{
			// Each conjunct in its own <a>, the "- oder" in the text node between, so the head
			// "Vereisungs" is a run of its own. The sibling "Frostgefahr" lives only in the block,
			// from which "gefahr" is borrowed → Vereisungsgefahr.
			var run = HeadRun(
				"<p>Warnung vor <a href=\"#\">Vereisungs</a>- oder <a href=\"#\">Frostgefahr</a>.</p>",
				"Vereisungs");
			Assert.Empty(Words(run));
		}

		[Theory]
		[InlineData("- / ")]
		[InlineData("- und ")]
		[InlineData("- oder ")]
		[InlineData("- bzw. ")]
		[InlineData("- sowie ")]
		public void AllConnectors_AreRescued(string connector)
		{
			// Anchor-split so the head is flagged for every connector (und/oder/bzw./sowie all JOIN and
			// vanish in a single text node; only the markup split surfaces them). "Schul" + "reise".
			var run = HeadRun(
				$"<p><a href=\"#\">Schul</a>{connector}<a href=\"#\">Studienreise</a></p>",
				"Schul");
			Assert.Empty(Words(run));
		}

		[Fact]
		public void HeadTypo_StillSurfaces()
		{
			// Valid suspension shape, but the misspelled head does not rejoin to any real word.
			var run = HeadRun(
				"<p><a href=\"#\">Schhul</a>- und <a href=\"#\">Studienreise</a></p>",
				"Schhul");
			Assert.Equal(new[] { "Schhul" }, Words(run));
		}

		[Fact]
		public void MarkerlessHead_StillSurfaces()
		{
			// No suspension marker — a bare non-word in prose is a genuine miss; keep flagging it.
			var run = HeadRun("<p>Die <a href=\"#\">Vereisungs</a> war gut.</p>", "Vereisungs");
			Assert.Equal(new[] { "Vereisungs" }, Words(run));
		}

		[Fact]
		public void ShortSharedTail_BelowFloor_StillSurfaces()
		{
			// "Schul- und Sportweg": the shared tail is "-weg" (3 letters), below the borrow floor, so
			// "Schulweg" is NOT reachable even though it IS a real word — the deliberate casualty of the
			// >= 4 floor. The head therefore stays flagged.
			var run = HeadRun(
				"<p><a href=\"#\">Schul</a>- und <a href=\"#\">Sportweg</a></p>",
				"Schul");
			Assert.Equal(new[] { "Schul" }, Words(run));
		}

		[Fact]
		public void NoRejoin_StillSurfaces()
		{
			// Valid shape and a known sibling, but no borrowed suffix forms a real word with this head.
			var run = HeadRun(
				"<p><a href=\"#\">Xyz</a>- und <a href=\"#\">Studienreise</a></p>",
				"Xyz");
			Assert.Equal(new[] { "Xyz" }, Words(run));
		}

		[Fact]
		public void LowercaseHead_SingleTextNode_IsRescued()
		{
			// An adjective compound: the head is lowercase, and "wetter" + borrowed "beständig" is a word.
			var run = HeadRun("<p>wetter- und lichtbeständig</p>", "wetter- und lichtbeständig");
			Assert.Empty(Words(run));
		}

		[Fact]
		public void LowercaseHead_AnchorSplit_IsRescued()
		{
			var run = HeadRun(
				"<p>Lack, <a href=\"#\">wetter</a>- und <a href=\"#\">lichtbeständig</a>.</p>",
				"wetter");
			Assert.Empty(Words(run));
		}

		[Fact]
		public void LowercaseHeadTypo_StillSurfaces()
		{
			// The misspelled lowercase head rejoins to no real word, so it stays flagged.
			var run = HeadRun("<p>wettre- und lichtbeständig</p>", "wettre- und lichtbeständig");
			Assert.Equal(new[] { "wettre" }, Words(run));
		}

		[Fact]
		public void LowercaseMarkerlessWord_StillSurfaces()
		{
			// No coordinator after the hyphen: not a suspension, so no rescue.
			var run = HeadRun("<p>die wetter war gut.</p>", "die wetter war gut.");
			Assert.Equal(new[] { "wetter" }, Words(run));
		}

		[Fact]
		public void NonGermanPage_IsNotRescued()
		{
			// German-gated: with no German in the set, the head surfaces as an ordinary miss.
			var run = HeadRun(
				"<p>Warnung vor <a href=\"#\">Vereisungs</a>- oder <a href=\"#\">Frostgefahr</a>.</p>",
				"Vereisungs");
			Assert.Equal(new[] { "Vereisungs" }, Words(run, "en"));
		}
	}
}
