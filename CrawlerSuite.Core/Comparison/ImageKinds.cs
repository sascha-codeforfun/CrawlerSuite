namespace CrawlerSuite.Core.Comparison
{
	/// <summary>
	/// The saved files the Comparison shows as pictures, by their extension: the raster formats
	/// Windows reads (WebP only with its codec installed). SVG is not among them: it is text and is
	/// compared line by line.
	/// </summary>
	public static class ImageKinds
	{
		private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
		{
			".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".webp", ".jxr", ".wdp",
		};

		/// <summary>Whether a saved file (by name or path) is a picture.</summary>
		public static bool IsImage(string name)
		{
			var extension = Path.GetExtension(name);
			return extension.Length > 0 && Extensions.Contains(extension);
		}
	}
}
