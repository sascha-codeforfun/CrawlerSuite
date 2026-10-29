using System;
using System.Collections.Generic;
using System.Linq;
using CrawlerSuite.Core.Links;
using CrawlerSuite.Core.Text;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Links
{
	/// <summary>Links handing the site's server an address to forward to: found by the value's shape, never requested. Invented addresses.</summary>
	public class ForwardingLinksTests
	{
		private static IReadOnlyList<ForwardingLinks.Target> In(string link) => ForwardingLinks.In(new Uri(link));

		[Theory]
		[InlineData("https://site.test/go.html?next=https://other.test/a?b=1", "next", "https://other.test/a?b=1", "other.test")]
		[InlineData("https://site.test/go.html?ref=1&to=https%3A%2F%2Fother.test%2Fx", "to", "https://other.test/x", "other.test")]
		[InlineData("https://site.test/go.html?to=https%253A%252F%252Fother.test%252Fx", "to", "https://other.test/x", "other.test")] // encoded twice
		[InlineData("https://site.test/go.html?to=//other.test/x", "to", "//other.test/x", "other.test")]
		[InlineData("https://site.test/go.html?Exit=HTTP://Other.Test/", "Exit", "HTTP://Other.Test/", "other.test")]
		public void AFullAddressInTheQuery_IsATarget_ByItsShapeAlone(string link, string parameter, string address, string host)
		{
			var target = Assert.Single(In(link));

			Assert.Equal(parameter, target.Parameter);
			Assert.Equal(address, target.Address);
			Assert.Equal(host, target.Host);
			Assert.True(target.External);
		}

		[Fact]
		public void AnAddressOfTheSiteItself_IsSameHost()
		{
			var target = Assert.Single(In("https://site.test/go.html?next=https://site.test/floe.html"));

			Assert.False(target.External);
		}

		[Theory]
		[InlineData("https://site.test/floe.html")]
		[InlineData("https://site.test/floe.html?page=2&sort=tide")]
		[InlineData("https://site.test/floe.html?q=http")]                 // a word, no address
		[InlineData("https://site.test/floe.html?path=/floe/ice.html")]     // a path, no host
		[InlineData("https://site.test/floe.html?x=https://localhost/")]    // no dotted host
		[InlineData("https://site.test/floe.html#next=https://other.test/")] // a fragment is no query
		public void NoAddressInTheQuery_IsNoForwardingLink(string link) => Assert.Empty(In(link));

		[Fact]
		public void SeveralAddresses_EachIsATarget()
		{
			var targets = In("https://site.test/go.html?a=https://one.test/&b=https://two.test/");

			Assert.Equal(["one.test", "two.test"], targets.Select(t => t.Host));
		}

		private static ForwardingLinkSource.Row Row(string page, string link, string parameter, string target, string host, bool external) =>
			new(page, link, parameter, target, host, external);

		[Fact]
		public void Triage_OneItemPerPageParameterAndHosts_ExternalFirst_TargetsWrittenOutInFull()
		{
			var rows = new List<ForwardingLinkSource.Row>
			{
				Row("https://site.test/a.html", "/go?next=https://site.test/b.html", "next", "https://site.test/b.html", "site.test", false),
				Row("https://site.test/a.html", "/go?to=https://other.test/x?y=1", "to", "https://other.test/x?y=1", "other.test", true),
				Row("https://site.test/a.html", "/go?to=https://Third.test/", "to", "https://Third.test/", "Third.test", true),
			};

			var items = ForwardingLinkSource.Items(rows, _ => null);

			Assert.Equal(2, items.Count);
			var external = items[0];
			Assert.Equal(ForwardingLinkSource.External, external.Type);
			Assert.True(external.IsSecurity);
			Assert.Equal("to → other.test, Third.test", external.Detail);
			Assert.Equal("FORWARDING_LINK_EXTERNAL|https://site.test/a.html|to|other.test,third.test", external.ItemKey);
			Assert.Contains("Forwards to: https://other.test/x?y=1", external.Context);
			Assert.Contains("Forwards to: https://Third.test/", external.Context);
			Assert.Contains("Not requested by the app", external.Context);
			Assert.Contains("Leitet weiter an: https://other.test/x?y=1", external.ContextIn(DocumentLanguage.German));

			var same = items[1];
			Assert.Equal(ForwardingLinkSource.SameHost, same.Type);
			Assert.False(same.IsSecurity);
		}

		[Fact]
		public void ANewTargetHost_IsANewItem_SoItComesBackToBeDecided()
		{
			var before = ForwardingLinkSource.KeyOf(ForwardingLinkSource.External, "https://site.test/a.html", "to", ["other.test"]);
			var after = ForwardingLinkSource.KeyOf(ForwardingLinkSource.External, "https://site.test/a.html", "to", ["Other.test", "new.test"]);

			Assert.NotEqual(before, after);
			Assert.Equal(before, ForwardingLinkSource.KeyOf(ForwardingLinkSource.External, "https://site.test/a.html", "to", ["OTHER.test", "other.test"]));
		}

		[Fact]
		public void Switches_AreLockedOn()
		{
			var modes = TriageSwitches.Defaults();

			Assert.Equal(TriageMode.On, modes[ForwardingLinkSource.External]);
			Assert.Equal(TriageMode.On, modes[ForwardingLinkSource.SameHost]);
			Assert.True(TriageSwitches.All.Single(s => s.Id == ForwardingLinkSource.External).Locked);
		}

		[Fact]
		public void CweNumbers_AreCitations() =>
			Assert.Equal("https://cwe.mitre.org/data/definitions/601.html", Citations.UrlOf("CWE-601"));
	}
}
