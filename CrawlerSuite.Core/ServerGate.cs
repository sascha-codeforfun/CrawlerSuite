namespace CrawlerSuite.Core
{
	using System;
	using System.Collections.Generic;

	/// <summary>
	/// Paces a crawl's requests to one server and holds them back when the server pushes back.
	///
	/// Pace: never more than the ceiling (<c>MaxRequestsPerSecond</c>; 0 = none) — requests get
	/// evenly spaced slots.
	///
	/// Pushback: connection-level failures (refused, TLS failed, no answer, stalled) and the
	/// answers that ask to be left alone (429, 502, 503, 504) — not a 500, which is that address's
	/// own answer — pause every new request to the server when they come from 8 different
	/// addresses within 10 s. Retries of one address never add up to a pause. The pause starts at
	/// 30 s and doubles each time the server is still not back, up to 5 minutes. When it ends, one
	/// request goes out alone as a probe — one to an address that has not failed, if one asks
	/// within a moment. If it is answered, requests resume at 2 per second, doubling every 5 s up
	/// to the ceiling; if not, the next pause is longer. A probe that can only go to an address
	/// that failed before shows that address broken, not the server: it ends the pause instead of
	/// lengthening it. A request that left before a pause began cannot cause another one.
	///
	/// The state is advanced only through <see cref="Enter"/>, <see cref="Answered"/> and
	/// <see cref="Failed"/>, each given the current time, so it is the same in a test as in a run.
	/// </summary>
	internal sealed class ServerGate
	{
		/// <summary>How a gate reacts; <see cref="Default"/> in a run, shorter in tests.</summary>
		internal sealed record Settings(int Threshold, TimeSpan Window, TimeSpan FirstPause, TimeSpan LongestPause,
			double StartRate, double FreeRate, TimeSpan RampStep, TimeSpan ProbePoll, TimeSpan ProbeGrace)
		{
			internal static readonly Settings Default = new(
				Threshold: 8,
				Window: TimeSpan.FromSeconds(10),
				FirstPause: TimeSpan.FromSeconds(30),
				LongestPause: TimeSpan.FromMinutes(5),
				StartRate: 2,
				FreeRate: 64,
				RampStep: TimeSpan.FromSeconds(5),
				ProbePoll: TimeSpan.FromMilliseconds(250),
				ProbeGrace: TimeSpan.FromSeconds(2));
		}

		/// <summary>Addresses remembered as failed; past this many, the oldest are forgotten.</summary>
		private const int FailedMemory = 10_000;

		private readonly Settings settings;
		private readonly double ceiling;
		private readonly object sync = new();
		private readonly Queue<(DateTime At, string Address)> failures = new();
		private readonly Dictionary<string, int> failuresByAddress = new(StringComparer.Ordinal);
		private readonly HashSet<string> failedBefore = new(StringComparer.Ordinal);
		private readonly Queue<string> failedOrder = new();
		private DateTime pausedUntil = DateTime.MinValue;
		private DateTime resumedAt = DateTime.MinValue;
		private TimeSpan nextPause;
		private bool probeDue;
		private bool probing;
		private double ramp; // requests per second while resuming; 0 = resumed fully
		private DateTime rampSince;
		private DateTime nextSlot = DateTime.MinValue;

		/// <param name="requestsPerSecond">The ceiling; 0 = none.</param>
		internal ServerGate(Settings? settings = null, double requestsPerSecond = 0)
		{
			this.settings = settings ?? Settings.Default;
			ceiling = Math.Max(0, requestsPerSecond);
			nextPause = this.settings.FirstPause;
		}

		/// <summary>Pauses so far.</summary>
		internal int Pauses { get; private set; }

		/// <summary>A pass through the gate: when it was taken, and whether it is the probe.</summary>
		internal readonly record struct Ticket(DateTime At, bool Probe);

		/// <summary>What the gate says to a request that wants to go: wait this long first (then ask again), or go with this ticket after the wait.</summary>
		internal readonly record struct Answer(TimeSpan Wait, Ticket? Ticket);

		/// <summary>Until when new requests are held (null when they are not).</summary>
		internal DateTime? PausedUntil(DateTime now)
		{
			lock (sync)
			{
				return now < pausedUntil ? pausedUntil : null;
			}
		}

		internal Answer Enter(DateTime now, string address)
		{
			lock (sync)
			{
				if (now < pausedUntil)
				{
					return new Answer(pausedUntil - now, null);
				}

				if (probing)
				{
					return new Answer(settings.ProbePoll, null);
				}

				if (probeDue)
				{
					// The probe should ask about the server, not about an address known to fail.
					if (failedBefore.Contains(address) && now - pausedUntil < settings.ProbeGrace)
					{
						return new Answer(settings.ProbePoll, null);
					}

					probeDue = false;
					probing = true;
					return new Answer(TimeSpan.Zero, new Ticket(now, Probe: true));
				}

				var rate = Rate(now);
				if (rate > 0)
				{
					var slot = nextSlot > now ? nextSlot : now;
					nextSlot = slot + TimeSpan.FromSeconds(1 / rate);
					return new Answer(slot - now, new Ticket(slot, Probe: false));
				}

				return new Answer(TimeSpan.Zero, new Ticket(now, Probe: false));
			}
		}

		/// <summary>The pace now: the ceiling, or the resuming ramp when that is lower; 0 = no limit.</summary>
		private double Rate(DateTime now)
		{
			while (ramp > 0 && now - rampSince >= settings.RampStep)
			{
				rampSince += settings.RampStep;
				ramp *= 2;
				if (ramp >= settings.FreeRate || (ceiling > 0 && ramp >= ceiling))
				{
					ramp = 0;
					nextPause = settings.FirstPause; // through a full ramp: the server is back for good
				}
			}

			return ramp > 0 && (ceiling == 0 || ramp < ceiling) ? ramp : ceiling;
		}

		/// <summary>The server answered: any status but 429, 502, 503 or 504.</summary>
		internal void Answered(Ticket ticket, DateTime now)
		{
			lock (sync)
			{
				if (!ticket.Probe)
				{
					return;
				}

				probing = false;
				resumedAt = now;
				ramp = settings.StartRate;
				rampSince = now;
				nextSlot = now;
			}
		}

		/// <summary>The request to <paramref name="address"/> failed at the connection level, or was pushed back (429, 502, 503, 504). True when this starts a pause.</summary>
		internal bool Failed(Ticket ticket, DateTime now, string address)
		{
			lock (sync)
			{
				bool before = failedBefore.Contains(address);
				Remember(address);

				if (ticket.Probe)
				{
					probing = false;
					if (before)
					{
						// Only an address known to fail was left to ask: it is broken, the server is not.
						resumedAt = now;
						return false;
					}

					return StartPause(now);
				}

				// Sent before the last pause ended: it belongs to the trouble already dealt with.
				if (ticket.At < resumedAt || now < pausedUntil || probing || probeDue)
				{
					return false;
				}

				failures.Enqueue((now, address));
				failuresByAddress[address] = failuresByAddress.GetValueOrDefault(address) + 1;
				while (failures.Count > 0 && now - failures.Peek().At > settings.Window)
				{
					var old = failures.Dequeue();
					if (--failuresByAddress[old.Address] == 0)
					{
						failuresByAddress.Remove(old.Address);
					}
				}

				return failuresByAddress.Count >= settings.Threshold && StartPause(now);
			}
		}

		private void Remember(string address)
		{
			if (!failedBefore.Add(address))
			{
				return;
			}

			failedOrder.Enqueue(address);
			if (failedOrder.Count > FailedMemory)
			{
				failedBefore.Remove(failedOrder.Dequeue());
			}
		}

		private bool StartPause(DateTime now)
		{
			failures.Clear();
			failuresByAddress.Clear();
			pausedUntil = now + nextPause;
			resumedAt = pausedUntil;
			nextPause = nextPause * 2 > settings.LongestPause ? settings.LongestPause : nextPause * 2;
			probeDue = true;
			ramp = 0;
			Pauses++;
			return true;
		}

		/// <summary>The length the pause just started has (for the log).</summary>
		internal TimeSpan PauseLength(DateTime now)
		{
			lock (sync)
			{
				return pausedUntil > now ? pausedUntil - now : TimeSpan.Zero;
			}
		}
	}
}
