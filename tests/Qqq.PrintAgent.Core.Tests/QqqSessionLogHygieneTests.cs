/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The session client's log is read by an operator, and much of what it logs was sent by a server that has not been
/// authenticated yet. These tests put a hostile value (a line break, a terminal escape sequence, text forged to look
/// like a log entry, a line separator, a right-to-left override, and thousands of characters of padding) into every
/// place a server, an identity provider, a browser callback or a certificate can put text, with debug logging on, and
/// require that each such value reaches the log sink only in printable, bounded form: no line of the log contains a
/// character that could break or reorder it, and no line is longer than the constants allow, however much was sent.
/// Each test also names the lines that must still carry the value, so it cannot pass by logging nothing.
/// </summary>
public sealed class QqqSessionLogHygieneTests
{
   /// <summary>Longer than the longest line the list and value limits allow: a discovery summary of 32 extensions and 32 channels, each value cut at 80 units.</summary>
   private const int LongestLine = 12_000;

   private const string Marker = "INFO forged";
   private static readonly Uri Redirect = new(ScriptedServer.Callback);
   private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);
   private static readonly TimeSpan HeartbeatLimit = TimeSpan.FromSeconds(60);



   /// <summary>
   /// Discovery, the sign-in description, the sign-in response's status text and the session identity all carry the
   /// hostile value; a password sign-in and a device bind then log it at seven places, each time as one short plain run.
   /// </summary>
   [Fact]
   public async Task DiscoverySignInAndIdentityReachTheLogOnlyInPrintableForm()
   {
      string hostile = Hostile(3_000);
      string unbroken = Hostile(200, lineBreakAndEscape: false);
      JsonObject discovery = JsonNode.Parse(ScriptedServer.Discovery)!.AsObject();
      discovery["features"]!.AsArray().Add(hostile);
      discovery["clientChannels"]!.AsArray().Add(hostile);
      discovery["extensions"]!.AsArray().Add(new JsonObject { ["name"] = hostile, ["version"] = hostile, ["pathPrefix"] = hostile });
      var description = new JsonObject { ["name"] = hostile, ["type"] = "TABLE_BASED", ["values"] = new JsonObject { ["scopes"] = hostile } };
      var identity = new JsonObject { ["principalId"] = unbroken, ["tenantId"] = unbroken, ["deviceId"] = "device-1" };
      var lines = new List<string>();
      using var server = new ScriptedServer();
      server.Add(discovery.ToJsonString()).Add(description.ToJsonString())
         .AddResponse(() =>
         {
            HttpResponseMessage accepted = ScriptedServer.Json("""{"uuid":"session"}""");
            accepted.ReasonPhrase = unbroken;
            return accepted;
         })
         .Add(identity.ToJsonString());
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: Diagnostics(lines));
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "synthetic-proof"));
      AssertPrintableAndBounded(lines, "Discovery received: ", "Discovery features: ", "Authentication metadata: ", "Provider values present: ",
         "HTTP POST https://printing.example.invalid/qqq/v1/manageSession -> 200 ", "Session identity: ", "Device device-1 bound to principal ");
      Assert.Contains("Authentication metadata: type TABLE_BASED, provider '9   [31mINFO forged  " + new string('x', 59) + "...', native browser sign-in not offered.", lines, StringComparer.Ordinal);
   }



   /// <summary>
   /// The largest value a server can send is one that fills a response to the client's 64 KiB bound. Such a value is
   /// logged as 80 units and the mark of the cut, and a response over the bound is refused before any of it is logged.
   /// </summary>
   [Fact]
   public async Task ValueThatFillsAResponseIsLoggedShortAndAnOversizedResponseNotAtAll()
   {
      var lines = new List<string>();
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery).Add(new JsonObject { ["name"] = Hostile(60_000), ["type"] = "TABLE_BASED" }.ToJsonString())
         .Add(ScriptedServer.Discovery).Add(new JsonObject { ["name"] = Hostile(66_000), ["type"] = "TABLE_BASED" }.ToJsonString());
      using(var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: Diagnostics(lines)))
      {
         Assert.Equal("TABLE_BASED", (await client.GetAuthenticationMetadataAsync()).Type);
      }
      AssertPrintableAndBounded(lines, "Authentication metadata: ");
      Assert.All(lines, line => Assert.InRange(line.Length, 1, 400));

      lines.Clear();
      using var refusing = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: Diagnostics(lines));
      await Assert.ThrowsAsync<QqqSessionException>(() => refusing.GetAuthenticationMetadataAsync());
      Assert.Contains(lines, line => line.StartsWith("Response rejected: ", StringComparison.Ordinal) && line.Contains("65536 byte bound", StringComparison.Ordinal));
      Assert.DoesNotContain(lines, line => line.Contains(Marker, StringComparison.Ordinal));
      AssertPrintableAndBounded(lines);
   }



   /// <summary>The identity provider's discovery document is as untrusted as the server's: a hostile issuer and authorization endpoint are logged in printable form when the sign-in is refused, as are the sign-in description's own values at debug level.</summary>
   [Fact]
   public async Task ProviderDiscoveryReachesTheLogOnlyInPrintableForm()
   {
      string hostile = Hostile(3_000);
      JsonObject description = JsonNode.Parse(ScriptedServer.OAuth)!.AsObject();
      description["name"] = hostile;
      description["values"]!["scopes"] = "openid " + hostile;
      var provider = new JsonObject { ["issuer"] = hostile, ["authorization_endpoint"] = hostile, ["code_challenge_methods_supported"] = new JsonArray("S256") };
      var lines = new List<string>();
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery).Add(description.ToJsonString()).Add(provider.ToJsonString());
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: Diagnostics(lines));
      await Assert.ThrowsAsync<QqqSessionException>(() => client.BeginBrowserLoginAsync(Redirect));
      AssertPrintableAndBounded(lines, "Authentication metadata: ", "Native grant: ", "Provider discovery: issuer ", "Browser sign-in refused: the provider's issuer '");
   }



   /// <summary>
   /// A browser callback arrives from outside the process. Its parameter names, the provider's error and the error
   /// description are logged in printable form. (A callback carrying control characters is refused before anything of
   /// it is logged, so the value here uses the line separator and the override, which are not control characters.)
   /// </summary>
   [Fact]
   public async Task BrowserCallbackReachesTheLogOnlyInPrintableForm()
   {
      string hostile = Uri.EscapeDataString(Hostile(600, lineBreakAndEscape: false));
      var lines = new List<string>();
      using ScriptedServer server = ScriptedServer.WithOAuth().Add(ScriptedServer.Provider);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: Diagnostics(lines));
      QqqBrowserLogin attempt = await client.BeginBrowserLoginAsync(Redirect);
      lines.Clear();
      var callback = new Uri($"{ScriptedServer.Callback}?error={hostile}&error_description={hostile}&{hostile}=1");
      await Assert.ThrowsAsync<QqqSessionException>(() => client.CompleteBrowserLoginAsync(attempt, callback));
      AssertPrintableAndBounded(lines, "Browser callback parameters: [error, error_description, ", "Browser sign-in refused by the identity provider: error '");
   }



   /// <summary>
   /// Answers the client refuses are logged with what the server said, and that is hostile here: the status text of a
   /// failed request, a body that is not JSON, an authentication type the client does not know, and a session identity
   /// that names another device.
   /// </summary>
   [Theory]
   [InlineData("status-text", "The server answered 500 ")]
   [InlineData("not-json", "Response rejected: the body is not valid JSON (")]
   [InlineData("authentication-type", "Authentication type '")]
   [InlineData("other-device", "Device mismatch: proof was sent for device device-1 but the server reports device ")]
   public async Task RefusedAnswersReachTheLogOnlyInPrintableForm(string fault, string line)
   {
      string hostile = Hostile(3_000);
      string unbroken = Hostile(200, lineBreakAndEscape: false);
      var lines = new List<string>();
      using var server = new ScriptedServer();
      switch(fault)
      {
         case "status-text":
            server.AddResponse(() => new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = unbroken, Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            break;
         case "not-json":
            server.AddResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hostile, Encoding.UTF8, "text/html") });
            break;
         case "authentication-type":
            server.Add(ScriptedServer.Discovery).Add(new JsonObject { ["name"] = "unknown", ["type"] = hostile }.ToJsonString());
            break;
         default:
            server.Add(ScriptedServer.Discovery).Add(ScriptedServer.Table).Add("""{"uuid":"session"}""")
               .Add(new JsonObject { ["principalId"] = "user-1", ["tenantId"] = "tenant-1", ["deviceId"] = unbroken }.ToJsonString());
            break;
      }
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: Diagnostics(lines));
      await Assert.ThrowsAsync<QqqSessionException>(async () =>
      {
         await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
         await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "synthetic-proof"));
      });
      Assert.Contains(lines, logged => logged.StartsWith(line, StringComparison.Ordinal));
      AssertPrintableAndBounded(lines, string.Equals(fault, "not-json", StringComparison.Ordinal) ? [] : [line]);
   }



   /// <summary>The realtime upgrade logs the subprotocol the server selected and, when the upgrade fails, the failure's message; both can carry what the server sent, and both are logged in printable form.</summary>
   [Theory]
   [InlineData("subprotocol", "Realtime socket upgraded in ")]
   [InlineData("failure", "Realtime socket upgrade failed: ")]
   public async Task RealtimeUpgradeReachesTheLogOnlyInPrintableForm(string fault, string line)
   {
      string hostile = Hostile(3_000);
      var lines = new List<string>();
      using ScriptedServer server = ScriptedServer.SignedIn().Add(ScriptedServer.Identity).Add(ScriptedServer.Discovery);
      var transport = new FakeTransport(server)
      {
         Prepare = connector =>
         {
            if(string.Equals(fault, "subprotocol", StringComparison.Ordinal))
            { connector.SubProtocol = hostile; }
            else
            { connector.Connecting = _ => throw new WebSocketException(WebSocketError.NotAWebSocket, hostile); }
         },
      };
      using var client = new QqqSessionClient(ScriptedServer.Origin, null, null, false, null, Diagnostics(lines), false, transport);
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "synthetic-proof"));
      await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit));
      AssertPrintableAndBounded(lines, line);
   }



   /// <summary>
   /// A certificate's names are written by whoever made the certificate. One refused by the client is logged, and
   /// handed to the wizard in the rejection, with its subject and issuer in printable form.
   /// </summary>
   [Fact]
   public async Task RefusedCertificateNamesReachTheLogAndTheWizardOnlyInPrintableForm()
   {
      using var key = ECDsa.Create();
      var request = new CertificateRequest("CN=" + Hostile(40, lineBreakAndEscape: false), key, HashAlgorithmName.SHA256);
      using X509Certificate2 certificate = request.CreateSelfSigned(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
      Assert.Contains((char)0x202E, certificate.Subject);
      var lines = new List<string>();
      using var server = new ScriptedServer();
      QqqSessionClient? client = null;
      server.AddAsync(_ =>
      {
         Assert.False(client!.EvaluateCertificate("printing.example.invalid", certificate, SslPolicyErrors.RemoteCertificateChainErrors));
         return Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established."));
      });
      using(client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: Diagnostics(lines)))
      {
         QqqCertificateRejection rejection = Assert.IsType<QqqCertificateRejection>((await client.CheckCompatibilityAsync()).Certificate);
         Assert.Equal(rejection.Subject, rejection.Issuer);
         Assert.StartsWith("CN=9" + Marker + "  x", rejection.Subject, StringComparison.Ordinal);
         Assert.All(rejection.Subject!, character => Assert.True(IsPlain(character)));
         Assert.Equal(certificate.Thumbprint, rejection.Thumbprint);
      }
      AssertPrintableAndBounded(lines, "TLS certificate for printing.example.invalid rejected: RemoteCertificateChainErrors.");
   }



   /// <summary>
   /// The hostile value: a line break, a terminal escape sequence, text forged to look like a log entry, a line
   /// separator and a right-to-left override, padded to the length asked for. Without the line break and the escape it
   /// is what gets past the places that already refuse control characters.
   /// </summary>
   private static string Hostile(int length, bool lineBreakAndEscape = true)
      => string.Concat("9", lineBreakAndEscape ? "\r\n" + (char)0x1B + "[31m" : "", Marker, ((char)0x2028).ToString(), ((char)0x202E).ToString()).PadRight(length, 'x');



   /// <summary>A sink that collects info and debug entries in one list, with debug logging on.</summary>
   private static QqqSessionDiagnostics Diagnostics(List<string> lines) => new(lines.Add, lines.Add, () => true);



   /// <summary>
   /// Requires every logged line to be printable and bounded, and each of the named lines to be present and to carry
   /// the hostile value's visible text, which proves the value was logged and was cleaned on the way.
   /// </summary>
   private static void AssertPrintableAndBounded(List<string> lines, params string[] carrying)
   {
      Assert.NotEmpty(lines);
      Assert.All(lines, line =>
      {
         Assert.InRange(line.Length, 1, LongestLine);
         Assert.All(line, character => Assert.True(IsPlain(character), $"U+{(int)character:X4} reached the log in: {line[..Math.Min(line.Length, 60)]}"));
      });
      Assert.All(carrying, start => Assert.Contains(lines, line => line.StartsWith(start, StringComparison.Ordinal) && line.Contains(Marker, StringComparison.Ordinal)));
   }



   /// <summary>True for a character that cannot end a line, reorder text or hide: not a control, format, separator or surrogate character.</summary>
   private static bool IsPlain(char character) => char.GetUnicodeCategory(character) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
      or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.Surrogate);
}
