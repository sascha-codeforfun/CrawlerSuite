using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Security;
using CrawlerSuite.Core.Urls;
using Xunit;

namespace CrawlerSuite.Core.Tests.Security
{
	/// <summary>Download names, the .log formula guard, the DNS record and the site's address history. Invented addresses.</summary>
	public class SecurityReviewGroupBTests
	{
		// ── Names the site cannot turn against Explorer or NTFS ─────────────────

		[Theory]
		[InlineData("https://site.test/a:b.txt", "a_b.txt")]          // no alternate stream
		[InlineData("https://site.test/file.txt.", "file.txt_")]      // Windows drops a trailing dot
		[InlineData("https://site.test/img/logo.png", "logo.png")]    // ordinary names unchanged
		public void FileNames_AreOnePlainName(string url, string ending) =>
			Assert.EndsWith(ending, Naming.GenerateFileName(url));

		[Fact]
		public void ALongSegment_IsCut_KeepingItsEnding()
		{
			var name = Naming.GenerateFileName("https://site.test/" + new string('x', 400) + ".css");

			Assert.EndsWith(".css", name);
			Assert.Equal(64 + Naming.MaxSegment, name.Length);
		}

		[Theory]
		[InlineData("https://site.test/x.url", ".asset.unverified")]
		[InlineData("https://site.test/x.library-ms", ".unverified")]
		[InlineData("https://site.test/setup.exe", ".asset.unverified")]
		[InlineData("https://site.test/img/floe.png", ".png")]
		[InlineData("https://site.test/css/tide.min.css?v=3", ".css")]
		[InlineData("https://site.test/fonts/ice.woff2", ".woff2")]
		[InlineData("https://site.test/api/vapour", ".htm")]               // no ending: the default .htm
		public void AnAssetsEnding_IsKept_OnlyWhenAssetsHaveIt(string url, string ending) =>
			Assert.EndsWith(ending, Naming.GenerateAssetFileName(url));

		// ── The .log files carry the formula guard; readers strip it ────────────

		[Fact]
		public void ComposeLine_GuardsFormulaTriggers()
		{
			var line = IssueLogWriter.ComposeLine('|', "page.html", "TYPE", "=SUM(A1)", "+49 ice", " -tide", "@floe", "plain");

			Assert.Equal("page.html|TYPE|'=SUM(A1)|'+49 ice| -tide|'@floe|plain", line);
		}

		[Fact]
		public void TheReaders_StripTheGuardAgain()
		{
			var dir = Path.Combine(Path.GetTempPath(), "sr4-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				var pipe = Path.Combine(dir, "10.log");
				File.WriteAllLines(pipe, ["Filename|IssueType|Detail|Context", IssueLogWriter.ComposeLine('|', "a.html", "T", "-floe", "=tide")]);
				var tab = Path.Combine(dir, "31.log");
				File.WriteAllLines(tab, ["# columns", $"  word\tfile.js\t{CsvInjectionGuard.Neutralize("+ice")}"]);

				Assert.Equal(["a.html", "T", "-floe", "=tide"], FindingFiles.Delimited(pipe, "|", 4, header: true).Single());
				Assert.Equal(["word", "file.js", "+ice"], FindingFiles.Tabbed(tab, 3).Single());
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}

		[Fact]
		public void TheIssueLedger_ReadsItsGuardedCellsBack()
		{
			var record = new IssueTracking.IssueRecord { Source = "x", Type = "QUALITY", Url = "https://site.test/a", Word = "-floe", Excerpt = "=tide" };

			var back = IssueTracking.IssueRecord.Parse(record.Serialize());

			Assert.Equal("-floe", back.Word);
			Assert.Equal("=tide", back.Excerpt);
			Assert.Equal(record.Key, back.Key);
		}

		// ── Where the names pointed, recorded and compared ─────────────────────

		[Theory]
		[InlineData("127.0.0.1", DnsRecord.AddressClass.Loopback)]
		[InlineData("10.1.2.3", DnsRecord.AddressClass.Private)]
		[InlineData("172.20.0.1", DnsRecord.AddressClass.Private)]
		[InlineData("192.168.4.5", DnsRecord.AddressClass.Private)]
		[InlineData("169.254.169.254", DnsRecord.AddressClass.LinkLocal)]
		[InlineData("100.64.0.9", DnsRecord.AddressClass.SharedNat)]
		[InlineData("203.0.113.7", DnsRecord.AddressClass.Public)]
		[InlineData("::1", DnsRecord.AddressClass.Loopback)]
		[InlineData("fd00::5", DnsRecord.AddressClass.Private)]
		[InlineData("fe80::1", DnsRecord.AddressClass.LinkLocal)]
		[InlineData("0.0.0.0", DnsRecord.AddressClass.ThisHost)]
		[InlineData("0.12.0.1", DnsRecord.AddressClass.ThisHost)]
		public void AddressClasses(string address, DnsRecord.AddressClass expected) => Assert.Equal(expected, DnsRecord.ClassOf(address));

		// ── The site's history, one file next to the ledger ────────────────────

		private static DnsRecord.Snapshot Snap(string name, params (string Host, string[] Addresses)[] hosts) =>
			new(name, false, hosts.Select(h => new DnsRecord.HostAddresses(h.Host, h.Addresses, null)).ToList(), []);

		[Fact]
		public void AFirstRecord_ReportsNothing()
		{
			var history = new DnsHistory();
			Assert.Empty(history.Changes(Snap("2026-10-01-10-00-00", ("www.site.test", ["203.0.113.4"]))));
		}

		[Fact]
		public void ARotatingPool_SettlesAfterAFewCrawls()
		{
			var history = new DnsHistory();
			history.Add(Snap("2026-10-01-10-00-00", ("www.site.test", ["203.0.113.4"])));
			history.Add(Snap("2026-10-02-10-00-00", ("www.site.test", ["203.0.113.5"])));

			// Back to an address it had two crawls ago: not new.
			Assert.Empty(history.Changes(Snap("2026-10-03-10-00-00", ("www.site.test", ["203.0.113.4"]))));

			var change = Assert.Single(history.Changes(Snap("2026-10-03-10-00-00", ("www.site.test", ["203.0.113.4", "203.0.113.6"]))));
			Assert.False(change.Prominent);
			Assert.Equal("www.site.test: new 203.0.113.6 (public)", change.Text);
		}

		[Fact]
		public void AClassTheHostNeverHad_IsProminent()
		{
			var history = new DnsHistory();
			history.Add(Snap("2026-10-01-10-00-00", ("www.site.test", ["203.0.113.4"]), ("help.site.test", ["203.0.113.9"])));

			var change = Assert.Single(history.Changes(Snap("2026-10-02-10-00-00", ("www.site.test", ["10.0.0.5"]), ("help.site.test", ["203.0.113.9"]))));
			Assert.True(change.Prominent);
			Assert.Equal("www.site.test: 10.0.0.5 (private) — before only public", change.Text);
		}

		[Fact]
		public void TheHistory_IsWrittenAndReadBack_InTheSiteFolder()
		{
			var site = Path.Combine(Path.GetTempPath(), "dns-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(site);
			try
			{
				Assert.True(DnsHistory.Read(site)!.IsEmpty); // none yet: empty, not unreadable

				var history = new DnsHistory();
				foreach (var (name, address) in new[] { ("2026-10-01-10-00-00", "203.0.113.4"), ("2026-10-02-10-00-00", "203.0.113.4"), ("2026-10-03-10-00-00", "203.0.113.5") })
				{
					var record = new DnsRecord(["WWW.site.test"], throughProxy: false);
					record.Connected("www.site.test", IPAddress.Parse(address));
					history.Add(record.ToSnapshot(name));
				}

				history.Write(site);
				var back = DnsHistory.Read(site)!;

				Assert.Equal(
					[("www.site.test", "203.0.113.5", "2026-10-03-10-00-00", "2026-10-03-10-00-00"), ("www.site.test", "203.0.113.4", "2026-10-01-10-00-00", "2026-10-02-10-00-00")],
					back.Entries.Select(e => (e.Host, e.Address, e.FirstSeen, e.LastSeen)));
				Assert.All(back.Entries, e => Assert.Equal([DnsHistory.ConnectedTo], e.SeenAs));
				Assert.False(File.Exists(Path.Combine(site, DnsHistory.FileName + ".tmp")));
			}
			finally
			{
				Directory.Delete(site, recursive: true);
			}
		}

		[Fact]
		public void AnUnreadableHistory_IsNeitherComparedNorOverwritten()
		{
			var site = Path.Combine(Path.GetTempPath(), "dns-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(site);
			try
			{
				File.WriteAllText(Path.Combine(site, DnsHistory.FileName), "{ not json");
				Assert.Null(DnsHistory.Read(site));
			}
			finally
			{
				Directory.Delete(site, recursive: true);
			}
		}

		[Fact]
		public async Task ALookupThatFails_IsRecordedWithItsError()
		{
			var record = new DnsRecord(["no-such-host.invalid"], throughProxy: false);
			await record.ResolveAsync();

			var (addresses, error) = record.Resolved["no-such-host.invalid"];
			Assert.Empty(addresses);
			Assert.False(string.IsNullOrEmpty(error));
		}

		[Fact]
		public async Task TheConnectStep_ConnectsAsTheHandlerWould_AndRecordsWhere()
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			int port = ((IPEndPoint)listener.LocalEndpoint).Port;
			var serve = Task.Run(async () =>
			{
				using var client = await listener.AcceptTcpClientAsync();
				using var stream = client.GetStream();
				var buffer = new byte[4096];
				_ = await stream.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false);
				var body = "ice";
				await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
			});

			var record = new DnsRecord(["127.0.0.1"], throughProxy: false);
			using var handler = new SocketsHttpHandler { ConnectCallback = record.ConnectAsync, UseProxy = false };
			using var http = new HttpClient(handler);
			var text = await http.GetStringAsync($"http://127.0.0.1:{port}/");
			await serve;
			listener.Stop();

			Assert.Equal("ice", text);
			Assert.Equal(["127.0.0.1"], record.ConnectedAddresses["127.0.0.1"]);
		}
	}
}
