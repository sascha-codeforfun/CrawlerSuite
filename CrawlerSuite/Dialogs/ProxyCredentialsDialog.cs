using System;
using System.Windows;
using System.Windows.Controls;
using CrawlerSuite.Core.Display;
using CrawlerSuite.Run;

namespace CrawlerSuite.Dialogs;

/// <summary>
/// Proxy sign-in, asked only when the proxy refused a sign-in: first the Windows sign-in,
/// which the crawl tries without asking, then what was typed here. Nothing is stored: the user
/// starts with the Windows user name and can be changed, the password starts empty and Sign in
/// stays off until it has one — an empty password is no sign-in. What is typed lives for this
/// job only and is dropped after it.
/// </summary>
internal static class ProxyCredentialsDialog
{
	/// <summary>The credentials to use, or null when the operator cancelled the crawl.</summary>
	public static (string User, string Password)? Show(Window owner, string proxyUrl, bool typedSignInRefused)
	{
		var why = typedSignInRefused
			? "The proxy did not accept this sign-in either."
			: "The proxy did not accept your Windows sign-in.";
		var shell = new DialogShell(owner, "Proxy sign-in",
			$"{why} Sign in to the proxy {proxyUrl} for this crawl. Nothing is stored: the next crawl tries your Windows sign-in again.",
			width: 440);

		var userLabel = new TextBlock { Text = "User", Margin = new Thickness(0, 0, 0, 4) };
		userLabel.SetResourceReference(FrameworkElement.StyleProperty, "Text.Caption");
		// While the screen is masked the prefilled account shows as dots; left untouched, it is still used.
		var prefilledUser = Environment.UserName;
		var user = new TextBox { Text = Mask.Instance.IsActive ? ScreenMask.Dots : prefilledUser };
		var passwordLabel = new TextBlock { Text = "Password", Margin = new Thickness(0, 12, 0, 4) };
		passwordLabel.SetResourceReference(FrameworkElement.StyleProperty, "Text.Caption");
		var password = new PasswordBox();
		System.Windows.Automation.AutomationProperties.SetName(user, "Proxy user");
		System.Windows.Automation.AutomationProperties.SetName(password, "Proxy password");

		var panel = new StackPanel();
		panel.Children.Add(userLabel);
		panel.Children.Add(user);
		panel.Children.Add(passwordLabel);
		panel.Children.Add(password);
		shell.Content = panel;

		var cancel = shell.AddButton("Cancel crawl", null, "cancel");
		cancel.IsCancel = true;
		var use = shell.AddButton("Sign in", "Hue.Button.Primary", "use");
		use.IsDefault = true;

		// No user or no password is no sign-in: Sign in (and Enter) do nothing until both are there.
		void Refresh() => use.IsEnabled = user.Text.Trim().Length > 0 && password.Password.Length > 0;
		user.TextChanged += (_, _) => Refresh();
		password.PasswordChanged += (_, _) => Refresh();
		Refresh();

		var result = shell.Show(password);
		return result == "use"
			? (user.Text == ScreenMask.Dots ? prefilledUser : user.Text.Trim(), password.Password)
			: null;
	}
}
