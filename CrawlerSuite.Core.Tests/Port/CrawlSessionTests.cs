using System.Text;
using CrawlerSuite.Core.Security;
using CrawlerSuite.Core.Urls;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>
	/// End-to-end download behaviour of the crawl session against a local site:
	/// streaming, the single-file limit with its sample, the stall timeout, the total limit,
	/// the modal-URL gate, the retry round and the pause when a server pushes back.
	/// </summary>
	[Collection("Logger")]
	public class CrawlSessionTests : IDisposable
	{
		private readonly string dir;
		private readonly TestSite site = new();

		public CrawlSessionTests()
		{
			dir = Path.Combine(Path.GetTempPath(), "cs-session-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			Logger.Initialize(Path.Combine(dir, "test.log"));
			RunUi.Attach(null);
		}

		public void Dispose()
		{
			Crawl.EndSession();
			site.Dispose();
			try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
		}

		private string Log => Path.Combine(dir, "00-crawler.log");

		/// <summary>A gate that reacts within a test's time: a short pause, a quick ramp.</summary>
		private static readonly ServerGate.Settings QuickGate = new(
			Threshold: 8, Window: TimeSpan.FromSeconds(10), FirstPause: TimeSpan.FromMilliseconds(300),
			LongestPause: TimeSpan.FromSeconds(1), StartRate: 50, FreeRate: 100,
			RampStep: TimeSpan.FromMilliseconds(100), ProbePoll: TimeSpan.FromMilliseconds(20),
			ProbeGrace: TimeSpan.FromMilliseconds(100));

		private CrawlSession Start(long maxFile = 0, long maxCrawl = 0, int stallSeconds = 30, TimeSpan? answer = null,
			ServerGate.Settings? gate = null) =>
			Crawl.StartSession(new CrawlSessionOptions
			{
				Policy = CrawlPolicy.FromConfig(site.BaseUrl, null),
				MaxSingleFileBytes = maxFile,
				MaxCrawlBytes = maxCrawl,
				StallTimeout = TimeSpan.FromSeconds(stallSeconds),
				AnswerTimeout = answer ?? TimeSpan.FromSeconds(100),
				RetryRoundPause = TimeSpan.Zero,
				Gate = gate ?? QuickGate,
			});

		/// <summary>A crawl phase as the orchestrator runs it: the crawl, then its retry round.</summary>
		private async Task Crawl_(string path, List<ModalQueryParam>? modal = null)
		{
			await Crawl.DownloadWebsiteAsync(site.BaseUrl + path, dir, Log, site.BaseUrl, [], modal ?? []);
			await Crawl.Session!.RetryRoundAsync();
		}

		private string[] Rows() => File.Exists(Log) ? File.ReadAllLines(Log, Encoding.UTF8) : [];

		private static string Status(string row) => row.Split('|', StringSplitOptions.TrimEntries)[2];

		[Fact]
		public async Task PageAndLinkedAsset_AreSavedAndCounted()
		{
			site.Page("/index.html", "<html><body><a href=\"/b.html\">b</a><img src=\"/logo.png\"></body></html>");
			site.Page("/b.html", "<html><body>b</body></html>");
			site.Bytes("/logo.png", "image/png", 2000, announceLength: true);
			var session = Start();

			await Crawl_("/index.html");

			var rows = Rows();
			Assert.Equal(2, rows.Count(r => Status(r) == "saved"));
			Assert.Single(rows, r => Status(r) == "savedAsset");
			Assert.Equal(3, session.FilesSaved);
			Assert.True(session.DownloadedBytes >= 2000);
			Assert.Equal(3, Directory.GetFiles(Path.Combine(dir, "download")).Count(f => !f.EndsWith(".header")));
		}

		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public async Task OverSingleFileLimit_NotStored_SampleAndHeaderKept(bool announced)
		{
			site.Page("/index.html", "<html><body><a href=\"/big.pdf\">big</a></body></html>");
			site.Bytes("/big.pdf", "application/pdf", 400_000, announceLength: announced);
			Start(maxFile: 100_000);

			await Crawl_("/index.html");

			var rows = Rows();
			var oversized = Assert.Single(rows, r => Status(r) == CrawlLogMessages.OversizedStatus);
			Assert.Contains("/big.pdf", oversized);
			Assert.DoesNotContain(rows, r => Status(r) == "saved" && r.Contains("/big.pdf"));

			var sampleName = OversizedFiles.SampleNameForUrl(site.BaseUrl + "/big.pdf");
			var sample = Path.Combine(dir, OversizedFiles.FolderName, sampleName);
			Assert.True(File.Exists(sample));
			Assert.True(new FileInfo(sample).Length <= OversizedFiles.SampleBytes);
			Assert.Equal("PDF", OversizedFiles.Sniff(File.ReadAllBytes(sample)));
			Assert.True(File.Exists(Path.ChangeExtension(sample, "header")));

			// Nothing of the big file is left in download/.
			var downloads = Directory.GetFiles(Path.Combine(dir, "download")).Where(f => !f.EndsWith(".header")).ToList();
			Assert.Single(downloads); // index.html only
		}

		[Fact]
		public async Task StalledDownload_IsAbortedAndLogged()
		{
			site.Page("/index.html", "<html><body><a href=\"/slow.html\">slow</a></body></html>");
			site.Stall("/slow.html", TimeSpan.FromSeconds(20));
			Start(stallSeconds: 1);

			var started = DateTime.UtcNow;
			await Crawl_("/index.html");
			Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "the stall timeout did not end the download");

			// Stalled again in the retry round: logged once, as failed.
			var stalled = Assert.Single(Rows(), r => r.Contains("/slow.html") && Status(r) == "N/A");
			Assert.Contains("stall timeout", stalled);
			Assert.DoesNotContain(Rows(), r => r.Contains("/slow.html") && Status(r) == "saved");
		}

		[Fact]
		public async Task SlowAnswer_IsWaitingNotAStall()
		{
			site.Page("/index.html", "<html><body><a href=\"/busy.html\">busy</a></body></html>");
			site.SlowAnswer("/busy.html", TimeSpan.FromSeconds(2), "<html><body>finally</body></html>");
			var session = Start(stallSeconds: 1, answer: TimeSpan.FromSeconds(10));

			await Crawl_("/index.html");

			Assert.Contains(Rows(), r => r.Contains("/busy.html") && Status(r) == "saved");
			Assert.Equal(0, session.Stalled);
		}

		[Fact]
		public async Task NoAnswer_IsRetriedThenLogged()
		{
			site.Page("/index.html", "<html><body><a href=\"/silent.html\">silent</a></body></html>");
			site.SlowAnswer("/silent.html", TimeSpan.FromSeconds(30), "<html></html>");
			var session = Start(answer: TimeSpan.FromMilliseconds(300));

			await Crawl_("/index.html");

			var row = Assert.Single(Rows(), r => r.Contains("/silent.html") && Status(r) == "N/A");
			Assert.Contains("answer timeout", row);
			Assert.Equal(0, session.Stalled);
		}

		[Fact]
		public async Task TotalLimit_StopsTheCrawl()
		{
			var links = string.Concat(Enumerable.Range(0, 20).Select(i => $"<a href=\"/f{i}.pdf\">f</a>"));
			site.Page("/index.html", $"<html><body>{links}</body></html>");
			for (int i = 0; i < 20; i++)
			{
				site.Bytes($"/f{i}.pdf", "application/pdf", 200_000, announceLength: true);
			}

			var session = Start(maxCrawl: 1_000_000);
			await Crawl_("/index.html");

			Assert.True(session.LimitExceeded);
			Assert.True(Rows().Count(r => Status(r) == "saved") < 21, "the crawl went on after the limit");
		}

		[Fact]
		public async Task ModalUnwrap_ToAnotherHost_IsNotFetched()
		{
			site.Page("/index.html", "<html><body>x</body></html>");
			Start();
			var modal = new List<ModalQueryParam> { new("popup") };

			await Crawl_("/index.html?popup=http%3A%2F%2F169.254.169.254%2Flatest%2Fmeta-data", modal);
			await Crawl_("/index.html?popup=https%3A%2F%2Fevil.example%2F", modal);

			Assert.DoesNotContain(Rows(), r => r.Contains("169.254.169.254") || r.Contains("evil.example"));
		}

		[Fact]
		public async Task MalformedModalValue_FailsThatUrlOnly()
		{
			site.Page("/index.html", "<html><body>x</body></html>");
			Start();
			var modal = new List<ModalQueryParam> { new("popup") };

			var ex = await Record.ExceptionAsync(() => Crawl_("/index.html?popup=http%3A", modal));

			Assert.Null(ex);
		}

		[Fact]
		public async Task APageThatStallsOnce_IsSavedInTheRetryRound_AndNotLoggedAsFailed()
		{
			int calls = 0;
			site.Page("/index.html", "<html><body><a href=\"/hiccup.html\">h</a></body></html>");
			site.Route("/hiccup.html", async ctx =>
			{
				if (Interlocked.Increment(ref calls) == 1)
				{
					ctx.Response.SendChunked = true;
					await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("<html>"));
					await ctx.Response.OutputStream.FlushAsync();
					await Task.Delay(TimeSpan.FromSeconds(5));
					return;
				}

				var bytes = Encoding.UTF8.GetBytes("<html><body>ice</body></html>");
				ctx.Response.ContentType = "text/html";
				ctx.Response.ContentLength64 = bytes.Length;
				await ctx.Response.OutputStream.WriteAsync(bytes);
			});
			var session = Start(stallSeconds: 1);

			await Crawl_("/index.html");

			var rows = Rows().Where(r => r.Contains("/hiccup.html")).ToList();
			Assert.Contains(rows, r => Status(r) == "saved");
			Assert.DoesNotContain(rows, r => Status(r) == "N/A");
			Assert.Single(rows, r => Status(r) == "crawled");
			Assert.Equal((1, 0), (session.Recovered, session.StillFailed));
			Assert.Equal(0, session.ToRetry);
		}

		[Fact]
		public async Task AnAssetTheServerRefusesForAWhile_IsAskedAgain()
		{
			int calls = 0;
			site.Page("/index.html", "<html><body><img src=\"/busy.png\"></body></html>");
			site.Route("/busy.png", async ctx =>
			{
				if (Interlocked.Increment(ref calls) <= 2)
				{
					ctx.Response.StatusCode = 503;
					return;
				}

				ctx.Response.ContentType = "image/png";
				ctx.Response.ContentLength64 = 3;
				await ctx.Response.OutputStream.WriteAsync(new byte[] { 1, 2, 3 });
			});
			Start();

			await Crawl_("/index.html");

			Assert.Contains(Rows(), r => r.Contains("/busy.png") && Status(r) == "savedAsset");
			Assert.DoesNotContain(Rows(), r => r.Contains("/busy.png") && Status(r) == "ServiceUnavailable");
		}

		[Fact]
		public async Task AServerThatPushesBack_IsPaused_ThenEverythingIsSaved()
		{
			// The server answers 503 to everything for its first 30 requests, then serves.
			int calls = 0;
			var links = string.Concat(Enumerable.Range(0, 12).Select(i => $"<a href=\"/p{i}.html\">p</a>"));
			site.Page("/index.html", $"<html><body>{links}</body></html>");
			for (int i = 0; i < 12; i++)
			{
				site.Route($"/p{i}.html", async ctx =>
				{
					if (Interlocked.Increment(ref calls) <= 30)
					{
						ctx.Response.StatusCode = 503;
						return;
					}

					var bytes = Encoding.UTF8.GetBytes("<html><body>water</body></html>");
					ctx.Response.ContentType = "text/html";
					ctx.Response.ContentLength64 = bytes.Length;
					await ctx.Response.OutputStream.WriteAsync(bytes);
				});
			}

			var session = Start();
			await Crawl_("/index.html");

			Assert.True(session.Pauses >= 1, "the cluster of 503s paused no request");
			Assert.Equal(13, Rows().Count(r => Status(r) == "saved"));
			Assert.DoesNotContain(Rows(), r => Status(r) == "ServiceUnavailable");
		}

		[Fact]
		public async Task AddressesThatAlwaysAnswer500_NeverPauseTheCrawl()
		{
			var links = string.Concat(Enumerable.Range(0, 10).Select(i => $"<a href=\"/broken{i}.html\">b</a>"));
			site.Page("/index.html", $"<html><body>{links}</body></html>");
			for (int i = 0; i < 10; i++)
			{
				site.Route($"/broken{i}.html", ctx =>
				{
					ctx.Response.StatusCode = 500;
					return Task.CompletedTask;
				});
			}

			// The real pause, not the quick one of the other tests: a pause would show as minutes.
			var session = Start(gate: ServerGate.Settings.Default);
			var started = DateTime.UtcNow;
			await Crawl_("/index.html");

			Assert.Equal(0, session.Pauses);
			Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(25), "the crawl waited on addresses that always answer 500");
			Assert.Equal(10, Rows().Count(r => Status(r) == "InternalServerError"));
		}

		[Fact]
		public async Task AnAddressWithAnUnfilledPlaceholder_IsNotFetched()
		{
			int calls = 0;
			site.Page("/index.html", "<html><body><a href=\"/x/%7B%7Bname%7D%7D\">t</a><img src=\"/y/{{asset}}.png\"></body></html>");
			site.Route("/x/{{name}}", ctx => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
			Start();

			await Crawl_("/index.html");

			Assert.DoesNotContain(Rows(), r => r.Contains("%7B%7B", StringComparison.OrdinalIgnoreCase) || r.Contains("{{"));
			Assert.Equal(0, calls);
		}

		[Fact]
		public async Task TheRequestCeiling_IsHeld()
		{
			var links = string.Concat(Enumerable.Range(0, 20).Select(i => $"<a href=\"/p{i}.html\">p</a>"));
			site.Page("/index.html", $"<html><body>{links}</body></html>");
			for (int i = 0; i < 20; i++)
			{
				site.Page($"/p{i}.html", "<html><body>ice</body></html>");
			}

			Crawl.StartSession(new CrawlSessionOptions
			{
				Policy = CrawlPolicy.FromConfig(site.BaseUrl, null),
				RetryRoundPause = TimeSpan.Zero,
				Gate = QuickGate,
				MaxRequestsPerSecond = 10,
			});
			var started = DateTime.UtcNow;
			await Crawl_("/index.html");

			// 21 requests at 10 per second: no sooner than 2 s.
			Assert.Equal(21, Rows().Count(r => Status(r) == "saved"));
			Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(1.9), "the crawl went faster than its ceiling");
		}

		[Fact]
		public async Task TheServersProtocol_IsNoted()
		{
			site.Page("/index.html", "<html><body>x</body></html>");
			var session = Start();
			await Crawl_("/index.html");
			Assert.Equal("HTTP/1.1", session.ProtocolNote());
		}

		[Theory]
		[InlineData("User-agent: *\nCrawl-delay: 10\n", 10.0)]
		[InlineData("User-agent: other\nCrawl-delay: 5\n\nUser-agent: *\nDisallow: /x\nCrawl-delay: 2.5\n", 2.5)]
		[InlineData("User-agent: a\nUser-agent: *\nCrawl-delay: 3 # slow\n", 3.0)]
		[InlineData("User-agent: other\nCrawl-delay: 5\n", null)]
		[InlineData("", null)]
		public void CrawlDelay_IsReadForEveryCrawler(string robots, double? expected)
		{
			Assert.Equal(expected, CrawlSession.CrawlDelayOf(robots));
		}

		[Fact]
		public async Task NewSession_StartsWithAnEmptyVisitedSet()
		{
			site.Page("/index.html", "<html><body>x</body></html>");
			Start();
			await Crawl_("/index.html");
			Start();
			await Crawl_("/index.html");

			Assert.Equal(2, Rows().Count(r => Status(r) == "saved"));
		}
	}
}
