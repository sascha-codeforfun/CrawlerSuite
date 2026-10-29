using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>The four groups of the triage filter.</summary>
	public class PageDecisionTests
	{
		[Theory]
		[InlineData(PageStatus.Open, false, PageDecision.Open)]
		[InlineData(PageStatus.PartlyDecided, false, PageDecision.Edited)]
		[InlineData(PageStatus.ReadyToFile, false, PageDecision.Edited)]
		[InlineData(PageStatus.Filed, true, PageDecision.Ticketed)]
		[InlineData(PageStatus.Reopened, true, PageDecision.Changed)]
		[InlineData(PageStatus.ReadyToFile, true, PageDecision.Changed)] // new findings decided, not ticketed again yet
		public void DecisionOf_GroupsTheStatuses(PageStatus status, bool filedBefore, PageDecision expected)
		{
			Assert.Equal(expected, PageSummary.DecisionOf(status, filedBefore));
		}
	}
}
