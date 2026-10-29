using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CrawlerSuite.Core.Findings;
using CrawlerSuite.Core.Triage;

namespace CrawlerSuite.Core.Tests.Text
{
	/// <summary>
	/// Every English text the app can write into a handed-out document, gathered from where it lives:
	/// the cards (by reflection over the catalog), the names of defects and their words, and the
	/// texts the code passes to the document text table (read from the source files, like the output
	/// files guard). Each needs its German in Text/de.json.
	/// </summary>
	internal static class DocumentKeys
	{
		/// <summary>The project folder, found from the test's working folder upwards.</summary>
		internal static string Root()
		{
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			{
				var core = Path.Combine(dir.FullName, "CrawlerSuite.Core");
				if (Directory.Exists(core) && File.Exists(Path.Combine(core, "Text", "DocumentText.cs")))
				{
					return dir.FullName;
				}

				var nested = Path.Combine(dir.FullName, "CrawlerSuite", "CrawlerSuite.Core", "Text", "DocumentText.cs");
				if (File.Exists(nested))
				{
					return Path.Combine(dir.FullName, "CrawlerSuite");
				}
			}

			throw new DirectoryNotFoundException("CrawlerSuite.Core not found above " + AppContext.BaseDirectory);
		}

		/// <summary>Every card of the catalog, wherever it is kept (a field, a table, a record holding one).</summary>
		internal static IReadOnlyList<FindingBasis> Cards()
		{
			var found = new List<FindingBasis>();
			void Take(object? value, int depth)
			{
				switch (value)
				{
					case null:
						return;
					case FindingBasis basis:
						found.Add(basis);
						return;
					case string:
						return;
					// A table of (title, card) pairs: its cards count too (the invisible-character causes, for one).
					case System.Runtime.CompilerServices.ITuple tuple when depth < 3:
						for (int i = 0; i < tuple.Length; i++)
						{
							Take(tuple[i], depth + 1);
						}

						return;
					case IDictionary dictionary when depth < 3:
						foreach (var v in dictionary.Values)
						{
							Take(v, depth + 1);
						}

						return;
					case IEnumerable list when depth < 3:
						foreach (var v in list)
						{
							Take(v, depth + 1);
						}

						return;
				}

				if (depth < 3 && value.GetType().Namespace?.StartsWith("CrawlerSuite", StringComparison.Ordinal) == true)
				{
					foreach (var p in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
					{
						if (p.PropertyType == typeof(FindingBasis) && p.GetIndexParameters().Length == 0)
						{
							Take(p.GetValue(value), depth + 1);
						}
					}
				}
			}

			foreach (var field in typeof(FindingsCatalog).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				Take(field.GetValue(null), 0);
			}

			return found.Distinct().ToList();
		}

		/// <summary>The English texts of the cards: text, requirement and link titles, and the legal note with its links.</summary>
		internal static IEnumerable<string> CardTexts()
		{
			foreach (var card in Cards())
			{
				yield return card.Text;
				if (card.Requirement is { Length: > 0 } requirement)
				{
					yield return requirement;
				}

				foreach (var (label, _) in card.Links)
				{
					yield return label;
				}
			}

			yield return FindingBasis.LegalNote;
			yield return FindingBasis.RequirementLabel;
			yield return FindingBasis.WcagLabel;
			foreach (var (label, _) in FindingBasis.LegalLinks)
			{
				yield return label;
			}
		}

		/// <summary>The names a document shows: families and switches, and the fixed words of logs 07 and 38.</summary>
		internal static IEnumerable<string> Names()
		{
			foreach (var s in TriageSwitches.All)
			{
				yield return s.Family;
				yield return s.Label;
			}

			yield return BrokenLinkSource.Family;
			yield return "Other";
			foreach (var word in CrawlerSuite.Core.Links.BrokenLinkOrigins.DocumentWords)
			{
				yield return word;
			}

			yield return "never requested (dead reference)";
			yield return CrawlerSuite.Core.Display.QualityMarks.NoPageLanguage;
		}

		// T(l, "…"), F(language, "…"), DocumentText.T(DocumentLanguage.German, "…"), L("…"): a call whose text is a literal,
		// possibly several joined with +.
		private static readonly Regex Call = new(@"\b(?:T|F|L)\(\s*(?:[\w.]+\s*,\s*)?(?=""|\$?@?"")", RegexOptions.Compiled);
		private static readonly Regex Literal = new(@"\G\s*(\+\s*)?""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
		private static readonly Regex Const = new(@"(?:public|internal|private)\s+const\s+string\s+\w+\s*=\s*((?:""(?:[^""\\]|\\.)*""\s*\+?\s*)+);", RegexOptions.Compiled);

		/// <summary>The files whose calls to the table are read.</summary>
		internal static IEnumerable<string> SourceFiles(string root)
		{
			foreach (var file in Directory.GetFiles(Path.Combine(root, "CrawlerSuite.Core", "Triage"), "*.cs"))
			{
				yield return file;
			}

			yield return Path.Combine(root, "CrawlerSuite.Core", "Links", "BrokenLinkReach.cs");
			yield return Path.Combine(root, "CrawlerSuite", "Views", "TicketDocument.cs");
		}

		/// <summary>The literal texts passed to the table in one source file, and the constants of a ticket's Texts table.</summary>
		internal static IEnumerable<string> FromSource(string file)
		{
			var source = File.ReadAllText(file);
			foreach (Match call in Call.Matches(source))
			{
				var text = new StringBuilder();
				int at = call.Index + call.Length;
				for (var m = Literal.Match(source, at); m.Success && (text.Length == 0 || m.Groups[1].Success); m = Literal.Match(source, at))
				{
					text.Append(Unescape(m.Groups[2].Value));
					at = m.Index + m.Length;
				}

				if (text.Length > 0)
				{
					yield return text.ToString();
				}
			}

			int texts = source.IndexOf("internal static class Texts", StringComparison.Ordinal);
			if (texts >= 0)
			{
				int end = source.IndexOf("\n\t}\n", texts, StringComparison.Ordinal);
				foreach (Match c in Const.Matches(source[texts..end]))
				{
					var parts = Regex.Matches(c.Groups[1].Value, @"""((?:[^""\\]|\\.)*)""").Select(m => Unescape(m.Groups[1].Value));
					var value = string.Concat(parts);
					if (value.Length > 0)
					{
						yield return value;
					}
				}

				// Texts.Wcag is the label itself.
			}

			// Constants passed by name in the ticket: the "as found" and "Offender" prefixes.
			foreach (Match c in Regex.Matches(source, @"const string (?:AsFound|Offender) = ""((?:[^""\\]|\\.)*)"""))
			{
				yield return Unescape(c.Groups[1].Value);
			}
		}

		internal static string Unescape(string literal) =>
			Regex.Replace(literal, @"\\(u[0-9a-fA-F]{4}|.)", m => m.Groups[1].Value switch
			{
				"n" => "\n",
				"t" => "\t",
				"r" => "\r",
				"\"" => "\"",
				"\\" => "\\",
				"0" => "\0",
				var u when u.StartsWith('u') => ((char)Convert.ToInt32(u[1..], 16)).ToString(),
				var other => other,
			});

		/// <summary>Every key, each once, in a stable order.</summary>
		internal static IReadOnlyList<string> All(string root) =>
			CardTexts().Concat(Names()).Concat(SourceFiles(root).SelectMany(FromSource))
				.Where(t => t.Length > 0)
				.Distinct(StringComparer.Ordinal)
				.ToList();
	}
}
