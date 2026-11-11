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
   /// Reads the whole aggregated state: the computer once and every server once. It is single-flight: a call made
   /// while one is in flight shares that one's result. It is repeated on a timer, so over the ceiling it is skipped
   /// (<see cref="ServiceOutcome.Skipped"/>) and asked again on the next tick.
   /// </summary>
   public Task<ServiceResult<ServiceSnapshot>> SnapshotAsync(CancellationToken cancellationToken = default)
      => ShareAsync(_snapshot, ManagementOperations.Snapshot, cancellationToken);



   /// <summary>
   /// Reads each server's transport state, traffic meter and last contact, for the meter between snapshots. It is
   /// single-flight and skipped over the ceiling, as <see cref="SnapshotAsync"/> is.
   /// </summary>
   public Task<ServiceResult<TransportResult>> TransportAsync(CancellationToken cancellationToken = default)
      => ShareAsync(_transport, ManagementOperations.TransportGet, cancellationToken);



   /// <summary>
   /// Asks the service to run the compatibility check for an address, under its own identity and certificate store,
   /// with or without the operator's acceptance of the development-only certificate exception. Nothing is stored. A
   /// check that fails is a refusal with <see cref="ManagementErrorCode.ServerIncompatible"/> or
   /// <see cref="ManagementErrorCode.CertificateUntrusted"/>, whose details are a <see cref="ServerCheckResult"/> too.
   /// What the check reports as the sign-in provider (<see cref="ServerCheckResult.ProviderOrigin"/>) is what the
   /// operator is shown, and the result the operator was shown is what <see cref="AddServerAsync"/> and
   /// <see cref="SetTrustAsync"/> must be given. An operator asks for it once, so over the ceiling it waits its turn.
   /// </summary>
   public Task<ServiceResult<ServerCheckResult>> CheckServerAsync(string address, bool acceptCertificateException, CancellationToken cancellationToken = default)
      => AskAsync<ServerCheckResult>(ManagementOperations.ServersCheck, operation => ManagementRequest.For(operation, null, new ServersCheckArgs(address, acceptCertificateException)), cancellationToken);



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
   /// A name that breaks the name rule (<see cref="ServerName"/>), or a check whose provider is text that names no
   /// origin, is not sent: the result is the refusal the service would give,
   /// <see cref="ManagementErrorCode.InvalidRequest"/>. The credential is passed on once and neither kept nor
   /// logged. Over the ceiling the request waits its turn.
   /// </para>
   /// </summary>
   public Task<ServiceResult<ServerAddResult>> AddServerAsync(string address, string name, bool acceptCertificateException, ServerCheckResult checkTheOperatorSaw,
      QqqSessionCredential sessionCredential, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(checkTheOperatorSaw);
      return AskAsync<ServerAddResult>(ManagementOperations.ServersAdd,
         operation => ManagementRequest.For(operation, null, new ServersAddArgs(address, name, acceptCertificateException, sessionCredential, checkTheOperatorSaw.ProviderOrigin)), cancellationToken);
   }



   /// <summary>Puts the servers in a new order: every server's identifier, each once. The result is the list in the order that is now stored. Over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<ServerListResult>> ReorderServersAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default)
      => AskAsync<ServerListResult>(ManagementOperations.ServersReorder, operation => ManagementRequest.For(operation, null, new ServersReorderArgs(serverIds)), cancellationToken);



   /// <summary>Changes this computer's name, description and location. A member left out keeps what is stored; empty text clears it. The result is the profile as it is now stored. Over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<DeviceProfile>> SetComputerProfileAsync(DeviceProfile profile, CancellationToken cancellationToken = default)
      => AskAsync<DeviceProfile>(ManagementOperations.ComputerSetProfile, operation => ManagementRequest.For(operation, null, new ComputerSetProfileArgs(profile)), cancellationToken);



   /// <summary>Discovers this computer's Windows printers now and returns them with the time. The snapshot only reports the list the service last discovered; the wizard's printer step asks here for a fresh one. An operator asks for it once, so over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<PrintersDiscoverResult>> DiscoverPrintersAsync(CancellationToken cancellationToken = default)
      => AskAsync<PrintersDiscoverResult>(ManagementOperations.PrintersDiscover, static operation => ManagementRequest.For(operation), cancellationToken);



   /// <summary>Reads the log limits and the current log file's size. A page asks for it once, so over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<LogSettings>> GetLogSettingsAsync(CancellationToken cancellationToken = default)
      => AskAsync<LogSettings>(ManagementOperations.LogsGetSettings, static operation => ManagementRequest.For(operation), cancellationToken);



   /// <summary>Changes the four log limits, all of them. The result is the settings as they are now stored, with the current file's size. Over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<LogSettings>> SetLogSettingsAsync(int retentionDays, int maxFileMegabytes, int segmentsPerDay, bool debugLogging, CancellationToken cancellationToken = default)
      => AskAsync<LogSettings>(ManagementOperations.LogsSetSettings,
         operation => ManagementRequest.For(operation, null, new LogsSetSettingsArgs(retentionDays, maxFileMegabytes, segmentsPerDay, debugLogging)), cancellationToken);



   /// <summary>Deletes the activity log files and says how many were deleted. Over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<LogsClearResult>> ClearLogsAsync(CancellationToken cancellationToken = default)
      => AskAsync<LogsClearResult>(ManagementOperations.LogsClear, static operation => ManagementRequest.For(operation), cancellationToken);
}
