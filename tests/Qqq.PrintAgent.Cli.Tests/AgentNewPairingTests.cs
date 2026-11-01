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



   /// <summary>A fake session for a user with no device bound that treats the existing device id as revoked, which is what the server reports after a pairing is withdrawn.</summary>
   private static SessionFixture.FakeSession UnboundSession()
   {
      SessionFixture.FakeSession session = new() { RevokedDeviceId = "device" };
      session.Binding = session.Binding with { DeviceId = null };
      return session;
   }
}
