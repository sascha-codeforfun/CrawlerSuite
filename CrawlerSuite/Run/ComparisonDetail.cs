using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using CrawlerSuite.Core.Comparison;
using CrawlerSuite.Core.Settings;

namespace CrawlerSuite.Run;

/// <summary>
/// The selected file of the Comparison page, A and B next to each other: as text, line by line,
/// what differs marked — or, for a file that is not text, size and checksum of each side.
/// </summary>
public sealed class FileDetailVm : Observable
{
	private readonly ComparisonModel owner;
	private FileRowVm? file;
	private bool isLoading;
	private bool isText;
	private string problem = string.Empty;
	private string counts = string.Empty;
	private string note = string.Empty;
	private IReadOnlyList<DiffRowVm> rows = [];
	private IReadOnlyList<int> differences = [];
	private int currentDifference = -1;
	private bool asSaved;
	private bool allLines;
	private FileSides? sides;
	private string? pathA;
	private string? pathB;
	private bool isImage;
	private ImageSide? imageA;
	private ImageSide? imageB;
	private bool actualSize;
	private IReadOnlyList<string> pdfLines = [];
	private IReadOnlyList<PdfRow> pdfRows = [];
	private IReadOnlyList<PdfPartRow> pdfParts = [];
	private string pdfPartsMore = string.Empty;
	private string crawlNote = string.Empty;

	public FileDetailVm(ComparisonModel owner)
	{
		this.owner = owner;

		// The screen mask changed: every row shows its text through the mask again.
		Mask.Instance.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(Mask.Level))
			{
				foreach (var row in rows)
				{
					row.Remask();
				}
			}
		};
	}

	/// <summary>Asks the view to bring a row into sight (its index in <see cref="Rows"/>).</summary>
	public event EventHandler<int>? ScrollRequested;

	public bool HasFile => file is not null;

	/// <summary>The File tab's header: the file's name from its address.</summary>
	public string TabHeader
	{
		get
		{
			if (file is null)
			{
				return "File";
			}

			var address = file.Address;
			int cut = address.IndexOfAny(['?', '#']);
			var path = (cut >= 0 ? address[..cut] : address).TrimEnd('/');
			int slash = path.LastIndexOf('/');
			var leaf = slash >= 0 ? path[(slash + 1)..] : path;
			return "File · " + (leaf.Length == 0 ? address : leaf);
		}
	}

	public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }

	public string Address => file?.Address ?? string.Empty;

	/// <summary>The address is a web address (http or https): the heading opens it in the browser.</summary>
	public bool IsWebAddress => Uri.TryCreate(Address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

	public string Name => file?.Name ?? string.Empty;

	public string State => file?.State ?? string.Empty;

	public string StateStyle => file?.StateStyle ?? "Pill";

	public bool IsPage => file?.File.IsPage == true;

	/// <summary>A saved SVG file drawn, A's and B's (none, or one each), above its text.</summary>
	public IReadOnlyList<EmbeddedPictureVm> DrawingsA { get => drawingsA; private set => Set(ref drawingsA, value); }

	public IReadOnlyList<EmbeddedPictureVm> DrawingsB { get => drawingsB; private set => Set(ref drawingsB, value); }

	public bool HasDrawings => DrawingsA.Count + DrawingsB.Count > 0;

	private IReadOnlyList<EmbeddedPictureVm> drawingsA = [];
	private IReadOnlyList<EmbeddedPictureVm> drawingsB = [];

	/// <summary>An SVG file is drawn too, beside being compared as text; B's drawing says how it compares with A's.</summary>
	private void SetDrawings(PictureSource where)
	{
		EmbeddedPictureVm? One(string? path, string side, string snapshot)
		{
			if (path is null || !string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase)
				|| new FileInfo(path).Length > SnapshotComparison.MaxShownBytes)
			{
				return null;
			}

			try
			{
				var bytes = File.ReadAllBytes(path);
				return EmbeddedPictures.Sniff(bytes) == "SVG"
					? EmbeddedPictureVm.SvgFile(bytes, ($"{side} · {snapshot} · SVG file · {Mask.Apply(where.Page)}", $"{PictureSource.NameOf(where.Page)}-{side}"))
					: null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}

		var a = One(pathA, "A", where.SnapshotA);
		var b = One(pathB, "B", where.SnapshotB);
		if (a is not null && b is not null)
		{
			b.Note = EmbeddedPictureVm.AgainstA(a, b);
		}

		DrawingsA = a is null ? [] : [a];
		DrawingsB = b is null ? [] : [b];
		Raise(nameof(HasDrawings));
	}

	/// <summary>A reference page: the File tab says so in front of its state.</summary>
	public bool IsReferencePage => file?.IsReferencePage == true;

	/// <summary>Shown as text, line by line.</summary>
	public bool IsText { get => isText; private set => Set(ref isText, value); }

	/// <summary>A picture: A and B shown as pictures, next to each other.</summary>
	public bool IsImage { get => isImage; private set => Set(ref isImage, value); }

	public ImageSource? PictureA => imageA?.Image;

	public ImageSource? PictureB => imageB?.Image;

	/// <summary>Width × height and size of A's picture, or why it is not shown.</summary>
	public string PictureTextA => PictureText(imageA, "not in A", sides?.HashA);

	public string PictureTextB => PictureText(imageB, "not in B", sides?.HashB);

	/// <summary>The pictures at their own pixels (both scroll together), not fitted to the space.</summary>
	public bool ActualSize { get => actualSize; set => Set(ref actualSize, value); }

	/// <summary>No picture on that side (not in it, or not decoded): the side shows why instead.</summary>
	public bool NoPictureA => IsImage && imageA is not { HasImage: true };

	public bool NoPictureB => IsImage && imageB is not { HasImage: true };

	public double PictureWidthA => imageA is { HasImage: true } s ? s.PixelWidth : double.NaN;

	public double PictureHeightA => imageA is { HasImage: true } s ? s.PixelHeight : double.NaN;

	public double PictureWidthB => imageB is { HasImage: true } s ? s.PixelWidth : double.NaN;

	public double PictureHeightB => imageB is { HasImage: true } s ? s.PixelHeight : double.NaN;

	/// <summary>A PDF in both snapshots: what differs inside it, in a few lines.</summary>
	public IReadOnlyList<string> PdfLines { get => pdfLines; private set => Set(ref pdfLines, value); }

	public bool HasPdf => PdfLines.Count > 0 || PdfRows.Count > 0;

	/// <summary>The PDF table: pages, content parts and stamps, A and B side by side.</summary>
	public IReadOnlyList<PdfRow> PdfRows { get => pdfRows; private set => Set(ref pdfRows, value); }

	public bool HasPdfRows => PdfRows.Count > 0;

	/// <summary>The parts only one side has, described.</summary>
	public IReadOnlyList<PdfPartRow> PdfParts { get => pdfParts; private set => Set(ref pdfParts, value); }

	public bool HasPdfParts => PdfParts.Count > 0;

	/// <summary>"… and 12 more" when not every differing part is listed.</summary>
	public string PdfPartsMore { get => pdfPartsMore; private set => Set(ref pdfPartsMore, value); }

	/// <summary>The checksum card: for a file that is not text, unless it is a picture (whose checksums stand under it).</summary>
	public bool ShowChecksums => IsBinary && !IsImage;

	/// <summary>A's SHA-256, in full, for the tooltip of the short one under the picture.</summary>
	public string PictureHashA => HashA;

	public string PictureHashB => HashB;

	public bool IsBinary => file is not null && !IsLoading && problem.Length == 0 && sides is { IsText: false };

	public string Problem { get => problem; private set => Set(ref problem, value); }

	public bool HasProblem => Problem.Length > 0;

	/// <summary>What else is known about the file (from the compare or why it is not shown as text).</summary>
	public string Note { get => note; private set => Set(ref note, value); }

	public bool HasNote => Note.Length > 0;

	/// <summary>For a file in one snapshot only: what the other snapshot's crawl did with its address.</summary>
	public string CrawlNote { get => crawlNote; private set => Set(ref crawlNote, value); }

	public bool HasCrawlNote => CrawlNote.Length > 0;

	/// <summary>How many lines differ, in a line.</summary>
	public string Counts { get => counts; private set => Set(ref counts, value); }

	public IReadOnlyList<DiffRowVm> Rows { get => rows; private set => Set(ref rows, value); }

	public string SizeA => pathA is null ? "not in A" : Format.Bytes(sides?.BytesA ?? 0);

	public string SizeB => pathB is null ? "not in B" : Format.Bytes(sides?.BytesB ?? 0);

	public string HashA => sides?.HashA ?? "–";

	public string HashB => sides?.HashB ?? "–";

	public bool SameHash => sides?.HashA is { } a && a == sides.HashB;

	/// <summary>Pages as saved (download), not as pruned.</summary>
	public bool AsSaved
	{
		get => asSaved;
		set
		{
			if (Set(ref asSaved, value))
			{
				_ = owner.ReloadDetailAsync();
			}
		}
	}

	/// <summary>Every line, not only the changes with a few lines around them.</summary>
	public bool AllLines
	{
		get => allLines;
		set
		{
			if (Set(ref allLines, value))
			{
				_ = owner.ReloadDetailAsync();
			}
		}
	}

	public bool HasDifferences => differences.Count > 0;

	/// <summary>"2 of 5": where in the file's differences the view stands.</summary>
	public string DifferenceText => differences.Count == 0 ? string.Empty
		: currentDifference < 0 ? $"{differences.Count} {(differences.Count == 1 ? "change" : "changes")}"
		: $"change {currentDifference + 1} of {differences.Count}";

	public bool CanRevealA => pathA is not null;

	public bool CanRevealB => pathB is not null;

	internal void Clear()
	{
		file = null;
		sides = null;
		pathA = pathB = null;
		IsLoading = false;
		Problem = string.Empty;
		Note = string.Empty;
		Counts = string.Empty;
		IsText = false;
		SetImages(null, null);
		SetPdf(null);
		CrawlNote = string.Empty;
		Rows = [];
		DrawingsA = [];
		DrawingsB = [];
		SetDifferences([]);
		RaiseAll();
	}

	internal void ShowLoading(FileRowVm row)
	{
		file = row;
		sides = null;
		IsLoading = true;
		Problem = string.Empty;
		RaiseAll();
	}

	internal void ShowProblem(FileRowVm row, string text)
	{
		file = row;
		IsLoading = false;
		IsText = false;
		SetImages(null, null);
		SetPdf(null);
		CrawlNote = string.Empty;
		Rows = [];
		DrawingsA = [];
		DrawingsB = [];
		Problem = text;
		SetDifferences([]);
		RaiseAll();
	}

	internal void Show(FileRowVm row, FileSides loaded, SideBySideView? view, string snapshotA, string snapshotB,
		ImageSide? pictureA = null, ImageSide? pictureB = null, PdfDifference? pdf = null, string crawlNote = "")
	{
		CrawlNote = crawlNote;
		SetImages(pictureA, pictureB);
		SetPdf(pdf);
		file = row;
		sides = loaded;
		pathA = Existing(snapshotA, row.File.FileA);
		pathB = Existing(snapshotB, row.Name);
		IsLoading = false;
		Problem = string.Empty;
		// "Not a text file" says nothing once the file is shown as pictures or read as a PDF.
		Note = (IsImage || pdf is not null) && !loaded.IsText && loaded.Note.StartsWith("Not a text file", StringComparison.Ordinal) ? string.Empty : loaded.Note;
		IsText = loaded.IsText && view is not null;
		// A line's embedded pictures are read from the whole line: a shown line may be cut.
		var linesA = loaded.TextA is null ? [] : LineDiff.Lines(loaded.TextA);
		var linesB = loaded.TextB is null ? [] : LineDiff.Lines(loaded.TextB);
		string? Whole(string[] lines, int? number) => number is int n && n >= 1 && n <= lines.Length ? lines[n - 1] : null;
		var where = new PictureSource(Path.GetFileName(snapshotA), Path.GetFileName(snapshotB), row.Address);
		SetDrawings(where);
		Rows = view is null ? [] : view.Rows.Select(r => new DiffRowVm(r, Whole(linesA, r.LineA), Whole(linesB, r.LineB), where)).ToList();
		Counts = view is null ? string.Empty
			: view.Changed + view.OnlyA + view.OnlyB == 0
				? AsSaved || !IsPage ? "The texts are the same line by line." : "The pruned pages are the same line by line."
				: $"{Format.Count(view.Changed)} changed · {Format.Count(view.OnlyA)} only in A · {Format.Count(view.OnlyB)} only in B "
					+ $"(lines: {Format.Count(view.LinesA)} in A, {Format.Count(view.LinesB)} in B)";
		SetDifferences(view?.Differences ?? []);
		RaiseAll();
		if (differences.Count > 0)
		{
			GoTo(0);
		}
	}

	/// <summary>The view came on screen: the change it stands at is brought into sight again (a list off screen cannot scroll).</summary>
	public void Rescroll()
	{
		if (currentDifference >= 0 && currentDifference < differences.Count)
		{
			ScrollRequested?.Invoke(this, differences[currentDifference]);
		}
	}

	/// <summary>Moves to the next (or previous) change in the file.</summary>
	public void StepDifference(int by)
	{
		if (differences.Count == 0)
		{
			return;
		}

		GoTo(currentDifference < 0 ? (by > 0 ? 0 : differences.Count - 1) : Math.Clamp(currentDifference + by, 0, differences.Count - 1));
	}

	public void RevealA()
	{
		if (pathA is not null)
		{
			Views.Shell.Reveal(pathA);
		}
	}

	public void RevealB()
	{
		if (pathB is not null)
		{
			Views.Shell.Reveal(pathB);
		}
	}

	private void GoTo(int index)
	{
		currentDifference = index;
		Raise(nameof(DifferenceText));
		ScrollRequested?.Invoke(this, differences[index]);
	}

	private void SetDifferences(IReadOnlyList<int> list)
	{
		differences = list;
		currentDifference = -1;
		Raise(nameof(HasDifferences));
		Raise(nameof(DifferenceText));
	}

	private void SetPdf(PdfDifference? pdf)
	{
		PdfRows = pdf?.Rows() ?? [];
		PdfLines = pdf is null ? [] : PdfRows.Count == 0 ? pdf.Lines() : [];
		var parts = pdf is null ? [] : (pdf.PartsOnlyA ?? []).Select(p => new PdfPartRow("Only in A", "Pill.Error", p))
			.Concat((pdf.PartsOnlyB ?? []).Select(p => new PdfPartRow("Only in B", "Pill.Ok", p))).ToList();
		PdfParts = parts;
		int hidden = pdf is null ? 0 : pdf.OnlyA + pdf.OnlyB - parts.Count;
		PdfPartsMore = hidden > 0 ? $"… and {Format.Count(hidden)} more parts not listed." : string.Empty;
		Raise(nameof(HasPdf));
		Raise(nameof(HasPdfRows));
		Raise(nameof(HasPdfParts));
	}

	private void SetImages(ImageSide? a, ImageSide? b)
	{
		imageA = a;
		imageB = b;
		IsImage = a is not null || b is not null;
		Raise(nameof(PictureA));
		Raise(nameof(PictureB));
		Raise(nameof(PictureTextA));
		Raise(nameof(PictureTextB));
		Raise(nameof(NoPictureA));
		Raise(nameof(NoPictureB));
		Raise(nameof(PictureWidthA));
		Raise(nameof(PictureHeightA));
		Raise(nameof(PictureWidthB));
		Raise(nameof(PictureHeightB));
	}

	private static string PictureText(ImageSide? side, string missing, string? hash) => side switch
	{
		null => missing,
		{ HasImage: true } s => $"{Format.Count(s.PixelWidth)} × {Format.Count(s.PixelHeight)} px · {Format.Bytes(s.Bytes)}"
			+ (s.PixelWidth > ImagePreview.MaxSide || s.PixelHeight > ImagePreview.MaxSide ? " · shown scaled down" : string.Empty)
			+ (hash is { Length: > 12 } ? $" · {hash[..12]}…" : string.Empty),
		{ } s => $"{Format.Bytes(s.Bytes)} · {s.Problem}",
	};

	private void RaiseAll()
	{
		Raise(nameof(HasFile));
		Raise(nameof(TabHeader));
		Raise(nameof(Address));
		Raise(nameof(IsWebAddress));
		Raise(nameof(Name));
		Raise(nameof(State));
		Raise(nameof(StateStyle));
		Raise(nameof(IsPage));
		Raise(nameof(IsReferencePage));
		Raise(nameof(IsBinary));
		Raise(nameof(HasProblem));
		Raise(nameof(HasNote));
		Raise(nameof(HasCrawlNote));
		Raise(nameof(SizeA));
		Raise(nameof(SizeB));
		Raise(nameof(HashA));
		Raise(nameof(HashB));
		Raise(nameof(SameHash));
		Raise(nameof(HasPdf));
		Raise(nameof(ShowChecksums));
		Raise(nameof(PictureHashA));
		Raise(nameof(PictureHashB));
		Raise(nameof(PictureTextA));
		Raise(nameof(PictureTextB));
		Raise(nameof(CanRevealA));
		Raise(nameof(CanRevealB));
	}

	private static string? Existing(string snapshot, string name)
	{
		var path = Path.Combine(snapshot, SnapshotComparison.DownloadFolder, name.Replace('/', Path.DirectorySeparatorChar));
		return File.Exists(path) ? path : null;
	}
}

/// <summary>
/// One row of the side-by-side view. Its text reaches the screen through the mask, built when the
/// row is first shown: what differs is a marked piece, blanks and line ends in it made visible.
/// </summary>
public sealed class DiffRowVm : INotifyPropertyChanged
{
	private readonly SideRow row;
	private readonly string? wholeA;
	private readonly string? wholeB;
	private IReadOnlyList<TextPiece>? piecesA;
	private IReadOnlyList<TextPiece>? piecesB;
	private readonly PictureSource? source;
	private (IReadOnlyList<EmbeddedPictureVm> A, IReadOnlyList<EmbeddedPictureVm> B)? pictures;
	private bool showBase64;

	/// <param name="wholeA">A's whole line (the shown one may be cut): where its embedded pictures are read from.</param>
	public DiffRowVm(SideRow row, string? wholeA = null, string? wholeB = null, PictureSource? source = null)
	{
		this.row = row;
		this.wholeA = wholeA;
		this.wholeB = wholeB;
		this.source = source;
	}

	/// <summary>The pictures embedded as base64 in a line that is not the same on both sides, A's and B's.</summary>
	public IReadOnlyList<EmbeddedPictureVm> PicturesA => Pictures().A;

	public IReadOnlyList<EmbeddedPictureVm> PicturesB => Pictures().B;

	public bool HasPicturesA => PicturesA.Count > 0;

	public bool HasPicturesB => PicturesB.Count > 0;

	public bool HasPictures => HasPicturesA || HasPicturesB;

	/// <summary>The base64 text unfolded: shown as it stands in the source, marks and all.</summary>
	public bool ShowBase64
	{
		get => showBase64;
		set
		{
			if (showBase64 != value)
			{
				showBase64 = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBase64)));
				Remask();
			}
		}
	}

	private (IReadOnlyList<EmbeddedPictureVm> A, IReadOnlyList<EmbeddedPictureVm> B) Pictures()
	{
		if (pictures is { } known)
		{
			return known;
		}

		if (row.Kind is RowKind.Same or RowKind.Gap)
		{
			pictures = ([], []);
			return pictures.Value;
		}

		var a = EmbeddedPictures.In(wholeA);
		var b = EmbeddedPictures.In(wholeB);
		var shownA = a.Select((p, i) => new EmbeddedPictureVm(p, Where("A", source?.SnapshotA, row.LineA, i, a.Count))).ToList();
		var shownB = b.Select((p, i) => new EmbeddedPictureVm(p, Where("B", source?.SnapshotB, row.LineB, i, b.Count))).ToList();
		for (int i = 0; i < Math.Max(shownA.Count, shownB.Count); i++)
		{
			if (i >= shownA.Count)
			{
				shownB[i].Note = "No counterpart in A's line.";
			}
			else if (i >= shownB.Count)
			{
				shownA[i].Note = "No counterpart in B's line.";
			}
			else
			{
				shownB[i].Note = EmbeddedPictureVm.AgainstA(shownA[i], shownB[i]);
			}
		}

		pictures = (shownA, shownB);
		return pictures.Value;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Same, Changed, OnlyA, OnlyB or Gap — for the row's colours.</summary>
	public string Kind => row.Kind.ToString();

	public bool IsGap => row.Kind == RowKind.Gap;

	public string LineA => row.LineA?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

	public string LineB => row.LineB?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

	public string GapText => row.Hidden == 1 ? "1 unchanged line" : $"{Format.Count(row.Hidden)} unchanged lines";

	/// <summary>Where a picture stands, for its window's title and the name it is saved under.</summary>
	private (string Title, string Name) Where(string side, string? snapshot, int? line, int index, int count)
	{
		var title = $"{side} · {snapshot} · line {line} · picture {index + 1} of {count} · {Mask.Apply(source?.Page ?? string.Empty)}";
		return (title, $"{PictureSource.NameOf(source?.Page)}-{side}-line{line}-{index + 1}");
	}

	public IReadOnlyList<TextPiece> PiecesA => piecesA ??= HasPicturesA && !showBase64 ? Folded(row.TextA, row.MarksA, row.CutA) : Pieces(row.TextA, row.MarksA, row.CutA);

	public IReadOnlyList<TextPiece> PiecesB => piecesB ??= HasPicturesB && !showBase64 ? Folded(row.TextB, row.MarksB, row.CutB) : Pieces(row.TextB, row.MarksB, row.CutB);

	/// <summary>The cell's pieces with its long base64 runs folded to one piece each, marked when anything in them differs.</summary>
	private static IReadOnlyList<TextPiece> Folded(string text, IReadOnlyList<Mark> marks, bool cut)
	{
		var list = new List<TextPiece>();
		foreach (var (part, hit, fold) in EmbeddedPictures.Pieces(text, [.. marks.Select(m => (m.Start, m.Length))], EmbeddedPictures.Folds(text)))
		{
			if (fold)
			{
				list.Add(new TextPiece(part, hit));
			}
			else
			{
				Add(list, hit ? Visible(part) : Plain(part), hit);
			}
		}

		if (cut)
		{
			list.Add(new TextPiece("  … (line cut here)", false));
		}

		return list;
	}

	internal void Remask()
	{
		piecesA = null;
		piecesB = null;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PiecesA)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PiecesB)));
	}

	/// <summary>The cell's pieces: plain and marked in turn; a line end outside a mark is not drawn.</summary>
	private static IReadOnlyList<TextPiece> Pieces(string text, IReadOnlyList<Mark> marks, bool cut)
	{
		var list = new List<TextPiece>(2 * marks.Count + 2);
		int at = 0;
		foreach (var m in marks)
		{
			if (m.Length <= 0 || m.Start < at || m.Start + m.Length > text.Length)
			{
				continue;
			}

			Add(list, Plain(text[at..m.Start]), false);
			Add(list, Visible(text.Substring(m.Start, m.Length)), true);
			at = m.Start + m.Length;
		}

		Add(list, Plain(text[at..]), false);
		if (cut)
		{
			list.Add(new TextPiece("  … (line cut here)", false));
		}

		return list;
	}

	private static void Add(List<TextPiece> list, string text, bool hit)
	{
		if (text.Length > 0)
		{
			list.Add(new TextPiece(Mask.Apply(text), hit));
		}
	}

	private static string Plain(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal);

	/// <summary>Blanks, tabs and line ends in a marked piece, made visible: they may be all that differs.</summary>
	private static string Visible(string text)
	{
		var sb = new StringBuilder(text.Length);
		foreach (var c in text)
		{
			sb.Append(c switch
			{
				' ' => '·',
				'\t' => '→',
				'\r' => '␍',
				' ' => '⍽',
				_ => c,
			});
		}

		return sb.ToString();
	}
}

/// <summary>
/// A picture a line carries as base64 text, as the File tab shows it: the picture itself when
/// Windows can draw it (an SVG as its text), and a label saying that the source holds it as base64,
/// with its type, size in pixels and bytes. B's says how it compares with A's.
/// </summary>
public sealed class EmbeddedPictureVm : INotifyPropertyChanged
{
	private string note = string.Empty;

	public EmbeddedPictureVm(EmbeddedPictures.Found found, (string Title, string Name) where = default)
		: this(found.Bytes, found.Kind, found.Labelled, found.Mislabelled, "Embedded in the source as base64", where)
	{
	}

	/// <summary>A saved SVG file, drawn above its text on the File tab.</summary>
	public static EmbeddedPictureVm SvgFile(byte[] bytes, (string Title, string Name) where) =>
		new(bytes, "SVG", "image/svg+xml", false, "SVG file", where);

	private EmbeddedPictureVm(byte[]? bytes, string kindOfBytes, string labelled, bool mislabelled, string origin, (string Title, string Name) where)
	{
		var found = new { Labelled = labelled, Kind = kindOfBytes };
		Title = where.Title ?? "Embedded picture";
		Kind = found.Kind;
		Extension = Kind switch
		{
			"PNG" => ".png",
			"JPEG" => ".jpg",
			"GIF" => ".gif",
			"WebP" => ".webp",
			"BMP" => ".bmp",
			"ICO" => ".ico",
			"AVIF" => ".avif",
			"SVG" => ".svg",
			_ => "." + found.Labelled[(found.Labelled.IndexOf('/') + 1)..].Split('+')[0],
		};
		SuggestedName = (where.Name ?? "embedded") + Extension;
		if (bytes is null)
		{
			Label = $"{origin} · {found.Labelled} · not readable as base64";
			return;
		}

		Bytes = bytes;
		var mislabel = mislabelled ? $" (labelled {found.Labelled})" : string.Empty;
		if (found.Kind == "SVG")
		{
			IsSvg = true;
			var text = Encoding.UTF8.GetString(bytes);
			var (drawing, problem) = SimpleSvg.Read(text);
			var (image, drawProblem) = drawing is null ? ((ImageSource?)null, problem) : SvgRender.Draw(drawing);
			if (drawing is not null && image is not null)
			{
				Image = image;
				IsVector = true;
				(PixelWidth, PixelHeight) = SvgRender.ShownSize(drawing);
				ViewWidth = drawing.Width;
				ViewHeight = drawing.Height;
				var shapes = drawing.Shapes.Count == 1 ? "1 shape" : $"{Format.Count(drawing.Shapes.Count)} shapes";
				var open = drawing.UsesCurrentColour ? " · drawn in the text colour where it leaves the colour open" : string.Empty;
				Label = $"{origin} · SVG{mislabel} · viewBox {Number(drawing.Width)}×{Number(drawing.Height)} · {shapes} · {Format.Bytes(bytes.LongLength)}{open}";
				return;
			}

			SvgText = Mask.Apply(text);
			Problem = drawProblem;
			Label = $"{origin} · SVG{mislabel} · {Format.Bytes(bytes.LongLength)} · its text:";
			return;
		}

		var side = ImagePreview.Decode(bytes);
		Image = side.Image;
		PixelWidth = side.PixelWidth;
		PixelHeight = side.PixelHeight;
		var kind = found.Kind == "unknown" ? found.Labelled : found.Kind;
		var size = side.HasImage ? $" · {Format.Count(side.PixelWidth)}×{Format.Count(side.PixelHeight)} px" : string.Empty;
		Label = $"{origin} · {kind}{mislabel}{size} · {Format.Bytes(bytes.LongLength)}";
		Problem = side.Problem;
	}

	private static string Number(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>An SVG, drawn or not.</summary>
	public bool IsSvg { get; }

	/// <summary>Drawn from an SVG: it scales without loss, so the picture window may enlarge it.</summary>
	public bool IsVector { get; }

	/// <summary>A drawing's view box, for drawing both sides at the same size to compare them.</summary>
	public double ViewWidth { get; }

	public double ViewHeight { get; }

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Label { get; }

	public ImageSource? Image { get; }

	public bool HasImage => Image is not null;

	public int PixelWidth { get; }

	public int PixelHeight { get; }

	/// <summary>An embedded SVG, decoded: its text.</summary>
	public string? SvgText { get; }

	public bool HasSvg => SvgText is not null;

	/// <summary>Why the picture is not drawn.</summary>
	public string Problem { get; } = string.Empty;

	internal byte[]? Bytes { get; }

	/// <summary>Its window's title: side, snapshot, line, which picture of the line, the page.</summary>
	public string Title { get; }

	/// <summary>The type the bytes are.</summary>
	public string Kind { get; }

	public string KindName => Kind is "unknown" or "unreadable" ? "Picture" : Kind + " picture";

	public string Extension { get; }

	public string SuggestedName { get; }

	public bool CanSave => Bytes is not null;

	/// <summary>How it compares with the other side's picture at the same place.</summary>
	public string Note
	{
		get => note;
		internal set
		{
			note = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Note)));
		}
	}

	/// <summary>B's picture against A's: the same bytes, the same pixels in other bytes, how many pixels differ, or another size.</summary>
	internal static string AgainstA(EmbeddedPictureVm shownA, EmbeddedPictureVm shownB)
	{
		if (shownA.Bytes is not null && shownB.Bytes is not null && shownA.Bytes.AsSpan().SequenceEqual(shownB.Bytes))
		{
			return "The same bytes as A's.";
		}

		if (shownA.IsVector && shownB.IsVector)
		{
			// Both drawn at the same size, compared by their pixels: other text can be the same drawing.
			var renderedA = SvgRender.Rendered(shownA.Image, shownA.ViewWidth, shownA.ViewHeight);
			var renderedB = SvgRender.Rendered(shownB.Image, shownB.ViewWidth, shownB.ViewHeight);
			var pa = ImagePreview.Pixels(renderedA);
			var pb = ImagePreview.Pixels(renderedB);
			if (renderedA is null || renderedB is null || pa is null || pb is null || pa.Length != pb.Length || renderedA.PixelWidth != renderedB.PixelWidth)
			{
				return "Another drawing than A's (another shape of view box).";
			}

			int changed = EmbeddedPictures.DifferingPixels(pa, pb);
			return changed == 0
				? "The same drawing as A's, in other text."
				: $"{Format.Count(changed)} of {Format.Count(pa.Length / 4)} pixels differ from A's (both drawn at {SvgRender.CompareSide} px).";
		}

		if (shownA.IsSvg && shownB.IsSvg)
		{
			return "Another text than A's.";
		}

		if (!shownA.HasImage || !shownB.HasImage)
		{
			return string.Empty;
		}

		if (shownA.PixelWidth != shownB.PixelWidth || shownA.PixelHeight != shownB.PixelHeight)
		{
			return $"Another size than A's ({Format.Count(shownA.PixelWidth)}×{Format.Count(shownA.PixelHeight)} px).";
		}

		var pixelsA = ImagePreview.Pixels(shownA.Image);
		var pixelsB = ImagePreview.Pixels(shownB.Image);
		if (pixelsA is null || pixelsB is null || pixelsA.Length != pixelsB.Length)
		{
			return string.Empty;
		}

		int differing = EmbeddedPictures.DifferingPixels(pixelsA, pixelsB);
		int total = pixelsA.Length / 4;
		return differing == 0
			? "The same picture as A's, in other bytes."
			: $"{Format.Count(differing)} of {Format.Count(total)} pixels differ from A's.";
	}
}

/// <summary>The snapshots and the page a file's rows come from: what an embedded picture's window says.</summary>
/// <param name="Page">The page's address (its saved name when no index names it).</param>
public sealed record PictureSource(string SnapshotA, string SnapshotB, string Page)
{
	/// <summary>
	/// A name to save a page's picture under: the last part of its address, without its
	/// extension, query or the hash a saved file's name starts with; "page" when nothing is left.
	/// </summary>
	public static string NameOf(string? address)
	{
		var last = (address ?? string.Empty).Split('?', '#')[0].TrimEnd('/');
		last = last[(last.LastIndexOf('/') + 1)..];
		last = System.Text.RegularExpressions.Regex.Replace(last, "^[0-9a-f]{64}", string.Empty);
		last = Path.GetFileNameWithoutExtension(last);
		var clean = new string(last.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
		return clean.Length == 0 ? "page" : clean;
	}
}

/// <summary>A part of a PDF only one side has, as the File tab lists it.</summary>
public sealed record PdfPartRow(string Side, string SideStyle, PdfPart Part)
{
	public string Kind => Part.Kind;

	public string Size => Format.Bytes(Part.Size);

	public string Text => Part.Text;
}
