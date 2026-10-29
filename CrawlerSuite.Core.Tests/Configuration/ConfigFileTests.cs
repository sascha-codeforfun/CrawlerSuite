using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace CrawlerSuite.Core.Tests.Configuration
{
	/// <summary>crawlersuite.config.json as plain data: comments read but never written, the agreed compact layout.</summary>
	public class ConfigFileTests : IDisposable
	{
		private readonly string dir = Path.Combine(Path.GetTempPath(), "st-configfile-" + Guid.NewGuid().ToString("N"));

		public ConfigFileTests() => Directory.CreateDirectory(dir);

		public void Dispose()
		{
			try { Directory.Delete(dir, recursive: true); } catch { }
		}

		[Fact]
		public void Format_ObjectsIndented_ValueListsOnOneLine_ObjectListsOnePerLine()
		{
			var root = JsonNode.Parse("""
				{ "UseProxy": true, "MaxSingleFileMB": 50,
				  "ScriptFileScanDictionaries": ["de", "en"],
				  "DictionaryMaintenance": { "Mode": "Report", "UpdateUserDictionary": true },
				  "Bundles": [ { "LanguageCode": "de", "DicFile": "a.dic" }, { "LanguageCode": "en", "DicFile": "b.dic" } ],
				  "Empty": [], "Nothing": {} }
				""")!.AsObject();

			var nl = FileFormat.NewLine;
			var expected = string.Join(nl,
				"{",
				"  \"UseProxy\": true,",
				"  \"MaxSingleFileMB\": 50,",
				"  \"ScriptFileScanDictionaries\": [\"de\",\"en\"],",
				"  \"DictionaryMaintenance\": {",
				"    \"Mode\": \"Report\",",
				"    \"UpdateUserDictionary\": true",
				"  },",
				"  \"Bundles\": [",
				"    {\"LanguageCode\":\"de\",\"DicFile\":\"a.dic\"},",
				"    {\"LanguageCode\":\"en\",\"DicFile\":\"b.dic\"}",
				"  ],",
				"  \"Empty\": [],",
				"  \"Nothing\": {}",
				"}") + nl;
			Assert.Equal(expected, ConfigFile.Format(root));
		}

		[Fact]
		public void Read_AcceptsComments_Save_WritesNone_AndKeepsLettersReadable()
		{
			var path = Path.Combine(dir, "crawlersuite.config.json");
			File.WriteAllText(path, "{\n  // a comment from an older file\n  \"Name\": \"Prüfung\", /* inline */\n  \"Unknown\": 1,\n}\n", Encoding.UTF8);

			var root = ConfigFile.Read(path)!;
			ConfigFile.Save(path, root);

			var text = File.ReadAllText(path, Encoding.UTF8);
			Assert.DoesNotContain("//", text, StringComparison.Ordinal);
			Assert.DoesNotContain("/*", text, StringComparison.Ordinal);
			Assert.Contains("\"Prüfung\"", text, StringComparison.Ordinal);
			Assert.Contains("\"Unknown\": 1", text, StringComparison.Ordinal); // keys are kept; dropping is decided before writing
			Assert.True(File.Exists(path + ".1")); // the original, backed up before the first write
			Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })); // no BOM
		}

		[Fact]
		public void Save_Backup_NeverKeepsTheProxyPassword()
		{
			var path = Path.Combine(dir, "crawlersuite.config.json");
			File.WriteAllText(path, "{\n  // kept\n  \"ProxyUser\": \"u\",\n  \"ProxyPassword\": \"se\\\"cret\",\n  \"UseProxy\": true\n}\n");

			ConfigFile.Save(path, ConfigFile.Read(path)!);

			var backup = File.ReadAllText(path + ".1");
			Assert.DoesNotContain("cret", backup, StringComparison.Ordinal);
			Assert.Contains("\"ProxyPassword\": \"\"", backup, StringComparison.Ordinal);
			Assert.Contains("// kept", backup, StringComparison.Ordinal);       // otherwise the file as it was
			Assert.Contains("\"ProxyUser\": \"u\"", backup, StringComparison.Ordinal);
		}

		[Fact]
		public void Read_MissingFile_IsNull()
		{
			Assert.Null(ConfigFile.Read(Path.Combine(dir, "nothing.json")));
		}
	}
}
