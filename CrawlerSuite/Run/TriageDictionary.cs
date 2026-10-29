using System;
using System.Collections.Generic;
using System.IO;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Lexicon;

namespace CrawlerSuite.Run;

/// <summary>
/// The dictionary choice from a triage spelling item — User or Site — for a word whose call takes
/// its sentence: exactly what the buttons on Spell findings do (the same files, the same backup
/// before the first change, the same one-time question before the shared user dictionary), and
/// remembered there too, so both pages show the word as decided and either can undo it until the
/// next run starts. The word is then right everywhere: every page's item of it is set aside.
/// </summary>
public sealed class TriageDictionary
{
	public static readonly TriageDictionary None = new(string.Empty, string.Empty, string.Empty);

	private readonly string snapshot;
	private readonly string userPath;
	private readonly string sitePath;

	/// <summary>Words added from triage in this session: which dictionary, and the entry the click wrote (empty when it was already there).</summary>
	private readonly Dictionary<string, (bool User, string Path, IReadOnlyList<string> Added)> added = new(StringComparer.Ordinal);

	public TriageDictionary(string snapshot, string userPath, string sitePath)
	{
		this.snapshot = snapshot;
		this.userPath = userPath;
		this.sitePath = sitePath;
	}

	public bool CanWrite => userPath.Length > 0 || sitePath.Length > 0;

	public bool CanWriteUser => userPath.Length > 0;

	public bool CanWriteSite => sitePath.Length > 0;

	/// <summary>Where the word was added from triage in this session: "user", "site", or null.</summary>
	public string? AddedTo(string word) => added.TryGetValue(word, out var a) ? (a.User ? "user" : "site") : null;

	/// <summary>Adds the word to the user or the site dictionary. Null when done or declined (<paramref name="declined"/> says which); else what went wrong.</summary>
	public string? Add(string word, bool user, Func<bool> confirmShared, out bool declined)
	{
		declined = false;
		if (added.ContainsKey(word))
		{
			return null;
		}

		var path = user ? userPath : sitePath;
		if (path.Length == 0)
		{
			return "No dictionary file is known for this.";
		}

		if (user && !SharedDictionaryConsent.Ask(confirmShared))
		{
			declined = true;
			return null;
		}

		try
		{
			IReadOnlyList<string> entries = DictionaryEditor.Add(path, word) ? [word.Trim()] : [];
			added[word] = (user, path, entries);
			SpellFindingsModel.RememberDecision(snapshot, word, path, entries);
			return null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.LogWarning($"Triage: could not add to {Path.GetFileName(path)} — {ex.Message}");
			return ex.Message;
		}
	}

	/// <summary>Takes the click back: removes exactly the entry it wrote, and the word is open again on both pages.</summary>
	public string? Undo(string word)
	{
		if (!added.TryGetValue(word, out var a))
		{
			return null;
		}

		try
		{
			foreach (var entry in a.Added)
			{
				DictionaryEditor.Remove(a.Path, entry);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Logger.LogWarning($"Triage: could not undo in {Path.GetFileName(a.Path)} — {ex.Message}");
			return ex.Message;
		}

		added.Remove(word);
		SpellFindingsModel.ForgetDecision(snapshot, word);
		return null;
	}
}
