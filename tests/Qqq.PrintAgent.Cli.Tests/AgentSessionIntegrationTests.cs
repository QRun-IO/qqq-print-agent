using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentSessionIntegrationTests
{
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
