using System;
using System.Windows;
using System.Windows.Controls;

namespace CrawlerSuite.Views;

/// <summary>The "Copy for AI" buttons: text to the clipboard, a short confirmation on the button.</summary>
internal static class CopyForAi
{
	/// <summary>The app version as the copies name it (major.minor.build).</summary>
	public static string AppVersion() =>
		System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "2.0.0";

	// The button's own label while "Copied" shows, and its running timer: a second click inside the
	// two seconds restarts the timer instead of taking "Copied" for the label.
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, Showing> Shown = new();

	private sealed class Showing(object? label, System.Windows.Threading.DispatcherTimer timer)
	{
		public object? Label { get; } = label;

		public System.Windows.Threading.DispatcherTimer Timer { get; } = timer;
	}

	/// <summary>Puts <paramref name="text"/> on the clipboard and shows "Copied" on the button for two seconds.</summary>
	public static void Copy(Button button, string text)
	{
		try
		{
			Clipboard.SetText(text);
		}
		catch (System.Runtime.InteropServices.ExternalException)
		{
			return; // the clipboard is held by another program; a second click will do
		}

		if (Shown.TryGetValue(button, out var showing))
		{
			showing.Timer.Stop();
			showing.Timer.Start();
			return;
		}

		var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
		var entry = new Showing(button.Content, timer);
		Shown.Add(button, entry);
		button.Content = "Copied";
		timer.Tick += (_, _) =>
		{
			timer.Stop();
			button.Content = entry.Label;
			Shown.Remove(button);
		};
		timer.Start();
	}
}
