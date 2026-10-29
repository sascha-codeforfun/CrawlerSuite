namespace CrawlerSuite.Core
{
	/// <summary>
	/// Who signs in to the proxy. Credentials are never part of the configuration: the
	/// proxy's address is a setting, who signs in is not.
	///
	///   * Every crawl tries the Windows sign-in first (ProxyConfig.Build's UseDefaultCredentials
	///     branch): the connectivity check goes through the proxy with it, nothing is asked.
	///   * Only when the proxy refuses (HTTP 407) is the operator asked for a user and a password,
	///     and asked again while the proxy refuses what was typed. Any other failure asks nothing:
	///     a password would not help. An unattended run never asks.
	///   * What is typed lives in the run (<see cref="ProxySignIn"/>): a start over keeps it, the
	///     next crawl tries Windows again.
	///
	/// This type is the testable half; the prompt itself is the app's ProxyCredentialsDialog.
	/// </summary>
	internal static class ProxyCredentialResolution
	{
		/// <summary>HTTP 407 Proxy Authentication Required.</summary>
		internal const int ProxyAuthenticationRequired = 407;

		/// <summary>Whether a check's outcome is a refused sign-in the operator can answer. Pure.</summary>
		internal static bool ShouldAsk(bool silent, string? proxyUrl, int? statusCode) =>
			!silent && !string.IsNullOrWhiteSpace(proxyUrl) && statusCode == ProxyAuthenticationRequired;
	}

	/// <summary>
	/// The proxy sign-in of one run: empty means the Windows sign-in; a user and password once the
	/// operator typed them after a refusal. Lives for the run and is dropped with it; never stored.
	/// </summary>
	public sealed class ProxySignIn
	{
		public string User { get; private set; } = string.Empty;

		public string Password { get; private set; } = string.Empty;

		/// <summary>True once the operator typed a sign-in; false while the Windows sign-in is used.</summary>
		public bool Typed => User.Length > 0;

		internal void Use(string user, string password)
		{
			User = user;
			Password = password;
		}
	}
}
