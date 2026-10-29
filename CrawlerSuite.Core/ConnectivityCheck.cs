using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace CrawlerSuite.Core
{
	/// <summary>
	/// Checks connectivity to the configured target URL before the crawl starts.
	/// Tests DNS resolution, HTTP HEAD, and proxy reachability.
	/// Results are appended to connectivity.log in the site root folder.
	/// </summary>
	public static class ConnectivityCheck
	{
		public record ConnectivityResult(
			bool IsOk,
			string MachineName,
			string[] MachineIPs,
			string Username,
			string TargetUrl,
			string TargetHostname,
			string[] ResolvedTargetIPs,
			string? ProxyUrl,
			string[] ResolvedProxyIPs,
			int? HeadStatusCode,
			long HeadResponseMs,
			string? FailureReason);

		/// <summary>
		/// Performs connectivity check. A failure is appended to connectivity.log; a success deletes it.
		/// Returns false and exits app on failure in silent mode.
		/// Returns false in interactive mode when user aborts.
		/// </summary>
		/// <param name="signIn">
		/// The run's proxy sign-in: the check tries it — the Windows sign-in while empty — and,
		/// when the proxy refuses it (407) in an interactive run, asks the operator and tries again
		/// with what was typed, until the proxy accepts or the operator cancels.
		/// </param>
		public static async Task<bool> RunAsync(
			string targetUrl,
			string? proxyUrl,
			string connectivityLogPath,
			bool silent,
			ProxySignIn? signIn = null)
		{
			signIn ??= new ProxySignIn();
			var checkedResult = await SignInAsync(
				(user, password) => CheckAsync(targetUrl, proxyUrl, user, password),
				proxyUrl, silent, signIn,
				(proxy, typedRefused) => RunUi.Prompts.AskProxySignIn(proxy, typedRefused),
				refused => AppendToLog(connectivityLogPath, refused)).ConfigureAwait(false);
			if (checkedResult is not { } result)
			{
				Logger.LogInfo("Aborted by user at the proxy sign-in.");
				return false;
			}

			// connectivity.log holds the failures since the connection last worked: each failed check
			// appends (a debugging session keeps its series), the first successful one deletes the file.
			if (!result.IsOk)
			{
				AppendToLog(connectivityLogPath, result);
			}
			else
			{
				ClearLog(connectivityLogPath);
			}

			if (result.IsOk)
			{
				RunUi.WriteStepRow("Connectivity", string.IsNullOrEmpty(proxyUrl) ? "reachable"
					: signIn.Typed ? "reachable · through the proxy, signed in as typed" : "reachable · through the proxy, Windows sign-in");
				return true;
			}

			// Build failure message.
			var lines = BuildFailureLines(result);

			if (silent)
			{
				// Silent mode — log everything and exit.
				Logger.LogError("Connectivity check failed — see connectivity.log for details.");
				foreach (var line in lines)
				{
					Logger.LogError(line);
				}

				return false;
			}

			// Interactive mode — show details and ask the operator.
			if (!RunUi.Prompts.AskContinueAfterConnectivityFailure(lines, proxyTip: string.IsNullOrEmpty(proxyUrl)))
			{
				Logger.LogInfo("Aborted by user after connectivity check failure.");
				return false;
			}

			Logger.LogWarning("User chose to continue despite connectivity check failure.");
			RunUi.WriteStepRow("Connectivity", "not reachable — continued anyway", dimmed: true);
			return true;
		}

		/// <summary>
		/// The check with the run's sign-in: the Windows sign-in while nothing was typed; on a
		/// refusal (407) in an interactive run, the operator is asked and the check tried again with
		/// what was typed, as long as the proxy refuses. Null when the operator cancelled.
		/// </summary>
		internal static async Task<ConnectivityResult?> SignInAsync(
			Func<string?, string?, Task<ConnectivityResult>> check,
			string? proxyUrl,
			bool silent,
			ProxySignIn signIn,
			Func<string, bool, (string User, string Password)?> ask,
			Action<ConnectivityResult> refused)
		{
			var result = await check(NullIfEmpty(signIn.User), NullIfEmpty(signIn.Password)).ConfigureAwait(false);
			while (!result.IsOk && ProxyCredentialResolution.ShouldAsk(silent, proxyUrl, result.HeadStatusCode))
			{
				refused(result);
				Logger.LogInfo(signIn.Typed
					? "Connectivity: the proxy refused the typed sign-in (407) — asking again."
					: "Connectivity: the proxy refused the Windows sign-in (407) — asking for a user and password.");
				if (ask(proxyUrl!, signIn.Typed) is not { } typed)
				{
					return null;
				}

				signIn.Use(typed.User, typed.Password);
				result = await check(signIn.User, signIn.Password).ConfigureAwait(false);
			}

			return result;
		}

		private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

		// ── Core check ────────────────────────────────────────────────────────────

		private static async Task<ConnectivityResult> CheckAsync(
			string targetUrl, string? proxyUrl,
			string? proxyUser = null, string? proxyPassword = null)
		{
			// Gather machine identity.
			var machineName = Environment.MachineName;
			var username = GetUsername();
			var machineIPs = GetMachineIPs();

			// Parse target hostname.
			Uri? targetUri = null;
			string hostname = "";
			try
			{
				targetUri = new Uri(targetUrl);
				hostname = targetUri.Host;
			}
			catch
			{
				return Fail(machineName, machineIPs, username, targetUrl, hostname,
					[], proxyUrl, [], null, 0, $"Invalid target URL: {targetUrl}");
			}

			// DNS resolve target.
			string[] resolvedTargetIPs = [];
			try
			{
				var addresses = await Dns.GetHostAddressesAsync(hostname);
				resolvedTargetIPs = [.. addresses.Select(a => a.ToString())];
			}
			catch (Exception ex)
			{
				return Fail(machineName, machineIPs, username, targetUrl, hostname,
					[], proxyUrl, [], null, 0,
					$"DNS resolution failed for '{hostname}': {ex.Message}");
			}

			// DNS resolve proxy if configured.
			string[] resolvedProxyIPs = [];
			if (!string.IsNullOrEmpty(proxyUrl))
			{
				try
				{
					var proxyUri = new Uri(proxyUrl);
					var addresses = await Dns.GetHostAddressesAsync(proxyUri.Host);
					resolvedProxyIPs = [.. addresses.Select(a => a.ToString())];
				}
				catch (Exception ex)
				{
					return Fail(machineName, machineIPs, username, targetUrl, hostname,
						resolvedTargetIPs, proxyUrl, [], null, 0,
						$"DNS resolution failed for proxy '{proxyUrl}': {ex.Message}");
				}
			}

			// HTTP HEAD to target URL (through proxy if configured). One place builds the proxy for the
			// check and the crawl, so both sign in the same way. No log line here: the crawl logs the
			// proxy moments later.
			var head = await HeadAsync(targetUrl, ProxyConfig.Build(proxyUrl, proxyUser, proxyPassword, logContext: null));
			if (head.Error is not null || !IsReachable(head.Status!.Value))
			{
				return Fail(machineName, machineIPs, username, targetUrl, hostname,
					resolvedTargetIPs, proxyUrl, resolvedProxyIPs, head.Status, head.Ms,
					head.Error is not null ? $"HEAD request failed: {head.Error}" : $"HEAD request returned HTTP {head.Status}");
			}

			int? statusCode = head.Status;
			long responseMs = head.Ms;

			return new ConnectivityResult(
				IsOk: true,
				MachineName: machineName,
				MachineIPs: machineIPs,
				Username: username,
				TargetUrl: targetUrl,
				TargetHostname: hostname,
				ResolvedTargetIPs: resolvedTargetIPs,
				ProxyUrl: proxyUrl,
				ResolvedProxyIPs: resolvedProxyIPs,
				HeadStatusCode: statusCode,
				HeadResponseMs: responseMs,
				FailureReason: null);
		}

		/// <summary>
		/// The check's request: HEAD, or GET (headers only) for a server that refuses HEAD. The status
		/// is the answer's, or the one a failed request carries: a proxy refusing an https tunnel
		/// (CONNECT) answers with 407 as an error, not a response, and the sign-in prompt depends on
		/// seeing it. Error is null when an answer came.
		/// </summary>
		internal static async Task<(int? Status, long Ms, string? Error)> HeadAsync(string targetUrl, IWebProxy? proxy)
		{
			int? statusCode = null;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				// [KEEP] No redirect is followed: the check asks only whether the configured address answers,
				// and the app never goes to a host it was not pointed at — a redirect to another host, an IP
				// literal or an internal address would be requested otherwise. Any 3xx counts as reachable.
				// No proxy configured means a direct connection, exactly like the crawl — never the
				// machine's system or environment proxy.
				using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = proxy != null, Proxy = proxy };
				using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
				using var headRequest = new HttpRequestMessage(HttpMethod.Head, targetUrl);
				var response = await client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
				if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
				{
					response.Dispose();
					using var getRequest = new HttpRequestMessage(HttpMethod.Get, targetUrl);
					response = await client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
				}

				using var disposeResponse = response;
				return ((int)response.StatusCode, sw.ElapsedMilliseconds, null);
			}
			catch (Exception ex)
			{
				if (ex is HttpRequestException { StatusCode: { } code })
				{
					statusCode = (int)code;
				}

				return (statusCode, sw.ElapsedMilliseconds, ex.Message);
			}
		}

		// ── Logging ───────────────────────────────────────────────────────────────

		internal static void AppendToLog(string logPath, ConnectivityResult r)
		{
			var sb = new StringBuilder();
			sb.AppendLine($"──────────────────────────────────────────────────────────────");
			sb.AppendLine($"Timestamp   : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}");
			sb.AppendLine($"Result      : {(r.IsOk ? "OK" : "FAILED")}");
			sb.AppendLine($"Machine     : {r.MachineName}");
			sb.AppendLine($"Machine IPs : {string.Join(", ", r.MachineIPs)}");
			sb.AppendLine($"Username    : {r.Username}");
			sb.AppendLine($"Target URL  : {r.TargetUrl}");
			sb.AppendLine($"Target host : {r.TargetHostname}");
			sb.AppendLine($"Resolved IPs: {string.Join(", ", r.ResolvedTargetIPs)}");
			if (!string.IsNullOrEmpty(r.ProxyUrl))
			{
				sb.AppendLine($"Proxy URL   : {r.ProxyUrl}");
				sb.AppendLine($"Proxy IPs   : {string.Join(", ", r.ResolvedProxyIPs)}");
			}
			if (r.HeadStatusCode.HasValue)
			{
				sb.AppendLine($"HEAD status : {r.HeadStatusCode} ({r.HeadResponseMs} ms)");
			}

			if (!string.IsNullOrEmpty(r.FailureReason))
			{
				sb.AppendLine($"Failure     : {r.FailureReason}");
			}

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
				File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not write connectivity.log: {ex.Message}");
			}
		}

		/// <summary>Deletes connectivity.log after a successful check; a file that cannot be deleted is logged, never fatal.</summary>
		internal static void ClearLog(string logPath)
		{
			try
			{
				if (File.Exists(logPath))
				{
					File.Delete(logPath);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Logger.LogWarning($"Could not delete connectivity.log: {ex.Message}");
			}
		}

		// [internal for test access — pure formatting helper, see ConnectivityCheckTests]
		internal static List<string> BuildFailureLines(ConnectivityResult r)
		{
			List<string> lines =
			[
				$"Machine     : {r.MachineName}",
				$"Machine IPs : {string.Join(", ", r.MachineIPs)}",
				$"Username    : {r.Username}",
				$"Target URL  : {r.TargetUrl}",
				$"Resolved IPs: {(r.ResolvedTargetIPs.Length > 0 ? string.Join(", ", r.ResolvedTargetIPs) : "— DNS failed")}",
			];
			if (!string.IsNullOrEmpty(r.ProxyUrl))
			{
				lines.Add($"Proxy URL   : {r.ProxyUrl}");
				lines.Add($"Proxy IPs   : {(r.ResolvedProxyIPs.Length > 0 ? string.Join(", ", r.ResolvedProxyIPs) : "— DNS failed")}");
			}
			if (r.HeadStatusCode.HasValue)
			{
				lines.Add($"HEAD status : {r.HeadStatusCode} ({r.HeadResponseMs} ms)");
			}

			lines.Add($"Reason      : {r.FailureReason}");
			return lines;
		}

		// ── Helpers ───────────────────────────────────────────────────────────────

		private static string GetUsername()
		{
			try
			{
				if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				{
					// WindowsIdentity gives the impersonated account when run via Task Scheduler.
					return System.Security.Principal.WindowsIdentity.GetCurrent().Name;
				}
				return Environment.UserName;
			}
			catch
			{
				return Environment.UserName;
			}
		}

		private static string[] GetMachineIPs()
		{
			try
			{
				return [..Dns.GetHostAddresses(Dns.GetHostName())
					.Where(a => a.AddressFamily == AddressFamily.InterNetwork
						|| a.AddressFamily == AddressFamily.InterNetworkV6)
					.Select(a => a.ToString())];
			}
			catch
			{
				return [];
			}
		}

		private static ConnectivityResult Fail(
			string machineName, string[] machineIPs, string username,
			string targetUrl, string hostname,
			string[] resolvedTargetIPs, string? proxyUrl, string[] resolvedProxyIPs,
			int? statusCode, long responseMs, string reason) =>
			new(false, machineName, machineIPs, username, targetUrl, hostname,
				resolvedTargetIPs, proxyUrl, resolvedProxyIPs, statusCode, responseMs, reason);

		/// <summary>The site answers: 2xx, or any redirect (it is not followed — the crawl decides on it through the admission gate).</summary>
		internal static bool IsReachable(int statusCode) => statusCode is >= 200 and < 400;
	}
}
