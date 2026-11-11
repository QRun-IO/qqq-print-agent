/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// A QQQ server that runs as several nodes sits behind a load balancer which names the node it chose in a cookie. These tests
/// fix what the session client does with that cookie: it keeps the load balancer's cookies and nothing else, sends them on
/// every later request and on the realtime upgrade so claims reach the node that holds the socket, and follows the load
/// balancer when it rewrites them. They also fix what a refused request means when a node stops:
/// only a 401 ends the session, and a 5xx or a dropped connection leaves it signed in for the next attempt.
/// The HTTP side runs on the scripted server and the upgrade on the fake transport; two tests at the end send a real
/// upgrade over loopback, because reading the answer's headers from the platform socket is the one step no fake shows.
/// </summary>
public sealed class LoadBalancerAffinityTests
{
   private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);
   private static readonly TimeSpan HeartbeatLimit = TimeSpan.FromSeconds(60);
   private static readonly Uri Loopback = new("http://127.0.0.1:18581");
   private const string Session = "sessionUUID=session-secret; Path=/; Secure; HttpOnly";
   private const string Claim = "device/jobs/claim";
   private const string NoJob = """{"job":null}""";
   private const string NodeOne = "AWSALB=node-one+aB3/xYz==; AWSALBCORS=node-one+aB3/xYz==";
   private const string NodeTwo = "AWSALB=node-two+aB3/xYz==; AWSALBCORS=node-two+aB3/xYz==";



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
   /// They are kept across the sign-out, so the next request, here a server check that holds no session, still carries them.
   /// </summary>
   [Fact]
   public async Task CookiesFromTheLoadBalancerTravelOnEveryLaterRequestBesideTheSession()
   {
      using var server = new ScriptedServer();
      Answer(server, ScriptedServer.Discovery, Balancer("node-one")).Add(ScriptedServer.Table);
      Answer(server, "{}", [Session, .. Balancer("node-one")]).Add(ScriptedServer.Identity);
      Answer(server, NoJob, Balancer("node-two")).Add(NoJob).Add("{}").Add(ScriptedServer.Discovery).Add(ScriptedServer.Table);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await client.SignInWithPasswordAsync("user", "password");
      Assert.Null(server.Requests[0].Cookie);
      Assert.Equal(NodeOne, server.Requests[1].Cookie);
      Assert.Equal(NodeOne, server.Requests[2].Cookie);
      Assert.Null(server.Requests[1].Origin);
      Assert.Null(server.Requests[2].Origin);
      Assert.True(client.IsAuthenticated);
      Assert.Equal("session-secret", client.ExportCredential().SessionToken);

      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof"));
      using(HttpResponseMessage first = await ClaimAsync(client))
      { Assert.Equal(HttpStatusCode.OK, first.StatusCode); }
      Assert.Equal("sessionUUID=session-secret; sessionId=session-secret; " + NodeOne, server.Requests[4].Cookie);
      Assert.Equal(ScriptedServer.Origin.GetLeftPart(UriPartial.Authority), server.Requests[4].Origin);

      using(HttpResponseMessage second = await ClaimAsync(client))
      { Assert.Equal(HttpStatusCode.OK, second.StatusCode); }
      Assert.Equal("sessionUUID=session-secret; sessionId=session-secret; " + NodeTwo, server.Requests[5].Cookie);
      Assert.Equal(2, client.AffinityCookieCount);

      await client.SignOutAsync();
      Assert.Equal("/qqq/v1/logout", server.Requests[6].Uri.AbsolutePath);
      Assert.Equal("sessionUUID=session-secret; sessionId=session-secret; " + NodeTwo, server.Requests[6].Cookie);
      Assert.False(client.IsAuthenticated);
      Assert.Equal(2, client.AffinityCookieCount);

      Assert.True((await client.CheckCompatibilityAsync()).IsCompatible);
      Assert.Equal(NodeTwo, server.Requests[7].Cookie);
      Assert.Equal(NodeTwo, server.Requests[8].Cookie);
      Assert.Null(server.Requests[7].Authorization);
      Assert.Null(server.Requests[7].DeviceToken);
   }



   /// <summary>
   /// Only the load balancer's own cookies are kept, and only when they are scoped to this host and the root path with a
   /// plain value. Any other cookie the server or a proxy sets, and any malformed one, is not stored and is never sent back.
   /// Attribute names are read without regard to letter case, a leading dot on the domain is allowed, and an attribute this
   /// client cannot read (an expiry that is not a date, a lifetime that is not a number) is ignored, not treated as expiry.
   /// A plain value is printable ASCII without a space, quote, comma or backslash. A carriage return or a line feed, the
   /// two characters that end a header line, a tab or any other control character, DEL, and a character outside ASCII
   /// each refuse the cookie, because the value is written into a Cookie header as it is and none of them may reach one.
   /// The fake server hands a value with a carriage return or a line feed to the client unchanged, so those rows test
   /// the client's own rule and not the platform's handling of such a header; the third is what the rule is for, a
   /// second header forged behind a line break.
   /// </summary>
   [Theory]
   [InlineData("AWSALB=value; Path=/", true)]
   [InlineData("AWSALB=value", true)]
   [InlineData("AWSALBTG=value; Expires=Tue, 17 Nov 2099 18:00:00 GMT; Path=/", true)]
   [InlineData("AWSALBAPP-0=value; Path=/", true)]
   [InlineData("AWSALBCORS=value; Path=/; SameSite=None; Secure", true)]
   [InlineData("AWSALB=value; Domain=printing.example.invalid; Path=/", true)]
   [InlineData("AWSALB=value; Max-Age=3600; Path=/", true)]
   [InlineData("AWSALB=value; domain=.PRINTING.example.invalid; path=/; secure", true)]
   [InlineData(" AWSALB = value ; Path = / ", true)]
   [InlineData("AWSALB=value; Max-Age=soon; Path=/", true)]
   [InlineData("AWSALB=value; Expires=not a date; Path=/", true)]
   [InlineData("tracking=value; Path=/", false)]
   [InlineData("JSESSIONID=value; Path=/", false)]
   [InlineData("awsalb=value; Path=/", false)]
   [InlineData("AWSALB.other=value; Path=/", false)]
   [InlineData("AWSALB=value; Domain=other.example.invalid; Path=/", false)]
   [InlineData("AWSALB=value; Domain=example.invalid; Path=/", false)]
   [InlineData("AWSALB=value; Path=/elsewhere", false)]
   [InlineData("AWSALB=value; Path=", false)]
   [InlineData("AWSALB=two words; Path=/", false)]
   [InlineData("AWSALB=\"quoted\"; Path=/", false)]
   [InlineData("AWSALB=one,two; Path=/", false)]
   [InlineData("AWSALB=back\\slash; Path=/", false)]
   [InlineData("AWSALB=one\rtwo; Path=/", false)]
   [InlineData("AWSALB=one\ntwo; Path=/", false)]
   [InlineData("AWSALB=one\r\nSet-Cookie:sessionUUID=forged; Path=/", false)]
   [InlineData("AWSALB=one\ttwo; Path=/", false)]
   [InlineData("AWSALB=one\u0001two; Path=/", false)]
   [InlineData("AWSALB=one\u001Ftwo; Path=/", false)]
   [InlineData("AWSALB=one\u007Ftwo; Path=/", false)]
   [InlineData("AWSALB=n\u00F6de; Path=/", false)]
   [InlineData("AWSALB=node\u2028two; Path=/", false)]
   [InlineData("AWSALB=!#$%&'()*+-./:<=>?@[]^_`{|}~; Path=/", true)]
   [InlineData("AWSALB=; Path=/", false)]
   [InlineData("AWSALB=value; Max-Age=0; Path=/", false)]
   [InlineData("AWSALB=value; Max-Age=-1; Path=/", false)]
   [InlineData("AWSALB=value; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/", false)]
   [InlineData("AWSALB", false)]
   [InlineData("=value", false)]
   [InlineData("", false)]
   public async Task OnlyTheLoadBalancersOwnWellFormedCookiesAreKept(string setCookie, bool kept)
   {
      using var server = new ScriptedServer();
      Answer(server, ScriptedServer.Discovery, [setCookie]);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await client.DiscoverAsync();
      Assert.Equal(kept ? 1 : 0, client.AffinityCookieCount);
   }



   /// <summary>
   /// A value longer than a load balancer ever sends, an oversized header, an over-long name and a ninth cookie are all
   /// refused, so a hostile server cannot make this client carry an unbounded Cookie header. The largest value and name
   /// that are allowed are kept. Once eight are held a new name is refused, while a rewrite of one already held is
   /// still followed, so a full set never stops the load balancer from moving the client.
   /// </summary>
   [Fact]
   public async Task TheHeldCookiesAreBounded()
   {
      string longestName = "AWSALBAPP-" + new string('n', 54);
      using var server = new ScriptedServer();
      Answer(server, "{}", ["AWSALB=" + new string('a', 4097) + "; Path=/", "AWSALBTG=" + new string('a', 9000), longestName + "x=value; Path=/"], HttpStatusCode.ServiceUnavailable);
      Answer(server, "{}", ["AWSALB=" + new string('a', 4096) + "; Path=/", longestName + "=value; Path=/"], HttpStatusCode.ServiceUnavailable);
      Answer(server, "{}", [.. Enumerable.Range(0, 12).Select(index => $"AWSALBAPP-{index}=value; Path=/")], HttpStatusCode.ServiceUnavailable);
      Answer(server, "{}", ["AWSALBTG=refused; Path=/", "AWSALB=moved; Path=/"], HttpStatusCode.ServiceUnavailable).Add("{}", HttpStatusCode.ServiceUnavailable);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);

      Assert.False((await client.CheckCompatibilityAsync()).IsCompatible);
      Assert.Equal(0, client.AffinityCookieCount);
      Assert.False((await client.CheckCompatibilityAsync()).IsCompatible);
      Assert.Equal(2, client.AffinityCookieCount);
      Assert.False((await client.CheckCompatibilityAsync()).IsCompatible);
      Assert.Equal(8, client.AffinityCookieCount);
      Assert.Equal(64, longestName.Length);
      Assert.Null(server.Requests[1].Cookie);
      Assert.Equal("AWSALB=" + new string('a', 4096) + "; " + longestName + "=value", server.Requests[2].Cookie);

      Assert.False((await client.CheckCompatibilityAsync()).IsCompatible);
      Assert.Equal(8, client.AffinityCookieCount);
      Assert.False((await client.CheckCompatibilityAsync()).IsCompatible);
      Assert.Equal(8, server.Requests[3].Cookie!.Split("; ").Length);
      Assert.StartsWith("AWSALB=moved; AWSALBAPP-0=value; ", server.Requests[4].Cookie, StringComparison.Ordinal);
      Assert.DoesNotContain("AWSALBTG", server.Requests[4].Cookie, StringComparison.Ordinal);
      Assert.Equal(8, server.Requests[4].Cookie!.Split("; ").Length);
   }



   /// <summary>
   /// A header has a bound of its own, 8,192 characters, whatever the length of the value in it: one character more and
   /// the cookie is not kept although its value is short, and a header of exactly that length is read. (The over-long
   /// header in the test above also carries an over-long value, so it cannot tell the two bounds apart.)
   /// </summary>
   [Theory]
   [InlineData(8192, 1)]
   [InlineData(8193, 0)]
   public async Task AHeaderIsBoundedWhateverTheLengthOfItsValue(int length, int held)
   {
      const string Start = "AWSALB=value; Path=/; Comment=";
      string header = Start + new string('c', length - Start.Length);
      using var server = new ScriptedServer();
      Answer(server, ScriptedServer.Discovery, [header]);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await client.DiscoverAsync();
      Assert.Equal(length, header.Length);
      Assert.Equal(held, client.AffinityCookieCount);
   }



   /// <summary>The load balancer replaces a cookie by sending it again and removes one by expiring it; an answer without the cookie leaves it alone.</summary>
   [Fact]
   public async Task ARewrittenCookieReplacesTheOldOneAndAnExpiredOneIsDropped()
   {
      using var server = new ScriptedServer();
      Answer(server, ScriptedServer.Discovery, Balancer("node-one"));
      Answer(server, ScriptedServer.Table, ["AWSALB=node-two; Path=/"]);
      Answer(server, "{}", [Session, "AWSALBCORS=gone; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/; SameSite=None; Secure"]).Add(ScriptedServer.Identity);
      Answer(server, ScriptedServer.Identity, ["AWSALB=; Max-Age=0; Path=/"]).Add(ScriptedServer.Identity);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await client.SignInWithPasswordAsync("user", "password");
      Assert.Equal("AWSALB=node-two; AWSALBCORS=node-one+aB3/xYz==", server.Requests[2].Cookie);
      await client.GetSessionIdentityAsync();
      Assert.Equal("sessionUUID=session-secret; sessionId=session-secret; AWSALB=node-two", server.Requests[3].Cookie);
      await client.GetSessionIdentityAsync();
      await client.GetSessionIdentityAsync();
      Assert.Equal("sessionUUID=session-secret; sessionId=session-secret; AWSALB=node-two", server.Requests[4].Cookie);
      Assert.Equal("sessionUUID=session-secret; sessionId=session-secret", server.Requests[5].Cookie);
      Assert.Equal(0, client.AffinityCookieCount);
      Assert.True(client.IsAuthenticated);
   }



   /// <summary>
   /// An expiry is judged on the client's clock: a cookie the load balancer sends with an expiry that has already passed on
   /// that clock removes the one held, and the same header an instant before that time keeps it.
   /// </summary>
   [Theory]
   [InlineData(-1, 1)]
   [InlineData(0, 0)]
   [InlineData(1, 0)]
   public async Task AnExpiryIsJudgedOnTheClientsClock(int secondsPastExpiry, int held)
   {
      var time = new ManualTimeProvider();
      time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(secondsPastExpiry));
      using var server = new ScriptedServer();
      Answer(server, ScriptedServer.Discovery, ["AWSALB=node-one; Path=/"]);
      Answer(server, ScriptedServer.Table, ["AWSALB=node-one; Expires=Thu, 01 Jan 2026 01:00:00 GMT; Path=/"]);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, time);
      await client.GetAuthenticationMetadataAsync();
      Assert.Equal(held, client.AffinityCookieCount);
   }



   /// <summary>
   /// Cookies under other names never decide whether a session answer is valid. A load balancer or proxy in front of the
   /// server may add one this client would not accept as a session cookie (here, scoped to another domain); sign-in still
   /// succeeds, the foreign cookie is not kept, and the session cookie is still checked as strictly as before. A second
   /// cookie that arrives folded into the session's own header, after a comma, is not the session either and is passed over.
   /// </summary>
   [Fact]
   public async Task CookiesUnderOtherNamesNeverInvalidateASessionAnswer()
   {
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery).Add(ScriptedServer.Table);
      Answer(server, "{}", ["edge=abc; Domain=other.example.invalid; Path=/", Session, .. Balancer("node-one")]).Add(ScriptedServer.Identity);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await client.SignInWithPasswordAsync("user", "password");
      Assert.True(client.IsAuthenticated);
      await client.GetSessionIdentityAsync();
      Assert.Equal("sessionUUID=session-secret; sessionId=session-secret; " + NodeOne, server.Requests[3].Cookie);

      using var strict = new ScriptedServer();
      strict.Add(ScriptedServer.Discovery).Add(ScriptedServer.Table);
      Answer(strict, "{}", ["sessionUUID=session-secret; Domain=other.example.invalid; Path=/", .. Balancer("node-one")]);
      using var refused = new QqqSessionClient(ScriptedServer.Origin, strict);
      await Assert.ThrowsAsync<QqqSessionException>(() => refused.SignInWithPasswordAsync("user", "password"));
      Assert.False(refused.IsAuthenticated);

      using var folding = new ScriptedServer();
      folding.Add(ScriptedServer.Discovery).Add(ScriptedServer.Table);
      Answer(folding, "{}", ["sessionUUID=session-secret; Path=/, edge=abc; Path=/"]);
      using var folded = new QqqSessionClient(ScriptedServer.Origin, folding);
      await folded.SignInWithPasswordAsync("user", "password");
      Assert.Equal("session-secret", folded.ExportCredential().SessionToken);
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
      using ScriptedServer server = SignedIn("node-one");
      Answer(server, """{"error":"unavailable"}""", Balancer("node-two"), status).Add(NoJob).Add("{}", HttpStatusCode.Unauthorized);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await client.SignInWithPasswordAsync("user", "password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof"));
      using(HttpResponseMessage failed = await ClaimAsync(client))
      { Assert.Equal(status, failed.StatusCode); }
      Assert.True(client.IsAuthenticated);
      using(HttpResponseMessage retried = await ClaimAsync(client))
      { Assert.Equal(HttpStatusCode.OK, retried.StatusCode); }
      Assert.Equal("sessionUUID=session-secret; sessionId=session-secret; " + NodeTwo, server.Requests[^1].Cookie);

      QqqSessionException ended = await Assert.ThrowsAsync<QqqSessionException>(() => ClaimAsync(client));
      Assert.Equal(QqqSessionException.FailureKind.AuthenticationRequired, ended.Failure);
      Assert.False(client.IsAuthenticated);
      Assert.Equal(2, client.AffinityCookieCount);
   }



   /// <summary>A connection that drops before any answer (the node died mid-request) surfaces as a transport failure and leaves the session and the held cookies as they were, so the next attempt goes out signed in.</summary>
   [Fact]
   public async Task ADroppedConnectionKeepsTheSession()
   {
      using ScriptedServer server = SignedIn("node-one");
      server.AddFailure(() => new HttpRequestException("The connection was reset.", new IOException("reset"))).Add(NoJob);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await client.SignInWithPasswordAsync("user", "password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof"));
      await Assert.ThrowsAsync<HttpRequestException>(() => ClaimAsync(client));
      Assert.True(client.IsAuthenticated);
      using HttpResponseMessage retried = await ClaimAsync(client);
      Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
      Assert.Equal("sessionUUID=session-secret; sessionId=session-secret; " + NodeOne, server.Requests[^1].Cookie);
   }



   /// <summary>
   /// The realtime upgrade carries the load balancer's cookie beside the session, so the socket opens on the node the HTTP
   /// requests already reach. A cookie on the upgrade answer itself is adopted, so requests made after the socket is open go
   /// to the node that holds it. Over loopback HTTP a cookie marked Secure is not kept, as a browser would not keep it. The
   /// answer's headers are taken from the connector once, and the header name is matched whatever its letter case.
   /// </summary>
   [Theory]
   [InlineData("Set-Cookie")]
   [InlineData("set-cookie")]
   public async Task TheRealtimeUpgradeCarriesAndAdoptsTheLoadBalancerCookie(string headerName)
   {
      (QqqSessionClient client, FakeTransport transport, ScriptedServer server) = await BoundOverLoopbackAsync(["AWSALB=node-one; Path=/", "AWSALBCORS=node-one; Path=/; SameSite=None; Secure"]);
      using(client)
      {
         Assert.Equal(1, client.AffinityCookieCount);
         transport.Prepare = connector => connector.UpgradeHeaders = new(StringComparer.Ordinal) { ["Server"] = ["synthetic"], [headerName] = ["AWSALB=node-two; Path=/"] };
         server.Add(ScriptedServer.Discovery).Add(ScriptedServer.Identity);
         using WebSocket socket = await client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit);
         FakeSocketConnector connector = Assert.Single(transport.Connectors);
         Assert.Equal("sessionUUID=session; sessionId=session; AWSALB=node-one", UpgradeCookie(connector));
         Assert.Equal(1, connector.HeadersTaken);
         Assert.True(connector.Released);
         await client.GetSessionIdentityAsync();
         Assert.Equal("sessionUUID=session; sessionId=session; AWSALB=node-two", server.Requests[^1].Cookie);
      }
   }



   /// <summary>
   /// Discovery runs just before each upgrade, and the load balancer may move the client on that very answer. The upgrade
   /// request is built after it, so the socket goes to the node the load balancer chose last, not the one before.
   /// </summary>
   [Fact]
   public async Task TheUpgradeFollowsACookieRewrittenByTheDiscoveryJustBeforeIt()
   {
      (QqqSessionClient client, FakeTransport transport, ScriptedServer server) = await BoundOverLoopbackAsync(["AWSALB=node-one; Path=/"]);
      using(client)
      {
         Answer(server, ScriptedServer.Discovery, ["AWSALB=node-fresh; Path=/"]);
         using WebSocket socket = await client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit);
         Assert.Equal("AWSALB=node-one", server.Requests[^1].Cookie);
         Assert.Equal("sessionUUID=session; sessionId=session; AWSALB=node-fresh", UpgradeCookie(transport.Connectors[0]));
      }
   }



   /// <summary>
   /// A refused upgrade is read for what it is. A 5xx (the node is stopping, or the load balancer has no healthy node yet) is
   /// a transport failure the runtime retries with backoff: the session stays signed in and the load balancer's cookie on the
   /// refusal is adopted, so the retry can land on another node. A 401 or 403 is the server withdrawing authorization, and
   /// the cookie on that refusal is adopted all the same, so the sign-in that follows reaches the node it names.
   /// </summary>
   [Theory]
   [InlineData(HttpStatusCode.ServiceUnavailable, false)]
   [InlineData(HttpStatusCode.BadGateway, false)]
   [InlineData(HttpStatusCode.Unauthorized, true)]
   [InlineData(HttpStatusCode.Forbidden, true)]
   public async Task ARefusedUpgradeIsRetryableUnlessAuthorizationWasWithdrawn(HttpStatusCode status, bool authorization)
   {
      (QqqSessionClient client, FakeTransport transport, ScriptedServer server) = await BoundOverLoopbackAsync(["AWSALB=node-one; Path=/"]);
      using(client)
      {
         transport.Prepare = connector =>
         {
            connector.UpgradeStatus = status;
            connector.UpgradeHeaders = new(StringComparer.Ordinal) { ["Set-Cookie"] = ["AWSALB=node-two; Path=/"] };
            connector.Connecting = _ => throw new WebSocketException(WebSocketError.NotAWebSocket, "The server returned status code '" + (int)status + "' when status code '101' was expected.");
         };
         server.Add(ScriptedServer.Discovery).Add(ScriptedServer.Identity);
         Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit));
         if(authorization)
         { Assert.Equal(QqqSessionException.FailureKind.AuthenticationRequired, Assert.IsType<QqqSessionException>(failure).Failure); }
         else
         { Assert.IsType<WebSocketException>(failure); }
         Assert.True(client.IsAuthenticated);
         Assert.Equal("sessionUUID=session; sessionId=session; AWSALB=node-one", UpgradeCookie(transport.Connectors[0]));
         Assert.Equal(1, transport.Connectors[0].HeadersTaken);
         Assert.True(transport.Connectors[0].Disposed);
         await client.GetSessionIdentityAsync();
         Assert.Equal("sessionUUID=session; sessionId=session; AWSALB=node-two", server.Requests[^1].Cookie);
      }
   }



   /// <summary>
   /// An upgrade that fails with no HTTP answer at all (the connection dropped, or the attempt was cancelled) has nothing to
   /// read: the held cookie stays as it was, and the failure reaches the caller unchanged.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task AnUpgradeWithoutAnAnswerLeavesTheHeldCookiesAlone(bool cancelled)
   {
      (QqqSessionClient client, FakeTransport transport, ScriptedServer server) = await BoundOverLoopbackAsync(["AWSALB=node-one; Path=/"]);
      using(client)
      {
         Exception thrown = cancelled ? new OperationCanceledException("Synthetic handshake timeout.") : new IOException("Synthetic connection loss.");
         transport.Prepare = connector => connector.Connecting = _ => throw thrown;
         server.Add(ScriptedServer.Discovery).Add(ScriptedServer.Identity);
         Assert.Same(thrown, await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit)));
         Assert.Equal(1, transport.Connectors[0].HeadersTaken);
         Assert.Equal(1, client.AffinityCookieCount);
         await client.GetSessionIdentityAsync();
         Assert.Equal("sessionUUID=session; sessionId=session; AWSALB=node-one", server.Requests[^1].Cookie);
      }
   }



   /// <summary>
   /// The sign-in provider never sees the load balancer's cookies and never sets them. Both requests this client makes to a
   /// provider, its discovery document at sign-in and again at sign-out, go out with no Cookie header although cookies are
   /// held for the server, and a Set-Cookie on the provider's answer under the load balancer's name is not adopted. The
   /// debug line counts the cookies a request carries: two for the server, none for the provider.
   /// </summary>
   [Fact]
   public async Task TheSignInProviderNeverReceivesOrSetsTheLoadBalancerCookies()
   {
      var debug = new List<string>();
      using var server = new ScriptedServer();
      Answer(server, ScriptedServer.Discovery, Balancer("node-one")).Add(ScriptedServer.OAuth);
      Answer(server, ScriptedServer.Provider, ["AWSALB=provider-node; Path=/", "AWSALBTG=provider-node; Path=/"]).Add("{}", cookie: "sessionUUID=browser-session; Path=/; HttpOnly; Secure");
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(debug: debug.Add));
      QqqBrowserLogin attempt = await client.BeginBrowserLoginAsync(new Uri(ScriptedServer.Callback));
      string state = attempt.AuthorizationUri.Query[1..].Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..];
      await client.CompleteBrowserLoginAsync(attempt, new Uri(ScriptedServer.Callback + "?code=synthetic-code&state=" + state));
      Assert.Equal("id.example.invalid", server.Requests[2].Uri.Host);
      Assert.True(server.Requests[2].IsPublic);
      Assert.Equal(NodeOne, server.Requests[3].Cookie);
      Assert.Equal(2, client.AffinityCookieCount);

      server.Add("""{"nativeLogoutData":{"issuer":"https://id.example.invalid/application/","endSessionEndpoint":"https://id.example.invalid/end-session","postLogoutRedirectUri":"io.qrun.qqq-print-agent:/oauth/callback","idTokenHint":"synthetic.id-token.hint"}}""");
      Answer(server, ScriptedServer.Provider, ["AWSALB=provider-node; Path=/"]);
      Assert.NotNull(await client.SignOutWithProviderAsync(new Uri(ScriptedServer.Callback)));
      Assert.Equal("sessionUUID=browser-session; sessionId=browser-session; " + NodeOne, server.Requests[^2].Cookie);
      Assert.Equal("id.example.invalid", server.Requests[^1].Uri.Host);
      Assert.True(server.Requests[^1].IsPublic);
      Assert.Equal(2, client.AffinityCookieCount);

      Assert.Contains(debug, line => line.StartsWith("HTTP GET https://printing.example.invalid/qqq/v1/metaData/authentication: session cookie no, load balancer cookies 2,", StringComparison.Ordinal));
      Assert.Equal(2, debug.Count(line => line.StartsWith("HTTP GET https://id.example.invalid/application/.well-known/openid-configuration: session cookie no, load balancer cookies 0,", StringComparison.Ordinal)));
   }



   /// <summary>
   /// The cookies are routing, not identity, and they are never written anywhere. The credential a host saves for a later
   /// start holds the origin and the session token and nothing else; a new client that restores it starts with no load
   /// balancer cookie and sends none until an answer sets one. No log line, at either level, ever names a load balancer
   /// cookie or shows its value: the log counts them.
   /// </summary>
   [Fact]
   public async Task TheCookiesAreNeverSavedWithTheSessionOrWrittenToALog()
   {
      var lines = new List<string>();
      using ScriptedServer server = SignedIn("node-one");
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(lines.Add, lines.Add));
      await client.SignInWithPasswordAsync("user", "password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof"));
      QqqSessionCredential saved = client.ExportCredential();
      Assert.Equal(["Origin", "SessionToken"], typeof(QqqSessionCredential).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal("https://printing.example.invalid/", saved.Origin.AbsoluteUri);
      Assert.Equal("session-secret", saved.SessionToken);
      Assert.Equal(2, client.AffinityCookieCount);
      Assert.Contains(lines, line => line.Contains("load balancer cookies 2", StringComparison.Ordinal));
      Assert.DoesNotContain(lines, line => line.Contains("AWSALB", StringComparison.Ordinal) || line.Contains("node-one", StringComparison.Ordinal));

      using var later = new ScriptedServer();
      later.Add(ScriptedServer.Discovery).Add(ScriptedServer.Table).Add("{}", cookie: Session);
      using var restored = new QqqSessionClient(ScriptedServer.Origin, later);
      await restored.RestoreSessionAsync(saved);
      Assert.Equal(0, restored.AffinityCookieCount);
      Assert.Equal("sessionUUID=session-secret; sessionId=session-secret", later.Requests[^1].Cookie);
   }



   /// <summary>
   /// The same upgrade over a real loopback connection and the platform socket, which is the one place the answer's headers
   /// are read from the platform and not from a fake. The upgrade carries the held cookie; the cookie on the accepted
   /// answer, or on a 503 refusal, is the one the next request carries.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task ThePlatformSocketHandsOverTheCookieOnTheUpgradeAnswer(bool accepted)
   {
      using var peer = new UpgradePeer();
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
      using var server = new ScriptedServer();
      Answer(server, ScriptedServer.Discovery, ["AWSALB=node-one; Path=/"]).Add(ScriptedServer.Table).Add("""{"uuid":"session"}""").Add(ScriptedServer.Identity).Add(ScriptedServer.Discovery).Add(ScriptedServer.Identity);
      using var client = new QqqSessionClient(peer.Origin, server, allowLoopbackHttp: true);
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password", timeout.Token);
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "synthetic-proof"), cancellationToken: timeout.Token);
      Task answered = peer.AnswerAsync(accepted, "AWSALB=node-two; Path=/", timeout.Token);
      if(accepted)
      {
         using WebSocket socket = await client.ConnectPrintingAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), timeout.Token);
         Assert.Equal(WebSocketState.Open, socket.State);
      }
      else
      { await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectPrintingAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), timeout.Token)); }
      await answered;
      Assert.Equal("sessionUUID=session; sessionId=session; AWSALB=node-one", peer.Cookie);
      await client.GetSessionIdentityAsync(timeout.Token);
      Assert.Equal("sessionUUID=session; sessionId=session; AWSALB=node-two", server.Requests[^1].Cookie);
   }



   /// <summary>Queues a JSON reply that carries the given Set-Cookie headers, each as its own header, as a load balancer adds them.</summary>
   private static ScriptedServer Answer(ScriptedServer server, string body, IEnumerable<string> cookies, HttpStatusCode status = HttpStatusCode.OK) => server.AddResponse(() =>
   {
      HttpResponseMessage response = ScriptedServer.Json(body, status);
      response.Headers.TryAddWithoutValidation("Set-Cookie", cookies);
      return response;
   });



   /// <summary>A fake server scripted through discovery, password sign-in and the identity read that binding a device makes, with the load balancer naming a node on the first answer.</summary>
   private static ScriptedServer SignedIn(string node)
   {
      var server = new ScriptedServer();
      Answer(server, ScriptedServer.Discovery, Balancer(node)).Add(ScriptedServer.Table);
      return Answer(server, "{}", [Session]).Add(ScriptedServer.Identity);
   }



   /// <summary>A client over the fake transport and plain loopback HTTP that has signed in and bound a device, with the load balancer's cookies on the first answer.</summary>
   private static async Task<(QqqSessionClient Client, FakeTransport Transport, ScriptedServer Server)> BoundOverLoopbackAsync(string[] balancerCookies)
   {
      var server = new ScriptedServer();
      Answer(server, ScriptedServer.Discovery, balancerCookies).Add(ScriptedServer.Table).Add("""{"uuid":"session"}""").Add(ScriptedServer.Identity);
      var transport = new FakeTransport(server);
      var client = new QqqSessionClient(Loopback, null, null, allowLoopbackHttp: true, null, null, QqqCertificateExceptionOrigins.None, transport);
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "synthetic-proof"));
      return (client, transport, server);
   }



   /// <summary>Sends one claim, the request a lane makes most often.</summary>
   private static Task<HttpResponseMessage> ClaimAsync(QqqSessionClient client) => client.SendPrintingAsync(HttpMethod.Post, Claim, new StringContent("{}", Encoding.UTF8, "application/json"));



   /// <summary>The Cookie header of the upgrade request a fake connector was given.</summary>
   private static string UpgradeCookie(FakeSocketConnector connector) => connector.Request!.Headers.Single(header => string.Equals(header.Key, "Cookie", StringComparison.Ordinal)).Value;



   /// <summary>
   /// A loopback listener that answers one WebSocket upgrade, accepting it or refusing it with 503, with a Set-Cookie header
   /// on the answer, and remembers the Cookie header the upgrade carried. Nothing but this test connects to it.
   /// </summary>
   private sealed class UpgradePeer : IDisposable
   {
      private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
      private TcpClient? _client;



      /// <summary>Starts listening on a port the system chooses.</summary>
      internal UpgradePeer()
      {
         _listener.Start();
         Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
      }



      /// <summary>The origin a session client connects to.</summary>
      internal Uri Origin { get; }



      /// <summary>The Cookie header of the upgrade request, once it arrived.</summary>
      internal string? Cookie { get; private set; }



      /// <summary>Reads one upgrade request and answers it: 101 with the handshake RFC 6455 requires, or 503, and the given Set-Cookie either way.</summary>
      internal async Task AnswerAsync(bool accept, string setCookie, CancellationToken token)
      {
         _client = await _listener.AcceptTcpClientAsync(token);
         NetworkStream stream = _client.GetStream();
         using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
         string key = "";
         while(await reader.ReadLineAsync(token) is { Length: > 0 } line)
         {
            if(line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            { Cookie = line[7..].Trim(); }
            if(line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            { key = line[18..].Trim(); }
         }
         // RFC6455 requires SHA-1 for the public handshake challenge; this is not an authentication digest.
#pragma warning disable CA5350
         string challenge = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
         string head = accept
            ? $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {challenge}\r\nSec-WebSocket-Protocol: qqq-printing.v1\r\n"
            : "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n";
         await stream.WriteAsync(Encoding.ASCII.GetBytes(head + "Set-Cookie: " + setCookie + "\r\n\r\n"), token);
         await stream.FlushAsync(token);
      }



      /// <summary>Stops listening and closes the accepted connection.</summary>
      public void Dispose()
      {
         _listener.Stop();
         _client?.Dispose();
      }
   }
}
