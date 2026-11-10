/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Device control part of the API client: the strict validation applied to every command the realtime channel delivers,
/// and delivery of command receipts over that channel. A command's identity, action, lifetime and target are checked
/// before anything acts on it.
/// </summary>
public sealed partial class PrintApiClient
{
   /// <summary>
   /// Checks a command that arrived over the realtime channel before anything acts on it. A command that fails the rule
   /// is a <see cref="PrintingProtocolException"/>, like any other malformed frame.
   /// </summary>
   public static void ValidateRealtimeCommand(DeviceControlCommand command)
   {
      if(!ValidControlCommand(command))
      { throw new PrintingProtocolException("The realtime command frame carries an invalid device control command."); }
   }



   /// <summary>
   /// True for a command that is safe to act on: present, with safe command and request identifiers, a bounded action, a
   /// creation and expiry time that parse, a lifetime of at most 15 minutes, and a job identifier when the action cancels
   /// a spool job.
   /// </summary>
   private static bool ValidControlCommand(DeviceControlCommand? command)
      => command is not null && ValidControlId(command.CommandId) && ValidControlId(command.RequestId)
         && !string.IsNullOrEmpty(command.Action) && command.Action.Length <= 64
         && DateTimeOffset.TryParse(command.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset created)
         && DateTimeOffset.TryParse(command.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset expires)
         && expires > created && expires - created <= TimeSpan.FromMinutes(15)
         && (!string.Equals(command.Action, "CANCEL_SPOOL", StringComparison.Ordinal) || ValidControlId(command.JobId));



   /// <summary>True for a non-empty identifier of at most 200 characters made only of letters, digits and the characters - _ . :, so it cannot carry path or markup characters into later use.</summary>
   internal static bool ValidControlId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) => !string.IsNullOrEmpty(value) && value.Length <= 200 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');



   /// <summary>
   /// Delivers one command receipt over the realtime channel, with a message identity derived from the receipt and the
   /// server's acknowledgement. A receipt that is not acknowledged, or a channel that is gone, throws, so the caller
   /// keeps the receipt pending and sends it again instead of treating it as delivered.
   /// </summary>
   public async Task ControlReceiptAsync(string commandId, DeviceControlReceipt receipt, CancellationToken token)
   {
      await RealtimeChannel.SendAsync("COMMAND_RECEIPT", new { commandId, receipt }, "receipt-" + receipt.ReceiptId, acknowledged: true, token: token).ConfigureAwait(false);
   }
}
