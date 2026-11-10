/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Enrollment part of the runtime. It pairs this computer with the signed-in QQQ user, so the device credential is only ever issued to
/// and stored for the session that asked for it.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>
   /// Exchanges the enrollment code for a device credential using the live user session on the selected server, binds the
   /// new device to that session and checks the identity again. Only then is the stored pairing replaced and the connection
   /// state reset, so a refused or interrupted bind leaves the previous pairing and session exactly as they were.
   /// Authentication changes are serialized by a gate.
   /// </summary>
   private async Task<string> EnrollNativeAsync(PipeRequest request, CancellationToken token)
   {
      await _authenticationGate.WaitAsync(token).ConfigureAwait(false);
      bool pairingSaved = false;
      try
      {
         WriteLog("INFO", $"Enrollment requested on {request.ServerUrl ?? "(no server)"} with a {(request.EnrollmentCode ?? "").Length}-character enrollment code (never logged), computer name {Environment.MachineName}, agent {PrintApiClient.AgentVersion}.");
         IQqqSessionClient session = _sessionClient ?? throw Refused($"no {AppIdentity.ServerName} session is held; sign in first.", new InvalidOperationException($"Sign in to {AppIdentity.ServerName} before enrolling this computer."));
         if(!session.IsAuthenticated || request.ServerUrl is null || !SameOrigin(session.Origin, request.ServerUrl))
         {
            throw Refused(!session.IsAuthenticated ? "the held session is not authenticated." : request.ServerUrl is null ? "no server address was given."
               : $"the session is for {session.Origin.Authority} but enrollment was requested on {request.ServerUrl}.", new InvalidOperationException($"Sign in to the selected {AppIdentity.ServerName} server before enrollment."));
         }
         WriteLog("INFO", "Step 1 of 4: reading the signed-in identity.");
         QqqSessionBinding identity = await session.GetSessionIdentityAsync(token).ConfigureAwait(false);
         using var api = PrintApiClient.ForSession(session);
         WriteLog("INFO", "Step 2 of 4: exchanging the enrollment code for a device credential.");
         EnrollmentResponse response = await api.EnrollAsync(new EnrollmentRequest(request.EnrollmentCode ?? "", Environment.MachineName, PrintApiClient.AgentVersion), token).ConfigureAwait(false);
         WriteLog("INFO", $"Enrollment accepted: device {response.DeviceId}, poll interval {response.PollIntervalSeconds} s.");
         QqqSessionBinding expected = identity with { DeviceId = response.DeviceId };
         WriteLog("INFO", "Step 3 of 4: binding the new device to the session and confirming the identity.");
         QqqSessionBinding actual = await session.BindDeviceAsync(new QqqDeviceCredential(response.DeviceId, response.DeviceToken), cancellationToken: token).ConfigureAwait(false);
         RequireSameIdentity(expected, actual);
         ////////////////////////////////////////////////////////////////////////////
         // the stored pairing is replaced only after the server bound the device //
         // and the identity matched: until here a failure costs the operator one //
         // enrollment code, never the pairing that was already working           //
         ////////////////////////////////////////////////////////////////////////////
         WriteLog("INFO", "Step 4 of 4: saving the pairing to protected storage and activating the session for printing.");
         using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
         store.SaveConfiguration(new AgentConfiguration(request.ServerUrl, response.DeviceId, response.DeviceToken, response.PollIntervalSeconds));
         pairingSaved = true;
         ResetConnectionState();
         ActivateSession(session, actual, store);
         lock(_stateLock)
         {
            _queuedJobs = [];
         }
         WriteLog("INFO", $"Device enrolled with the {AppIdentity.ServerName} user session.");
         WakePoll();
         return $"Enrolled device {response.DeviceId}";
      }
      catch(Exception exception) when(exception is not InvalidOperationException)
      {
         WriteFailure(pairingSaved ? "Enrollment failed after the new pairing was saved; sign in again to finish activating it"
            : "Enrollment failed; any existing pairing is unchanged", exception);
         throw;
      }
      finally { _authenticationGate.Release(); }
   }



   /// <summary>Logs why an enrollment request was refused before it reached the server, and returns the exception to throw.</summary>
   private InvalidOperationException Refused(string reason, InvalidOperationException exception)
   {
      WriteLog("WARN", "Enrollment refused: " + reason);
      return exception;
   }
}
