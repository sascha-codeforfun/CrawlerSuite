using System.Linq;
using CrawlerSuite.Core.SpellCheck;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// A character reference that survives canonicalization must not become a word.
	///
	/// Canonicalizer decodes exactly once, on purpose, so a double-encoded source
	/// ("&amp;amp;nbsp;" in the markup) leaves the literal text "&amp;nbsp;" in the
	/// run. Without a skip span the word pattern emits "nbsp" as a token, no
	/// dictionary contains it, and an encoding defect is filed as a misspelling —
	/// with the tempting remedy being a dictionary entry that would blind the tool
	/// to the whole class for good.
	/// </summary>
	public class SpellTokenizerEntitySpanTests
	{
		private static List<string> Tokens(string text) =>
			SpellTokenizer
				.Tokenize(new TextRun(null!, RunSource.TextNode, "p[#text]", text))
				.Select(t => t.Text)
				.ToList();

		// ── Entity names are suppressed ──────────────────────────────────

		[Theory]
		[InlineData("Alpha&nbsp;Beta", "nbsp")]
		[InlineData("Report &quot;Alpha&quot; Beta", "quot")]
		[InlineData("Fisch &amp; Co", "amp")]
		[InlineData("Silben&shy;trennung", "shy")]
		[InlineData("A&mdash;B", "mdash")]
		public void Tokenize_EntityNameIsNotEmittedAsAWord(string text, string entityName)
		{
			Assert.DoesNotContain(entityName, Tokens(text));
		}

		[Fact]
		public void Tokenize_SurroundingProseIsStillTokenized()
		{
			var tokens = Tokens("Report &quot;Alpha Beta&quot;&nbsp;Gamma");
			Assert.Contains("Report", tokens);
			Assert.Contains("Alpha", tokens);
			Assert.Contains("Beta", tokens);
			Assert.Contains("Gamma", tokens);
			Assert.DoesNotContain("quot", tokens);
			Assert.DoesNotContain("nbsp", tokens);
		}

		[Fact]
		public void Tokenize_NumericAndHexReferencesAreSuppressed()
		{
			Assert.DoesNotContain("x2013", Tokens("A&#x2013;B"));
			Assert.DoesNotContain("8211", Tokens("A&#8211;B"));
		}

		// ── Prose that merely looks like an entity is untouched ──────────

		[Fact]
		public void Tokenize_ShortAmpersandSequenceInProseIsNotSuppressed()
		{
			// "&D;" has only one character before the semicolon, below the minimum
			// length a real entity name can have, so the span cannot swallow it.
			Assert.Contains("D", Tokens("R&D; die Abteilung"));
		}

		[Fact]
		public void Tokenize_NonAsciiAfterAmpersandIsNotSuppressed()
		{
			// Entity names are ASCII, so an umlaut breaks the shape and the word
			// survives — which matters in exactly the language this tool was built for.
			Assert.Contains("Söhne", Tokens("Fischer&Söhne; Hamburg"));
		}

		[Fact]
		public void Tokenize_AmpersandWithSpacesIsNotSuppressed()
		{
			var tokens = Tokens("Meier & Sohn; danach mehr");
			Assert.Contains("Sohn", tokens);
			Assert.Contains("danach", tokens);
		}

		// ── The span suppresses, it does not rewrite ─────────────────────

		/// <summary>
		/// Suppressing by span rather than by editing the word pattern keeps the
		/// entity in the run text, so the excerpt an operator reads still shows
		/// "&amp;nbsp;" in context and the evidence survives for the content-quality
		/// check that reports the encoding defect under its own name.
		/// </summary>
		[Fact]
		public void Tokenize_LeavesTheRunTextIntact()
		{
			const string text = "Alpha&nbsp;Beta";
			var run = new TextRun(null!, RunSource.TextNode, "p[#text]", text);
			_ = SpellTokenizer.Tokenize(run).ToList();
			Assert.Equal(text, run.RawText);
		}
	}
}
