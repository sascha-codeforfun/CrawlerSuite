namespace CrawlerSuite.Core.Lexicon
{
	using System.Diagnostics;
	using System.Threading;

	/// <summary>
	/// The slowest system-dictionary checks of a run, kept so a pathological token would show
	/// up: with no time limit on compound checks, a word that takes long is visible here instead
	/// of being silently cut off. Keeps no words: only time, length and language. Times are wall
	/// time, so a memory clean-up pause during a check counts in; a short word with a long time
	/// is a pause, a long word with a long time is the case to look at. Thread-safe.
	/// </summary>
	public static class WordCheckStats
	{
		/// <summary>One check: how long it took, how long the word was, which dictionary.</summary>
		public readonly record struct SlowCheck(double Milliseconds, int Length, string Language);

		private const int Kept = 3;

		private static readonly object Gate = new();
		private static readonly (long Ticks, int Length, string Language)[] Slowest = new (long, int, string)[Kept];
		private static long threshold;
		private static long checks;

		/// <summary>Clears the record for a new run.</summary>
		public static void Reset()
		{
			lock (Gate)
			{
				for (int i = 0; i < Kept; i++)
				{
					Slowest[i] = default;
				}

				Volatile.Write(ref threshold, 0);
				Interlocked.Exchange(ref checks, 0);
			}
		}

		/// <summary>Records one dictionary check (only checks that reached the dictionary, not remembered ones).</summary>
		internal static void Record(long ticks, int length, string language)
		{
			Interlocked.Increment(ref checks);
			if (ticks <= Volatile.Read(ref threshold))
			{
				return;
			}

			lock (Gate)
			{
				int at = Kept - 1;
				if (ticks <= Slowest[at].Ticks)
				{
					return;
				}

				while (at > 0 && Slowest[at - 1].Ticks < ticks)
				{
					Slowest[at] = Slowest[at - 1];
					at--;
				}

				Slowest[at] = (ticks, length, language);
				Volatile.Write(ref threshold, Slowest[Kept - 1].Ticks);
			}
		}

		/// <summary>How many checks reached the dictionary this run.</summary>
		public static long Checks => Interlocked.Read(ref checks);

		/// <summary>The slowest checks of the run, slowest first.</summary>
		public static IReadOnlyList<SlowCheck> SlowestChecks()
		{
			lock (Gate)
			{
				return Slowest
					.Where(s => s.Ticks > 0)
					.Select(s => new SlowCheck(s.Ticks * 1000.0 / Stopwatch.Frequency, s.Length, s.Language ?? string.Empty))
					.ToList();
			}
		}
	}
}
