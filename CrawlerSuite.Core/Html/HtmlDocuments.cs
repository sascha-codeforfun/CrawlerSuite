namespace CrawlerSuite.Core.Html
{
	using HtmlAgilityPack;

	/// <summary>
	/// The one place HTML documents are created, so every parse shares the same safety
	/// settings. A page nested deeper than <see cref="MaxNestedChildNodes"/> levels (only a
	/// broken or hostile page gets near it) makes the parse throw instead of building a tree
	/// so deep that walking it could exhaust the stack; callers treat that like any other
	/// unreadable page.
	/// </summary>
	internal static class HtmlDocuments
	{
		internal const int MaxNestedChildNodes = 2000;

		/// <summary>A document with the standard settings; every one is loaded once, so this is where parses are counted.</summary>
		internal static HtmlDocument Create()
		{
			CrawlerSuite.Core.Host.WorkCounters.Parsed();
			return new() { OptionMaxNestedChildNodes = MaxNestedChildNodes };
		}

		/// <summary>A document with the standard settings, loaded from <paramref name="html"/>.</summary>
		internal static HtmlDocument Parse(string html)
		{
			var doc = Create();
			doc.LoadHtml(html);
			return doc;
		}
	}
}
