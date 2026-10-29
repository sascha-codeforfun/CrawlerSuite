using System.Security.Cryptography;
using System.Text;
using CrawlerSuite.Core.Boilerplate;
using CrawlerSuite.Core.Snapshots;
using Xunit;

namespace CrawlerSuite.Core.Tests.Snapshots
{
	/// <summary>Re-pruning a snapshot: only its pruned pages and stamp change, never the triage ledger.</summary>
	[Collection("Logger")]
	public class RepruneTests : IDisposable
	{
		private readonly string site = Path.Combine(Path.GetTempPath(), "reprune-" + Guid.NewGuid().ToString("N"), "https___site_test");
		private readonly string snapshot;

		private const string Page = "abc123ice.html";
		private const string Html = "<html><body><nav class=\"frost menu\">menu</nav><main>ice</main><footer class=\"floe\">foot</footer></body></html>";

		public RepruneTests()
		{
			snapshot = Path.Combine(site, "2026-01-02-10-00-00");
			Directory.CreateDirectory(Path.Combine(snapshot, "download"));
			Directory.CreateDirectory(Path.Combine(snapshot, "pruned"));
			File.WriteAllText(Path.Combine(snapshot, "download", Page), Html);
			File.WriteAllText(Path.Combine(snapshot, "download", "keep.html"), Html);
			File.WriteAllText(Path.Combine(snapshot, LogFileNames.Crawler),
				$"2026-01-02 10:00:01 | https://site.test/ice.html | saved | {Page} | discovery\r\n"
				+ "2026-01-02 10:00:02 | https://site.test/reference.html | saved | keep.html | discovery\r\n");
			File.WriteAllText(Path.Combine(snapshot, "pruned", Page), "old prune");
			File.WriteAllText(Path.Combine(snapshot, "pruned", "stale.html"), "a page no longer saved");
			File.WriteAllText(Path.Combine(snapshot, "08-seo-data.csv"), "findings stay as they are");
			File.WriteAllText(Path.Combine(site, "triage-ledger.json"), "{\"ledger\":\"water\"}");
			File.WriteAllText(Path.Combine(site, "triage-ledger.json.1"), "{\"ledger\":\"vapour\"}");
			Logger.Initialize(Path.Combine(site, "..", "test.log"));
		}

		public void Dispose()
		{
			try
			{
				Directory.Delete(Path.GetDirectoryName(site)!, recursive: true);
			}
			catch (IOException)
			{
			}
		}

		private static Config ConfigWith(params BoilerplateGroupConfig[] groups)
		{
			var config = new Config { FilePattern = "*.html", MaxDegreeOfParallelism = 1 };
			config.SpellCheckEngine.BoilerplateGroups = [.. groups];
			return config;
		}

		private static BoilerplateGroupConfig Group(string prefix, string[] referencePages, params (string Type, string Value, bool On)[] parts) => new()
		{
			PathPrefix = prefix,
			PagesToCheckBoiler = [.. referencePages],
			BoilerplateSelectors = [.. parts.Select(p => new BoilerplateSelectorConfig { Type = p.Type, Value = p.Value, Enabled = p.On })],
		};

		/// <summary>Every file under the site folder except the snapshot's pruned/ and stamp, with its hash.</summary>
		private Dictionary<string, string> EverythingElse() => Directory
			.EnumerateFiles(Path.GetDirectoryName(site)!, "*", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}pruned", StringComparison.Ordinal)
				&& !f.EndsWith(Reprune.StampFileName, StringComparison.Ordinal) && !f.EndsWith("test.log", StringComparison.Ordinal))
			.ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

		[Fact]
		public void Run_RewritesPrunedPagesAndStamp_AndNothingElse_NotTheLedger()
		{
			var config = ConfigWith(Group("/", ["/reference.html"], ("class", "frost", true), ("class", "floe", true)));
			var before = EverythingElse();

			var outcome = Reprune.Run(snapshot, config);

			Assert.Null(outcome.Skipped);
			Assert.Equal(2, outcome.Pages);
			var pruned = File.ReadAllText(Path.Combine(snapshot, "pruned", Page));
			Assert.Contains("ice", pruned);
			Assert.DoesNotContain("menu", pruned);
			Assert.DoesNotContain("foot", pruned);

			// The reference page stays whole; the stale page is gone.
			Assert.Contains("menu", File.ReadAllText(Path.Combine(snapshot, "pruned", "keep.html")));
			Assert.False(File.Exists(Path.Combine(snapshot, "pruned", "stale.html")));
			Assert.False(Directory.Exists(Path.Combine(snapshot, "pruned.new")));

			var stamp = Reprune.ReadStamp(snapshot);
			Assert.NotNull(stamp);
			Assert.Equal(Reprune.Fingerprint(config.SpellCheckEngine.BoilerplateGroups), stamp!.Fingerprint);
			Assert.Equal((1, 2), (stamp.Groups, stamp.Rules));

			Assert.Equal(before, EverythingElse());
		}

		[Fact]
		public void Run_ASnapshotWhoseCrawlDidNotFinish_IsLeftAsItWas()
		{
			File.Delete(Path.Combine(snapshot, LogFileNames.Crawler));
			var outcome = Reprune.Run(snapshot, ConfigWith());
			Assert.NotNull(outcome.Skipped);
			Assert.Equal("old prune", File.ReadAllText(Path.Combine(snapshot, "pruned", Page)));
			Assert.Null(Reprune.ReadStamp(snapshot));
		}

		[Fact]
		public void Run_Stopped_KeepsTheOldPrunedPages()
		{
			using var cancel = new CancellationTokenSource();
			cancel.Cancel();
			Assert.ThrowsAny<OperationCanceledException>(() => Reprune.Run(snapshot, ConfigWith(), null, cancel.Token));
			Assert.Equal("old prune", File.ReadAllText(Path.Combine(snapshot, "pruned", Page)));
			Assert.False(Directory.Exists(Path.Combine(snapshot, "pruned.new")));
		}

		[Fact]
		public void Fingerprint_IgnoresOrderAndSwitchedOffParts_ButNotValues()
		{
			var a = Reprune.Fingerprint([Group("/", ["/k1", "/k2"], ("class", "frost", true), ("xpath", "//footer", true))]);
			var reordered = Reprune.Fingerprint([Group("/", ["/k2", "/k1"], ("xpath", "//footer", true), ("Class", "frost", true), ("class", "slush", false))]);
			var changed = Reprune.Fingerprint([Group("/", ["/k1", "/k2"], ("class", "frost-new", true), ("xpath", "//footer", true))]);
			Assert.Equal(a, reordered);
			Assert.NotEqual(a, changed);
			Assert.Equal(16, a.Length);
		}

		[Fact]
		public void Hint_SaysWhatTheStampsMean()
		{
			var now = DateTime.UtcNow;
			var x = new PruneStamp("aaaa", 1, 2, now);
			var y = new PruneStamp("bbbb", 1, 2, now);
			Assert.Equal(string.Empty, Reprune.Hint(x, x, "aaaa"));
			Assert.Contains("different boilerplate groups", Reprune.Hint(x, y, "aaaa"));
			Assert.Contains("not with the boilerplate groups in effect now", Reprune.Hint(x, x, "cccc"));
			Assert.StartsWith("A was pruned before", Reprune.Hint(null, x, "aaaa"));
			Assert.StartsWith("A and B were pruned before", Reprune.Hint(null, null, "aaaa"));
		}
	}
}
