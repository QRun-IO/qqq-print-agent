using System.Net;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentNewPairingTests
{
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

   private static SessionFixture.FakeSession UnboundSession()
   {
      SessionFixture.FakeSession session = new() { RevokedDeviceId = "device" };
      session.Binding = session.Binding with { DeviceId = null };
      return session;
   }
}
