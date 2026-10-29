using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Core.Settings;

namespace CrawlerSuite.Views;

/// <summary>
/// Fills a TextBlock with text pieces and marks the hits: <see cref="PiecesProperty"/> the settings
/// search's hits (soft accent); <see cref="RemovedPiecesProperty"/> and <see cref="AddedPiecesProperty"/>
/// the pieces of a changed line that really differ, on A's side solid red, on B's solid green, both
/// with white text — strong on the row's own red or green tint, so what changed inside it stands out.
/// </summary>
public static class Highlight
{
	public static readonly DependencyProperty PiecesProperty =
		DependencyProperty.RegisterAttached("Pieces", typeof(IReadOnlyList<TextPiece>), typeof(Highlight),
			new PropertyMetadata(null, (target, e) => Fill(target, e.NewValue, "Brush.AccentSoft", "Brush.Text")));

	/// <summary>A's side of a changed line: the pieces B does not have.</summary>
	public static readonly DependencyProperty RemovedPiecesProperty =
		DependencyProperty.RegisterAttached("RemovedPieces", typeof(IReadOnlyList<TextPiece>), typeof(Highlight),
			new PropertyMetadata(null, (target, e) => Fill(target, e.NewValue, "Brush.ErrorSolid", "Brush.OnError")));

	/// <summary>B's side of a changed line: the pieces A does not have.</summary>
	public static readonly DependencyProperty AddedPiecesProperty =
		DependencyProperty.RegisterAttached("AddedPieces", typeof(IReadOnlyList<TextPiece>), typeof(Highlight),
			new PropertyMetadata(null, (target, e) => Fill(target, e.NewValue, "Brush.OkSolid", "Brush.OnOk")));

	public static IReadOnlyList<TextPiece>? GetPieces(DependencyObject target) => (IReadOnlyList<TextPiece>?)target.GetValue(PiecesProperty);

	public static void SetPieces(DependencyObject target, IReadOnlyList<TextPiece>? value) => target.SetValue(PiecesProperty, value);

	public static IReadOnlyList<TextPiece>? GetRemovedPieces(DependencyObject target) => (IReadOnlyList<TextPiece>?)target.GetValue(RemovedPiecesProperty);

	public static void SetRemovedPieces(DependencyObject target, IReadOnlyList<TextPiece>? value) => target.SetValue(RemovedPiecesProperty, value);

	public static IReadOnlyList<TextPiece>? GetAddedPieces(DependencyObject target) => (IReadOnlyList<TextPiece>?)target.GetValue(AddedPiecesProperty);

	public static void SetAddedPieces(DependencyObject target, IReadOnlyList<TextPiece>? value) => target.SetValue(AddedPiecesProperty, value);

	private static void Fill(DependencyObject target, object? pieces, string hitBackground, string hitForeground)
	{
		if (target is not TextBlock block)
		{
			return;
		}

		block.Inlines.Clear();
		foreach (var piece in pieces as IReadOnlyList<TextPiece> ?? [])
		{
			var run = new System.Windows.Documents.Run(piece.Text);
			if (piece.Hit)
			{
				run.SetResourceReference(System.Windows.Documents.TextElement.BackgroundProperty, hitBackground);
				run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, hitForeground);
				run.FontWeight = FontWeights.SemiBold;
			}

			block.Inlines.Add(run);
		}
	}
}
