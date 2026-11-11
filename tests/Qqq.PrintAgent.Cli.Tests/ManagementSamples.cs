/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Security;
using System.Reflection;
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
/// carries a canary where the real one carries the secret, so a result that copied a secret would show it. The fully
/// populated snapshot sets every member at once, which no real service would; two further snapshots show what a healthy
/// service and a degraded one really send.
/// </summary>
internal static class ManagementSamples
{
   /// <summary>The local identifier of the first example server.</summary>
   internal const string ServerId = "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f";



   /// <summary>The local identifier of the second example server; it shares its first eight characters with the first.</summary>
   internal const string SecondServerId = "3f2b8c1e-0000-4f6b-9a7c-0e1d2c3b4a60";



   /// <summary>The address of the first example server.</summary>
   internal const string Address = "https://qqq.example.test";



   /// <summary>The address of the sign-in provider the first example server names.</summary>
   internal const string ProviderOrigin = "https://sign-in.example.test";



   /// <summary>Stands for a session cookie. It may appear in a request that carries a credential and in no reply.</summary>
   internal const string SessionCookieCanary = "CANARY-SESSION-COOKIE-7f3a";



   /// <summary>Stands for a device token. It may appear in no request and no reply.</summary>
   internal const string DeviceTokenCanary = "CANARY-DEVICE-TOKEN-91c2";



   /// <summary>Stands for an enrollment code. It may appear in the enrollment request and in no reply.</summary>
   internal const string EnrollmentCodeCanary = "CANARY-ENROLLMENT-CODE-55d0";



   /// <summary>Stands for the provider's ID-token hint. It may appear in the sign-out reply and nowhere else.</summary>
   internal const string IdTokenHintCanary = "CANARY-ID-TOKEN-HINT-08be";



   /// <summary>The instant the examples count their times from.</summary>
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



   /// <summary>Further golden replies beside the one per operation, by file name: what a healthy service with two servers sends for a snapshot, and what a service whose stored data is not ready sends.</summary>
   internal static IReadOnlyDictionary<string, ServiceSnapshot> FurtherSnapshots { get; } = new Dictionary<string, ServiceSnapshot>(StringComparer.Ordinal)
   {
      ["snapshot.healthy.reply.json"] = HealthySnapshot,
      ["snapshot.degraded.reply.json"] = DegradedSnapshot,
   };



   /// <summary>The server an operation's example request names, or null for an operation on the computer.</summary>
   internal static string? ServerFor(ManagementOperationInfo operation) => operation.Scope == ManagementScope.Server ? ServerId : null;



   /// <summary>The example arguments of an operation, or null for one that takes none.</summary>
   internal static object? Arguments(string operation) => operation switch
   {
      ManagementOperations.ServersCheck => new ServersCheckArgs(Address, AcceptCertificateException: true),
      ManagementOperations.ServersAdd => new ServersAddArgs(Address, "Warehouse", acceptCertificateException: true, SessionCredential, ProviderOrigin),
      ManagementOperations.ServersReorder => new ServersReorderArgs([SecondServerId, ServerId]),
      ManagementOperations.ComputerSetProfile => new ComputerSetProfileArgs(Computer.Profile),
      ManagementOperations.LogsSetSettings => new LogsSetSettingsArgs(RetentionDays: 14, MaxFileMegabytes: 50, SegmentsPerDay: 5, DebugLogging: true),
      ManagementOperations.ServersRename => new ServersRenameArgs("Warehouse east"),
      ManagementOperations.SessionSignIn => new SessionSignInArgs(SessionCredential, SessionSignInArgs.AssociatePairing),
      ManagementOperations.SessionSignOut => new SessionSignOutArgs("io.qrun.qqq-print-agent:/oauth/callback"),
      ManagementOperations.PairingEnroll => new PairingEnrollArgs(EnrollmentCodeCanary),
      ManagementOperations.TrustSet => new TrustSetArgs(AcceptCertificateException: true, ProviderOrigin),
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
      ManagementOperations.TransportGet => new TransportResult([new ServerTransport(ServerId, Server.Transport, Server.LastContact), new ServerTransport(SecondServerId, Stopped)]),
      ManagementOperations.ServersList or ManagementOperations.ServersReorder => new ServerListResult([Summary, new ServerSummary(SecondServerId, "Test bench", "https://bench.example.test", 1, ServerSnapshot.Red, "Sign-in is required.", Removing: true)]),
      ManagementOperations.ServersCheck => CheckPassed,
      ManagementOperations.ServersAdd => new ServerAddResult(Summary, Authentication),
      ManagementOperations.ComputerGetProfile or ManagementOperations.ComputerSetProfile => Computer.Profile,
      ManagementOperations.PrintersDiscover => new PrintersDiscoverResult([LabelPrinter], s_instant),
      ManagementOperations.LogsGetSettings or ManagementOperations.LogsSetSettings => Computer.LogSettings,
      ManagementOperations.LogsClear => new LogsClearResult(12),
      ManagementOperations.UpdatePrepare => new UpdatePrepareResult(Ready: false, JobsInHand: 2, ClaimPending: true),
      ManagementOperations.ServersRename => Summary,
      ManagementOperations.ServersRemove => new ServerRemoveResult(ServerRemoveResult.Removing),
      ManagementOperations.SessionSignIn => new SignInResult(Authentication, Paired: true, PrintingEnabled: true),
      ManagementOperations.SessionSignOut => new SignOutResult(ServerSessionEnded: true, UnreadableSessionSetAside: true, ProviderLogout),
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
         [new QqqCompatibilityProblem(QqqCompatibilityIssue.SecureConnectionFailed, "Windows does not trust the authority that issued the certificate")], null, QqqSignInMethods.None,
         new QqqCertificateRejection(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, "CN=qqq.example.test", "CN=Example Development Root",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), "A1B2C3D4E5F60718293A4B5C6D7E8F9001122334", new Uri(Address), s_instant)), ProviderOrigin, ServerId),
      ManagementErrorCode.IdentityChanged => IdentityChangedDetails.For(StoredSession.Binding.PrincipalId, StoredSession.Binding.TenantId),
      ManagementErrorCode.EnrollmentUnconfirmed => new EnrollmentUnconfirmedDetails(EnrollmentUnconfirmedDetails.DeviceBound, PrintingBlocked: true, EnrollmentAnswer.DeviceId),
      ManagementErrorCode.CancelRefused => new CancelRefusedDetails(CancelRefusedDetails.OwnershipMismatch),
      _ => null,
   };



   /// <summary>The message every example failure carries.</summary>
   internal const string FailureMessage = "Synthetic message for people.";



   /// <summary>The example failure of a code, built by the factory the service will use for it: the one that takes details for a code that carries them, with the example details, and the other for a code that carries none.</summary>
   internal static ManagementReply Failure(string code) => Details(code) is { } details ? FailureWith(code, details) : ManagementReply.Failure(code, FailureMessage);



   /// <summary>A failed reply built through the contract's own factory for details whose type is known only at run time. A refusal by the factory is thrown as itself, not wrapped by the reflection call.</summary>
   internal static ManagementReply FailureWith(string code, object details)
   {
      MethodInfo factory = typeof(ManagementReply).GetMethods().Single(method => method is { Name: nameof(ManagementReply.Failure), IsGenericMethodDefinition: true });
      return (ManagementReply)factory.MakeGenericMethod(details.GetType()).Invoke(null, BindingFlags.DoNotWrapExceptions, null, [code, FailureMessage, details], null)!;
   }



   /// <summary>A check in which every stage passed, for a server that offers both sign-in methods and is already a server here.</summary>
   private static ServerCheckResult CheckPassed => ServerCheckResult.From(new QqqServerCompatibility([], null, QqqSignInMethods.Password | QqqSignInMethods.Browser), ProviderOrigin, ServerId);



   /// <summary>Who is signed in, taken from the stored session's identity and never from its credential.</summary>
   private static ServerAuthentication Authentication
      => ServerAuthentication.From(new AgentAuthenticationStatus(SignedIn: true, StoredSession.Binding.PrincipalId, StoredSession.Binding.TenantId, "Signed in. Printing is enabled.", Authorized: true));



   /// <summary>The certificate Windows refused, as the trust operations describe it: remembered from the sign-in provider's address.</summary>
   private static PresentedCertificate Certificate => PresentedCertificate.From(new QqqCertificateRejection(SslPolicyErrors.RemoteCertificateChainErrors, "CN=sign-in.example.test",
      "CN=Example Development Root", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), "A1B2C3D4E5F60718293A4B5C6D7E8F9001122334",
      new Uri(ProviderOrigin), s_instant.AddMinutes(-20)));



   /// <summary>One job with every member set.</summary>
   private static JobSnapshot Job => new("job-204", "FAILED", "2026-11-10T15:04:05.0000000Z", "The printer is offline.", "42", "printer-a1", "Shipping labels", "Invoice 5512", "PDF",
      FailureAcknowledged: true, CreatedAt: "2026-11-10T15:00:00.0000000Z");



   /// <summary>The first server as the list operations show it.</summary>
   private static ServerSummary Summary => new(ServerId, "Warehouse", Address, 0, ServerSnapshot.Green, "Connected.");



   /// <summary>The transport of a server that has no connection: nothing sent, nothing received.</summary>
   private static AgentTransportSnapshot Stopped => new("qqq-printing.v1", "STOPPED", 0, 0, 0, 0, s_instant.AddHours(-3));



   /// <summary>The one Windows printer the examples have.</summary>
   private static ComputerPrinter LabelPrinter => ComputerPrinter.From(new PrinterInfo("printer-a1", "ZDesigner ZT411", "ZDesigner ZT411-203dpi ZPL", "USB001", "READY", ["PDF", "ZPL"],
      "Not carried", "Not carried", "Not carried", [new PrinterMediaProfile("label-4x6", new string('a', 64), "4 x 6 in", 101600, 152400, 203, 203)]));



   /// <summary>The computer with every member set.</summary>
   private static ComputerSnapshot Computer => new(
      [LabelPrinter],
      s_instant.AddSeconds(-40),
      new ServiceState("2.0.0-dev+abc1234", s_instant.AddHours(-3)),
      new UpdateState(Paused: true, s_instant.AddMinutes(10)),
      new LogSettings(30, 100, 10, DebugLogging: true, CurrentFileBytes: 20480, SegmentsToday: 2),
      new StoredState(StoredState.UpgradeIncomplete, "connection.dpapi", FoundVersion: 3, SupportedVersion: 2),
      new DeviceProfile("Packing station 7", "Label printing for outbound orders", "West warehouse"));



   /// <summary>One server with every member set, built from the stored pairing and session the way the service will build it.</summary>
   private static ServerSnapshot Server => new(ServerId, "Warehouse", Address, 0, ServerSnapshot.Orange, "Dispatch paused by the administrator.", AttentionCount: 3,
      Authentication, Paired: true, StoredPairing.DeviceId,
      new AgentTransportSnapshot("qqq-printing.v1", "CONNECTED", 1048576, 73400320, 512.5, 20480.25, s_instant.AddHours(-3)), s_instant.AddSeconds(-4),
      ServerControl.From(new AgentControlState("SUPPORTED", new DeviceDesiredState(12, DispatchPaused: true, "CONNECTED"), "2026-11-10T15:04:00.0000000Z", "REFRESH_INVENTORY", "INVENTORY_REFRESHED", "2026-11-10T14:58:00.0000000Z")),
      [new ServerPrinter("printer-a1", PrinterServerState.Approved, "Shipping labels", "Thermal printer", "Dock 2")],
      new PrinterServerSync(PrinterServerSync.Reported, "2026-11-10T15:03:00.0000000Z", "2 printers approved", Address, PrinterServerSync.RealtimeSource),
      new CertificateExceptionState(Enabled: true, Address, ProviderOrigin), StateUnreadable: true, Removing: true);



   /// <summary>What a healthy service sends: stored data ready, no update pause, one server connected and printing, and a second that is signed in but not yet paired and shares nothing.</summary>
   private static ServiceSnapshot HealthySnapshot => new(
      new ComputerSnapshot([LabelPrinter], s_instant.AddSeconds(-25), new ServiceState("2.0.0", s_instant.AddDays(-2)), new UpdateState(Paused: false), new LogSettings(), new StoredState(StoredState.Ready),
         new DeviceProfile("Packing station 7", "", "West warehouse")),
      [
         new ServerSnapshot(ServerId, "Warehouse", Address, 0, ServerSnapshot.Green, "Connected. Waiting for print jobs.", AttentionCount: 0,
            ServerAuthentication.From(new AgentAuthenticationStatus(SignedIn: true, "principal-4", "tenant-2", "Signed in. Printing is enabled.", Authorized: true)), Paired: true, "device-17",
            new AgentTransportSnapshot("qqq-printing.v1", "CONNECTED", 20480, 5242880, 0, 0, s_instant.AddDays(-2)), s_instant.AddSeconds(-12),
            new ServerControl("SUPPORTED", new DeviceDesiredState(3, DispatchPaused: false, "CONNECTED"), "2026-11-10T15:03:53.0000000Z"),
            [new ServerPrinter("printer-a1", PrinterServerState.Approved, "Shipping labels")],
            new PrinterServerSync(PrinterServerSync.Reported, "2026-11-10T15:03:00.0000000Z", Server: Address, Source: PrinterServerSync.RealtimeSource),
            new CertificateExceptionState(Enabled: false, Address), StateUnreadable: false, Removing: false),
         new ServerSnapshot(SecondServerId, "Test bench", "https://bench.example.test", 1, ServerSnapshot.Orange, "Signed in. Pair this computer to enable printing.", AttentionCount: 0,
            ServerAuthentication.From(new AgentAuthenticationStatus(SignedIn: true, "principal-9", "tenant-2", "Signed in. Pair this computer to enable printing.", Authorized: false)), Paired: false, DeviceId: null,
            Stopped, LastContact: null, new ServerControl("UNKNOWN", new DeviceDesiredState(0, DispatchPaused: false, "CONNECTED")), [],
            PrinterServerSync.Initial, new CertificateExceptionState(Enabled: true, "https://bench.example.test", "https://bench.example.test"), StateUnreadable: false, Removing: false),
      ]);



   /// <summary>What a service sends when a newer build wrote its stored data: it runs no connections, so there are no servers, and the computer's part says why.</summary>
   private static ServiceSnapshot DegradedSnapshot => new(
      new ComputerSnapshot([LabelPrinter], s_instant.AddSeconds(-55), new ServiceState("2.0.0-rc.2", s_instant.AddMinutes(-5)), new UpdateState(Paused: false), new LogSettings(),
         new StoredState(StoredState.NewerBuild, FoundVersion: 4, SupportedVersion: 3), new DeviceProfile()),
      []);
}
