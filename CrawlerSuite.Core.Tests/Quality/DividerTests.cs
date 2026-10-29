using Xunit;

namespace CrawlerSuite.Core.Tests.Quality
{
	/// <summary>
	/// Tests for the Divider rule primitives used in ticket text. Pins each named primitive's character and
	/// width, the Of(char, count) factory, and the defensive empty-on-non-
	/// positive-count behavior.
	/// </summary>
	public class DividerTests
	{
		[Fact]
		public void NamedPrimitives_AreDefaultWidth()
		{
			Assert.Equal(Divider.DefaultWidth, Divider.Line.Length);
			Assert.Equal(Divider.DefaultWidth, Divider.DoubleLine.Length);
			Assert.Equal(Divider.DefaultWidth, Divider.Underscore.Length);
			Assert.Equal(Divider.DefaultWidth, Divider.Hash.Length);
			Assert.Equal(Divider.DefaultWidth, Divider.Plus.Length);
		}

		[Fact]
		public void NamedPrimitives_UseCorrectCharacter()
		{
			Assert.All(Divider.Line, c => Assert.Equal('-', c));
			Assert.All(Divider.DoubleLine, c => Assert.Equal('=', c));
			Assert.All(Divider.Underscore, c => Assert.Equal('_', c));
			Assert.All(Divider.Hash, c => Assert.Equal('#', c));
			Assert.All(Divider.Plus, c => Assert.Equal('+', c));
		}

		[Fact]
		public void Of_BuildsRequestedWidth()
		{
			Assert.Equal(60, Divider.Of('-', 60).Length);
			Assert.Equal(78, Divider.Of('-', 78).Length);
			Assert.Equal(1, Divider.Of('=', 1).Length);
		}

		[Fact]
		public void Of_UsesRequestedCharacter()
		{
			Assert.All(Divider.Of('*', 10), c => Assert.Equal('*', c));
		}

		[Theory]
		[InlineData(0)]
		[InlineData(-1)]
		[InlineData(-100)]
		public void Of_NonPositiveCount_ReturnsEmpty(int count)
		{
			Assert.Equal(string.Empty, Divider.Of('-', count));
		}

		[Fact]
		public void Of_MatchesLegacyConstruction_ByteIdentical()
		{
			// The Divider primitives stand in for inline `new string('=', 80)` /
			// `new string('-', 60)`. They must be byte-identical so the
			// ticket-text output keeps its exact shape.
			Assert.Equal(new string('=', 80), Divider.DoubleLine);
			Assert.Equal(new string('-', 60), Divider.Of('-', 60));
			Assert.Equal(new string('-', 78), Divider.Of('-', 78));
		}

		[Fact]
		public void NamedPrimitives_CarryNoTrailingNewline()
		{
			// Caller controls line breaks; the primitive is just the rule.
			Assert.DoesNotContain('\n', Divider.DoubleLine);
			Assert.DoesNotContain('\r', Divider.DoubleLine);
		}
	}
}
