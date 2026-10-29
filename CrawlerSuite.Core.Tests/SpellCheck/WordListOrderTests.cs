using CrawlerSuite.Core.SpellCheck;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	public class WordListOrderTests
	{
		[Fact]
		public void WordsEqualIgnoringCase_ComeOutInOneOrder_WhateverTheyArrivedIn()
		{
			var first = new List<string> { "abc", "Zebra", "ABC", "apfel", "Abc" };
			var second = new List<string> { "Abc", "ABC", "apfel", "abc", "Zebra" };
			first.Sort(WordListOrder.Comparer);
			second.Sort(WordListOrder.Comparer);
			Assert.Equal(first, second);
			Assert.Equal(new[] { "ABC", "Abc", "abc", "apfel", "Zebra" }, first);
		}

		[Fact]
		public void Store_UniqueView_IsTheSame_ForReversedInput()
		{
			var words = new[] { "Abc", "abc", "ABC", "Beta", "beta" };
			var a = words.Select(w => w).ToList();
			var b = words.Reverse().ToList();
			a.Sort(WordListOrder.Comparer);
			b.Sort(WordListOrder.Comparer);
			Assert.Equal(a, b);
		}
	}
}
