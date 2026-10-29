namespace CrawlerSuite.Core.Quality
{
	internal record QualityIssue(
		string Filename,
		string IssueType,
		string Detail,
		string Context)
	{
		/// <summary>The unwanted-pattern group a finding comes from (its Name); null for every other finding. Counts only, not written.</summary>
		public string? Group { get; init; }
	}
}
