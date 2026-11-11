/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// Puts a snapshot that has just arrived together with what the window already shows. A snapshot says what was true
/// when its request was sent. When a server has been changed from this window and the snapshot was not sent after
/// that change was answered, the snapshot's entry for that server may be older than the change and is not taken:
/// the server keeps the entry the window has, until a snapshot that was sent after the change arrives. Every other
/// server's entry is taken. The same holds for the computer's own part and for the order of the list, which a
/// change under <see cref="OperationGate.Computer"/> (the computer's details, a reorder) makes older than the
/// window.
/// </summary>
internal static class SnapshotMerge
{
   /// <summary>
   /// The servers of a list that the window can show: those whose identifier is one, each once, in the list's order.
   /// The contract promises exactly that of a snapshot; an entry that breaks the promise is left out here, so one
   /// bad entry cannot take the window down or stand in for another server.
   /// </summary>
   internal static IReadOnlyList<ServerSnapshot> Usable(IReadOnlyList<ServerSnapshot> servers)
      => [.. servers.Where(server => ManagementProtocol.IsServerId(server.ServerId)).DistinctBy(server => server.ServerId, StringComparer.Ordinal)];



   /// <summary>
   /// The snapshot to show: the new one, without its entry for every server (and for the computer) that
   /// <paramref name="changedSince"/> says was changed locally and not before the new one was sent. Such a server
   /// keeps the entry the window has; when the window has none for it yet, it is left out, and is shown from the
   /// first snapshot that was sent after its change. A server that changed and is missing from the new list stays,
   /// at the place it had, so a snapshot that is not newer than a server's change never makes that server go. When
   /// the computer changed, the servers stay in the order the window has, with any new ones after them.
   /// <paramref name="heldBack"/> names what was not taken from the new snapshot: the identifier of every such
   /// server, and <see cref="OperationGate.Computer"/> when the computer's part and the order are the window's.
   /// </summary>
   internal static ServiceSnapshot Apply(ServiceSnapshot? previous, ServiceSnapshot fresh, Func<string, bool> changedSince, out IReadOnlyList<string> heldBack)
   {
      IReadOnlyList<ServerSnapshot> shown = previous?.Servers ?? [];
      List<ServerSnapshot> merged = [];
      List<string> held = [];
      foreach(ServerSnapshot server in Usable(fresh.Servers))
      {
         if(!changedSince(server.ServerId))
         {
            merged.Add(server);
            continue;
         }
         held.Add(server.ServerId);
         if(Find(shown, server.ServerId) is { } kept)
         {
            merged.Add(kept);
         }
      }
      for(int index = 0; index < shown.Count; index++)
      {
         ServerSnapshot before = shown[index];
         if(changedSince(before.ServerId) && !held.Contains(before.ServerId, StringComparer.Ordinal))
         {
            merged.Insert(Math.Min(index, merged.Count), before);
            held.Add(before.ServerId);
         }
      }
      heldBack = held;
      if(previous is null || !changedSince(OperationGate.Computer))
      {
         return new ServiceSnapshot(fresh.Computer, merged);
      }
      held.Add(OperationGate.Computer);
      return new ServiceSnapshot(previous.Computer, [.. merged.OrderBy(server => Place(shown, server.ServerId))]);
   }



   /// <summary>A server's entry in a list, or null when the list has none for it.</summary>
   private static ServerSnapshot? Find(IReadOnlyList<ServerSnapshot> servers, string serverId)
      => servers.FirstOrDefault(server => string.Equals(server.ServerId, serverId, StringComparison.Ordinal));



   /// <summary>A server's place in a list, or a place after every server of the list for one it does not have.</summary>
   private static int Place(IReadOnlyList<ServerSnapshot> servers, string serverId)
   {
      for(int index = 0; index < servers.Count; index++)
      {
         if(string.Equals(servers[index].ServerId, serverId, StringComparison.Ordinal))
         {
            return index;
         }
      }
      return servers.Count;
   }
}
