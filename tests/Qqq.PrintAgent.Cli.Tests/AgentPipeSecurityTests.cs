using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentPipeSecurityTests
{
   [Fact]
   public async Task ExplicitClientRightsConnectWithoutPermissionToCreateServerInstances()
   {
      string name = "QqqPrintAgent-acl-test-" + Guid.NewGuid().ToString("N");
      PipeSecurity security = AgentRuntime.CreatePipeSecurity();
      Assert.All(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()
         .Where(rule => string.Equals(rule.IdentityReference.Value, "S-1-5-32-544", StringComparison.Ordinal)),
         rule => Assert.False(rule.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance)));
      using var identity = WindowsIdentity.GetCurrent();
      // A non-elevated test runner receives precisely the production BA client rights.
      security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
      await using NamedPipeServerStream server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
         PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      for(int connection = 0; connection < 2; connection++)
      {
         Task accepted = server.WaitForConnectionAsync(timeout.Token);
         await using NamedPipeClientStream client = AgentPipeClient.CreateClient(name);
         await client.ConnectAsync(timeout.Token);
         await accepted;
         await client.WriteAsync(new byte[] { 42 }, timeout.Token);
         byte[] input = new byte[1];
         Assert.Equal(1, await server.ReadAsync(input, timeout.Token));
         Assert.Equal(42, input[0]);
         server.Disconnect();
         Assert.Throws<UnauthorizedAccessException>(() => NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security));
      }
   }

   [Theory]
   [InlineData("enroll")]
   [InlineData("snapshot")]
   public async Task SubstituteServerReceivesNoRequestBytes(string operation)
   {
      string name = "QqqPrintAgent-untrusted-test-" + Guid.NewGuid().ToString("N");
      await using NamedPipeServerStream fake = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      Task connected = fake.WaitForConnectionAsync(timeout.Token);
      Task<string> request = AgentPipeClient.SendAsync(new PipeRequest(operation, "https://print.example.com", "synthetic-enrollment-never-send"), name);
      await connected;
      await Assert.ThrowsAnyAsync<Exception>(() => request);
      byte[] buffer = new byte[1];
      Assert.Equal(0, await fake.ReadAsync(buffer, timeout.Token));
   }

   [Fact]
   public void StartupDefinitionCannotBeReplacedWhileSchedulerReadsIt()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-startup-lock-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      string definition = Path.Combine(directory, "startup.xml");
      try
      {
         using(FileStream held = AgentStartup.CreateHeldDefinition(definition, "<Task>synthetic</Task>"))
         {
            Assert.Contains("synthetic", File.ReadAllText(definition), StringComparison.Ordinal);
            Assert.Throws<IOException>(() => File.WriteAllText(definition, "replacement"));
            Assert.Throws<IOException>(() => File.Delete(definition));
            Assert.Throws<IOException>(() => File.Move(definition, Path.Combine(directory, "moved.xml")));
         }
         Assert.Contains("synthetic", File.ReadAllText(definition), StringComparison.Ordinal);
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }
}
