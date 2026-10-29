using System.Collections.Generic;
using System.Linq;
using CrawlerSuite.Core.SpellCheck;
using CrawlerSuite.Core.Boilerplate;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests
{
	/// <summary>
	/// The config model + page-to-group resolver. Tests the resolution rules
	/// (longest PathPrefix governs; reference-page membership → prune nothing; no match → check
	/// everything). All fixtures synthetic.
	/// </summary>
	public class SpellCheckEngineConfigTests
	{
		private static BoilerplateGroupConfig Group(string prefix, string[] referencePages, params string[] classTokens)
		{
			return new BoilerplateGroupConfig
			{
				PathPrefix = prefix,
				PagesToCheckBoiler = new List<string>(referencePages),
				BoilerplateSelectors = classTokens
					.Select(t => new BoilerplateSelectorConfig { Type = "class", Value = t })
					.ToList(),
			};
		}

		// A node carrying a class, for probing whether a resolved matcher prunes it.
		private static HtmlNode NodeWithClass(string cls) => HtmlNode.CreateNode($"<div class=\"{cls}\">x</div>");

		[Fact]
		public void NoGroups_ReturnsNullMatcher_CheckEverything()
		{
			var r = new BoilerplateResolver(null);
			var (matcher, isReference) = r.Resolve("/any/page.html");
			Assert.Null(matcher);
			Assert.False(isReference);
		}

		[Fact]
		public void LongestPrefix_Governs()
		{
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de/home.html" }, "general_nav"),
				Group("/de/section-a/", new[] { "/de/section-a/entry.html" }, "section_nav"),
			});

			// A page under the more specific prefix uses the section group's selectors.
			var (matcher, isReference) = r.Resolve("/de/section-a/page1.html");
			Assert.False(isReference);
			Assert.NotNull(matcher);
			Assert.True(matcher!.IsBoilerplate(NodeWithClass("section_nav")));
			Assert.False(matcher.IsBoilerplate(NodeWithClass("general_nav"))); // not the general group's

			// A page under only the general prefix uses the general group's selectors.
			var (m2, _) = r.Resolve("/de/other/page2.html");
			Assert.True(m2!.IsBoilerplate(NodeWithClass("general_nav")));
			Assert.False(m2.IsBoilerplate(NodeWithClass("section_nav")));
		}

		[Fact]
		public void CheckPage_FlaggedAsEntry_UsesOwnGroupsSelectors()
		{
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de/home.html" }, "nav_block"),
			});

			var (matcher, isReference) = r.Resolve("/de/home.html");
			Assert.True(isReference);                 // the reference page → prune nothing (caller passes isReferencePage=true)
			Assert.NotNull(matcher);              // its own group's matcher available
			Assert.True(matcher!.IsBoilerplate(NodeWithClass("nav_block")));
		}

		[Fact]
		public void CheckPage_LocationIndependentOfPrefix()
		{
			// Reference page sits OUTSIDE its group's PathPrefix (root file governing /de/ descendants).
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de-home.html" }, "nav_block"),
			});

			var (_, isReferenceRoot) = r.Resolve("/de-home.html");
			Assert.True(isReferenceRoot);  // recognized as a reference page despite not being under /de/

			var (matcher, isCheckDesc) = r.Resolve("/de/page1.html");
			Assert.False(isCheckDesc); // a governed descendant prunes
			Assert.True(matcher!.IsBoilerplate(NodeWithClass("nav_block")));
		}

		[Fact]
		public void NoMatchingPrefix_AndNotCheckPage_ChecksEverything()
		{
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de/home.html" }, "nav_block"),
			});

			var (matcher, isReference) = r.Resolve("/en/page.html"); // no /en/ group
			Assert.Null(matcher);
			Assert.False(isReference);
		}

		[Fact]
		public void Resolve_AcceptsFullUrl_NotJustPath()
		{
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de/home.html" }, "nav_block"),
			});

			var (matcher, _) = r.Resolve("https://example.com/de/deep/page.html");
			Assert.NotNull(matcher);
			Assert.True(matcher!.IsBoilerplate(NodeWithClass("nav_block")));
		}

		[Fact]
		public void FourBranches_SharedSelectors_EachGoverned()
		{
			// Four prefixes, same selector set, one home reference-page each.
			var shared = new[] { "skip_a", "head_b", "foot_c" };
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de/start.html" }, shared),
				Group("/en/", new[] { "/en/start.html" }, shared),
				Group("/nl/", new[] { "/nl/start.html" }, shared),
				Group("/da/", new[] { "/da/start.html" }, shared),
			});

			foreach (var branch in new[] { "de", "en", "nl", "da" })
			{
				var (matcher, isReference) = r.Resolve($"/{branch}/some/page.html");
				Assert.False(isReference);
				Assert.True(matcher!.IsBoilerplate(NodeWithClass("head_b")));

				var (_, homeIsCheck) = r.Resolve($"/{branch}/start.html");
				Assert.True(homeIsCheck);
			}
		}
	}
}
