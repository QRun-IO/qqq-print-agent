/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Resolves the command line's <c>--server</c> value to one server. The service takes a full identifier only, so this
/// runs in the client, on the list <see cref="ManagementOperations.ServersList"/> returned, and is pure. The value always
/// selects a server and never names an address.
/// </summary>
public static class ServerSelector
{
   /// <summary>The fewest characters of an identifier that select by prefix. Fewer would make a short hexadecimal name and a prefix too easy to confuse.</summary>
   public const int MinimumPrefixLength = 8;



   /// <summary>
   /// Selects a server. No value selects the only server and is refused when there are none or several. A full
   /// identifier selects that server outright. Otherwise the value may be a server's name or the first eight or more
   /// characters of an identifier, compared without regard to letter case: if that is one server it is selected, and if
   /// it is several the selection is ambiguous and their identifiers are returned in the list's order. A value shorter
   /// than eight characters that only begins an identifier is refused as too short, with the identifiers it begins.
   /// </summary>
   public static ServerSelection Resolve(string? selector, IReadOnlyList<ServerSummary> servers)
   {
      ArgumentNullException.ThrowIfNull(servers);
      if(servers.Count == 0)
      {
         return new ServerSelection(ServerSelectionOutcome.NoServers, null, []);
      }
      string value = selector?.Trim() ?? "";
      if(value.Length == 0)
      {
         return servers.Count == 1 ? Selected(servers[0]) : new ServerSelection(ServerSelectionOutcome.SelectorRequired, null, Identifiers(servers));
      }
      if(servers.FirstOrDefault(server => string.Equals(server.ServerId, value, StringComparison.OrdinalIgnoreCase)) is { } exact)
      {
         return Selected(exact);
      }
      ServerSummary[] beginning = [.. servers.Where(server => server.ServerId.StartsWith(value, StringComparison.OrdinalIgnoreCase))];
      ServerSummary[] matches = [.. servers.Where(server => string.Equals(server.Name, value, StringComparison.OrdinalIgnoreCase)
         || (value.Length >= MinimumPrefixLength && beginning.Contains(server)))];
      return matches.Length switch
      {
         1 => Selected(matches[0]),
         > 1 => new ServerSelection(ServerSelectionOutcome.Ambiguous, null, Identifiers(matches)),
         _ => beginning.Length > 0 ? new ServerSelection(ServerSelectionOutcome.PrefixTooShort, null, Identifiers(beginning))
            : new ServerSelection(ServerSelectionOutcome.NotFound, null, []),
      };
   }



   /// <summary>A selection of one server.</summary>
   private static ServerSelection Selected(ServerSummary server) => new(ServerSelectionOutcome.Selected, server.ServerId, [server.ServerId]);



   /// <summary>The identifiers of some servers, in the order given.</summary>
   private static string[] Identifiers(IEnumerable<ServerSummary> servers) => [.. servers.Select(server => server.ServerId)];
}
