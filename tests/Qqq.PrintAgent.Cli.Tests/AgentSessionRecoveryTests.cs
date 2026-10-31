using System.Security.Cryptography;
using System.Text;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentSessionRecoveryTests
{
   [Fact]
   public async Task StructurallyInvalidProtectedSessionCanBeReplacedAfterSignIn()
   {
      string[] invalid =
      [
         "{}",
         """{"binding":{"principalId":"principal","tenantId":"tenant"}}""",
         """{"binding":{"origin":"https://synthetic.invalid/","tenantId":"tenant"}}""",
         """{"binding":{"origin":"https://synthetic.invalid/","principalId":"principal","tenantId":" "}}""",
         """{"binding":{"origin":"http://remote.invalid/","principalId":"principal","tenantId":"tenant"}}""",
         """{"binding":{"origin":"https://synthetic.invalid/","principalId":"principal","tenantId":"tenant"},"credential":{"origin":"https://other.invalid/","sessionToken":"synthetic-cookie"}}""",
      ];
      foreach(string json in invalid)
      {
         using SessionFixture fixture = new();
         fixture.Local.SeedSpooledJob();
         byte[] corrupt = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
         File.WriteAllBytes(Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi"), corrupt);
         using AgentRuntime runtime = fixture.Runtime();
         using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
         using(AgentStore unreadable = new(fixture.Local.DirectoryPath))
         {
            Assert.Throws<InvalidDataException>(() => unreadable.LoadAuthentication());
         }
         _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.Session.ExportCredential()), timeout.Token);
         Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
         Assert.Equal(corrupt, File.ReadAllBytes(Assert.Single(Directory.GetFiles(fixture.Local.DirectoryPath, "qqq-session.recovery-*.dpapi"))));
         using AgentStore store = new(fixture.Local.DirectoryPath);
         Assert.Equal("SPOOLED", store.GetState("job"));
         Assert.NotNull(store.LoadAuthentication()!.Binding);
      }
   }

   [Fact]
   public async Task ExplicitSignInReplacesUnreadableSessionAfterServerBinding()
   {
      using SessionFixture fixture = new();
      fixture.Local.SeedSpooledJob();
      byte[] corrupt = [1, 2, 3, 4];
      File.WriteAllBytes(Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi"), corrupt);
      using AgentRuntime runtime = fixture.Runtime();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
      fixture.Session.DenyBinding = true;
      await Assert.ThrowsAsync<Qqq.PrintAgent.Core.Authentication.QqqSessionException>(() => runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.Session.ExportCredential()), timeout.Token));
      Assert.Equal(corrupt, File.ReadAllBytes(Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi")));
      Assert.Empty(Directory.GetFiles(fixture.Local.DirectoryPath, "qqq-session.recovery-*.dpapi"));
      fixture.Session.DenyBinding = false;
      _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.Session.ExportCredential()), timeout.Token);
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
      using AgentStore store = new(fixture.Local.DirectoryPath);
      Assert.Equal("device", store.LoadConfiguration()!.DeviceId);
      Assert.Equal("SPOOLED", store.GetState("job"));
      Assert.Contains("printer", store.GetExposedPrinterIds());
      Assert.Equal(corrupt, File.ReadAllBytes(Assert.Single(Directory.GetFiles(fixture.Local.DirectoryPath, "qqq-session.recovery-*.dpapi"))));
      Assert.NotNull(store.LoadAuthentication()!.Credential);
   }

   [Fact]
   public async Task LostDeviceProofRequiresExplicitEnrollmentAfterFreshSignIn()
   {
      using SessionFixture fixture = new();
      fixture.Local.SeedSpooledJob();
      string path = Path.Combine(fixture.Local.DirectoryPath, "connection.dpapi");
      byte[] original = File.ReadAllBytes(path);
      byte[] corrupt = [5, 6, 7, 8];
      File.WriteAllBytes(path, corrupt);
      fixture.Session.Binding = fixture.Session.Binding with { DeviceId = null };
      using AgentRuntime runtime = fixture.Runtime();
      Assert.False(runtime.GetSnapshot().Enrolled);
      Assert.Equal(corrupt, File.ReadAllBytes(path));
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
      _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.Session.ExportCredential()), timeout.Token);
      Assert.False(runtime.GetSnapshot().Authentication!.Authorized);
      Assert.Equal(corrupt, File.ReadAllBytes(path));
      Assert.Empty(fixture.Session.Paths);
      _ = await runtime.EnrollWithGatesAsync(new PipeRequest("enroll", "https://synthetic.invalid", "synthetic-code"), timeout.Token);
      Assert.True(runtime.GetSnapshot().Authentication!.Authorized);
      Assert.Equal(corrupt, File.ReadAllBytes(Assert.Single(Directory.GetFiles(fixture.Local.DirectoryPath, "connection.recovery-*.dpapi"))));
      using(AgentStore store = new(fixture.Local.DirectoryPath))
      {
         Assert.Equal("replacement-device", store.LoadConfiguration()!.DeviceId);
         Assert.Null(store.GetState("job"));
         Assert.Contains("printer", store.GetExposedPrinterIds());
      }
      // Restore only the fixture's old proof to verify its journal was retained, not reassigned.
      File.WriteAllBytes(path, original);
      using AgentStore originalStore = new(fixture.Local.DirectoryPath);
      Assert.Equal("SPOOLED", originalStore.GetState("job"));
   }

   [Fact]
   public async Task ExplicitSignOutQuarantinesUnreadableSessionWithoutLosingPairing()
   {
      using SessionFixture fixture = new();
      fixture.Local.SeedSpooledJob();
      byte[] corrupt = [9, 10, 11];
      string path = Path.Combine(fixture.Local.DirectoryPath, "qqq-session.dpapi");
      File.WriteAllBytes(path, corrupt);
      using AgentRuntime runtime = fixture.Runtime();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
      Assert.Contains("revoke the previous session in QQQ", await runtime.SignOutAsync(timeout.Token), StringComparison.Ordinal);
      Assert.False(File.Exists(path));
      Assert.Equal(corrupt, File.ReadAllBytes(Assert.Single(Directory.GetFiles(fixture.Local.DirectoryPath, "qqq-session.recovery-*.dpapi"))));
      Assert.False(runtime.GetSnapshot().Authentication!.Authorized);
      using AgentStore store = new(fixture.Local.DirectoryPath);
      Assert.Equal("device", store.LoadConfiguration()!.DeviceId);
      Assert.Equal("SPOOLED", store.GetState("job"));
      Assert.Contains("printer", store.GetExposedPrinterIds());
   }
}
