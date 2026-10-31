using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

internal sealed class SessionFixture : IDisposable
{
   internal ControlFixture Local { get; } = new();
   internal FakeSession Session { get; } = new();
   internal FakeSession? NextSession { get; set; }
   internal Func<Uri, IQqqSessionClient>? Factory { get; set; }
   internal int Created;

   internal AgentRuntime Runtime() => new(Local.DirectoryPath, _ => { }, Local.Printer, sessionFactory: origin =>
   {
      Created++;
      return Factory?.Invoke(origin) ?? NextSession ?? Session;
   })
   { SessionResumeInterval = TimeSpan.Zero };

   internal void SaveSession()
   {
      using AgentStore store = new(Local.DirectoryPath);
      store.SaveAuthentication(new AgentAuthentication(Session.Binding, Session.ExportCredential()));
   }

   public void Dispose() => Local.Dispose();

   internal sealed class FakeSession : IQqqSessionClient
   {
      public Uri Origin { get; } = new("https://synthetic.invalid/");
      public bool IsAuthenticated { get; private set; }
      internal QqqSessionBinding Binding { get; set; } = new(new Uri("https://synthetic.invalid/"), "principal", "tenant", "device");
      internal List<string> Paths { get; } = [];
      internal int Resumes;
      internal QqqSessionException.FailureKind? ResumeFailure { get; set; }
      internal bool ClaimedLegacy;
      internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
      internal bool DelayUnauthorizedPoll { get; set; }
      internal TaskCompletionSource PollEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      internal TaskCompletionSource ReleasePoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public void Dispose() { }
      public QqqSessionCredential ExportCredential() => new(Origin, "synthetic-qqq-cookie");
      public Task RestoreSessionAsync(QqqSessionCredential credential, CancellationToken cancellationToken = default)
      {
         Assert.Equal(Origin, credential.Origin);
         IsAuthenticated = true;
         return Task.CompletedTask;
      }
      public Task ResumeSessionAsync(CancellationToken cancellationToken = default)
      {
         Resumes++;
         if(ResumeFailure is { } failure)
         {
            throw new QqqSessionException(failure, "Synthetic session resume failure.");
         }
         return Task.CompletedTask;
      }
      public Task SignOutAsync(CancellationToken cancellationToken = default) { IsAuthenticated = false; return Task.CompletedTask; }
      public Task<QqqSessionBinding> GetSessionIdentityAsync(CancellationToken cancellationToken = default) => Task.FromResult(Binding);
      public Task<QqqSessionBinding> BindDeviceAsync(QqqDeviceCredential credential, bool claimLegacyDevice = false, CancellationToken cancellationToken = default)
      {
         Assert.Equal("device", credential.DeviceId);
         ClaimedLegacy = claimLegacyDevice;
         return Task.FromResult(Binding);
      }
      public async Task<HttpResponseMessage> SendPrintingAsync(HttpMethod method, string relativePath, HttpContent? content = null, CancellationToken cancellationToken = default)
      {
         Paths.Add(relativePath);
         if(DelayUnauthorizedPoll)
         {
            PollEntered.TrySetResult();
            await ReleasePoll.Task.WaitAsync(cancellationToken);
            throw new QqqSessionException(QqqSessionException.FailureKind.AuthenticationRequired, "Synthetic old-session denial.");
         }
         object body = new DeviceControlPoll(1, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), 5, new DeviceDesiredState(1, false, "CONNECTED"), []);
         return new HttpResponseMessage(Status) { Content = new StringContent(JsonSerializer.Serialize(body, PrintApiClient.JsonOptions), Encoding.UTF8, "application/json") };
      }
      public Task<QqqApiDiscovery> DiscoverAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task<QqqAuthenticationMetadata> GetAuthenticationMetadataAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task<QqqBrowserLogin> BeginBrowserLoginAsync(Uri redirectUri, CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task CompleteBrowserLoginAsync(QqqBrowserLogin attempt, Uri callback, CancellationToken cancellationToken = default) => throw new NotSupportedException();
   }
}
