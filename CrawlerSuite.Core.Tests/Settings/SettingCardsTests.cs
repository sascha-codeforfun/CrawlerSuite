using System.Text.Json.Nodes;
using CrawlerSuite.Core.Settings;
using Xunit;

namespace CrawlerSuite.Core.Tests.Settings
{
	/// <summary>The helpers behind the chip and card editors: file kinds, classes, suppression types, pattern shapes.</summary>
	public class SettingCardsTests
	{
		[Fact]
		public void FileKinds_DefaultIsPagesScriptsStyleSheets()
		{
			var defaults = new Config().Base64AssetFileExtensions;
			var on = SettingEditors.FileKinds.Where(k => k.Extensions.All(defaults.Contains)).Select(k => k.Name);
			Assert.Equal(new[] { "Pages", "Scripts", "Style sheets" }, on.ToArray());
			Assert.Equal(defaults.Count, SettingEditors.FileKinds.Take(3).Sum(k => k.Extensions.Count));
		}

		[Theory]
		[InlineData(".txt", true)]
		[InlineData(".webmanifest", true)]
		[InlineData("txt", false)]
		[InlineData(".a b", false)]
		[InlineData(".", false)]
		public void Extension_Checked(string extension, bool ok) =>
			Assert.Equal(ok, SettingEditors.CheckExtension(extension) is null);

		[Theory]
		[InlineData("card-title", true)]
		[InlineData(".card-title", false)]
		[InlineData("two words", false)]
		[InlineData("", false)]
		public void ClassName_Checked(string name, bool ok) =>
			Assert.Equal(ok, SettingEditors.CheckClassName(name) is null);

		[Fact]
		public void SuppressionTypes_PlainNames_UniqueCodes_NoTemplateDefects()
		{
			var types = SettingEditors.SuppressionTypes;
			Assert.Equal(types.Count, types.Select(t => t.Code).Distinct().Count());
			Assert.Equal(types.Count, types.Select(t => t.Name).Distinct().Count());
			Assert.DoesNotContain(types, t => t.Code == "CONTENT_BEFORE_DOCTYPE");
			Assert.Contains("content before doctype", SettingEditors.SuppressionName("MALFORMED_HTML"));
			Assert.NotNull(SettingEditors.SuppressionName("WORD_COLLISION"));
			Assert.Null(SettingEditors.SuppressionName("NO_SUCH_TYPE"));
			Assert.All(types, t => Assert.DoesNotContain(t.Code, t.Name));
		}

		[Fact]
		public void Shape_FromTheSwitches()
		{
			Assert.Equal(SettingEditors.PatternShape.Words, SettingEditors.ShapeOf(Group(false, "Examplix")));
			Assert.Equal(SettingEditors.PatternShape.Words, SettingEditors.ShapeOf(Group(true, "Examplix", "T-Shirt")));
			Assert.Equal(SettingEditors.PatternShape.Placeholder, SettingEditors.ShapeOf(Group(true, "[[", "]]")));
			var folding = Group(true, "AB", "CD");
			folding["Reference"] = "Other";
			Assert.Equal(SettingEditors.PatternShape.Placeholder, SettingEditors.ShapeOf(folding));
			var binding = Group(true, "{{", "}}");
			binding["OnlyFlagUnbalanced"] = true;
			Assert.Equal(SettingEditors.PatternShape.Binding, SettingEditors.ShapeOf(binding));
		}

		[Fact]
		public void Explanation_FollowsTheSwitches_AndTheFoldLinkFromBothSides()
		{
			var placeholder = Group(true, "[[", "]]");
			placeholder["Reference"] = "Markers";
			placeholder["CaseSensitive"] = true;
			var text = SettingEditors.ExplainPattern(placeholder, []);
			Assert.Contains("findings of Markers", text);
			Assert.Contains("Case must match.", text);

			var words = Group(false, "Examplix");
			words["ExcludeHref"] = true;
			words["CheckScript"] = false;
			var folded = SettingEditors.ExplainPattern(words, ["Placeholders"]);
			Assert.Contains("Link targets are skipped", folded);
			Assert.Contains("Inline scripts are not searched.", folded);
			Assert.Contains("Inside an open Placeholders placeholder", folded);
			Assert.Contains("Case is ignored.", folded);
		}

		[Fact]
		public void Purposes_HaveUniqueNames_AndSecurityOnlyWhereALeakIsMeant()
		{
			var purposes = SettingEditors.PatternPurposes();
			Assert.Equal(purposes.Count, purposes.Select(p => p.Name).Distinct().Count());
			Assert.Equal(SettingEditors.PatternShape.Binding,
				SettingEditors.ShapeOf(purposes.Single(p => p.Name == "Template binding").Group));
			Assert.All(purposes.Where(p => p.Security), p => Assert.Equal("Security", p.Category));
		}

		[Fact]
		public void Lists_FitTheEditors()
		{
			Assert.True(SettingEditors.FitsPlainList(new JsonArray("a", "b")));
			Assert.False(SettingEditors.FitsPlainList(new JsonArray(new JsonObject { ["Value"] = "a" })));
			Assert.True(SettingEditors.FitsObjectList(new JsonArray(new JsonObject())));
			Assert.False(SettingEditors.FitsObjectList(new JsonArray("a")));
		}

		[Fact]
		public void Site_ContentListOn_NeedsTenantAndProductionGroup()
		{
			var on = new SiteDraft { Name = "A", Url = "https://www.example.com", IsPrimary = true, UsesContentList = true };
			var problems = SiteRules.Check([on]);
			Assert.Contains(problems, p => p.Contains("a tenant is needed", StringComparison.Ordinal));
			Assert.Contains(problems, p => p.Contains("a production group is needed", StringComparison.Ordinal));

			on.UsesContentList = false;
			Assert.Empty(SiteRules.Check([on]));

			// An older file without the switch: on when a tenant is set, so the missing group shows.
			var older = new SiteDraft { Name = "B", Url = "https://www.example.com", IsPrimary = true, Tenant = "t" };
			Assert.True(older.UsesContentListResolved);
			Assert.Single(SiteRules.Check([older]));
		}

		private static JsonObject Group(bool grouped, params string[] patterns) => new()
		{
			["Name"] = "G",
			["GroupPatterns"] = grouped,
			["Patterns"] = new JsonArray(patterns.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
		};
	}
}
