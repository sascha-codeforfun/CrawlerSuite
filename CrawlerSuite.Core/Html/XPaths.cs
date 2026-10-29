namespace CrawlerSuite.Core.Html
{
	/// <summary>
	/// XPath expressions shared by the analyzers. HTML attribute values like rel="Canonical"
	/// or name="Description" are case-insensitive, and rel may list several tokens; XPath 1.0
	/// compares case-sensitively, so the value is lower-cased with translate() first.
	/// </summary>
	internal static class XPaths
	{
		private const string Lower = "'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'";

		/// <summary>Every &lt;link&gt; whose rel contains the token "canonical" and that has an href.</summary>
		internal const string CanonicalLinks =
			"//link[contains(concat(' ',normalize-space(translate(@rel," + Lower + ")),' '),' canonical ')][@href]";

		/// <summary>The first &lt;meta&gt; whose name equals <paramref name="name"/> (lower-case), ignoring case.</summary>
		internal static string MetaByName(string name) =>
			"//meta[translate(@name," + Lower + ")='" + name + "']";
	}
}
