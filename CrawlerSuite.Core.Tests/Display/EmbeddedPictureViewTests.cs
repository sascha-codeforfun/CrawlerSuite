using System.IO;
using Xunit;

namespace CrawlerSuite.Core.Tests
{
	/// <summary>An embedded picture in the File tab: fitted to the list, opened whole in a window, saved as the source holds it.</summary>
	public class EmbeddedPictureViewTests
	{
		private static string App(params string[] parts) => File.ReadAllText(Path.Combine([Text.DocumentKeys.Root(), "CrawlerSuite", .. parts]));

		[Fact]
		public void The_picture_in_a_row_is_kept_within_the_list_and_opens_on_a_click()
		{
			var view = App("Views", "ComparisonView.xaml");
			Assert.Contains("RelativeSource={RelativeSource AncestorType=ListBox}, Converter={x:Static views:LessBy.Instance}", view);
			Assert.Contains(@"StretchDirection=""DownOnly""", view);
			Assert.Contains(@"Click=""OnOpenPicture""", view);
		}

		[Fact]
		public void Saving_writes_the_embedded_bytes_unchanged()
		{
			var window = App("Views", "PictureWindow.cs");
			Assert.Contains("File.WriteAllBytes(dialog.FileName, bytes);", window);
			Assert.DoesNotContain("BitmapEncoder", window);
		}
	}
}
