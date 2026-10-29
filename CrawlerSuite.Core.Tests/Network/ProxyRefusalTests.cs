using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CrawlerSuite.Core.Tests.Network
{
	/// <summary>
	/// A proxy's refusal (407) is seen for http and https targets alike: through a proxy an https
	/// request first opens a tunnel, and a refused tunnel comes back as an error, not a response.
	/// Against a proxy on loopback that refuses everything; no network, no clock.
	/// </summary>
	public class ProxyRefusalTests
	{
		/// <summary>Sends every request to the loopback proxy (a plain WebProxy would bypass loopback targets).</summary>
		private sealed class LoopbackProxy(int port) : IWebProxy
		{
			public ICredentials? Credentials { get; set; }

			public Uri GetProxy(Uri destination) => new($"http://127.0.0.1:{port}/");

			public bool IsBypassed(Uri host) => false;
		}

		private static async Task RefuseAll(TcpListener listener, CancellationToken stop)
		{
			while (!stop.IsCancellationRequested)
			{
				TcpClient client;
				try
				{
					client = await listener.AcceptTcpClientAsync(stop);
				}
				catch (OperationCanceledException)
				{
					return;
				}

				using (client)
				{
					var stream = client.GetStream();
					var buffer = new byte[8192];
					var seen = new StringBuilder();
					while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
					{
						int n = await stream.ReadAsync(buffer, stop);
						if (n == 0)
						{
							break;
						}

						seen.Append(Encoding.ASCII.GetString(buffer, 0, n));
					}

					var answer = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
					await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), stop);
				}
			}
		}

		[Theory]
		[InlineData("https://site.test/")] // a refused CONNECT: an error carrying 407
		[InlineData("http://site.test/")]  // a plain request: a 407 response
		public async Task ARefusingProxy_Gives407(string target)
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			using var stop = new CancellationTokenSource();
			var proxy = new LoopbackProxy(((IPEndPoint)listener.LocalEndpoint).Port);
			var serve = RefuseAll(listener, stop.Token);
			try
			{
				var (status, _, _) = await ConnectivityCheck.HeadAsync(target, proxy);
				Assert.Equal(407, status);

				// What was typed is refused the same way.
				proxy.Credentials = new NetworkCredential("floe", "tide");
				var (typed, _, _) = await ConnectivityCheck.HeadAsync(target, proxy);
				Assert.Equal(407, typed);
			}
			finally
			{
				stop.Cancel();
				listener.Stop();
				try { await serve; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
			}
		}
	}
}
