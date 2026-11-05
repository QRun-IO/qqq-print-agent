/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Enrollment part of the runtime. It pairs this computer with the signed-in QQQ user, so the device credential is only ever issued to
/// and stored for the session that asked for it.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>Exchanges the enrollment code for a device credential using the live user session on the selected server. Authentication changes are serialized by a gate, the identity is checked again after binding, and the old connection state is reset first so nothing from the previous pairing survives.</summary>
   private async Task<string> EnrollNativeAsync(PipeRequest request, CancellationToken token)
   {
      await _authenticationGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         WriteLog("INFO", $"Enrollment requested on {request.ServerUrl ?? "(no server)"} with a {(request.EnrollmentCode ?? "").Length}-character enrollment code (never logged), computer name {Environment.MachineName}, agent {PrintApiClient.AgentVersion}.");
         IQqqSessionClient session = _sessionClient ?? throw Refused("no QQQ session is held; sign in first.", new InvalidOperationException("Sign in to QQQ before enrolling this computer."));
         if(!session.IsAuthenticated || request.ServerUrl is null || !SameOrigin(session.Origin, request.ServerUrl))
         {
            throw Refused(!session.IsAuthenticated ? "the held session is not authenticated." : request.ServerUrl is null ? "no server address was given."
               : $"the session is for {session.Origin.Authority} but enrollment was requested on {request.ServerUrl}.", new InvalidOperationException("Sign in to the selected QQQ server before enrollment."));
         }
         WriteLog("INFO", "Step 1 of 4: reading the signed-in identity.");
         QqqSessionBinding identity = await session.GetSessionIdentityAsync(token).ConfigureAwait(false);
         using var api = PrintApiClient.ForSession(session);
         WriteLog("INFO", "Step 2 of 4: exchanging the enrollment code for a device credential.");
         EnrollmentResponse response = await api.EnrollAsync(new EnrollmentRequest(request.EnrollmentCode ?? "", Environment.MachineName, PrintApiClient.AgentVersion), token).ConfigureAwait(false);
         WriteLog("INFO", $"Enrollment accepted: device {response.DeviceId}, poll interval {response.PollIntervalSeconds} s. Saving the pairing to protected storage.");
         using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
         store.SaveConfiguration(new AgentConfiguration(request.ServerUrl, response.DeviceId, response.DeviceToken, response.PollIntervalSeconds));
         QqqSessionBinding expected = identity with { DeviceId = response.DeviceId };
         store.SaveAuthentication(new AgentAuthentication(expected, session.ExportCredential()));
         ResetConnectionState();
         WriteLog("INFO", "Step 3 of 4: binding the new device to the session and confirming the identity.");
         QqqSessionBinding actual = await session.BindDeviceAsync(new QqqDeviceCredential(response.DeviceId, response.DeviceToken), cancellationToken: token).ConfigureAwait(false);
         RequireSameIdentity(expected, actual);
         WriteLog("INFO", "Step 4 of 4: activating the session for printing.");
         ActivateSession(session, actual, store);
         lock(_stateLock)
         {
            _queuedJobs = [];
         }
         WriteLog("INFO", "Device enrolled with the QQQ user session.");
         WakePoll();
         return $"Enrolled device {response.DeviceId}";
      }
      catch(Exception exception) when(exception is not InvalidOperationException)
      {
         WriteFailure("Enrollment failed; any existing pairing is unchanged", exception);
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
