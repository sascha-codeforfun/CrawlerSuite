using System;
using System.Globalization;
using CrawlerSuite.Core.Display;

namespace CrawlerSuite.Run;

/// <summary>Number and time formatting shared by the views.</summary>
public static class Format
{
	public static string Bytes(long bytes)
	{
		string[] units = ["B", "KB", "MB", "GB", "TB"];
		double size = bytes;
		int u = 0;
		while (size >= 1024 && u < units.Length - 1)
		{
			size /= 1024;
			u++;
		}

		// "0.#" never groups, so 1 023 B is the largest value that would need it: group explicitly.
		return (u == 0 ? NumberText.Group(bytes) : size.ToString(size >= 1000 ? "#,0.#" : "0.#", NumberText.Format)) + " " + units[u];
	}

	public static string Duration(TimeSpan t) =>
		t.TotalHours >= 1
			? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
			: $"{(int)t.TotalMinutes}:{t.Seconds:00}";

	/// <summary>ISO grouping with a narrow no-break space: 1 234 567.</summary>
	public static string Count(int n) => NumberText.Group(n);

	public static string Count(long n) => NumberText.Group(n);
}
