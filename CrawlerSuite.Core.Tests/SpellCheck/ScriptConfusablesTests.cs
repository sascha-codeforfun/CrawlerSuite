using CrawlerSuite.Core.SpellCheck;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Look-alike hyphens in code identifiers of a script string are found before tokenizing, so the
	/// ones the tokenizer would split away are reported too; prose and string values stay silent.
	/// </summary>
	public class ScriptConfusablesTests
	{
		[Theory]
		[InlineData("paddingｰtop:4px", "paddingｰtop")]   // halfwidth prolonged sound mark
		[InlineData("borderーcolor: red", "borderーcolor")] // prolonged sound mark, space before ':'
		[InlineData("margin－left:0", "margin－left")]     // fullwidth hyphen-minus
		[InlineData("font―size:1em", "font―size")]       // horizontal bar
		[InlineData("line—height:2", "line—height")]     // em dash
		[InlineData("max−width:9px", "max−width")]       // minus sign
		[InlineData("min–height:9px", "min–height")]     // en dash
		[InlineData("text‑align:left", "text‑align")]    // non-breaking hyphen
		[InlineData("border-top−color:red", "border-top−color")] // ASCII hyphen elsewhere in it
		public void LookAlikeInADeclaredIdentifier_IsFound(string literal, string expected) =>
			Assert.Equal([expected], ScriptConfusables.Find(literal));

		[Fact]
		public void EveryIdentifierOfOneString_IsFound_EachOnce()
		{
			var literal = "margin－left:0;line—height:2;margin－left:1px;color:red";
			Assert.Equal(["margin－left", "line—height"], ScriptConfusables.Find(literal));
		}

		[Theory]
		[InlineData("content:\"a—b\"")]          // a quoted value nested in the string
		[InlineData("content:'a—b: c'")]         // the same in single quotes, with a colon inside
		[InlineData("width:a2−b")]               // a digit next to the look-alike
		[InlineData("—left:0")]                  // a look-alike at the edge
		[InlineData("margin—:0")]                // and at the other edge
		[InlineData("Coming soon—later today.")] // an em dash in a sentence: no colon follows
		[InlineData("Bitte E‑Mail eingeben:")]   // a capital: prose typography, not code
		[InlineData("margin-left:0")]                 // plain ASCII
		[InlineData("")]
		public void ProseValuesAndPlainCode_AreNotFound(string literal) =>
			Assert.Empty(ScriptConfusables.Find(literal));

		[Theory]
		[InlineData("margin－left", "margin-left")]
		[InlineData("text‑align", "text-align")]
		public void AFoundIdentifier_IsLabelledByTheEnricher(string token, string suggestion)
		{
			Assert.True(ConfusablePunctuationDetector.TryDetect(token, out var finding));
			Assert.Equal(suggestion, finding.Suggestion);
		}
	}
}
