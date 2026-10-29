using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Security;
using Xunit;

namespace CrawlerSuite.Core.Tests.Security
{
	/// <summary>Lazy matches, samples of quarantined assets, and addresses no website has. Invented addresses.</summary>
	public class SecurityReviewSecondFollowUpTests
	{
		// ── Matches are walked, not collected ──────────────────────────────────

		[Fact]
		public void AWalkedMatch_IsNotKept()
		{
			var text = string.Concat(Enumerable.Repeat("1 0 obj ", 10_000));
			var regex = new Regex(@"\d+ 0 obj");
			WeakReference first = null!;
			int count = 0;

			void Walk()
			{
				foreach (var m in regex.Each(text))
				{
					if (count++ == 0)
					{
						first = new WeakReference(m);
					}
				}
			}

			Walk();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();

			Assert.Equal(10_000, count);
			Assert.False(first.IsAlive); // a MatchCollection would still hold it, and every one after it
		}

		/// <summary>No code collects matches: every regex walk goes through <see cref="RegexWalk"/>.</summary>
		[Fact]
		public void NoMatchCollections_InTheCode()
		{
			var root = Tests.Text.DocumentKeys.Root();
			var collecting = new Regex(@"\bRegex\.Matches\(|(?<!acronyms|SnapshotFolder|\br)\.Matches\(|\bMatchCollection\b");
			var wrong = new[] { "CrawlerSuite.Core", "CrawlerSuite" }
				.SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
				.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
					&& !f.EndsWith("RegexWalk.cs", StringComparison.Ordinal))
				.SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
				.Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && collecting.IsMatch(x.line))
				.Select(x => $"{Path.GetRelativePath(root, x.f)}:{x.i + 1}")
				.ToList();

			Assert.True(wrong.Count == 0, string.Join("\n", wrong));
		}

		[Fact]
		public void TheLastOneWins_WithoutCollecting()
		{
			const string text = "trailer << /Root 3 0 R >> update trailer << /Root 7 0 R >>";
			Assert.Equal(7, PdfObjects.TrailerReference(text, "/Root"));
			Assert.Equal("7", RegexWalk.Each(text, @"/Root\s+(\d+)").LastOrDefault()!.Groups[1].Value);
			Assert.Null(RegexWalk.Each(text, @"/Info\s+(\d+)").LastOrDefault());
		}

		[Fact]
		public void TheObjectScan_IsTheSameWalked()
		{
			const string text = "1 0 obj <</A 1>> endobj 2 0 obj 3 0 obj <</B 2>> endobj";
			Assert.Equal(["<</A 1>>", "", "<</B 2>>"], PdfObjects.Bodies(text).Select(b => text[b.From..b.End].Trim()));
			Assert.Equal((text.IndexOf("3 0 obj", StringComparison.Ordinal), "<</B 2>>"), PdfObjects.Find(text, 3) is { } f ? (f.Start, f.Body.Trim()) : default);
		}

		// ── A quarantined asset's sample is found ──────────────────────────────

		[Fact]
		public void TheSampleOfAQuarantinedAsset_IsFound()
		{
			var dir = Path.Combine(Path.GetTempPath(), "sr23-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				const string url = "https://site.test/media/ice.iso";
				var assetSample = OversizedFiles.SampleName(CrawlerSuite.Core.Urls.Naming.GenerateAssetFileName(url));
				Assert.EndsWith(".asset.sample", assetSample);

				Assert.Equal(OversizedFiles.SampleNameForUrl(url), OversizedFiles.SampleNameForUrl(url, dir)); // nothing there: the page's
				File.WriteAllBytes(Path.Combine(dir, assetSample), [1, 2, 3]);
				Assert.Equal(assetSample, OversizedFiles.SampleNameForUrl(url, dir));
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}

		// ── An address no website has is a warning, first record or not ────────

		[Fact]
		public void AnAddressNoWebsiteHas_IsNamed_EvenForAHostSeenTheFirstTime()
		{
			var snapshot = new DnsRecord.Snapshot("2026-10-28-10-00-00", false,
				[
					new("www.site.test", ["203.0.113.4"], null),
					new("help.site.test", ["127.0.0.1"], null),
					new("intra.site.test", ["10.0.0.8"], null),     // private: an intranet site, no warning
					new("cdn.site.test", ["169.254.1.1", "0.0.0.0"], null),
				], []);

			Assert.True(new DnsHistory().IsEmpty);
			Assert.Equal(
				["cdn.site.test: 0.0.0.0 (this host) — no website's address", "cdn.site.test: 169.254.1.1 (link-local) — no website's address", "help.site.test: 127.0.0.1 (loopback) — no website's address"],
				DnsHistory.NeverAWebsite(snapshot).Select(DnsHistory.NeverAWebsiteText));
		}
	}
}
