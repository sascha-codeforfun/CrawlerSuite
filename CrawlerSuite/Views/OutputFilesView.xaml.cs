using System.Windows.Controls;
using System.Windows.Data;
using CrawlerSuite.Core.Formats;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>The Output files page: the format registry, readable.</summary>
public partial class OutputFilesView : UserControl
{
	public OutputFilesView()
	{
		InitializeComponent();
		var model = new OutputFilesModel();
		DataContext = model;
		var view = CollectionViewSource.GetDefaultView(model.Formats);
		view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OutputFormat.Group)));
	}
}
