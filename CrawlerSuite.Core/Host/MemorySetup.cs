using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime;

namespace CrawlerSuite.Core.Host
{
	/// <summary>Which memory manager (garbage collector) the process runs with.</summary>
	public enum Collector
	{
		/// <summary>One heap; each clean-up runs on one core while every thread waits. .NET's default for a desktop app.</summary>
		Workstation,

		/// <summary>Server collector with DATAS: starts with one heap and adds heaps only when clean-up costs too much time. The app's default.</summary>
		ServerDatas,

		/// <summary>Server collector without DATAS: one heap per core from the start, each with its own allowance.</summary>
		ServerClassic,
	}

	/// <summary>
	/// A test lever for the memory manager. .NET fixes it when the process starts, so a choice
	/// takes effect only in a fresh process: the app starts one with these settings in its
	/// environment (that process only — nothing is written to Windows) and closes itself.
	/// A normal start always runs with the defaults.
	/// </summary>
	public sealed record MemorySetup(Collector Collector, int ConserveMemory, int MemoryCapGigabytes = 0)
	{
		/// <summary>The highest "conserve memory" level .NET accepts.</summary>
		public const int MaxConserveMemory = 9;

		/// <summary>
		/// The memory caps offered, in GB (0 = none): to make the app behave as on a machine with
		/// that much memory. .NET then cleans up harder as it nears the cap; work that cannot fit
		/// under it ends the run with an out-of-memory error. Caps .NET's memory, not the whole
		/// process (the program itself and native libraries come on top).
		/// </summary>
		public static IReadOnlyList<int> MemoryCaps { get; } = [0, 2, 3, 4];

		// The variables .NET reads at start-up. Values are hexadecimal; 0–9 read the same either way.
		internal const string ServerVariable = "DOTNET_gcServer";
		internal const string DatasVariable = "DOTNET_GCDynamicAdaptationMode";
		internal const string ConserveVariable = "DOTNET_GCConserveMemory";
		internal const string CapVariable = "DOTNET_GCHeapHardLimit";

		/// <summary>
		/// Server with DATAS, conserve memory 0: what a normal start runs with, set in the app's
		/// project file (ServerGarbageCollection, GarbageCollectionAdaptationMode). The variables a
		/// restart sets take precedence over it.
		/// </summary>
		public static MemorySetup Default { get; } = new(Collector.ServerDatas, 0);

		/// <summary>What the next restart starts with; begins as what this process runs with. Not saved.</summary>
		public static MemorySetup Chosen { get; set; } = InEffect();

		public bool IsDefault => this == Default;

		/// <summary>The environment the fresh process starts with: every variable set, so nothing is inherited by chance.</summary>
		public IReadOnlyDictionary<string, string> Environment() => new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[ServerVariable] = Collector == Collector.Workstation ? "0" : "1",
			[DatasVariable] = Collector == Collector.ServerClassic ? "0" : "1",
			[ConserveVariable] = Math.Clamp(ConserveMemory, 0, MaxConserveMemory).ToString(CultureInfo.InvariantCulture),
			[CapVariable] = (Math.Max(0, MemoryCapGigabytes) * Gigabyte).ToString("X", CultureInfo.InvariantCulture),
		};

		private const long Gigabyte = 1024L * 1024 * 1024;

		/// <summary>"server · DATAS on · conserve memory 5" — as the run steps name it.</summary>
		public string Describe()
		{
			var collector = Collector switch
			{
				Collector.ServerDatas => "server · DATAS on",
				Collector.ServerClassic => "server · DATAS off",
				_ => "workstation",
			};
			var cap = MemoryCapGigabytes > 0 ? $" · memory cap {MemoryCapGigabytes} GB" : string.Empty;
			return $"{collector} · conserve memory {ConserveMemory}{cap}";
		}

		/// <summary>
		/// What this process actually runs with: the collector from the runtime itself, DATAS and
		/// the conserve level from the runtime's own configuration where it reports them, else from
		/// the environment it was started with (the .NET defaults when neither says).
		/// </summary>
		public static MemorySetup InEffect()
		{
			IReadOnlyDictionary<string, object>? reported = null;
			try
			{
				reported = GC.GetConfigurationVariables();
			}
			catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
			{
				// Fall back to the environment below.
			}

			bool server = GCSettings.IsServerGC;
			// The runtime reports them as "GCDynamicAdaptationMode" and "GCConserveMem" (checked on .NET 10).
			long? datas = Number(reported, "DynamicAdaptation") ?? Number(System.Environment.GetEnvironmentVariable(DatasVariable));
			long conserve = Number(reported, "ConserveMem") ?? Number(System.Environment.GetEnvironmentVariable(ConserveVariable)) ?? 0;

			// The cap from the environment only: the runtime also reports a limit it derives itself
			// (from a container's or job's memory limit), which is no cap set here.
			long capBytes = Number(System.Environment.GetEnvironmentVariable(CapVariable)) ?? 0;
			int capGigabytes = (int)Math.Clamp((capBytes + Gigabyte / 2) / Gigabyte, 0, int.MaxValue);

			var collector = !server ? Collector.Workstation : datas == 0 ? Collector.ServerClassic : Collector.ServerDatas;
			return new MemorySetup(collector, (int)Math.Clamp(conserve, 0, MaxConserveMemory), capGigabytes);
		}

		/// <summary>How to start the app again with this setup: the same program, these variables added.</summary>
		public ProcessStartInfo RestartInfo(string programPath)
		{
			var info = new ProcessStartInfo(programPath) { UseShellExecute = false };
			foreach (var (name, value) in Environment())
			{
				info.Environment[name] = value;
			}

			return info;
		}

		private static long? Number(IReadOnlyDictionary<string, object>? reported, string keyPart)
		{
			if (reported is null)
			{
				return null;
			}

			var key = reported.Keys.FirstOrDefault(k => k.Contains(keyPart, StringComparison.OrdinalIgnoreCase));
			return key is null ? null : reported[key] switch
			{
				long l => l,
				int i => i,
				ulong u => (long)Math.Min(u, long.MaxValue),
				uint u => u,
				bool b => b ? 1 : 0,
				string s => Number(s),
				_ => null,
			};
		}

		/// <summary>A start-up value as .NET reads it (hexadecimal); null when absent or unreadable.</summary>
		internal static long? Number(string? value)
		{
			var text = value?.Trim() ?? string.Empty;
			if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			{
				text = text[2..]; // .NET accepts the prefix too
			}

			return long.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n) ? n : null;
		}
	}
}
