using CrawlerSuite.Core.Quality;

namespace CrawlerSuite.Core.Display
{
	/// <summary>How a marked stretch of a context is drawn.</summary>
	public enum MarkRole
	{
		/// <summary>What the finding is about: strong red.</summary>
		Offender,

		/// <summary>A cause or a matching fact worth noticing: amber.</summary>
		Warn,

		/// <summary>An inserted or invisible marker: filled, so a single character cannot be missed.</summary>
		Fill,

		/// <summary>Related, not the offender (an anchor's text, the inside of a split tag): accent tint.</summary>
		Tint,

		/// <summary>
		/// Part of the same defect, not the one that trips it (the other quotes of the finding, other
		/// invisible characters next to the culprit): red text on a light red tint, so single
		/// characters stay legible and the solid red still shows which one is wrong.
		/// </summary>
		Partner,

		/// <summary>The tail that escaped (split word, collision): green.</summary>
		Ok,

		/// <summary>Surrounding markup pushed back so the marks stand out.</summary>
		Dim,

		/// <summary>
		/// The word (or quoted phrase) an offender belongs to: a soft highlighter background, so the
		/// eye finds the word first and the red character in it second. Drawn under every other mark.
		/// </summary>
		Highlight,
	}

	/// <summary>A marked stretch of a context.</summary>
	public sealed record Mark(int Start, int Length, MarkRole Role);

	/// <summary>A context ready to draw: its text (an empty anchor gets a visible marker), the marks, and an optional note.</summary>
	public sealed record MarkedContext(string Text, IReadOnlyList<Mark> Marks, string? Note);

	/// <summary>
	/// The triage's highlighting, per content-quality issue type, for the findings views. Every
	/// type uses the same routine as the triage, so views and triage mark the same
	/// characters; when a position cannot be located, nothing is marked rather than the wrong thing.
	/// </summary>
	public static partial class QualityMarks
	{
		public static MarkedContext For(string issueType, string context, string detail, ContentQualityConfig? config)
		{
			// Several excerpts of one finding (triage merges identical findings on a page, one per
			// line): each line is marked on its own, so every occurrence shows its spot.
			if (context.Contains('\n'))
			{
				var text = new System.Text.StringBuilder();
				var marks = new List<Mark>();
				string? note = null;
				foreach (var line in context.Split('\n'))
				{
					if (text.Length > 0)
					{
						text.Append('\n');
					}

					var one = For(issueType, line, detail, config);
					int offset = text.Length;
					marks.AddRange(one.Marks.Select(m => m with { Start = m.Start + offset }));
					note ??= one.Note;
					text.Append(one.Text);
				}

				return new MarkedContext(text.ToString(), marks, note);
			}

			var marked = ForCore(issueType, context, detail, config);
			return HighlightsWords(issueType) ? WithHighlights(marked, issueType.StartsWith("QUOTE_", StringComparison.Ordinal)) : marked;
		}

		/// <summary>Types whose marks sit on single characters, in or beside a word; the others already mark a whole stretch.</summary>
		private static bool HighlightsWords(string issueType) =>
			issueType.StartsWith("QUOTE_", StringComparison.Ordinal)
			|| issueType is "LIGATURE" or "DECOMPOSED_TEXT_IN_CONTENT" or "MIXED_NORMALIZATION_IN_CONTENT"
				or "CONTROL_CHARS_IN_CONTENT" or "EMBEDDED_BOM_IN_BODY" or "INVISIBLE_CHAR_IN_BODY";

		/// <summary>The longest quoted phrase highlighted as one; a longer one gets only the word at the quote.</summary>
		private const int MaxQuotedPhrase = 120;

		/// <summary>
		/// Adds a highlight under the word each offender touches — the letters right before and after
		/// it. For a quote with its partner nearby, the quoted phrase between them instead.
		/// </summary>
		internal static MarkedContext WithHighlights(MarkedContext marked, bool quotes)
		{
			var text = marked.Text;
			var offenders = marked.Marks.Where(m => m.Role is MarkRole.Offender or MarkRole.Fill or MarkRole.Warn).ToList();
			if (offenders.Count == 0)
			{
				return marked;
			}

			var added = new List<Mark>();
			var quoteMarks = quotes ? marked.Marks.Where(m => m.Role is MarkRole.Tint or MarkRole.Partner or MarkRole.Offender).OrderBy(m => m.Start).ToList() : [];
			foreach (var o in offenders)
			{
				if (quotes && QuotedPhrase(o, quoteMarks) is { } phrase)
				{
					added.Add(phrase);
					continue;
				}

				int left = o.Start;
				while (left > 0 && IsWordChar(text[left - 1]))
				{
					left--;
				}

				int end = o.Start + o.Length;
				int right = end;
				while (right < text.Length && IsWordChar(text[right]))
				{
					right++;
				}

				if (left < o.Start)
				{
					added.Add(new Mark(left, o.Start - left, MarkRole.Highlight));
				}

				if (right > end)
				{
					added.Add(new Mark(end, right - end, MarkRole.Highlight));
				}
			}

			return added.Count == 0
				? marked
				: marked with { Marks = marked.Marks.Concat(added).OrderBy(m => m.Start).ToList() };
		}

		/// <summary>The text between a quote and the nearest other quote, when nothing else lies between and it is short.</summary>
		private static Mark? QuotedPhrase(Mark quote, List<Mark> quoteMarks)
		{
			int at = quoteMarks.IndexOf(quote);
			if (at < 0)
			{
				return null;
			}

			Mark? before = at > 0 ? quoteMarks[at - 1] : null;
			Mark? after = at < quoteMarks.Count - 1 ? quoteMarks[at + 1] : null;
			int beforeGap = before is null ? int.MaxValue : quote.Start - (before.Start + before.Length);
			int afterGap = after is null ? int.MaxValue : after.Start - (quote.Start + quote.Length);
			int gap = Math.Min(beforeGap, afterGap);
			if (gap <= 0 || gap > MaxQuotedPhrase)
			{
				return null;
			}

			return beforeGap <= afterGap
				? new Mark(before!.Start + before.Length, beforeGap, MarkRole.Highlight)
				: new Mark(quote.Start + quote.Length, afterGap, MarkRole.Highlight);
		}

		private static bool IsWordChar(char c) =>
			char.IsLetterOrDigit(c)
			|| char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark;

		/// <summary>A spelling finding: every whole-word occurrence of the flagged word ("word (de)" → word) in red.</summary>
		internal static IEnumerable<Mark> SpelledWord(string context, string detail)
		{
			var word = Triage.SpellingSource.WordOf(detail);
			if (word.Length == 0)
			{
				yield break;
			}

			for (int at = context.IndexOf(word, StringComparison.Ordinal); at >= 0; at = context.IndexOf(word, at + word.Length, StringComparison.Ordinal))
			{
				int end = at + word.Length;
				bool startsWord = at == 0 || !char.IsLetterOrDigit(context[at - 1]);
				bool endsWord = end == context.Length || !char.IsLetterOrDigit(context[end]);
				if (startsWord && endsWord)
				{
					yield return new Mark(at, word.Length, MarkRole.Offender);
				}
			}
		}

		private static MarkedContext ForCore(string issueType, string context, string detail, ContentQualityConfig? config)
		{
			if (string.IsNullOrEmpty(context))
			{
				return new MarkedContext(context ?? string.Empty, [], null);
			}

			try
			{
				return issueType switch
				{
					_ when issueType.StartsWith("QUOTE_", StringComparison.Ordinal) => Quotes(issueType, context, detail, config),
					"LIGATURE" => Plain(context, TriageHighlights.ComputeLigatureSpans(context).Select(s => new Mark(s.Start, s.Length, MarkRole.Offender))),
					"DECOMPOSED_TEXT_IN_CONTENT" => Plain(context, TriageHighlights.ComputeDecompositionSpans(context).Select(s => new Mark(s.Start, s.Length, MarkRole.Offender))),
					"MIXED_NORMALIZATION_IN_CONTENT" => Plain(context, Occurrences(context, TriageHighlights.ExtractDecompositionMarkers(context), MarkRole.Warn)),
					"CONTROL_CHARS_IN_CONTENT" => Plain(context, ByCulprit(Occurrences(context, TriageHighlights.ExtractControlCharMarkers(context), MarkRole.Offender), context, detail)),
					"LINE_BREAK_GLUE_INERT" or "LINE_BREAK_GLUE_DOUBLE_SPACE" => Plain(context, GlueHighlight.ComputeSpans(context).Select(Anchor)),
					"MISPLACED_ANCHOR_EMPTY" or "MISPLACED_ANCHOR_EMPTY_CONDITIONAL" or "MISPLACED_ANCHOR_EMPTY_UNDETERMINED" => EmptyAnchor(context, detail),
					// Hidden wherever it is: nobody misses its text, so no marker and no red.
					"MISPLACED_ANCHOR_EMPTY_CLEARED" => Plain(context, []),
					// Cleared is no violation: no red; the markup that takes it out of the Tab order in green.
					"ARIA_HIDDEN_FOCUSABLE_CLEARED" => Plain(context, Cleared(context)),
					"ARIA_HIDDEN_FOCUSABLE" or "ARIA_HIDDEN_FOCUSABLE_CONDITIONAL" or "ARIA_HIDDEN_FOCUSABLE_UNDETERMINED"
						=> Plain(context, HiddenFocusable(context)),
					"ARIA_REFERENCE_BROKEN" => Plain(context, BrokenReferences(context, detail)),
					"SPLIT_WORD_ANCHOR" => Plain(context, TriageHighlights.ComputeSplitWordSpans(context).Select(Split)),
					"WORD_COLLISION" => Plain(context, TriageHighlights.ComputeWordCollisionSpans(context).Select(Split)),
					"SPACE_AFTER_HYPHEN" or "SPACE_BEFORE_HYPHEN" => Plain(context, HyphenGap(context, issueType, detail)),
					"WORD_DOUBLED" => Plain(context, Doubled(context, detail)),
					"PUNCTUATION_WEAK" => Plain(context, Quality.DoubledWords.Spot(context, detail) is { } lead
						? [new Mark(lead.First.Index, lead.First.Length, MarkRole.Warn), new Mark(lead.Second.Index, lead.Second.Length, MarkRole.Tint)]
						: []),
					"ADJACENT_ANCHOR" => Plain(context, TriageHighlights.ComputeAdjacentHintSpans(context).Select(Adjacent)),
					Triage.SpellingSource.Type => Plain(context, SpelledWord(context, detail)),
					// The link back to the page: its address wherever the excerpt shows it.
					Triage.SelfLinkSource.Type => Plain(context, Occurrences(context, [detail], MarkRole.Offender, ignoreCase: false)),
					"EMBEDDED_BOM_IN_BODY" or "INVISIBLE_CHAR_IN_BODY" when InvisibleMarker().IsMatch(context)
						=> Plain(context, ByCulprit(InvisibleMarker().Each(context).Select(m => new Mark(m.Index, m.Length, MarkRole.Offender)), context, detail)),
					"UNWANTED_PATTERN" => Plain(context, Occurrences(context, TriageHighlights.ExtractHighlightPatterns(detail), MarkRole.Offender, ignoreCase: true)),
					_ => new MarkedContext(context, [], null),
				};
			}
			catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
			{
				// A cut-off excerpt the helper did not expect: show it unmarked.
				return new MarkedContext(context, [], null);
			}
		}

		private static MarkedContext Quotes(string issueType, string context, string detail, ContentQualityConfig? config)
		{
			IReadOnlyList<int> triggers = [];

			// Mixed systems: which one is wrong is decided by the page language the finding names. Without
			// one it cannot be told — every mark is a partner, none is blamed.
			if (issueType == "QUOTE_SYSTEM_MIX")
			{
				var language = Quality.Quotes.PageLanguageIn(detail);
				var at = language is null ? -1 : Quality.Quotes.LocateSystemMixMismatch(context, language);
				triggers = at >= 0 ? [at] : [];
				var spans = TriageHighlights.ComputeQuoteSpans(context, triggers)
					.Select(s => new Mark(s.Start, s.Length, s.IsTrigger ? MarkRole.Offender : MarkRole.Partner))
					.ToList();
				var blamed = TriageHighlights.FormatQuoteOffenders(context, triggers);
				return new MarkedContext(context, spans,
					language is null ? NoPageLanguage : blamed.Length == 0 ? null : "Offender: " + blamed);
			}

			if (config is not null)
			{
				triggers = Locate(null);

				// A quotation opened and closed with the same mark: which end is wrong was decided by the
				// page language, which the excerpt does not carry. The detail names the end; the
				// language that yields that end locates it.
				if (triggers.Count == 0 && detail.Contains(Quality.Quotes.SameMarkNote, StringComparison.Ordinal))
				{
					foreach (var language in Quality.Quotes.QuoteLanguages)
					{
						triggers = Locate(language);
						if (triggers.Count > 0)
						{
							break;
						}
					}
				}
			}

			IReadOnlyList<int> Locate(string? language) => Quality.Quotes.LocateFlags(context, config!, language)
				.Where(f => f.Type.Equals(issueType, StringComparison.Ordinal))
				.Select(f => f.Pos)
				.ToList();

			var marks = TriageHighlights.ComputeQuoteSpans(context, triggers)
				.Select(s => new Mark(s.Start, s.Length, s.IsTrigger ? MarkRole.Offender : MarkRole.Partner))
				.ToList();
			var offenders = TriageHighlights.FormatQuoteOffenders(context, triggers);
			return new MarkedContext(context, marks, offenders.Length == 0 ? null : "Offender: " + offenders);
		}

		/// <summary>The note under a mixed-systems excerpt whose finding names no page language.</summary>
		internal const string NoPageLanguage = "No page language recorded: which quote system is wrong cannot be told.";

		private static MarkedContext EmptyAnchor(string context, string detail)
		{
			var text = TriageHighlights.InjectEmptyAnchorMarker(context, detail);
			return Plain(text, TriageHighlights.ComputeEmptyAnchorSpans(text).Select(Anchor));
		}

		/// <summary>aria-hidden attributes in amber; the opening tags of elements still in the Tab order inside them in red.</summary>
		private static IEnumerable<Mark> HiddenFocusable(string context)
		{
			foreach (System.Text.RegularExpressions.Match m in AriaHiddenTrue().Each(context))
			{
				yield return new Mark(m.Index, m.Length, MarkRole.Warn);
			}

			// A short excerpt the app already cut, not a whole page: no nesting cap needed (HtmlDocuments is for pages).
			var fragment = new HtmlAgilityPack.HtmlDocument();
			fragment.LoadHtml(context);
			foreach (var element in fragment.DocumentNode.Descendants().Where(n => n.NodeType == HtmlAgilityPack.HtmlNodeType.Element))
			{
				if (element.OuterStartIndex >= 0 && element.InnerStartIndex > element.OuterStartIndex
					&& LinkAccessibility.IsFocusable(element) && LinkAccessibility.IsAriaHidden(element))
				{
					// The opening tag only: from "<" to the ">" before the content.
					yield return new Mark(element.OuterStartIndex, element.InnerStartIndex - element.OuterStartIndex, MarkRole.Offender);
				}
			}
		}

		/// <summary>
		/// A cleared candidate: aria-hidden in amber, and what takes the element out of the Tab order
		/// (hidden, inert, disabled, tabindex="-1", an inline display:none …) in green. A candidate
		/// cleared by a stylesheet has its evidence in the detail, not in the excerpt.
		/// </summary>
		private static IEnumerable<Mark> Cleared(string context)
		{
			foreach (System.Text.RegularExpressions.Match m in AriaHiddenTrue().Each(context))
			{
				yield return new Mark(m.Index, m.Length, MarkRole.Warn);
			}

			// A short excerpt the app already cut, not a whole page: no nesting cap needed (HtmlDocuments is for pages).
			var fragment = new HtmlAgilityPack.HtmlDocument();
			fragment.LoadHtml(context);
			foreach (var element in fragment.DocumentNode.Descendants().Where(n => n.NodeType == HtmlAgilityPack.HtmlNodeType.Element))
			{
				if (element.Name is "template" or "noscript" or "details" && element.OuterStartIndex >= 0)
				{
					yield return new Mark(element.OuterStartIndex, element.Name.Length + 1, MarkRole.Ok);
				}

				foreach (var attribute in element.Attributes)
				{
					int at = attribute.StreamPosition;
					if (at < 0 || at >= context.Length)
					{
						continue;
					}

					switch (attribute.Name.ToLowerInvariant())
					{
						case "hidden" or "inert" or "disabled":
							yield return new Mark(at, attribute.Name.Length, MarkRole.Ok);
							break;
						case "tabindex" when attribute.Value.Trim().StartsWith('-'):
						case "style":
							var written = ClearedEvidenceRegex.Match(context, at);
							if (written.Success && written.Index < at + attribute.Name.Length + attribute.Value.Length + 4)
							{
								yield return new Mark(written.Index, written.Length, MarkRole.Ok);
							}

							break;
					}
				}
			}
		}

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly System.Text.RegularExpressions.Regex ClearedEvidenceRegex = new(
			@"tabindex\s*=\s*[""']?\s*-\d+[""']?"
			+ @"|(?<![\w-])display\s*:\s*none|(?<![\w-])visibility\s*:\s*(?:hidden|collapse)|content-visibility\s*:\s*hidden",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

		/// <summary>The missing ids the Detail names, in red, where an aria-labelledby/-describedby value lists them.</summary>
		private static IEnumerable<Mark> BrokenReferences(string context, string detail)
		{
			var missing = QuotedIds().Each(detail.Split(" on <")[0]).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
			foreach (System.Text.RegularExpressions.Match attribute in ReferenceAttribute().Each(context))
			{
				var value = attribute.Groups["v"];
				foreach (System.Text.RegularExpressions.Match token in Token().Each(value.Value))
				{
					if (missing.Contains(token.Value))
					{
						yield return new Mark(value.Index + token.Index, token.Length, MarkRole.Offender);
					}
				}
			}
		}

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly System.Text.RegularExpressions.Regex AriaHiddenTrueRegex = new(@"aria-hidden\s*=\s*[""']?true[""']?", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

		private static System.Text.RegularExpressions.Regex AriaHiddenTrue() => AriaHiddenTrueRegex;

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly System.Text.RegularExpressions.Regex ReferenceAttributeRegex = new(@"aria-(?:labelledby|describedby)\s*=\s*[""'](?<v>[^""']*)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

		private static System.Text.RegularExpressions.Regex ReferenceAttribute() => ReferenceAttributeRegex;

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly System.Text.RegularExpressions.Regex QuotedIdsRegex = new(@"""([^""]+)""", System.Text.RegularExpressions.RegexOptions.Compiled);

		private static System.Text.RegularExpressions.Regex QuotedIds() => QuotedIdsRegex;

		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly System.Text.RegularExpressions.Regex TokenRegex = new(@"\S+", System.Text.RegularExpressions.RegexOptions.Compiled);

		private static System.Text.RegularExpressions.Regex Token() => TokenRegex;

		// The readable stand-in the template checks write for an invisible character:
		// "[BOM U+FEFF]", "[ZWSP (U+200B)]", "[bidi control (U+202E)]".
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly System.Text.RegularExpressions.Regex InvisibleMarkerRegex = new(@"\[[^\[\]]{1,40}?\(?U\+[0-9A-Fa-f]{4}\)?\]", System.Text.RegularExpressions.RegexOptions.Compiled);

		private static System.Text.RegularExpressions.Regex InvisibleMarker() => InvisibleMarkerRegex;

		// "U+2028" in a detail or a marker.
		// A plain Regex, not source-generated: generated helpers carry a hash of the source path.
		private static readonly System.Text.RegularExpressions.Regex CodePointRegex = new(@"U\+([0-9A-Fa-f]{4,6})", System.Text.RegularExpressions.RegexOptions.Compiled);

		/// <summary>
		/// The finding is about the character its detail names first ("Found LINE SEPARATOR
		/// (U+2028) in …"); other invisible characters in the same excerpt are bystanders. The
		/// culprit's markers stay Offender, the bystanders' become Tint, so a character that is only
		/// in the same text does not look like the reason for the finding. When the detail names
		/// no character, every marker stays Offender.
		/// </summary>
		internal static IEnumerable<Mark> ByCulprit(IEnumerable<Mark> markers, string context, string detail)
		{
			var named = CodePointRegex.Match(detail ?? string.Empty);
			if (!named.Success)
			{
				return markers;
			}

			int culprit = Convert.ToInt32(named.Groups[1].Value, 16);
			return markers.Select(m => CodePointOfMarker(context.Substring(m.Start, m.Length)) is { } cp && cp != culprit
				? m with { Role = MarkRole.Partner }
				: m).ToList();
		}

		/// <summary>The character a marker stands for: [CR], [LF], [TAB], or the U+ code it names; null when unknown.</summary>
		private static int? CodePointOfMarker(string marker) => marker switch
		{
			"[CR]" => 0x0D,
			"[LF]" => 0x0A,
			"[TAB]" => 0x09,
			_ => CodePointRegex.Match(marker) is { Success: true } m ? Convert.ToInt32(m.Groups[1].Value, 16) : null,
		};

		private static MarkedContext Plain(string text, IEnumerable<Mark> marks) =>
			new(text, marks.Where(m => m.Length > 0 && m.Start >= 0 && m.Start + m.Length <= text.Length).OrderBy(m => m.Start).ToList(), null);

		private static IEnumerable<Mark> Occurrences(string text, IEnumerable<string> needles, MarkRole role, bool ignoreCase = false)
		{
			var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			foreach (var needle in needles.Where(n => n.Length > 0).Distinct())
			{
				for (int at = text.IndexOf(needle, comparison); at >= 0; at = text.IndexOf(needle, at + needle.Length, comparison))
				{
					yield return new Mark(at, needle.Length, role);
				}
			}
		}

		/// <summary>A space at a hyphen: the hyphen with its space is the offender, the word at it is highlighted.</summary>
		private static IEnumerable<Mark> HyphenGap(string context, string issueType, string detail) =>
			Quality.HyphenSpacing.Spot(context, issueType, detail) is { } spot
				? [new Mark(spot.Word.Index, spot.Word.Length, MarkRole.Highlight), new Mark(spot.Gap.Index, spot.Gap.Length, MarkRole.Offender)]
				: [];

		private static IEnumerable<Mark> Doubled(string context, string detail) =>
			Quality.DoubledWords.Spot(context, detail) is { } spot
				? [new Mark(spot.First.Index, spot.First.Length, MarkRole.Highlight), new Mark(spot.Second.Index, spot.Second.Length, MarkRole.Offender)]
				: [];

		private static Mark Anchor(TriageHighlights.EmptyAnchorSpan s) => new(s.Start, s.Length, s.Kind switch
		{
			TriageHighlights.EmptyAnchorSpanKind.Structure => MarkRole.Offender,
			TriageHighlights.EmptyAnchorSpanKind.Href => MarkRole.Warn,
			TriageHighlights.EmptyAnchorSpanKind.Marker or TriageHighlights.EmptyAnchorSpanKind.Emphasis => MarkRole.Fill,
			_ => MarkRole.Dim,
		});

		private static Mark Split(TriageHighlights.SplitSpan s) => new(s.Start, s.Length, s.Kind switch
		{
			TriageHighlights.SplitSpanKind.Tag => MarkRole.Offender,
			TriageHighlights.SplitSpanKind.Inside => MarkRole.Tint,
			TriageHighlights.SplitSpanKind.Tail => MarkRole.Ok,
			_ => MarkRole.Dim,
		});

		private static Mark Adjacent(TriageHighlights.AdjacentHintSpan s) => new(s.Start, s.Length, s.Kind switch
		{
			TriageHighlights.AdjacentHintSpanKind.Collision => MarkRole.Offender,
			TriageHighlights.AdjacentHintSpanKind.HrefMatch or TriageHighlights.AdjacentHintSpanKind.HrefPlaceholder => MarkRole.Warn,
			TriageHighlights.AdjacentHintSpanKind.TitleMatch or TriageHighlights.AdjacentHintSpanKind.HrefBaseline => MarkRole.Tint,
			_ => MarkRole.Dim,
		});
	}
}
