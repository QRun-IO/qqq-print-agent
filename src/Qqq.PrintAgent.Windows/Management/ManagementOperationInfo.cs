/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Everything the pipe needs to know about one operation before running it: its name on the wire, whether it names a
/// server, how it behaves beside others, how long the service lets it run, whether it still answers while the stored
/// data is not ready (written by a newer build, or an upgrade that could not finish), and the records its arguments
/// and its result are read into. The service and its clients both read the time limit from here, so they cannot drift
/// apart the way two hard-coded lists of operation names could.
/// </summary>
public sealed record ManagementOperationInfo(string Name, ManagementScope Scope, ManagementOperationKind Kind, TimeSpan ServerTimeLimit, bool AvailableWhenDegraded,
   Type? ArgumentsType, Type ResultType)
{
   /// <summary>
   /// How long a client waits for the reply: the service's limit and a margin for the reply to arrive, so the client
   /// hears the service's own timeout instead of giving up first. The margin is 5 seconds for an ordinary operation and
   /// 30 for a long one, which gives the 35 and 150 seconds version 1 clients use.
   /// </summary>
   public TimeSpan ClientTimeLimit() => ServerTimeLimit + (ServerTimeLimit > ManagementOperations.OrdinaryTimeLimit ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5));



   /// <summary>
   /// Whether the operation is still answered for a server that is being removed. Every read is. So is a repeated
   /// <see cref="ManagementOperations.ServersRemove"/>, which succeeds with <see cref="ServerRemoveResult.Removing"/>
   /// again: a client that retried after a timeout is told what is true instead of being refused. Every other change
   /// to that server is refused with <see cref="ManagementErrorCode.ServerRemoving"/>. An operation on the computer
   /// names no server, so the question does not arise and it is answered.
   /// </summary>
   public bool AnsweredWhileRemoving()
      => Scope == ManagementScope.Computer || Kind == ManagementOperationKind.Read || string.Equals(Name, ManagementOperations.ServersRemove, StringComparison.Ordinal);
}
