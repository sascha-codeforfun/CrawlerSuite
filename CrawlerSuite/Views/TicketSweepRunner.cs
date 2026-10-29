using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Threading;
using CrawlerSuite.Core.Debugging;
using CrawlerSuite.Run;

namespace CrawlerSuite.Views;

/// <summary>
/// Debug mode's ticket sweep: every triage page as a ticket PDF (every item as Ticket, decided in
/// memory only), each with its text next to it and checked, then <c>sweep.md</c>,
/// <c>sweep.tsv</c> and a comparison with the sweep before. The documents are laid out and
/// written on the UI thread one at a time, the checks run on the worker between them, so the
/// busy overlay keeps turning and Cancel stops after the ticket in hand.
/// </summary>
internal static class TicketSweepRunner
{
	/// <summary>What the UI thread hands the worker for one ticket.</summary>
	private sealed record Made(string File, string Url, int Pages, int Items, string Shape, string Text,
		IReadOnlyList<string> Excerpts, IReadOnlyList<string> QuoteDetails, string? LocalFile,
		IReadOnlyList<string> WriterNotes, string? Error);

	/// <summary>The outcome, for the message after the sweep.</summary>
	internal sealed record Outcome(string Folder, int Tickets, int Pdfs, int Flagged, int Shapes, bool Cancelled, bool Compared);

	public static async Task<Outcome> RunAsync(IReadOnlyList<TriagePageVm> pages, string folder, string snapshotName, string version,
		Core.Text.DocumentLanguage language, Dispatcher ui, BusyOverlayKit.BusyReport report, CancellationToken cancel)
	{
		Directory.CreateDirectory(folder);
		var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var tickets = new List<SweepTicket>();
		bool cancelled = false;
		for (int i = 0; i < pages.Count; i++)
		{
			if (cancel.IsCancellationRequested)
			{
				cancelled = true;
				break;
			}

			report.Say($"Ticket {i + 1} of {pages.Count}", i, pages.Count);
			var page = pages[i];
			var stem = TicketSweep.UniqueName(TicketDocument.SuggestedName(page.Url), taken);
			var made = await ui.InvokeAsync(() => Write(page, folder, stem, snapshotName, version, language), DispatcherPriority.Background);
			tickets.Add(Check(made, folder));
		}

		var now = DateTime.Now;
		File.WriteAllText(Path.Combine(folder, TicketSweep.ReportFile), TicketSweep.Report(snapshotName, version, now, tickets, cancelled));
		var table = TicketSweep.Table(tickets);
		File.WriteAllText(Path.Combine(folder, TicketSweep.TableFile), table);

		// Against the sweep before, when there is one with a table.
		var sweeps = Path.GetDirectoryName(folder) ?? folder;
		var name = Path.GetFileName(folder);
		bool compared = false;
		if (TicketSweep.Previous(sweeps, name) is { } previous
			&& Path.Combine(sweeps, previous, TicketSweep.TableFile) is var previousTable && File.Exists(previousTable))
		{
			File.WriteAllText(Path.Combine(folder, TicketSweep.CompareFile),
				TicketSweep.Compare(previous, File.ReadAllText(previousTable), name, table));
			compared = true;
		}

		return new Outcome(folder, tickets.Count, tickets.Count(t => t.Error is null), tickets.Count(t => t.HasFindings),
			TicketSweep.Representatives(tickets).Count, cancelled, compared);
	}

	/// <summary>On the UI thread: the ticket laid out and written as PDF, with what the checks need from it.</summary>
	private static Made Write(TriagePageVm page, string folder, string stem, string snapshotName, string version, Core.Text.DocumentLanguage language)
	{
		var ticketed = page.AllItems.Where(i => i.IsTicket || i.IsLocale).ToList();
		var shape = TicketSweep.ShapeOf(ticketed.Select(i => (i.Item.Family, i.Item.Label)));
		var excerpts = ticketed.Where(i => i.IsSpelling).Select(i => i.Item.Context).ToList();
		var quotes = ticketed.Where(i => i.Item.Type is "QUOTE_WRONG_OPEN" or "QUOTE_WRONG_CLOSE").Select(i => i.Item.Detail).ToList();
		var local = page.AllItems.Select(i => i.Item.LocalFile).FirstOrDefault(f => !string.IsNullOrEmpty(f));
		var file = stem + ".pdf";
		try
		{
			var document = TicketDocument.Build(page, snapshotName, text => text, language);
			var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
			var pages = new TicketDocument.FooterPaginator(document, version, snapshotName, language);
			var written = Pdf.FlowDocumentPdf.Save(document, pages, Path.Combine(folder, file));
			return new Made(file, page.Url, written.Pages, ticketed.Count, shape, text, excerpts, quotes, local, written.Skipped, null);
		}
		catch (Exception ex) when (ex is Pdf.PdfExportException or InvalidOperationException or IOException or UnauthorizedAccessException
			or ArgumentException or FormatException or System.Windows.Markup.XamlParseException)
		{
			Core.Logger.LogWarning($"Ticket sweep: no PDF for {page.Url} — {ex.Message}");
			return new Made(file, page.Url, 0, ticketed.Count, shape, string.Empty, excerpts, quotes, local, [], ex.Message);
		}
	}

	/// <summary>On the worker: the ticket's text next to it, and the checks.</summary>
	private static SweepTicket Check(Made made, string folder)
	{
		if (made.Error is not null)
		{
			return new SweepTicket(made.File, made.Url, 0, 0, made.Items, made.Shape, [], made.Error);
		}

		var path = Path.Combine(folder, made.File);
		var checks = new List<string>();
		long bytes = 0;
		try
		{
			File.WriteAllText(Path.ChangeExtension(path, ".txt"), made.Text);
			var pdf = File.ReadAllBytes(path);
			bytes = pdf.Length;
			Add(TicketSweep.Size(bytes));
			Add(TicketSweep.FontTables(pdf));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			checks.Add($"not read back: {ex.Message}");
		}

		Add(TicketSweep.EmptyPairs(made.Text));
		if ((made.Excerpts.Count > 0 || made.QuoteDetails.Count > 0) && made.LocalFile is { } local && File.Exists(local))
		{
			try
			{
				var html = File.ReadAllText(local);
				Add(TicketSweep.MissingExcerpts(made.Excerpts, TicketSweep.PageText(html)));
				Add(TicketSweep.QuoteAdvice(made.QuoteDetails, TicketSweep.VisibleText(html)));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				checks.Add($"page not read: {ex.Message}");
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				// The parser refuses a page nested past its cap with a plain Exception: this
				// ticket notes it, the sweep goes on.
				checks.Add($"page not parsed: {ex.Message}");
			}
		}

		if (made.WriterNotes.Count > 0)
		{
			checks.Add("PDF writer notes: " + string.Join("; ", made.WriterNotes));
		}

		return new SweepTicket(made.File, made.Url, made.Pages, bytes, made.Items, made.Shape, checks);

		void Add(string? check)
		{
			if (check is not null)
			{
				checks.Add(check);
			}
		}
	}
}
