using CrawlerSuite.Core.Links;
using CrawlerSuite.Core.Text;
using CrawlerSuite.Core.Triage;
using Xunit;

namespace CrawlerSuite.Core.Tests.Triage
{
	/// <summary>
	/// The reach "When opened" (a tap or the keyboard opens too) also reads its older name
	/// "When a visitor clicks" from logs and saved switch settings; German says "Bei Aufruf".
	/// </summary>
	public class ReachRenameTests
	{
		[Fact]
		public void TheOldName_IsReadAsTheNew()
		{
			Assert.Equal("When opened", BrokenLinkReach.Click);
			Assert.Equal(BrokenLinkReach.Click, BrokenLinkReach.Current("When a visitor clicks"));
			Assert.Equal(BrokenLinkReach.PageView, BrokenLinkReach.Current(BrokenLinkReach.PageView));
		}

		[Fact]
		public void ASavedSwitch_UnderTheOldName_KeepsItsMode()
		{
			var modes = TriageSwitches.ModesFrom(new Dictionary<string, string>
			{
				[BrokenLinkSource.SwitchIdOf("When a visitor clicks")] = "Off",
			});
			Assert.Equal(TriageMode.Off, modes[BrokenLinkSource.SwitchIdOf(BrokenLinkReach.Click)]);
			Assert.DoesNotContain(BrokenLinkSource.SwitchIdOf("When a visitor clicks"), modes.Keys);
		}

		[Fact]
		public void TheReaches_InGerman()
		{
			Assert.Equal("Beim Laden der Seite", DocumentText.T(DocumentLanguage.German, BrokenLinkReach.PageView));
			Assert.Equal("Bei Aufruf", DocumentText.T(DocumentLanguage.German, BrokenLinkReach.Click));
			Assert.Equal("Nur per Skript", DocumentText.T(DocumentLanguage.German, BrokenLinkReach.Script));
		}
	}
}
