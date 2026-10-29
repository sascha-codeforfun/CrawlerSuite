using System;
using System.Diagnostics;
using CrawlerSuite.Core.Host;

namespace CrawlerSuite.Run;

/// <summary>
/// The run's wall clocks in the window header: Crawl (connectivity check, crawl and extra
/// download), Analysis (every other phase) and Total (the two added). Each counts only while a
/// phase of its kind is running; time spent waiting for the operator's answer to a question
/// counts towards neither. Display only: nothing in the run reads these clocks.
/// </summary>
public sealed class RunClocks : Observable
{
	private enum Kind { None, Crawl, Analysis }

	private long crawlTicks;
	private long analysisTicks;
	private Kind current = Kind.None;
	private long segmentStart;
	private int pauses;
	private bool running;
	private bool visible;
	private bool crawled;
	private string crawlText = string.Empty;
	private string analysisText = string.Empty;
	private string totalText = string.Empty;

	/// <summary>True from the first run on; before that the header shows no clocks.</summary>
	public bool Visible { get => visible; private set => Set(ref visible, value); }

	/// <summary>True while a run is going: the pills are amber and tick.</summary>
	public bool IsRunning { get => running; private set => Set(ref running, value); }

	public string CrawlText { get => crawlText; private set => Set(ref crawlText, value); }

	public string AnalysisText { get => analysisText; private set => Set(ref analysisText, value); }

	public string TotalText { get => totalText; private set => Set(ref totalText, value); }

	/// <summary>A run starts: all clocks at zero, none counting until the first phase.</summary>
	public void Start()
	{
		crawlTicks = 0;
		analysisTicks = 0;
		current = Kind.None;
		pauses = 0;
		crawled = false;
		IsRunning = true;
		Visible = true;
		Refresh();
	}

	/// <summary>A phase begins: the clock of its kind counts from now.</summary>
	public void EnterPhase(string title)
	{
		if (!running)
		{
			return;
		}

		CloseSegment();
		current = KindOf(title);
		crawled |= current == Kind.Crawl;
		segmentStart = Stopwatch.GetTimestamp();
		Refresh();
	}

	/// <summary>A question to the operator opens: no clock counts while it waits for an answer.</summary>
	public void Pause()
	{
		if (pauses == 0)
		{
			CloseSegment(); // book the time up to the question before the clocks hold
		}

		pauses++;
	}

	/// <summary>The answer is given: the running phase's clock counts again.</summary>
	public void Resume()
	{
		if (pauses > 0 && --pauses == 0)
		{
			segmentStart = Stopwatch.GetTimestamp();
		}
	}

	/// <summary>The run has ended (done, stopped or failed): the clocks stop and keep their times.</summary>
	public void Finish()
	{
		if (!running)
		{
			return;
		}

		CloseSegment();
		current = Kind.None;
		IsRunning = false;
		Refresh();
	}

	/// <summary>Updates the shown times (called every second while a run is going).</summary>
	public void Refresh()
	{
		long crawl = crawlTicks;
		long analysis = analysisTicks;
		if (running && pauses == 0)
		{
			long open = Stopwatch.GetTimestamp() - segmentStart;
			if (current == Kind.Crawl)
			{
				crawl += open;
			}
			else if (current == Kind.Analysis)
			{
				analysis += open;
			}
		}

		// Whole seconds, and the total is the sum of the two shown values, so the pills always add up.
		long crawlSeconds = crawl / Stopwatch.Frequency;
		long analysisSeconds = analysis / Stopwatch.Frequency;
		CrawlText = "Crawl " + (crawled ? Clock(crawlSeconds) : "—");
		AnalysisText = "Analysis " + Clock(analysisSeconds);
		TotalText = "Total " + Clock(crawlSeconds + analysisSeconds);
	}

	private void CloseSegment()
	{
		if (!running || pauses > 0 || current == Kind.None)
		{
			return;
		}

		long elapsed = Stopwatch.GetTimestamp() - segmentStart;
		if (current == Kind.Crawl)
		{
			crawlTicks += elapsed;
		}
		else
		{
			analysisTicks += elapsed;
		}

		segmentStart = Stopwatch.GetTimestamp();
	}

	private static Kind KindOf(string phase) => phase switch
	{
		RunPhases.Connectivity or RunPhases.Crawl or RunPhases.ExtraDownload => Kind.Crawl,
		RunPhases.Prepare or RunPhases.Analysis or RunPhases.SpellCheck or RunPhases.Output or RunPhases.Dictionaries => Kind.Analysis,
		_ => Kind.None, // Debug (archive and compare, not part of the analysis), Complete, Stopped: not counted
	};

	/// <summary>mm:ss, from an hour on h:mm:ss.</summary>
	internal static string Clock(long seconds)
	{
		var t = TimeSpan.FromSeconds(seconds);
		return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
	}
}
