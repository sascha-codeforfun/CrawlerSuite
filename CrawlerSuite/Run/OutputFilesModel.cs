using System.Collections.Generic;
using System.Linq;
using CrawlerSuite.Core.Formats;

namespace CrawlerSuite.Run;

/// <summary>The Output files page: every output format, one selected.</summary>
public sealed class OutputFilesModel : Observable
{
	private OutputFormat? selected;

	public OutputFilesModel()
	{
		Formats = OutputFormats.All.ToList();
		selected = Formats.FirstOrDefault();
	}

	public IReadOnlyList<OutputFormat> Formats { get; }

	public OutputFormat? Selected { get => selected; set => Set(ref selected, value); }
}
