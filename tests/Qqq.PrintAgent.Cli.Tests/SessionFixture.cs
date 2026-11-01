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
/// Test harness for signed-in (QQQ session) behavior: a control fixture plus a scriptable fake session that records the
/// printing requests it receives and can fail resume, deny device binding or stall a poll. No network or real sign-in is used.
/// </summary>
internal sealed class SessionFixture : IDisposable
{
   internal ControlFixture Local { get; } = new();
   internal FakeSession Session { get; } = new();
   internal FakeSession? NextSession { get; set; }
   internal Func<Uri, IQqqSessionClient>? Factory { get; set; }
   internal int Created;



   /// <summary>Builds a runtime whose session factory hands out the scripted sessions in priority order (custom factory, next session, default) and counts each creation.</summary>
   internal AgentRuntime Runtime() => new(Local.DirectoryPath, _ => { }, Local.Printer, sessionFactory: origin =>
   {
      Created++;
      return Factory?.Invoke(origin) ?? NextSession ?? Session;
   })
   { SessionResumeInterval = TimeSpan.Zero };



   /// <summary>Stores the fake session's binding and credential as if the user had already signed in, so a runtime can resume it.</summary>
   internal void SaveSession()
   {
      using AgentStore store = new(Local.DirectoryPath);
      store.SaveAuthentication(new AgentAuthentication(Session.Binding, Session.ExportCredential()));
   }



   /// <summary>Disposes the underlying control fixture, which removes the temporary state.</summary>
   public void Dispose() => Local.Dispose();



   /// <summary>
   /// In-memory stand-in for the signed-in QQQ session client. It accepts a restore, can be told to fail resume or deny binding,
   /// answers the device enroll and control poll with canned responses, and refuses the browser and password sign-in calls this
   /// fixture never needs.
   /// </summary>
   internal sealed class FakeSession : IQqqSessionClient
   {
      public Uri Origin { get; } = new("https://synthetic.invalid/");
      public bool IsAuthenticated { get; private set; }
      internal QqqSessionBinding Binding { get; set; } = new(new Uri("https://synthetic.invalid/"), "principal", "tenant", "device");
      internal List<string> Paths { get; } = [];
      internal int Resumes;
      internal QqqSessionException.FailureKind? ResumeFailure { get; set; }
      internal bool ClaimedLegacy;
      internal bool DenyBinding;
      internal string? RevokedDeviceId;
      internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
      internal bool DelayUnauthorizedPoll { get; set; }
      internal Func<CancellationToken, Task<System.Net.WebSockets.WebSocket>>? ConnectRealtime { get; set; }



      /// <summary>Opens the realtime channel through the scripted connector; without one the realtime path is unsupported in that test.</summary>
      public Task<System.Net.WebSockets.WebSocket> ConnectPrintingAsync(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout, CancellationToken cancellationToken = default)
         => ConnectRealtime?.Invoke(cancellationToken) ?? throw new NotSupportedException();
      internal TaskCompletionSource PollEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      internal TaskCompletionSource ReleasePoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Nothing to release.</summary>
      public void Dispose() { }



      /// <summary>Returns a fixed synthetic cookie credential for this origin.</summary>
      public QqqSessionCredential ExportCredential() => new(Origin, "synthetic-qqq-cookie");



      /// <summary>Checks the credential belongs to this origin and marks the session authenticated.</summary>
      public Task RestoreSessionAsync(QqqSessionCredential credential, CancellationToken cancellationToken = default)
      {
         Assert.Equal(Origin, credential.Origin);
         IsAuthenticated = true;
         return Task.CompletedTask;
      }



      /// <summary>Counts the resume attempt and throws the scripted failure kind, if any, so recovery and sign-out paths can be driven.</summary>
      public Task ResumeSessionAsync(CancellationToken cancellationToken = default)
      {
         Resumes++;
         if(ResumeFailure is { } failure)
         {
            throw new QqqSessionException(failure, "Synthetic session resume failure.");
         }
         return Task.CompletedTask;
      }



      /// <summary>Marks the session signed out.</summary>
      public Task SignOutAsync(CancellationToken cancellationToken = default) { IsAuthenticated = false; return Task.CompletedTask; }



      /// <summary>Returns the scripted principal, tenant and device binding.</summary>
      public Task<QqqSessionBinding> GetSessionIdentityAsync(CancellationToken cancellationToken = default) => Task.FromResult(Binding);



      /// <summary>Refuses with permission-denied when binding is denied or the device was revoked; otherwise checks the device id matches and records whether a legacy device was claimed.</summary>
      public Task<QqqSessionBinding> BindDeviceAsync(QqqDeviceCredential credential, bool claimLegacyDevice = false, CancellationToken cancellationToken = default)
      {
         if(DenyBinding || string.Equals(RevokedDeviceId, credential.DeviceId, StringComparison.Ordinal))
         {
            throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, "Synthetic binding denied.");
         }
         Assert.Equal(Binding.DeviceId, credential.DeviceId);
         ClaimedLegacy = claimLegacyDevice;
         return Task.FromResult(Binding);
      }



      /// <summary>
      /// Records the requested path. Device enrollment returns a replacement device and token (or the scripted error status). A
      /// poll can be held open and then fail as an old-session denial, to test a session replaced mid-request; otherwise it
      /// returns a normal connected control poll.
      /// </summary>
      public async Task<HttpResponseMessage> SendPrintingAsync(HttpMethod method, string relativePath, HttpContent? content = null, CancellationToken cancellationToken = default)
      {
         Paths.Add(relativePath);
         if(string.Equals(relativePath, "devices/enroll", StringComparison.Ordinal))
         {
            if(Status != HttpStatusCode.OK)
            {
               return new HttpResponseMessage(Status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            Binding = Binding with { DeviceId = "replacement-device" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new EnrollmentResponse(1, "replacement-device", "replacement-token", 5), PrintApiClient.JsonOptions), Encoding.UTF8, "application/json") };
         }
         if(DelayUnauthorizedPoll)
         {
            PollEntered.TrySetResult();
            await ReleasePoll.Task.WaitAsync(cancellationToken);
            throw new QqqSessionException(QqqSessionException.FailureKind.AuthenticationRequired, "Synthetic old-session denial.");
         }
         object body = new DeviceControlPoll(1, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), 5, new DeviceDesiredState(1, false, "CONNECTED"), []);
         return new HttpResponseMessage(Status) { Content = new StringContent(JsonSerializer.Serialize(body, PrintApiClient.JsonOptions), Encoding.UTF8, "application/json") };
      }



      /// <summary>Returns an empty discovery result.</summary>
      public Task<QqqApiDiscovery> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(new QqqApiDiscovery());



      /// <summary>Not used by these tests; throws if reached.</summary>
      public Task<QqqAuthenticationMetadata> GetAuthenticationMetadataAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();



      /// <summary>Not used by these tests; throws if reached.</summary>
      public Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();



      /// <summary>Not used by these tests; throws if reached.</summary>
      public Task<QqqBrowserLogin> BeginBrowserLoginAsync(Uri redirectUri, CancellationToken cancellationToken = default) => throw new NotSupportedException();



      /// <summary>Not used by these tests; throws if reached.</summary>
      public Task CompleteBrowserLoginAsync(QqqBrowserLogin attempt, Uri callback, CancellationToken cancellationToken = default) => throw new NotSupportedException();
   }
}
