/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// How the service uses the signed-in QQQ session: it resumes it, survives temporary failures, refuses a changed identity, and
/// keeps the device pairing and job journal intact through sign-out. A fake session stands in for the server, so these tests
/// check the service's decisions rather than the network. Where a test needs authorization evaluated once it calls the entry
/// point the reconnect loop and the heartbeat use. Where it claims the service sent nothing, it runs the service loop for a
/// whole connection pass, because only the loop would send anything: the evaluation alone never does.
/// </summary>
public sealed class AgentSessionIntegrationTests
{
   /// <summary>A temporary renewal failure (the transport was refused) surfaces as an error for that evaluation only and does not block authorization; once the server is reachable again the same stored session renews without asking the user to sign in again.</summary>
   [Fact]
   public async Task TransientResumeFailureDoesNotRequireNewSignIn()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      using SingleServerHarness runtime = fixture.Runtime();
      Assert.True(await runtime.EnsureAuthorizationAsync(CancellationToken.None));
      fixture.Session.ResumeFailure = QqqSessionException.FailureKind.TransportRefused;
      await Assert.ThrowsAsync<QqqSessionException>(() => fixture.EvaluateAsync(runtime));
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
      Assert.NotEqual("AUTHORIZATION_REQUIRED", runtime.GetTransportSnapshot().State, StringComparer.Ordinal);
      fixture.Session.ResumeFailure = null;
      Assert.True(await runtime.EnsureAuthorizationAsync(CancellationToken.None));
      Assert.Equal(1, fixture.Created);
      Assert.Equal(2, fixture.Session.Resumes);
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
   }



   /// <summary>A user who signed in but has no device paired yet is recognized as signed in but not authorized, and the service makes no printing requests, so it cannot claim jobs before enrollment.</summary>
   [Fact]
   public async Task SavedSignInBeforeEnrollmentResumesWithoutClaimingJobs()
   {
      using SessionFixture fixture = new();
      File.Delete(Path.Combine(fixture.Local.DirectoryPath, "connection.dpapi"));
      fixture.Session.Binding = fixture.Session.Binding with { DeviceId = null };
      fixture.SaveSession();
      using SingleServerHarness runtime = fixture.Runtime();
      await fixture.RunOnePassAsync(runtime);
      Assert.True(runtime.GetSnapshot().Authentication!.SignedIn);
      Assert.False(runtime.GetSnapshot().Authentication!.Authorized);
      Assert.Equal(1, fixture.Created);
      fixture.AssertSentNothing(fixture.Session);
   }



   /// <summary>A sign-in attempt with a credential for an invalid origin is rejected, and the rejection must release the sign-in gate; otherwise the next, valid attempt would hang. The valid retry here completes within five seconds.</summary>
   [Fact]
   public async Task RejectedOriginDoesNotKeepAuthenticationGateLocked()
   {
      using SessionFixture fixture = new();
      fixture.Factory = origin => string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal)
         ? fixture.Session : new QqqSessionClient(origin);
      using SingleServerHarness runtime = fixture.Runtime();
      QqqSessionCredential invalid = new(new Uri("https://synthetic.invalid/not-an-origin"), "synthetic-cookie");
      await Assert.ThrowsAsync<ArgumentException>(() => runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: invalid), CancellationToken.None));
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
      _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.Session.ExportCredential()), timeout.Token);
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
   }



   /// <summary>An installation paired with the older device-token method, with no saved sign-in, waits for the user to sign in: no session is created and nothing is sent. The existing pairing and the job journal are left exactly as they were.</summary>
   [Fact]
   public async Task InstalledLegacyPairingWaitsForSignInWithoutNetworkOrJournalLoss()
   {
      using SessionFixture fixture = new();
      fixture.Local.SeedSpooledJob();
      using SingleServerHarness runtime = fixture.Runtime();
      await fixture.RunOnePassAsync(runtime);
      Assert.Equal(0, fixture.Created);
      fixture.AssertSentNothing(fixture.Session);
      Assert.Contains(fixture.Logs, line => line.StartsWith("No saved QQQ session", StringComparison.Ordinal));
      Assert.False(runtime.GetSnapshot().Connected);
      using AgentStore store = new(fixture.Local.DirectoryPath);
      Assert.Equal("device", store.LoadConfiguration()!.DeviceId);
      Assert.Equal("SPOOLED", store.GetState("job"));
   }



   /// <summary>
   /// With a pairing but no usable sign-in, the running service waits; nothing a reconnect could retry exists. An operator who
   /// asks to reconnect over the management pipe is told that a sign-in is required, and no connection is attempted.
   /// </summary>
   [Fact]
   public async Task ReconnectWithoutAUsableSignInSaysASignInIsRequired()
   {
      using SessionFixture fixture = new();
      using SingleServerHarness runtime = fixture.Runtime();
      using CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
      Task running = runtime.RunAsync(stop.Token);
      try
      {
         while(fixture.Clock.PendingTimers == 0)
         { await Task.Delay(10, stop.Token); }
         string reply = await runtime.PipeAsync(new PipeRequest("reconnect"), stop.Token);
         Assert.StartsWith("Sign-in is required", reply, StringComparison.Ordinal);
         Assert.DoesNotContain("Connection retry requested.", fixture.Logs, StringComparer.Ordinal);
         fixture.AssertSentNothing(fixture.Session);
      }
      finally
      {
         await stop.CancelAsync();
         await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
      }
   }



   /// <summary>
   /// A running service restores the saved session once and asks that session for the realtime socket, with no HTTP printing
   /// request first. An authorization evaluation before the renewal interval has passed leaves the session alone; one made
   /// after it, as the heartbeat makes, renews the same session instead of creating another. Neither the session cookie nor the device token appears in the status
   /// snapshot, and the cookie is not readable in the protected file.
   /// </summary>
   [Fact]
   public async Task RestoredSessionOpensTheRealtimeSocketAndRenewsItself()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.Session.ConnectRealtime = async token =>
      {
         opened.TrySetResult();
         await Task.Delay(Timeout.InfiniteTimeSpan, token);
         throw new OperationCanceledException(token);
      };
      using CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
      using SingleServerHarness runtime = fixture.Runtime();
      Task running = runtime.RunAsync(stop.Token);
      try
      {
         await opened.Task.WaitAsync(stop.Token);
         Assert.Equal(1, fixture.Created);
         Assert.Equal(0, fixture.Session.Resumes);
         Assert.Equal(1, fixture.Session.RealtimeAttempts);
         Assert.True(await runtime.EnsureAuthorizationAsync(stop.Token));
         Assert.Equal(0, fixture.Session.Resumes);
         Assert.True(await fixture.EvaluateAsync(runtime, stop.Token));
         Assert.Equal(1, fixture.Created);
         Assert.Equal(1, fixture.Session.Resumes);
         Assert.Empty(fixture.Session.Paths);
         Assert.True(runtime.GetSnapshot().Authentication!.SignedIn);
         string snapshot = JsonSerializer.Serialize(runtime.GetSnapshot(), PrintApiClient.JsonOptions);
         Assert.DoesNotContain("synthetic-qqq-cookie", snapshot, StringComparison.Ordinal);
         Assert.DoesNotContain("synthetic-token", snapshot, StringComparison.Ordinal);
         string protectedData = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi")));
         Assert.DoesNotContain("synthetic-qqq-cookie", protectedData, StringComparison.Ordinal);
      }
      finally
      {
         await stop.CancelAsync();
         await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
      }
   }



   /// <summary>If a restored session turns out to belong to a different user than the one stored with this pairing, the service refuses to use it and keeps the stored identity, so another user's login cannot take over the device.</summary>
   [Fact]
   public async Task RestoredDifferentPrincipalCannotResumeOrReplaceStoredIdentity()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      fixture.Session.Binding = fixture.Session.Binding with { PrincipalId = "different-user" };
      using SingleServerHarness runtime = fixture.Runtime();
      await fixture.RunOnePassAsync(runtime);
      Assert.False(runtime.GetSnapshot().Authentication!.Authorized);
      Assert.Equal(1, fixture.Created);
      fixture.AssertSentNothing(fixture.Session);
      using AgentStore store = new(fixture.Local.DirectoryPath);
      Assert.Equal("principal", store.LoadAuthentication()!.Binding.PrincipalId);
   }



   /// <summary>
   /// An explicit sign-in can claim an existing legacy device for the signed-in user, and the request text never reveals the
   /// cookie. Signing out leaves the service unauthorized and sends no printing requests, but keeps the device pairing, the
   /// stored identity (without its credential) and the job journal, so signing in again continues the same history.
   /// </summary>
   [Fact]
   public async Task ExplicitSignInBindsLegacyAndSignOutRetainsDeviceAndJournal()
   {
      using SessionFixture fixture = new();
      fixture.Local.SeedSpooledJob();
      using SingleServerHarness runtime = fixture.Runtime();
      PipeRequest request = new("sign-in", SessionCredential: fixture.Session.ExportCredential(), ClaimLegacyDevice: true);
      Assert.DoesNotContain("synthetic-qqq-cookie", request.ToString(), StringComparison.Ordinal);
      _ = await runtime.SignInAsync(request, CancellationToken.None);
      Assert.True(fixture.Session.ClaimedLegacy);
      _ = await runtime.SignOutAsync(CancellationToken.None);
      await fixture.RunOnePassAsync(runtime);
      Assert.False(runtime.GetSnapshot().Authentication!.Authorized);
      fixture.AssertSentNothing(fixture.Session);
      using AgentStore store = new(fixture.Local.DirectoryPath);
      Assert.Null(store.LoadAuthentication()!.Credential);
      Assert.Equal("principal", store.LoadAuthentication()!.Binding.PrincipalId);
      Assert.Equal("device", store.LoadConfiguration()!.DeviceId);
      Assert.Equal("SPOOLED", store.GetState("job"));
   }
}
