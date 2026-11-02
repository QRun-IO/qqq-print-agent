/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Remote command part of the runtime: executing the server's commands (refresh printers, cancel one spooler job) at most once each. The
/// intent is committed before any effect and the receipt before delivery, so a crash or lost acknowledgement is answered with the
/// recorded result or an honest "outcome unknown", never by repeating a physical action.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>
   /// Applies one command. An expired command is answered as expired, and a refresh is skipped while traffic is not allowed. The
   /// execution intent is committed first; if a receipt already exists it is simply redelivered, and a command whose intent was
   /// recorded without a receipt is reported as outcome unknown instead of being run again. The receipt message is redacted and
   /// bounded, and the last command is recorded in the control state.
   /// </summary>
   private async Task ApplyControlCommandAsync(AgentStore store, PrintApiClient api, DeviceControlCommand command, DateTimeOffset serverTime, CancellationToken token)
   {
      bool expired = DateTimeOffset.Parse(command.ExpiresAt, CultureInfo.InvariantCulture) <= serverTime;
      if(!expired && string.Equals(command.Action, "REFRESH_INVENTORY", StringComparison.Ordinal) && !NormalTrafficAllowed)
      {
         return;
      }
      bool first = store.BeginControlCommand(command);
      DeviceControlReceipt? existing = store.GetControlReceipt(command.CommandId);
      if(existing is not null)
      {
         // Replay the exact receipt, even if its previous acknowledgement was lost.
         await DeliverControlReceiptAsync(store, api, command.CommandId, existing, token).ConfigureAwait(false);
         return;
      }
      (string outcome, string message) = !first
         ? ("OUTCOME_UNKNOWN", "The agent stopped after recording execution intent. No command was automatically repeated.")
         : expired ? ("EXPIRED", "The command expired before execution.")
         : await ExecuteControlCommandAsync(store, api, command, token).ConfigureAwait(false);
      message = AgentActivityLog.Redact(message);
      DeviceControlReceipt receipt = new(Guid.NewGuid().ToString("N"), outcome, Now(), message.Length <= 1024 ? message : message[..1024]);
      store.SaveControlReceipt(command.CommandId, receipt);
      AgentControlState state;
      lock(_stateLock)
      { state = _controlState with { LastCommand = command.Action, LastOutcome = outcome, LastCommandAt = receipt.CompletedAt }; }
      SetControlState(store, state);
   }



   /// <summary>
   /// Carries out a command and turns the result into an outcome code and message. A cancelled, timed-out or lost-result execution is
   /// always reported as outcome unknown with advice to inspect the printer, and a known failure as failed, so no failure path suggests
   /// an action did or did not happen when that is not known.
   /// </summary>
   private async Task<(string Outcome, string Message)> ExecuteControlCommandAsync(AgentStore store, PrintApiClient api, DeviceControlCommand command, CancellationToken token)
   {
      try
      {
         switch(command.Action)
         {
            case "REFRESH_INVENTORY":
               ExposedPrinterBackend exposed = new(new ProfiledPrinterBackend(_printers, store), store.GetExposedPrinterIds);
               await api.InventoryAsync(exposed.Discover(), token).ConfigureAwait(false);
               return ("INVENTORY_REFRESHED", "The current exposed printer inventory was synchronized.");
            case "CANCEL_SPOOL":
               return await CancelRemoteSpoolAsync(store, command.JobId!, token).ConfigureAwait(false);
            default:
               return ("UNSUPPORTED", "This agent does not implement the requested control action.");
         }
      }
      catch(SpoolCancellationException exception)
      {
         return (exception.Outcome, exception.Message);
      }
      catch(OperationCanceledException)
      {
         return ("OUTCOME_UNKNOWN", "Execution was interrupted. No command will be automatically repeated; inspect the printer.");
      }
      catch(TimeoutException)
      {
         return ("OUTCOME_UNKNOWN", "The printer did not respond before the deadline. No command will be automatically repeated; inspect its output.");
      }
      catch(IOException) when(string.Equals(command.Action, "CANCEL_SPOOL", StringComparison.Ordinal))
      {
         return ("OUTCOME_UNKNOWN", "The isolated spooler result was lost. Cancellation may have occurred; the request will not be repeated.");
      }
      catch(Exception exception) when(exception is IOException or InvalidOperationException or HttpRequestException or System.ComponentModel.Win32Exception)
      {
         return ("FAILED", "The requested action could not be confirmed. Inspect local status; pages already printed cannot be recalled.");
      }
   }



   /// <summary>
   /// Cancels one job on the server's request, but only when it can be done safely: a job still downloading is cancelled before it
   /// reaches the printer; a spooled job is cancelled by its exact recorded spooler receipt on its recorded printer; every other state
   /// (no receipt, already completed, unknown, unsupported backend, missing printer) is refused with a specific outcome and nothing is changed.
   /// </summary>
   private async Task<(string Outcome, string Message)> CancelRemoteSpoolAsync(AgentStore store, string jobId, CancellationToken token)
   {
      JobSnapshot? job = store.GetJobDetails(jobId)?.Job;
      lock(_stateLock)
      {
         if(string.Equals(job?.Status, "RECEIVED", StringComparison.Ordinal) && !_submittingJobs.Contains(jobId) && _activeTransfers.TryGetValue(jobId, out CancellationTokenSource? download))
         {
            download.Cancel();
            return ("CANCEL_REQUESTED", "The active download was canceled before native spool submission.");
         }
      }
      if(job is null || job.PrinterId is null || job.SpoolerJobId is null)
      {
         return ("OWNERSHIP_MISMATCH", "No exact committed spooler receipt exists for this job in the current connection. No Windows job was changed.");
      }
      if(string.Equals(job.Status, "SPOOLER_COMPLETED", StringComparison.Ordinal))
      {
         return ("ALREADY_ABSENT", "This job was already reported completed; its printed pages cannot be recalled.");
      }
      if(!string.Equals(job.Status, "SPOOLED", StringComparison.Ordinal))
      {
         return ("OUTCOME_UNKNOWN", "The job does not have a safely cancelable spooler state. Inspect its output.");
      }
      if(_printers is not IPrinterJobControl control)
      {
         return ("UNSUPPORTED", "This printer backend does not support verified spool cancellation.");
      }
      PrinterInfo? printer = _printers.Discover().SingleOrDefault(value => string.Equals(value.PrinterId, job.PrinterId, StringComparison.Ordinal));
      if(printer is null)
      {
         return ("FAILED", "The recorded printer is unavailable. No other printer queue was changed.");
      }
      await control.CancelAsync(printer.Name, job.JobId, job.SpoolerJobId, token).ConfigureAwait(false);
      return ("CANCEL_REQUESTED", "Windows accepted cancellation of the verified job. Pages already sent or printed cannot be recalled.");
   }
}
