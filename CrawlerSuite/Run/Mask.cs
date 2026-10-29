using System;
using System.IO;
using CrawlerSuite.Core;
using CrawlerSuite.Core.Display;

namespace CrawlerSuite.Run;

/// <summary>
/// The window's mask for screenshots and shared screens. The level is picked in the banner and
/// lives only in memory: every start is <see cref="MaskLevel.Clear"/>. Text reaches the screen
/// through <see cref="Apply"/> (bindings use <c>views:Masked</c>), so switching the level
/// re-renders everything already shown. Files and logs are never masked.
/// </summary>
public sealed class Mask : Observable
{
	private readonly ScreenMask rules = new();
	private MaskLevel level = MaskLevel.Clear;

	private Mask()
	{
		rules.LearnFolder(AppPaths.BaseDirectory);
		rules.LearnFolder(Path.GetTempPath());
		rules.LearnFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
	}

	public static Mask Instance { get; } = new();

	public MaskLevel Level
	{
		get => level;
		set
		{
			if (Set(ref level, value))
			{
				Raise(nameof(IsActive));
			}
		}
	}

	public bool IsActive => level != MaskLevel.Clear;

	/// <summary>Learns what a (re)loaded configuration names. Names learnt earlier stay masked.</summary>
	public void Learn(Config? config, string? configPath)
	{
		rules.Learn(config, configPath);
		if (IsActive)
		{
			// Re-render what is on screen: names learnt just now may already be shown.
			Raise(nameof(Level));
		}
	}

	/// <summary>Masks text at the strictest level, whatever the screen shows — for copies meant to be shared.</summary>
	public static string ApplyStrict(string? text) => Instance.rules.Apply(text, MaskLevel.SchemeOnly);

	/// <summary>Masks display text at the current level.</summary>
	public static string Apply(string? text) => Instance.rules.Apply(text, Instance.level);

	/// <summary>Masks the value of a URL attribute in HTML (src, href, srcset …, quotes included) at the current level.</summary>
	public static string ApplyToAttributeValue(string? value) => Instance.rules.ApplyToAttributeValue(value, Instance.level);

	/// <summary>Masks a site-relative URL path ("/en/products/") at the current level.</summary>
	public static string ApplyToUrlPath(string? path) => Instance.rules.ApplyToUrlPath(path, Instance.level);

	/// <summary>A site's name as the screen shows it: its label ("Primary site", "Site 2") while masked.</summary>
	public static string SiteName(string? name) =>
		!Instance.IsActive || string.IsNullOrWhiteSpace(name) ? name ?? string.Empty : Instance.rules.SiteLabel(name);

	/// <summary>What a bound value shows: site items get their label, everything else is masked text.</summary>
	public static string Display(object? value) => value switch
	{
		null => string.Empty,
		SiteItem site when Instance.IsActive => Instance.rules.SiteLabel(site.Name),
		SiteItem site => site.Label,
		string text => Apply(text),
		_ => Apply(value.ToString()),
	};
}
