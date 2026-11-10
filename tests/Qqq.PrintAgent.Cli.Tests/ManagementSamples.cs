/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Security;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// One fully populated example of every argument record, every result record and every typed error detail of the
/// management contract, for the tests that need a value of each: the golden files are these examples as the wire carries
/// them, and the secrecy test serializes every result here. The examples are built the way the service will build them,
/// from the objects that hold its secrets (a session credential, a stored pairing, an enrollment answer), each of which
/// carries a canary where the real one carries the secret, so a result that copied a secret would show it.
/// </summary>
internal static class ManagementSamples
{
   internal const string ServerId = "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f";
   internal const string SecondServerId = "3f2b8c1e-0000-4f6b-9a7c-0e1d2c3b4a60";
   internal const string Address = "https://qqq.example.test";
   internal const string ProviderOrigin = "https://sign-in.example.test";
   internal const string SessionCookieCanary = "CANARY-SESSION-COOKIE-7f3a";
   internal const string DeviceTokenCanary = "CANARY-DEVICE-TOKEN-91c2";
   internal const string EnrollmentCodeCanary = "CANARY-ENROLLMENT-CODE-55d0";
   internal const string IdTokenHintCanary = "CANARY-ID-TOKEN-HINT-08be";

   private static readonly DateTimeOffset s_instant = new(2026, 11, 10, 15, 4, 5, TimeSpan.Zero);



   /// <summary>The session a completed sign-in hands the service: its token is the cookie canary.</summary>
   internal static QqqSessionCredential SessionCredential => new(new Uri(Address), SessionCookieCanary);



   /// <summary>The pairing as the service stores it: its device token is a canary.</summary>
   internal static AgentConfiguration StoredPairing => new(Address, "device-17", DeviceTokenCanary);



   /// <summary>The server's answer to an enrollment: its device token is a canary.</summary>
   internal static EnrollmentResponse EnrollmentAnswer => new(1, "device-17", DeviceTokenCanary, 5);



   /// <summary>The session as the service stores it: an identity and the credential with the cookie canary.</summary>
   internal static AgentAuthentication StoredSession => new(new QqqSessionBinding(new Uri(Address), "principal-4", "tenant-2", StoredPairing.DeviceId), SessionCredential);



   /// <summary>The identity provider's sign-out request, whose address carries the ID-token hint canary. It is the one sensitive value a reply may carry.</summary>
   internal static QqqProviderLogout ProviderLogout => new(new Uri("https://sign-in.example.test/logout?id_token_hint=" + IdTokenHintCanary), new Uri("io.qrun.qqq-print-agent:/oauth/callback"), "state-31");



   /// <summary>The server an operation's example request names, or null for an operation on the computer.</summary>
   internal static string? ServerFor(ManagementOperationInfo operation) => operation.Scope == ManagementScope.Server ? ServerId : null;



   /// <summary>The example arguments of an operation, or null for one that takes none.</summary>
   internal static object? Arguments(string operation) => operation switch
   {
      ManagementOperations.ServersCheck => new ServersCheckArgs(Address, AcceptCertificateException: true),
      ManagementOperations.ServersAdd => new ServersAddArgs(Address, "Warehouse", AcceptCertificateException: true, SessionCredential),
      ManagementOperations.ServersReorder => new ServersReorderArgs([SecondServerId, ServerId]),
      ManagementOperations.ComputerSetProfile => new ComputerSetProfileArgs(Computer.Profile),
      ManagementOperations.LogsSetSettings => new LogsSetSettingsArgs(new LogSettings(14, 50, 5, DebugLogging: true)),
      ManagementOperations.ServersRename => new ServersRenameArgs("Warehouse east"),
      ManagementOperations.SessionSignIn => new SessionSignInArgs(SessionCredential, SessionSignInArgs.AssociatePairing),
      ManagementOperations.SessionSignOut => new SessionSignOutArgs("io.qrun.qqq-print-agent:/oauth/callback"),
      ManagementOperations.PairingEnroll => new PairingEnrollArgs(EnrollmentCodeCanary),
      ManagementOperations.TrustSet => new TrustSetArgs(AcceptCertificateException: true),
      ManagementOperations.PrintersShare => new PrintersShareArgs("printer-a1", Shared: true),
      ManagementOperations.PrintersSetProfile => new PrintersSetProfileArgs("printer-a1", new PrinterProfile("Shipping labels", "Thermal printer", "Dock 2")),
      ManagementOperations.PrintersTestPrint => new PrintersTestPrintArgs("printer-a1", "PDF", "alignment"),
      ManagementOperations.JobsQuery => new JobsQueryArgs(new JobQuery("inv", "FAILED", "printer-a1", NeedsAttentionOnly: true, Offset: 50, PageSize: 25, SortBy: "updatedAt", Descending: false)),
      ManagementOperations.JobsDetails => new JobsDetailsArgs("job-204"),
      ManagementOperations.JobsRetry => new JobsRetryArgs("job-204", ConfirmDuplicateRisk: true),
      ManagementOperations.JobsCancel => new JobsCancelArgs("job-204"),
      ManagementOperations.JobsAcknowledge => new JobsAcknowledgeArgs("job-204"),
      _ => null,
   };



   /// <summary>The example result of an operation.</summary>
   internal static object Result(string operation) => operation switch
   {
      ManagementOperations.Snapshot => new ServiceSnapshot(Computer, [Server]),
      ManagementOperations.ServersList or ManagementOperations.ServersReorder => new ServerListResult([Summary, new ServerSummary(SecondServerId, "Test bench", "https://bench.example.test", 1, ServerSnapshot.Red, "Sign-in is required.", Removing: true)]),
      ManagementOperations.ServersCheck => CheckPassed,
      ManagementOperations.ServersAdd => new ServerAddResult(Summary, Authentication),
      ManagementOperations.ComputerGetProfile or ManagementOperations.ComputerSetProfile => Computer.Profile,
      ManagementOperations.LogsGetSettings or ManagementOperations.LogsSetSettings => Computer.LogSettings,
      ManagementOperations.LogsClear => new LogsClearResult(12),
      ManagementOperations.UpdatePrepare => new UpdatePrepareResult(Ready: false, JobsInHand: 2, ClaimPending: true),
      ManagementOperations.ServersRename => Summary,
      ManagementOperations.ServersRemove => new ServerRemoveResult(ServerRemoveResult.Removing),
      ManagementOperations.SessionSignIn => new SignInResult(Authentication, Paired: true, PrintingEnabled: true),
      ManagementOperations.SessionSignOut => new SignOutResult(UnreadableSessionSetAside: true, ProviderLogout),
      ManagementOperations.PairingEnroll => EnrollResult.From(EnrollmentAnswer),
      ManagementOperations.ConnectionReconnect => new ReconnectResult(ReconnectResult.Requested),
      ManagementOperations.TrustGet or ManagementOperations.TrustSet => new TrustResult(Server.CertificateException, Certificate),
      ManagementOperations.PrintersTestPrint or ManagementOperations.JobsRetry => new JobQueuedResult("job-205"),
      ManagementOperations.JobsQuery => new JobPage([Job, Job with { JobId = "job-090", EarlierPairing = true }], TotalCount: 77, Offset: 50, PageSize: 25, ServerQueueLimited: true),
      ManagementOperations.JobsDetails => new JobDetailsSnapshot(Job, [new JobEventSnapshot("event-9", "FAILED", "2026-11-10T15:04:05.0000000Z", "The printer is offline.", "42", Delivered: true)], HistoryPruned: true),
      ManagementOperations.JobsCancel => new JobCancelResult(JobCancelResult.Spooler),
      _ => new NoResult(),
   };



   /// <summary>The example details of an error code that carries details, or null for a code that carries none.</summary>
   internal static object? Details(string code) => code switch
   {
      ManagementErrorCode.ServerIncompatible => ServerCheckResult.From(new QqqServerCompatibility(
         [new QqqCompatibilityProblem(QqqCompatibilityIssue.PrintingExtensionTooOld, "the printing extension is version 1.4 and realtime printing needs 2.3")], null, QqqSignInMethods.None)),
      ManagementErrorCode.CertificateUntrusted => ServerCheckResult.From(new QqqServerCompatibility(
         [new QqqCompatibilityProblem(QqqCompatibilityIssue.SecureConnectionFailed, "Windows does not trust the authority that issued the server's certificate")], null, QqqSignInMethods.None,
         new QqqCertificateRejection(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, "CN=qqq.example.test", "CN=Example Development Root",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), "A1B2C3D4E5F60718293A4B5C6D7E8F9001122334")), ProviderOrigin, ServerId),
      ManagementErrorCode.EnrollmentUnconfirmed => new EnrollmentUnconfirmedDetails(EnrollmentUnconfirmedDetails.DeviceBound, PrintingBlocked: true, EnrollmentAnswer.DeviceId),
      _ => null,
   };



   /// <summary>A check in which every stage passed, for a server that offers both sign-in methods and is already a server here.</summary>
   private static ServerCheckResult CheckPassed => ServerCheckResult.From(new QqqServerCompatibility([], null, QqqSignInMethods.Password | QqqSignInMethods.Browser), ProviderOrigin, ServerId);



   /// <summary>Who is signed in, taken from the stored session's identity and never from its credential.</summary>
   private static AgentAuthenticationStatus Authentication
      => new(SignedIn: true, StoredSession.Binding.PrincipalId, StoredSession.Binding.TenantId, "Signed in. Printing is enabled.", Authorized: true);



   /// <summary>The certificate Windows refused, as the trust operations describe it.</summary>
   private static PresentedCertificate Certificate => PresentedCertificate.From(new QqqCertificateRejection(SslPolicyErrors.RemoteCertificateChainErrors, "CN=qqq.example.test",
      "CN=Example Development Root", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), "A1B2C3D4E5F60718293A4B5C6D7E8F9001122334"));



   /// <summary>One job with every member set.</summary>
   private static JobSnapshot Job => new("job-204", "FAILED", "2026-11-10T15:04:05.0000000Z", "The printer is offline.", "42", "printer-a1", "Shipping labels", "Invoice 5512", "PDF",
      FailureAcknowledged: true, CreatedAt: "2026-11-10T15:00:00.0000000Z");



   /// <summary>The first server as the list operations show it.</summary>
   private static ServerSummary Summary => new(ServerId, "Warehouse", Address, 0, ServerSnapshot.Green, "Connected.");



   /// <summary>The computer with every member set.</summary>
   private static ComputerSnapshot Computer => new(
      [ComputerPrinter.From(new PrinterInfo("printer-a1", "ZDesigner ZT411", "ZDesigner ZT411-203dpi ZPL", "USB001", "READY", ["PDF", "ZPL"], "Not carried", "Not carried", "Not carried",
         [new PrinterMediaProfile("label-4x6", new string('a', 64), "4 x 6 in", 101600, 152400, 203, 203)]))],
      new ServiceState("2.0.0-dev+abc1234", s_instant.AddHours(-3)),
      new UpdateState(Paused: true, s_instant.AddMinutes(10)),
      new LogSettings(30, 100, 10, DebugLogging: true, CurrentFileBytes: 20480, SegmentsToday: 2),
      new StoredState(StoredState.UpgradeIncomplete, "connection.dpapi", FoundVersion: 3, SupportedVersion: 2),
      new DeviceProfile("Packing station 7", "Label printing for outbound orders", "West warehouse"));



   /// <summary>One server with every member set, built from the stored pairing and session the way the service will build it.</summary>
   private static ServerSnapshot Server => new(ServerId, "Warehouse", Address, 0, ServerSnapshot.Orange, "Dispatch paused by the administrator.", AttentionCount: 3,
      Authentication, Paired: true, StoredPairing.DeviceId,
      new AgentTransportSnapshot("qqq-printing.v1", "CONNECTED", 1048576, 73400320, 512.5, 20480.25, s_instant.AddHours(-3)),
      ServerControl.From(new AgentControlState("SUPPORTED", new DeviceDesiredState(12, DispatchPaused: true, "CONNECTED"), "2026-11-10T15:04:00.0000000Z", "REFRESH_INVENTORY", "INVENTORY_REFRESHED", "2026-11-10T14:58:00.0000000Z")),
      [new ServerPrinter("printer-a1", PrinterServerState.Approved, "Shipping labels", "Thermal printer", "Dock 2")],
      new PrinterServerSync(PrinterServerSync.Reported, "2026-11-10T15:03:00.0000000Z", "2 printers approved", Address, PrinterServerSync.RealtimeSource),
      new CertificateExceptionState(Enabled: true, Address, ProviderOrigin), StateUnreadable: true, Removing: true);
}
