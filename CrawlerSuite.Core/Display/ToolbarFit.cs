namespace CrawlerSuite.Core.Display
{
	/// <summary>
	/// Whether a toolbar's actions still fit on the line beside its fixed part (pickers and the like)
	/// with room left for a one-line status, or go on a line of their own below — so a narrow window
	/// moves them instead of cutting them off or squeezing the status to nothing.
	/// </summary>
	public static class ToolbarFit
	{
		/// <summary>The least width the status keeps beside the actions; below it the actions go to their own line.</summary>
		public const double StatusMinWidth = 160;

		/// <summary>
		/// True when the actions belong on their own line: the fixed part, the actions and the status's
		/// least width together are wider than <paramref name="available"/>. Nothing is laid out yet
		/// (no width): they stay on the line.
		/// </summary>
		public static bool Stack(double available, double fixedPart, double actions, double statusMin = StatusMinWidth)
			=> available > 0 && fixedPart + actions + statusMin > available;
	}
}
