using System.Text;
using CrawlerSuite.Core.Boilerplate;
using CrawlerSuite.Core.Settings;
using CrawlerSuite.Core.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace CrawlerSuite.Core.Tests.Boilerplate
{
	/// <summary>Boilerplate parts of type attribute: their form, what is refused, and that the prune, the matcher and the spell walk agree.</summary>
	public class BoilerplateAttributeTests
	{
		[Theory]
		[InlineData("@data-salt", null, "data-salt")]
		[InlineData("script/@data-x", "script", "data-x")]
		[InlineData("*/@data-x", null, "data-x")]
		[InlineData("  BODY/@Data-X  ", "body", "data-x")]
		public void TryParse_TheTwoForms(string value, string? tag, string name)
		{
			Assert.True(BoilerplateAttribute.TryParse(value, out var t, out var n));
			Assert.Equal((tag, name), (t, n));
		}

		[Theory]
		[InlineData("data-x")]
		[InlineData("//body/@data-x")]
		[InlineData("body@data-x")]
		[InlineData("@")]
		[InlineData("div p/@data-x")]
		[InlineData("@data x")]
		public void Check_OtherForms_AreRefused(string value) =>
			Assert.StartsWith("Write @name for every element or tag/@name for one", BoilerplateAttribute.Check(value));

		[Theory]
		[InlineData("@lang")]
		[InlineData("html/@xml:lang")]
		[InlineData("@class")]
		[InlineData("a/@href")]
		[InlineData("@aria-label")]
		[InlineData("@onclick")]
		[InlineData("img/@alt")]
		public void Check_AttributesTheAnalysisReads_AreRefused(string value) =>
			Assert.StartsWith("The analysis reads", BoilerplateAttribute.Check(value));

		[Fact]
		public void Check_DataAttributes_AreAccepted()
		{
			Assert.Null(BoilerplateAttribute.Check("@data-salt"));
			Assert.Null(BoilerplateAttribute.Check("script/@data-config"));
		}

		[Fact]
		public void PartsOf_EnabledValidAttributePartsOnly()
		{
			var parts = BoilerplateAttribute.PartsOf(
			[
				new() { Type = "attribute", Value = "@data-a" },
				new() { Type = "attribute", Value = "@data-b", Enabled = false },
				new() { Type = "attribute", Value = "@class" },
				new() { Type = "class", Value = "nav" },
				new() { Type = "Attribute", Value = "p/@data-c" },
			]);
			Assert.Equal([(null, "data-a"), ("p", "data-c")], parts);
		}

		[Fact]
		public void RemoveFrom_TakesTheAttribute_TheElementStays()
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<body data-salt=\"1\" id=\"x\"><p data-salt=\"2\">ice</p><script data-salt=\"3\"></script></body>");
			Assert.Equal(1, BoilerplateAttribute.RemoveFrom(doc, [("script", "data-salt")]));
			Assert.Equal("<body data-salt=\"1\" id=\"x\"><p data-salt=\"2\">ice</p><script></script></body>", doc.DocumentNode.OuterHtml);
			Assert.Equal(2, BoilerplateAttribute.RemoveFrom(doc, [(null, "data-salt")]));
			Assert.Equal("<body id=\"x\"><p>ice</p><script></script></body>", doc.DocumentNode.OuterHtml);
		}

		[Fact]
		public void Matcher_AnAttributePart_NeverMakesItsElementBoilerplate()
		{
			var matcher = new BoilerplateMatcher([new BoilerplateSelector("attribute", "@data-salt")]);
			var doc = new HtmlDocument();
			doc.LoadHtml("<body data-salt=\"1\"><p>ice</p></body>");
			var body = doc.DocumentNode.SelectSingleNode("//body");

			Assert.False(matcher.IsEmpty);
			Assert.False(matcher.IsBoilerplate(body));
			Assert.False(matcher.IsBoilerplate(body.SelectSingleNode("p")));
			Assert.True(matcher.IsBoilerplateAttribute(body, "data-salt"));
			Assert.False(matcher.IsBoilerplateAttribute(body, "id"));
		}

		[Fact]
		public void SpellWalk_SkipsTheAttributeOnGroupPages_ChecksItOnReferencePages()
		{
			var matcher = new BoilerplateMatcher([new BoilerplateSelector("attribute", "div/@data-note")]);
			var doc = DomTraverser.Parse(Encoding.UTF8.GetBytes("<html><body><div data-note=\"waterfall words here\"><p>ice text</p></div></body></html>"));

			var onGroupPage = DomTraverser.Traverse(doc, matcher, isReferencePage: false).Select(r => r.RawText).ToList();
			var onReferencePage = DomTraverser.Traverse(doc, matcher, isReferencePage: true).Select(r => r.RawText).ToList();
			Assert.DoesNotContain(onGroupPage, t => t.Contains("waterfall"));
			Assert.Contains(onGroupPage, t => t.Contains("ice text"));
			Assert.Contains(onReferencePage, t => t.Contains("waterfall"));
		}

		[Fact]
		public void Prune_RemovesTheAttributeOnEveryPage_ReferencePagesKeepTheirTemplate()
		{
			var root = Path.Combine(Path.GetTempPath(), "bp-attr-" + Guid.NewGuid().ToString("N"));
			var source = Path.Combine(root, "download");
			var target = Path.Combine(root, "pruned");
			Directory.CreateDirectory(source);
			try
			{
				const string html = "<html><body data-salt=\"9z\"><nav>menu</nav><p>ice</p></body></html>";
				File.WriteAllText(Path.Combine(source, "page.html"), html);
				File.WriteAllText(Path.Combine(source, "reference.html"), html);
				var group = new BoilerplateGroupConfig
				{
					PathPrefix = "/",
					PagesToCheckBoiler = ["/reference.html"],
					BoilerplateSelectors = [new() { Type = "xpath", Value = "//nav" }, new() { Type = "attribute", Value = "body/@data-salt" }],
				};

				BoilerplateSimplifier.Run(source, target, new BoilerplateResolver([group]), f => "https://site.test/" + f, "*.html", 1);

				Assert.Equal("<html><body><p>ice</p></body></html>", File.ReadAllText(Path.Combine(target, "page.html")));
				// The reference page keeps its template whole; only the per-delivery value goes.
				Assert.Equal("<html><body><nav>menu</nav><p>ice</p></body></html>", File.ReadAllText(Path.Combine(target, "reference.html")));
			}
			finally
			{
				Directory.Delete(root, recursive: true);
			}
		}

		[Fact]
		public void Editor_ChecksTheParts_AndRefusesAnAttributeXpath()
		{
			Assert.Contains("attribute", SettingEditors.PartTypes);
			Assert.Null(SettingEditors.CheckPart("attribute", "script/@data-x"));
			Assert.StartsWith("The analysis reads", SettingEditors.CheckPart("attribute", "@lang"));
			Assert.StartsWith("This XPath selects attributes", SettingEditors.CheckPart("xpath", "//body/@class"));
			Assert.Null(SettingEditors.CheckPart("xpath", "//div[@id='nav']"));
		}

		[Fact]
		public void Editor_AClassPartShapedLikeAnAttributeOrAnXpath_PointsToTheRightType()
		{
			Assert.StartsWith("Looks like an attribute", SettingEditors.CheckPart("class", "script/@data-x"));
			Assert.StartsWith("Looks like an attribute", SettingEditors.CheckPart("class", "@data-x"));
			Assert.StartsWith("Looks like an XPath", SettingEditors.CheckPart("class", "//nav"));

			// Utility-framework class names stay valid.
			Assert.Null(SettingEditors.CheckPart("class", "w-1/2 @container w-[200px]"));
			Assert.Null(SettingEditors.CheckPart("class", "nav_footer"));
		}

		[Theory]
		[InlineData("//body/@class", true)]
		[InlineData("//@data-x | //p/@data-y", true)]
		[InlineData("(//body/@data-x)[1]", true)]
		[InlineData("//div[@data-x]", false)]
		[InlineData("//body/@data-x | //p", false)]
		[InlineData("//p/text()", false)]
		public void XpathShape_EndsInAttribute(string xpath, bool expected) => Assert.Equal(expected, XpathShape.EndsInAttribute(xpath));
	}
}
