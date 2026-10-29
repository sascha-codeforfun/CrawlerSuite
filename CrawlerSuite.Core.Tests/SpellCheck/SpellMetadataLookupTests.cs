using Xunit;

namespace CrawlerSuite.Core.Tests.SpellCheck
{
	/// <summary>
	/// Tests for SpellMetadataLookup.BuildMetadataLookup. Synthetic-fixture style:
	/// each test writes a tiny CmsContentList with known structure, constructs a
	/// minimal Config, builds the lookup function, and asserts the resulting
	/// TicketMetadata for representative URLs.
	///
	/// Focus area: the strip-prefix resolution, where an empty
	/// PathStripPrefix must fall back to config.Url. Without that
	/// fallback the full URL stays in place, which then
	/// cannot match any path-shaped row in the CSV, producing empty ticket
	/// metadata silently.
	///
	/// All fixture content uses generic placeholder names (alpha, beta, foo,
	/// /cms/path/, https://example.test) with no resemblance to any real site
	/// or CMS structure.
	/// </summary>
	[Collection("Logger")]
	public class SpellMetadataLookupTests : IDisposable
	{
		private readonly string _tempDir;
		private readonly string _csvPath;

		public SpellMetadataLookupTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"meta-lookup-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			_csvPath = Path.Combine(_tempDir, "content.csv");
			Logger.Initialize(Path.Combine(_tempDir, "test.log"));
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		// ── Fixture helpers ─────────────────────────────────────────────────

		private void WriteCsv(params string[] lines)
		{
			File.WriteAllLines(_csvPath, lines, System.Text.Encoding.UTF8);
		}

		/// <summary>
		/// Reads the current log file content for assertions in diagnostic tests.
		/// Logger writes to BaseDirectory + filename; when filename is rooted
		/// (our case — _tempDir/test.log), the rooted path wins so the log
		/// ends up in _tempDir.
		/// </summary>
		private string ReadLog()
		{
			var logPath = Path.Combine(_tempDir, "test.log");
			if (!File.Exists(logPath))
			{
				return string.Empty;
			}

			return File.ReadAllText(logPath);
		}

		/// <summary>
		/// Builds a minimal Config wired to invoke the path-construction logic
		/// in BuildMetadataLookup. The TicketGeneration section is set up to
		/// drive Location/Package/CmsLink resolution against the synthetic CSV.
		/// </summary>
		private Config BuildConfig(string url, string stripPrefix)
		{
			return new Config
			{
				Url = url,
				CmsContentList = new CmsContentListConfig
				{
					Path = _csvPath,
					ColumnDelimiter = ";",
					SkipRows = 0,
					RowFilter = "/cms/path",
					ValuePrefixReplace = true,
					ValueSuffix = ".html",
					PathStripPrefix = stripPrefix,
				},
				TicketGeneration = new TicketGenerationConfig
				{
					UrlSourceColumn = "Source",
					UrlSourceLocalName = "local",
					UrlSourceExternalName = "inherited",
					PackageColumn = "Module",
					CmsPathColumn = "Path",
					CmsEditorBaseUrl = "https://editor.example.test/cf#",
					CmsEditorBaseUrlSuffix = ".html",
					PathColumn = "Path",
				}
			};
		}

		// ── Scenario 1: Empty strip prefix falls back to config.Url ─────────

		[Fact]
		public void BuildMetadataLookup_EmptyStripPrefix_FallsBackToConfigUrl()
		{
			// CSV row matches the path constructed from a URL after config.Url is stripped.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""  // empty — should fall back to config.Url
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var meta = lookup("https://example.test/alpha/page-one.html");

			Assert.Equal("local", meta.Location);
			Assert.Equal("ModuleAlpha", meta.Package);
			Assert.Equal("https://editor.example.test/cf#/cms/path/alpha/page-one.html", meta.CmsLink);
		}

		// ── Scenario 2: Explicit strip prefix overrides config.Url ──────────

		[Fact]
		public void BuildMetadataLookup_ExplicitStripPrefix_OverridesConfigUrl()
		{
			// In some CMSes the export URL differs from the public site URL.
			// An explicit PathStripPrefix must take precedence.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/beta/page-two;inherited;ModuleBeta"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: "https://export.example.test"  // explicit — different from config.Url
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);

			// Input URL uses the export host — the strip prefix removes it cleanly.
			var meta = lookup("https://export.example.test/beta/page-two.html");

			Assert.Equal("inherited", meta.Location);
			Assert.Equal("ModuleBeta", meta.Package);
		}

		// ── Scenario 3: Both prefixes empty — defensive fallback ──────────

		[Fact]
		public void BuildMetadataLookup_EmptyStripPrefixWithNoConfigUrl_BehavesGracefully()
		{
			// Edge case: both config.Url AND PathStripPrefix empty.
			// The lookup uses the URL as-is, which won't match any path-shaped
			// CSV row → returns empty metadata. No exception, just empty fields.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "",  // both empty — defensive fallback only
				stripPrefix: ""
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var meta = lookup("https://example.test/alpha/page-one.html");

			// Empty metadata returned without crashing.
			Assert.Equal("", meta.Location);
			Assert.Equal("", meta.Package);
			Assert.Equal("", meta.CmsLink);
		}

		// ── Scenario 4: URL with no match in CSV ─────────────────────────────

		[Fact]
		public void BuildMetadataLookup_UrlNotInCsv_ReturnsEmptyMetadata()
		{
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);

			// URL exists on the site but no CSV row matches.
			var meta = lookup("https://example.test/gamma/never-listed.html");

			Assert.Equal("", meta.Location);
			Assert.Equal("", meta.Package);
			Assert.Equal("", meta.CmsLink);
		}

		// ── Scenario 5: Configured fields populated correctly ────────────────

		[Fact]
		public void BuildMetadataLookup_AllFieldsPopulated_RendersCompleteMetadata()
		{
			// Confirms full path-construction-and-column-lookup pipeline works
			// end-to-end. Tests Location, Package, CmsLink in one go.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/foo/sub/page-three;local;ModuleFoo"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var meta = lookup("https://example.test/foo/sub/page-three.html");

			Assert.Equal("local", meta.Location);
			Assert.Equal("ModuleFoo", meta.Package);
			Assert.Equal("https://editor.example.test/cf#/cms/path/foo/sub/page-three.html", meta.CmsLink);
		}

		// ── Scenario 6: Suffix stripping when ValuePrefixReplace=true ─

		[Fact]
		public void BuildMetadataLookup_StripsSuffixBeforePrefixing()
		{
			// The .html extension is stripped from the URL before prefixing with
			// RowFilter, because the CSV's path column never has extensions.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);

			// URL has .html, CSV row does not. The match must still succeed.
			var meta = lookup("https://example.test/alpha/page-one.html");

			Assert.Equal("ModuleAlpha", meta.Package);
		}

		// ── Scenario 7: Case-insensitive strip ───────────────────────────────

		[Fact]
		public void BuildMetadataLookup_StripPrefixIsCaseInsensitive()
		{
			// pageUrl.Replace uses OrdinalIgnoreCase in the production code.
			// A page URL with mixed-case host should still be stripped correctly.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var meta = lookup("HTTPS://Example.Test/alpha/page-one.html");

			Assert.Equal("ModuleAlpha", meta.Package);
		}

		// ── Scenario 8: Blank line in preamble counts toward skip ───────────

		[Fact]
		public void BuildMetadataLookup_BlankLineInPreamble_CountsTowardSkip()
		{
			// Mirrors Power Query's Table.Skip(table, N) semantic: blank rows
			// occupy a row number and consume a skip slot. The user-configured
			// SkipRows matches the row count visible in any editor (Excel,
			// Power Query, plain text).
			//
			// File layout: 3 metadata lines + 1 blank + header + 1 data row.
			// SkipRows = 4 must skip exactly those 4 rows so the header is
			// captured from row 5.
			WriteCsv(
				"Foo = some value",                                // row 1
				"Bar = another value",                             // row 2
				"Baz = yet another",                               // row 3
				"",                                                // row 4 — blank
				"Path;Source;Module",                              // row 5 — header
				"/cms/path/alpha/page-one;local;ModuleAlpha"       // row 6 — data
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);
			config.CmsContentList!.SkipRows = 4;

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var meta = lookup("https://example.test/alpha/page-one.html");

			Assert.Equal("local", meta.Location);
			Assert.Equal("ModuleAlpha", meta.Package);
		}

		// ── Scenario 9: No blanks in preamble — baseline check ──────────────

		[Fact]
		public void BuildMetadataLookup_NoBlanksInPreamble_SkipsExactRowCount()
		{
			// Guard: the all-non-blank preamble case must work
			// correctly too.
			WriteCsv(
				"line one of preamble",                            // row 1
				"line two of preamble",                            // row 2
				"line three of preamble",                          // row 3
				"line four of preamble",                           // row 4
				"Path;Source;Module",                              // row 5 — header
				"/cms/path/alpha/page-one;local;ModuleAlpha"       // row 6 — data
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);
			config.CmsContentList!.SkipRows = 4;

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var meta = lookup("https://example.test/alpha/page-one.html");

			Assert.Equal("local", meta.Location);
			Assert.Equal("ModuleAlpha", meta.Package);
		}

		// ── Scenario 10: Blank rows after preamble are filtered ─────────────

		[Fact]
		public void BuildMetadataLookup_BlankDataRows_FilteredSilently()
		{
			// Blanks AFTER the preamble (in the data area) are still filtered
			// silently — common with CSV exports that include a trailing empty
			// row. Affects only post-preamble lines; preamble blanks still count.
			WriteCsv(
				"Path;Source;Module",                              // row 1 — header
				"/cms/path/alpha/page-one;local;ModuleAlpha",      // row 2 — data
				"",                                                // row 3 — blank
				"/cms/path/beta/page-two;inherited;ModuleBeta"     // row 4 — data
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);
			config.CmsContentList!.SkipRows = 0;

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);

			// Both data rows resolve successfully; the blank does not break parsing.
			var metaAlpha = lookup("https://example.test/alpha/page-one.html");
			var metaBeta = lookup("https://example.test/beta/page-two.html");

			Assert.Equal("ModuleAlpha", metaAlpha.Package);
			Assert.Equal("ModuleBeta", metaBeta.Package);
		}
		// ── Scenario 11: Template blank-line separators are preserved ───────

		// ── Scenario 12: Non-empty SpecialInfo renders cleanly ──────────────

		// ── Scenario 13: Diagnostic logs file presence + headers ────────────

		[Fact]
		public void LogMetadataLookupDiagnostics_ValidConfig_LogsHeaderAndSample()
		{
			WriteCsv(
				"preamble row 1",
				"preamble row 2",
				"Path;Source;Module",                                  // header
				"/cms/path/alpha/page-one;local;ModuleAlpha"           // first data row
			);

			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.CmsContentList!.SkipRows = 2;

			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			var log = ReadLog();
			Assert.Contains("File present: yes", log);
			Assert.Contains("Path | Source | Module", log);
			// The sample's last segment is "sample" (synthetic by design); the rest of the path shows the URL derivation works.
			Assert.Contains("https://example.test/alpha/sample.html", log);
			Assert.Contains("Source: local", log);
			Assert.Contains("package: ModuleAlpha", log);
		}

		// ── Scenario 14: Missing CSV file warns ─────────────────────────────

		[Fact]
		public void LogMetadataLookupDiagnostics_MissingFile_Warns()
		{
			// Don't write the CSV — point at a nonexistent path.
			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.CmsContentList!.Path = Path.Combine(_tempDir, "does-not-exist.csv");

			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			var log = ReadLog();
			Assert.Contains("[WARNING]", log);
			Assert.Contains("file not found", log);
		}

		// ── Scenario 15: Empty CmsContentList treated as disabled ───────────

		[Fact]
		public void LogMetadataLookupDiagnostics_EmptyContentListCsv_DisablesGracefully()
		{
			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.CmsContentList!.Path = "";

			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			var log = ReadLog();
			// Informational only — no warning.
			Assert.Contains("disabled", log);
			Assert.DoesNotContain("[WARNING] Ticket metadata lookup", log);
		}

		// ── Scenario 16: Configured column missing in header warns ──────────

		[Fact]
		public void LogMetadataLookupDiagnostics_ConfiguredColumnNotInHeader_Warns()
		{
			// The failure mode this guards: SkipRows off by one
			// causes the captured "header" to actually be data values, so
			// configured column names won't match.
			WriteCsv(
				"preamble row 1",
				"Path;Source;Module",                                  // would-be header at row 2
				"some-value;local;ModuleAlpha"                         // captured AS header due to off-by-one
			);

			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.CmsContentList!.SkipRows = 2;  // off by one — skips both preamble AND real header
			config.TicketGeneration.PackageColumn = "Module";          // won't be in captured header

			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			var log = ReadLog();
			Assert.Contains("[WARNING]", log);
			// Warning identifies the missing column by name.
			Assert.Contains("PackageColumn", log);
			Assert.Contains("Module", log);
		}

		// ── Scenario 17: Empty data area produces no sample ─────────────────

		[Fact]
		public void LogMetadataLookupDiagnostics_NoDataRows_InformsButDoesNotWarn()
		{
			// Header only, no data rows below it.
			WriteCsv(
				"Path;Source;Module"
			);

			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.CmsContentList!.SkipRows = 0;

			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			var log = ReadLog();
			Assert.Contains("Header captured", log);
			Assert.Contains("No data rows found", log);
		}

		// ── Context line / dash separators / sanitization / empty ──────────

		// ── BuildMetadataLookup resolver-creation guards ────────────────────
		// These cover the resolver itself (no CSV / missing file /
		// valid match / unknown URL), complementing the field-extraction cases above.

		[Fact]
		public void BuildMetadataLookup_NoCsvConfigured_ReturnsEmptyResolver()
		{
			var config = new Config { CmsContentList = new CmsContentListConfig { Path = "" } };
			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var result = lookup("https://example.com/page");
			Assert.Equal("", result.Location);
			Assert.Equal("", result.Package);
		}

		[Fact]
		public void BuildMetadataLookup_CsvNotFound_ReturnsEmptyResolver()
		{
			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = Path.Combine(_tempDir, "missing.csv")
				}
			};
			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var result = lookup("https://example.com/page");
			Assert.Equal("", result.Location);
		}

		[Fact]
		public void BuildMetadataLookup_ValidCsv_ReturnsLocationAndModule()
		{
			var csvPath = Path.Combine(_tempDir, "content.csv");
			File.WriteAllLines(csvPath, [
				"Path;Location;Module",
				"https://example.com/de/home/page;RegionA;ModuleB",
			]);

			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csvPath,
					ColumnDelimiter = ";",
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					UrlSourceColumn = "Location",
					PackageColumn = "Module",
				}
			};

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var result = lookup("https://example.com/de/home/page");
			Assert.Equal("RegionA", result.Location);
			Assert.Equal("ModuleB", result.Package);
		}

		[Fact]
		public void BuildMetadataLookup_UnknownUrl_ReturnsEmpty()
		{
			var csvPath = Path.Combine(_tempDir, "content.csv");
			File.WriteAllLines(csvPath, [
				"Path;Location;Module",
				"https://example.com/de/home/page;RegionA;ModuleB",
			]);

			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csvPath,
					ColumnDelimiter = ";",
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					UrlSourceColumn = "Location",
					PackageColumn = "Module",
				}
			};

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var result = lookup("https://example.com/unknown/page");
			Assert.Equal("", result.Location);
			Assert.Equal("", result.Package);
		}
	}
}
