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
   /// <summary>How far an enrollment got, which decides what a failure has to report and whether the live session can still be trusted.</summary>
   private enum EnrollmentProgress
   {
      /// <summary>The server has not bound a new device to the session, and the stored pairing and session are untouched.</summary>
      NothingChanged,

      /// <summary>The server bound the new device to the live session; the stored pairing and session still describe the old one.</summary>
      DeviceBound,

      /// <summary>The new pairing is stored with its session staged beside it, which any later reader moves into place; the session is not active yet.</summary>
      Saved,

      /// <summary>The new pairing and session are stored and the session is active.</summary>
      Activated,
   }



   /// <summary>
   /// Exchanges the enrollment code for a device credential using the live user session on the selected server, binds the
   /// new device to that session and checks the identity again. Only then are the stored pairing and stored session replaced,
   /// one straight after the other, and the connection state reset, so a refused or interrupted bind leaves both files as they
   /// were. Authentication changes are serialized by a gate.
   /// </summary>
   private async Task<string> EnrollNativeAsync(PipeRequest request, CancellationToken token)
   {
      await _authenticationGate.WaitAsync(token).ConfigureAwait(false);
      EnrollmentProgress progress = EnrollmentProgress.NothingChanged;
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
         progress = EnrollmentProgress.DeviceBound;
         RequireSameIdentity(expected, actual);
         ////////////////////////////////////////////////////////////////////
         // the stored pairing and session are replaced only after the     //
         // server bound the device and the identity matched, and as one   //
         // staged step: a failure leaves either both files as they were,  //
         // or the new pairing with a session any later reader completes   //
         ////////////////////////////////////////////////////////////////////
         WriteLog("INFO", "Step 4 of 4: saving the pairing to protected storage and activating the session for printing.");
         AgentAuthentication authentication = new(actual, session.ExportCredential());
         using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
         store.StagePairing(new AgentConfiguration(request.ServerUrl, response.DeviceId, response.DeviceToken, response.PollIntervalSeconds), authentication);
         progress = EnrollmentProgress.Saved;
         store.CompleteStagedPairing();
         ResetConnectionState();
         ActivateSession(session, actual, store);
         progress = EnrollmentProgress.Activated;
         lock(_stateLock)
         {
            _queuedJobs = [];
         }
         WriteLog("INFO", $"Device enrolled with the {AppIdentity.ServerName} user session.");
         WakePoll();
         return $"Enrolled device {response.DeviceId}";
      }
      catch(Exception exception) when(progress != EnrollmentProgress.NothingChanged || exception is not InvalidOperationException)
      {
         ReportEnrollmentFailure(progress, exception);
         throw;
      }
      finally { _authenticationGate.Release(); }
   }



   /// <summary>
   /// Stops the live session being used once the server has bound it to the new device while this computer has not finished
   /// switching to it, then says what the failed enrollment left behind and how to recover. Such a session no longer matches
   /// the stored pairing or the state the runtime holds, so printing waits for the operator instead of running on a mismatch
   /// until the next renewal finds it. Blocking comes first, so a failure while logging cannot leave the session usable. In
   /// both blocked states an ordinary sign-in recovers: before the save it binds the old device again, and after it the
   /// store hands the sign-in the session that belongs to the new pairing.
   /// </summary>
   private void ReportEnrollmentFailure(EnrollmentProgress progress, Exception exception)
   {
      if(progress is EnrollmentProgress.DeviceBound or EnrollmentProgress.Saved)
      { BlockAuthorization(); }
      WriteFailure(progress switch
      {
         EnrollmentProgress.NothingChanged => "Enrollment failed; the stored pairing and session are unchanged",
         EnrollmentProgress.DeviceBound => "Enrollment failed after the server bound the new device; the stored pairing and session are unchanged, and printing is blocked until you sign in again",
         EnrollmentProgress.Saved => "Enrollment failed after the new pairing and its session were saved; printing is blocked until you sign in again or the service restarts, either of which finishes activating it",
         _ => "Enrollment completed, but a later step failed",
      }, exception);
   }



   /// <summary>Logs why an enrollment request was refused before it reached the server, and returns the exception to throw.</summary>
   private InvalidOperationException Refused(string reason, InvalidOperationException exception)
   {
      WriteLog("WARN", "Enrollment refused: " + reason);
      return exception;
   }
}
