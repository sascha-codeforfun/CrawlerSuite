using System;
using System.Runtime;
using CrawlerSuite.Core.Host;
using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>
	/// The memory-manager lever: the environment a restart starts with, how a setup is named in
	/// the run steps, and what this process reports it runs with.
	/// </summary>
	public class MemorySetupTests
	{
		[Theory]
		[InlineData(Collector.Workstation, 0, "0", "1", "0")]
		[InlineData(Collector.ServerDatas, 5, "1", "1", "5")]
		[InlineData(Collector.ServerClassic, 9, "1", "0", "9")]
		public void Environment_SetsEveryVariable(Collector collector, int conserve, string server, string datas, string level)
		{
			var env = new MemorySetup(collector, conserve).Environment();

			Assert.Equal(4, env.Count);
			Assert.Equal("0", env[MemorySetup.CapVariable]);
			Assert.Equal(server, env[MemorySetup.ServerVariable]);
			Assert.Equal(datas, env[MemorySetup.DatasVariable]);
			Assert.Equal(level, env[MemorySetup.ConserveVariable]);
		}

		[Theory]
		[InlineData(2, "80000000")]
		[InlineData(3, "C0000000")]
		[InlineData(4, "100000000")]
		public void Environment_SetsTheCapInHexBytes(int gigabytes, string hex) =>
			Assert.Equal(hex, new MemorySetup(Collector.ServerDatas, 0, gigabytes).Environment()[MemorySetup.CapVariable]);

		[Fact]
		public void Describe_NamesACapOnlyWhenSet()
		{
			Assert.Equal("server · DATAS on · conserve memory 0 · memory cap 3 GB", new MemorySetup(Collector.ServerDatas, 0, 3).Describe());
			Assert.DoesNotContain("cap", new MemorySetup(Collector.ServerDatas, 0).Describe());
		}

		[Fact]
		public void Environment_KeepsTheLevelInRange() =>
			Assert.Equal("9", new MemorySetup(Collector.Workstation, 42).Environment()[MemorySetup.ConserveVariable]);

		[Theory]
		[InlineData(Collector.Workstation, 0, "workstation · conserve memory 0")]
		[InlineData(Collector.ServerDatas, 5, "server · DATAS on · conserve memory 5")]
		[InlineData(Collector.ServerClassic, 9, "server · DATAS off · conserve memory 9")]
		public void Describe_NamesTheSetup(Collector collector, int conserve, string expected) =>
			Assert.Equal(expected, new MemorySetup(collector, conserve).Describe());

		[Fact]
		public void Default_IsServerWithDatasWithoutConserving()
		{
			Assert.Equal(new MemorySetup(Collector.ServerDatas, 0), MemorySetup.Default);
			Assert.True(MemorySetup.Default.IsDefault);
			Assert.False(new MemorySetup(Collector.Workstation, 0).IsDefault);
		}

		[Fact]
		public void InEffect_TakesTheCollectorFromTheRuntime()
		{
			var now = MemorySetup.InEffect();
			Assert.Equal(GCSettings.IsServerGC, now.Collector != Collector.Workstation);
			Assert.InRange(now.ConserveMemory, 0, MemorySetup.MaxConserveMemory);
		}

		[Fact]
		public void RestartInfo_StartsTheSameProgramWithTheVariables()
		{
			var info = new MemorySetup(Collector.ServerClassic, 3).RestartInfo("water-app");

			Assert.Equal("water-app", info.FileName);
			Assert.False(info.UseShellExecute);
			Assert.Equal("1", info.Environment[MemorySetup.ServerVariable]);
			Assert.Equal("0", info.Environment[MemorySetup.DatasVariable]);
			Assert.Equal("3", info.Environment[MemorySetup.ConserveVariable]);
		}

		[Fact]
		public void MemoryReading_GivesBothFigures_AfterACleanUp()
		{
			var keep = new byte[4 * 1024 * 1024];
			GC.Collect();
			var now = MemoryReading.Now();
			GC.KeepAlive(keep);

			Assert.True(now.HeldBytes > 0);
			Assert.True(now.AfterCleanUpBytes >= keep.Length);
		}

		[Theory]
		[InlineData("0", 0L)]
		[InlineData(" 9 ", 9L)]
		[InlineData("a", 10L)]
		[InlineData("", null)]
		[InlineData(null, null)]
		[InlineData("ice", null)]
		[InlineData("0xC0000000", 3221225472L)]
		public void Number_ReadsAStartUpValueAsHex(string? value, long? expected) =>
			Assert.Equal(expected, MemorySetup.Number(value));
	}
}
