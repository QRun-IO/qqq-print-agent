/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Device control part of the API client: polling for desired state and commands over HTTP (the compatibility path), the
/// strict validation applied to every command whatever its transport, and delivery of command receipts. Responses are
/// bounded and every command's identity, action, lifetime and target are checked before anything acts on it.
/// </summary>
public sealed partial class PrintApiClient
{
   private static readonly string[] ControlCapabilities = ["REFRESH_INVENTORY", "CANCEL_SPOOL"];



   /// <summary>Applies the same validation to a single command that arrived over the realtime channel as an HTTP poll response would get, so both transports accept exactly the same commands.</summary>
   public static void ValidateRealtimeCommand(DeviceControlCommand command)
      => ValidateControlPoll(new DeviceControlPoll(1, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), 5, new DeviceDesiredState(0, false, "CONNECTED"), [command]));



   /// <summary>A 404 means legacy only before this connection has negotiated control support.</summary>
   public async Task<DeviceControlPoll?> ControlPollAsync(long revision, CancellationToken token)
   {
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
      timeout.CancelAfter(TimeSpan.FromSeconds(20));
      using HttpResponseMessage response = await _http.PostAsJsonAsync("device/control/poll", new
      {
         controlVersion = 1,
         agentVersion = AgentVersion,
         capabilities = ControlCapabilities,
         lastAppliedRevision = revision,
      }, JsonOptions, timeout.Token).ConfigureAwait(false);
      if(response.StatusCode == HttpStatusCode.NotFound)
      {
         if(_nativeSession)
         {
            throw new InvalidDataException("The advertised native printing control endpoint is unavailable.");
         }
         return null;
      }
      response.EnsureSuccessStatusCode();
      byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
      if(bytes.Length > 65_536)
      {
         throw new InvalidDataException("Device control response exceeded its bound.");
      }
      DeviceControlPoll poll = JsonSerializer.Deserialize<DeviceControlPoll>(bytes, JsonOptions) ?? throw new InvalidDataException("Missing device control response.");
      ValidateControlPoll(poll);
      return poll;
   }



   /// <summary>
   /// Checks a control response in full: version 1, a poll interval of 1 to 300 seconds, a valid desired state and server time, at
   /// most 20 commands, and for each command a safe unique identifier, a bounded action, a lifetime of at most 15 minutes, and a
   /// job identifier when the action cancels a spool job. Anything else is rejected as an invalid response, not partly applied.
   /// </summary>
   private static void ValidateControlPoll(DeviceControlPoll poll)
   {
      if(poll.ControlVersion != 1 || poll.PollIntervalSeconds is < 1 or > 300 || poll.DesiredState is null
         || poll.DesiredState.Revision < 0 || poll.DesiredState.ConnectionState is not ("CONNECTED" or "DISCONNECTED")
         || poll.Commands is null || poll.Commands.Count > 20 || !DateTimeOffset.TryParse(poll.ServerTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
      {
         throw new InvalidDataException("Invalid device control response.");
      }
      var identifiers = new HashSet<string>(StringComparer.Ordinal);
      foreach(DeviceControlCommand command in poll.Commands)
      {
         if(command is null || !ValidControlId(command.CommandId) || !ValidControlId(command.RequestId) || !identifiers.Add(command.CommandId)
            || string.IsNullOrEmpty(command.Action) || command.Action.Length > 64
            || !DateTimeOffset.TryParse(command.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset created)
            || !DateTimeOffset.TryParse(command.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset expires)
            || expires <= created || expires - created > TimeSpan.FromMinutes(15)
            || (string.Equals(command.Action, "CANCEL_SPOOL", StringComparison.Ordinal) && !ValidControlId(command.JobId)))
         {
            throw new InvalidDataException("Invalid device control command.");
         }
      }
   }



   /// <summary>True for a non-empty identifier of at most 200 characters made only of letters, digits and the characters - _ . :, so it cannot carry path or markup characters into later use.</summary>
   private static bool ValidControlId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 200 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');



   /// <summary>
   /// Delivers one command receipt: over the realtime channel with a stable message identity and the server's acknowledgement
   /// when attached, otherwise by HTTP, where the server must answer that it accepted the receipt. A receipt the server did not
   /// accept throws, so the caller keeps it pending and sends it again instead of treating it as delivered.
   /// </summary>
   public async Task ControlReceiptAsync(string commandId, DeviceControlReceipt receipt, CancellationToken token)
   {
      if(_realtime is not null)
      {
         await RealtimeChannel.SendAsync("COMMAND_RECEIPT", new { commandId, receipt }, "receipt-" + receipt.ReceiptId, acknowledged: true, token: token).ConfigureAwait(false);
         return;
      }
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
      timeout.CancelAfter(TimeSpan.FromSeconds(20));
      using HttpResponseMessage response = await _http.PostAsJsonAsync($"device/control/commands/{Uri.EscapeDataString(commandId)}/receipt", receipt, JsonOptions, timeout.Token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
      if(!body.RootElement.TryGetProperty("accepted", out JsonElement accepted) || accepted.ValueKind != JsonValueKind.True)
      {
         throw new InvalidDataException("Control receipt was not accepted.");
      }
   }
}
