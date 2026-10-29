using CrawlerSuite.Core.Text;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Formats;
using CrawlerSuite.Core.Links;
using CrawlerSuite.Core.Security;

namespace CrawlerSuite.Core.Triage
{
	/// <summary>
	/// Image findings (log 25) as triage items, placed where the fix is made (log 38): the one page
	/// or stylesheet that uses the image; the image itself when several use it (they are listed,
	/// each marked shown or linked) or none was found. A size or dimension finding counts only
	/// where the image is shown: an image only offered as a link (a high-resolution download) is
	/// meant to be large. Metadata counts wherever the file can be fetched. Rights, equipment,
	/// program and date fields are never triaged — the Results Images tab lists them.
	/// </summary>
	public static class ImageSource
	{
		public const string Family = "Images";

		public const string Truncated = "IMAGE_TRUNCATED";
		public const string FileOversize = "IMAGE_FILE_OVERSIZE";
		public const string DimensionsOversize = "IMAGE_DIMENSIONS_OVERSIZE";
		public const string Tiny = "IMAGE_TINY";
		public const string LocationOrDevice = "IMAGE_LOCATION_DEVICE";
		public const string People = "IMAGE_PEOPLE";

		/// <summary>The switches of the Images family, in order, with their labels and defaults.</summary>
		public static IReadOnlyList<(string Type, string Label, TriageMode Default)> Types { get; } =
		[
			(Truncated, "Truncated file", TriageMode.On),
			(FileOversize, "Oversized file where shown", TriageMode.On),
			(DimensionsOversize, "Oversized dimensions where shown", TriageMode.On),
			(Tiny, "Tiny image (0 or 1 px) where shown", TriageMode.Off),
			(LocationOrDevice, "Location or device data", TriageMode.On),
			(People, "Names of people", TriageMode.Off),
		];

		/// <summary>Metadata fields that place a picture or identify a device or its owner.</summary>
		public static IReadOnlyList<string> LocationOrDeviceFields { get; } = ["GPS", "BodySerialNumber", "LensSerialNumber", "CameraOwnerName"];

		/// <summary>Metadata fields that name a person (the photographer or author).</summary>
		public static IReadOnlyList<string> PeopleFields { get; } = ["Artist", "IptcByline", "XmpCreator"];

		/// <summary>The kind of a reference that offers the image as a link rather than showing it.</summary>
		public const string LinkKind = "link";

		public static bool IsImage(string type) => Types.Any(t => t.Type == type);

		/// <summary>Size and dimension findings: they count only where the image is shown.</summary>
		public static bool OnlyWhereShown(string type) => type is FileOversize or DimensionsOversize or Tiny;

		/// <summary>
		/// The triage types a log 25 finding falls under: none for what is never triaged (rights,
		/// equipment, program, dates); two for metadata with both location or device data and names.
		/// </summary>
		public static IReadOnlyList<string> TypesOf(string issueType, string detail)
		{
			detail = detail.Trim();
			switch (issueType.Trim())
			{
				case "ASSET_SIZE":
					return detail switch
					{
						"LENGTH_MISMATCH" => [Truncated],
						"OVERSIZE" => [FileOversize],
						_ => [],
					};
				case "ASSET_DIMENSIONS":
					return detail switch
					{
						"OVERSIZE" => [DimensionsOversize],
						"DEGENERATE" => [Tiny],
						_ => [],
					};
				case "ASSET_METADATA":
					var fields = detail.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
					var types = new List<string>(2);
					if (fields.Any(f => LocationOrDeviceFields.Contains(f, StringComparer.Ordinal)))
					{
						types.Add(LocationOrDevice);
					}

					if (fields.Any(f => PeopleFields.Contains(f, StringComparer.Ordinal)))
					{
						types.Add(People);
					}

					return types;
				default:
					return [];
			}
		}

		/// <summary>The fields of a metadata finding a type is about ("GPS+Make+Artist", location or device → GPS).</summary>
		public static IReadOnlyList<string> FieldsOf(string type, string detail)
		{
			var wanted = type switch
			{
				LocationOrDevice => LocationOrDeviceFields,
				People => PeopleFields,
				_ => [],
			};
			return detail.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
				.Where(f => wanted.Contains(f, StringComparer.Ordinal)).ToList();
		}

		/// <summary>Whether the snapshot holds log 25; without it "no image finding" means "not looked".</summary>
		public static bool HasLogs(string snapshotDirectory) =>
			OutputFormats.ByNumber("25") is { } f && FindingFiles.PairPath(snapshotDirectory, f.PrimaryFileName) is not null;

		/// <summary>Image verdicts a match against this snapshot must keep: all when it has no log 25.</summary>
		public static IEnumerable<string> KeptWithoutLogs(string snapshotDirectory, TriageLedger ledger) =>
			HasLogs(snapshotDirectory) ? [] : ledger.Findings.Values.Where(f => IsImage(f.Type)).Select(f => f.Key).ToList();

		/// <summary>The ledger key: the finding and the image, exact but for a cache buster — never where it is used.</summary>
		public static string KeyOf(string type, string image) => $"{type}|{BrokenLinkSource.CacheNeutral(image)}";

		/// <summary>One image finding as log 25 has it.</summary>
		internal sealed record Finding(string Image, string IssueType, string Detail, string Context, string Exif, string Iptc, string Xmp);

		/// <summary>One reference as log 38 has it.</summary>
		internal sealed record Use(string Image, string Kind, string Referrer, string AsWritten, string WhenRequested, string Evidence);

		/// <summary>
		/// The image findings of a snapshot as triage items. <paramref name="localOf"/> gives the saved
		/// file of a page or stylesheet address (null when none).
		/// </summary>
		public static IReadOnlyList<TriageItem> Read(string snapshotDirectory, Func<string, string?> localOf)
		{
			if (OutputFormats.ByNumber("25") is not { } format || FindingFiles.PairPath(snapshotDirectory, format.PrimaryFileName) is not { } path)
			{
				return [];
			}

			// Filename, Url, IssueType, Detail, Context, Exif, Iptc, Xmp
			var findings = FindingFiles.Csv(path, 8, header: true)
				.Select(c => new Finding(c[1].Length > 0 ? c[1] : c[0], c[2], c[3], CsvInjectionGuard.Denormalize(c[4]),
					CsvInjectionGuard.Denormalize(c[5]), CsvInjectionGuard.Denormalize(c[6]), CsvInjectionGuard.Denormalize(c[7])))
				.Where(f => f.Image.Length > 0 && TypesOf(f.IssueType, f.Detail).Count > 0)
				.ToList();
			if (findings.Count == 0)
			{
				return [];
			}

			// Image, Kind, Referrer, AsWritten, WhenRequested, Evidence — null when the snapshot has no log 38.
			List<Use>? uses = null;
			if (OutputFormats.ByNumber("38") is { } references && FindingFiles.PairPath(snapshotDirectory, references.PrimaryFileName) is { } usesPath)
			{
				uses = FindingFiles.Csv(usesPath, 6, header: true)
					.Select(c => new Use(c[0], c[1], c[2], CsvInjectionGuard.Denormalize(c[3]), c[4], CsvInjectionGuard.Denormalize(c[5])))
					.Where(u => u.Image.Length > 0)
					.ToList();
			}

			return Items(findings, uses, localOf);
		}

		private static bool IsAddress(string referrer) =>
			Uri.TryCreate(referrer, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

		/// <summary>A reference that shows the image: not a link to it, not a stylesheet rule no page applies.</summary>
		internal static bool Shows(Use use) => use.Kind != LinkKind && use.WhenRequested != BrokenLinkOrigins.Dead;

		/// <summary>How one user uses the image: shown, linked, or never requested.</summary>
		internal static string MarkOf(IReadOnlyCollection<Use> ofOneUser, DocumentLanguage language = DocumentLanguage.English)
		{
			var marks = new List<string>(2);
			if (ofOneUser.Any(Shows))
			{
				marks.Add(DocumentText.T(language, "shown"));
			}

			if (ofOneUser.Any(u => u.Kind == LinkKind))
			{
				marks.Add(DocumentText.T(language, "linked"));
			}

			if (marks.Count == 0)
			{
				marks.Add(DocumentText.F(language, "never requested ({0})", DocumentText.T(language, BrokenLinkOrigins.Dead)));
			}

			return string.Join(", ", marks);
		}

		internal static IReadOnlyList<TriageItem> Items(IReadOnlyList<Finding> findings, IReadOnlyList<Use>? uses, Func<string, string?> localOf)
		{
			var usesByImage = (uses ?? [])
				.Where(u => IsAddress(u.Referrer))
				.GroupBy(u => BrokenLinkOrigins.KeyOf(u.Image), StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

			var items = new List<TriageItem>();
			foreach (var finding in findings)
			{
				var ofImage = usesByImage.GetValueOrDefault(BrokenLinkOrigins.KeyOf(finding.Image)) ?? [];
				var users = ofImage.GroupBy(u => u.Referrer, StringComparer.OrdinalIgnoreCase)
					.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
					.ToList();
				foreach (var type in TypesOf(finding.IssueType, finding.Detail))
				{
					// Where shown only: an image every user links to (or no page applies) is not triaged for its size.
					if (OnlyWhereShown(type) && users.Count > 0 && !ofImage.Any(Shows))
					{
						continue;
					}

					var entry = users.Count == 1 ? users[0].Key : finding.Image;
					var (family, label) = TriageSwitches.Describe(type);
					var key = KeyOf(type, finding.Image);
					var notes = users.Count > 1
						? users.ToDictionary(g => g.Key, g => MarkOf(g.ToList()), StringComparer.OrdinalIgnoreCase)
						: null;
					var byUser = users.Select(g => (g.Key, (IReadOnlyList<Use>)g.ToList())).ToList();
					items.Add(new TriageItem(key, [key], entry, type, type, family, label, finding.Image,
						ContextOf(type, finding, byUser, uses is not null),
						users.Count == 1 ? localOf(entry) : null,
						Pages: notes?.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
						PageNotes: notes,
						ContextDe: ContextOf(type, finding, byUser, uses is not null, DocumentLanguage.German)));
				}
			}

			return items;
		}

		/// <summary>What the operator decides on: the finding, the fields it is about, and who uses the image.</summary>
		internal static string ContextOf(string type, Finding finding, IReadOnlyList<(string Referrer, IReadOnlyList<Use> Uses)> users, bool usesKnown,
			DocumentLanguage language = DocumentLanguage.English)
		{
			string T(string english) => DocumentText.T(language, english);
			string F(string format, params object?[] args) => DocumentText.F(language, format, args);
			var lines = new List<string>();
			if (type is LocationOrDevice or People)
			{
				lines.Add(F("Fields: {0}", string.Join(", ", FieldsOf(type, finding.Detail))));
				foreach (var (block, value) in new[] { ("EXIF", finding.Exif), ("IPTC", finding.Iptc), ("XMP", finding.Xmp) })
				{
					if (value.Length > 0)
					{
						lines.Add($"{block}: {value}");
					}
				}
			}
			else if (finding.Context.Length > 0)
			{
				lines.Add(LogSentences.AssetFinding(language, finding.Context));
			}

			if (users.Count == 1)
			{
				lines.Add(F("Used only here ({0}):", MarkOf([.. users[0].Uses], language)));
				foreach (var u in users[0].Uses.DistinctBy(u => (u.Kind, u.AsWritten, u.WhenRequested, u.Evidence)))
				{
					lines.Add(F("Referenced as: {0}", T(u.Kind)) + (u.AsWritten.Length > 0 ? $" — {u.AsWritten}" : string.Empty));
					if (u.WhenRequested.Length > 0)
					{
						lines.Add(Links.BrokenLinkOrigins.IsTime(u.WhenRequested) ? F("When requested: {0}", T(u.WhenRequested)) : F("Kind: {0}", T(u.WhenRequested)));
					}

					if (u.Evidence.Length > 0)
					{
						lines.Add(F("Evidence: {0}", LogSentences.Evidence(language, u.Evidence)));
					}
				}
			}
			else if (users.Count > 1)
			{
				lines.Add(F("Used by {0} pages or stylesheets — one fix in the image:", users.Count));
				lines.AddRange(users.Take(SharedEntries.ListedInItem).Select(u => $"  {u.Referrer} — {MarkOf([.. u.Uses], language)}"));
				if (users.Count > SharedEntries.ListedInItem)
				{
					lines.Add(F("  … and {0} more (all in the ticket)", users.Count - SharedEntries.ListedInItem));
				}
			}
			else
			{
				lines.Add(usesKnown
					? T("No saved page or stylesheet references it (log 38).")
					: T("Who uses it is not known: this snapshot has no log 38."));
			}

			return string.Join("\n", lines);
		}
	}
}
