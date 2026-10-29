using System.IO.Compression;
using System.Text;
using CrawlerSuite.Core.Comparison;
using Xunit;

namespace CrawlerSuite.Core.Tests.Comparison
{
	/// <summary>
	/// PDFs written out again by a generator that prints something anew each time: the random subset
	/// tag inside an embedded font program (generic), and the site's own patterns in the page text
	/// (from the setting). Invented forms: an ice form whose number carries the day it was written.
	/// </summary>
	[Collection("Logger")]
	public class PdfSetAsideTests : IDisposable
	{
		private readonly string root = Path.Combine(Path.GetTempPath(), "setaside-" + Guid.NewGuid().ToString("N"));

		public PdfSetAsideTests() => Directory.CreateDirectory(root);

		public void Dispose()
		{
			try
			{
				Directory.Delete(root, recursive: true);
			}
			catch (IOException)
			{
			}
		}

		// ── A TrueType program: head, name and glyf tables ────────────────────────────────────

		private static byte[] Sfnt(string tag, string glyphs, uint adjustment)
		{
			var head = new byte[54];
			head[8] = (byte)(adjustment >> 24);
			head[9] = (byte)(adjustment >> 16);
			head[10] = (byte)(adjustment >> 8);
			head[11] = (byte)adjustment;
			head[20] = 0x12; // a creation date that stays
			var wide = string.Concat($"{tag}+Frost".Select(c => "\0" + c));
			var name = Encoding.Latin1.GetBytes("\0\0\0\u0002Regular" + tag + "+Frost" + wide);
			var glyf = Encoding.Latin1.GetBytes(glyphs);
			var tables = new[] { ("glyf", glyf), ("head", head), ("name", name) };
			using var output = new MemoryStream();
			void U16(int v) { output.WriteByte((byte)(v >> 8)); output.WriteByte((byte)v); }
			void U32(long v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
			U32(0x00010000);
			U16(tables.Length);
			U16(0);
			U16(0);
			U16(0);
			long offset = 12 + (16 * tables.Length);
			foreach (var (t, data) in tables)
			{
				output.Write(Encoding.ASCII.GetBytes(t));
				U32(data.Sum(b => (long)b) + adjustment); // a checksum that changes with the tag
				U32(offset);
				U32(data.Length);
				offset += data.Length;
			}

			foreach (var (_, data) in tables)
			{
				output.Write(data);
			}

			return output.ToArray();
		}

		[Fact]
		public void A_font_program_differing_only_in_its_subset_tag_and_checksums_is_the_same()
		{
			var a = Sfnt("ABCDEF", "glyphs-0123", 0x11111111);
			var b = Sfnt("QWERTY", "glyphs-0123", 0x22222222);
			Assert.NotEqual(a, b);
			Assert.Equal(FontSubsets.Neutralise(a), FontSubsets.Neutralise(b));
		}

		[Fact]
		public void A_font_program_with_other_glyphs_still_differs()
		{
			Assert.NotEqual(FontSubsets.Neutralise(Sfnt("ABCDEF", "glyphs-0123", 1)), FontSubsets.Neutralise(Sfnt("ABCDEF", "glyphs-0124", 1)));
		}

		[Fact]
		public void A_compact_font_program_has_its_tag_set_aside_as_before()
		{
			var a = Encoding.Latin1.GetBytes("\u0001\u0000/FontName /ABCDEF+Frost def");
			var b = Encoding.Latin1.GetBytes("\u0001\u0000/FontName /QWERTY+Frost def");
			Assert.Equal(FontSubsets.Neutralise(a), FontSubsets.Neutralise(b));
		}

		// ── A one-page PDF with a content stream and an embedded font program ──────────────────

		private static byte[] Deflate(byte[] data)
		{
			using var output = new MemoryStream();
			using (var z = new ZLibStream(output, CompressionLevel.Optimal))
			{
				z.Write(data);
			}

			return output.ToArray();
		}

		private static byte[] Pdf(string content, byte[] font, string tag = "ABCDEF", string mod = "D:20260101020000Z")
		{
			using var output = new MemoryStream();
			void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));
			void Stream(int number, string dict, byte[] data)
			{
				var packed = Deflate(data);
				Write($"{number} 0 obj\n<< {dict} /Filter /FlateDecode /Length {packed.Length} >>\nstream\n");
				output.Write(packed);
				Write("\nendstream\nendobj\n");
			}

			Write("%PDF-1.7\n");
			Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
			Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
			Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
			Stream(4, string.Empty, Encoding.Latin1.GetBytes(content));
			Write($"5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /{tag}+Frost /FontDescriptor 6 0 R >>\nendobj\n");
			Write($"6 0 obj\n<< /Type /FontDescriptor /FontName /{tag}+Frost /FontFile2 7 0 R >>\nendobj\n");
			Stream(7, $"/Length1 {font.Length}", font);
			Write($"8 0 obj\n<< /Title (Ice form) /ModDate ({mod}) /CreationDate ({mod}) >>\nendobj\n");
			Write("trailer\n<< /Root 1 0 R /Info 8 0 R >>\n%%EOF\n");
			return output.ToArray();
		}

		private static string Form(string day, string word = "ice") =>
			$"BT /F1 9 Tf 10 80 Td ({word} form) Tj 10 20 Td (ICE0042{day}10260) Tj 10 10 Td (*ICE0042{day}10260*) Tj ET";

		private string Save(string name, byte[] bytes)
		{
			var path = Path.Combine(root, name);
			File.WriteAllBytes(path, bytes);
			return path;
		}

		private static PdfSetAside Patterns(params (string Name, string Pattern)[] entries) =>
			PdfSetAside.From(entries.Select(e => new ComparisonPattern { Name = e.Name, Pattern = e.Pattern }))!;

		private static readonly (string, string) FormDate = ("form date", @"ICE0042\d{6}0");

		[Fact]
		public void Written_out_again_with_another_subset_tag_inside_the_font_is_the_same_content()
		{
			var a = Pdf(Form("23"), Sfnt("ABCDEF", "glyphs-0123", 1), tag: "ABCDEF");
			var b = Pdf(Form("23"), Sfnt("QWERTY", "glyphs-0123", 2), tag: "QWERTY", mod: "D:20260102020000Z");
			Assert.True(PdfContent.Compare(PdfContent.Read(a), PdfContent.Read(b)).SameContent);
		}

		[Fact]
		public void A_printed_date_set_aside_by_a_pattern_makes_the_pdf_the_same_and_names_the_pattern()
		{
			// The new day needs another digit, so the font subset differs too.
			var a = Save("a.pdf", Pdf(Form("23"), Sfnt("ABCDEF", "glyphs-0123", 1)));
			var b = Save("b.pdf", Pdf(Form("24"), Sfnt("QWERTY", "glyphs-0124", 2), tag: "QWERTY", mod: "D:20260102020000Z"));

			Assert.False(PdfContent.SameContent(a, b));
			var but = PdfContent.SameContentBut(a, b, Patterns(FormDate));
			Assert.NotNull(but);
			Assert.True(but.Value.Same);
			Assert.Equal(["form date"], but.Value.Hits);
		}

		[Fact]
		public void Another_change_in_the_text_still_counts()
		{
			var a = Save("a.pdf", Pdf(Form("23"), Sfnt("ABCDEF", "glyphs-0123", 1)));
			var b = Save("b.pdf", Pdf(Form("24", word: "water"), Sfnt("ABCDEF", "glyphs-0124", 1)));
			Assert.False(PdfContent.SameContentBut(a, b, Patterns(FormDate))!.Value.Same);
		}

		[Fact]
		public void Fonts_alone_never_pass_without_a_pattern_that_matched()
		{
			// Same text, other glyphs: no pattern took anything, so the font program counts.
			var a = Save("a.pdf", Pdf(Form("23"), Sfnt("ABCDEF", "glyphs-0123", 1)));
			var b = Save("b.pdf", Pdf(Form("23"), Sfnt("ABCDEF", "glyphs-9999", 1)));
			var but = PdfContent.SameContentBut(a, b, Patterns(("unrelated", "WATER\\d{4}")))!.Value;
			Assert.False(but.Same);
			Assert.Empty(but.Hits);
		}

		[Fact]
		public void A_pattern_that_matches_more_than_the_date_is_still_bounded_by_the_text()
		{
			// A date line next to the form number, set aside by a second pattern.
			var a = Save("a.pdf", Pdf(Form("23") + " BT (Icetown, 23.10.2026) Tj ET", Sfnt("ABCDEF", "g", 1)));
			var b = Save("b.pdf", Pdf(Form("24") + " BT (Icetown, 24.10.2026) Tj ET", Sfnt("ABCDEF", "h", 1)));
			Assert.False(PdfContent.SameContentBut(a, b, Patterns(FormDate))!.Value.Same);
			var both = PdfContent.SameContentBut(a, b, Patterns(FormDate, ("dated line", @"Icetown, \d{2}\.\d{2}\.\d{4}")))!.Value;
			Assert.True(both.Same);
			Assert.Equal(["dated line", "form date"], both.Hits);
		}

		[Theory]
		[InlineData("form date", @"ICE0042\d{6}0", null)]
		[InlineData("", @"ICE0042\d{6}0", "An active pattern needs a name")]
		[InlineData("form date", "", "An active pattern needs a regular expression")]
		[InlineData("form date", @"\d*", "The pattern matches nothing at all")]
		[InlineData("form date", @"(?<=ICE)\d+", "Not supported here")]
		[InlineData("form date", @"ICE(\d+", "Not a valid regular expression")]
		public void A_pattern_is_checked(string name, string pattern, string? problem)
		{
			var found = PdfSetAside.Problem(new ComparisonPattern { Name = name, Pattern = pattern });
			if (problem is null)
			{
				Assert.Null(found);
			}
			else
			{
				Assert.StartsWith(problem, found);
			}
		}

		[Fact]
		public void Only_active_valid_patterns_are_used_and_none_means_nothing_set_aside()
		{
			Assert.Null(PdfSetAside.From(null));
			Assert.Null(PdfSetAside.From([new ComparisonPattern { Name = "off", Pattern = "ICE", Enabled = false }]));
			var set = PdfSetAside.From([
				new ComparisonPattern { Name = "broken", Pattern = "(" },
				new ComparisonPattern { Name = "form date", Pattern = @"ICE0042\d{6}0" },
			]);
			Assert.Equal(["form date"], set!.Entries.Select(e => e.Name).ToArray());
		}

		// ── In a comparison of two snapshots ─────────────────────────────────────────────────

		[Fact]
		public void The_comparison_counts_pdfs_identical_but_for_patterns_per_pattern()
		{
			var a = Path.Combine(root, "https___site_test", "2026-01-01-10-00-00");
			var b = Path.Combine(root, "https___site_test", "2026-01-02-10-00-00");
			foreach (var s in new[] { a, b })
			{
				Directory.CreateDirectory(Path.Combine(s, SnapshotComparison.DownloadFolder));
				Directory.CreateDirectory(Path.Combine(s, SnapshotComparison.PrunedFolder));
			}

			File.WriteAllBytes(Path.Combine(a, SnapshotComparison.DownloadFolder, "form.pdf"), Pdf(Form("23"), Sfnt("ABCDEF", "glyphs-0123", 1)));
			File.WriteAllBytes(Path.Combine(b, SnapshotComparison.DownloadFolder, "form.pdf"), Pdf(Form("24"), Sfnt("QWERTY", "glyphs-0124", 2), tag: "QWERTY"));
			File.WriteAllBytes(Path.Combine(a, SnapshotComparison.DownloadFolder, "edited.pdf"), Pdf(Form("23"), Sfnt("ABCDEF", "glyphs-0123", 1)));
			File.WriteAllBytes(Path.Combine(b, SnapshotComparison.DownloadFolder, "edited.pdf"), Pdf(Form("24", "water"), Sfnt("ABCDEF", "glyphs-0124", 1)));

			var plain = SnapshotComparison.Run(a, b);
			Assert.Equal(2, plain.Count(CompareState.Differs));
			Assert.Equal(0, plain.IdenticalOnlyPatterns);

			var withPatterns = SnapshotComparison.Run(a, b, pdfSetAside: Patterns(FormDate));
			Assert.Equal(["edited.pdf"], withPatterns.Listed.Select(f => f.Name).ToArray());
			Assert.Equal(1, withPatterns.IdenticalOnlyPatterns);
			Assert.Equal(1, withPatterns.PatternHits!["form date"]);
		}
	}
}
