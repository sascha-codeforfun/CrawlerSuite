using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>The per-delivery tokens the comparison sets aside, and the words it must not take for them.</summary>
	public class DeliveryTokensTests
	{
		[Theory]
		[InlineData("QwErTyIcEpLaNkSa")]
		[InlineData("hYdRoGeNwAtErIcE")]
		[InlineData("FrOsTyMoRnInGvAp")]
		[InlineData("KlMnOpQrStUvWxYz")]
		public void RandomlyCasedLetters_AreTokens(string token) => Assert.True(DeliveryTokens.IsRandomLetters(token));

		[Theory]
		[InlineData("checkFirstSubmit")]
		[InlineData("invisibleButtons")]
		[InlineData("Donaudampfschiff")]
		[InlineData("ICEWATERVAPOURXY")]
		[InlineData("icewatervapourxy")]
		[InlineData("getElementsByTag")]
		[InlineData("QwErTyIcEpLaNkS")]
		[InlineData("QwErTyIcEpLaN1Sa")]
		public void WordsCamelCaseAndOtherLengths_AreNot(string word) => Assert.False(DeliveryTokens.IsRandomLetters(word));

		[Fact]
		public void Neutral_SetsAsideTokensInFieldsScriptsAndFormNames_AndKeepsTheRest()
		{
			const string a = "<form name=\"3fa9c0b17de24e65\"><input name=\"QwErTyIcEpLaNkSa\" id=\"QwErTyIcEpLaNkSa\"></form>"
				+ "<script>IF.put('invisibleButton','hYdRoGeNwAtErIcE'); document.forms['3fa9c0b17de24e65'].elements['QwErTyIcEpLaNkSa'];</script><p>ice</p>";
			const string b = "<form name=\"8e01d5c2b94af37a\"><input name=\"FrOsTyMoRnInGvAp\" id=\"FrOsTyMoRnInGvAp\"></form>"
				+ "<script>IF.put('invisibleButton','KlMnOpQrStUvWxYz'); document.forms['8e01d5c2b94af37a'].elements['FrOsTyMoRnInGvAp'];</script><p>ice</p>";
			Assert.Equal(DeliveryTokens.Neutral(a), DeliveryTokens.Neutral(b));
			Assert.Contains("invisibleButton", DeliveryTokens.Neutral(a));

			// A real change still shows.
			Assert.NotEqual(DeliveryTokens.Neutral(a), DeliveryTokens.Neutral(b.Replace("<p>ice</p>", "<p>water</p>")));
		}

		[Fact]
		public void Neutral_LeavesHexElsewhereAlone()
		{
			// A colour, a hex value outside name/id/for and a fingerprint in a file name stay.
			const string text = "<div data-x=\"3fa9c0b17de24e65\" style=\"color:#a1b2c3\"><img src=\"/lib.3fa9c0b17de24e65.png\"></div>";
			Assert.Equal(text, DeliveryTokens.Neutral(text));
		}

		[Fact]
		public void SideBySide_ALineThatDiffersOnlyInTokens_IsTheSame_WhenSetAside()
		{
			const string a = "<input type=\"text\" name=\"QwErTyIcEpLaNkSa\" tabindex=\"-1\">";
			const string b = "<input type=\"text\" name=\"FrOsTyMoRnInGvAp\" tabindex=\"-1\">";
			Assert.Equal(RowKind.Same, SideBySide.Build(a, b, allLines: true, setCacheBustersAside: true).Rows[0].Kind);
			Assert.Equal(RowKind.Changed, SideBySide.Build(a, b, allLines: true).Rows[0].Kind);
		}
	}
}
