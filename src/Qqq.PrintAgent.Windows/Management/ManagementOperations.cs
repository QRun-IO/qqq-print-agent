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
   /// <summary>The whole aggregated state: the computer once and every server once. No jobs and no log lines. It reports what the service already knows and never starts printer discovery, so it is always cheap.</summary>
   public const string Snapshot = "snapshot";



   /// <summary>Each server's transport state, traffic meter and last contact, and nothing else: the light read a client repeats every second to draw the meter, between the snapshots it takes every five. It answers with an empty list while the stored data is not ready, so that state does not produce an error every second.</summary>
   public const string TransportGet = "transport.get";



   /// <summary>The servers in the operator's order, with what is needed to name and select one.</summary>
   public const string ServersList = "servers.list";



   /// <summary>Runs the compatibility check for an address in the service, under the service's own identity and certificate store.</summary>
   public const string ServersCheck = "servers.check";



   /// <summary>Stores a new server together with its certificate exception and its session, or nothing. The request states which sign-in provider the check showed the operator: the origin the check reported, or none when the member is left out. Nothing is stored and the answer is <see cref="ManagementErrorCode.ProviderChanged"/> when the provider the service reads is not that one, which includes reading one where the check named none and none where it named one.</summary>
   public const string ServersAdd = "servers.add";



   /// <summary>Puts the servers in a new order.</summary>
   public const string ServersReorder = "servers.reorder";



   /// <summary>Reads this computer's name, description and location.</summary>
   public const string ComputerGetProfile = "computer.getProfile";



   /// <summary>Changes this computer's name, description and location.</summary>
   public const string ComputerSetProfile = "computer.setProfile";



   /// <summary>
   /// Discovers this computer's Windows printers now and returns them with the time. A snapshot only reports the list
   /// the service last discovered; a client that needs a fresh one (the wizard's printer step, the command line's
   /// <c>printers</c>) asks here. It is a read, and the one single-flight operation
   /// (<see cref="ManagementOperationInfo.SingleFlight"/>): a discovery loads printer drivers in a helper process, so the
   /// computer runs one at a time, and a request that arrives while one is running, whether a client or the service's
   /// own refresh started it, joins that discovery and gets its result. A hung driver and a client that retries then
   /// cost one helper process, not one for each request.
   /// </summary>
   public const string PrintersDiscover = "printers.discover";



   /// <summary>Reads the log limits and the current log file's size.</summary>
   public const string LogsGetSettings = "logs.getSettings";



   /// <summary>Changes the log limits.</summary>
   public const string LogsSetSettings = "logs.setSettings";



   /// <summary>Deletes the activity log files.</summary>
   public const string LogsClear = "logs.clear";



   /// <summary>Pauses claiming for an update and says whether work in hand has finished. The update helper is always one version behind the service it talks to, so if this operation ever gains arguments, a request without them must stay accepted.</summary>
   public const string UpdatePrepare = "update.prepare";



   /// <summary>Lifts the update pause. The update helper is always one version behind the service it talks to, so if this operation ever gains arguments, a request without them must stay accepted.</summary>
   public const string UpdateResume = "update.resume";



   /// <summary>Changes a server's name.</summary>
   public const string ServersRename = "servers.rename";



   /// <summary>Starts removing a server from this computer. Asked again for a server that is already being removed, it succeeds with <see cref="ServerRemoveResult.Removing"/> again, where every other change to that server is refused with <see cref="ManagementErrorCode.ServerRemoving"/>.</summary>
   public const string ServersRemove = "servers.remove";



   /// <summary>Gives a server that already exists a new session from a completed sign-in.</summary>
   public const string SessionSignIn = "session.signIn";



   /// <summary>Ends a server's session.</summary>
   public const string SessionSignOut = "session.signOut";



   /// <summary>Pairs this computer with a server using a one-time enrollment code.</summary>
   public const string PairingEnroll = "pairing.enroll";



   /// <summary>Asks a server's connection to reconnect.</summary>
   public const string ConnectionReconnect = "connection.reconnect";



   /// <summary>Reads a server's certificate exception, the two addresses it covers and the certificate the service last saw refused for it. It reports what the service remembers and never contacts the server.</summary>
   public const string TrustGet = "trust.get";



   /// <summary>Switches a server's certificate exception on or off. A request that switches it on states which sign-in provider the check showed the operator: the origin the check reported, or none when the member is left out. Nothing is changed and the answer is <see cref="ManagementErrorCode.ProviderChanged"/> when the provider the service reads is not that one, which includes reading one where the check named none and none where it named one. A request that switches it off accepts nothing, so nothing is compared.</summary>
   public const string TrustSet = "trust.set";



   /// <summary>Shares a Windows printer with a server, or stops sharing it.</summary>
   public const string PrintersShare = "printers.share";



   /// <summary>Changes the name, description and location a server is given for a shared printer.</summary>
   public const string PrintersSetProfile = "printers.setProfile";



   /// <summary>Asks a server to queue a test print for a printer shared with it. A printer that is not shared with the server is refused as not found, and a format the printer does not offer as an invalid request.</summary>
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
      Computer(TransportGet, ManagementOperationKind.Read, typeof(TransportResult), degraded: true),
      Computer(ServersList, ManagementOperationKind.Read, typeof(ServerListResult)),
      Computer(ServersCheck, ManagementOperationKind.Read, typeof(ServerCheckResult), typeof(ServersCheckArgs), isLong: true),
      Computer(ServersAdd, ManagementOperationKind.Change, typeof(ServerAddResult), typeof(ServersAddArgs), isLong: true),
      Computer(ServersReorder, ManagementOperationKind.Change, typeof(ServerListResult), typeof(ServersReorderArgs)),
      Computer(ComputerGetProfile, ManagementOperationKind.Read, typeof(DeviceProfile)),
      Computer(ComputerSetProfile, ManagementOperationKind.Change, typeof(DeviceProfile), typeof(ComputerSetProfileArgs)),
      Computer(PrintersDiscover, ManagementOperationKind.Read, typeof(PrintersDiscoverResult), singleFlight: true),
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
   /// can, and otherwise the error to answer: the operation does not exist; an operation on the computer names a
   /// server; an operation on one server names none; or an operation that takes no arguments is given some (an empty
   /// object is accepted, because it says the same as leaving the arguments out). It is pure, so the dispatcher and
   /// the tests share one rule. The messages are fixed and never repeat what the request said. Three things are not
   /// asked here because no request can be otherwise (<see cref="ManagementRequest"/>): its version is this
   /// contract's, its operation is shaped like a name, and a server it names is named by a full identifier. Text
   /// that breaks one of them is refused by the parser, which is where <see cref="ManagementErrorCode.UnsupportedVersion"/>
   /// is answered.
   /// </summary>
   public static ManagementError? Check(ManagementRequest request, out ManagementOperationInfo? operation)
   {
      ArgumentNullException.ThrowIfNull(request);
      if(!TryFind(request.Op, out operation))
      {
         return new ManagementError(ManagementErrorCode.UnknownOperation, "There is no operation with this name.");
      }
      if(operation.Scope == ManagementScope.Computer && request.Server is not null)
      {
         return new ManagementError(ManagementErrorCode.InvalidRequest, "This operation acts on the computer and must not name a server.");
      }
      if(operation.Scope == ManagementScope.Server && request.Server is null)
      {
         return new ManagementError(ManagementErrorCode.InvalidRequest, "This operation acts on one server and must name it by its full identifier.");
      }
      return operation.ArgumentsType is null && request.Args is { } given && !given.IsEmpty()
         ? new ManagementError(ManagementErrorCode.InvalidRequest, "This operation takes no arguments.") : null;
   }



   /// <summary>Describes an operation on the computer or the list of servers.</summary>
   private static ManagementOperationInfo Computer(string name, ManagementOperationKind kind, Type result, Type? arguments = null, bool isLong = false, bool degraded = false,
      bool singleFlight = false)
      => new(name, ManagementScope.Computer, kind, isLong ? LongTimeLimit : OrdinaryTimeLimit, degraded, arguments, result, singleFlight);



   /// <summary>Describes an operation on one server. None of them answers while the stored data is not ready.</summary>
   private static ManagementOperationInfo Server(string name, ManagementOperationKind kind, Type result, Type? arguments = null, bool isLong = false)
      => new(name, ManagementScope.Server, kind, isLong ? LongTimeLimit : OrdinaryTimeLimit, AvailableWhenDegraded: false, arguments, result);
}
