namespace CrawlerSuite.Core.Quality
{
	using System.Globalization;

	/// <summary>
	/// The Unicode line-breaking behaviour <see cref="NoBreakChars"/> needs, as an
	/// authored table.
	///
	/// WHY A TABLE AT ALL: .NET exposes no Line_Break property, and the behaviour
	/// reported here is defined by that property rather than by anything derivable
	/// from <see cref="UnicodeCategory"/> — see the warning on
	/// <see cref="AllowsBreakBeforeGlue"/>.
	///
	/// SCOPE — DELIBERATELY NARROW, AND NOT COMPLETE. This is not a transcription of
	/// the Unicode Character Database. It is the set of characters that occur in the
	/// scripts this tool is used against: Latin, Cyrillic, Greek and Arabic. Checked
	/// against those blocks, Greek, Cyrillic and Arabic contribute NO characters of
	/// class BA at all, so everything below is script-neutral white space and
	/// punctuation plus the hyphens.
	///
	/// Everything else was dropped on purpose: Devanagari, Thai, Khmer and Myanmar
	/// dandas, Tibetan, Mongolian and Ethiopic marks, the Brahmic and historic
	/// scripts, and the editorial/medieval punctuation of the Supplemental
	/// Punctuation block. Each is genuinely class BA; none can occur in the content
	/// this runs against.
	///
	/// WHAT THAT COSTS, STATED PLAINLY: a dropped character is a SILENT MISS. Nothing
	/// else in the tool reports it — a no-break space is not a word, so the spell
	/// checker never sees it, and the invisible-character checks cover zero-width and
	/// bidi characters, not this. Pointing this tool at Thai or Devanagari content
	/// means adding the characters you need here: one line each.
	///
	/// SAFE DIRECTION: narrowing this table can only make <see cref="NoBreakChars"/>
	/// report LESS. Adding a character that does not belong would invent findings;
	/// omitting one can only lose them.
	///
	/// REFERENCE: Unicode Standard Annex #14, "Unicode Line Breaking Algorithm",
	/// rules LB12a and LB18; property values per LineBreak.txt in the Unicode
	/// Character Database. Implemented from the public specification, exactly as the
	/// EXIF and IPTC readers are — no third-party component is bundled or required.
	///
	/// VERSION SENSITIVITY: Unicode 17.0 introduced class HH ("Unambiguous Hyphen")
	/// and moved U+2010, U+2012 and U+2013 into it from class BA. Behaviour did not
	/// change — HH is referenced by rule LB12a exactly as BA is — but code testing
	/// for the literal class name "BA" silently stopped matching an en dash, which is
	/// the most common case in real content. This file therefore exposes BEHAVIOUR
	/// predicates rather than class names, and LineBreakDataTests pins the characters
	/// that matter so a future revision cannot quietly regress them.
	/// </summary>
	internal static class LineBreakData
	{
		/// <summary>UCD version the values below were read against.</summary>
		internal const string UnicodeVersion = "17.0.0";

		/// <summary>
		/// Glue (class GL) characters an author plausibly inserted to hold two words
		/// together. Narrower than class GL, which also contains combining double
		/// diacritics, Tibetan delimiters and Egyptian hieroglyph joiners — all glue,
		/// none of them anybody typing "do not wrap here".
		/// </summary>
		internal static bool IsAuthorGlue(char c) =>
			c == '\u00A0'     // NO-BREAK SPACE          — the editor toolbar button
			|| c == '\u202F'  // NARROW NO-BREAK SPACE   — locale autocorrect inserts this
			|| c == '\u2007'  // FIGURE SPACE            — digit-width, used inside numbers
			|| c == '\u2011'; // NON-BREAKING HYPHEN

		/// <summary>
		/// True when a line break IS permitted between this character and a glue
		/// character following it — the LB12a exception set (classes SP, BA, HH, HY).
		/// A glue character in that position cannot prevent the break, and on wrap it
		/// is carried to the start of the next line as visible white space.
		///
		/// DO NOT REPLACE THIS WITH A General_Category TEST. Dash_Punctuation spans
		/// seven line break classes and is wrong in both directions:
		///   Pd but must NOT match — U+2014 EM DASH (B2), U+2015 HORIZONTAL BAR (AI),
		///     U+301C WAVE DASH (NS), U+2E1A HYPHEN WITH DIAERESIS (AL),
		///     U+2011 NON-BREAKING HYPHEN (GL), U+1806 MONGOLIAN TODO SOFT HYPHEN (BB)
		///   not Pd but MUST match — U+00AD SOFT HYPHEN (Cf), U+0009 TAB (Cc),
		///     U+2009 THIN SPACE and the other breaking spaces (Zs)
		///
		/// On U+003D EQUALS SIGN and the other ASCII symbols: they are class AL by
		/// default, so glue after them is protective and correctly NOT reported. UAX
		/// #14 names '=' as the standard example of a character an implementation may
		/// TAILOR to class BA, under which the verdict would flip. Its absence here is
		/// deliberate, not an oversight.
		/// </summary>
		internal static bool AllowsBreakBeforeGlue(char c) =>
			// SP — the only member of its class.
			c == '\u0020'
			// HY — the only member of its class.
			|| c == '\u002D'
			// HH (Unicode 17.0; class BA in 16.0 and earlier).
			|| c == '\u2010'                     // HYPHEN
			|| c == '\u2012'                     // FIGURE DASH
			|| c == '\u2013'                     // EN DASH
			// BA — the members reachable in Latin / Cyrillic / Greek / Arabic text.
			|| c == '\u0009'                     // TAB
			|| c == '\u007C'                     // VERTICAL LINE — common web separator
			|| c == '\u00AD'                     // SOFT HYPHEN
			|| (c >= '\u2000' && c <= '\u2006')  // EN QUAD .. SIX-PER-EM SPACE
			|| (c >= '\u2008' && c <= '\u200A')  // PUNCTUATION SPACE .. HAIR SPACE
			|| c == '\u2027'                     // HYPHENATION POINT
			|| c == '\u205F';                    // MEDIUM MATHEMATICAL SPACE

		/// <summary>
		/// True for the members of the LB12a exception set that are SPACE-SHAPED —
		/// an ordinary space, a tab, or one of the breaking spaces. Glue next to one
		/// of these produces a VISIBLE double space, which is a different defect from
		/// glue after a dash: the reader sees it without resizing anything, and the
		/// fix is to delete a character rather than to reposition one.
		///
		/// The split exists because the two need different instructions. Rules LB12a
		/// and LB18 cut across the symptom rather than along it, so the issue types
		/// follow what the author sees, not which rule fired.
		/// </summary>
		internal static bool IsSpaceLike(char c) =>
			c == '\u0020'                        // SPACE
			|| c == '\u0009'                     // TAB
			|| (c >= '\u2000' && c <= '\u2006')  // EN QUAD .. SIX-PER-EM SPACE
			|| (c >= '\u2008' && c <= '\u200A')  // PUNCTUATION SPACE .. HAIR SPACE
			|| c == '\u205F';                    // MEDIUM MATHEMATICAL SPACE

		/// <summary>
		/// True for characters rule LB9 makes TRANSPARENT: a combining mark or
		/// zero-width joiner takes the line-breaking behaviour of the character BEFORE
		/// it. A backwards scan asking "what precedes this glue character" must skip
		/// them, or it reads an invisible mark and returns the wrong verdict.
		///
		/// Format characters are class CM by default, but several are not and are
		/// excluded explicitly: soft hyphen is BA, word joiner and BOM are WJ,
		/// zero-width space is ZW, Mongolian vowel separator is GL, and the Arabic
		/// number-sign group is NU/AL.
		/// </summary>
		internal static bool IsTransparent(char c)
		{
			if (c == '\u00AD' || c == '\u2060' || c == '\uFEFF'
				|| c == '\u200B' || c == '\u180E' || c == '\u070F' || c == '\u08E2'
				|| (c >= '\u0600' && c <= '\u0605') || c == '\u06DD')
			{
				return false;
			}

			var category = CharUnicodeInfo.GetUnicodeCategory(c);
			return category == UnicodeCategory.NonSpacingMark
				|| category == UnicodeCategory.EnclosingMark
				|| category == UnicodeCategory.SpacingCombiningMark
				|| category == UnicodeCategory.Format;
		}

		/// <summary>
		/// Operator-facing name for a codepoint, e.g. "U+2013 EN DASH", used in the
		/// finding Detail so a reader can identify a character they cannot see.
		/// Covers every character this check can report; anything else falls back to
		/// the bare codepoint.
		/// </summary>
		internal static string Describe(char c)
		{
			var name = c switch
			{
				'\u0009' => "TAB",
				'\u0020' => "SPACE",
				'\u002D' => "HYPHEN-MINUS",
				'\u007C' => "VERTICAL LINE",
				'\u00A0' => "NO-BREAK SPACE",
				'\u00AD' => "SOFT HYPHEN",
				'\u2000' => "EN QUAD",
				'\u2001' => "EM QUAD",
				'\u2002' => "EN SPACE",
				'\u2003' => "EM SPACE",
				'\u2004' => "THREE-PER-EM SPACE",
				'\u2005' => "FOUR-PER-EM SPACE",
				'\u2006' => "SIX-PER-EM SPACE",
				'\u2007' => "FIGURE SPACE",
				'\u2008' => "PUNCTUATION SPACE",
				'\u2009' => "THIN SPACE",
				'\u200A' => "HAIR SPACE",
				'\u2010' => "HYPHEN",
				'\u2011' => "NON-BREAKING HYPHEN",
				'\u2012' => "FIGURE DASH",
				'\u2013' => "EN DASH",
				'\u2027' => "HYPHENATION POINT",
				'\u202F' => "NARROW NO-BREAK SPACE",
				'\u205F' => "MEDIUM MATHEMATICAL SPACE",
				_ => null,
			};

			return name is null
				? $"U+{(int)c:X4}"
				: $"U+{(int)c:X4} {name}";
		}
	}
}
