/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// Puts a snapshot that has just arrived together with what the window already shows. A snapshot says what was true
/// when it was asked for. When a server has been changed from this window since then, the snapshot's entry for that
/// server is older than the change and is not taken: the server keeps the entry the window has, until a snapshot
/// that was asked for after the change arrives. Every other server's entry is taken. The same holds for the
/// computer's own part and for the order of the list, which a change under <see cref="OperationGate.Computer"/>
/// (the computer's details, a reorder) makes older than the window.
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
   /// The snapshot to show: the new one, with the entry the window has kept for every server (and for the computer)
   /// that <paramref name="changedSince"/> says has been changed locally since the new one was asked for. A server
   /// that changed and is missing from the new list stays, at the place it had; when the computer changed, the
   /// servers stay in the order the window has, with any new ones after them.
   /// </summary>
   internal static ServiceSnapshot Apply(ServiceSnapshot? previous, ServiceSnapshot fresh, Func<string, bool> changedSince)
   {
      IReadOnlyList<ServerSnapshot> usable = Usable(fresh.Servers);
      if(previous is null)
      {
         return new ServiceSnapshot(fresh.Computer, usable);
      }
      List<ServerSnapshot> merged = [.. usable.Select(server => changedSince(server.ServerId) && Find(previous, server.ServerId) is { } kept ? kept : server)];
      for(int index = 0; index < previous.Servers.Count; index++)
      {
         ServerSnapshot before = previous.Servers[index];
         if(changedSince(before.ServerId) && !merged.Exists(server => string.Equals(server.ServerId, before.ServerId, StringComparison.Ordinal)))
         {
            merged.Insert(Math.Min(index, merged.Count), before);
         }
      }
      if(!changedSince(OperationGate.Computer))
      {
         return new ServiceSnapshot(fresh.Computer, merged);
      }
      return new ServiceSnapshot(previous.Computer, [.. merged.OrderBy(server => Place(previous, server.ServerId))]);
   }



   /// <summary>A server's entry in a snapshot, or null when the snapshot has none for it.</summary>
   private static ServerSnapshot? Find(ServiceSnapshot snapshot, string serverId)
      => snapshot.Servers.FirstOrDefault(server => string.Equals(server.ServerId, serverId, StringComparison.Ordinal));



   /// <summary>A server's place in a snapshot's list, or a place after every server of the list for one it does not have.</summary>
   private static int Place(ServiceSnapshot snapshot, string serverId)
   {
      for(int index = 0; index < snapshot.Servers.Count; index++)
      {
         if(string.Equals(snapshot.Servers[index].ServerId, serverId, StringComparison.Ordinal))
         {
            return index;
         }
      }
      return snapshot.Servers.Count;
   }
}
