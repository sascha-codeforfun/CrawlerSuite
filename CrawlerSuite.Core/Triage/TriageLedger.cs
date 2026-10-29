using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>The verdict on one finding of a page.</summary>
	public enum Verdict
	{
		/// <summary>Goes into the page's ticket.</summary>
		Ticket,

		/// <summary>Not fixed — on this page only.</summary>
		Wontfix,

		/// <summary>An intentional language choice.</summary>
		Locale,
	}

	/// <summary>A decided finding. A draft until its page is filed.</summary>
	public sealed class LedgerFinding
	{
		public string Key { get; set; } = string.Empty;
		public string Page { get; set; } = string.Empty;
		public string Type { get; set; } = string.Empty;
		public Verdict Verdict { get; set; }
		public string Comment { get; set; } = string.Empty;

		/// <summary>False while the page has not been filed with this verdict (a draft).</summary>
		public bool Filed { get; set; }

		/// <summary>The day it was decided — a date, never a time of day (see <see cref="LedgerDate"/>).</summary>
		public DateOnly Decided { get; set; }

		/// <summary>The day it was filed; null while a draft.</summary>
		public DateOnly? FiledAt { get; set; }

		/// <summary>The finding's detail when it was decided, for reading the ledger by hand.</summary>
		public string Detail { get; set; } = string.Empty;
	}

	/// <summary>One filing of a page: what went into it, and under which ticket.</summary>
	public sealed class LedgerFiling
	{
		/// <summary>The day of the filing.</summary>
		public DateOnly At { get; set; }

		/// <summary>"new" (a new ticket), "addendum" (added to <see cref="Ticket"/>) or "none" (nothing to ticket).</summary>
		public string Mode { get; set; } = "new";

		public string Ticket { get; set; } = string.Empty;
		public List<string> TicketKeys { get; set; } = [];
	}

	/// <summary>A page that has been worked on: its ticket numbers and filings.</summary>
	public sealed class LedgerPage
	{
		public string Url { get; set; } = string.Empty;

		/// <summary>The upstream ticket numbers, typed in by hand once the tickets exist.</summary>
		public List<string> Tickets { get; set; } = [];

		public List<LedgerFiling> Filings { get; set; } = [];
	}

	/// <summary>
	/// The triage ledger of a site: one JSON file next to its snapshots (never in the user
	/// profile). Every verdict is saved as it is clicked (a draft) and becomes filed with its
	/// page. Matched against each newer snapshot: what is gone is gone — verdicts of findings no
	/// longer found are removed, drafts included.
	/// </summary>
	public sealed class TriageLedger
	{
		public const string FileName = "triage-ledger.json";

		private static readonly JsonSerializerOptions Json = new()
		{
			WriteIndented = true,
			Converters = { new JsonStringEnumConverter(), new LedgerDate() },
		};

		public int Version { get; set; } = 1;

		/// <summary>The newest snapshot the ledger was matched against (folder name; they sort by time).</summary>
		public string Snapshot { get; set; } = string.Empty;

		public Dictionary<string, LedgerFinding> Findings { get; set; } = new(StringComparer.Ordinal);

		public Dictionary<string, LedgerPage> Pages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Where a snapshot's ledger is: in the site folder that holds the snapshot.</summary>
		public static string PathFor(string snapshotDirectory) =>
			Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(snapshotDirectory)) ?? snapshotDirectory, FileName);

		/// <summary>Loads a ledger; a missing file is a new, empty ledger. Throws when the file cannot be read or parsed.</summary>
		public static TriageLedger Load(string path)
		{
			if (!File.Exists(path))
			{
				return new TriageLedger();
			}

			string text;
			using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			using (var reader = new StreamReader(stream, Encoding.UTF8))
			{
				text = reader.ReadToEnd();
			}

			var ledger = JsonSerializer.Deserialize<TriageLedger>(text, Json) ?? new TriageLedger();

			// Comparers are not serialised.
			ledger.Findings = new Dictionary<string, LedgerFinding>(ledger.Findings, StringComparer.Ordinal);
			ledger.Pages = new Dictionary<string, LedgerPage>(ledger.Pages, StringComparer.OrdinalIgnoreCase);

			// A ledger that still holds times of day is saved once straight away with dates only,
			// so the times leave the disk, not just new entries.
			if (LedgerDate.HasTimeOfDay(text))
			{
				try
				{
					ledger.Save(path);
					Logger.LogInfo("Triage ledger: times of day removed, dates kept.");
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					Logger.LogWarning($"Triage ledger: could not remove the times of day yet — {ex.Message}");
				}
			}

			return ledger;
		}

		/// <summary>How many earlier ledgers <see cref="Backup"/> keeps (".1" newest … ".5" oldest).</summary>
		public const int Backups = 5;

		/// <summary>
		/// Keeps a copy of the ledger file as it is on disk before something is removed from it:
		/// "triage-ledger.json.1" is the newest, older copies move up to ".5". Nothing to do when
		/// there is no file yet.
		/// </summary>
		public static void Backup(string path)
		{
			if (!File.Exists(path))
			{
				return;
			}

			for (int n = Backups - 1; n >= 1; n--)
			{
				var from = $"{path}.{n}";
				if (File.Exists(from))
				{
					File.Move(from, $"{path}.{n + 1}", overwrite: true);
				}
			}

			File.Copy(path, $"{path}.1", overwrite: true);
		}

		/// <summary>Saves through a temporary file, so a failure never leaves half a ledger. Throws IOException when the file is locked.</summary>
		public void Save(string path)
		{
			var temp = path + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
			File.Move(temp, path, overwrite: true);
		}

		/// <summary>
		/// Whether this snapshot may change the ledger: only the newest snapshot (or a newer one)
		/// does; an older snapshot would bring back what has gone since.
		/// </summary>
		public bool AcceptsSnapshot(string snapshotName) =>
			string.CompareOrdinal(snapshotName, Snapshot) >= 0;

		/// <summary>
		/// Matches the ledger against a snapshot's findings: verdicts of findings no longer found
		/// are removed (drafts too), and pages without findings or tickets left are dropped.
		/// Returns how many verdicts went. Only call when <see cref="AcceptsSnapshot"/> is true.
		/// </summary>
		public int Reconcile(string snapshotName, IEnumerable<string> currentKeys)
		{
			var current = currentKeys.ToHashSet(StringComparer.Ordinal);
			var gone = Findings.Keys.Where(k => !current.Contains(k)).ToList();
			foreach (var key in gone)
			{
				Findings.Remove(key);
			}

			var pagesWithFindings = Findings.Values.Select(f => f.Page).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var currentPages = current.Select(PageOfKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var url in Pages.Keys.Where(u => !pagesWithFindings.Contains(u) && !currentPages.Contains(u)).ToList())
			{
				Pages.Remove(url);
			}

			Snapshot = snapshotName;
			return gone.Count;
		}

		/// <summary>The page part of a key ("TYPE|page|detail").</summary>
		internal static string PageOfKey(string key)
		{
			// The detail may hold '|'; type and URL never do.
			int first = key.IndexOf('|');
			int second = first < 0 ? -1 : key.IndexOf('|', first + 1);
			return second < 0 ? string.Empty : key[(first + 1)..second];
		}

		/// <summary>The verdict recorded for an item (all its keys agree), or null when undecided.</summary>
		public LedgerFinding? VerdictOf(TriageItem item)
		{
			LedgerFinding? first = null;
			foreach (var key in item.Keys)
			{
				if (!Findings.TryGetValue(key, out var f))
				{
					return null;
				}

				first ??= f;
			}

			return first;
		}

		/// <summary>True when every key of the item is filed.</summary>
		public bool IsFiled(TriageItem item) => item.Keys.All(k => Findings.TryGetValue(k, out var f) && f.Filed);

		/// <summary>Records a verdict for an item, as a draft (the page needs filing again).</summary>
		public void Decide(TriageItem item, Verdict verdict, string comment, DateTime now)
		{
			foreach (var key in item.Keys)
			{
				Findings[key] = new LedgerFinding
				{
					Key = key,
					Page = item.Page,
					Type = item.Type,
					Verdict = verdict,
					Comment = verdict == Verdict.Wontfix ? comment.Trim() : string.Empty,
					Filed = false,
					Decided = DateOnly.FromDateTime(now),
					Detail = item.Detail,
				};
			}
		}

		/// <summary>Takes an item's verdict back: it is undecided again.</summary>
		public void Undecide(TriageItem item)
		{
			foreach (var key in item.Keys)
			{
				Findings.Remove(key);
			}
		}

		public LedgerPage PageOf(string url)
		{
			if (!Pages.TryGetValue(url, out var page))
			{
				page = new LedgerPage { Url = url };
				Pages[url] = page;
			}

			return page;
		}

		/// <summary>The ticket keys filed for a page before, by any earlier filing.</summary>
		public IReadOnlySet<string> PreviouslyTicketed(string url) =>
			Pages.TryGetValue(url, out var page)
				? page.Filings.SelectMany(f => f.TicketKeys).ToHashSet(StringComparer.Ordinal)
				: new HashSet<string>(StringComparer.Ordinal);

		/// <summary>
		/// Files a page: every verdict of <paramref name="items"/> becomes filed, and the filing is
		/// recorded with the keys that went into the ticket. <paramref name="mode"/> is "new",
		/// "addendum" (with <paramref name="ticket"/>) or "none" when nothing is to be ticketed.
		/// </summary>
		public LedgerFiling FilePage(string url, IReadOnlyList<TriageItem> items, string mode, string ticket, DateTime now)
		{
			var ticketKeys = new List<string>();
			foreach (var item in items)
			{
				foreach (var key in item.Keys)
				{
					if (!Findings.TryGetValue(key, out var f))
					{
						throw new InvalidOperationException($"Cannot file {url}: an item has no verdict.");
					}

					f.Filed = true;
					f.FiledAt = DateOnly.FromDateTime(now);
					if (f.Verdict == Verdict.Ticket)
					{
						ticketKeys.Add(key);
					}
				}
			}

			var filing = new LedgerFiling { At = DateOnly.FromDateTime(now), Mode = mode, Ticket = mode == "addendum" ? ticket : string.Empty, TicketKeys = ticketKeys };
			PageOf(url).Filings.Add(filing);
			return filing;
		}
	}

	/// <summary>
	/// The ledger's dates: written as "yyyy-MM-dd", never with a time of day — a verdict's time would
	/// record how someone works, which the ledger has no use for. Reads the full timestamps of older
	/// ledgers too, keeping their (local) date.
	/// </summary>
	public sealed class LedgerDate : JsonConverter<DateOnly>
	{
		private static readonly System.Text.RegularExpressions.Regex TimeOfDay =
			new(@"""\d{4}-\d{2}-\d{2}T\d{2}:", System.Text.RegularExpressions.RegexOptions.Compiled);

		/// <summary>True when a ledger's text still holds a timestamp with a time of day.</summary>
		public static bool HasTimeOfDay(string json) => TimeOfDay.IsMatch(json);

		public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			var value = reader.GetString() ?? string.Empty;
			if (DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.None, out var date))
			{
				return date;
			}

			if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.RoundtripKind, out var stamp))
			{
				return DateOnly.FromDateTime(stamp.Kind == DateTimeKind.Utc ? stamp.ToLocalTime() : stamp);
			}

			throw new JsonException($"Not a date: {value}");
		}

		public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
			writer.WriteStringValue(value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
	}
}
