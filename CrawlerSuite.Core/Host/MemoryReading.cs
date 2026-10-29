using System;

namespace CrawlerSuite.Core.Host
{
	/// <summary>
	/// Two figures for the memory the app uses at one moment.
	/// <para><see cref="HeldBytes"/>: what the process holds (working set) — what Windows, and on a
	/// virtual machine the host, cannot give to anyone else.</para>
	/// <para><see cref="AfterCleanUpBytes"/>: what .NET still counted after its most recent clean-up,
	/// gaps left out. Closer to what the app needs than the memory held, which also contains memory no
	/// longer in use that has not been cleaned up or not been returned yet. Still an upper bound: a
	/// clean-up of the young generations leaves the oldest one as it was, dead objects included, until
	/// a full clean-up looks at it.</para>
	/// </summary>
	public readonly record struct MemoryReading(long HeldBytes, long AfterCleanUpBytes)
	{
		/// <summary>Both figures now; a figure that cannot be read is 0.</summary>
		public static MemoryReading Now() => new(HeldNow(), AfterCleanUpNow());

		/// <summary>The working set; 0 when unavailable. Cheap: no process snapshot.</summary>
		public static long HeldNow()
		{
			try
			{
				return Environment.WorkingSet;
			}
			catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
			{
				return 0;
			}
		}

		/// <summary>Size after the most recent clean-up, summed over the generations, gaps left out; 0 before the first one.</summary>
		public static long AfterCleanUpNow()
		{
			var info = GC.GetGCMemoryInfo(GCKind.Any);
			if (info.Index == 0)
			{
				return 0;
			}

			long total = 0;
			foreach (var generation in info.GenerationInfo)
			{
				total += Math.Max(0, generation.SizeAfterBytes - generation.FragmentationAfterBytes);
			}

			return total;
		}
	}
}
