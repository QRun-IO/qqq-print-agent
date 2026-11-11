/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// The operations on one server other than its jobs. Each takes the server's full identifier, as the snapshot gives
/// it; text that is not one is not sent, and the result is the refusal the service would give,
/// <see cref="ManagementErrorCode.InvalidRequest"/>.
/// </summary>
public sealed partial class ManagementClient
{
   /// <summary>Changes a server's name. A name that breaks the name rule (<see cref="ServerName"/>) is not sent. The result is the server as it is now stored.</summary>
   public Task<ServiceResult<ServerSummary>> RenameServerAsync(string serverId, string name, CancellationToken cancellationToken = default)
      => AskAsync<ServerSummary>(ManagementOperations.ServersRename, operation => ManagementRequest.For(operation, serverId, new ServersRenameArgs(name)), cancellationToken);



   /// <summary>Starts removing a server from this computer. The result says whether it is being removed or is already gone; asked again meanwhile, the answer is the same and not a refusal.</summary>
   public Task<ServiceResult<ServerRemoveResult>> RemoveServerAsync(string serverId, CancellationToken cancellationToken = default)
      => AskAsync<ServerRemoveResult>(ManagementOperations.ServersRemove, operation => ManagementRequest.For(operation, serverId), cancellationToken);



   /// <summary>
   /// Gives a server that already exists the session from a sign-in the companion has completed. Without a repair the
   /// saved pairing is bound as it is; only after that was refused with <see cref="ManagementErrorCode.BindRefused"/>
   /// is the repair the operator chose given, <see cref="SessionSignInArgs.AssociatePairing"/> or
   /// <see cref="SessionSignInArgs.NewPairing"/>. The credential is passed on once and neither kept nor logged.
   /// </summary>
   public Task<ServiceResult<SignInResult>> SignInAsync(string serverId, QqqSessionCredential sessionCredential, string? repair = null, CancellationToken cancellationToken = default)
      => AskAsync<SignInResult>(ManagementOperations.SessionSignIn, operation => ManagementRequest.For(operation, serverId, new SessionSignInArgs(sessionCredential, repair)), cancellationToken);



   /// <summary>
   /// Ends a server's session. Given the application's registered callback address, the service also asks the server
   /// for the identity provider's sign-out request and returns it in the result, for the browser; given null, only the
   /// QQQ session ends. That request carries the provider's ID-token hint: it goes from the result to the browser and
   /// nowhere else, and this client neither keeps nor logs it.
   /// </summary>
   public Task<ServiceResult<SignOutResult>> SignOutAsync(string serverId, string? postLogoutRedirectUri, CancellationToken cancellationToken = default)
      => AskAsync<SignOutResult>(ManagementOperations.SessionSignOut, operation => ManagementRequest.For(operation, serverId, new SessionSignOutArgs(postLogoutRedirectUri)), cancellationToken);



   /// <summary>
   /// Pairs this computer with a server using a one-time enrollment code. When the outcome is not known the refusal is
   /// <see cref="ManagementErrorCode.EnrollmentUnconfirmed"/>, whose details say what was left behind. The code is
   /// passed on once and neither kept nor logged.
   /// </summary>
   public Task<ServiceResult<EnrollResult>> EnrollAsync(string serverId, string enrollmentCode, CancellationToken cancellationToken = default)
      => AskAsync<EnrollResult>(ManagementOperations.PairingEnroll, operation => ManagementRequest.For(operation, serverId, new PairingEnrollArgs(enrollmentCode)), cancellationToken);



   /// <summary>Asks a server's connection to reconnect. The result says whether that was requested or whether the server's administrator holds the device disconnected.</summary>
   public Task<ServiceResult<ReconnectResult>> ReconnectAsync(string serverId, CancellationToken cancellationToken = default)
      => AskAsync<ReconnectResult>(ManagementOperations.ConnectionReconnect, operation => ManagementRequest.For(operation, serverId), cancellationToken);



   /// <summary>Reads a server's certificate exception, the two addresses it covers and the certificate the service last saw refused for it. The service answers from what it remembers and contacts no server.</summary>
   public Task<ServiceResult<TrustResult>> GetTrustAsync(string serverId, CancellationToken cancellationToken = default)
      => AskAsync<TrustResult>(ManagementOperations.TrustGet, operation => ManagementRequest.For(operation, serverId), cancellationToken);



   /// <summary>
   /// Switches a server's development-only certificate exception on or off.
   /// <para>
   /// <paramref name="providerOriginTheCheckShowed"/> states what the operator was shown, and has no default so that
   /// no caller can leave it out. To switch the exception on, check the server first
   /// (<see cref="CheckServerAsync"/>), show the operator both origins the exception will cover, and give exactly
   /// the <see cref="ServerCheckResult.ProviderOrigin"/> of that check: null only when that check named no provider.
   /// Null is a statement like any other ("the check named none") and is compared too. Never give an origin from
   /// another source. The service compares this value with what it reads from the server now, and when they differ
   /// it changes nothing and answers <see cref="ManagementErrorCode.ProviderChanged"/>, so that the exception never
   /// comes to cover a provider the operator did not see (ADR-0004, "Who names the sign-in provider"). Switching the
   /// exception off accepts nothing, so nothing is compared, and null is what to give.
   /// </para>
   /// <para>Text that names no origin is not sent, whichever way the switch goes: the result is the refusal the service would give, <see cref="ManagementErrorCode.InvalidRequest"/>.</para>
   /// </summary>
   public Task<ServiceResult<TrustResult>> SetTrustAsync(string serverId, bool acceptCertificateException, string? providerOriginTheCheckShowed, CancellationToken cancellationToken = default)
      => AskAsync<TrustResult>(ManagementOperations.TrustSet,
         operation => ManagementRequest.For(operation, serverId, new TrustSetArgs(acceptCertificateException, providerOriginTheCheckShowed)), cancellationToken);



   /// <summary>Shares a Windows printer with a server, or stops sharing it.</summary>
   public Task<ServiceResult<NoResult>> SharePrinterAsync(string serverId, string printerId, bool shared, CancellationToken cancellationToken = default)
      => AskAsync<NoResult>(ManagementOperations.PrintersShare, operation => ManagementRequest.For(operation, serverId, new PrintersShareArgs(printerId, shared)), cancellationToken);



   /// <summary>Changes the name, description and location a server is given for a printer shared with it.</summary>
   public Task<ServiceResult<NoResult>> SetPrinterProfileAsync(string serverId, string printerId, PrinterProfile profile, CancellationToken cancellationToken = default)
      => AskAsync<NoResult>(ManagementOperations.PrintersSetProfile, operation => ManagementRequest.For(operation, serverId, new PrintersSetProfileArgs(printerId, profile)), cancellationToken);



   /// <summary>
   /// Asks a server to queue a test print for a printer shared with it, in one of the printer's formats and optionally
   /// with one of the server's test documents. The result names the job when the server gave it an identifier. It asks
   /// for a job to be queued; it does not say that anything was printed.
   /// </summary>
   public Task<ServiceResult<JobQueuedResult>> TestPrintAsync(string serverId, string printerId, string format, string? testProfile, CancellationToken cancellationToken = default)
      => AskAsync<JobQueuedResult>(ManagementOperations.PrintersTestPrint, operation => ManagementRequest.For(operation, serverId, new PrintersTestPrintArgs(printerId, format, testProfile)), cancellationToken);
}
