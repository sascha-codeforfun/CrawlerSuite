using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CrawlerSuite.Core.Comparison;

namespace CrawlerSuite.Run;

/// <summary>One side of a picture: the decoded image (frozen, so any thread may hand it over), its pixel size, or why it is not shown.</summary>
public sealed record ImageSide(ImageSource? Image, int PixelWidth, int PixelHeight, long Bytes, string Problem)
{
	public bool HasImage => Image is not null;
}

/// <summary>
/// Decodes a saved picture for the Comparison's File tab. The file is read into memory first, so
/// nothing in the snapshot stays locked; a very large picture is decoded at screen size.
/// </summary>
public static class ImagePreview
{
	/// <summary>Pictures wider or taller than this are decoded scaled down to it.</summary>
	public const int MaxSide = 4096;

	/// <summary>The picture at a path; null when there is no file there.</summary>
	public static ImageSide? Load(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}

		long bytes = new FileInfo(path).Length;
		if (bytes > SnapshotComparison.MaxShownBytes)
		{
			return new ImageSide(null, 0, 0, bytes, "Not shown: larger than " + Format.Bytes(SnapshotComparison.MaxShownBytes) + ".");
		}

		try
		{
			return Decode(File.ReadAllBytes(path));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return new ImageSide(null, 0, 0, bytes, $"Not shown: {ex.Message}");
		}
	}

	/// <summary>A picture from its bytes (a saved file's, or one embedded in a page as base64).</summary>
	public static ImageSide Decode(byte[] data)
	{
		long bytes = data.LongLength;
		try
		{
			// The size first, without decoding the pixels.
			int width;
			int height;
			using (var probe = new MemoryStream(data, writable: false))
			{
				var frame = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
				width = frame.PixelWidth;
				height = frame.PixelHeight;
			}

			var image = new BitmapImage();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
			image.StreamSource = new MemoryStream(data, writable: false);
			if (width > MaxSide || height > MaxSide)
			{
				if (width >= height)
				{
					image.DecodePixelWidth = MaxSide;
				}
				else
				{
					image.DecodePixelHeight = MaxSide;
				}
			}

			image.EndInit();
			image.Freeze();
			return new ImageSide(image, width, height, bytes, string.Empty);
		}
		catch (NotSupportedException)
		{
			return new ImageSide(null, 0, 0, bytes,
				"Not shown: Windows has no decoder for this picture type (WebP needs the WebP Image Extensions from the Microsoft Store).");
		}
		catch (Exception ex) when (ex is FileFormatException or ArgumentException or OverflowException or InvalidOperationException
			or System.Runtime.InteropServices.ExternalException)
		{
			return new ImageSide(null, 0, 0, bytes, $"Not shown: the picture is damaged or cut short ({ex.Message})");
		}
		catch (IOException ex)
		{
			return new ImageSide(null, 0, 0, bytes, $"Not shown: {ex.Message}");
		}
	}

	/// <summary>A decoded picture's pixels as 32-bit BGRA, for comparing two; null when they cannot be read.</summary>
	public static byte[]? Pixels(ImageSource? image)
	{
		if (image is not BitmapSource source)
		{
			return null;
		}

		try
		{
			var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
			var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
			bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
			return pixels;
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException
			or System.Runtime.InteropServices.ExternalException)
		{
			return null;
		}
	}
}
