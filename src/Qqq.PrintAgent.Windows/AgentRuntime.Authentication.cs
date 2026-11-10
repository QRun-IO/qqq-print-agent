/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;
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
   private string _authenticationMessage = $"Sign in to {AppIdentity.ServerName} to enable printing. Existing pairing and history are preserved.";
   private string? _lastAuthorizationNote;
   internal TimeSpan SessionResumeInterval { get; init; } = TimeSpan.FromMinutes(5);



   /// <summary>
   /// Creates the session client for a server origin, using the injected factory when one is supplied (tests) and
   /// otherwise a real client that permits plain HTTP only for a loopback origin.
   /// </summary>
   private IQqqSessionClient NewSession(Uri origin)
   {
      if(sessionFactory is not null)
      { return sessionFactory(origin); }
      ServerTrust trust;
      using(var store = new AgentStore(directory, recoverUnreadableConfiguration: true))
      { trust = store.LoadServerTrust(); }
      bool allowUntrusted = trust.AppliesTo(origin);
      if(allowUntrusted)
      { WriteLog("WARN", $"Creating a session for {origin.Authority} with untrusted server certificates ALLOWED for this server (administrator setting; development only)."); }
      else if(trust.AllowUntrustedCertificate)
      { WriteLog("INFO", $"The untrusted-certificate exception is saved for {trust.Origin}, not {ServerTrust.Normalize(origin)}; normal certificate validation applies to this session."); }
      return new QqqSessionClient(origin, allowLoopbackHttp: origin.IsLoopback, traffic: _traffic, diagnostics: SessionDiagnostics(), allowUntrustedCertificate: allowUntrusted);
   }



   /// <summary>Logs an authorization observation only when it differs from the last one, because the reconnect loop re-evaluates authorization on every attempt.</summary>
   private void NoteAuthorization(string level, string message)
   {
      lock(_stateLock)
      {
         if(string.Equals(_lastAuthorizationNote, message, StringComparison.Ordinal))
         { return; }
         _lastAuthorizationNote = message;
      }
      WriteLog(level, message);
   }



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
         if(_authorizationBlocked)
         {
            // Whoever held the gate (a sign-out, a failed enrollment) blocked authorization while this call was waiting.
            return false;
         }
         using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
         AgentAuthentication? saved = LoadSavedSession(store, forRecovery: false);
         AgentConfiguration? configuration = store.LoadConfiguration();
         if(saved?.Credential is null || (saved.Binding.HasApprovedDevice
            && (configuration is null || !SameOrigin(saved.Binding.Origin, configuration.ServerUrl))))
         {
            NoteAuthorization("INFO", DescribeMissingAuthorization(saved, configuration));
            lock(_stateLock)
            { _transportNeedsSignIn = true; }
            return false;
         }
         if(_sessionClient is null)
         {
            await RestoreSavedSessionAsync(saved, configuration, store, token).ConfigureAwait(false);
         }
         else if(!_sessionClient.IsAuthenticated)
         {
            WriteLog("WARN", $"The held {AppIdentity.ServerName} session is no longer authenticated (the server ended it or an identity check failed).");
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
         WriteLog("WARN", $"Authorization withdrawn ({exception.Failure}): {exception.Message}");
         BlockAuthorization();
         return false;
      }
      finally { _authenticationGate.Release(); }
   }



   /// <summary>
   /// Reads the saved session and logs what that read did about a pairing change that was staged and not finished. The
   /// store has no log, and moving, discarding or setting aside a credential file must leave a line behind.
   /// </summary>
   private AgentAuthentication? LoadSavedSession(AgentStore store, bool forRecovery)
   {
      try
      {
         return forRecovery ? store.LoadAuthenticationForRecovery() : store.LoadAuthentication();
      }
      finally
      {
         NoteStagedPairing(store);
      }
   }



   /// <summary>Writes the log line for what the store last did about a staged pairing change, including when the read that did it then failed.</summary>
   private void NoteStagedPairing(AgentStore store)
   {
      switch(store.StagedPairing)
      {
         case StagedPairingOutcome.Completed:
            WriteLog("INFO", "Finished a pairing change that was interrupted: the session saved with the new pairing is now in place.");
            break;
         case StagedPairingOutcome.CompletedAfterQuarantine:
            WriteLog("WARN", "Finished a pairing change that was interrupted: the session saved with the new pairing is now in place, and the earlier session data, which could not be read, was moved aside for recovery.");
            break;
         case StagedPairingOutcome.Deferred:
            WriteLog("WARN", "A pairing change is waiting to finish: the session saved with the new pairing is in use but could not be moved into place yet, and will be tried again. Reason: "
               + (store.StagedPairingFailure is null ? "not recorded." : QqqSessionDiagnostics.Describe(store.StagedPairingFailure)));
            break;
         case StagedPairingOutcome.Discarded:
            WriteLog("INFO", "Discarded a session left over from a pairing change: it did not belong to the stored pairing, or the stored session already did.");
            break;
         case StagedPairingOutcome.SetAside:
            WriteLog("WARN", "A session staged for a pairing change could not be read and was moved aside for recovery.");
            break;
         default:
            break;
      }
   }



   /// <summary>Explains, for the log, why the service cannot talk to QQQ yet: no session, signed out, no pairing, or a session for a server other than the paired one.</summary>
   private static string DescribeMissingAuthorization(AgentAuthentication? saved, AgentConfiguration? configuration) => saved is null
      ? $"No saved {AppIdentity.ServerName} session: sign in from the companion to enable printing."
      : saved.Credential is null ? $"Signed out of {saved.Binding.Origin.Authority}: sign in again to enable printing; pairing and history are preserved."
      : configuration is null ? $"The saved session for {saved.Binding.Origin.Authority} names device {saved.Binding.DeviceId}, but this computer has no pairing configuration; sign in and enroll again."
      : $"The saved session is for {saved.Binding.Origin.Authority} but the pairing is for {configuration.ServerUrl}; sign in to the paired server, or start a new pairing.";



   /// <summary>
   /// Restores the protected saved session on a fresh client, binds the paired device when there is one, checks the identity
   /// against what was approved and makes the session current. The client is disposed and the failure logged on any error.
   /// </summary>
   private async Task RestoreSavedSessionAsync(AgentAuthentication saved, AgentConfiguration? configuration, AgentStore store, CancellationToken token)
   {
      QqqSessionCredential credential = saved.Credential ?? throw new InvalidOperationException("Sign in is required.");
      WriteLog("INFO", $"Restoring the saved {AppIdentity.ServerName} session for {credential.Origin.Authority} (saved identity: principal {saved.Binding.PrincipalId}, tenant {saved.Binding.TenantId}, device {saved.Binding.DeviceId ?? "(none)"}).");
      IQqqSessionClient restored = NewSession(credential.Origin);
      try
      {
         await restored.RestoreSessionAsync(credential, token).ConfigureAwait(false);
         QqqSessionBinding binding = configuration is null || !saved.Binding.HasApprovedDevice ? await restored.GetSessionIdentityAsync(token).ConfigureAwait(false)
            : await restored.BindDeviceAsync(new QqqDeviceCredential(configuration.DeviceId, configuration.DeviceToken), cancellationToken: token).ConfigureAwait(false);
         RequireSameIdentity(saved.Binding, binding);
         ActivateSession(restored, binding, store);
         WriteLog("INFO", $"Saved {AppIdentity.ServerName} session restored. " + _authenticationMessage);
      }
      catch(Exception exception)
      {
         WriteFailure($"Saved {AppIdentity.ServerName} session could not be restored", exception);
         restored.Dispose();
         throw;
      }
   }



   /// <summary>
   /// Renews the live session, re-verifies the identity and saves the new credential. When the session token rotated, the
   /// realtime socket is cancelled and a reconnect requested, because the socket was authenticated with the old token.
   /// </summary>
   private async Task ResumeAuthorizationAsync(AgentAuthentication saved, AgentStore store, CancellationToken token)
   {
      IQqqSessionClient session = _sessionClient ?? throw new InvalidOperationException("Sign in is required.");
      string previousToken = session.ExportCredential().SessionToken;
      WriteLog("INFO", "Scheduled session renewal: renewing with the server and re-checking the identity.");
      await session.ResumeSessionAsync(token).ConfigureAwait(false);
      QqqSessionBinding binding = await session.GetSessionIdentityAsync(token).ConfigureAwait(false);
      RequireSameIdentity(saved.Binding, binding);
      ActivateSession(session, binding, store);
      if(!string.Equals(previousToken, session.ExportCredential().SessionToken, StringComparison.Ordinal))
      {
         WriteLog("INFO", "The session token was rotated during renewal; the realtime socket will reconnect with the new token.");
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
      bool ready;
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
         _authenticationMessage = _authorizationReady ? $"Signed in to {AppIdentity.ServerName}." : "Signed in. Enroll this computer to enable printing.";
         ready = _authorizationReady;
         _lastAuthorizationNote = null;
      }
      WriteLog("INFO", $"{AppIdentity.ServerName} session active for {binding.Origin.Authority}: principal {binding.PrincipalId}, tenant {binding.TenantId}, device {binding.DeviceId ?? "(none)"}; "
         + (ready ? "authorized to print." : configuration is null ? "this computer is not enrolled on any server; enroll to print."
            : !SameOrigin(binding.Origin, configuration.ServerUrl) ? $"the pairing is for {configuration.ServerUrl}, so printing stays blocked until this computer is enrolled on the signed-in server."
            : $"the session device is {binding.DeviceId ?? "(none)"} but the pairing is device {configuration.DeviceId}, so printing stays blocked until the pairing is re-associated.")
         + $" Next renewal in {SessionResumeInterval.TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} min.");
   }



   /// <summary>
   /// Stops all new work after authorization is lost: clears readiness, cancels the realtime socket and tells the operator
   /// to sign in again. Pairing, history and receipts are untouched; retrying a connection cannot restore a withdrawn permission.
   /// </summary>
   private void BlockAuthorization()
   {
      bool changed;
      lock(_stateLock)
      {
         changed = !_authorizationBlocked;
         _authorizationReady = false;
         _authorizationBlocked = true;
         _controlReady = false;
         _realtimeReady = false;
         _realtimeLifetime?.Cancel();
         _authenticationMessage = $"{AppIdentity.ServerName} authorization is required. Sign in again; pairing and history are preserved.";
      }
      if(changed)
      { WriteLog("WARN", $"Printing blocked: {AppIdentity.ServerName} authorization is required. Sign in again from the companion; pairing and history are preserved."); }
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
   private void RequireSameIdentity(QqqSessionBinding expected, QqqSessionBinding actual)
   {
      if(!string.Equals(expected.IdentityKey, actual.IdentityKey, StringComparison.Ordinal))
      {
         WriteLog("WARN", $"Identity check failed: the approved identity is principal {expected.PrincipalId}, tenant {expected.TenantId}, device {expected.DeviceId ?? "(none)"} on {expected.Origin.Authority}; "
            + $"the session now reports principal {actual.PrincipalId}, tenant {actual.TenantId}, device {actual.DeviceId ?? "(none)"} on {actual.Origin.Authority}. Sign in as the approved user, or start a new pairing.");
         throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, $"The {AppIdentity.ServerName} user, tenant or device changed. Explicit connection setup is required.");
      }
   }



   /// <summary>Snapshot shown to the companion: whether a session is live, who it is, and the current readable explanation.</summary>
   private AgentAuthenticationStatus AuthenticationStatus() => new(_sessionClient?.IsAuthenticated == true,
      _sessionBinding?.PrincipalId, _sessionBinding?.TenantId, _authenticationMessage, _authorizationReady);
}
