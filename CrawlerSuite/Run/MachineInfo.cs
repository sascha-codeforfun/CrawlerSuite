using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CrawlerSuite.Run;

/// <summary>The processors as Windows reports them. Cores and Logical are 0 when Windows could not be asked.</summary>
/// <param name="Available">What this app may use (respects a restricted processor set or a cap on processor time).</param>
/// <param name="Performance">Cores of the fastest kind on a chip with mixed core types; 0 when all cores are alike.</param>
public sealed record ProcessorReport(int Available, int Cores, int Logical, int Performance, int Efficiency);

/// <summary>Free and total space of the drive a folder is on, from the nearest folder that exists.</summary>
public sealed record DriveSpace(string Root, long Free, long Total);

/// <summary>
/// What the settings screen shows about this computer. Both questions go straight to Windows
/// with documented calls that need no administrator rights, no service and no registry; they
/// only read what Windows already knows. A failed call yields less information, never a guess.
/// </summary>
public static class MachineInfo
{
	private const int RelationProcessorCore = 0;
	private const int ErrorInsufficientBuffer = 122;

	/// <summary>Reads the processors (once per call; nothing is kept).</summary>
	public static ProcessorReport Processors()
	{
		int available = Environment.ProcessorCount;
		if (!OperatingSystem.IsWindows())
		{
			return new ProcessorReport(available, 0, 0, 0, 0);
		}

		try
		{
			uint length = 0;
			if (GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length) || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
			{
				return new ProcessorReport(available, 0, 0, 0, 0);
			}

			var buffer = Marshal.AllocHGlobal((int)length);
			try
			{
				if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
				{
					return new ProcessorReport(available, 0, 0, 0, 0);
				}

				var classes = new System.Collections.Generic.List<byte>();
				int logical = 0;
				int offset = 0;
				while (offset < length)
				{
					var entry = buffer + offset;
					int size = Marshal.ReadInt32(entry, 4);
					if (Marshal.ReadInt32(entry, 0) == RelationProcessorCore && size > 32)
					{
						// PROCESSOR_RELATIONSHIP: Flags, EfficiencyClass, 20 reserved bytes, GroupCount, then the group masks.
						classes.Add(Marshal.ReadByte(entry, 9));
						int groups = Marshal.ReadInt16(entry, 30);
						int maskSize = IntPtr.Size == 8 ? 16 : 12;
						for (int g = 0; g < groups; g++)
						{
							var mask = IntPtr.Size == 8 ? (ulong)Marshal.ReadInt64(entry, 32 + g * maskSize) : (uint)Marshal.ReadInt32(entry, 32 + g * maskSize);
							logical += BitOperations.PopCount(mask);
						}
					}

					if (size <= 0)
					{
						break;
					}

					offset += size;
				}

				int cores = classes.Count;
				if (cores == 0 || logical == 0)
				{
					return new ProcessorReport(available, 0, 0, 0, 0);
				}

				// A higher efficiency class is the faster kind; all equal means one kind of core.
				byte top = classes.Max();
				int performance = classes.Count(c => c == top);
				return performance == cores
					? new ProcessorReport(available, cores, logical, 0, 0)
					: new ProcessorReport(available, cores, logical, performance, cores - performance);
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
		{
			return new ProcessorReport(available, 0, 0, 0, 0);
		}
	}

	/// <summary>The space of the drive a folder is on; null when the path points at no drive that answers.</summary>
	public static DriveSpace? SpaceOf(string folder)
	{
		if (string.IsNullOrWhiteSpace(folder))
		{
			return null;
		}

		try
		{
			var path = Path.GetFullPath(folder);
			while (!Directory.Exists(path))
			{
				path = Path.GetDirectoryName(path);
				if (string.IsNullOrEmpty(path))
				{
					return null;
				}
			}

			var root = Path.GetPathRoot(path) ?? path;
			if (OperatingSystem.IsWindows())
			{
				return GetDiskFreeSpaceEx(path, out var free, out var total, out _)
					? new DriveSpace(root.TrimEnd('\\', '/'), (long)free, (long)total)
					: null;
			}

			var drive = new DriveInfo(root);
			return new DriveSpace(root, drive.AvailableFreeSpace, drive.TotalSize);
		}
		catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
		{
			return null;
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
}
