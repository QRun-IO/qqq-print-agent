/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Test harness for signed-in (QQQ session) behavior: an enrolled local state plus a scriptable fake session that records the
/// printing requests it receives and can fail resume, deny device binding or hold the realtime socket open. No network or real
/// sign-in is used.
/// </summary>
internal sealed class SessionFixture : IDisposable
{
   internal EnrolledStateFixture Local { get; } = new();
   internal FakeSession Session { get; } = new();
   internal FakeSession? NextSession { get; set; }
   internal Func<Uri, IQqqSessionClient>? Factory { get; set; }
   internal int Created;



   /// <summary>
   /// Builds a runtime on a private pipe whose session factory hands out the scripted sessions in priority order (custom
   /// factory, next session, default) and counts each creation. The session renewal interval is zero, so every authorization
   /// evaluation after the first renews the session.
   /// </summary>
   internal AgentRuntime Runtime()
   {
      string pipe = "qqq-session-fixture-" + Guid.NewGuid().ToString("N");
      return new(Local.DirectoryPath, _ => { }, Local.Printer, pipeName: pipe,
         pipeFactory: () => new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
         sessionFactory: origin =>
         {
            Created++;
            return Factory?.Invoke(origin) ?? NextSession ?? Session;
         })
      { SessionResumeInterval = TimeSpan.Zero };
   }



   /// <summary>Stores the fake session's binding and credential as if the user had already signed in, so a runtime can resume it.</summary>
   internal void SaveSession()
   {
      using AgentStore store = new(Local.DirectoryPath);
      store.SaveAuthentication(new AgentAuthentication(Session.Binding, Session.ExportCredential()));
   }



   /// <summary>Disposes the underlying local state, which removes the temporary folder.</summary>
   public void Dispose() => Local.Dispose();



   /// <summary>
   /// In-memory stand-in for the signed-in QQQ session client. It accepts a restore, can be told to fail resume or deny binding,
   /// answers device enrollment with a canned response, and refuses the browser and password sign-in calls this fixture never
   /// needs.
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

      /// <summary>When set, binding a device fails as a lost connection before the server has bound anything.</summary>
      internal bool FailBindingTransport;
      /// <summary>When set, a successful bind reports a different principal, as if the device had been bound for another user.</summary>
      internal bool BindAsAnotherPrincipal;

      /// <summary>How many more credential exports succeed before one fails as a lost connection; null never fails.</summary>
      internal int? ExportsBeforeFailure;
      private string? _issuedDeviceId;
      private string? _deviceBeforeIssue;
      internal string? RevokedDeviceId;
      internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
      internal Func<CancellationToken, Task<System.Net.WebSockets.WebSocket>>? ConnectRealtime { get; set; }



      /// <summary>Opens the realtime channel through the scripted connector; without one the realtime path is unsupported in that test.</summary>
      public Task<System.Net.WebSockets.WebSocket> ConnectPrintingAsync(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout, CancellationToken cancellationToken = default)
         => ConnectRealtime?.Invoke(cancellationToken) ?? throw new NotSupportedException();



      /// <summary>Does nothing: the fake session owns no connection or handle, so there is nothing to release when the runtime disposes it.</summary>
      public void Dispose() { }



      /// <summary>Returns a fixed synthetic cookie credential for this origin, or fails as a lost connection once the scripted number of exports is used up.</summary>
      public QqqSessionCredential ExportCredential()
      {
         if(ExportsBeforeFailure is { } remaining)
         {
            if(remaining == 0)
            {
               throw new IOException("Synthetic failure while exporting the session.");
            }
            ExportsBeforeFailure = remaining - 1;
         }
         return new(Origin, "synthetic-qqq-cookie");
      }



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



      /// <summary>The scripted provider sign-out the fake server returns at logout; null means the server offers none.</summary>
      internal QqqProviderLogout? ProviderLogout { get; set; }

      /// <summary>The callback the service asked the provider to return to, or null when only the QQQ session was ended.</summary>
      internal Uri? RequestedLogoutRedirect { get; private set; }



      /// <summary>Marks the session signed out, records which callback was asked for and returns the scripted provider sign-out.</summary>
      public Task<QqqProviderLogout?> SignOutWithProviderAsync(Uri postLogoutRedirectUri, CancellationToken cancellationToken = default)
      {
         IsAuthenticated = false;
         RequestedLogoutRedirect = postLogoutRedirectUri;
         return Task.FromResult(ProviderLogout);
      }



      /// <summary>Returns the scripted principal, tenant and device binding.</summary>
      public Task<QqqSessionBinding> GetSessionIdentityAsync(CancellationToken cancellationToken = default) => Task.FromResult(Binding);



      /// <summary>
      /// Fails as a lost connection when scripted to, and refuses with permission-denied when binding is denied or the device was
      /// revoked. Otherwise it behaves as the server does: the session takes on a device only when that device is bound, so binding
      /// the device an enrollment issued switches to it, and binding the device held before switches back. Any other device must
      /// already be the one the session holds. It records whether a legacy device was claimed and returns the binding (as another
      /// principal when scripted to).
      /// </summary>
      public Task<QqqSessionBinding> BindDeviceAsync(QqqDeviceCredential credential, bool claimLegacyDevice = false, CancellationToken cancellationToken = default)
      {
         if(FailBindingTransport)
         {
            throw new IOException("Synthetic connection loss while binding the device.");
         }
         if(DenyBinding || string.Equals(RevokedDeviceId, credential.DeviceId, StringComparison.Ordinal))
         {
            throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, "Synthetic binding denied.");
         }
         if(string.Equals(credential.DeviceId, _issuedDeviceId, StringComparison.Ordinal) && !string.Equals(Binding.DeviceId, _issuedDeviceId, StringComparison.Ordinal))
         {
            _deviceBeforeIssue = Binding.DeviceId;
            Binding = Binding with { DeviceId = _issuedDeviceId };
         }
         else if(_deviceBeforeIssue is not null && string.Equals(credential.DeviceId, _deviceBeforeIssue, StringComparison.Ordinal))
         {
            Binding = Binding with { DeviceId = _deviceBeforeIssue };
         }
         Assert.Equal(Binding.DeviceId, credential.DeviceId);
         ClaimedLegacy = claimLegacyDevice;
         return Task.FromResult(BindAsAnotherPrincipal ? Binding with { PrincipalId = "someone-else" } : Binding);
      }



      /// <summary>
      /// Records the requested path. Device enrollment returns a replacement device and token (or the scripted error status).
      /// Nothing else is expected over HTTP from a runtime that has no realtime socket, so any other path gets an empty object
      /// with the scripted status and the recorded path shows the test what was asked.
      /// </summary>
      public Task<HttpResponseMessage> SendPrintingAsync(HttpMethod method, string relativePath, HttpContent? content = null, CancellationToken cancellationToken = default)
      {
         Paths.Add(relativePath);
         if(string.Equals(relativePath, "devices/enroll", StringComparison.Ordinal))
         {
            if(Status == HttpStatusCode.OK)
            {
               _issuedDeviceId = "replacement-device";
               return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new EnrollmentResponse(1, "replacement-device", "replacement-token", 5), PrintApiClient.JsonOptions), Encoding.UTF8, "application/json") });
            }
         }
         return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
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
