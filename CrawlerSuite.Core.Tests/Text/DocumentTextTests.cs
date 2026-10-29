using System.Text.RegularExpressions;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Text;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Text
{
	/// <summary>
	/// The German ticket documents: every English text the app writes into a document has its German,
	/// the table holds nothing else, the German keeps what the code relies on (the first sentence, the
	/// fix, the places for the snapshot's parts), and the items carry their sentences in German.
	/// </summary>
	public class DocumentTextTests
	{
		private static readonly string Root = DocumentKeys.Root();

		[Fact]
		public void EveryEnglishText_HasItsGerman_AndTheTableHoldsNothingElse()
		{
			var keys = DocumentKeys.All(Root);
			Assert.True(keys.Count > 250, $"only {keys.Count} texts gathered: the gathering is broken");

			var missing = keys.Where(k => !DocumentText.German.ContainsKey(k)).ToList();
			Assert.True(missing.Count == 0, "No German for:\n" + string.Join("\n", missing.Select(m => "  " + m)));

			var stale = DocumentText.German.Keys.Except(keys, StringComparer.Ordinal).ToList();
			Assert.True(stale.Count == 0, "German for texts the app no longer writes:\n" + string.Join("\n", stale.Select(m => "  " + m)));
		}

		[Fact]
		public void TheGerman_KeepsThePlaces_TheFirstSentence_AndTheFix()
		{
			var placeholder = new Regex(@"\{\d+\}");
			var fixEnglish = new Regex(@"(?:^|(?<=[.!?:]\s))Fix\b");
			var fixGerman = new Regex(@"(?:^|(?<=[.!?:]\s))Lösung\b");
			foreach (var (english, german) in DocumentText.German)
			{
				Assert.Equal(placeholder.Matches(english).Select(m => m.Value).Order(), placeholder.Matches(german).Select(m => m.Value).Order());
				Assert.False(string.IsNullOrWhiteSpace(german), english);

				// An abbreviation with ". " would end the first sentence — the card's lead — too early.
				Assert.DoesNotMatch(@"\b(?:z|d|u|o|v)\. ?[A-Za-zä]\. |\bca\. |\bbzw\. |\bggf\. |\bvgl\. ", german);

				if (english.Length > 200)
				{
					Assert.Equal(fixEnglish.IsMatch(english), fixGerman.IsMatch(german));
				}
			}

			foreach (var card in DocumentKeys.Cards())
			{
				var german = DocumentText.T(DocumentLanguage.German, card.Text);
				Assert.Equal(SharedSummary.FixOf(card.Text) is null, SharedSummary.FixOf(german, DocumentLanguage.German) is null);
				Assert.DoesNotContain("Standardmäßig", SharedSummary.FixOf(german, DocumentLanguage.German) ?? string.Empty);
			}
		}

		[Fact]
		public void TheGerman_DoesNotAddressTheReader()
		{
			foreach (var german in DocumentText.German.Values)
			{
				Assert.DoesNotMatch(@"(?<![.:!?]\s|^)\bSie\b|\bdu\b|\bdein\w*|\beuch\b", german);
			}
		}

		[Fact]
		public void TheSetting_NamesTheLanguage()
		{
			Assert.Equal(DocumentLanguage.German, DocumentText.Parse("Deutsch"));
			Assert.Equal(DocumentLanguage.German, DocumentText.Parse(" german "));
			Assert.Equal(DocumentLanguage.English, DocumentText.Parse("English"));
			Assert.Equal(DocumentLanguage.English, DocumentText.Parse(null));
			Assert.Equal(DocumentText.EnglishName, new Config().TicketLanguage);
			Assert.Equal("Seite mit Titel versehen", DocumentText.T(DocumentLanguage.German, "2.4.2 Page Titled")[6..]);
			Assert.Equal("2.4.2 Page Titled", DocumentText.T(DocumentLanguage.English, "2.4.2 Page Titled"));
		}

		/// <summary>
		/// English texts of the table that a German sentence must not still hold — except where it names
		/// a place in the app ("SEO › Canonical issues"): the app's screens stay English.
		/// </summary>
		private static IEnumerable<string> EnglishLeftIn(string german) =>
			DocumentText.German
				.Where(kv => kv.Key.Length >= 12 && kv.Key != kv.Value && !kv.Key.Contains('{', StringComparison.Ordinal))
				.Select(kv => kv.Key.Trim())
				.Where(k => german.Contains(k, StringComparison.Ordinal) && !german.Contains("› " + k, StringComparison.Ordinal));

		[Fact]
		public void Items_CarryTheirSentencesInGerman()
		{
			var rows = new[]
			{
				new SeoSource.Row("https://site.test/water.html", "index", "", "", "rain, tide", SEO.Search.In, 0, []),
				new SeoSource.Row("https://site.test/ice.html", "index", "Ice", "Hail", "", SEO.Search.In, 2, ["Ice", "Hail warning"]),
				new SeoSource.Row("https://site.test/tide.html", "index", "Ice", "Hail", "", SEO.Search.In, 1, ["Tide"]),
			};
			var items = SeoSource.Items(rows, _ => null);
			Assert.NotEmpty(items);
			foreach (var item in items.Where(i => i.Context.Length > 0))
			{
				Assert.NotNull(item.ContextDe);
				Assert.NotEqual(item.Context, item.ContextDe);
				Assert.Empty(EnglishLeftIn(item.ContextDe!));
			}

			var missing = Assert.Single(items, i => i.Type == SeoSource.H1Missing);
			Assert.Equal("(no H1)", missing.DetailIn(DocumentLanguage.English));
			Assert.Equal("(keine H1)", missing.DetailIn(DocumentLanguage.German));
			Assert.Equal("Ice", Assert.Single(items, i => i.Type == SeoSource.TitleShared && i.Page.EndsWith("ice.html")).DetailIn(DocumentLanguage.German));
		}

		[Fact]
		public void BrokenLinksAndImages_CarryTheirSentencesInGerman()
		{
			foreach (var reach in CrawlerSuite.Core.Links.BrokenLinkReach.All)
			{
				var german = CrawlerSuite.Core.Links.BrokenLinkReach.BrowserCheck(reach, "https://site.test/img/wave.jpg", "canonical", DocumentLanguage.German);
				Assert.NotEqual(CrawlerSuite.Core.Links.BrokenLinkReach.BrowserCheck(reach, "https://site.test/img/wave.jpg", "canonical"), german);
				Assert.Empty(EnglishLeftIn(german));
			}

			var rows = new List<BrokenLinkSource.Row>
			{
				new("https://site.test/gone.html", "link", "https://site.test/water.html", "/gone.html", "", "", CrawlerSuite.Core.Links.BrokenLinkReach.Click),
			};
			var context = BrokenLinkSource.ContextOf(rows, CrawlerSuite.Core.Links.BrokenLinkReach.Click, shared: false, DocumentLanguage.German);
			Assert.StartsWith("Verwendet als: Link — /gone.html\nPrüfen: Den Link öffnen", context);
			Assert.Equal("Bild", DocumentText.T(DocumentLanguage.German, "image"));
			Assert.Equal("angezeigt, verlinkt", string.Join(", ", new[] { "shown", "linked" }.Select(w => DocumentText.T(DocumentLanguage.German, w))));
		}

		[Fact]
		public void ACard_InGerman_StartsWithItsLead_AndASharedTitleKeepsWhatWasFound()
		{
			var basis = FindingsCatalog.Bases["STYLED_HEADING"];
			var german = DocumentText.T(DocumentLanguage.German, basis.Text);
			Assert.StartsWith("Wer einen Screenreader nutzt", FindingBasis.VerdictOf(german));
			Assert.StartsWith("Lösung", SharedSummary.FixOf(german, DocumentLanguage.German));

			var entry = SharedEntries.EntryOf("STYLED_HEADING", "<span class=\"h1\">", string.Empty);
			Assert.Equal("Gestylte Überschriften — <span class=\"h1\">", SharedEntries.TitleOf(entry, DocumentLanguage.German));
		}
	}
}
