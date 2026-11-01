/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// How the service uses the signed-in QQQ session: it resumes it, survives temporary failures, refuses a changed identity, and
/// keeps the device pairing and job journal intact through sign-out. A fake session stands in for the server, so these tests
/// check the service's decisions rather than the network.
/// </summary>
public sealed class AgentSessionIntegrationTests
{
   /// <summary>A temporary resume failure (the transport was refused) surfaces as an error for that poll only; once the server is reachable again the same stored session works without asking the user to sign in again.</summary>
   [Fact]
   public async Task TransientResumeFailureDoesNotRequireNewSignIn()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      using AgentRuntime runtime = fixture.Runtime();
      _ = await runtime.PollControlsOnceAsync(CancellationToken.None);
      fixture.Session.ResumeFailure = QqqSessionException.FailureKind.TransportRefused;
      await Assert.ThrowsAsync<QqqSessionException>(() => runtime.PollControlsOnceAsync(CancellationToken.None));
      fixture.Session.ResumeFailure = null;
      _ = await runtime.PollControlsOnceAsync(CancellationToken.None);
      Assert.Equal(2, fixture.Session.Paths.Count);
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
      using AgentRuntime runtime = fixture.Runtime();
      _ = await runtime.PollControlsOnceAsync(CancellationToken.None);
      Assert.True(runtime.GetSnapshot().Authentication!.SignedIn);
      Assert.False(runtime.GetSnapshot().Authentication!.Authorized);
      Assert.Empty(fixture.Session.Paths);
   }



   /// <summary>A sign-in attempt with a credential for an invalid origin is rejected, and the rejection must release the sign-in gate; otherwise the next, valid attempt would hang. The valid retry here completes within five seconds.</summary>
   [Fact]
   public async Task RejectedOriginDoesNotKeepAuthenticationGateLocked()
   {
      using SessionFixture fixture = new();
      fixture.Factory = origin => string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal)
         ? fixture.Session : new QqqSessionClient(origin);
      using AgentRuntime runtime = fixture.Runtime();
      QqqSessionCredential invalid = new(new Uri("https://synthetic.invalid/not-an-origin"), "synthetic-cookie");
      await Assert.ThrowsAsync<ArgumentException>(() => runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: invalid), CancellationToken.None));
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
      _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.Session.ExportCredential()), timeout.Token);
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
   }



   /// <summary>
   /// While a request on the old session is still waiting to be denied, the user signs in again. The new sign-in must not wait
   /// for the old request, and the late denial must not sign the new session out; later polls use only the new session.
   /// </summary>
   [Fact]
   public async Task DelayedOldSessionDenialCannotBlockNewSignIn()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      fixture.Session.DelayUnauthorizedPoll = true;
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      using AgentRuntime runtime = fixture.Runtime();
      Task<int> oldPoll = runtime.PollControlsOnceAsync(timeout.Token);
      await fixture.Session.PollEntered.Task.WaitAsync(timeout.Token);
      fixture.NextSession = new SessionFixture.FakeSession();
      _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.NextSession.ExportCredential()), timeout.Token);
      fixture.Session.ReleasePoll.TrySetResult();
      _ = await oldPoll;
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
      _ = await runtime.PollControlsOnceAsync(timeout.Token);
      Assert.Single(fixture.NextSession.Paths);
   }



   /// <summary>An installation paired with the older device-token method, with no saved sign-in, waits for the user to sign in: no session is created and nothing is sent. The existing pairing and the job journal are left exactly as they were.</summary>
   [Fact]
   public async Task InstalledLegacyPairingWaitsForSignInWithoutNetworkOrJournalLoss()
   {
      using SessionFixture fixture = new();
      fixture.Local.SeedSpooledJob();
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      Assert.Equal(0, fixture.Created);
      Assert.Empty(fixture.Session.Paths);
      Assert.False(runtime.GetSnapshot().Connected);
      using AgentStore store = new(fixture.Local.DirectoryPath);
      Assert.Equal("device", store.LoadConfiguration()!.DeviceId);
      Assert.Equal("SPOOLED", store.GetState("job"));
   }



   /// <summary>
   /// A saved session is restored once, polls over the session transport, and renews itself with a resume between polls. Neither
   /// the session cookie nor the device token appears in the status snapshot, and the cookie is not readable in the protected file.
   /// </summary>
   [Fact]
   public async Task RestoredSessionUsesNativeTransportAndPeriodicallyResumes()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      Assert.Equal(1, fixture.Created);
      Assert.Equal(1, fixture.Session.Resumes);
      Assert.Equal(new[] { "device/control/poll", "device/control/poll" }, fixture.Session.Paths, StringComparer.Ordinal);
      Assert.True(runtime.GetSnapshot().Authentication!.SignedIn);
      string snapshot = JsonSerializer.Serialize(runtime.GetSnapshot(), PrintApiClient.JsonOptions);
      Assert.DoesNotContain("synthetic-qqq-cookie", snapshot, StringComparison.Ordinal);
      Assert.DoesNotContain("synthetic-token", snapshot, StringComparison.Ordinal);
      string protectedData = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi")));
      Assert.DoesNotContain("synthetic-qqq-cookie", protectedData, StringComparison.Ordinal);
   }



   /// <summary>If a restored session turns out to belong to a different user than the one stored with this pairing, the service refuses to use it and keeps the stored identity, so another user's login cannot take over the device.</summary>
   [Fact]
   public async Task RestoredDifferentPrincipalCannotResumeOrReplaceStoredIdentity()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      fixture.Session.Binding = fixture.Session.Binding with { PrincipalId = "different-user" };
      using AgentRuntime runtime = fixture.Runtime();
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      Assert.Empty(fixture.Session.Paths);
      using AgentStore store = new(fixture.Local.DirectoryPath);
      Assert.Equal("principal", store.LoadAuthentication()!.Binding.PrincipalId);
   }



   /// <summary>When the server uses the signed-in session (so it supports remote control), a 404 on the control poll is an error and must not be read as "legacy server, printing allowed", which would bypass pause and disconnect.</summary>
   [Fact]
   public async Task NativeAdvertisedControlsNeverFallBackOn404()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      fixture.Session.Status = HttpStatusCode.NotFound;
      using AgentRuntime runtime = fixture.Runtime();
      await Assert.ThrowsAsync<InvalidDataException>(() => runtime.PollControlsOnceAsync(CancellationToken.None));
      Assert.NotEqual("LEGACY", runtime.GetSnapshot().Control!.Support, StringComparer.Ordinal);
   }



   /// <summary>
   /// An explicit sign-in can claim an existing legacy device for the signed-in user, and the request text never reveals the
   /// cookie. Signing out stops all printing requests but keeps the device pairing, the stored identity (without its credential)
   /// and the job journal, so signing in again continues the same history.
   /// </summary>
   [Fact]
   public async Task ExplicitSignInBindsLegacyAndSignOutRetainsDeviceAndJournal()
   {
      using SessionFixture fixture = new();
      fixture.Local.SeedSpooledJob();
      using AgentRuntime runtime = fixture.Runtime();
      PipeRequest request = new("sign-in", SessionCredential: fixture.Session.ExportCredential(), ClaimLegacyDevice: true);
      Assert.DoesNotContain("synthetic-qqq-cookie", request.ToString(), StringComparison.Ordinal);
      _ = await runtime.SignInAsync(request, CancellationToken.None);
      Assert.True(fixture.Session.ClaimedLegacy);
      _ = await runtime.SignOutAsync(CancellationToken.None);
      await runtime.PollControlsOnceAsync(CancellationToken.None);
      Assert.Empty(fixture.Session.Paths);
      using AgentStore store = new(fixture.Local.DirectoryPath);
      Assert.Null(store.LoadAuthentication()!.Credential);
      Assert.Equal("principal", store.LoadAuthentication()!.Binding.PrincipalId);
      Assert.Equal("device", store.LoadConfiguration()!.DeviceId);
      Assert.Equal("SPOOLED", store.GetState("job"));
   }
}
