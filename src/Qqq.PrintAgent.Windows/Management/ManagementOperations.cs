/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The operations of version 2 of the management pipe: their names as on the wire, and for each what the pipe must know
/// to route it (<see cref="ManagementOperationInfo"/>). The two lists are those of ADR-0004. This table is the single
/// definition; nothing else compares operation names.
/// </summary>
public static class ManagementOperations
{
   /// <summary>The whole aggregated state: the computer once and every server once. No jobs and no log lines.</summary>
   public const string Snapshot = "snapshot";

   /// <summary>The servers in the operator's order, with what is needed to name and select one.</summary>
   public const string ServersList = "servers.list";

   /// <summary>Runs the compatibility check for an address in the service, under the service's own identity and certificate store.</summary>
   public const string ServersCheck = "servers.check";

   /// <summary>Stores a new server together with its certificate exception and its session, or nothing.</summary>
   public const string ServersAdd = "servers.add";

   /// <summary>Puts the servers in a new order.</summary>
   public const string ServersReorder = "servers.reorder";

   /// <summary>Reads this computer's name, description and location.</summary>
   public const string ComputerGetProfile = "computer.getProfile";

   /// <summary>Changes this computer's name, description and location.</summary>
   public const string ComputerSetProfile = "computer.setProfile";

   /// <summary>Reads the log limits and the current log file's size.</summary>
   public const string LogsGetSettings = "logs.getSettings";

   /// <summary>Changes the log limits.</summary>
   public const string LogsSetSettings = "logs.setSettings";

   /// <summary>Deletes the activity log files.</summary>
   public const string LogsClear = "logs.clear";

   /// <summary>Pauses claiming for an update and says whether work in hand has finished.</summary>
   public const string UpdatePrepare = "update.prepare";

   /// <summary>Lifts the update pause.</summary>
   public const string UpdateResume = "update.resume";

   /// <summary>Changes a server's name.</summary>
   public const string ServersRename = "servers.rename";

   /// <summary>Starts removing a server from this computer.</summary>
   public const string ServersRemove = "servers.remove";

   /// <summary>Gives a server that already exists a new session from a completed sign-in.</summary>
   public const string SessionSignIn = "session.signIn";

   /// <summary>Ends a server's session.</summary>
   public const string SessionSignOut = "session.signOut";

   /// <summary>Pairs this computer with a server using a one-time enrollment code.</summary>
   public const string PairingEnroll = "pairing.enroll";

   /// <summary>Asks a server's connection to reconnect.</summary>
   public const string ConnectionReconnect = "connection.reconnect";

   /// <summary>Reads a server's certificate exception, the two addresses it covers and the certificate last refused.</summary>
   public const string TrustGet = "trust.get";

   /// <summary>Switches a server's certificate exception on or off.</summary>
   public const string TrustSet = "trust.set";

   /// <summary>Shares a Windows printer with a server, or stops sharing it.</summary>
   public const string PrintersShare = "printers.share";

   /// <summary>Changes the name, description and location a server is given for a shared printer.</summary>
   public const string PrintersSetProfile = "printers.setProfile";

   /// <summary>Asks a server to queue a test print for a shared printer.</summary>
   public const string PrintersTestPrint = "printers.testPrint";

   /// <summary>Reads one page of a server's jobs.</summary>
   public const string JobsQuery = "jobs.query";

   /// <summary>Reads one job and its recorded events.</summary>
   public const string JobsDetails = "jobs.details";

   /// <summary>Asks a server to queue a job again.</summary>
   public const string JobsRetry = "jobs.retry";

   /// <summary>Cancels a job: in the Windows spooler when it is there, otherwise on the server.</summary>
   public const string JobsCancel = "jobs.cancel";

   /// <summary>Marks a failed job as seen by the operator.</summary>
   public const string JobsAcknowledge = "jobs.acknowledge";



   /// <summary>How long the service lets an ordinary operation run: 30 seconds, as in version 1.</summary>
   public static readonly TimeSpan OrdinaryTimeLimit = TimeSpan.FromSeconds(30);



   /// <summary>
   /// How long the service lets a long operation run: 120 seconds, as version 1 gives sign-in, sign-out and enrollment,
   /// because they wait for a QQQ server. Checking and adding a server wait for one too and are long as well.
   /// </summary>
   public static readonly TimeSpan LongTimeLimit = TimeSpan.FromSeconds(120);



   /// <summary>Every operation, the computer's first and then a server's, each list in the order ADR-0004 gives it.</summary>
   public static IReadOnlyList<ManagementOperationInfo> All { get; } =
   [
      Computer(Snapshot, ManagementOperationKind.Read, typeof(ServiceSnapshot), degraded: true),
      Computer(ServersList, ManagementOperationKind.Read, typeof(ServerListResult)),
      Computer(ServersCheck, ManagementOperationKind.Read, typeof(ServerCheckResult), typeof(ServersCheckArgs), isLong: true),
      Computer(ServersAdd, ManagementOperationKind.Change, typeof(ServerAddResult), typeof(ServersAddArgs), isLong: true),
      Computer(ServersReorder, ManagementOperationKind.Change, typeof(ServerListResult), typeof(ServersReorderArgs)),
      Computer(ComputerGetProfile, ManagementOperationKind.Read, typeof(DeviceProfile)),
      Computer(ComputerSetProfile, ManagementOperationKind.Change, typeof(DeviceProfile), typeof(ComputerSetProfileArgs)),
      Computer(LogsGetSettings, ManagementOperationKind.Read, typeof(LogSettings), degraded: true),
      Computer(LogsSetSettings, ManagementOperationKind.Change, typeof(LogSettings), typeof(LogsSetSettingsArgs), degraded: true),
      Computer(LogsClear, ManagementOperationKind.Change, typeof(LogsClearResult), degraded: true),
      Computer(UpdatePrepare, ManagementOperationKind.Change, typeof(UpdatePrepareResult), degraded: true),
      Computer(UpdateResume, ManagementOperationKind.Change, typeof(NoResult), degraded: true),
      Server(ServersRename, ManagementOperationKind.Change, typeof(ServerSummary), typeof(ServersRenameArgs)),
      Server(ServersRemove, ManagementOperationKind.SessionChange, typeof(ServerRemoveResult)),
      Server(SessionSignIn, ManagementOperationKind.SessionChange, typeof(SignInResult), typeof(SessionSignInArgs), isLong: true),
      Server(SessionSignOut, ManagementOperationKind.SessionChange, typeof(SignOutResult), typeof(SessionSignOutArgs), isLong: true),
      Server(PairingEnroll, ManagementOperationKind.SessionChange, typeof(EnrollResult), typeof(PairingEnrollArgs), isLong: true),
      Server(ConnectionReconnect, ManagementOperationKind.Change, typeof(ReconnectResult)),
      Server(TrustGet, ManagementOperationKind.Read, typeof(TrustResult)),
      Server(TrustSet, ManagementOperationKind.SessionChange, typeof(TrustResult), typeof(TrustSetArgs)),
      Server(PrintersShare, ManagementOperationKind.Change, typeof(NoResult), typeof(PrintersShareArgs)),
      Server(PrintersSetProfile, ManagementOperationKind.Change, typeof(NoResult), typeof(PrintersSetProfileArgs)),
      Server(PrintersTestPrint, ManagementOperationKind.Change, typeof(JobQueuedResult), typeof(PrintersTestPrintArgs)),
      Server(JobsQuery, ManagementOperationKind.Read, typeof(JobPage), typeof(JobsQueryArgs)),
      Server(JobsDetails, ManagementOperationKind.Read, typeof(JobDetailsSnapshot), typeof(JobsDetailsArgs)),
      Server(JobsRetry, ManagementOperationKind.Change, typeof(JobQueuedResult), typeof(JobsRetryArgs)),
      Server(JobsCancel, ManagementOperationKind.Change, typeof(JobCancelResult), typeof(JobsCancelArgs)),
      Server(JobsAcknowledge, ManagementOperationKind.Change, typeof(NoResult), typeof(JobsAcknowledgeArgs)),
   ];



   /// <summary>Finds an operation by its name on the wire, exactly as spelled; names are case-sensitive.</summary>
   public static bool TryFind(string name, [NotNullWhen(true)] out ManagementOperationInfo? operation)
   {
      operation = All.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
      return operation is not null;
   }



   /// <summary>
   /// Decides whether a request whose envelope was read can be routed, and to which operation. It returns null when it
   /// can, and otherwise the error to answer: the version is not this contract's; the operation does not exist; an
   /// operation on the computer names a server; or an operation on one server names none, or names it by anything but a
   /// full identifier. It is pure, so the dispatcher and the tests share one rule. The messages are fixed and never
   /// repeat what the request said.
   /// </summary>
   public static ManagementError? Check(ManagementRequest request, out ManagementOperationInfo? operation)
   {
      ArgumentNullException.ThrowIfNull(request);
      operation = null;
      if(request.V != ManagementProtocol.Version)
      {
         return new ManagementError(ManagementErrorCode.UnsupportedVersion, "This service speaks version 2 of the management protocol.");
      }
      if(!TryFind(request.Op, out operation))
      {
         return new ManagementError(ManagementErrorCode.UnknownOperation, "There is no operation with this name.");
      }
      if(operation.Scope == ManagementScope.Computer)
      {
         return request.Server is null ? null : new ManagementError(ManagementErrorCode.InvalidRequest, "This operation acts on the computer and must not name a server.");
      }
      return ManagementProtocol.IsServerId(request.Server) ? null
         : new ManagementError(ManagementErrorCode.InvalidRequest, "This operation acts on one server and must name it by its full identifier.");
   }



   /// <summary>Describes an operation on the computer or the list of servers.</summary>
   private static ManagementOperationInfo Computer(string name, ManagementOperationKind kind, Type result, Type? arguments = null, bool isLong = false, bool degraded = false)
      => new(name, ManagementScope.Computer, kind, isLong ? LongTimeLimit : OrdinaryTimeLimit, degraded, arguments, result);



   /// <summary>Describes an operation on one server. None of them answers while the stored data is not ready.</summary>
   private static ManagementOperationInfo Server(string name, ManagementOperationKind kind, Type result, Type? arguments = null, bool isLong = false)
      => new(name, ManagementScope.Server, kind, isLong ? LongTimeLimit : OrdinaryTimeLimit, AvailableWhenDegraded: false, arguments, result);
}
