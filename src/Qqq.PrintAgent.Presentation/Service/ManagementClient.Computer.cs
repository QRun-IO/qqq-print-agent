/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// The operations on the computer and on the list of servers. The companion does not send
/// <see cref="ManagementOperations.ServersList"/> or <see cref="ManagementOperations.ComputerGetProfile"/>, because the
/// snapshot carries both, nor <see cref="ManagementOperations.UpdatePrepare"/> and
/// <see cref="ManagementOperations.UpdateResume"/>, which the update service sends itself.
/// </summary>
public sealed partial class ManagementClient
{
   /// <summary>
   /// Reads the whole aggregated state: the computer once and every server once. It is single-flight: one snapshot
   /// is at the pipe at a time.
   /// <para>
   /// Called as the timer calls it, with <paramref name="sentAfterTheCall"/> false, a call made while a snapshot is
   /// in flight shares that one's result, and over the ceiling the call is skipped
   /// (<see cref="ServiceOutcome.Skipped"/>) and asked again on the next tick. The answer may therefore be that of a
   /// request sent before the call, and so may not show a change the caller has just made.
   /// </para>
   /// <para>
   /// With <paramref name="sentAfterTheCall"/> true, the answer is that of a snapshot sent after this call was made
   /// and never an earlier one, which is what a caller needs that reads the state to learn the outcome of a change:
   /// the "check" after an add or an enrollment whose outcome is not known, and a page refreshed after a change. The
   /// call waits out a snapshot that is in flight and then sends another; several such calls that wait together
   /// share that one. Over the ceiling it waits its turn and is not skipped. A token cancelled while it waits ends
   /// it with nothing sent.
   /// </para>
   /// </summary>
   public Task<ServiceResult<ServiceSnapshot>> SnapshotAsync(bool sentAfterTheCall = false, CancellationToken cancellationToken = default)
      => ShareAsync(_snapshot, ManagementOperations.Snapshot, sentAfterTheCall, cancellationToken);



   /// <summary>
   /// Reads each server's transport state, traffic meter and last contact, for the meter between snapshots. It is
   /// single-flight and skipped over the ceiling, as <see cref="SnapshotAsync"/> is when the timer calls it.
   /// </summary>
   public Task<ServiceResult<TransportResult>> TransportAsync(CancellationToken cancellationToken = default)
      => ShareAsync(_transport, ManagementOperations.TransportGet, sentAfterTheCall: false, cancellationToken);



   /// <summary>
   /// Asks the service to run the compatibility check for an address, under its own identity and certificate store,
   /// with or without the operator's acceptance of the development-only certificate exception. Nothing is stored. A
   /// check that fails is a refusal with <see cref="ManagementErrorCode.ServerIncompatible"/> or
   /// <see cref="ManagementErrorCode.CertificateUntrusted"/>, whose details are a <see cref="ServerCheckResult"/> too.
   /// What the check reports as the sign-in provider (<see cref="ServerCheckResult.ProviderOrigin"/>) is what the
   /// operator is shown, and the result the operator was shown is what <see cref="AddServerAsync"/> and
   /// <see cref="SetTrustAsync"/> must be given. An operator asks for it once, so over the ceiling it waits its turn.
   /// <para>
   /// <paramref name="address"/> is the server's origin as <see cref="ServerAddress.TryRead"/> returned it for the
   /// text the operator typed, not that text: only the origin is what was judged, and what is stored and shown.
   /// The request carries it as scheme, host and port. An address that rule refuses (a path, a query, a scheme that
   /// is not secure) is not sent: the result is the refusal the service would give,
   /// <see cref="ManagementErrorCode.InvalidRequest"/>.
   /// </para>
   /// </summary>
   public Task<ServiceResult<ServerCheckResult>> CheckServerAsync(Uri address, bool acceptCertificateException, CancellationToken cancellationToken = default)
      => AskAsync<ServerCheckResult>(ManagementOperations.ServersCheck, operation => WithArguments(operation, null, new ServersCheckArgs(AddressOf(address), acceptCertificateException)), cancellationToken);



   /// <summary>
   /// Stores a new server together with its certificate exception, if one was accepted, and the session from a sign-in
   /// the companion has completed; or stores nothing.
   /// <para>
   /// <paramref name="checkTheOperatorSaw"/> is the result of the check (<see cref="CheckServerAsync"/>) that the
   /// operator was shown before going on, and it must be given. The request states which sign-in provider the
   /// operator accepted, so that statement has to come from the check the operator saw and from no other read of
   /// the provider. That is why this method takes the check's result and not an origin as text: text would let a
   /// caller pass the companion's own read of the sign-in description by mistake. The client sends the result's
   /// <see cref="ServerCheckResult.ProviderOrigin"/> exactly as the check reported it, and leaves the member out
   /// when the check named no provider, which is a statement like any other ("the check named none") and is
   /// compared too. The service compares it with what it reads from the server now, and when they differ it stores
   /// nothing and answers <see cref="ManagementErrorCode.ProviderChanged"/>, so that a certificate exception never
   /// comes to cover a provider the operator did not see (ADR-0004, "Who names the sign-in provider"). After that
   /// refusal the server is checked again and the operator is shown the new result.
   /// </para>
   /// <para>
   /// What the type of that argument guarantees, and what it does not. It keeps text from being passed by accident,
   /// and a check that failed (<see cref="ServerCheckResult.FailureCode"/> is not null) is refused here as an
   /// argument that is wrong, with nothing sent: the operator goes on only from a check in which every stage passed,
   /// so a failed one is never the check the operator went on from. It does not show that the result is the one the
   /// service returned: the record is public, and a result changed with a <c>with</c> expression is sent as changed.
   /// Nor does it show that the check was of this address: the record does not name the address it was for, so a
   /// check of another server that names the same provider is not detected. The caller has to keep the check with
   /// the address it was made for.
   /// </para>
   /// <para>
   /// <paramref name="address"/> is the server's origin as <see cref="ServerAddress.TryRead"/> returned it, as for
   /// <see cref="CheckServerAsync"/>. An address that rule refuses, a name that breaks the name rule
   /// (<see cref="ServerName"/>), a check whose provider is text that names no origin, and a credential that is
   /// missing are not sent: the result is the refusal the service would give,
   /// <see cref="ManagementErrorCode.InvalidRequest"/>. The credential is passed on once and neither kept nor
   /// logged. Over the ceiling the request waits its turn. A reply that names another address than the one asked
   /// for is unreadable.
   /// </para>
   /// </summary>
   public Task<ServiceResult<ServerAddResult>> AddServerAsync(Uri address, string name, bool acceptCertificateException, ServerCheckResult checkTheOperatorSaw,
      QqqSessionCredential sessionCredential, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(checkTheOperatorSaw);
      if(checkTheOperatorSaw.FailureCode() is not null)
      {
         throw new ArgumentException("The check given is one that failed. A server is added only after a check in which every stage passed.", nameof(checkTheOperatorSaw));
      }
      return AskAsync<ServerAddResult>(ManagementOperations.ServersAdd,
         operation => WithArguments(operation, null, new ServersAddArgs(AddressOf(address), name, acceptCertificateException, sessionCredential, checkTheOperatorSaw.ProviderOrigin)), cancellationToken,
         added => IsAddress(added.Server.Address, address));
   }



   /// <summary>
   /// Puts the servers in a new order: every server's identifier, each once. The result is the list in the order that
   /// is now stored. Over the ceiling it waits its turn. A list that is missing, or that has nothing where an
   /// identifier belongs, is not sent. A reply that does not name exactly the servers that were given is unreadable;
   /// the order it names them in is the service's to say and is not compared. The list is copied when the call is
   /// made, and the copy is what is sent and what the reply is compared with, so a caller may change its list while
   /// the request is in flight.
   /// </summary>
   public Task<ServiceResult<ServerListResult>> ReorderServersAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default)
   {
      string[]? asked = serverIds?.ToArray();
      return AskAsync<ServerListResult>(ManagementOperations.ServersReorder, operation => WithArguments(operation, null, new ServersReorderArgs(asked!)), cancellationToken,
         list => NamesExactly(list, asked!));
   }



   /// <summary>True when a list of servers a reply holds names exactly the servers that were asked about, each as often as it was given, in whatever order.</summary>
   private static bool NamesExactly(ServerListResult list, IReadOnlyList<string> serverIds)
      => list.Servers.Select(server => server.ServerId).Order(StringComparer.Ordinal).SequenceEqual(serverIds.Order(StringComparer.Ordinal), StringComparer.Ordinal);



   /// <summary>Changes this computer's name, description and location. A member left out keeps what is stored; empty text clears it. The result is the profile as it is now stored. Over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<DeviceProfile>> SetComputerProfileAsync(DeviceProfile profile, CancellationToken cancellationToken = default)
      => AskAsync<DeviceProfile>(ManagementOperations.ComputerSetProfile, operation => WithArguments(operation, null, new ComputerSetProfileArgs(profile)), cancellationToken);



   /// <summary>Discovers this computer's Windows printers now and returns them with the time. The snapshot only reports the list the service last discovered; the wizard's printer step asks here for a fresh one. An operator asks for it once, so over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<PrintersDiscoverResult>> DiscoverPrintersAsync(CancellationToken cancellationToken = default)
      => AskAsync<PrintersDiscoverResult>(ManagementOperations.PrintersDiscover, static operation => ManagementRequest.For(operation), cancellationToken);



   /// <summary>Reads the log limits and the current log file's size. A page asks for it once, so over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<LogSettings>> GetLogSettingsAsync(CancellationToken cancellationToken = default)
      => AskAsync<LogSettings>(ManagementOperations.LogsGetSettings, static operation => ManagementRequest.For(operation), cancellationToken);



   /// <summary>Changes the four log limits, all of them, given as the contract's own record. The result is the settings as they are now stored, with the current file's size. Over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<LogSettings>> SetLogSettingsAsync(LogsSetSettingsArgs settings, CancellationToken cancellationToken = default)
      => AskAsync<LogSettings>(ManagementOperations.LogsSetSettings, operation => WithArguments(operation, null, settings), cancellationToken);



   /// <summary>Deletes the activity log files and says how many were deleted. Over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<LogsClearResult>> ClearLogsAsync(CancellationToken cancellationToken = default)
      => AskAsync<LogsClearResult>(ManagementOperations.LogsClear, static operation => ManagementRequest.For(operation), cancellationToken);
}
