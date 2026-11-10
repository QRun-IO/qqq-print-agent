/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.Service;
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
      fixture.Forget();
      return fixture;
   }



   /// <summary>Without a discovered session a sign-in does nothing: no request, no message, and the caller is told nothing was attempted.</summary>
   [Fact]
   public async Task WithoutDiscovery_NothingHappens()
   {
      SignInFixture fixture = new();

      Assert.False(await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent));

      Assert.Empty(fixture.Requests);
      Assert.Empty(fixture.Shown);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>
   /// A sign-in the server and the service both accept passes the trimmed user name and the password to the server and hands
   /// only the session credential to the service. Once the service confirms, its sentence is shown as success, the flow says
   /// printing is enabled, and the flow's own session is discarded with the methods it offered: the service is now the only
   /// process holding the session.
   /// </summary>
   [Fact]
   public async Task AcceptedSignIn_HandsTheSessionToTheServiceAndKeepsNone()
   {
      SignInFixture fixture = await DiscoveredAsync();

      Assert.True(await fixture.Flow.SignInWithPasswordAsync("  operator ", " pass word ", PairingChoice.KeepCurrent));

      Assert.Equal(("operator", " pass word "), fixture.Sessions[0].PasswordAttempt);
      PipeRequest request = Assert.Single(fixture.Requests);
      Assert.Equal("sign-in", request.Operation);
      Assert.Equal("synthetic-session-token", request.SessionCredential?.SessionToken);
      Assert.False(request.ClaimLegacyDevice);
      Assert.False(request.StartNewPairing);
      Assert.Equal([new FeedbackMessage("Signing in", "Checking the username and password with the server.", FeedbackSeverity.Informational),
         new FeedbackMessage("Signed in", "Signed in to QQQ. Printing is enabled.", FeedbackSeverity.Success)], fixture.Shown);
      Assert.Equal([SignInStage.SigningIn, SignInStage.HandingOver, SignInStage.SignedIn], fixture.Stages);
      Assert.Equal(SignInReadiness.PrintingEnabled, fixture.Flow.Readiness);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.PasswordOffered);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Equal(["INFO Password sign-in submitted for print.example.com.", "INFO Sign-in accepted by the server; handing the session to the service (keep the current pairing).",
         "INFO Service sign-in result: Signed in to QQQ. Printing is enabled."], fixture.Log.Entries);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("pass word", StringComparison.Ordinal) || entry.Contains("synthetic-session-token", StringComparison.Ordinal));
   }



   /// <summary>When the service takes over the session but this computer is not enrolled yet, that is still a confirmed sign-in, shown with the service's sentence, and the flow says enrollment is what is missing so a caller can lead there.</summary>
   [Fact]
   public async Task SignedInButNotEnrolled_IsConfirmedAndSaysEnrollmentIsNeeded()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromResult(SignInFixture.SignedInNeedsEnrollment);

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      Assert.Equal(new FeedbackMessage("Signed in", "Signed in to QQQ. Enroll this computer to enable printing.", FeedbackSeverity.Success), fixture.Shown[^1]);
      Assert.Equal(SignInReadiness.EnrollmentNeeded, fixture.Flow.Readiness);
      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);
      Assert.False(fixture.Flow.HasSession);
   }



   /// <summary>The pairing box maps to a choice by position: the second entry claims a legacy device, the third starts a new pairing, and the first, no selection or anything unexpected keeps the current pairing.</summary>
   [Theory]
   [InlineData(0, PairingChoice.KeepCurrent)]
   [InlineData(1, PairingChoice.ClaimLegacyDevice)]
   [InlineData(2, PairingChoice.StartNewPairing)]
   [InlineData(-1, PairingChoice.KeepCurrent)]
   [InlineData(7, PairingChoice.KeepCurrent)]
   public void PairingBox_MapsToAChoiceAndDefaultsToKeeping(int selectedIndex, PairingChoice expected)
      => Assert.Equal(expected, PairingSelection.FromIndex(selectedIndex));



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



   /// <summary>When the server refuses the credentials nothing is handed to the service, and the message gives the reason without saying which part was wrong; the session and the password form stay for another attempt.</summary>
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
      Assert.True(fixture.Flow.PasswordOffered);
      Assert.Null(fixture.Flow.Readiness);
      Assert.Contains("ERROR Password sign-in not completed: QqqSessionException: The server did not accept this sign-in.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("wrong", StringComparison.Ordinal));
   }



   /// <summary>When the service answers the hand-over with an error, its reply is shown whole as an error and logged as one, and the session stays for another attempt.</summary>
   [Fact]
   public async Task ServiceThatRefusesTheSession_IsShownAsNotCompleted()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromResult("Error: The session belongs to another user.");

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      Assert.Equal(new FeedbackMessage("Service authorization not completed", "Error: The session belongs to another user.", FeedbackSeverity.Error), fixture.Shown[^1]);
      Assert.Contains("ERROR Service sign-in result: Error: The session belongs to another user.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.True(fixture.Flow.HasSession);
      Assert.Null(fixture.Flow.Readiness);
   }



   /// <summary>
   /// When the hand-over never left this computer (nothing is listening, the service was not verified), nothing was
   /// handed over: the sign-in is reported as not completed with the failure's reason, the log says nothing was sent and
   /// keeps the full exception as debug detail, and the session stays for another attempt.
   /// </summary>
   [Theory]
   [MemberData(nameof(NotSentFailures))]
   public async Task HandOverThatNeverLeft_IsReportedAsNotCompletedAndTheSessionStays(Exception failure, ServiceFailure cause)
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromException<string>(failure);

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      Assert.Equal(new FeedbackMessage("Sign-in not completed", "The server did not authorize this connection, or the service could not confirm it. Reason: " + failure.Message + " The activity log has the full detail.", FeedbackSeverity.Error), fixture.Shown[^1]);
      Assert.Contains("ERROR Password sign-in not completed: the service did not answer (" + cause + ", nothing sent).", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains(fixture.Log.Entries, entry => entry.StartsWith("DEBUG Service request 'sign-in' failure detail: ", StringComparison.Ordinal));
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.True(fixture.Flow.HasSession);
      Assert.False(fixture.Sessions[0].Disposed);
   }



   /// <summary>The failures of <see cref="HandOverThatNeverLeft_IsReportedAsNotCompletedAndTheSessionStays"/>, after which the service certainly holds nothing.</summary>
   public static TheoryData<Exception, ServiceFailure> NotSentFailures() => new()
   {
      { new AgentPipeNotSentException(new OperationCanceledException("Nothing is listening on the pipe.")), ServiceFailure.TimedOut },
      { new AgentPipeNotSentException(new UnauthorizedAccessException("The local endpoint is not the installed Windows print service.")), ServiceFailure.AccessDenied },
   };



   /// <summary>
   /// When the hand-over left this computer and no usable answer came back, the service may have signed in all the same.
   /// The operator is not told the sign-in failed: the message says it could not be confirmed, what really happened to the
   /// request, and to check the status before trying again. The flow discards its session, because the service may now
   /// hold it, so the companion never keeps a session next to the service's.
   /// </summary>
   [Theory]
   [MemberData(nameof(UnconfirmedFailures))]
   public async Task HandOverThatLeftWithoutAnAnswer_IsUnconfirmedAndTheSessionIsDiscarded(Exception failure, string cause)
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromException<string>(failure);

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      Assert.Equal(new FeedbackMessage("Sign-in could not be confirmed", cause + " The sign-in was sent to the service, which may still complete it. Check the connection status before trying again; if it does not show this account as signed in, choose Discover sign-in options and sign in again. The activity log has the full detail.", FeedbackSeverity.Warning), fixture.Shown[^1]);
      Assert.Equal("Signing in > Sign-in could not be confirmed", fixture.Titles);
      Assert.Contains("ERROR The hand-over to the service could not be confirmed, so the service may or may not have signed in; the attempt was discarded: " + cause, fixture.Log.Entries, StringComparer.Ordinal);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.PasswordOffered);
      Assert.Null(fixture.Flow.Readiness);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>The failures of <see cref="HandOverThatLeftWithoutAnAnswer_IsUnconfirmedAndTheSessionIsDiscarded"/> and the sentence that says what happened for each: a refusal or a failed Windows call that arrives without the "not sent" mark is treated as possibly sent, and its reason is quoted.</summary>
   public static TheoryData<Exception, string> UnconfirmedFailures() => new()
   {
      { new OperationCanceledException("The reply did not arrive in time."), "The request timed out." },
      { new IOException("Service connection closed before a complete response was received."), "The connection to the service closed before its answer arrived." },
      { new InvalidDataException("Local pipe message exceeds the permitted size."), "The service's answer was larger than the companion accepts." },
      { new UnauthorizedAccessException("Access to the path is denied."), "The service's answer did not arrive. Reason: Access to the path is denied." },
   };



   /// <summary>An answer to the hand-over that cannot be understood is unconfirmed too, and the message says the service did answer.</summary>
   [Fact]
   public async Task HandOverAnsweredUnreadably_IsUnconfirmed()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromException<string>(new System.Text.Json.JsonException("The reply was not what the operation answers."));

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      Assert.Equal("Sign-in could not be confirmed", fixture.Shown[^1].Title);
      Assert.StartsWith("The service answered, but its answer could not be read. The sign-in was sent to the service, which may still complete it.", fixture.Shown[^1].Message, StringComparison.Ordinal);
      Assert.False(fixture.Flow.HasSession);
   }



   /// <summary>
   /// The server's own time limit is a failure like any other and is shown as "not completed" with its reason. It is a
   /// cancellation nobody here asked for, so it must not be mistaken for the caller ending the wait, which ends quietly.
   /// </summary>
   [Fact]
   public async Task ServerTimeLimit_IsShownAsNotCompleted()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Sessions[0].PasswordFailure = new TaskCanceledException("The server did not answer in time.");

      Assert.True(await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent));

      Assert.Empty(fixture.Requests);
      Assert.Equal(new FeedbackMessage("Sign-in not completed", "The server did not authorize this connection, or the service could not confirm it. Reason: The server did not answer in time. The activity log has the full detail.", FeedbackSeverity.Error), fixture.Shown[^1]);
      Assert.Contains("ERROR Password sign-in not completed: TaskCanceledException: The server did not answer in time.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.True(fixture.Flow.HasSession);
   }



   /// <summary>A session that signed in but cannot give up its credential is reported like any other sign-in failure, and nothing is sent to the service.</summary>
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



   /// <summary>After a confirmed sign-in the flow holds no session, so another sign-in does nothing until the server is discovered again.</summary>
   [Fact]
   public async Task AfterSigningIn_AnotherAttemptNeedsDiscovery()
   {
      SignInFixture fixture = await DiscoveredAsync();
      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);

      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.StartNewPairing);
      Assert.Single(fixture.Requests);
      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);

      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.StartNewPairing);
      Assert.Equal(2, fixture.Requests.Count);
      Assert.True(fixture.Requests[1].StartNewPairing);
      Assert.Equal(2, fixture.Sessions.Count);
   }



   /// <summary>Cancelling while the server checks the password ends the attempt quietly: nothing is handed over, no failure is shown, and the session stays.</summary>
   [Fact]
   public async Task CancelledWhileTheServerChecks_EndsQuietly()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Sessions[0].PendingPassword = new TaskCompletionSource();
      using CancellationTokenSource cancellation = new();

      Task<bool> running = fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent, cancellation.Token);
      Assert.Equal(SignInStage.SigningIn, fixture.Flow.Stage);
      await cancellation.CancelAsync();

      Assert.True(await SignInFixture.EndsAsync(running));
      Assert.Empty(fixture.Requests);
      Assert.Equal("Signing in", Assert.Single(fixture.Shown).Title);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.True(fixture.Flow.HasSession);
      Assert.Equal("DEBUG Password sign-in was cancelled by its caller (TaskCanceledException).", fixture.Log.Entries[^1]);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.StartsWith("WARN", StringComparison.Ordinal) || entry.StartsWith("ERROR", StringComparison.Ordinal));
   }



   /// <summary>
   /// Cancelling while the service is taking over the session ends the wait, not the hand-over: the request has left, so
   /// the service may still sign in. Neither success nor failure is claimed: the operator is told the sign-in could not
   /// be confirmed because the wait was ended, and the session is discarded because the service may hold it.
   /// </summary>
   [Fact]
   public async Task CancelledDuringTheHandOver_IsUnconfirmedAndTheSessionIsDiscarded()
   {
      SignInFixture fixture = await DiscoveredAsync();
      (Task reached, _) = fixture.HoldService();
      using CancellationTokenSource cancellation = new();

      Task<bool> running = fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent, cancellation.Token);
      await reached;
      Assert.Equal(SignInStage.HandingOver, fixture.Flow.Stage);
      await cancellation.CancelAsync();

      Assert.True(await SignInFixture.EndsAsync(running));
      Assert.Equal("Signing in > Sign-in could not be confirmed", fixture.Titles);
      Assert.StartsWith("The wait for the service's answer was ended. The sign-in was sent to the service, which may still complete it.", fixture.Shown[^1].Message, StringComparison.Ordinal);
      Assert.Contains("ERROR The hand-over to the service could not be confirmed, so the service may or may not have signed in; the attempt was discarded: The wait for the service's answer was ended.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.Null(fixture.Flow.Readiness);
   }
}
