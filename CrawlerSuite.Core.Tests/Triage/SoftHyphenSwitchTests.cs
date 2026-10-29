using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>One switch for every soft hyphen, whichever finding type reported it.</summary>
	public class SoftHyphenSwitchTests
	{
		private const string Page = "https://x.test/a";

		[Fact]
		public void SoftHyphens_FromBothTypes_FallUnderTheSoftHyphenSwitch()
		{
			Assert.Equal(TriageSwitches.SoftHyphens, TriageSwitches.IdOf("CONTROL_CHARS_IN_CONTENT", "Found SHY (U+00AD) in <title> text"));
			Assert.Equal(TriageSwitches.SoftHyphens, TriageSwitches.IdOf("INVISIBLE_CHAR_IN_BODY", "SHY (U+00AD) in <span>"));
		}

		[Fact]
		public void OtherControlCharacters_KeepTheirSwitch_EvenWithASoftHyphenInTheText()
		{
			// The finding is about the first character it names; a soft hyphen later in the text is a bystander.
			Assert.Equal("CONTROL_CHARS_IN_CONTENT",
				TriageSwitches.IdOf("CONTROL_CHARS_IN_CONTENT", "Found ZWSP (U+200B) in <p> text ‖ <p> :: a[INVISIBLE SOFT HYPHEN U+00AD]b"));
		}

		[Fact]
		public void SwitchOff_TakesEverySoftHyphenOutOfTriage()
		{
			var items = TriageSource.Group(
			[
				("CONTROL_CHARS_IN_CONTENT", Page, "Found SHY (U+00AD) in <title> text", "x", null),
				("INVISIBLE_CHAR_IN_BODY", Page, "SHY (U+00AD) in <span>", "y", null),
			]);

			var modes = TriageSwitches.Defaults();
			Assert.Equal(TriageMode.Off, modes[TriageSwitches.SoftHyphens]);
			Assert.Empty(TriageBoard.Pages(items, modes, new TriageLedger()));

			modes[TriageSwitches.SoftHyphens] = TriageMode.On;
			Assert.Equal(2, Assert.Single(TriageBoard.Pages(items, modes, new TriageLedger())).Triaged.Count);
		}
	}
}
