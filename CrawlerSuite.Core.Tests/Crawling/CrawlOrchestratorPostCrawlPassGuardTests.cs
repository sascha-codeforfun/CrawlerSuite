using Xunit;

namespace CrawlerSuite.Core.Tests.Crawling
{
	/// <summary>
	/// Truth-table tests for <see cref="CrawlOrchestrator.ShouldSkipPostCrawlPass"/>.
	///
	/// The guard combines a nullable flag with two other conditions, and the
	/// obvious inline form is a precedence trap:
	///     if (!config.CmsContentList?.PostCrawlPass ?? false
	///         || ctx.IsDebugSession
	///         || !File.Exists(ctx.ContentCrawlCompareLogFile))
	///         return;
	/// LOOKS like it skips on any of three conditions, but C#
	/// precedence puts `||` higher than `??`, so it parses as:
	///     (!config.CmsContentList?.PostCrawlPass)
	///         ?? (false || ctx.IsDebugSession || !File.Exists(...))
	/// When PostCrawlPass=true, the left side evaluates to (bool?)false —
	/// NOT null — so `??` short-circuits and the entire right side
	/// (including ctx.IsDebugSession) is DEAD CODE: the post-crawl pass would
	/// download pages during a debug replay, where the operator has
	/// forbidden network traffic.
	///
	/// These tests pin the truth table so that trap cannot go unnoticed.
	/// </summary>
	public class CrawlOrchestratorPostCrawlPassGuardTests
	{
		// ── Fixture helpers ──────────────────────────────────────────────

		private static Config ConfigWithPostCrawlPass(bool postCrawlPassEnabled)
			=> new()
			{
				CmsContentList = new CmsContentListConfig
				{
					PostCrawlPass = postCrawlPassEnabled,
				},
			};

		private static Config ConfigWithoutCmsContentList()
			=> new() { CmsContentList = null };

		private static CrawlerRunContext Ctx(bool isDebugSession)
			=> new() { IsDebugSession = isDebugSession };

		// ── Debug replay with the pass enabled ────────────────────────────

		[Fact]
		public void OperatorBugScenario_DebugReplayWithPostCrawlPassEnabled_Skips()
		{
			// A replay must skip the pass even with PostCrawlPass on. With the inline
			// `?? false || IsDebugSession` form the IsDebugSession check would be
			// dead code and the pass would download pages.
			var config = ConfigWithPostCrawlPass(postCrawlPassEnabled: true);
			var ctx = Ctx(isDebugSession: true);

			Assert.True(CrawlOrchestrator.ShouldSkipPostCrawlPass(
				ctx, config, compareLogExists: true));
		}

		// ── Full truth table ──────────────────────────────────────────────
		//
		// Skip=true is correct for ALL combinations EXCEPT
		// (PostCrawlPass=true, IsDebugSession=false, compareLogExists=true).
		// This single "off-state" row is what actually triggers the
		// download; all other rows are guarded paths.

		[Theory]
		// (postCrawlPassEnabled, isDebugSession, compareLogExists, expectedSkip)
		[InlineData(false, false, false, true)]
		[InlineData(false, false, true, true)]
		[InlineData(false, true, false, true)]
		[InlineData(false, true, true, true)]
		[InlineData(true, false, false, true)]
		[InlineData(true, false, true, false)]  // the only "run" row
		[InlineData(true, true, false, true)]
		[InlineData(true, true, true, true)]   // debug replay with the pass enabled
		public void TruthTable(
			bool postCrawlPassEnabled,
			bool isDebugSession,
			bool compareLogExists,
			bool expectedSkip)
		{
			var config = ConfigWithPostCrawlPass(postCrawlPassEnabled);
			var ctx = Ctx(isDebugSession);

			Assert.Equal(expectedSkip,
				CrawlOrchestrator.ShouldSkipPostCrawlPass(ctx, config, compareLogExists));
		}

		// ── Null CmsContentList ───────────────────────────────────────────

		[Fact]
		public void NullCmsContentList_AlwaysSkips()
		{
			// The feature requires a content list. When the operator hasn't
			// configured one at all, the post-crawl pass has nothing to
			// compare against and must skip. config.CmsContentList?.PostCrawlPass
			// returns null in that case; the `?? false` defaults that to false
			// (feature disabled), and the helper returns skip=true.
			var config = ConfigWithoutCmsContentList();
			var ctx = Ctx(isDebugSession: false);

			Assert.True(CrawlOrchestrator.ShouldSkipPostCrawlPass(
				ctx, config, compareLogExists: true));
		}

		[Fact]
		public void NullCmsContentList_StillSkipsInDebug()
		{
			// Defense in depth: even if somehow CmsContentList becomes null
			// AND IsDebugSession is true, we skip. (Either alone is
			// sufficient; both together obviously so.)
			var config = ConfigWithoutCmsContentList();
			var ctx = Ctx(isDebugSession: true);

			Assert.True(CrawlOrchestrator.ShouldSkipPostCrawlPass(
				ctx, config, compareLogExists: true));
		}

		// ── The "actually run" case ────────────────────────────────────────

		[Fact]
		public void NormalCrawl_PostCrawlPassEnabled_CompareLogExists_DoesNotSkip()
		{
			// The single combination where the post-crawl pass SHOULD run:
			// fresh crawl (not debug), feature enabled, compare log was
			// produced by Step_WriteContentListLogs.
			var config = ConfigWithPostCrawlPass(postCrawlPassEnabled: true);
			var ctx = Ctx(isDebugSession: false);

			Assert.False(CrawlOrchestrator.ShouldSkipPostCrawlPass(
				ctx, config, compareLogExists: true));
		}
	}
}
