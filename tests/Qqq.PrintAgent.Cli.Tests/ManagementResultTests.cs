/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Security;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The result records that are built from what Core and the store already hold: the compatibility check's stages, the
/// refused certificate, a Windows printer, the control state and an enrollment answer. Each takes what the pipe carries
/// and leaves the rest behind. The last test holds the one existing record that gained a member to its version 1 form.
/// </summary>
public sealed class ManagementResultTests
{
   /// <summary>A server that passes every check reports five passed stages in the order they are made, the sign-in methods it offers, and the provider and existing server the service adds; it is a success, with no error code.</summary>
   [Fact]
   public void Check_ThatPasses_ReportsFivePassedStagesInOrder()
   {
      var result = ServerCheckResult.From(new QqqServerCompatibility([], null, QqqSignInMethods.Password | QqqSignInMethods.Browser), "https://sign-in.example.test", ManagementSamples.ServerId);

      Assert.Equal(["connection", "security", "server", "printing", "signIn"], result.Stages.Select(stage => stage.Stage), StringComparer.Ordinal);
      Assert.Equal(CompatibilityStageResult.Order, result.Stages.Select(stage => stage.Stage), StringComparer.Ordinal);
      Assert.All(result.Stages, stage => Assert.Equal((CompatibilityStageResult.Passed, 0), (stage.Outcome, stage.Problems.Count)));
      Assert.Equal(["password", "browser"], result.SignInMethods);
      Assert.Equal(("https://sign-in.example.test", ManagementSamples.ServerId), (result.ProviderOrigin, result.ExistingServerId));
      Assert.Null(result.Certificate);
      Assert.Null(result.FailureCode());
   }



   /// <summary>A failure at any stage leaves the stages before it passed and the stages after it not reached, with the reason on the stage that failed, and is answered as an incompatible server.</summary>
   [Theory]
   [InlineData(QqqCompatibilityIssue.ServerUnreachable, "failed,notReached,notReached,notReached,notReached")]
   [InlineData(QqqCompatibilityIssue.SecureConnectionFailed, "passed,failed,notReached,notReached,notReached")]
   [InlineData(QqqCompatibilityIssue.NotQqqServer, "passed,passed,failed,notReached,notReached")]
   [InlineData(QqqCompatibilityIssue.ApiVersionUnsupported, "passed,passed,failed,notReached,notReached")]
   [InlineData(QqqCompatibilityIssue.PrintingExtensionMissing, "passed,passed,passed,failed,notReached")]
   [InlineData(QqqCompatibilityIssue.SignInUnavailable, "passed,passed,passed,passed,failed")]
   public void Check_ThatFailsAtOneStage_MarksEarlierStagesPassedAndLaterOnesNotReached(QqqCompatibilityIssue issue, string outcomes)
   {
      var result = ServerCheckResult.From(new QqqServerCompatibility([new QqqCompatibilityProblem(issue, "the reason Core gave")], null, QqqSignInMethods.None));

      Assert.Equal(outcomes.Split(','), result.Stages.Select(stage => stage.Outcome), StringComparer.Ordinal);
      Assert.Equal(["the reason Core gave"], Assert.Single(result.Stages, stage => string.Equals(stage.Outcome, CompatibilityStageResult.Failed, StringComparison.Ordinal)).Problems);
      Assert.Empty(result.SignInMethods);
      Assert.Equal((null, null), (result.ProviderOrigin, result.ExistingServerId));
      Assert.Equal(ManagementErrorCode.ServerIncompatible, result.FailureCode());
   }



   /// <summary>Several problems of one stage are all listed on it, in Core's order, and a later stage that has a problem of its own is failed, not merely unreached, so nothing Core found is dropped.</summary>
   [Fact]
   public void Check_WithSeveralProblems_KeepsEveryOneOnItsStage()
   {
      var result = ServerCheckResult.From(new QqqServerCompatibility(
         [
            new QqqCompatibilityProblem(QqqCompatibilityIssue.ApiFeatureMissing, "first"),
            new QqqCompatibilityProblem(QqqCompatibilityIssue.SignInUnavailable, "third"),
            new QqqCompatibilityProblem(QqqCompatibilityIssue.ClientChannelMissing, "second"),
         ], null, QqqSignInMethods.Password));

      Assert.Equal(["passed", "passed", "failed", "notReached", "failed"], result.Stages.Select(stage => stage.Outcome), StringComparer.Ordinal);
      Assert.Equal(["first", "second"], result.Stages[2].Problems);
      Assert.Equal(["third"], result.Stages[4].Problems);
      Assert.Equal(["password"], result.SignInMethods);
   }



   /// <summary>A refused certificate turns a failed secure connection into the code the operator can act on, and its description is carried; a failed secure connection without a certificate, and a certificate beside a failure at another stage, stay an incompatible server.</summary>
   [Fact]
   public void Check_WithARefusedCertificate_IsAnsweredAsUntrusted()
   {
      QqqCertificateRejection rejection = new(SslPolicyErrors.RemoteCertificateChainErrors, "CN=qqq.example.test", "CN=Example Development Root",
         new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), "A1B2C3");
      QqqCompatibilityProblem secure = new(QqqCompatibilityIssue.SecureConnectionFailed, "the certificate was refused");
      QqqCompatibilityProblem unreachable = new(QqqCompatibilityIssue.ServerUnreachable, "no answer");

      var untrusted = ServerCheckResult.From(new QqqServerCompatibility([secure], null, QqqSignInMethods.None, rejection), certificateOrigin: "https://qqq.example.test", observedAt: new DateTimeOffset(2026, 11, 10, 15, 4, 5, TimeSpan.Zero));
      var handshake = ServerCheckResult.From(new QqqServerCompatibility([secure], null, QqqSignInMethods.None));
      var elsewhere = ServerCheckResult.From(new QqqServerCompatibility([unreachable], null, QqqSignInMethods.None, rejection));

      Assert.Equal(ManagementErrorCode.CertificateUntrusted, untrusted.FailureCode());
      Assert.Equal(("A1B2C3", "https://qqq.example.test", new DateTimeOffset(2026, 11, 10, 15, 4, 5, TimeSpan.Zero)), (untrusted.Certificate!.Fingerprint, untrusted.Certificate.Origin, untrusted.Certificate.ObservedAt));
      Assert.Equal(ManagementErrorCode.ServerIncompatible, handshake.FailureCode());
      Assert.Null(handshake.Certificate);
      Assert.Equal(ManagementErrorCode.ServerIncompatible, elsewhere.FailureCode());
   }



   /// <summary>A refused certificate is described by its subject, issuer, validity in UTC and fingerprint as Core recorded them, with every reason Windows gave in a fixed order and the address and time the service adds; a certificate that was never presented has reasons and nothing else.</summary>
   [Theory]
   [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, "untrustedAuthority")]
   [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, "nameMismatch")]
   [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, "notPresented")]
   [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, "untrustedAuthority,nameMismatch")]
   [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, "untrustedAuthority,nameMismatch,notPresented")]
   public void Certificate_IsDescribedWithEveryReasonItWasRefused(SslPolicyErrors errors, string refusals)
   {
      var described = PresentedCertificate.From(new QqqCertificateRejection(errors, "CN=qqq.example.test", "CN=Example Development Root",
         new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 8, 30, 0, DateTimeKind.Unspecified), "A1B2C3"),
         "https://sign-in.example.test", new DateTimeOffset(2026, 11, 10, 15, 4, 5, TimeSpan.Zero));
      var absent = PresentedCertificate.From(new QqqCertificateRejection(SslPolicyErrors.RemoteCertificateNotAvailable, null, null, null, null, null));

      Assert.Equal(refusals.Split(','), described.Refusals);
      Assert.Equal(("CN=qqq.example.test", "CN=Example Development Root", "A1B2C3"), (described.Subject, described.Issuer, described.Fingerprint));
      Assert.Equal(new DateTimeOffset(2026, 1, 1, 8, 30, 0, TimeSpan.Zero), described.NotBefore);
      Assert.Equal(new DateTimeOffset(2027, 1, 1, 8, 30, 0, TimeSpan.Zero), described.NotAfter);
      Assert.Equal(("https://sign-in.example.test", new DateTimeOffset(2026, 11, 10, 15, 4, 5, TimeSpan.Zero)), (described.Origin, described.ObservedAt));
      Assert.Equal((null, null), (absent.Origin, absent.ObservedAt));
      Assert.Equal(new PresentedCertificate(absent.Refusals), absent);
      Assert.Equal(["notPresented"], absent.Refusals);
   }



   /// <summary>A Windows printer in the computer's part of the snapshot carries what describes the queue and not the name, description and location an operator gave it, because those are per server.</summary>
   [Fact]
   public void ComputerPrinter_LeavesOutThePerServerProfile()
   {
      PrinterMediaProfile media = new("label-4x6", new string('a', 64), "4 x 6 in", 101600, 152400, 203, 203);
      PrinterInfo discovered = new("printer-a1", "ZDesigner ZT411", "ZDesigner driver", "USB001", "READY", ["PDF", "ZPL"], "Operator name", "Operator description", "Operator location", [media]);

      var printer = ComputerPrinter.From(discovered);
      string written = JsonSerializer.Serialize(printer, ManagementJson.Options);

      Assert.Equal(("printer-a1", "ZDesigner ZT411", "ZDesigner driver", "USB001", "READY"), (printer.PrinterId, printer.Name, printer.DriverName, printer.PortName, printer.Status));
      Assert.Equal(["PDF", "ZPL"], printer.Formats);
      Assert.Equal([media], printer.MediaProfiles);
      Assert.DoesNotContain("Operator", written, StringComparison.Ordinal);
      Assert.Null(ComputerPrinter.From(discovered with { MediaProfiles = null }).MediaProfiles);
   }



   /// <summary>A server's control state on the wire is a record of the contract, built from the stored state member by member, so the form of the state file can change without changing what clients read. The times are carried as the stored text.</summary>
   [Fact]
   public void ServerControl_IsBuiltFromTheStoredStateMemberByMember()
   {
      AgentControlState stored = new("SUPPORTED", new DeviceDesiredState(12, DispatchPaused: true, "DISCONNECTED"), "contact", "REFRESH_INVENTORY", "INVENTORY_REFRESHED", "commanded");

      var control = ServerControl.From(stored);
      string written = JsonSerializer.Serialize(control, ManagementJson.Options);

      Assert.Equal(new ServerControl("SUPPORTED", new DeviceDesiredState(12, DispatchPaused: true, "DISCONNECTED"), "contact", "REFRESH_INVENTORY", "INVENTORY_REFRESHED", "commanded"), control);
      Assert.Equal("{\"support\":\"SUPPORTED\",\"desiredState\":{\"revision\":12,\"dispatchPaused\":true,\"connectionState\":\"DISCONNECTED\"},\"lastContact\":\"contact\",\"lastCommand\":\"REFRESH_INVENTORY\",\"lastOutcome\":\"INVENTORY_REFRESHED\",\"lastCommandAt\":\"commanded\"}", written);
      Assert.Equal(new ServerControl("UNKNOWN", new DeviceDesiredState(0, DispatchPaused: false, "CONNECTED")), ServerControl.From(AgentControlState.Initial));
   }



   /// <summary>The result of an enrollment is the device identifier from the server's answer; the answer's device token is not carried.</summary>
   [Fact]
   public void EnrollResult_TakesTheDeviceIdentifierAndNotTheToken()
   {
      var result = EnrollResult.From(ManagementSamples.EnrollmentAnswer);

      Assert.Equal(new EnrollResult("device-17"), result);
      Assert.Equal("{\"deviceId\":\"device-17\"}", JsonSerializer.Serialize(result, ManagementJson.Options));
   }



   /// <summary>The names the contract uses on the wire for the values of its small vocabularies, so renaming one is a deliberate change.</summary>
   [Fact]
   public void Vocabularies_AreSpelledAsOnTheWire()
   {
      Assert.Equal(("ready", "upgradeIncomplete", "newerBuild"), (StoredState.Ready, StoredState.UpgradeIncomplete, StoredState.NewerBuild));
      Assert.Equal(("GREEN", "ORANGE", "RED"), (ServerSnapshot.Green, ServerSnapshot.Orange, ServerSnapshot.Red));
      Assert.Equal(("removing", "removed"), (ServerRemoveResult.Removing, ServerRemoveResult.Removed));
      Assert.Equal(("requested", "heldByAdministrator"), (ReconnectResult.Requested, ReconnectResult.HeldByAdministrator));
      Assert.Equal(("spooler", "server"), (JobCancelResult.Spooler, JobCancelResult.Server));
      Assert.Equal(("documentGone", "ownershipMismatch"), (CancelRefusedDetails.DocumentGone, CancelRefusedDetails.OwnershipMismatch));
      Assert.Equal(("untrustedAuthority", "nameMismatch", "notPresented"), (PresentedCertificate.UntrustedAuthority, PresentedCertificate.NameMismatch, PresentedCertificate.NotPresented));
      Assert.Equal(("associatePairing", "newPairing"), (SessionSignInArgs.AssociatePairing, SessionSignInArgs.NewPairing));
      Assert.Equal(("nothingChanged", "deviceBound", "saved", "activated"),
         (EnrollmentUnconfirmedDetails.NothingChanged, EnrollmentUnconfirmedDetails.DeviceBound, EnrollmentUnconfirmedDetails.Saved, EnrollmentUnconfirmedDetails.Activated));
      Assert.Equal(("password", "browser"), (ServerCheckResult.Password, ServerCheckResult.Browser));
      Assert.Equal(("passed", "failed", "notReached"), (CompatibilityStageResult.Passed, CompatibilityStageResult.Failed, CompatibilityStageResult.NotReached));
   }



   /// <summary>
   /// The job record gained a marker for a job from an earlier pairing. It is written only when true, so what version 1 of
   /// the pipe sends for a job is byte for byte what it sent before the member existed, with the version 1 serializer;
   /// version 2 carries the marker when it is set and leaves it out otherwise, and reads its absence as false.
   /// </summary>
   [Fact]
   public void JobSnapshot_EarlierPairingMarker_LeavesTheVersionOneFormUnchanged()
   {
      JobSnapshot job = new("job-204", "FAILED", "2026-11-10T15:04:05Z", "The printer is offline.", "42", "printer-a1", "Shipping labels", "Invoice 5512", "PDF", FailureAcknowledged: false, CreatedAt: "2026-11-10T15:00:00Z");
      const string VersionOne = "{\"jobId\":\"job-204\",\"status\":\"FAILED\",\"updatedAt\":\"2026-11-10T15:04:05Z\",\"message\":\"The printer is offline.\",\"spoolerJobId\":\"42\","
         + "\"printerId\":\"printer-a1\",\"printerName\":\"Shipping labels\",\"title\":\"Invoice 5512\",\"format\":\"PDF\",\"failureAcknowledged\":false,\"createdAt\":\"2026-11-10T15:00:00Z\"}";

      Assert.Equal(VersionOne, JsonSerializer.Serialize(job, PrintApiClient.JsonOptions));
      Assert.Equal(VersionOne, JsonSerializer.Serialize(job, ManagementJson.Options));
      Assert.Equal(VersionOne[..^1] + ",\"earlierPairing\":true}", JsonSerializer.Serialize(job with { EarlierPairing = true }, ManagementJson.Options));
      Assert.False(JsonSerializer.Deserialize<JobSnapshot>(VersionOne, ManagementJson.Options)!.EarlierPairing);
      Assert.Equal(job, JsonSerializer.Deserialize<JobSnapshot>(VersionOne, PrintApiClient.JsonOptions));
      Assert.True(JsonSerializer.Deserialize<JobSnapshot>(VersionOne[..^1] + ",\"earlierPairing\":true}", ManagementJson.Options)!.EarlierPairing);
   }
}
