using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Result = CrawlerSuite.Core.ConnectivityCheck.ConnectivityResult;

namespace CrawlerSuite.Core.Tests.Network
{
	/// <summary>
	/// Who signs in to the proxy: the configuration holds no credentials; every crawl
	/// tries the Windows sign-in first and asks for a user and password only when the proxy
	/// refuses it (407), again while it refuses what was typed. Invented addresses.
	/// </summary>
	[Collection("Logger")]
	public class ProxyCredentialResolutionTests
	{
		private const string Proxy = "http://proxy.test:8080";

		[Theory]
		[InlineData(false, Proxy, 407, true)]
		[InlineData(true, Proxy, 407, false)]   // unattended: never asks
		[InlineData(false, "", 407, false)]     // no proxy: a 407 is the site's, not ours to answer
		[InlineData(false, Proxy, 403, false)]  // any other failure: a password would not help
		[InlineData(false, Proxy, null, false)] // no answer at all
		public void OnlyARefusedSignIn_IsAsked(bool silent, string proxyUrl, int? status, bool asks) =>
			Assert.Equal(asks, ProxyCredentialResolution.ShouldAsk(silent, proxyUrl, status));

		[Fact]
		public void TheConfiguration_HasNoCredentials()
		{
			Assert.Null(typeof(Config).GetProperty("ProxyUser"));
			Assert.Null(typeof(Config).GetProperty("ProxyPassword"));
		}

		private static Result With(int? status) =>
			new(status is >= 200 and < 400, "m", [], "u", "https://site.test/", "site.test", [], Proxy, [], status, 1, status is >= 200 and < 400 ? null : "refused");

		/// <summary>A proxy that accepts only the given user and password; null user = the Windows sign-in.</summary>
		private static (List<(string? User, string? Password)> Tried, System.Func<string?, string?, Task<Result>> Check) ProxyAccepting(string? user, string? password)
		{
			var tried = new List<(string?, string?)>();
			return (tried, (u, p) =>
			{
				tried.Add((u, p));
				return Task.FromResult(With(u == user && p == password ? 200 : 407));
			});
		}

		[Fact]
		public async Task TheWindowsSignIn_IsTriedFirst_AndNothingIsAsked()
		{
			var (tried, check) = ProxyAccepting(null, null);
			var signIn = new ProxySignIn();

			var result = await ConnectivityCheck.SignInAsync(check, Proxy, silent: false, signIn,
				(_, _) => throw new Xunit.Sdk.XunitException("asked"), _ => { });

			Assert.True(result!.IsOk);
			Assert.Equal([(null, null)], tried);
			Assert.False(signIn.Typed);
		}

		[Fact]
		public async Task ARefusal_Asks_UntilTheProxyAccepts()
		{
			var (tried, check) = ProxyAccepting("floe", "tide");
			var answers = new Queue<(string, string)>([("floe", "wrong"), ("floe", "tide")]);
			var asked = new List<bool>();
			int logged = 0;
			var signIn = new ProxySignIn();

			var result = await ConnectivityCheck.SignInAsync(check, Proxy, silent: false, signIn,
				(_, typedRefused) => { asked.Add(typedRefused); return answers.Dequeue(); }, _ => logged++);

			Assert.True(result!.IsOk);
			Assert.Equal([(null, null), ("floe", "wrong"), ("floe", "tide")], tried);
			Assert.Equal([false, true], asked); // first the Windows sign-in was refused, then the typed one
			Assert.Equal(2, logged);
			Assert.True(signIn.Typed);
			Assert.Equal("tide", signIn.Password);
		}

		[Fact]
		public async Task CancellingTheSignIn_CancelsTheCrawl()
		{
			var (_, check) = ProxyAccepting("floe", "tide");

			Assert.Null(await ConnectivityCheck.SignInAsync(check, Proxy, silent: false, new ProxySignIn(), (_, _) => null, _ => { }));
		}

		[Fact]
		public async Task AStartOver_KeepsWhatWasTyped()
		{
			var (tried, check) = ProxyAccepting("floe", "tide");
			var signIn = new ProxySignIn();
			await ConnectivityCheck.SignInAsync(check, Proxy, false, signIn, (_, _) => ("floe", "tide"), _ => { });
			tried.Clear();

			var again = await ConnectivityCheck.SignInAsync(check, Proxy, false, signIn, (_, _) => throw new Xunit.Sdk.XunitException("asked"), _ => { });

			Assert.True(again!.IsOk);
			Assert.Equal([("floe", "tide")], tried);
		}

		[Fact]
		public async Task AnUnattendedRun_NeverAsks()
		{
			var (_, check) = ProxyAccepting("floe", "tide");

			var result = await ConnectivityCheck.SignInAsync(check, Proxy, silent: true, new ProxySignIn(),
				(_, _) => throw new Xunit.Sdk.XunitException("asked"), _ => { });

			Assert.False(result!.IsOk);
			Assert.Equal(407, result.HeadStatusCode);
		}
	}
}
