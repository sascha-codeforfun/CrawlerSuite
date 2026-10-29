using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CrawlerSuite.Core.Tests.Port
{
	/// <summary>
	/// A tiny local web site for download tests: each path is answered by a handler that can
	/// send any status, headers and body — including bodies that stall or never announce
	/// their size. Listens on localhost only (host name, not an IP literal, so the crawl gate
	/// admits it).
	/// </summary>
	internal sealed class TestSite : IDisposable
	{
		private readonly HttpListener listener = new();
		private readonly ConcurrentDictionary<string, Func<HttpListenerContext, Task>> routes = new(StringComparer.OrdinalIgnoreCase);
		private readonly CancellationTokenSource stop = new();
		private readonly Task loop;

		public TestSite()
		{
			Port = FreePort();
			BaseUrl = $"http://localhost:{Port}";
			listener.Prefixes.Add(BaseUrl + "/");
			listener.Start();
			loop = Task.Run(AcceptLoop);
		}

		public int Port { get; }

		public string BaseUrl { get; }

		public void Page(string path, string html) => Route(path, async ctx =>
		{
			var bytes = Encoding.UTF8.GetBytes(html);
			ctx.Response.ContentType = "text/html; charset=utf-8";
			ctx.Response.ContentLength64 = bytes.Length;
			await ctx.Response.OutputStream.WriteAsync(bytes);
		});

		public void Bytes(string path, string contentType, int length, bool announceLength)
			=> Route(path, async ctx =>
			{
				ctx.Response.ContentType = contentType;
				if (announceLength)
				{
					ctx.Response.ContentLength64 = length;
				}
				else
				{
					ctx.Response.SendChunked = true;
				}

				var chunk = new byte[16 * 1024];
				chunk[0] = (byte)'%'; chunk[1] = (byte)'P'; chunk[2] = (byte)'D'; chunk[3] = (byte)'F'; chunk[4] = (byte)'-';
				int sent = 0;
				try
				{
					while (sent < length)
					{
						int n = Math.Min(chunk.Length, length - sent);
						await ctx.Response.OutputStream.WriteAsync(chunk.AsMemory(0, n));
						sent += n;
						if (sent > 0)
						{
							chunk[0] = 0; // only the first chunk carries the signature
						}
					}
				}
				catch (Exception) when (true)
				{
					// The client hung up (expected for oversized files).
				}
			});

		/// <summary>Sends headers and a few bytes, then goes silent for <paramref name="silence"/>.</summary>
		public void Stall(string path, TimeSpan silence) => Route(path, async ctx =>
		{
			ctx.Response.ContentType = "text/html";
			ctx.Response.SendChunked = true;
			try
			{
				await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("<html><body>"));
				await ctx.Response.OutputStream.FlushAsync();
				await Task.Delay(silence, stop.Token);
			}
			catch (Exception) when (true)
			{
			}
		});

		/// <summary>Says nothing for <paramref name="delay"/> (no status, no headers), then serves the page.</summary>
		public void SlowAnswer(string path, TimeSpan delay, string html) => Route(path, async ctx =>
		{
			try
			{
				await Task.Delay(delay, stop.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			var bytes = Encoding.UTF8.GetBytes(html);
			ctx.Response.ContentType = "text/html; charset=utf-8";
			ctx.Response.ContentLength64 = bytes.Length;
			await ctx.Response.OutputStream.WriteAsync(bytes);
		});

		public void Route(string path, Func<HttpListenerContext, Task> handler) => routes[path] = handler;

		private async Task AcceptLoop()
		{
			while (!stop.IsCancellationRequested)
			{
				HttpListenerContext ctx;
				try
				{
					ctx = await listener.GetContextAsync();
				}
				catch (Exception)
				{
					return;
				}

				_ = Task.Run(async () =>
				{
					try
					{
						if (routes.TryGetValue(ctx.Request.Url!.AbsolutePath, out var handler))
						{
							await handler(ctx);
						}
						else
						{
							ctx.Response.StatusCode = 404;
						}
					}
					catch (Exception)
					{
					}
					finally
					{
						try { ctx.Response.Close(); } catch (Exception) { }
					}
				});
			}
		}

		private static int FreePort()
		{
			var l = new TcpListener(IPAddress.Loopback, 0);
			l.Start();
			int port = ((IPEndPoint)l.LocalEndpoint).Port;
			l.Stop();
			return port;
		}

		public void Dispose()
		{
			stop.Cancel();
			try { listener.Stop(); listener.Close(); } catch (Exception) { }
			try { loop.Wait(2000); } catch (Exception) { }
		}
	}
}
