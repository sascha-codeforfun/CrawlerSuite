namespace CrawlerSuite.Core.Debugging
{
	using System;
	using System.Buffers.Binary;
	using System.Collections.Generic;
	using System.Globalization;
	using System.IO;
	using System.IO.Compression;
	using System.Linq;
	using System.Text;
	using System.Text.RegularExpressions;
	using CrawlerSuite.Core.Pdf;
	using CrawlerSuite.Core.SpellCheck;
	using HtmlAgilityPack;

	/// <summary>One ticket the sweep wrote: what <c>sweep.md</c> lists for it.</summary>
	/// <param name="File">The PDF's file name in the sweep folder.</param>
	/// <param name="Shape">The kinds of findings on it ("Spelling › Misspelled words + …"): tickets of one shape look alike.</param>
	/// <param name="Checks">What the automatic checks found; empty when nothing.</param>
	/// <param name="Error">Why no PDF was written; null when one was.</param>
	public sealed record SweepTicket(string File, string Url, int Pages, long Bytes, int Items, string Shape,
		IReadOnlyList<string> Checks, string? Error = null)
	{
		public bool HasFindings => Checks.Count > 0 || Error is not null;
	}

	/// <summary>
	/// Debug mode's ticket sweep: every page of triage made into a ticket with all its items, as a
	/// PDF, for a reader to go through — with cheap checks run on each first, so the read starts
	/// where something is likely off. Nothing here touches the triage ledger. The sweeps of a
	/// snapshot sit apart from its archived runs ("ticket-sweeps"), numbered like them, and each
	/// is compared with the one before.
	/// </summary>
	public static class TicketSweep
	{
		/// <summary>The folder of a snapshot's sweeps, inside its archive folder; apart from the runs, which it never counts as.</summary>
		public const string FolderName = "ticket-sweeps";

		public const string ReportFile = "sweep.md";
		public const string TableFile = "sweep.tsv";
		public const string CompareFile = "sweep-compare.md";

		/// <summary>A PDF above this needs a look: a ticket of a few pages is tens of kilobytes.</summary>
		public const long SizeBudget = 400 * 1024;

		/// <summary>The sweeps folder of a snapshot.</summary>
		public static string Folder(string snapshotArchive) => Path.Combine(snapshotArchive, FolderName);

		/// <summary>The next sweep's folder name in <paramref name="sweeps"/>: version and number, as a run is named.</summary>
		public static string NextName(string sweeps, string version) => RunArchive.NextRunName(sweeps, version);

		/// <summary>The sweep before <paramref name="name"/> in <paramref name="sweeps"/>, or null.</summary>
		public static string? Previous(string sweeps, string name)
		{
			var all = RunArchive.Runs(sweeps);
			int at = all.ToList().IndexOf(name);
			return at > 0 ? all[at - 1] : at < 0 && all.Count > 0 ? all[^1] : null;
		}

		/// <summary>A file name not yet taken: "name.pdf", then "name-2.pdf" …</summary>
		public static string UniqueName(string stem, ISet<string> taken)
		{
			var name = stem;
			for (int n = 2; !taken.Add(name); n++)
			{
				name = $"{stem}-{n}";
			}

			return name;
		}

		// ── Shapes ────────────────────────────────────────────────────────────

		/// <summary>The kinds on a ticket, as "Family › Label" in a fixed order: the ticket's shape.</summary>
		public static string ShapeOf(IEnumerable<(string Family, string Label)> kinds)
		{
			var parts = kinds.Select(k => $"{k.Family} \u203A {k.Label}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
			return parts.Count == 0 ? "(no items)" : string.Join(" + ", parts);
		}

		/// <summary>
		/// One ticket per shape to read first: the one with the most items (the first of those),
		/// ticket with an error or a check finding first, so nothing flagged hides behind a clean one.
		/// </summary>
		public static IReadOnlyList<SweepTicket> Representatives(IEnumerable<SweepTicket> tickets) =>
			tickets.GroupBy(t => t.Shape, StringComparer.Ordinal)
				.Select(g => g.OrderByDescending(t => t.HasFindings).ThenByDescending(t => t.Items).First())
				.ToList();

		// ── Checks ────────────────────────────────────────────────────────────

		/// <summary>A PDF above the budget.</summary>
		public static string? Size(long bytes) =>
			bytes > SizeBudget ? $"size {Kb(bytes)} (budget {Kb(SizeBudget)})" : null;

		// Brackets or quotation marks with nothing but space inside: text that went missing on the way.
		private static readonly Regex EmptyPairPattern = new(
			"(?<![\\p{L}\\p{N}_])\\(\\s*\\)|\\[\\s*\\]|\u201E\\s*\u201C|\u201C\\s*\u201D|\u00AB\\s*\u00BB|\u201A\\s*\u2018|\u2018\\s*\u2019",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		/// <summary>Empty brackets or quotes in the ticket's text, counted by form.</summary>
		public static string? EmptyPairs(string ticketText)
		{
			var found = EmptyPairPattern.Each(ticketText)
				.GroupBy(m => Regex.Replace(m.Value, "\\s+", " "), StringComparer.Ordinal)
				.Select(g => $"\u201A{g.Key}\u2018 \u00D7{g.Count()}")
				.ToList();
			return found.Count == 0 ? null : "empty brackets or quotes: " + string.Join(", ", found);
		}

		/// <summary>
		/// The text a visitor can read on a saved page, normalised as the spell check's excerpts are:
		/// the body text without scripts and styles, the title, meta values, alt and title texts.
		/// </summary>
		public static string VisibleText(string html)
		{
			// A saved page, so the crawled site's bytes: through HtmlDocuments, with its nesting cap.
			var doc = Html.HtmlDocuments.Create();
			doc.LoadHtml(html);
			foreach (var node in doc.DocumentNode.Descendants().Where(n => n.Name is "script" or "style" or "noscript" or "template").ToList())
			{
				node.Remove();
			}

			var sb = new StringBuilder();
			foreach (var node in doc.DocumentNode.Descendants())
			{
				if (node.NodeType == HtmlNodeType.Text)
				{
					sb.Append(node.InnerText).Append(' ');
				}
				else if (node.NodeType == HtmlNodeType.Element)
				{
					foreach (var name in new[] { "content", "alt", "title", "aria-label" })
					{
						if (node.GetAttributeValue(name, string.Empty) is { Length: > 0 } value)
						{
							sb.Append(' ').Append(value).Append(' ');
						}
					}
				}
			}

			return Canonicalizer.CanonicalizeKeepingTagText(sb.ToString());
		}

		/// <summary>
		/// Everything an excerpt can come from on a saved page, normalised as the excerpts are: the
		/// text nodes (scripts included) in document order, then every attribute value on its own,
		/// apart from each other (<see cref="PoolBreak"/>). Kept apart, a sentence with a link, a
		/// superscript or a span inside reads on across the markup as the excerpt does, and no attribute
		/// value lands in its middle. Only for <see cref="MissingExcerpts"/>: an excerpt that is not even
		/// in here lost text on the way.
		/// </summary>
		public static string PageText(string html)
		{
			// A saved page, so the crawled site's bytes: through HtmlDocuments, with its nesting cap.
			var doc = Html.HtmlDocuments.Create();
			doc.LoadHtml(html);
			var text = new StringBuilder();
			var attributes = new List<string>();
			foreach (var node in doc.DocumentNode.Descendants())
			{
				if (node.NodeType == HtmlNodeType.Text)
				{
					text.Append(node.InnerText);
				}
				else if (node.NodeType == HtmlNodeType.Element)
				{
					attributes.AddRange(node.Attributes.Select(a => a.Value).Where(v => v.Length > 0));
				}
			}

			return string.Join(PoolBreak, new[] { text.ToString() }.Concat(attributes).Select(Canonicalizer.CanonicalizeKeepingTagText));
		}

		/// <summary>Between the pools of <see cref="PageText"/>: no excerpt holds it, so none is found across it.</summary>
		public const string PoolBreak = "\n\uFFFF\n";

		// "Description: …", "alt of <img>: …": the place an occurrence stands, in front of its excerpt.
		private static readonly Regex SourceLabel = new(@"^(?:Description|Keywords|Meta tag \S+|[\w:-]+ of <[\w-]+>): ", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		// A script's string is shown decoded; the page has it escaped. Not comparable, not checked.
		private const string ScriptLabel = "In the page's script";

		/// <summary>
		/// Spelling excerpts whose text the page does not have: each excerpt's pieces between the
		/// "…" of its window must stand in the page's text (<see cref="PageText"/>), spaces aside — a
		/// window joins what markup separates. A piece missing means the excerpt lost or changed
		/// text on the way (an emptied bracket, a dropped word). The place named in front of an
		/// excerpt is not part of it; script strings are left out.
		/// </summary>
		public static string? MissingExcerpts(IEnumerable<string> excerpts, string pageText)
		{
			var missing = new List<string>();
			var page = NoSpace(pageText);
			foreach (var line in excerpts.SelectMany(e => e.Split('\n')))
			{
				if (line.StartsWith(ScriptLabel, StringComparison.Ordinal))
				{
					continue;
				}

				var excerpt = SourceLabel.Replace(line, string.Empty);
				foreach (var piece in Canonicalizer.CanonicalizeKeepingTagText(excerpt).Split('\u2026'))
				{
					var p = piece.Trim();
					if (p.Length >= 12 && !page.Contains(NoSpace(p), StringComparison.Ordinal))
					{
						missing.Add(p);
					}
				}
			}

			if (missing.Count == 0)
			{
				return null;
			}

			var shown = missing.Take(2).Select(p => "\u201A" + (p.Length > 70 ? p[..70] + "\u2026" : p) + "\u2018");
			return $"excerpt not in the page's text: {missing.Count} piece(s), e.g. {string.Join(", ", shown)}";
		}

		private static string NoSpace(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

		// "Closer '“' should be '”' (English-double): …" — the advice and the system it belongs to.
		private static readonly Regex AdvicePattern = new(@"should be '(?<mark>.)' \((?<system>[^)]+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		/// <summary>
		/// Quotation-mark advice that points away from how the page quotes: a fix in English
		/// double quotes on a page that opens its quotations with „, or the other way round.
		/// </summary>
		public static string? QuoteAdvice(IEnumerable<string> quoteDetails, string visibleText)
		{
			int german = visibleText.Count(c => c == '\u201E');
			int english = visibleText.Count(c => c == '\u201D');
			var against = new List<string>();
			foreach (var detail in quoteDetails)
			{
				if (AdvicePattern.Match(detail) is not { Success: true } m)
				{
					continue;
				}

				var system = m.Groups["system"].Value;
				if ((system == "English-double" && german > english) || (system == "German-double" && english > german))
				{
					against.Add($"\u201A{m.Groups["mark"].Value}\u2018 ({system})");
				}
			}

			return against.Count == 0
				? null
				: $"quote advice against the page's own quotes ({german} \u201E, {english} \u201D): {string.Join(", ", against.Distinct(StringComparer.Ordinal))}";
		}

		/// <summary>Tables of the embedded font programs that a PDF does not draw with (see <see cref="SfntTables.ForPdf"/>).</summary>
		public static string? FontTables(byte[] pdf)
		{
			var extra = new SortedSet<string>(StringComparer.Ordinal);
			foreach (var program in FontPrograms(pdf))
			{
				if (program.Length < 12)
				{
					continue;
				}

				int count = BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(4));
				for (int i = 0; i < count && 12 + i * 16 + 4 <= program.Length; i++)
				{
					var tag = Encoding.ASCII.GetString(program, 12 + i * 16, 4);
					if (!SfntTables.ForPdf.Contains(tag))
					{
						extra.Add(tag.TrimEnd());
					}
				}
			}

			return extra.Count == 0 ? null : "font tables a PDF does not use: " + string.Join(", ", extra);
		}

		// This app's PDF writer: "<< /Length n /Filter /FlateDecode /Length1 m >>" (TrueType) or
		// "… /Subtype /OpenType >>" (CFF) before a font program's stream.
		private static readonly Regex FontStream = new(@"<< /Length (?<len>\d+) /Filter /FlateDecode (?:/Length1 \d+|/Subtype /OpenType) >>\nstream\n",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		/// <summary>The font programs embedded in a PDF this app wrote, unpacked.</summary>
		internal static IEnumerable<byte[]> FontPrograms(byte[] pdf)
		{
			var text = Encoding.Latin1.GetString(pdf);
			foreach (Match m in FontStream.Each(text))
			{
				int length = int.Parse(m.Groups["len"].Value, CultureInfo.InvariantCulture);
				int start = m.Index + m.Length;
				if (start + length > pdf.Length)
				{
					continue;
				}

				byte[] program;
				try
				{
					using var z = new ZLibStream(new MemoryStream(pdf, start, length), CompressionMode.Decompress);
					using var output = new MemoryStream();
					z.CopyTo(output);
					program = output.ToArray();
				}
				catch (InvalidDataException)
				{
					continue;
				}

				yield return program;
			}
		}

		// ── Report ────────────────────────────────────────────────────────────

		/// <summary>
		/// <c>sweep.md</c>: what was swept, the checks by kind, the tickets to read first (one per
		/// shape), then every ticket. Addresses as they are: the folder is the operator's own.
		/// </summary>
		public static string Report(string snapshotName, string version, DateTime made, IReadOnlyList<SweepTicket> tickets, bool cancelled)
		{
			var nl = "\n";
			var sb = new StringBuilder();
			var representatives = Representatives(tickets);
			var picked = representatives.Select(t => t.File).ToHashSet(StringComparer.Ordinal);
			sb.Append("# Ticket sweep").Append(nl).Append(nl);
			sb.Append($"CrawlerSuite {version} · snapshot {snapshotName} · {made.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}").Append(nl).Append(nl);
			sb.Append($"{tickets.Count} ticket(s), {tickets.Count(t => t.Error is null)} PDF(s), {representatives.Count} shape(s), " +
				$"{tickets.Count(t => t.HasFindings)} with a check finding or an error.").Append(nl);
			sb.Append("Every item of every page as Ticket, in memory only: the triage ledger is not touched.").Append(nl);
			if (cancelled)
			{
				sb.Append(nl).Append("**Stopped before the end:** the list is what was written until then.").Append(nl);
			}

			sb.Append(nl).Append("## Checks").Append(nl).Append(nl);
			var byKind = tickets.SelectMany(t => t.Checks.Select(c => (Kind: KindOf(c), t.File)))
				.Concat(tickets.Where(t => t.Error is not null).Select(t => (Kind: "no PDF", t.File)))
				.GroupBy(x => x.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ToList();
			if (byKind.Count == 0)
			{
				sb.Append("Nothing found by the checks.").Append(nl);
			}
			else
			{
				sb.Append("| Check | Tickets | e.g. |").Append(nl).Append("|---|---:|---|").Append(nl);
				foreach (var g in byKind)
				{
					sb.Append($"| {g.Key} | {g.Count()} | {Cell(g.First().File)} |").Append(nl);
				}
			}

			sb.Append(nl).Append("## Read first: one ticket per shape").Append(nl).Append(nl);
			sb.Append("| # | File | Pages | Size | Items | Shape | Checks |").Append(nl).Append("|---:|---|---:|---:|---:|---|---|").Append(nl);
			int n = 0;
			foreach (var t in representatives.OrderByDescending(t => t.HasFindings).ThenBy(t => t.Shape, StringComparer.Ordinal))
			{
				sb.Append($"| {++n} | {Cell(t.File)} | {t.Pages} | {Kb(t.Bytes)} | {t.Items} | {Cell(t.Shape)} | {Cell(ChecksText(t))} |").Append(nl);
			}

			sb.Append(nl).Append("## Every ticket").Append(nl).Append(nl);
			sb.Append("| File | Page | Pages | Size | Items | Read first | Checks |").Append(nl).Append("|---|---|---:|---:|---:|---|---|").Append(nl);
			foreach (var t in tickets)
			{
				sb.Append($"| {Cell(t.File)} | {Cell(t.Url)} | {t.Pages} | {Kb(t.Bytes)} | {t.Items} | {(picked.Contains(t.File) ? "yes" : string.Empty)} | {Cell(ChecksText(t))} |").Append(nl);
			}

			return sb.ToString();
		}

		/// <summary><c>sweep.tsv</c>: one line per ticket, for comparing with the next sweep.</summary>
		public static string Table(IReadOnlyList<SweepTicket> tickets)
		{
			var sb = new StringBuilder("file\tpages\tbytes\titems\tshape\tchecks\n");
			foreach (var t in tickets)
			{
				sb.Append(Tab(t.File)).Append('\t').Append(t.Pages.ToString(CultureInfo.InvariantCulture)).Append('\t')
					.Append(t.Bytes.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(t.Items.ToString(CultureInfo.InvariantCulture)).Append('\t')
					.Append(Tab(t.Shape)).Append('\t').Append(Tab(string.Join(" | ", AllChecks(t).Select(KindOf).Distinct(StringComparer.Ordinal)))).Append('\n');
			}

			return sb.ToString();
		}

		private sealed record Row(int Pages, long Bytes, int Items, string Shape, string Checks);

		private static Dictionary<string, Row> Rows(string table)
		{
			var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
			foreach (var line in table.Split('\n').Skip(1).Where(l => l.Length > 0))
			{
				var c = line.Split('\t');
				if (c.Length >= 6
					&& int.TryParse(c[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages)
					&& long.TryParse(c[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
					&& int.TryParse(c[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var items))
				{
					rows[c[0]] = new Row(pages, bytes, items, c[4], c[5]);
				}
			}

			return rows;
		}

		/// <summary>
		/// <c>sweep-compare.md</c>: what changed against the sweep before — tickets new or gone,
		/// other page counts, sizes off by more than a fifth, checks that appeared or went away.
		/// </summary>
		public static string Compare(string previousName, string previousTable, string currentName, string currentTable)
		{
			var before = Rows(previousTable);
			var now = Rows(currentTable);
			var nl = "\n";
			var lines = new List<string>();
			foreach (var file in now.Keys.Except(before.Keys).Order(StringComparer.Ordinal))
			{
				lines.Add($"| {Cell(file)} | new | | {Cell(now[file].Checks)} |");
			}

			foreach (var file in before.Keys.Except(now.Keys).Order(StringComparer.Ordinal))
			{
				lines.Add($"| {Cell(file)} | gone | | |");
			}

			foreach (var file in now.Keys.Intersect(before.Keys).Order(StringComparer.Ordinal))
			{
				var (a, b) = (before[file], now[file]);
				var changes = new List<string>();
				if (a.Pages != b.Pages) changes.Add($"pages {a.Pages} → {b.Pages}");
				if (a.Bytes > 0 && Math.Abs(b.Bytes - a.Bytes) > a.Bytes / 5) changes.Add($"size {Kb(a.Bytes)} → {Kb(b.Bytes)}");
				if (a.Items != b.Items) changes.Add($"items {a.Items} → {b.Items}");
				if (a.Shape != b.Shape) changes.Add("other shape");
				if (a.Checks != b.Checks || changes.Count > 0)
				{
					var checks = a.Checks == b.Checks ? string.Empty : $"{(a.Checks.Length == 0 ? "–" : a.Checks)} → {(b.Checks.Length == 0 ? "–" : b.Checks)}";
					lines.Add($"| {Cell(file)} | {Cell(string.Join(", ", changes))} | | {Cell(checks)} |");
				}
			}

			var sb = new StringBuilder($"# Ticket sweep {currentName} against {previousName}").Append(nl).Append(nl);
			if (lines.Count == 0)
			{
				return sb.Append("No ticket changed in pages, size (by more than a fifth), items, shape or checks.").Append(nl).ToString();
			}

			sb.Append($"{lines.Count} ticket(s) changed.").Append(nl).Append(nl);
			sb.Append("| File | Change | | Checks |").Append(nl).Append("|---|---|---|---|").Append(nl);
			foreach (var line in lines)
			{
				sb.Append(line).Append(nl);
			}

			return sb.ToString();
		}

		private static IEnumerable<string> AllChecks(SweepTicket t) => t.Error is null ? t.Checks : t.Checks.Append("no PDF: " + t.Error);

		private static string ChecksText(SweepTicket t) => string.Join("; ", AllChecks(t));

		// "size 512 KB (budget …)" → "size": the part before the first digit, colon or bracket names the check.
		private static string KindOf(string check)
		{
			int cut = check.IndexOfAny([':', '(']);
			var head = cut > 0 ? check[..cut] : check;
			int digit = head.IndexOfAny("0123456789".ToCharArray());
			return (digit > 0 ? head[..digit] : head).Trim();
		}

		private static string Kb(long bytes) => $"{Math.Max(1, (bytes + 512) / 1024).ToString(CultureInfo.InvariantCulture)} KB";

		private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

		private static string Tab(string text) => text.Replace('\t', ' ').Replace('\n', ' ');
	}
}
