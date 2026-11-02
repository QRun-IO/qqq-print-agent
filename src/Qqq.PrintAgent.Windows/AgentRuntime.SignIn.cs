/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Sign-in and sign-out part of the runtime. The companion signs the user in and hands over only the session credential; the
/// service verifies the identity (and, for an existing pairing, the device), then swaps sessions inside a connection transition so
/// printing never runs half-way between identities. Pairing and job history survive both operations.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>Runs sign-in as a connection transition, so it cannot overlap a claim or a printing job.</summary>
   internal async Task<string> SignInAsync(PipeRequest request, CancellationToken token)
      => await ConnectionTransitionAsync(() => SignInCoreAsync(request, token), token).ConfigureAwait(false);



   /// <summary>
   /// Verifies and adopts a session from the companion. The credential is restored on a fresh candidate client and its identity read.
   /// A new pairing needs a session without device proof; an existing pairing is bound (optionally claiming a legacy device) and must
   /// have the same identity as before, and any authorization demand is cleared. Only then is the candidate made current and the old
   /// session disposed; on any failure the candidate is discarded and the previous session is left as it was.
   /// </summary>
   private async Task<string> SignInCoreAsync(PipeRequest request, CancellationToken token)
   {
      QqqSessionCredential credential = request.SessionCredential ?? throw new InvalidOperationException("A QQQ sign-in session is required.");
      if(request.StartNewPairing && request.ClaimLegacyDevice)
      {
         throw new InvalidOperationException("Choose either a new pairing or association with the existing pairing.");
      }
      await _authenticationGate.WaitAsync(token).ConfigureAwait(false);
      IQqqSessionClient? candidate = null;
      try
      {
         candidate = NewSession(credential.Origin);
         await candidate.RestoreSessionAsync(credential, token).ConfigureAwait(false);
         using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
         AgentConfiguration? configuration = store.LoadConfiguration();
         QqqSessionBinding binding = await candidate.GetSessionIdentityAsync(token).ConfigureAwait(false);
         if(request.StartNewPairing && binding.DeviceId is not null)
         {
            throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, "New pairing requires a fresh QQQ session without device proof.");
         }
         AgentAuthentication? saved = store.LoadAuthenticationForRecovery();
         if(!request.StartNewPairing && configuration is not null && SameOrigin(credential.Origin, configuration.ServerUrl))
         {
            binding = await candidate.BindDeviceAsync(new QqqDeviceCredential(configuration.DeviceId, configuration.DeviceToken), request.ClaimLegacyDevice, token).ConfigureAwait(false);
            if(saved?.Binding.DeviceId is not null && SameOrigin(saved.Binding.Origin, configuration.ServerUrl))
            {
               RequireSameIdentity(saved.Binding, binding);
            }
            store.SaveControlState(store.LoadControlState() with { AuthorizationRequired = false });
         }
         IQqqSessionClient? previous = _sessionClient;
         ResetConnectionState();
         ActivateSession(candidate, binding, store);
         previous?.Dispose();
         WakePoll();
         return "Signed in to QQQ. " + _authenticationMessage;
      }
      catch
      {
         candidate?.Dispose();
         throw;
      }
      finally { _authenticationGate.Release(); }
   }



   /// <summary>Runs sign-out as a connection transition, so it cannot overlap a claim or a printing job.</summary>
   internal async Task<string> SignOutAsync(CancellationToken token)
      => await ConnectionTransitionAsync(() => SignOutCoreAsync(token), token).ConfigureAwait(false);



   /// <summary>
   /// Blocks printing first, then removes the saved session credential while keeping the identity, so pairing and history stay and a later
   /// sign-in is checked against the same user. An unreadable saved session is moved aside, not deleted. The server session is ended when
   /// it can be, and the local client and connection state are always cleared, even if ending it on the server fails.
   /// </summary>
   private async Task<string> SignOutCoreAsync(CancellationToken token)
   {
      await _authenticationGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         BlockAuthorization();
         using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
         AgentAuthentication? saved = store.LoadAuthenticationForRecovery();
         bool unreadableSession = store.QuarantineUnreadableAuthentication();
         if(saved is not null)
         {
            store.SaveAuthentication(new AgentAuthentication(saved.Binding, null));
         }
         try
         {
            if(_sessionClient is null && saved?.Credential is not null)
            {
               _sessionClient = NewSession(saved.Credential.Origin);
               await _sessionClient.RestoreSessionAsync(saved.Credential, token).ConfigureAwait(false);
            }
            if(_sessionClient is not null)
            {
               await _sessionClient.SignOutAsync(token).ConfigureAwait(false);
            }
         }
         finally
         {
            _sessionClient?.Dispose();
            _sessionClient = null;
            ResetConnectionState();
         }
         return unreadableSession
            ? "Signed out locally. Unreadable session data was preserved for recovery; revoke the previous session in QQQ. Device pairing and print history were preserved."
            : "Signed out. Device pairing and print history were preserved.";
      }
      finally { _authenticationGate.Release(); }
   }
}
