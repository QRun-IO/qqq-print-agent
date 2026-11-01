/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Authorization part of the runtime. It holds the service's QQQ session, proves on every connection that the signed-in
/// user, tenant and paired device are still the ones that were approved, and blocks printing (never deletes pairing or
/// history) the moment that stops being true.
/// </summary>
public sealed partial class AgentRuntime
{
   private readonly SemaphoreSlim _authenticationGate = new(1, 1);
   private IQqqSessionClient? _sessionClient;
   private QqqSessionBinding? _sessionBinding;
   private DateTimeOffset _nextSessionResume;
   private bool _authorizationReady;
   private bool _authorizationBlocked;
   private string _authenticationMessage = "Sign in to QQQ to enable printing. Existing pairing and history are preserved.";
   internal TimeSpan SessionResumeInterval { get; init; } = TimeSpan.FromMinutes(5);



   /// <summary>
   /// Creates the session client for a server origin, using the injected factory when one is supplied (tests) and
   /// otherwise a real client that permits plain HTTP only for a loopback origin.
   /// </summary>
   private IQqqSessionClient NewSession(Uri origin) => sessionFactory?.Invoke(origin)
      ?? new QqqSessionClient(origin, allowLoopbackHttp: origin.IsLoopback, traffic: _traffic);



   /// <summary>
   /// Answers whether the service may talk to QQQ right now. It reads the protected saved session, restores it on first
   /// use and checks that the identity (user, tenant, device) matches what was approved, and afterwards renews the
   /// session on the resume interval without querying jobs. A saved session for a different server than the paired one,
   /// or one the server no longer accepts, leaves printing blocked and keeps pairing and history intact.
   /// </summary>
   private async Task<bool> EnsureAuthorizationAsync(CancellationToken token)
   {
      if(apiFactory is not null)
      {
         return true;
      }
      if(_authorizationBlocked)
      {
         return false;
      }
      await _authenticationGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
         AgentAuthentication? saved = store.LoadAuthentication();
         AgentConfiguration? configuration = store.LoadConfiguration();
         if(saved?.Credential is null || (saved.Binding.HasApprovedDevice
            && (configuration is null || !SameOrigin(saved.Binding.Origin, configuration.ServerUrl))))
         {
            lock(_stateLock)
            { _transportNeedsSignIn = true; }
            return false;
         }
         if(_sessionClient is null)
         {
            IQqqSessionClient restored = NewSession(saved.Credential.Origin);
            try
            {
               await restored.RestoreSessionAsync(saved.Credential, token).ConfigureAwait(false);
               QqqSessionBinding binding = configuration is null || !saved.Binding.HasApprovedDevice ? await restored.GetSessionIdentityAsync(token).ConfigureAwait(false)
                  : await restored.BindDeviceAsync(new QqqDeviceCredential(configuration.DeviceId, configuration.DeviceToken), cancellationToken: token).ConfigureAwait(false);
               RequireSameIdentity(saved.Binding, binding);
               ActivateSession(restored, binding, store);
            }
            catch { restored.Dispose(); throw; }
         }
         else if(!_sessionClient.IsAuthenticated)
         {
            BlockAuthorization();
            return false;
         }
         else if(DateTimeOffset.UtcNow >= _nextSessionResume)
         {
            await ResumeAuthorizationAsync(saved, store, token).ConfigureAwait(false);
         }
         return _authorizationReady;
      }
      catch(QqqSessionException exception) when(exception.Failure is QqqSessionException.FailureKind.AuthenticationRequired or QqqSessionException.FailureKind.PermissionDenied)
      {
         BlockAuthorization();
         return false;
      }
      finally { _authenticationGate.Release(); }
   }



   /// <summary>
   /// Renews the live session, re-verifies the identity and saves the new credential. When the session token rotated, the
   /// realtime socket is cancelled and a reconnect requested, because the socket was authenticated with the old token.
   /// </summary>
   private async Task ResumeAuthorizationAsync(AgentAuthentication saved, AgentStore store, CancellationToken token)
   {
      IQqqSessionClient session = _sessionClient ?? throw new InvalidOperationException("Sign in is required.");
      string previousToken = session.ExportCredential().SessionToken;
      await session.ResumeSessionAsync(token).ConfigureAwait(false);
      QqqSessionBinding binding = await session.GetSessionIdentityAsync(token).ConfigureAwait(false);
      RequireSameIdentity(saved.Binding, binding);
      ActivateSession(session, binding, store);
      if(!string.Equals(previousToken, session.ExportCredential().SessionToken, StringComparison.Ordinal))
      {
         lock(_stateLock)
         {
            _realtimeReady = false;
            _realtimeLifetime?.Cancel();
            if(_reconnectRequested.CurrentCount == 0)
            { _reconnectRequested.Release(); }
         }
         SignalPipeline();
      }
   }



   /// <summary>
   /// Persists the verified session and makes it current. Authorization is ready only when the session's server and
   /// device match the saved pairing; a signed-in but unpaired computer is told to enroll instead of being allowed to print.
   /// </summary>
   private void ActivateSession(IQqqSessionClient client, QqqSessionBinding binding, AgentStore store)
   {
      store.SaveAuthentication(new AgentAuthentication(binding, client.ExportCredential()));
      AgentConfiguration? configuration = store.LoadConfiguration();
      lock(_stateLock)
      {
         _sessionClient = client;
         _sessionBinding = binding;
         _transportOrigin = binding.Origin;
         _transportNeedsSignIn = false;
         _authorizationReady = configuration is not null && SameOrigin(binding.Origin, configuration.ServerUrl)
            && string.Equals(binding.DeviceId, configuration.DeviceId, StringComparison.Ordinal);
         _authorizationBlocked = false;
         _nextSessionResume = DateTimeOffset.UtcNow + SessionResumeInterval;
         _authenticationMessage = _authorizationReady ? "Signed in to QQQ." : "Signed in. Enroll this computer to enable printing.";
      }
   }



   /// <summary>
   /// Stops all new work after authorization is lost: clears readiness, cancels the realtime socket and tells the operator
   /// to sign in again. Pairing, history and receipts are untouched; retrying a connection cannot restore a withdrawn permission.
   /// </summary>
   private void BlockAuthorization()
   {
      lock(_stateLock)
      {
         _authorizationReady = false;
         _authorizationBlocked = true;
         _controlReady = false;
         _realtimeReady = false;
         _realtimeLifetime?.Cancel();
         _authenticationMessage = "QQQ authorization is required. Sign in again; pairing and history are preserved.";
      }
      SignalPipeline();
   }



   /// <summary>
   /// Compares scheme, host and port only (case-insensitively), so a session is accepted for the paired server's origin
   /// and never for another host that merely shares a path.
   /// </summary>
   private static bool SameOrigin(Uri origin, string address) => Uri.TryCreate(address, UriKind.Absolute, out Uri? configured)
      && string.Equals(origin.GetLeftPart(UriPartial.Authority), configured.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);



   /// <summary>
   /// Requires the identity key (user, tenant and device) to be unchanged since setup. A different identity is reported as
   /// permission denied, because silently adopting it would let another account print through this pairing.
   /// </summary>
   private static void RequireSameIdentity(QqqSessionBinding expected, QqqSessionBinding actual)
   {
      if(!string.Equals(expected.IdentityKey, actual.IdentityKey, StringComparison.Ordinal))
      {
         throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, "The QQQ user, tenant or device changed. Explicit connection setup is required.");
      }
   }



   /// <summary>Snapshot shown to the companion: whether a session is live, who it is, and the current readable explanation.</summary>
   private AgentAuthenticationStatus AuthenticationStatus() => new(_sessionClient?.IsAuthenticated == true,
      _sessionBinding?.PrincipalId, _sessionBinding?.TenantId, _authenticationMessage, _authorizationReady);
}
