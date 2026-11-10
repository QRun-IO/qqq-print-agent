/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.WebSockets;
using System.Text;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// A QQQ server that runs as several nodes sits behind a load balancer which names the node it chose in a cookie. These tests
/// fix what the session client does with that cookie: it keeps the load balancer's cookies and nothing else, sends them on
/// every later request and on the realtime upgrade so claims reach the node that holds the socket, and follows the load
/// balancer when it rewrites them. They also fix what a refused request means when a node stops:
/// only a 401 ends the session, and a 5xx or a dropped connection leaves it signed in for the next attempt.
/// </summary>
public sealed class LoadBalancerAffinityTests
{
   private static readonly Uri Origin = new("https://printing.example.invalid");
   private const string Discovery = """{"apiVersion":"1.9.0","features":["version","metaData","processes","processCancel","extensions","widgets","nativeSessionLogin"],"clientChannels":["api"],"extensions":[{"name":"printing","version":"2.4.0","pathPrefix":"/qbit/printing"}]}""";
   private const string Table = """{"name":"local","type":"TABLE_BASED"}""";
   private const string Identity = """{"principalId":"user-1","tenantId":"tenant-1","deviceId":"device-1"}""";
   private const string Session = "sessionUUID=session-secret; Path=/; Secure; HttpOnly";

   /// <summary>The two cookies an Application Load Balancer with duration stickiness adds to every answer, in its own format.</summary>
   private static string[] Balancer(string node) =>
   [
      $"AWSALB={node}+aB3/xYz==; Expires=Tue, 17 Nov 2099 18:00:00 GMT; Path=/",
      $"AWSALBCORS={node}+aB3/xYz==; Expires=Tue, 17 Nov 2099 18:00:00 GMT; Path=/; SameSite=None; Secure",
   ];



   /// <summary>
   /// The load balancer's cookies from the first public answer travel on every later request, with or without a session, and
   /// never displace or disturb the session cookie. When a later answer rewrites one (the load balancer moved this client to
   /// another node), the next request carries the new value. The sign-out request carries them too, so it reaches that node.
   /// </summary>
   [Fact]
   public async Task CookiesFromTheLoadBalancerTravelOnEveryLaterRequestBesideTheSession()
   {
      using var handler = new Handler();
      handler.Add(Discovery, cookies: Balancer("node-one"));
      handler.Add(Table);
      handler.Add("{}", cookies: [Session, .. Balancer("node-one")]);
      handler.Add(Identity);
      handler.Add("""{"job":null}""", cookies: Balancer("node-two"));
      handler.Add("""{"job":null}""");
      handler.Add("{}");
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      Assert.Null(handler.Requests[0].Cookie);
      Assert.Equal("AWSALB=node-one+aB3/xYz==; AWSALBCORS=node-one+aB3/xYz==", handler.Requests[1].Cookie);
      Assert.Equal("AWSALB=node-one+aB3/xYz==; AWSALBCORS=node-one+aB3/xYz==", handler.Requests[2].Cookie);
      Assert.Null(handler.Requests[1].Origin);
      Assert.True(client.IsAuthenticated);
      Assert.Equal("session-secret", client.ExportCredential().SessionToken);

      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof"));
      using(HttpResponseMessage first = await client.SendPrintingAsync(HttpMethod.Post, "device/jobs/claim", new StringContent("{}", Encoding.UTF8, "application/json")))
      { Assert.Equal(HttpStatusCode.OK, first.StatusCode); }
      string claim = handler.Requests[4].Cookie!;
      Assert.Contains("sessionUUID=session-secret", claim, StringComparison.Ordinal);
      Assert.Contains("sessionId=session-secret", claim, StringComparison.Ordinal);
      Assert.EndsWith("; AWSALB=node-one+aB3/xYz==; AWSALBCORS=node-one+aB3/xYz==", claim, StringComparison.Ordinal);
      Assert.Equal(Origin.GetLeftPart(UriPartial.Authority), handler.Requests[4].Origin);

      using(HttpResponseMessage second = await client.SendPrintingAsync(HttpMethod.Post, "device/jobs/claim", new StringContent("{}", Encoding.UTF8, "application/json")))
      { Assert.Equal(HttpStatusCode.OK, second.StatusCode); }
      Assert.EndsWith("; AWSALB=node-two+aB3/xYz==; AWSALBCORS=node-two+aB3/xYz==", handler.Requests[5].Cookie, StringComparison.Ordinal);
      Assert.Equal(2, client.AffinityCookieCount);

      await client.SignOutAsync();
      Assert.Contains("AWSALB=node-two+aB3/xYz==", handler.Requests[6].Cookie, StringComparison.Ordinal);
      Assert.Contains("sessionUUID=session-secret", handler.Requests[6].Cookie, StringComparison.Ordinal);
      Assert.False(client.IsAuthenticated);
   }



   /// <summary>
   /// Only the load balancer's own cookies are kept, and only when they are scoped to this host and the root path with a
   /// plain value. Any other cookie the server or a proxy sets, and any malformed one, is not stored and is never sent back.
   /// </summary>
   [Theory]
   [InlineData("AWSALB=value; Path=/", true)]
   [InlineData("AWSALB=value", true)]
   [InlineData("AWSALBTG=value; Expires=Tue, 17 Nov 2099 18:00:00 GMT; Path=/", true)]
   [InlineData("AWSALBAPP-0=value; Path=/", true)]
   [InlineData("AWSALBCORS=value; Path=/; SameSite=None; Secure", true)]
   [InlineData("AWSALB=value; Domain=printing.example.invalid; Path=/", true)]
   [InlineData("AWSALB=value; Max-Age=3600; Path=/", true)]
   [InlineData("tracking=value; Path=/", false)]
   [InlineData("JSESSIONID=value; Path=/", false)]
   [InlineData("awsalb=value; Path=/", false)]
   [InlineData("AWSALB.other=value; Path=/", false)]
   [InlineData("AWSALB=value; Domain=other.example.invalid; Path=/", false)]
   [InlineData("AWSALB=value; Path=/elsewhere", false)]
   [InlineData("AWSALB=two words; Path=/", false)]
   [InlineData("AWSALB=\"quoted\"; Path=/", false)]
   [InlineData("AWSALB=; Path=/", false)]
   [InlineData("AWSALB=value; Max-Age=0; Path=/", false)]
   [InlineData("AWSALB=value; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/", false)]
   [InlineData("AWSALB", false)]
   [InlineData("=value", false)]
   public async Task OnlyTheLoadBalancersOwnWellFormedCookiesAreKept(string setCookie, bool kept)
   {
      using var handler = new Handler();
      handler.Add(Discovery, cookies: [setCookie]);
      using var client = new QqqSessionClient(Origin, handler);
      await client.DiscoverAsync();
      Assert.Equal(kept ? 1 : 0, client.AffinityCookieCount);
   }



   /// <summary>A value longer than a load balancer ever sends, an oversized header, and a ninth cookie are all refused, so a hostile server cannot make this client carry an unbounded Cookie header.</summary>
   [Fact]
   public async Task TheHeldCookiesAreBounded()
   {
      using var handler = new Handler();
      handler.Add(Discovery, cookies: ["AWSALB=" + new string('a', 4097) + "; Path=/", "AWSALBTG=" + new string('a', 9000), .. Enumerable.Range(0, 12).Select(index => $"AWSALBAPP-{index}=value; Path=/")]);
      using var client = new QqqSessionClient(Origin, handler);
      await client.DiscoverAsync();
      Assert.Equal(8, client.AffinityCookieCount);
   }



   /// <summary>The load balancer replaces a cookie by sending it again and removes one by expiring it; an answer without the cookie leaves it alone.</summary>
   [Fact]
   public async Task ARewrittenCookieReplacesTheOldOneAndAnExpiredOneIsDropped()
   {
      using var handler = new Handler();
      handler.Add(Discovery, cookies: Balancer("node-one"));
      handler.Add(Table, cookies: ["AWSALB=node-two; Path=/"]);
      handler.Add("{}", cookies: [Session, "AWSALBCORS=gone; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/; SameSite=None; Secure"]);
      handler.Add(Identity);
      handler.Add(Identity, cookies: ["AWSALB=; Max-Age=0; Path=/"]);
      handler.Add(Identity);
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      Assert.Equal("AWSALB=node-two; AWSALBCORS=node-one+aB3/xYz==", handler.Requests[2].Cookie);
      await client.GetSessionIdentityAsync();
      Assert.EndsWith("; AWSALB=node-two", handler.Requests[3].Cookie, StringComparison.Ordinal);
      Assert.DoesNotContain("AWSALBCORS", handler.Requests[3].Cookie, StringComparison.Ordinal);
      await client.GetSessionIdentityAsync();
      await client.GetSessionIdentityAsync();
      Assert.DoesNotContain("AWSALB", handler.Requests[5].Cookie, StringComparison.Ordinal);
      Assert.Contains("sessionUUID=session-secret", handler.Requests[5].Cookie, StringComparison.Ordinal);
      Assert.True(client.IsAuthenticated);
   }



   /// <summary>
   /// Cookies under other names never decide whether a session answer is valid. A load balancer or proxy in front of the
   /// server may add one this client would not accept as a session cookie (here, scoped to another domain); sign-in still
   /// succeeds, the foreign cookie is not kept, and the session cookie is still checked as strictly as before.
   /// </summary>
   [Fact]
   public async Task CookiesUnderOtherNamesNeverInvalidateASessionAnswer()
   {
      using var handler = new Handler();
      handler.Add(Discovery);
      handler.Add(Table);
      handler.Add("{}", cookies: ["edge=abc; Domain=other.example.invalid; Path=/", Session, .. Balancer("node-one")]);
      handler.Add(Identity);
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      Assert.True(client.IsAuthenticated);
      await client.GetSessionIdentityAsync();
      Assert.DoesNotContain("edge=", handler.Requests[3].Cookie, StringComparison.Ordinal);

      using var strict = new Handler();
      strict.Add(Discovery);
      strict.Add(Table);
      strict.Add("{}", cookies: ["sessionUUID=session-secret; Domain=other.example.invalid; Path=/", .. Balancer("node-one")]);
      using var refused = new QqqSessionClient(Origin, strict);
      await Assert.ThrowsAsync<QqqSessionException>(() => refused.SignInWithPasswordAsync("user", "password"));
      Assert.False(refused.IsAuthenticated);
   }



   /// <summary>
   /// A node that is stopping answers 5xx, or the load balancer answers 502, 503 or 504 for it. That is not a verdict on the
   /// session: the answer is handed back, the session stays signed in, and the load balancer's new choice of node in that
   /// answer is adopted for the retry. Only a 401 ends the session.
   /// </summary>
   [Theory]
   [InlineData(HttpStatusCode.BadGateway)]
   [InlineData(HttpStatusCode.ServiceUnavailable)]
   [InlineData(HttpStatusCode.GatewayTimeout)]
   [InlineData(HttpStatusCode.InternalServerError)]
   [InlineData(HttpStatusCode.Conflict)]
   public async Task AServerErrorKeepsTheSessionAndFollowsTheLoadBalancer(HttpStatusCode status)
   {
      using Handler handler = SignedIn("node-one");
      handler.Add("""{"error":"unavailable"}""", status, Balancer("node-two"));
      handler.Add("""{"job":null}""");
      handler.Add("{}", HttpStatusCode.Unauthorized);
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof"));
      using(HttpResponseMessage failed = await client.SendPrintingAsync(HttpMethod.Post, "device/jobs/claim", new StringContent("{}", Encoding.UTF8, "application/json")))
      { Assert.Equal(status, failed.StatusCode); }
      Assert.True(client.IsAuthenticated);
      using(HttpResponseMessage retried = await client.SendPrintingAsync(HttpMethod.Post, "device/jobs/claim", new StringContent("{}", Encoding.UTF8, "application/json")))
      { Assert.Equal(HttpStatusCode.OK, retried.StatusCode); }
      Assert.Contains("AWSALB=node-two+aB3/xYz==", handler.Requests[^1].Cookie, StringComparison.Ordinal);
      Assert.Contains("sessionUUID=session-secret", handler.Requests[^1].Cookie, StringComparison.Ordinal);

      QqqSessionException ended = await Assert.ThrowsAsync<QqqSessionException>(() => client.SendPrintingAsync(HttpMethod.Post, "device/jobs/claim", new StringContent("{}", Encoding.UTF8, "application/json")));
      Assert.Equal(QqqSessionException.FailureKind.AuthenticationRequired, ended.Failure);
      Assert.False(client.IsAuthenticated);
   }



   /// <summary>A connection that drops before any answer (the node died mid-request) surfaces as a transport failure and leaves the session and the held cookies as they were, so the next attempt goes out signed in.</summary>
   [Fact]
   public async Task ADroppedConnectionKeepsTheSession()
   {
      using Handler handler = SignedIn("node-one");
      handler.AddAsync(_ => throw new HttpRequestException("The connection was reset.", new IOException("reset")));
      handler.Add("""{"job":null}""");
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof"));
      await Assert.ThrowsAsync<HttpRequestException>(() => client.SendPrintingAsync(HttpMethod.Post, "device/jobs/claim", new StringContent("{}", Encoding.UTF8, "application/json")));
      Assert.True(client.IsAuthenticated);
      using HttpResponseMessage retried = await client.SendPrintingAsync(HttpMethod.Post, "device/jobs/claim", new StringContent("{}", Encoding.UTF8, "application/json"));
      Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
      Assert.Contains("AWSALB=node-one+aB3/xYz==", handler.Requests[^1].Cookie, StringComparison.Ordinal);
   }



   /// <summary>
   /// The realtime upgrade carries the load balancer's cookie beside the session, so the socket opens on the node the HTTP
   /// requests already reach. A cookie on the upgrade answer itself is adopted, so requests made after the socket is open go
   /// to the node that holds it. Over loopback HTTP a cookie marked Secure is not kept, as a browser would not keep it.
   /// </summary>
   [Fact]
   public async Task TheRealtimeUpgradeCarriesAndAdoptsTheLoadBalancerCookie()
   {
      using var peer = new LoopbackWebSocketPeer();
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
      using var handler = new NativeHandler(["AWSALB=node-one; Path=/", "AWSALBCORS=node-one; Path=/; SameSite=None; Secure"]);
      using var session = new QqqSessionClient(peer.Origin, handler, allowLoopbackHttp: true);
      await session.SignInWithPasswordAsync("synthetic-user", "synthetic-password", timeout.Token);
      await session.BindDeviceAsync(new QqqDeviceCredential("device", "synthetic-proof"), cancellationToken: timeout.Token);
      Assert.Equal(1, session.AffinityCookieCount);
      peer.AnswerHeaders.Add("Set-Cookie: AWSALB=node-two; Path=/");
      Task<WebSocket> accepted = peer.AcceptAsync(timeout.Token);
      using WebSocket socket = await session.ConnectPrintingAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), timeout.Token);
      using WebSocket server = await accepted;
      Assert.Contains("sessionUUID=synthetic-session", peer.Headers["Cookie"], StringComparison.Ordinal);
      Assert.EndsWith("; AWSALB=node-one", peer.Headers["Cookie"], StringComparison.Ordinal);
      await session.GetSessionIdentityAsync(timeout.Token);
      Assert.EndsWith("; AWSALB=node-two", handler.Cookies[^1], StringComparison.Ordinal);
   }



   /// <summary>
   /// A refused upgrade is read for what it is. A 5xx (the node is stopping, or the load balancer has no healthy node yet) is
   /// a transport failure the runtime retries with backoff: the session stays signed in and the load balancer's cookie on the
   /// refusal is adopted, so the retry can land on another node. A 401 is the server withdrawing authorization.
   /// </summary>
   [Theory]
   [InlineData("503 Service Unavailable", false)]
   [InlineData("502 Bad Gateway", false)]
   [InlineData("401 Unauthorized", true)]
   public async Task ARefusedUpgradeIsRetryableUnlessAuthorizationWasWithdrawn(string status, bool authorization)
   {
      using var peer = new LoopbackWebSocketPeer();
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
      using var handler = new NativeHandler(["AWSALB=node-one; Path=/"]);
      using var session = new QqqSessionClient(peer.Origin, handler, allowLoopbackHttp: true);
      await session.SignInWithPasswordAsync("synthetic-user", "synthetic-password", timeout.Token);
      await session.BindDeviceAsync(new QqqDeviceCredential("device", "synthetic-proof"), cancellationToken: timeout.Token);
      peer.AnswerHeaders.Add("Set-Cookie: AWSALB=node-two; Path=/");
      Task refused = peer.RefuseAsync(status, timeout.Token);
      Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
      {
         using WebSocket socket = await session.ConnectPrintingAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), timeout.Token);
      });
      await refused;
      if(authorization)
      { Assert.Equal(QqqSessionException.FailureKind.AuthenticationRequired, Assert.IsType<QqqSessionException>(failure).Failure); }
      else
      { Assert.IsType<WebSocketException>(failure); }
      Assert.True(session.IsAuthenticated);
      Assert.EndsWith("; AWSALB=node-one", peer.Headers["Cookie"], StringComparison.Ordinal);
      await session.GetSessionIdentityAsync(timeout.Token);
      Assert.EndsWith("; AWSALB=node-two", handler.Cookies[^1], StringComparison.Ordinal);
   }



   /// <summary>A fake server scripted through discovery, password sign-in and the identity read that binding a device makes, with the load balancer naming a node on the first answer.</summary>
   private static Handler SignedIn(string node)
   {
      var handler = new Handler();
      handler.Add(Discovery, cookies: Balancer(node));
      handler.Add(Table);
      handler.Add("{}", cookies: [Session]);
      handler.Add(Identity);
      return handler;
   }



   /// <summary>What the fake server saw for one request: its URL and the Cookie and Origin headers.</summary>
   private sealed record Request(Uri Uri, string? Cookie, string? Origin);



   /// <summary>Scripted fake server: it records every request and answers them in the order replies were queued.</summary>
   private sealed class Handler : HttpMessageHandler
   {
      private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();
      internal List<Request> Requests { get; } = [];



      /// <summary>Queues a JSON reply with the given status and Set-Cookie headers.</summary>
      internal void Add(string body, HttpStatusCode status = HttpStatusCode.OK, IEnumerable<string>? cookies = null) => AddAsync(_ =>
      {
         var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
         if(cookies is not null)
         { response.Headers.TryAddWithoutValidation("Set-Cookie", cookies); }
         return Task.FromResult(response);
      });



      /// <summary>Queues a reply built on demand, for one that fails instead of answering.</summary>
      internal void AddAsync(Func<CancellationToken, Task<HttpResponseMessage>> response) => _responses.Enqueue(response);



      /// <summary>Records the request's cookie and origin, then returns the next queued reply.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string? Header(string name) => request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join("; ", values) : null;
         Requests.Add(new Request(request.RequestUri!, Header("Cookie"), Header("Origin")));
         return _responses.Dequeue()(cancellationToken);
      }
   }



   /// <summary>Fake native QQQ server for the realtime tests: discovery advertising realtime printing, table-based sign-in and the session identity, each answer carrying the given load balancer cookies; it records the Cookie header of every request.</summary>
   private sealed class NativeHandler(string[] balancerCookies) : HttpMessageHandler
   {
      internal List<string> Cookies { get; } = [];



      /// <summary>Serves the scripted routes; any other route is a test failure.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         Cookies.Add(request.Headers.TryGetValues("Cookie", out IEnumerable<string>? values) ? string.Join("; ", values) : "");
         string body = request.RequestUri!.AbsolutePath switch
         {
            "/qqq/v1/version" => Discovery,
            "/qqq/v1/metaData/authentication" => Table,
            "/qqq/v1/manageSession" => """{"uuid":"synthetic-session"}""",
            "/qqq/v1/qbit/printing/session" => """{"principalId":"user","tenantId":"tenant","deviceId":"device"}""",
            _ => throw new InvalidOperationException("Unexpected HTTP route."),
         };
         var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
         if(Cookies.Count == 1)
         { response.Headers.TryAddWithoutValidation("Set-Cookie", balancerCookies); }
         return Task.FromResult(response);
      }
   }
}
