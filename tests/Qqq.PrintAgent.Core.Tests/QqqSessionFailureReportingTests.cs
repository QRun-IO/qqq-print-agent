/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// Every session operation that fails says so once in the log, by name and with the sanitized reason, and then hands the
/// caller the original failure unchanged. An operator reading the log learns which step stopped; the caller's handling
/// does not depend on the log at all.
/// </summary>
public sealed class QqqSessionFailureReportingTests
{
   private static readonly Uri Redirect = new(ScriptedServer.Callback);



   /// <summary>Each operation, refused by the server at its own request, logs "operation failed" with the failure kind and rethrows the same session failure.</summary>
   [Theory]
   [InlineData("Password sign-in")]
   [InlineData("Session restore")]
   [InlineData("Session renewal")]
   [InlineData("Sign-out")]
   [InlineData("Provider sign-out")]
   [InlineData("Device binding")]
   [InlineData("Browser sign-in start")]
   [InlineData("Browser sign-in completion")]
   public async Task EachOperationLogsItsFailureByNameAndRethrowsIt(string operation)
   {
      var info = new List<string>();
      ScriptedServer server = operation switch
      {
         "Password sign-in" or "Session restore" => ScriptedServer.WithTable(),
         "Browser sign-in start" => ScriptedServer.WithOAuth(),
         "Browser sign-in completion" => ScriptedServer.WithOAuth().Add(ScriptedServer.Provider),
         _ => ScriptedServer.SignedIn(),
      };
      server.Add("{}", HttpStatusCode.InternalServerError);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqSessionException failure = await Assert.ThrowsAsync<QqqSessionException>(() => RunAsync(client, operation));
      Assert.Equal(QqqSessionException.FailureKind.TransportRefused, failure.Failure);
      Assert.Single(info, line => string.Equals(line, operation + " failed (TransportRefused): The QQQ request did not succeed.", StringComparison.Ordinal));
      Assert.Equal(operation is "Session renewal" or "Device binding", client.IsAuthenticated);
   }



   /// <summary>A failure that is not a session failure is described by its type and message, and the same exception object reaches the caller.</summary>
   [Fact]
   public async Task UnexpectedFailureIsDescribedAndRethrownUnchanged()
   {
      var info = new List<string>();
      var fault = new InvalidOperationException("Synthetic transport fault.");
      ScriptedServer server = ScriptedServer.SignedIn().AddFailure(() => fault);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      Assert.Same(fault, await Assert.ThrowsAsync<InvalidOperationException>(() => client.ResumeSessionAsync()));
      Assert.Contains("Session renewal failed: InvalidOperationException: Synthetic transport fault.", info, StringComparer.Ordinal);
      Assert.True(client.IsAuthenticated);
   }



   /// <summary>A cancelled operation is noted as cancelled, not reported as a failure, and the cancellation reaches the caller.</summary>
   [Fact]
   public async Task CancellationIsNotedNotReportedAsAFailure()
   {
      var info = new List<string>();
      using var caller = new CancellationTokenSource();
      ScriptedServer server = ScriptedServer.SignedIn().AddAsync(async token =>
      {
         await caller.CancelAsync();
         token.ThrowIfCancellationRequested();
         return ScriptedServer.Json("{}");
      });
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ResumeSessionAsync(caller.Token));
      Assert.Contains("Session renewal was cancelled.", info, StringComparer.Ordinal);
      Assert.DoesNotContain(info, line => line.StartsWith("Session renewal failed", StringComparison.Ordinal));
   }



   /// <summary>Signs in where the operation needs a session, then runs the named operation against the request the server will refuse.</summary>
   private static async Task RunAsync(QqqSessionClient client, string operation)
   {
      if(operation is "Session renewal" or "Sign-out" or "Provider sign-out" or "Device binding")
      { await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password"); }
      switch(operation)
      {
         case "Password sign-in":
            await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
            break;
         case "Session restore":
            await client.RestoreSessionAsync(new QqqSessionCredential(ScriptedServer.Origin, "saved-session"));
            break;
         case "Session renewal":
            await client.ResumeSessionAsync();
            break;
         case "Sign-out":
            await client.SignOutAsync();
            break;
         case "Provider sign-out":
            await client.SignOutWithProviderAsync(Redirect);
            break;
         case "Device binding":
            await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "synthetic-proof"));
            break;
         case "Browser sign-in start":
            await client.BeginBrowserLoginAsync(Redirect);
            break;
         default:
            QqqBrowserLogin attempt = await client.BeginBrowserLoginAsync(Redirect);
            string state = attempt.AuthorizationUri.Query[1..].Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..];
            await client.CompleteBrowserLoginAsync(attempt, new Uri(ScriptedServer.Callback + "?code=synthetic-code&state=" + state));
            break;
      }
   }
}
