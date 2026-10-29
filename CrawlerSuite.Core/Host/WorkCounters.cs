namespace CrawlerSuite.Core.Host
{
	using System.Collections.Generic;
	using System.IO;
	using System.Text;
	using System.Threading;

	/// <summary>
	/// How much work a run did: files and bytes the analysis read, HTML documents parsed, text
	/// runs examined for spelling (page text and script literals), words checked, and dictionary
	/// lookups (asked of the dictionary, or answered from the per-word memory). Counted, never
	/// estimated. Pure bookkeeping: nothing in the run reads these numbers back.
	///
	/// Every thread counts into its own slot, so parallel loops never contend for a counter; the
	/// slots are added up when a step row is written (<see cref="Mark"/>).
	/// </summary>
	public static class WorkCounters
	{
		private static WorkTally current = new();

		/// <summary>Starts counting for a new run (all at zero).</summary>
		internal static void Reset() => Volatile.Write(ref current, new WorkTally());

		internal static void FileRead(long bytes) => Volatile.Read(ref current).FileRead(bytes);

		internal static void Parsed() => Volatile.Read(ref current).Parsed();

		internal static void TextRuns(long count) => Volatile.Read(ref current).TextRuns(count);

		internal static void Words(long count) => Volatile.Read(ref current).Words(count);

		internal static void Lookup(bool remembered) => Volatile.Read(ref current).Lookup(remembered);

		/// <summary>The totals so far in this run, all threads added up.</summary>
		internal static StepWork Mark() => Volatile.Read(ref current).Mark();
	}

	/// <summary>One run's counts: a slot per thread, added up on <see cref="Mark"/>.</summary>
	internal sealed class WorkTally
	{
		private sealed class Slot
		{
			public long FilesRead;
			public long BytesRead;
			public long Parses;
			public long TextRuns;
			public long Words;
			public long Lookups;
			public long Remembered;
		}

		private readonly ThreadLocal<Slot> slots = new(() => new Slot(), trackAllValues: true);

		public void FileRead(long bytes)
		{
			var slot = slots.Value!;
			slot.FilesRead++;
			slot.BytesRead += bytes;
		}

		public void Parsed() => slots.Value!.Parses++;

		public void TextRuns(long count) => slots.Value!.TextRuns += count;

		public void Words(long count) => slots.Value!.Words += count;

		public void Lookup(bool remembered)
		{
			var slot = slots.Value!;
			if (remembered)
			{
				slot.Remembered++;
			}
			else
			{
				slot.Lookups++;
			}
		}

		public StepWork Mark()
		{
			long files = 0, bytes = 0, parses = 0, runs = 0, words = 0, lookups = 0, remembered = 0;
			foreach (var slot in slots.Values)
			{
				files += Interlocked.Read(ref slot.FilesRead);
				bytes += Interlocked.Read(ref slot.BytesRead);
				parses += Interlocked.Read(ref slot.Parses);
				runs += Interlocked.Read(ref slot.TextRuns);
				words += Interlocked.Read(ref slot.Words);
				lookups += Interlocked.Read(ref slot.Lookups);
				remembered += Interlocked.Read(ref slot.Remembered);
			}

			return new StepWork(files, bytes, parses, runs, words, lookups, remembered);
		}
	}

	/// <summary>File reads of the analysis that count towards the work figures. Same results as File.*.</summary>
	internal static class WorkFile
	{
		public static byte[] ReadAllBytes(string path)
		{
			var bytes = File.ReadAllBytes(path);
			WorkCounters.FileRead(bytes.LongLength);
			return bytes;
		}

		public static string ReadAllText(string path, Encoding encoding)
		{
			var text = File.ReadAllText(path, encoding);
			WorkCounters.FileRead(new FileInfo(path).Length);
			return text;
		}

		public static string ReadAllText(string path)
		{
			var text = File.ReadAllText(path);
			WorkCounters.FileRead(new FileInfo(path).Length);
			return text;
		}
	}
}
