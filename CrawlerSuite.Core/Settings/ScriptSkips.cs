namespace CrawlerSuite.Core.Settings
{
	using System.Collections.Concurrent;
	using CrawlerSuite.Core.SpellCheck;

	/// <summary>
	/// Counts how often each "script string to skip" (SpellCheckJavaScript.TokensToFilter) really
	/// kept a string from the spell check in one scan: only strings that the general script filters
	/// would otherwise have checked count, so an entry the app no longer needs shows zero. Switched-off
	/// entries are counted as if they were on, so the next run shows whether one is needed again.
	/// Thread-safe; one counter per scan (inline scripts in pages, script files).
	/// </summary>
	public sealed class ScriptSkipCounter
	{
		private readonly Dictionary<string, string> entries = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, Tally> tallies = new(StringComparer.OrdinalIgnoreCase);

		public ScriptSkipCounter(IEnumerable<string>? active, IEnumerable<string>? switchedOff = null)
		{
			foreach (var entry in (active ?? []).Concat(switchedOff ?? []).Select(e => e.Trim()).Where(e => e.Length > 0))
			{
				entries.TryAdd(entry, entry);
			}

			foreach (var entry in (switchedOff ?? []).Select(e => e.Trim()).Where(e => e.Length > 0))
			{
				disabled.Add(entry);
			}
		}

		/// <summary>The counter for one page or one script file.</summary>
		public ScriptSkipScope For(string source) => new(this, source);

		/// <summary>Every entry with its count, zero included: (pages or files, strings skipped).</summary>
		public IReadOnlyDictionary<string, UsageCount> Counts() =>
			entries.Values.ToDictionary(
				e => e,
				e => tallies.TryGetValue(e, out var t) ? t.Count : new UsageCount(0, 0),
				StringComparer.Ordinal);

		/// <summary>
		/// Records what the script classifier decided for a string (a whole literal, or a lone token
		/// on the file-scan gate path). A skip by an entry counts when the string would otherwise have
		/// been checked; a string a switched-off entry would have skipped counts the same way.
		/// </summary>
		internal void Observe(string raw, ValueClass verdict, string source)
		{
			var v = raw.Trim();
			if (verdict.Verdict == ValueVerdict.SkipSiteToken)
			{
				if (ValueClassifier.ClassifyScriptLiteral(raw, null).ShouldCheck)
				{
					Hit(v, source);
				}
			}
			else if (verdict.ShouldCheck && disabled.Contains(v))
			{
				Hit(v, source);
			}
		}

		private void Hit(string value, string source)
		{
			if (entries.TryGetValue(value, out var entry))
			{
				tallies.GetOrAdd(entry, _ => new Tally()).Add(source);
			}
		}

		private sealed class Tally
		{
			private readonly ConcurrentDictionary<string, byte> sources = new(StringComparer.OrdinalIgnoreCase);
			private int hits;

			public UsageCount Count => new(sources.Count, Volatile.Read(ref hits));

			public void Add(string source)
			{
				Interlocked.Increment(ref hits);
				sources.TryAdd(source, 0);
			}
		}
	}

	/// <summary>A <see cref="ScriptSkipCounter"/> bound to the page or file being checked.</summary>
	public sealed class ScriptSkipScope
	{
		private readonly ScriptSkipCounter counter;
		private readonly string source;

		internal ScriptSkipScope(ScriptSkipCounter counter, string source)
		{
			this.counter = counter;
			this.source = source;
		}

		internal void Observe(string raw, ValueClass verdict) => counter.Observe(raw, verdict, source);
	}
}
