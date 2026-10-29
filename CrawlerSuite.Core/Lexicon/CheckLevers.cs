namespace CrawlerSuite.Core.Lexicon
{
	using System;
	using System.Threading;
	using WeCantSpell.Hunspell;

	/// <summary>Whether dictionary checks wait for each other.</summary>
	public enum TakeTurns
	{
		/// <summary>Checks run in parallel (normal).</summary>
		None,

		/// <summary>One check at a time per dictionary; different dictionaries run in parallel.</summary>
		PerLanguage,

		/// <summary>One dictionary check at a time in the whole program.</summary>
		WholeProgram,
	}

	/// <summary>
	/// Test levers for the system-dictionary checks. They exist to compare variants run by run:
	/// held for this session only, never written to the configuration, and back to
	/// <see cref="Default"/> at every start. They apply only in Debug mode; a run takes them as
	/// they are when it loads its dictionaries and reports them in its run steps.
	///
	/// The library has four clocks. When one runs out, the search it limits stops early: the
	/// compound check then calls a valid compound unknown, and the suggestion test that follows a
	/// compound of three or more parts ("is this a misspelt dictionary word?") finds nothing, so a
	/// misspelling is accepted. Either way the verdict depends on how much time the machine gives
	/// the check. All four are off by default.
	///
	/// The character-variant table (MAP) of each .aff is left out by default: in that suggestion
	/// test it makes the work grow exponentially with the word's length (see <see cref="AffixFile"/>).
	/// Kept, with the clocks off, a single word can take minutes.
	/// </summary>
	public sealed record CheckLevers(
		bool CompoundCheckClock,
		bool CompoundSuggestClock,
		bool SuggestStepClock,
		bool SuggestGlobalClock,
		TakeTurns TakeTurns,
		bool CharacterMap = false)
	{
		/// <summary>All clocks off, checks in parallel, the character-variant table left out (see <see cref="AffixFile"/>).</summary>
		public static CheckLevers Default { get; } = new(false, false, false, false, TakeTurns.None, false);

		private static CheckLevers session = Default;

		/// <summary>The levers as set for this session (memory only).</summary>
		public static CheckLevers Session
		{
			get => Volatile.Read(ref session);
			set => Volatile.Write(ref session, value ?? Default);
		}

		/// <summary>The library's own limits, used when a clock is switched on.</summary>
		private static readonly QueryOptions Library = new();

		public bool IsDefault => this == Default;

		/// <summary>The query options these levers stand for. A negative limit switches a clock off.</summary>
		public QueryOptions ToOptions() => new()
		{
			TimeLimitCompoundCheck = CompoundCheckClock ? Library.TimeLimitCompoundCheck : Timeout.InfiniteTimeSpan,
			TimeLimitCompoundSuggest = CompoundSuggestClock ? Library.TimeLimitCompoundSuggest : Timeout.InfiniteTimeSpan,
			TimeLimitSuggestStep = SuggestStepClock ? Library.TimeLimitSuggestStep : Timeout.InfiniteTimeSpan,
			TimeLimitSuggestGlobal = SuggestGlobalClock ? Library.TimeLimitSuggestGlobal : Timeout.InfiniteTimeSpan,
		};

		/// <summary>The library's limit for each clock, for the panel's labels.</summary>
		public static int LibraryMilliseconds(string clock) => clock switch
		{
			nameof(CompoundCheckClock) => (int)Library.TimeLimitCompoundCheck.TotalMilliseconds,
			nameof(CompoundSuggestClock) => (int)Library.TimeLimitCompoundSuggest.TotalMilliseconds,
			nameof(SuggestStepClock) => (int)Library.TimeLimitSuggestStep.TotalMilliseconds,
			nameof(SuggestGlobalClock) => (int)Library.TimeLimitSuggestGlobal.TotalMilliseconds,
			_ => throw new ArgumentOutOfRangeException(nameof(clock)),
		};

		/// <summary>One line for the run steps: every lever, and whether it is the default.</summary>
		public string Describe()
		{
			static string Clock(bool on, string name) => on ? $"{LibraryMilliseconds(name)} ms" : "off";
			string turns = TakeTurns switch
			{
				TakeTurns.PerLanguage => "per language",
				TakeTurns.WholeProgram => "whole program",
				_ => "none",
			};
			return $"compound check clock {Clock(CompoundCheckClock, nameof(CompoundCheckClock))}"
				+ $" · compound suggestion clock {Clock(CompoundSuggestClock, nameof(CompoundSuggestClock))}"
				+ $" · suggestion step clock {Clock(SuggestStepClock, nameof(SuggestStepClock))}"
				+ $" · overall suggestion clock {Clock(SuggestGlobalClock, nameof(SuggestGlobalClock))}"
				+ $" · take turns: {turns}"
				+ $" · character variants (MAP) {(CharacterMap ? "kept" : "left out")}"
				+ (IsDefault ? " (defaults)" : " (changed for this session)");
		}
	}
}
