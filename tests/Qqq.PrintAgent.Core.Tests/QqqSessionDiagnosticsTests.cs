/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The session client's progress reporting: every step of a sign-in is narrated, failures say which server-side requirement is
/// missing, request detail appears only while debug is on, and no entry at any level carries the password, the session token,
/// a device token or a browser code.
/// </summary>
public sealed class QqqSessionDiagnosticsTests
{
   private static readonly Uri Origin = new("https://printing.example.invalid");
   private const string Discovery = """{"apiVersion":"1.9.0","features":["version","metaData","processes","processCancel","extensions","widgets","nativeSessionLogin"],"clientChannels":["api"],"extensions":[{"name":"printing","version":"2.3.0","pathPrefix":"/qbit/printing"}]}""";
   private const string Table = """{"name":"local","type":"TABLE_BASED"}""";
   private const string Identity = """{"principalId":"user-1","tenantId":"tenant-1","deviceId":"device-1"}""";



   /// <summary>A password sign-in followed by a device bind produces the step narrative at INFO, and request routes with status codes at DEBUG, without the password, session token or device token appearing anywhere.</summary>
   [Fact]
   public async Task SignInNarratesEachStepAndNeverLogsSecrets()
   {
      var info = new List<string>();
      var debug = new List<string>();
      bool debugOn = true;
      using var handler = new Handler();
      handler.Add(Discovery);
      handler.Add(Table);
      handler.Add("{}", cookie: "sessionUUID=session-secret-token; Path=/; Secure; HttpOnly");
      handler.Add(Identity);
      using var client = new QqqSessionClient(Origin, handler, diagnostics: new QqqSessionDiagnostics(info.Add, debug.Add, () => debugOn));
      await client.SignInWithPasswordAsync("operator", "hunter2-password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "device-secret-token"));

      Assert.Contains(info, line => line.StartsWith("Discovering QQQ at printing.example.invalid", StringComparison.Ordinal));
      Assert.Contains(info, line => line.Contains("printing 2.3.0 at /qbit/printing", StringComparison.Ordinal));
      Assert.Contains(info, line => line.StartsWith("Authentication metadata: type TABLE_BASED", StringComparison.Ordinal));
      Assert.Contains("Password sign-in requested.", info, StringComparer.Ordinal);
      Assert.Contains("Password sign-in accepted; a server-managed session is now held.", info, StringComparer.Ordinal);
      Assert.Contains("Session identity: principal user-1, tenant tenant-1, device device-1.", info, StringComparer.Ordinal);
      Assert.Contains("Device device-1 bound to principal user-1 in tenant tenant-1.", info, StringComparer.Ordinal);
      Assert.Contains(debug, line => line.StartsWith("HTTP GET https://printing.example.invalid/qqq/v1/version", StringComparison.Ordinal));
      Assert.Contains(debug, line => line.Contains("/qqq/v1/manageSession -> 200", StringComparison.Ordinal));
      Assert.Contains(debug, line => line.Contains("basic credential yes", StringComparison.Ordinal));
      foreach(string line in info.Concat(debug))
      {
         Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal);
         Assert.DoesNotContain("session-secret-token", line, StringComparison.Ordinal);
         Assert.DoesNotContain("device-secret-token", line, StringComparison.Ordinal);
         Assert.DoesNotContain("Basic ", line, StringComparison.Ordinal);
      }
   }



   /// <summary>Debug messages are not even built while the host says debug is off, so a chatty client costs nothing in normal operation.</summary>
   [Fact]
   public async Task DebugDetailIsSkippedWhileDisabled()
   {
      var debug = new List<string>();
      using var handler = new Handler();
      handler.Add(Discovery);
      using var client = new QqqSessionClient(Origin, handler, diagnostics: new QqqSessionDiagnostics(null, debug.Add, () => false));
      _ = await client.DiscoverAsync();
      Assert.Empty(debug);
   }



   /// <summary>A server without the printing extension is refused with the generic configuration failure, while the log names the missing extension, so the operator learns what to fix without the UI revealing server detail.</summary>
   [Fact]
   public async Task DiscoveryRejectionExplainsWhichRequirementIsMissing()
   {
      var info = new List<string>();
      using var handler = new Handler();
      handler.Add(Discovery.Replace("\"printing\"", "\"other\"", StringComparison.Ordinal).Replace("1.9.0", "1.7.0", StringComparison.Ordinal));
      using var client = new QqqSessionClient(Origin, handler, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqSessionException exception = await Assert.ThrowsAsync<QqqSessionException>(() => client.DiscoverAsync());
      Assert.Equal(QqqSessionException.FailureKind.ConfigurationRequired, exception.Failure);
      string rejection = Assert.Single(info, line => line.StartsWith("Discovery rejected:", StringComparison.Ordinal));
      Assert.Contains("API version '1.7.0' is not a plain 1.8+ release", rejection, StringComparison.Ordinal);
      Assert.Contains("no printing extension is advertised", rejection, StringComparison.Ordinal);
   }



   /// <summary>A 401 on an authenticated request is logged as the session being withdrawn, and a non-JSON body is logged as such, each before the generic failure is thrown.</summary>
   [Fact]
   public async Task ServerRefusalsAreExplainedInTheLog()
   {
      var info = new List<string>();
      using var handler = new Handler();
      handler.Add(Discovery);
      handler.Add(Table);
      handler.Add("{}", cookie: "sessionUUID=token-1; Path=/; Secure; HttpOnly");
      handler.Add("", HttpStatusCode.Unauthorized);
      using var client = new QqqSessionClient(Origin, handler, diagnostics: new QqqSessionDiagnostics(info.Add));
      await client.SignInWithPasswordAsync("operator", "secret");
      QqqSessionException withdrawn = await Assert.ThrowsAsync<QqqSessionException>(() => client.GetSessionIdentityAsync());
      Assert.Equal(QqqSessionException.FailureKind.AuthenticationRequired, withdrawn.Failure);
      Assert.Contains(info, line => line.StartsWith("The server answered 401 Unauthorized", StringComparison.Ordinal));
      Assert.False(client.IsAuthenticated);

      using var pageHandler = new Handler();
      pageHandler.AddResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>sign in</html>", Encoding.UTF8, "text/html") });
      using var pageClient = new QqqSessionClient(Origin, pageHandler, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqSessionException invalid = await Assert.ThrowsAsync<QqqSessionException>(() => pageClient.DiscoverAsync());
      Assert.Equal(QqqSessionException.FailureKind.InvalidResponse, invalid.Failure);
      Assert.Contains(info, line => line.StartsWith("Response rejected: the body is not valid JSON", StringComparison.Ordinal) && line.Contains("text/html", StringComparison.Ordinal));
   }



   /// <summary>Scripted fake server answering queued replies in order.</summary>
   private sealed class Handler : HttpMessageHandler
   {
      private readonly Queue<Func<HttpResponseMessage>> _responses = new();



      /// <summary>Queues a JSON reply with the given status and optional cookie.</summary>
      internal void Add(string body, HttpStatusCode status = HttpStatusCode.OK, string? cookie = null) => AddResponse(() =>
      {
         var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
         if(cookie is not null)
         { response.Headers.Add("Set-Cookie", cookie); }
         return response;
      });



      /// <summary>Queues a reply built on demand.</summary>
      internal void AddResponse(Func<HttpResponseMessage> response) => _responses.Enqueue(response);



      /// <summary>Returns the next queued reply.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_responses.Dequeue()());
   }
}
