using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>The blank lines a page compared as pruned sets aside: empty, or only spaces and tabs.</summary>
	public class BlankLinesTests
	{
		[Fact]
		public void Empty_and_whitespace_only_lines_go_with_their_line_ends()
		{
			Assert.Equal("<p>ice</p>\n<p>water</p>", BlankLines.Neutral("<p>ice</p>\n\n\t\t\n   \n<p>water</p>\n"));
		}

		[Fact]
		public void A_tabs_only_line_and_an_empty_line_are_alike()
		{
			Assert.Equal(BlankLines.Neutral("<div>\n\t\t\n</div>"), BlankLines.Neutral("<div>\n\n</div>"));
			Assert.Equal(BlankLines.Neutral("<div>\n\t\t\n\n</div>"), BlankLines.Neutral("<div>\n</div>"));
		}

		[Fact]
		public void Windows_line_ends_on_blank_lines_go_too_but_content_lines_keep_theirs()
		{
			Assert.Equal("<p>ice</p>\r\n<p>water</p>\r", BlankLines.Neutral("<p>ice</p>\r\n \t\r\n\r\n<p>water</p>\r\n"));
			Assert.NotEqual(BlankLines.Neutral("<p>ice</p>\r\n"), BlankLines.Neutral("<p>ice</p>\n"));
		}

		[Fact]
		public void Whitespace_within_a_line_still_counts()
		{
			Assert.NotEqual(BlankLines.Neutral("\t<p>ice</p>"), BlankLines.Neutral("<p>ice</p>"));
			Assert.NotEqual(BlankLines.Neutral("<p>ice </p>"), BlankLines.Neutral("<p>ice</p>"));
		}

		[Fact]
		public void A_non_breaking_space_is_content_not_blank()
		{
			Assert.Equal("\u00A0", BlankLines.Neutral("\n\u00A0\n"));
		}

		[Fact]
		public void Nothing_but_blank_lines_is_empty()
		{
			Assert.Equal(string.Empty, BlankLines.Neutral("\n \n\t\n"));
			Assert.Equal(string.Empty, BlankLines.Neutral(string.Empty));
		}
	}
}
