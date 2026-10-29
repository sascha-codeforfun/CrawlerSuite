using System;
using System.Windows;
using System.Windows.Threading;
using CrawlerSuite.Core;
using CrawlerSuite.Run;
using HueThemeKit;

namespace CrawlerSuite;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		HueContrast.Follow(this);

		// Relative paths in the configuration (dictionaries, CMS lists) resolve from the
		// program folder, wherever the app is started from.
		Environment.CurrentDirectory = AppPaths.BaseDirectory;
		CrawlerApp.InitializeProcess();

		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
			Logger.LogError($"Unhandled exception: {args.ExceptionObject}");
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		Logger.LogError($"Unhandled UI exception: {e.Exception}");
		// Owner only when the window is up: a crash during its construction must not crash here too.
		HueMessage.Show(MainWindow is { IsLoaded: true } window ? window : null, "Something went wrong",
			Mask.Apply(e.Exception.Message) + Environment.NewLine + Environment.NewLine + "Details are in application.log next to the program.");
		e.Handled = true;
	}
}
