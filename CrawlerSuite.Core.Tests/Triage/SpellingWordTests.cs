using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>The word of a triage spelling item: what User / Site add to a dictionary.</summary>
	public class SpellingWordTests
	{
		[Theory]
		[InlineData("Frost (de)", "Frost")]
		[InlineData("Floe (de, en-GB)", "Floe")]
		[InlineData("Ice-Floe (de)", "Ice-Floe")]
		[InlineData("Vapour", "Vapour")]
		public void The_word_is_the_detail_without_its_languages(string detail, string word)
		{
			Assert.Equal(word, SpellingSource.WordOf(detail));
		}
	}
}
