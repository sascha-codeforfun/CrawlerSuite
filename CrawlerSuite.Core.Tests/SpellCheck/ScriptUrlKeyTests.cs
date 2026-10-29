using CrawlerSuite.Core.SpellCheck;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Pins the stable-key derivation: the build-time cache-buster is stripped so a bundle's
	/// identity survives re-deploys, while two distinct bundles that happen to share a fingerprint
	/// stay distinct. Fixtures invented.
	/// </summary>
	public class ScriptUrlKeyTests
	{
		[Fact]
		public void StripsTrailingCacheBuster()
		{
			Assert.Equal(
				"/ice/floes/x/foo_bar.min.js",
				ScriptUrlKey.StableKey("https://h/ice/floes/x/foo_bar.min.deadbeef00112233445566778899aabb.js"));
		}

		[Fact]
		public void HashlessNameUnchanged()
		{
			Assert.Equal("/x/xyz.min.js", ScriptUrlKey.StableKey("https://h/x/xyz.min.js"));
			Assert.Equal("/x/foo_bar.js", ScriptUrlKey.StableKey("https://h/x/foo_bar.js"));
		}

		[Fact]
		public void SharedFingerprint_DistinctBundlesStayDistinct()
		{
			// Two distinct bundles (foo_bar, some_calculator) can ship under the SAME hash.
			// Keying on the hash would collide them; keying on the path must not.
			const string h = "1af36ca40815c63b204487cd79530475";
			var a = ScriptUrlKey.StableKey($"https://h/x/foo_bar.min.{h}.js");
			var b = ScriptUrlKey.StableKey($"https://h/x/some_calculator.min.{h}.js");
			Assert.NotEqual(a, b);
		}

		[Fact]
		public void NonStandardFingerprintLeftIntact_NeverWrongJoined()
		{
			// A vendor agent with a trailing numeric id is not the 32-hex shape — left as-is rather
			// than mangled (it may churn, but it can never wrong-join to another asset).
			const string url = "https://h/x/floeagentjs_ABCDEFGHIJKL_12345678901234567.js";
			Assert.Equal("/x/floeagentjs_ABCDEFGHIJKL_12345678901234567.js", ScriptUrlKey.StableKey(url));
		}

		[Fact]
		public void EmptyIsEmpty()
		{
			Assert.Equal(string.Empty, ScriptUrlKey.StableKey(""));
		}
	}
}
