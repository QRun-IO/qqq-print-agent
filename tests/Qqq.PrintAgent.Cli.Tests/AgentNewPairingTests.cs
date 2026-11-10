/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Starting a new pairing must never destroy the old one until the new device is actually enrolled. These tests cover the guarded
/// transition: the old configuration and journal are retained and restorable, a revoked or mismatched device proof blocks resuming,
/// and a rejected enrollment changes nothing on disk.
/// </summary>
public sealed class AgentNewPairingTests
{
   /// <summary>
   /// After an explicit "start new pairing" sign-in, the agent waits (signed in, not authorized, no server requests) even across a
   /// restart, whether the old pairing file was replaced by a different pairing or became unreadable. Enrolling then creates the
   /// replacement device and a fresh journal while the old file bytes are preserved, so restoring them brings the old history back.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task PendingSetupRestoresWithDifferentOrUnreadableOldConfiguration(bool unreadable)
   {
      using SessionFixture fixture = new();
      fixture.Local.SeedSpooledJob();
      string path = Path.Combine(fixture.Local.DirectoryPath, "connection.dpapi");
      byte[] original = File.ReadAllBytes(path);
      ChangeOldConfiguration(fixture, unreadable);
      byte[] retained = File.ReadAllBytes(path);
      fixture.NextSession = UnboundSession();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      using(AgentRuntime runtime = fixture.Runtime())
      {
         _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.NextSession.ExportCredential(), StartNewPairing: true), timeout.Token);
         Assert.False(runtime.GetSnapshot().Authentication!.Authorized);
      }
      fixture.NextSession = UnboundSession();
      using(AgentRuntime restarted = fixture.Runtime())
      {
         _ = await restarted.PollControlsOnceAsync(timeout.Token);
         Assert.True(restarted.GetSnapshot().Authentication!.SignedIn);
         Assert.False(restarted.GetSnapshot().Authentication!.Authorized);
         Assert.Empty(fixture.NextSession.Paths);
         Assert.Equal(retained, File.ReadAllBytes(path));
         _ = await restarted.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-new-code"), timeout.Token);
         Assert.True(restarted.GetSnapshot().Authentication!.Authorized);
         Assert.Equal(new[] { "devices/enroll" }, fixture.NextSession.Paths, StringComparer.Ordinal);
      }
      using(AgentStore store = new(fixture.Local.DirectoryPath))
      {
         Assert.Equal("replacement-device", store.LoadConfiguration()!.DeviceId);
         Assert.Null(store.GetState("job"));
         Assert.Contains("printer", store.GetExposedPrinterIds());
      }
      File.WriteAllBytes(path, original);
      using AgentStore originalStore = new(fixture.Local.DirectoryPath);
      Assert.Equal("SPOOLED", originalStore.GetState("job"));
   }



   /// <summary>A saved sign-in bound to one device cannot be used when the stored device proof now belongs to a different device or cannot be read: no session is created, nothing is sent, and the pairing file is left unchanged.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task BoundSessionCannotResumeWithDifferentOrUnreadableDeviceProof(bool unreadable)
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      ChangeOldConfiguration(fixture, unreadable);
      string path = Path.Combine(fixture.Local.DirectoryPath, "connection.dpapi");
      byte[] retained = File.ReadAllBytes(path);
      using AgentRuntime runtime = fixture.Runtime();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
      _ = await runtime.PollControlsOnceAsync(timeout.Token);
      Assert.False(runtime.GetSnapshot().Authentication!.Authorized);
      Assert.Equal(0, fixture.Created);
      Assert.Empty(fixture.Session.Paths);
      Assert.Equal(retained, File.ReadAllBytes(path));
   }



   /// <summary>Makes the stored pairing differ from the signed-in device: either a saved configuration for another server and device, or garbage bytes that cannot be decrypted.</summary>
   private static void ChangeOldConfiguration(SessionFixture fixture, bool unreadable)
   {
      if(unreadable)
      {
         File.WriteAllBytes(Path.Combine(fixture.Local.DirectoryPath, "connection.dpapi"), [1, 3, 5]);
      }
      else
      {
         using AgentStore store = new(fixture.Local.DirectoryPath);
         store.SaveConfiguration(new AgentConfiguration("https://old-server.invalid", "old-device", "synthetic-old-proof"));
      }
   }



   /// <summary>
   /// When the server has revoked this device, an ordinary sign-in is refused without touching the stored pairing or session
   /// files; only an explicit new-pairing sign-in is accepted, and it still sends nothing. A rejected enrollment (403) leaves
   /// everything unchanged, and a later accepted one replaces the device while the original file can still be restored.
   /// </summary>
   [Fact]
   public async Task RevokedPairingNeedsExplicitNewSetupAndSurvivesRestartBeforeEnrollment()
   {
      using SessionFixture fixture = new();
      fixture.Local.SeedSpooledJob();
      fixture.SaveSession();
      string path = Path.Combine(fixture.Local.DirectoryPath, "connection.dpapi");
      byte[] original = File.ReadAllBytes(path);
      string authPath = Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi");
      byte[] originalAuth = File.ReadAllBytes(authPath);
      fixture.NextSession = UnboundSession();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      using(AgentRuntime runtime = fixture.Runtime())
      {
         await Assert.ThrowsAsync<QqqSessionException>(() => runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.NextSession.ExportCredential()), timeout.Token));
         Assert.Equal(original, File.ReadAllBytes(path));
         Assert.Equal(originalAuth, File.ReadAllBytes(authPath));
         _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.NextSession.ExportCredential(), StartNewPairing: true), timeout.Token);
         Assert.True(runtime.GetSnapshot().Authentication!.SignedIn);
         Assert.False(runtime.GetSnapshot().Authentication!.Authorized);
         Assert.Equal(original, File.ReadAllBytes(path));
         Assert.Empty(fixture.NextSession.Paths);
      }
      fixture.NextSession = UnboundSession();
      using(AgentRuntime restarted = fixture.Runtime())
      {
         _ = await restarted.PollControlsOnceAsync(timeout.Token);
         Assert.True(restarted.GetSnapshot().Authentication!.SignedIn);
         Assert.False(restarted.GetSnapshot().Authentication!.Authorized);
         Assert.Empty(fixture.NextSession.Paths);
         Assert.Equal(original, File.ReadAllBytes(path));
         fixture.NextSession.Status = HttpStatusCode.Forbidden;
         await Assert.ThrowsAsync<HttpRequestException>(() => restarted.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-rejected-code"), timeout.Token));
         Assert.Equal(original, File.ReadAllBytes(path));
         Assert.False(restarted.GetSnapshot().Authentication!.Authorized);
         fixture.NextSession.Status = HttpStatusCode.OK;
         _ = await restarted.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-new-code"), timeout.Token);
         Assert.True(restarted.GetSnapshot().Authentication!.Authorized);
         Assert.Equal(new[] { "devices/enroll", "devices/enroll" }, fixture.NextSession.Paths, StringComparer.Ordinal);
      }
      using(AgentStore store = new(fixture.Local.DirectoryPath))
      {
         Assert.Equal("replacement-device", store.LoadConfiguration()!.DeviceId);
         Assert.Null(store.GetState("job"));
         Assert.Contains("printer", store.GetExposedPrinterIds());
      }
      File.WriteAllBytes(path, original);
      using AgentStore originalStore = new(fixture.Local.DirectoryPath);
      Assert.Equal("SPOOLED", originalStore.GetState("job"));
   }



   /// <summary>
   /// The server can accept the enrollment code and then fail the next step, which binds the new device to the session: either it
   /// refuses, or the connection is lost. Neither may rewrite the stored pairing or the stored session, whether or not the old
   /// pairing could still be read, and neither blocks printing, because the operator is told both are unchanged. The operator
   /// stays signed in, and a later attempt with a fresh code completes on a new journal scope.
   /// </summary>
   [Theory]
   [InlineData(false, false)]
   [InlineData(true, false)]
   [InlineData(false, true)]
   [InlineData(true, true)]
   public async Task BindFailureAfterAcceptedEnrollmentKeepsTheStoredPairingAndSession(bool connectionLost, bool unreadable)
   {
      using SessionFixture fixture = new();
      fixture.Local.SeedSpooledJob();
      if(unreadable)
      { ChangeOldConfiguration(fixture, unreadable: true); }
      else
      { fixture.SaveSession(); }
      string path = Path.Combine(fixture.Local.DirectoryPath, "connection.dpapi");
      byte[] original = File.ReadAllBytes(path);
      string authPath = Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi");
      fixture.NextSession = UnboundSession();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      using(AgentRuntime runtime = fixture.Runtime())
      {
         _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.NextSession.ExportCredential(), StartNewPairing: true), timeout.Token);
         byte[] signedIn = File.ReadAllBytes(authPath);
         fixture.NextSession.FailBindingTransport = connectionLost;
         fixture.NextSession.DenyBinding = !connectionLost;
         _ = await Assert.ThrowsAnyAsync<IOException>(() => runtime.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-accepted-code"), timeout.Token));
         Assert.Equal(new[] { "devices/enroll" }, fixture.NextSession.Paths, StringComparer.Ordinal);
         Assert.Equal(original, File.ReadAllBytes(path));
         Assert.Equal(signedIn, File.ReadAllBytes(authPath));
         Assert.Empty(Directory.GetFiles(fixture.Local.DirectoryPath, "connection.recovery-*.dpapi"));
         AgentSnapshot failed = runtime.GetSnapshot();
         Assert.True(failed.Authentication!.SignedIn);
         Assert.DoesNotContain("authorization is required", failed.Authentication.Message, StringComparison.Ordinal);
         Assert.Equal(unreadable ? null : "device", failed.DeviceId);
         Assert.Contains(failed.Logs, entry => entry.Message.Contains("Enrollment failed; the stored pairing and session are unchanged", StringComparison.Ordinal));
         fixture.NextSession.FailBindingTransport = false;
         fixture.NextSession.DenyBinding = false;
         _ = await runtime.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-second-code"), timeout.Token);
         Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
      }
      Assert.Equal(unreadable ? 1 : 0, Directory.GetFiles(fixture.Local.DirectoryPath, "connection.recovery-*.dpapi").Length);
      using AgentStore store = new(fixture.Local.DirectoryPath);
      Assert.Equal("replacement-device", store.LoadConfiguration()!.DeviceId);
      Assert.Null(store.GetState("job"));
   }



   /// <summary>
   /// A pairing that is working keeps working when a second enrollment is accepted but the new device is never bound, because the
   /// server refused or the connection dropped: neither file is rewritten, and the next authorization pass still succeeds.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task BindFailureLeavesAWorkingPairingWorking(bool connectionLost)
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      string path = Path.Combine(fixture.Local.DirectoryPath, "connection.dpapi");
      byte[] original = File.ReadAllBytes(path);
      string authPath = Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi");
      using AgentRuntime runtime = fixture.Runtime();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      _ = await runtime.PollControlsOnceAsync(timeout.Token);
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
      byte[] session = File.ReadAllBytes(authPath);
      fixture.Session.FailBindingTransport = connectionLost;
      fixture.Session.DenyBinding = !connectionLost;
      _ = await Assert.ThrowsAnyAsync<IOException>(() => runtime.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-accepted-code"), timeout.Token));
      Assert.Equal(original, File.ReadAllBytes(path));
      Assert.Equal(session, File.ReadAllBytes(authPath));
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
      fixture.Session.FailBindingTransport = false;
      fixture.Session.DenyBinding = false;
      _ = await runtime.PollControlsOnceAsync(timeout.Token);
      AgentSnapshot afterNextPass = runtime.GetSnapshot();
      Assert.True(afterNextPass.Authentication!.Authorized);
      Assert.Equal("device", afterNextPass.DeviceId);
   }



   /// <summary>
   /// Once the server has bound the new device, the live session no longer matches what this computer has stored or holds in
   /// memory. If the bound identity then turns out to belong to another user, the session cannot be exported, or the pairing
   /// cannot be written, nothing has been saved: printing is blocked instead of running on the mismatch, the log says so, and an
   /// ordinary sign-in binds the old device again and restores the old pairing.
   /// </summary>
   [Theory]
   [InlineData("identity")]
   [InlineData("export")]
   [InlineData("pairing-file")]
   public async Task FailureAfterTheServerBoundTheDeviceBlocksPrintingUntilSignIn(string failingStep)
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      string path = Path.Combine(fixture.Local.DirectoryPath, "connection.dpapi");
      byte[] original = File.ReadAllBytes(path);
      string authPath = Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi");
      using AgentRuntime runtime = fixture.Runtime();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      _ = await runtime.PollControlsOnceAsync(timeout.Token);
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
      byte[] session = File.ReadAllBytes(authPath);
      fixture.Session.BindAsAnotherPrincipal = failingStep is "identity";
      fixture.Session.ExportsBeforeFailure = failingStep is "export" ? 0 : null;
      if(failingStep is "pairing-file")
      {
         // SaveConfiguration writes this temporary name first; a directory in its place makes that write fail.
         _ = Directory.CreateDirectory(path + ".tmp");
      }
      Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => runtime.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-accepted-code"), timeout.Token));
      Assert.IsAssignableFrom(failingStep is "pairing-file" ? typeof(UnauthorizedAccessException) : typeof(IOException), thrown);
      Assert.Equal(original, File.ReadAllBytes(path));
      Assert.Equal(session, File.ReadAllBytes(authPath));
      AgentSnapshot failed = runtime.GetSnapshot();
      Assert.False(failed.Authentication!.Authorized);
      Assert.Contains("Sign in again", failed.Authentication.Message, StringComparison.Ordinal);
      Assert.Contains(failed.Logs, entry => entry.Message.Contains("after the server bound the new device", StringComparison.Ordinal));
      fixture.Session.BindAsAnotherPrincipal = false;
      fixture.Session.ExportsBeforeFailure = null;
      _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.Session.ExportCredential()), timeout.Token);
      AgentSnapshot recovered = runtime.GetSnapshot();
      Assert.True(recovered.Authentication!.Authorized);
      Assert.Equal("device", recovered.DeviceId);
   }



   /// <summary>
   /// If the new pairing is saved and its session then cannot be written, the two files disagree and an ordinary sign-in is
   /// refused. Printing is blocked, the status and the log name the recovery that works instead of a sign-in, and enrolling
   /// again with a fresh code completes.
   /// </summary>
   [Fact]
   public async Task FailureBetweenSavingThePairingAndItsSessionBlocksPrintingUntilEnrolledAgain()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      string path = Path.Combine(fixture.Local.DirectoryPath, "connection.dpapi");
      byte[] original = File.ReadAllBytes(path);
      string blocker = Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi.tmp");
      using AgentRuntime runtime = fixture.Runtime();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      _ = await runtime.PollControlsOnceAsync(timeout.Token);
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
      // SaveAuthentication writes this temporary name first; a directory in its place makes that write fail.
      _ = Directory.CreateDirectory(blocker);
      _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-accepted-code"), timeout.Token));
      Assert.NotEqual(original, File.ReadAllBytes(path));
      AgentSnapshot failed = runtime.GetSnapshot();
      Assert.False(failed.Authentication!.Authorized);
      Assert.Contains("Enroll this computer again", failed.Authentication.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("Sign in again", failed.Authentication.Message, StringComparison.Ordinal);
      Assert.DoesNotContain(failed.Logs, entry => entry.Message.Contains("Sign in again", StringComparison.Ordinal));
      Assert.Contains(failed.Logs, entry => entry.Message.Contains("after the new pairing was saved but before its session was", StringComparison.Ordinal));
      Directory.Delete(blocker);
      _ = await Assert.ThrowsAsync<QqqSessionException>(() => runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.Session.ExportCredential()), timeout.Token));
      Assert.False(runtime.GetSnapshot().Authentication!.Authorized);
      _ = await runtime.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-second-code"), timeout.Token);
      AgentSnapshot recovered = runtime.GetSnapshot();
      Assert.True(recovered.Authentication!.Authorized);
      Assert.Equal("replacement-device", recovered.DeviceId);
   }



   /// <summary>
   /// If activation fails after the new pairing and its session are both saved, the files agree with each other and with the
   /// server. Printing is blocked in the running service, and a restarted service finishes the switch with no operator action.
   /// </summary>
   [Fact]
   public async Task FailureAfterBothFilesAreSavedIsFinishedByARestart()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      using(AgentRuntime runtime = fixture.Runtime())
      {
         _ = await runtime.PollControlsOnceAsync(timeout.Token);
         Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
         // The first export builds the session to save; the second, during activation, is the one that fails.
         fixture.Session.ExportsBeforeFailure = 1;
         _ = await Assert.ThrowsAsync<IOException>(() => runtime.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-accepted-code"), timeout.Token));
         AgentSnapshot failed = runtime.GetSnapshot();
         Assert.False(failed.Authentication!.Authorized);
         Assert.Contains(failed.Logs, entry => entry.Message.Contains("after the new pairing and its session were saved", StringComparison.Ordinal));
      }
      fixture.Session.ExportsBeforeFailure = null;
      using AgentRuntime restarted = fixture.Runtime();
      _ = await restarted.PollControlsOnceAsync(timeout.Token);
      AgentSnapshot recovered = restarted.GetSnapshot();
      Assert.True(recovered.Authentication!.Authorized);
      Assert.Equal("replacement-device", recovered.DeviceId);
   }



   /// <summary>A fake session for a user with no device bound that treats the existing device id as revoked, which is what the server reports after a pairing is withdrawn.</summary>
   private static SessionFixture.FakeSession UnboundSession()
   {
      SessionFixture.FakeSession session = new() { RevokedDeviceId = "device" };
      session.Binding = session.Binding with { DeviceId = null };
      return session;
   }
}
