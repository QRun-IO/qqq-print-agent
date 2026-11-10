/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>Password sign-in and the hand-over of the session to the service with the operator's pairing choice.</summary>
public sealed class SignInFlowPasswordTests
{
   /// <summary>Builds a fixture whose flow has already discovered a password server.</summary>
   private static async Task<SignInFixture> DiscoveredAsync()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Shown.Clear();
      fixture.Stages.Clear();
      fixture.Log.Entries.Clear();
      return fixture;
   }



   /// <summary>Without a discovered session a sign-in does nothing: no request, no message.</summary>
   [Fact]
   public async Task WithoutDiscovery_NothingHappens()
   {
      SignInFixture fixture = new();

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      Assert.Empty(fixture.Requests);
      Assert.Empty(fixture.Shown);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>A sign-in the server and the service both accept passes the trimmed user name and the password to the server, hands only the session credential to the service, and ends signed in with the service's own sentence shown as success.</summary>
   [Fact]
   public async Task AcceptedSignIn_HandsOnlyTheSessionToTheService()
   {
      SignInFixture fixture = await DiscoveredAsync();

      await fixture.Flow.SignInWithPasswordAsync("  operator ", " pass word ", PairingChoice.KeepCurrent);

      Assert.Equal(("operator", " pass word "), fixture.Sessions[0].PasswordAttempt);
      PipeRequest request = Assert.Single(fixture.Requests);
      Assert.Equal("sign-in", request.Operation);
      Assert.Equal("synthetic-session-token", request.SessionCredential?.SessionToken);
      Assert.False(request.ClaimLegacyDevice);
      Assert.False(request.StartNewPairing);
      Assert.Equal([new FeedbackMessage("Signing in", "Checking the username and password with the server.", FeedbackSeverity.Informational),
         new FeedbackMessage("Signed in", "Signed in to QQQ. Printing is ready.", FeedbackSeverity.Success)], fixture.Shown);
      Assert.Equal([SignInStage.SigningIn, SignInStage.HandingOver, SignInStage.SignedIn], fixture.Stages);
      Assert.Equal(["INFO Password sign-in submitted for print.example.com.", "INFO Sign-in accepted by the server; handing the session to the service (keep the current pairing).",
         "INFO Service sign-in result: Signed in to QQQ. Printing is ready."], fixture.Log.Entries);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("pass word", StringComparison.Ordinal) || entry.Contains("synthetic-session-token", StringComparison.Ordinal));
   }



   /// <summary>The pairing choice travels as the two flags the service reads and is named in the log; keeping the current pairing sets neither.</summary>
   [Theory]
   [InlineData(PairingChoice.KeepCurrent, false, false, "keep the current pairing")]
   [InlineData(PairingChoice.ClaimLegacyDevice, true, false, "associate with the existing pairing")]
   [InlineData(PairingChoice.StartNewPairing, false, true, "start a new pairing")]
   public async Task PairingChoice_TravelsWithTheSession(PairingChoice pairing, bool claim, bool startNew, string wording)
   {
      SignInFixture fixture = await DiscoveredAsync();

      await fixture.Flow.SignInWithPasswordAsync(null, "secret", pairing);

      PipeRequest request = Assert.Single(fixture.Requests);
      Assert.Equal(claim, request.ClaimLegacyDevice);
      Assert.Equal(startNew, request.StartNewPairing);
      Assert.Contains($"INFO Sign-in accepted by the server; handing the session to the service ({wording}).", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(string.Empty, fixture.Sessions[0].PasswordAttempt?.Username);
   }



   /// <summary>When the server refuses the credentials nothing is handed to the service, and the message gives the reason without saying which part was wrong; the flow is ready for another attempt.</summary>
   [Fact]
   public async Task RefusedCredentials_AreReportedWithoutReachingTheService()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Sessions[0].PasswordFailure = new QqqSessionException(QqqSessionException.FailureKind.AuthenticationRequired, "The server did not accept this sign-in.");

      await fixture.Flow.SignInWithPasswordAsync("operator", "wrong", PairingChoice.KeepCurrent);

      Assert.Empty(fixture.Requests);
      Assert.Equal(new FeedbackMessage("Sign-in not completed", "The server did not authorize this connection, or the service could not confirm it. Reason: The server did not accept this sign-in. The activity log has the full detail.", FeedbackSeverity.Error), fixture.Shown[^1]);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.True(fixture.Flow.HasSession);
      Assert.Contains("ERROR Password sign-in not completed: QqqSessionException: The server did not accept this sign-in.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("wrong", StringComparison.Ordinal));
   }



   /// <summary>When the service answers but does not confirm the sign-in (for example the computer still has to be enrolled), its sentence is shown as an error and logged as one.</summary>
   [Fact]
   public async Task ServiceThatDoesNotConfirm_IsShownAsNotCompleted()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromResult("Signed in. Enroll this computer to enable printing.");

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      Assert.Equal(new FeedbackMessage("Service authorization not completed", "Signed in. Enroll this computer to enable printing.", FeedbackSeverity.Error), fixture.Shown[^1]);
      Assert.Contains("ERROR Service sign-in result: Signed in. Enroll this computer to enable printing.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
   }



   /// <summary>When the service cannot be reached for the hand-over, whatever the pipe failure (including the two kinds that used to escape the handler), the sign-in is reported as not completed with the failure's reason.</summary>
   [Theory]
   [MemberData(nameof(PipeFailures))]
   public async Task UnreachableService_IsReportedAsNotCompleted(Exception failure)
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromException<string>(failure);

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      Assert.Equal(new FeedbackMessage("Sign-in not completed", "The server did not authorize this connection, or the service could not confirm it. Reason: " + failure.Message + " The activity log has the full detail.", FeedbackSeverity.Error), fixture.Shown[^1]);
      Assert.Contains(fixture.Log.Entries, entry => entry.StartsWith("ERROR Password sign-in not completed: the service did not answer (", StringComparison.Ordinal));
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
   }



   /// <summary>The pipe failures of <see cref="UnreachableService_IsReportedAsNotCompleted"/>.</summary>
   public static TheoryData<Exception> PipeFailures() => new()
   {
      new IOException("Service connection closed before a complete response was received."),
      new OperationCanceledException("The pipe time limit ran out."),
      new InvalidDataException("Local pipe message exceeds the permitted size."),
   };



   /// <summary>When the session has no credential to export after all, the failure is reported like any other sign-in failure and nothing is sent to the service.</summary>
   [Fact]
   public async Task MissingCredential_IsReportedAndNothingIsSent()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Sessions[0].ExportFailure = new QqqSessionException(QqqSessionException.FailureKind.AuthenticationRequired, "Sign in first.");

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      Assert.Empty(fixture.Requests);
      Assert.Equal("Sign-in not completed", fixture.Shown[^1].Title);
      Assert.Contains("Reason: Sign in first.", fixture.Shown[^1].Message, StringComparison.Ordinal);
   }



   /// <summary>After a confirmed sign-in the session stays usable, so the operator can sign in again (for example with another pairing choice) without rediscovering.</summary>
   [Fact]
   public async Task AfterSigningIn_AnotherSignInIsPossible()
   {
      SignInFixture fixture = await DiscoveredAsync();
      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.StartNewPairing);

      Assert.Equal(2, fixture.Requests.Count);
      Assert.True(fixture.Requests[1].StartNewPairing);
      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);
   }
}
