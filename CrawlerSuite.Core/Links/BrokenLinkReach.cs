namespace CrawlerSuite.Core.Links
{
	/// <summary>
	/// Who runs into a broken reference — the basis for deciding whether to ticket it. Read from the
	/// reference's kind and grade in log 07; the order is from most to least serious.
	/// </summary>
	public static class BrokenLinkReach
	{
		public const string PageView = "Every page view";
		/// <summary>A visitor opens it: clicks, taps or uses the keyboard.</summary>
		public const string Click = "When opened";

		/// <summary>The former name of <see cref="Click"/>, still found in older logs 07 and saved switch settings.</summary>
		public const string ClickBefore = "When a visitor clicks";

		/// <summary>A reach as the app names it now: an older log's or setting's name read as today's.</summary>
		public static string Current(string reach) => reach == ClickBefore ? Click : reach;
		public const string Script = "Only if a script acts";
		/// <summary>A stylesheet reference whose rule the app cannot judge (graded "undecided"): it may well be requested; the evidence says why it cannot tell.</summary>
		public const string Undecided = "Reach undecided";
		public const string Never = "Never requested";
		public const string Elsewhere = "Error elsewhere on site";
		public const string Unknown = "Origin unknown";

		/// <summary>The reaches, most serious first.</summary>
		public static IReadOnlyList<string> All { get; } = [PageView, Click, Script, Undecided, Never, Elsewhere, Unknown];

		/// <summary>The position in <see cref="All"/>; an unknown name sorts last.</summary>
		public static int Rank(string reach)
		{
			for (int i = 0; i < All.Count; i++)
			{
				if (All[i] == reach)
				{
					return i;
				}
			}

			return All.Count;
		}

		// <link rel> values a browser requests while loading the page.
		private static readonly HashSet<string> FetchedRels = new(StringComparer.OrdinalIgnoreCase)
		{
			"stylesheet", "icon", "shortcut", "apple-touch-icon", "apple-touch-icon-precomposed", "mask-icon",
			"preload", "modulepreload", "prefetch", "manifest",
		};

		/// <summary>Whether a browser requests the target of a <c>&lt;link rel="…"&gt;</c> while loading the page.</summary>
		public static bool IsFetchedRel(string rel) =>
			rel.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Any(FetchedRels.Contains);

		/// <summary>
		/// Whether search engines read the target of a <c>&lt;link rel="…"&gt;</c> a browser does not
		/// request: an alternate version of the page (another language, a feed). A 404 there is an
		/// error elsewhere on the site, not clutter.
		/// </summary>
		public static bool IsReadByCrawlers(string rel) =>
			rel.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Any(r => r.Equals("alternate", StringComparison.OrdinalIgnoreCase));

		/// <summary>
		/// The reach of one reference: its kind and grade as in log 07; <paramref name="rel"/> is the
		/// rel of a link element (empty for other kinds).
		/// </summary>
		public static string Of(string kind, string grade, string rel = "") => kind switch
		{
			"script" or "image" => PageView,
			"link element" => IsFetchedRel(rel) ? PageView : IsReadByCrawlers(rel) ? Elsewhere : Never,
			"link" or "form action" or "redirect" => Click,
			"data attribute" or "inline data" => Script,
			"stylesheet url()" => grade switch
			{
				BrokenLinkOrigins.OnLoad or BrokenLinkOrigins.OnDevices => PageView,
				BrokenLinkOrigins.InState or BrokenLinkOrigins.WhenPrinted => Click,
				BrokenLinkOrigins.WhenScriptRuns => Script,
				BrokenLinkOrigins.Dead => Never,
				_ => Undecided,
			},
			"canonical" or "content list" => Elsewhere,
			_ => Unknown,
		};

		/// <summary>
		/// What to do in a browser to see the 404 for oneself, by reach; <paramref name="kind"/> (as in
		/// log 07) names where an error elsewhere on the site is written.
		/// </summary>
		public static string BrowserCheck(string reach, string address, string kind = "",
			Text.DocumentLanguage language = Text.DocumentLanguage.English)
		{
			var name = FileNameOf(address);
			string T(string english) => Text.DocumentText.T(language, english);
			string F(string format) => Text.DocumentText.F(language, format, name);
			return reach switch
			{
				PageView => F("Open the page with the browser's developer tools › Network, filtered to \"{0}\": it answers 404 while the page loads (on a device the evidence names, where it names one)."),
				Click => F("Open the link (or do what the evidence names) with Network filtered to \"{0}\": it answers 404."),
				Script => F("Use the page the way that makes its script act (open the part, choose the option), with Network filtered to \"{0}\". No request means the script did not reach that code here."),
				Undecided => F("The app cannot tell when this is requested (the evidence says why): open the page with Network filtered to \"{0}\" and use it normally."),
				Never => T("Nothing on the crawled pages requests it. Whether it is still needed — for example by a part of the site the crawl did not see — is for you to decide."),
				Elsewhere => kind switch
				{
					"canonical" => T("Fix it where it is written: the page's canonical (SEO › Canonical issues)."),
					"content list" => T("Fix it where it is written: the entry of the CMS content list."),
					"link element" => T("Fix it where it is written: the page's <link rel=\"alternate\"> — search engines read it, visitors do not."),
					_ => T("Fix it where it is written: the evidence names the place."),
				},
				_ => T("The crawl asked for it, but nothing saved points to it: look in the sitemap, or for a script that builds the address."),
			};
		}

		private static string FileNameOf(string address)
		{
			var path = Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.AbsolutePath : address;
			var name = path.TrimEnd('/');
			int slash = name.LastIndexOf('/');
			return slash >= 0 && slash < name.Length - 1 ? name[(slash + 1)..] : address;
		}
	}
}
