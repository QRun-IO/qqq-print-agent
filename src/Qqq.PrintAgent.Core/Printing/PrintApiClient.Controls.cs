using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

public sealed partial class PrintApiClient
{
   private static readonly string[] ControlCapabilities = ["REFRESH_INVENTORY", "CANCEL_SPOOL"];
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

   private static bool ValidControlId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 200 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

   public async Task ControlReceiptAsync(string commandId, DeviceControlReceipt receipt, CancellationToken token)
   {
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
