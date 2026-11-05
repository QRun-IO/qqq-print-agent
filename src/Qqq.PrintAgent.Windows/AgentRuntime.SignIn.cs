/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;
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
      QqqSessionCredential credential = BeginSignIn(request);
      await _authenticationGate.WaitAsync(token).ConfigureAwait(false);
      IQqqSessionClient? candidate = null;
      try
      {
         candidate = NewSession(credential.Origin);
         WriteLog("INFO", "Step 1 of 4: verifying the companion's session with the server.");
         await candidate.RestoreSessionAsync(credential, token).ConfigureAwait(false);
         using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
         AgentConfiguration? configuration = store.LoadConfiguration();
         WriteLog("INFO", "Step 2 of 4: reading the session identity. Saved pairing: "
            + (configuration is null ? "none (this computer has never been enrolled)." : $"server {configuration.ServerUrl}, device {configuration.DeviceId}."));
         QqqSessionBinding binding = await candidate.GetSessionIdentityAsync(token).ConfigureAwait(false);
         if(request.StartNewPairing && binding.DeviceId is not null)
         {
            WriteLog("WARN", $"Sign-in refused: a new pairing needs a session without device proof, but the session already names device {binding.DeviceId}.");
            throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, $"New pairing requires a fresh {AppIdentity.ServerName} session without device proof.");
         }
         AgentAuthentication? saved = store.LoadAuthenticationForRecovery();
         if(!request.StartNewPairing && configuration is not null && SameOrigin(credential.Origin, configuration.ServerUrl))
         {
            WriteLog("INFO", $"Step 3 of 4: the session is for the paired server; binding device {configuration.DeviceId}{(request.ClaimLegacyDevice ? " and claiming it as a legacy device" : "")}.");
            binding = await candidate.BindDeviceAsync(new QqqDeviceCredential(configuration.DeviceId, configuration.DeviceToken), request.ClaimLegacyDevice, token).ConfigureAwait(false);
            if(saved?.Binding.DeviceId is not null && SameOrigin(saved.Binding.Origin, configuration.ServerUrl))
            {
               WriteLog("INFO", "Comparing the new identity with the previously approved identity.");
               RequireSameIdentity(saved.Binding, binding);
            }
            store.SaveControlState(store.LoadControlState() with { AuthorizationRequired = false });
         }
         else
         {
            WriteLog("INFO", "Step 3 of 4: no device binding. "
               + (request.StartNewPairing ? "A new pairing was requested; enroll this computer next."
                  : configuration is null ? "This computer is not enrolled; enroll it next to enable printing."
                  : $"The session is for {credential.Origin.Authority} but the pairing is for {configuration.ServerUrl}; enroll on this server, or sign in to the paired one."));
         }
         IQqqSessionClient? previous = _sessionClient;
         ResetConnectionState();
         WriteLog("INFO", "Step 4 of 4: making the verified session current and saving it to protected storage.");
         ActivateSession(candidate, binding, store);
         previous?.Dispose();
         WakePoll();
         WriteLog("INFO", "Sign-in complete. " + _authenticationMessage);
         return SignInReply();
      }
      catch(Exception exception)
      {
         WriteFailure("Sign-in failed; the previous session, pairing and history are unchanged", exception);
         candidate?.Dispose();
         throw;
      }
      finally { _authenticationGate.Release(); }
   }



   /// <summary>
   /// The one-line result the companion shows after a successful sign-in. It always starts with "Signed in to {server}." (the
   /// companion's success marker) and then says whether printing is enabled or what still has to happen.
   /// </summary>
   private string SignInReply()
   {
      string prefix = $"Signed in to {AppIdentity.ServerName}.";
      const string signedIn = "Signed in.";
      return _authorizationReady ? prefix + " Printing is enabled."
         : _authenticationMessage.StartsWith(signedIn, StringComparison.Ordinal) ? prefix + _authenticationMessage[signedIn.Length..]
         : prefix + " " + _authenticationMessage;
   }



   /// <summary>Checks the request shape before any network work (a credential is present and the pairing choice is not contradictory) and logs what was asked.</summary>
   private QqqSessionCredential BeginSignIn(PipeRequest request)
   {
      QqqSessionCredential credential = request.SessionCredential ?? throw new InvalidOperationException($"A {AppIdentity.ServerName} sign-in session is required.");
      if(request.StartNewPairing && request.ClaimLegacyDevice)
      {
         WriteLog("WARN", "Sign-in refused: the companion asked for both a new pairing and association with the existing pairing.");
         throw new InvalidOperationException("Choose either a new pairing or association with the existing pairing.");
      }
      WriteLog("INFO", $"Sign-in requested for {credential.Origin.Authority}; pairing choice: "
         + (request.StartNewPairing ? "start a new pairing." : request.ClaimLegacyDevice ? "associate with the existing pairing (claim legacy device)." : "keep the current pairing."));
      return credential;
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
         WriteLog("INFO", "Sign-out requested: blocking printing, removing the saved session credential and ending the server session. Pairing and history are kept.");
         BlockAuthorization();
         using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
         AgentAuthentication? saved = store.LoadAuthenticationForRecovery();
         bool unreadableSession = store.QuarantineUnreadableAuthentication();
         if(unreadableSession)
         { WriteLog("WARN", $"The saved session data could not be read and was moved aside for recovery; revoke the previous session in {AppIdentity.ServerName}."); }
         if(saved is not null)
         {
            store.SaveAuthentication(new AgentAuthentication(saved.Binding, null));
         }
         try
         {
            if(_sessionClient is null && saved?.Credential is not null)
            {
               WriteLog("INFO", $"No live session is held; restoring the saved one for {saved.Credential.Origin.Authority} so the server can end it.");
               _sessionClient = NewSession(saved.Credential.Origin);
               await _sessionClient.RestoreSessionAsync(saved.Credential, token).ConfigureAwait(false);
            }
            if(_sessionClient is not null)
            {
               await _sessionClient.SignOutAsync(token).ConfigureAwait(false);
            }
            else
            { WriteLog("INFO", "No session to end on the server; local state cleared."); }
         }
         catch(Exception exception)
         {
            WriteFailure("The server session could not be ended; local sign-out still completes", exception);
            throw;
         }
         finally
         {
            _sessionClient?.Dispose();
            _sessionClient = null;
            ResetConnectionState();
            WriteLog("INFO", "Signed out locally. Device pairing and print history were preserved.");
         }
         return unreadableSession
            ? $"Signed out locally. Unreadable session data was preserved for recovery; revoke the previous session in {AppIdentity.ServerName}. Device pairing and print history were preserved."
            : "Signed out. Device pairing and print history were preserved.";
      }
      finally { _authenticationGate.Release(); }
   }
}
