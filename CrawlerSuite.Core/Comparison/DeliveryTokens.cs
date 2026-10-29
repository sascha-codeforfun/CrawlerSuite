namespace CrawlerSuite.Core.Comparison
{
	using System.Text.RegularExpressions;

	/// <summary>
	/// The per-delivery tokens the comparison sets aside: values a page carries freshly random on
	/// every delivery, so that a script cannot fill in its forms — the names of honeypot and guard
	/// fields, of the form itself, and the scripts that refer to them. They never carry content,
	/// so two deliveries of an unchanged page differ only in them. Only these forms:
	///   * 16 letters with their case scattered at random ("QwErTyIcEpLaNkSa"): at least 4 capitals,
	///     4 small letters and 6 changes between them — a word or a camelCase name
	///     ("checkFirstSubmit") changes case only where a part begins;
	///   * 16 small hex digits as the value of name, id or for, or in document.forms['…']
	///     ("3fa9c0b17de24e65"), with at least two digits and two letters.
	/// For the comparison only, like <see cref="CacheBusters"/>: the pages themselves, the prune and
	/// every check see the tokens as they are.
	/// </summary>
	public static class DeliveryTokens
	{
		/// <summary>What a set-aside token becomes.</summary>
		public const string Placeholder = "%";

		private const int Length = 16;

		// Plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly Regex Letters = new(
			@"(?<![A-Za-z0-9_$-])[A-Za-z]{16}(?![A-Za-z0-9_$-])",
			RegexOptions.CultureInvariant | RegexOptions.Compiled);

		private static readonly Regex HexName = new(
			@"(?<=\b(?:name|id|for)\s*=\s*[""'])[0-9a-f]{16}(?=[""'])|(?<=\bforms\[[""'])[0-9a-f]{16}(?=[""']\])",
			RegexOptions.CultureInvariant | RegexOptions.Compiled);

		/// <summary>The text with every per-delivery token replaced by <see cref="Placeholder"/>.</summary>
		public static string Neutral(string text)
		{
			var lettersOut = Letters.Replace(text, m => IsRandomLetters(m.Value) ? Placeholder : m.Value);
			return HexName.Replace(lettersOut, m => IsHexName(m.Value) ? Placeholder : m.Value);
		}

		/// <summary>True for 16 letters whose case is scattered at random (see the class).</summary>
		public static bool IsRandomLetters(string word)
		{
			if (word.Length != Length)
			{
				return false;
			}

			int upper = 0, lower = 0, changes = 0;
			for (int i = 0; i < word.Length; i++)
			{
				char c = word[i];
				if (!char.IsAsciiLetter(c))
				{
					return false;
				}

				if (char.IsAsciiLetterUpper(c))
				{
					upper++;
				}
				else
				{
					lower++;
				}

				if (i > 0 && char.IsAsciiLetterUpper(c) != char.IsAsciiLetterUpper(word[i - 1]))
				{
					changes++;
				}
			}

			return upper >= 4 && lower >= 4 && changes >= 6;
		}

		/// <summary>True for 16 small hex digits with at least two digits and two letters.</summary>
		public static bool IsHexName(string value)
		{
			if (value.Length != Length)
			{
				return false;
			}

			int digits = 0, letters = 0;
			foreach (var c in value)
			{
				if (char.IsAsciiDigit(c))
				{
					digits++;
				}
				else if (c is >= 'a' and <= 'f')
				{
					letters++;
				}
				else
				{
					return false;
				}
			}

			return digits >= 2 && letters >= 2;
		}

		/// <summary>True when a piece of a line, on its own, is a per-delivery token (for not marking it).</summary>
		internal static bool IsOne(string piece) => IsRandomLetters(piece) || IsHexName(piece);
	}
}
