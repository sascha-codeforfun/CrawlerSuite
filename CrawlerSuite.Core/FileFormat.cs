namespace CrawlerSuite.Core
{
	using System.Collections.Generic;
	using System.IO;
	using System.Text;

	/// <summary>
	/// The byte-level conventions every output file shares, fixed so the files come out
	/// identical whatever operating system writes them: UTF-8 with a byte-order mark and
	/// CRLF line endings, the Windows convention.
	/// </summary>
	public static class FileFormat
	{
		/// <summary>Line ending of every output file.</summary>
		public const string NewLine = "\r\n";

		/// <summary>UTF-8 with byte-order mark.</summary>
		public static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

		/// <summary>Overwrites <paramref name="path"/> with the lines, CRLF-terminated, UTF-8 with BOM.</summary>
		public static void WriteAllLines(string path, IEnumerable<string> lines)
		{
			using var writer = new StreamWriter(path, append: false, Utf8Bom) { NewLine = NewLine };
			foreach (var line in lines)
			{
				writer.WriteLine(line);
			}
		}

		/// <summary>
		/// Appends the lines, CRLF-terminated. Like File.AppendAllLines, the BOM is written
		/// only when the file is new or empty.
		/// </summary>
		public static void AppendAllLines(string path, IEnumerable<string> lines)
		{
			using var writer = new StreamWriter(path, append: true, Utf8Bom) { NewLine = NewLine };
			foreach (var line in lines)
			{
				writer.WriteLine(line);
			}
		}

		/// <summary>Appends text as-is (callers include the line ending). BOM only on a new file.</summary>
		public static void AppendAllText(string path, string text)
		{
			using var writer = new StreamWriter(path, append: true, Utf8Bom);
			writer.Write(text);
		}

		/// <summary>Overwrites <paramref name="path"/> with the text as-is, UTF-8 with BOM.</summary>
		public static void WriteAllText(string path, string text)
		{
			using var writer = new StreamWriter(path, append: false, Utf8Bom);
			writer.Write(text);
		}
	}
}
