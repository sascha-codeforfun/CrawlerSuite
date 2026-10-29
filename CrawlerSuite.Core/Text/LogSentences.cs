using System.Text.RegularExpressions;

namespace CrawlerSuite.Core.Text
{
	/// <summary>
	/// Sentences other steps write into their logs and a ticket shows — the evidence of a broken or
	/// used reference (logs 07 and 38), an image's finding (log 25), a canonical issue's detail
	/// (log 16) — in the document's language. The logs keep the English: other steps and the app's
	/// screens read them. A German document reads each sentence through one pattern per format a step
	/// writes; what the pattern captures — addresses, paths, folders, selectors, media and container
	/// conditions, class names, script addresses, host names, status names, numbers — stays as found.
	/// A sentence no pattern knows stays English, as does every sentence in an English document.
	/// </summary>
	public static class LogSentences
	{
		// Plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant;

		private static Regex R(string pattern) => new(pattern, Options);

		// ── Evidence (logs 07 and 38: BrokenLinkOrigins) ─────────────────────

		/// <summary>Added to any stylesheet's evidence when the same file is deployed at other addresses.</summary>
		private static readonly Regex Twins = R(@"^(?<body>.+) The same stylesheet is also at (?<list>.+)\.$");

		/// <summary>Closes a graded stylesheet's evidence: on how many pages the rule was tried.</summary>
		private static readonly Regex CheckedOn = R(@"^(?<head>.+)\. (?:(?<none>No saved page loads the stylesheet directly\.)|Checked on (?<n>\d+) page\(s\) that load it\.)$");

		/// <summary>Whole sentences: nothing follows them but, for a stylesheet, its twins.</summary>
		private static readonly (Regex English, Func<Match, string> German)[] EvidenceSentences =
		[
			(R(@"^Written without a leading '/': the browser puts the page's folder (?<folder>.+?) in front\. Written as /(?<h>.+?) it leads to a page of the site; it works as written only on a page at the top level\.$"),
				m => $"Ohne führenden „/“ geschrieben: Der Browser setzt den Ordner der Seite {m.Groups["folder"].Value} davor. Als /{m.Groups["h"].Value} geschrieben führt es auf eine Seite der Website; so wie geschrieben funktioniert es nur auf einer Seite der obersten Ebene."),
			(R(@"^The page's canonical points here — fix the canonical \(SEO › Canonical issues\)\.$"),
				_ => "Das Canonical der Seite zeigt hierher – das Canonical korrigieren (SEO › Canonical issues)."),
			(R(@"^Not referenced by a saved page or stylesheet, a redirect or the content list — e\.g\. a script builds it, or the page that links it was not saved\.$"),
				_ => "Weder eine gespeicherte Seite noch ein Stylesheet, eine Weiterleitung oder die Inhaltsliste verweist darauf – etwa weil ein Skript die Adresse zusammensetzt oder die verlinkende Seite nicht gespeichert wurde."),
			(R(@"^Not referenced by a saved page or stylesheet — e\.g\. a script builds the address, or the page that uses it was not saved\.$"),
				_ => "Weder eine gespeicherte Seite noch ein Stylesheet verweist darauf – etwa weil ein Skript die Adresse zusammensetzt oder die verwendende Seite nicht gespeichert wurde."),
			(R(@"^The stylesheet's address cannot be read\.$"),
				_ => "Die Adresse des Stylesheets lässt sich nicht lesen."),
			(R(@"^In a rule whose selector the app cannot read: (?<sel>.+)\.$"),
				m => $"In einer Regel, deren Selektor die App nicht lesen kann: {m.Groups["sel"].Value}."),
			(R(@"^Not in a style rule \(e\.g\. @font-face or a nested rule\): loaded when that font or rule is used\.$"),
				_ => "Nicht in einer Stilregel (etwa @font-face oder eine verschachtelte Regel): wird geladen, wenn diese Schrift oder Regel verwendet wird."),
		];

		/// <summary>The head of a graded stylesheet's evidence, before "Checked on …" (no closing full stop).</summary>
		private static readonly (Regex English, Func<Match, string> German)[] EvidenceHeads =
		[
			(R(@"^(?<sel>.+?) applies on (?<url>\S+) under (?<cond>.+): requested on load on every device that matches$"),
				m => $"{m.Groups["sel"].Value} greift auf {m.Groups["url"].Value} unter {m.Groups["cond"].Value}: wird beim Laden auf jedem passenden Gerät angefordert"),
			(R(@"^(?<sel>.+?) applies on (?<url>\S+) only in a state \((?<states>.*?)(?:, under (?<cond>.+))?\)$"),
				m => $"{m.Groups["sel"].Value} greift auf {m.Groups["url"].Value} nur in einem Zustand ({States(m.Groups["states"].Value)}" +
					(m.Groups["cond"].Success ? $", unter {m.Groups["cond"].Value}" : string.Empty) + ")"),
			(R(@"^(?<sel>.+?) applies on (?<url>\S+) only when printed \((?<cond>.+)\)$"),
				m => $"{m.Groups["sel"].Value} greift auf {m.Groups["url"].Value} nur beim Drucken ({m.Groups["cond"].Value})"),
			(R(@"^(?<part>.+?) does not apply on load; \.(?<name>\S+) is named in an inline script on (?<url>\S+)$"),
				m => $"{m.Groups["part"].Value} greift nicht beim Laden; .{m.Groups["name"].Value} wird in einem Inline-Skript auf {m.Groups["url"].Value} genannt"),
			(R(@"^(?<part>.+?) does not apply on load; \.(?<name>\S+) is named in (?<where>.+)$"),
				m => $"{m.Groups["part"].Value} greift nicht beim Laden; .{m.Groups["name"].Value} wird in {m.Groups["where"].Value} genannt"),
			(R(@"^(?<sel>.+): its classes are on the pages, but no element matches on load$"),
				m => $"{m.Groups["sel"].Value}: Die Klassen stehen auf den Seiten, aber beim Laden passt kein Element"),
			(R(@"^(?<part>.+?) needs \.(?<name>\S+), which no crawled page and no script names$"),
				m => $"{m.Groups["part"].Value} braucht .{m.Groups["name"].Value}, das weder eine gecrawlte Seite noch ein Skript nennt"),
		];

		/// <summary>A rule an element of the page matches on load (no "Checked on"): tried after the heads, which say more.</summary>
		private static readonly Regex OnLoad = R(@"^(?<sel>.+?) applies on (?<url>\S+)\.$");

		/// <summary>The evidence of a reference (log 07, Evidence; log 38, Evidence) in the document's language.</summary>
		public static string Evidence(DocumentLanguage language, string evidence)
		{
			if (language != DocumentLanguage.German || string.IsNullOrEmpty(evidence))
			{
				return evidence;
			}

			var body = evidence;
			var twins = string.Empty;
			if (Twins.Match(evidence) is { Success: true } t)
			{
				body = t.Groups["body"].Value;
				twins = $" Dasselbe Stylesheet liegt auch unter {t.Groups["list"].Value}.";
			}

			var german = EvidenceBody(body);
			return german is null ? evidence : german + twins;
		}

		private static string? EvidenceBody(string body)
		{
			foreach (var (english, german) in EvidenceSentences)
			{
				if (english.Match(body) is { Success: true } m)
				{
					return german(m);
				}
			}

			if (CheckedOn.Match(body) is { Success: true } c)
			{
				var checkedOn = c.Groups["none"].Success
					? "Keine gespeicherte Seite lädt das Stylesheet direkt."
					: $"Geprüft auf {c.Groups["n"].Value} Seite(n), die es laden.";
				foreach (var (english, german) in EvidenceHeads)
				{
					if (english.Match(c.Groups["head"].Value) is { Success: true } m)
					{
						return $"{german(m)}. {checkedOn}";
					}
				}

				return null;
			}

			return OnLoad.Match(body) is { Success: true } o
				? $"{o.Groups["sel"].Value} greift auf {o.Groups["url"].Value}."
				: null;
		}

		/// <summary>The states a selector needs (":hover, :focus"), or the app's words when it names none.</summary>
		private static string States(string states) =>
			states == "named in the selector" ? "im Selektor genannt" : states;

		// ── Image findings (log 25: AssetQuality, AssetMetadataReportRenderer) ──

		/// <summary>What a metadata finding names as present.</summary>
		private static readonly Dictionary<string, string> MetadataFlags = new(StringComparer.Ordinal)
		{
			["GPS"] = "GPS",
			["copyright"] = "Copyright",
			["camera"] = "Kamera",
			["device-id"] = "Geräte-ID",
			["author"] = "Urheber",
			["software"] = "Software",
		};

		private static readonly (Regex English, Func<Match, string> German)[] AssetSentences =
		[
			(R(@"^declared Content-Length (?<d>\d+) != actual (?<a>\d+) bytes \(truncated or corrupt download\)$"),
				m => $"angegebene Content-Length {m.Groups["d"].Value} ≠ tatsächlich {m.Groups["a"].Value} Byte (Download abgeschnitten oder Datei beschädigt)"),
			(R(@"^(?<n>\d+) bytes exceeds threshold (?<t>\d+)$"),
				m => $"{m.Groups["n"].Value} Byte, mehr als die Grenze von {m.Groups["t"].Value}"),
			(R(@"^(?<w>\d+)x(?<h>\d+) px \(tracking-pixel or degenerate image\)$"),
				m => $"{m.Groups["w"].Value}x{m.Groups["h"].Value} px (Zählpixel oder Bild ohne sichtbaren Inhalt)"),
			(R(@"^(?<w>\d+)x(?<h>\d+) px exceeds (?<m>\d+) px on a side$"),
				m => $"{m.Groups["w"].Value}x{m.Groups["h"].Value} px, mehr als {m.Groups["m"].Value} px auf einer Seite"),
			(R(@"^metadata present \((?<n>\d+) fields?\)$"),
				m => $"Metadaten vorhanden ({Fields(m.Groups["n"].Value)})"),
			(R(@"^(?<flags>(?:GPS|copyright|camera|device-id|author|software)(?:, (?:GPS|copyright|camera|device-id|author|software))*) present \((?<n>\d+) fields?\)$"),
				m => $"{string.Join(", ", m.Groups["flags"].Value.Split(", ").Select(f => MetadataFlags[f]))} vorhanden ({Fields(m.Groups["n"].Value)})"),
		];

		private static string Fields(string n) => n == "1" ? "1 Feld" : $"{n} Felder";

		/// <summary>An image finding's sentence (log 25, Context) in the document's language.</summary>
		public static string AssetFinding(DocumentLanguage language, string text) => ByPattern(language, text, AssetSentences);

		// ── Canonical issues (log 16: CanonicalAnalyzer) ─────────────────────

		/// <summary>The search values of log 08 a canonical issue names.</summary>
		private static string Search(string value) => value switch
		{
			"noindex in page" => "noindex in der Seite",
			"noindex by header" => "noindex per Header",
			_ => value,
		};

		private static readonly (Regex English, Func<Match, string> German)[] CanonicalSentences =
		[
			(R(@"^(?<n>\d+) canonical tags found — only first honoured: (?<list>.+)$"),
				m => $"{m.Groups["n"].Value} Canonical-Tags gefunden – nur das erste gilt: {m.Groups["list"].Value}"),
			(R(@"^Canonical points outside allowed crawl scope \((?<hosts>.*)\) to: (?<host>.*)$"),
				m => $"Das Canonical zeigt außerhalb des erlaubten Crawl-Bereichs ({m.Groups["hosts"].Value}) auf: {m.Groups["host"].Value}"),
			(R(@"^Canonical target redirects \((?<status>[^()]*)\) to: (?<target>.*)$"),
				m => $"Das Canonical-Ziel leitet weiter ({m.Groups["status"].Value}) auf: {m.Groups["target"].Value}"),
			(R(@"^Canonical target not found in crawler index: (?<url>.*)$"),
				m => $"Das Canonical-Ziel ist nicht im Crawler-Index: {m.Groups["url"].Value}"),
			(R(@"^Canonical target is kept out of search \((?<search>[^()]*)\): (?<url>.*)$"),
				m => $"Das Canonical-Ziel ist von der Suche ausgeschlossen ({Search(m.Groups["search"].Value)}): {m.Groups["url"].Value}"),
			(R(@"^Chain: (?<chain>.*)$"),
				m => $"Kette: {m.Groups["chain"].Value}"),
		];

		/// <summary>A canonical issue's detail (log 16, Detail) in the document's language.</summary>
		public static string CanonicalDetail(DocumentLanguage language, string detail) => ByPattern(language, detail, CanonicalSentences);

		// ── ─────────────────────────────────────────────────────────────────

		/// <summary>How many formats are known, per method: for the tests.</summary>
		internal static (int Evidence, int Asset, int Canonical) Count =>
			(EvidenceSentences.Length + EvidenceHeads.Length + 1, AssetSentences.Length, CanonicalSentences.Length);

		private static string ByPattern(DocumentLanguage language, string text, (Regex English, Func<Match, string> German)[] rules)
		{
			if (language != DocumentLanguage.German || string.IsNullOrEmpty(text))
			{
				return text;
			}

			foreach (var (english, german) in rules)
			{
				if (english.Match(text) is { Success: true } m)
				{
					return german(m);
				}
			}

			return text;
		}
	}
}
