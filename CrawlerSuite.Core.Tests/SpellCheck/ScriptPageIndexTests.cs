using System.Collections.Generic;
using CrawlerSuite.Core.SpellCheck;
using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Pins the reverse index (bundle → pages): the inversion, reach counting, and — critically —
	/// that a bundle re-deployed under a different fingerprint across pages still unifies to one key
	/// (so reach is real, not split by cache-busters). Synthetic pages; no disk, no Cache.
	/// </summary>
	public class ScriptPageIndexTests
	{
		private static string Page(params string?[] srcs)
		{
			var sb = new System.Text.StringBuilder("<html><body>");
			foreach (var s in srcs)
			{
				sb.Append(s == null ? "<script>var inline=1;</script>" : $"<script src=\"{s}\"></script>");
			}
			sb.Append("</body></html>");
			return sb.ToString();
		}

		[Fact]
		public void SameBundleDifferentHashAcrossPages_UnifiesToReachTwo()
		{
			var idx = ScriptPageIndex.Build(new List<(string, string)>
			{
				("https://s/a.html", Page("/x/floe_check.min.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.js")),
				("https://s/b.html", Page("/x/floe_check.min.BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB.js")),
			});

			Assert.Equal(1, idx.KeyCount);
			Assert.Equal(2, idx.Reach("/x/floe_check.min.js"));
			Assert.Equal(new[] { "https://s/a.html", "https://s/b.html" }, idx.Pages("/x/floe_check.min.js"));
		}

		[Fact]
		public void PageWithTwoBundles_BothReachOne()
		{
			var idx = ScriptPageIndex.Build(new List<(string, string)>
			{
				("https://s/p.html", Page("/x/foo.min.js", "/x/bar.min.js")),
			});

			Assert.Equal(1, idx.Reach("/x/foo.min.js"));
			Assert.Equal(1, idx.Reach("/x/bar.min.js"));
		}

		[Fact]
		public void InlineScriptIgnored()
		{
			var idx = ScriptPageIndex.Build(new List<(string, string)>
			{
				("https://s/p.html", Page(new string?[] { null })),
			});

			Assert.Equal(0, idx.KeyCount);
		}

		[Fact]
		public void UnknownKey_ReachZero_NoPages()
		{
			var idx = ScriptPageIndex.Build(new List<(string, string)>());
			Assert.Equal(0, idx.Reach("/x/nope.min.js"));
			Assert.Empty(idx.Pages("/x/nope.min.js"));
		}

		[Fact]
		public void RelativeSrcResolvedAgainstPageUrl()
		{
			var idx = ScriptPageIndex.Build(new List<(string, string)>
			{
				("https://s/sub/page.html", Page("../x/rel.min.a1b2c3d4e5f60718293a4b5c6d7e8f90.js")),
			});

			Assert.Equal(1, idx.Reach("/x/rel.min.js"));
		}
	}
}
