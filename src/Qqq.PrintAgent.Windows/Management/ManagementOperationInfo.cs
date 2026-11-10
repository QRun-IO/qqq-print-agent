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
}
