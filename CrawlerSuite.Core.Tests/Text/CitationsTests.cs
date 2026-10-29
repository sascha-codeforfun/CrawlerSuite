using System;
using System.Collections.Generic;
using System.Linq;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Text;
using Xunit;

namespace CrawlerSuite.Core.Tests.Text
{
	/// <summary>Cited rules as links in place, and the guard: every rule a card cites leads somewhere, and to one of its own links.</summary>
	public class CitationsTests
	{
		[Theory]
		[InlineData("UAX #14", "https://www.unicode.org/reports/tr14/")]
		[InlineData("UTR #20", "https://www.unicode.org/reports/tr20/")]
		[InlineData("LB12a", "https://www.unicode.org/reports/tr14/#LB12a")]
		[InlineData("LB18", "https://www.unicode.org/reports/tr14/#LB18")]
		[InlineData("RFC 9110", "https://www.rfc-editor.org/rfc/rfc9110")]
		[InlineData("2.4.4", "https://www.w3.org/WAI/WCAG22/Understanding/link-purpose-in-context.html")]
		[InlineData("Technique H58", "https://www.w3.org/WAI/WCAG22/Techniques/html/H58")]
		[InlineData("Technik ARIA12", "https://www.w3.org/WAI/WCAG22/Techniques/aria/ARIA12")]
		[InlineData("Fehlerbild F89", "https://www.w3.org/WAI/WCAG22/Techniques/failures/F89")]
		public void ACitation_LeadsToItsRule(string token, string url) => Assert.Equal(url, Citations.UrlOf(token));

		[Theory]
		[InlineData("Give the page one H1 and make the others H2 or lower.")] // heading levels, not techniques
		[InlineData("EN 301 549 v3.2.1 (2021), its successor v4.1.1")] // versions, not success criteria
		[InlineData("C1 controls (U+0080–009F) and build 2.0.170")]
		[InlineData("WCAG 2.2 Level AA")]
		public void WhatOnlyLooksLikeACitation_StaysText(string text)
		{
			var pieces = Citations.Split(text);

			Assert.Single(pieces);
			Assert.Null(pieces[0].Url);
		}

		[Fact]
		public void ATextInPieces_IsTheTextAgain_WithTheCitationsLinked()
		{
			const string text = "a line may break (UAX #14, rules LB12a and LB18), see 2.4.4.";
			var pieces = Citations.Split(text);

			Assert.Equal(text, string.Concat(pieces.Select(p => p.Text)));
			Assert.Equal(["UAX #14", "LB12a", "LB18", "2.4.4"], pieces.Where(p => p.Url is not null).Select(p => p.Text));
		}

		[Fact]
		public void AnUnlistedSuccessCriterion_IsNotLinked_AndTheGuardSeesIt()
		{
			Assert.Single(Citations.Split("see 2.5.8 Target Size"));
			Assert.Equal([("2.5.8", (string?)null)], Citations.In("see 2.5.8 Target Size"));
		}

		/// <summary>
		/// Every rule a card cites in its text — English and German, requirement and legal note too —
		/// has an address, and the card lists it (or the page it is part of) under its links, so the
		/// printed ticket writes the address out.
		/// </summary>
		[Fact]
		public void EveryCitedRule_LeadsToALinkOfItsCard()
		{
			var wrong = new List<string>();
			foreach (var card in DocumentKeys.Cards())
			{
				var texts = new List<string> { card.FullText, DocumentText.T(DocumentLanguage.German, card.Text) };
				if (card.Requirement is { Length: > 0 } requirement)
				{
					texts.Add(DocumentText.T(DocumentLanguage.German, requirement));
				}

				foreach (var (token, url) in texts.SelectMany(Citations.In))
				{
					if (url is null)
					{
						wrong.Add($"{token}: no address (add it to Citations.SuccessCriteria) — {Start(card.Text)}");
					}
					else if (!card.AllLinks.Any(l => l.Url.StartsWith(url, StringComparison.Ordinal)))
					{
						wrong.Add($"{token}: {url} is not among the card's links — {Start(card.Text)}");
					}
				}
			}

			Assert.True(wrong.Count == 0, string.Join("\n", wrong.Distinct()));

			static string Start(string text) => text.Length > 60 ? text[..60] : text;
		}

		[Fact]
		public void TheLineBreakCards_LinkTheRulesTheyCite()
		{
			var inert = FindingsCatalog.Bases["LINE_BREAK_GLUE_INERT"].Links.Select(l => l.Url);
			var doubled = FindingsCatalog.Bases["LINE_BREAK_GLUE_DOUBLE_SPACE"].Links.Select(l => l.Url);

			Assert.Contains("https://www.unicode.org/reports/tr14/#LB12a", inert);
			Assert.Contains("https://www.unicode.org/reports/tr14/#LB12a", doubled);
			Assert.Contains("https://www.unicode.org/reports/tr14/#LB18", doubled);
		}
	}
}
