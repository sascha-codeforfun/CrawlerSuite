using System.Collections;

namespace CrawlerSuite.Core.Lexicon
{
	/// <summary>
	/// The configured dictionaries, each loaded the first time it is asked for. Its keys are every
	/// configured language from the start, so whoever asks whether a language is there gets the same
	/// answer as with all of them loaded; a dictionary is read only when a word is checked in it.
	/// Going through the values loads every dictionary not yet loaded (in parallel) and yields them in
	/// configuration order — a check across all languages sees all of them.
	/// <see cref="Release"/> lets go of every loaded dictionary at the end of a run.
	/// Safe for use from several threads: each dictionary loads once.
	/// </summary>
	public sealed class BundleSet : IReadOnlyDictionary<string, Bundle>
	{
		private readonly List<string> keys;
		private readonly Dictionary<string, Lazy<Bundle>> entries;

		/// <summary>An empty set: no languages.</summary>
		public static BundleSet Empty { get; } = new([], _ => throw new KeyNotFoundException());

		/// <param name="keys">The language codes, in configuration order (case ignored; the first of a repeat counts).</param>
		/// <param name="load">Reads the dictionary of one code; called at most once per code.</param>
		public BundleSet(IEnumerable<string> keys, Func<string, Bundle> load)
		{
			this.keys = [];
			entries = new Dictionary<string, Lazy<Bundle>>(StringComparer.OrdinalIgnoreCase);
			foreach (var key in keys)
			{
				if (entries.ContainsKey(key))
				{
					continue;
				}

				var code = key;
				this.keys.Add(code);
				entries[code] = new Lazy<Bundle>(() => load(code), LazyThreadSafetyMode.ExecutionAndPublication);
			}
		}

		/// <summary>The same languages, each made from this set's dictionary when first asked for.</summary>
		public BundleSet Derive(Func<Bundle, Bundle> make) => new(keys, code => make(this[code]));

		public int Count => keys.Count;

		/// <summary>Every configured language, loaded or not, in configuration order.</summary>
		public IEnumerable<string> Keys => keys;

		/// <summary>The languages whose dictionary is loaded, in configuration order.</summary>
		public IReadOnlyList<string> Loaded => keys.Where(k => entries[k].IsValueCreated).ToList();

		public bool ContainsKey(string key) => entries.ContainsKey(key);

		public Bundle this[string key] => entries[key].Value;

		public bool TryGetValue(string key, out Bundle value)
		{
			if (entries.TryGetValue(key, out var entry))
			{
				value = entry.Value;
				return true;
			}

			value = null!;
			return false;
		}

		/// <summary>Every dictionary, all loaded, in configuration order.</summary>
		public IEnumerable<Bundle> Values
		{
			get
			{
				LoadAll();
				return keys.Select(k => entries[k].Value).ToList();
			}
		}

		public IEnumerator<KeyValuePair<string, Bundle>> GetEnumerator()
		{
			LoadAll();
			return keys.Select(k => new KeyValuePair<string, Bundle>(k, entries[k].Value)).ToList().GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		/// <summary>Loads every dictionary not yet loaded, in parallel; the first failure is thrown as itself.</summary>
		public void LoadAll() => Load(keys);

		/// <summary>
		/// Loads the named dictionaries not yet loaded, in parallel (unknown names are left out), so a
		/// step that needs several does not read them one after another; the first failure is thrown as itself.
		/// </summary>
		public void Load(IEnumerable<string> names)
		{
			var missing = names.Where(k => entries.TryGetValue(k, out var e) && !e.IsValueCreated)
				.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (missing.Count == 0)
			{
				return;
			}

			try
			{
				Parallel.ForEach(missing, k => _ = entries[k].Value);
			}
			catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
			{
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
			}
		}

		/// <summary>Lets go of every loaded dictionary: the set is empty afterwards.</summary>
		public void Release()
		{
			keys.Clear();
			entries.Clear();
		}
	}
}
