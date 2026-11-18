using CrawlerSuite.Core;
using Xunit;

namespace CrawlerSuite.Core.Tests.Configuration
{
	/// <summary>
	/// The configurations Reload offers: crawlersuite.config.json and every name.crawlersuite.config.json
	/// next to the program, the plain one first, the others by name; backups and other files are not
	/// offered. Each is shown by its short name.
	/// </summary>
	public sealed class ConfigChoiceTests : IDisposable
	{
		private readonly string folder = Path.Combine(Path.GetTempPath(), "cs-config-choice-" + Guid.NewGuid().ToString("N"));

		public ConfigChoiceTests() => Directory.CreateDirectory(folder);

		public void Dispose() => Directory.Delete(folder, recursive: true);

		private void Touch(params string[] names)
		{
			foreach (var name in names)
			{
				File.WriteAllText(Path.Combine(folder, name), "{}");
			}
		}

		[Fact]
		public void ThePlainOne_First_TheOthers_ByName()
		{
			Touch("zebra.crawlersuite.config.json", "crawlersuite.config.json", "Apple.crawlersuite.config.json", "mango.crawlersuite.config.json");
			var names = AppPaths.ConfigFiles(folder).Select(Path.GetFileName).ToList();
			Assert.Equal(["crawlersuite.config.json", "Apple.crawlersuite.config.json", "mango.crawlersuite.config.json", "zebra.crawlersuite.config.json"], names);
		}

		[Fact]
		public void BackupsAndOtherFiles_AreNotOffered()
		{
			Touch("crawlersuite.config.json", "crawlersuite.config.json.1", "mango.crawlersuite.config.json.2",
				"mangocrawlersuite.config.json", "crawlersuite.config.json.bak", "notes.json", ".crawlersuite.config.json");
			var names = AppPaths.ConfigFiles(folder).Select(Path.GetFileName).ToList();
			Assert.Equal(["crawlersuite.config.json"], names);
		}

		[Fact]
		public void WithoutThePlainOne_TheOthersAreStillOffered()
		{
			Touch("mango.crawlersuite.config.json", "kiwi.crawlersuite.config.json");
			Assert.Equal(["kiwi", "mango"], AppPaths.ConfigFiles(folder).Select(AppPaths.ConfigName).ToList());
		}

		[Fact]
		public void AMissingFolder_OffersNothing() =>
			Assert.Empty(AppPaths.ConfigFiles(Path.Combine(folder, "not-there")));

		[Theory]
		[InlineData("crawlersuite.config.json", "crawlersuite")]
		[InlineData("kettles.crawlersuite.config.json", "kettles")]
		[InlineData("my.test.crawlersuite.config.json", "my.test")]
		[InlineData("Mango.CrawlerSuite.Config.json", "Mango")]
		[InlineData("other.json", "other.json")]
		public void TheShortName(string file, string name) =>
			Assert.Equal(name, AppPaths.ConfigName(Path.Combine(folder, file)));
	}
}
