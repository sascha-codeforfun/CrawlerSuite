using Xunit;
using System.Collections.Concurrent;
using CrawlerSuite.Core.Lexicon;

namespace CrawlerSuite.Core.Tests.Lexicon
{
	/// <summary>The dictionaries load when first asked for; every language is there from the start.</summary>
	public class BundleSetTests
	{
		private static (BundleSet Set, ConcurrentBag<string> Loads) Make(params string[] codes)
		{
			var loads = new ConcurrentBag<string>();
			var set = new BundleSet(codes, code =>
			{
				loads.Add(code);
				return new Bundle { Language = code };
			});
			return (set, loads);
		}

		[Fact]
		public void Keys_AreEveryLanguage_WithoutLoading()
		{
			var (set, loads) = Make("de", "en-GB", "en-US", "fr");

			Assert.Equal(["de", "en-GB", "en-US", "fr"], set.Keys);
			Assert.Equal(4, set.Count);
			Assert.True(set.ContainsKey("FR"));
			Assert.False(set.ContainsKey("it"));
			Assert.Empty(loads);
			Assert.Empty(set.Loaded);
		}

		[Fact]
		public void AskingForOne_LoadsOnlyThatOne_Once()
		{
			var (set, loads) = Make("de", "en-GB", "fr");

			Assert.True(set.TryGetValue("EN-gb", out var english));
			Assert.Same(english, set["en-GB"]);
			Assert.Equal("en-GB", english.Language);
			Assert.Equal(["en-GB"], loads);
			Assert.Equal(["en-GB"], set.Loaded);
			Assert.False(set.TryGetValue("it", out _));
		}

		[Fact]
		public void Values_LoadEveryOne_InConfigurationOrder()
		{
			var (set, loads) = Make("fr", "de", "en-US");
			_ = set["de"];

			Assert.Equal(["fr", "de", "en-US"], set.Values.Select(b => b.Language));
			Assert.Equal(["fr", "de", "en-US"], set.Select(kv => kv.Key));
			Assert.Equal(3, loads.Count);
			Assert.Equal(["fr", "de", "en-US"], set.Loaded);
		}

		[Fact]
		public void Load_TheNamedOnes_InParallel_UnknownLeftOut()
		{
			var (set, loads) = Make("de", "en-GB", "en-US", "fr");

			set.Load(["fr", "DE", "it", "fr"]);

			Assert.Equal(["de", "fr"], set.Loaded);
			Assert.Equal(2, loads.Count);
		}

		[Fact]
		public void ManyThreads_LoadEachOnce()
		{
			var (set, loads) = Make("de", "fr");

			Parallel.For(0, 64, i => _ = set[i % 2 == 0 ? "de" : "fr"]);

			Assert.Equal(2, loads.Count);
		}

		[Fact]
		public void Repeated_Code_IsOneLanguage_FirstSpelling()
		{
			var (set, _) = Make("de", "DE", "fr");

			Assert.Equal(["de", "fr"], set.Keys);
		}

		[Fact]
		public void Derive_MakesEachWhenFirstAskedFor_FromTheSource()
		{
			var (set, loads) = Make("de", "fr");
			var bare = set.Derive(b => new Bundle { Language = b.Language + "*" });

			Assert.Empty(loads);
			Assert.Equal("fr*", bare["fr"].Language);
			Assert.Equal(["fr"], loads);
			Assert.Equal(["fr"], set.Loaded);
			Assert.Equal(["fr"], bare.Loaded);
		}

		[Fact]
		public void Release_LetsGoOfEverything()
		{
			var (set, _) = Make("de", "fr");
			_ = set.Values;

			set.Release();

			Assert.Equal(0, set.Count);
			Assert.Empty(set.Keys);
			Assert.False(set.ContainsKey("de"));
		}

		[Fact]
		public void LoadFailure_IsThrownAsItself()
		{
			var set = new BundleSet(["de", "fr"], code => code == "fr" ? throw new InvalidOperationException("bad " + code) : new Bundle());

			var ex = Assert.Throws<InvalidOperationException>(() => set.LoadAll());
			Assert.Equal("bad fr", ex.Message);
		}

		[Fact]
		public void EnglishOf_AsksOnlyTheEnglishDictionaries()
		{
			var (set, loads) = Make("de", "en-GB", "en-US", "fr");

			_ = CrawlerSuite.Core.SpellCheck.ToolsSpellChecker.EnglishOf(set);

			Assert.Equal(["en-GB", "en-US"], loads.Order(StringComparer.Ordinal));
		}
	}
}
