using Xunit;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>The gate that paces a crawl and holds it back while a server pushes back: ceiling, pause, probe, ramp.</summary>
	public class ServerGateTests
	{
		private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

		private static DateTime At(double seconds) => T0.AddSeconds(seconds);

		private static string Address(int i) => $"https://site.test/ice{i}.html";

		private static ServerGate.Ticket Go(ServerGate gate, DateTime now, string? address = null)
		{
			var answer = gate.Enter(now, address ?? Address(0));
			Assert.NotNull(answer.Ticket);
			return answer.Ticket!.Value;
		}

		/// <summary>One failing request to each of <paramref name="count"/> different addresses at <paramref name="at"/>; true when one of them started a pause.</summary>
		private static bool FailMany(ServerGate gate, int count, DateTime at, int first = 0)
		{
			bool paused = false;
			for (int i = first; i < first + count; i++)
			{
				paused |= gate.Failed(Go(gate, at, Address(i)), at, Address(i));
			}

			return paused;
		}

		[Fact]
		public void AFewFailures_DoNotPause()
		{
			var gate = new ServerGate();
			Assert.False(FailMany(gate, 7, At(0)));
			Assert.Equal(TimeSpan.Zero, gate.Enter(At(1), Address(99)).Wait);
			Assert.Equal(0, gate.Pauses);
		}

		[Fact]
		public void RetriesOfOneAddress_NeverPause()
		{
			var gate = new ServerGate();
			for (int i = 0; i < 40; i++)
			{
				Assert.False(gate.Failed(Go(gate, At(i * 0.1), Address(1)), At(i * 0.1), Address(1)));
			}

			Assert.Equal(0, gate.Pauses);
		}

		[Fact]
		public void FailuresSpreadOverMoreThanTheWindow_DoNotPause()
		{
			var gate = new ServerGate();
			for (int i = 0; i < 20; i++)
			{
				var at = At(i * 2);
				Assert.False(gate.Failed(Go(gate, at, Address(i)), at, Address(i)));
			}
		}

		[Fact]
		public void EightDifferentAddresses_PauseNewRequests_ForThirtySeconds()
		{
			var gate = new ServerGate();
			Assert.True(FailMany(gate, 8, At(0)));
			Assert.Equal(1, gate.Pauses);

			var held = gate.Enter(At(10), Address(50));
			Assert.Null(held.Ticket);
			Assert.Equal(TimeSpan.FromSeconds(20), held.Wait);
			Assert.Equal(At(30), gate.PausedUntil(At(10)));
		}

		[Fact]
		public void AfterThePause_OneProbeGoesAlone_ToAnAddressThatHasNotFailed()
		{
			var gate = new ServerGate();
			FailMany(gate, 8, At(0));

			// An address that failed asks first: it waits, a fresh one gets the probe.
			Assert.Null(gate.Enter(At(30), Address(3)).Ticket);
			var probe = Go(gate, At(30.1), Address(50));
			Assert.True(probe.Probe);

			var waiting = gate.Enter(At(30.2), Address(51));
			Assert.Null(waiting.Ticket);
			Assert.Equal(ServerGate.Settings.Default.ProbePoll, waiting.Wait);
		}

		[Fact]
		public void WhenOnlyFailedAddressesAsk_OneOfThemProbesAfterAMoment_AndItsFailureEndsThePause()
		{
			var gate = new ServerGate();
			FailMany(gate, 8, At(0));

			Assert.Null(gate.Enter(At(30), Address(2)).Ticket);
			var probe = Go(gate, At(32.5), Address(2));
			Assert.True(probe.Probe);

			// Address 2 is broken, the server is not: no second pause, requests go on.
			Assert.False(gate.Failed(probe, At(33), Address(2)));
			Assert.Equal(1, gate.Pauses);
			Assert.Equal(TimeSpan.Zero, gate.Enter(At(33), Address(60)).Wait);
		}

		[Fact]
		public void AnAnsweredProbe_ResumesSlowly_ThenFreely()
		{
			var gate = new ServerGate();
			FailMany(gate, 8, At(0));
			gate.Answered(Go(gate, At(30), Address(50)), At(30.5));

			// Two per second at first: the second request waits half a second.
			Assert.Equal(TimeSpan.Zero, gate.Enter(At(30.5), Address(51)).Wait);
			Assert.Equal(TimeSpan.FromSeconds(0.5), gate.Enter(At(30.5), Address(52)).Wait);

			// Doubling every 5 s, the limit falls away at 64 per second (after 25 s).
			Assert.Equal(TimeSpan.Zero, gate.Enter(At(70), Address(53)).Wait);
			Assert.Equal(TimeSpan.Zero, gate.Enter(At(70), Address(54)).Wait);
		}

		[Fact]
		public void AFailedProbeToAFreshAddress_PausesAgain_Longer_UpToFiveMinutes()
		{
			var gate = new ServerGate();
			FailMany(gate, 8, At(0));
			var now = 30.0;
			int fresh = 100;
			foreach (var expected in new[] { 60, 120, 240, 300, 300 })
			{
				var probe = Go(gate, At(now), Address(fresh));
				Assert.True(probe.Probe);
				Assert.True(gate.Failed(probe, At(now), Address(fresh)));
				Assert.Equal(TimeSpan.FromSeconds(expected), gate.Enter(At(now), Address(fresh + 1)).Wait);
				now += expected;
				fresh += 2;
			}
		}

		[Fact]
		public void RequestsSentBeforeThePause_DoNotStartAnother()
		{
			var gate = new ServerGate();
			var early = Enumerable.Range(0, 20).Select(i => (Ticket: Go(gate, At(0), Address(i)), Address: Address(i))).ToList();
			foreach (var (ticket, address) in early.Take(8))
			{
				gate.Failed(ticket, At(1), address);
			}

			gate.Answered(Go(gate, At(31), Address(50)), At(31));

			// The rest of the early ones fail late (a stall shows after 30 s): no new pause.
			foreach (var (ticket, address) in early.Skip(8))
			{
				Assert.False(gate.Failed(ticket, At(32), address));
			}

			Assert.Equal(1, gate.Pauses);
		}

		[Fact]
		public void TheCeiling_SpacesRequestsEvenly()
		{
			var gate = new ServerGate(requestsPerSecond: 50);
			var waits = Enumerable.Range(0, 5).Select(i => gate.Enter(At(0), Address(i)).Wait.TotalMilliseconds).ToList();
			Assert.Equal([0, 20, 40, 60, 80], waits.Select(w => Math.Round(w)).ToList());

			// A request after a quiet second goes at once.
			Assert.Equal(TimeSpan.Zero, gate.Enter(At(2), Address(9)).Wait);
		}

		[Fact]
		public void Resuming_RampsUpToTheCeiling_NotBeyond()
		{
			var gate = new ServerGate(requestsPerSecond: 10);
			FailMany(gate, 8, At(0));
			gate.Answered(Go(gate, At(30), Address(50)), At(30));

			// Long after the ramp: still 10 per second.
			Assert.Equal(TimeSpan.Zero, gate.Enter(At(100), Address(51)).Wait);
			Assert.Equal(100, Math.Round(gate.Enter(At(100), Address(52)).Wait.TotalMilliseconds));
		}
	}
}
