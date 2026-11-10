/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>Browser sign-in: the exchange through the default browser, its time limit and cancellation, and what is reported when it does not complete.</summary>
public sealed class SignInFlowBrowserTests
{
   /// <summary>Builds a fixture whose flow has discovered a server offering the browser grant for this application's callback, and password sign-in when asked.</summary>
   private static async Task<SignInFixture> DiscoveredAsync(bool password = false, string redirect = SignInFixture.Callback)
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.Metadata = SignInFixture.Offers(password, redirect) };
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Forget();
      return fixture;
   }



   /// <summary>Without a discovered session a browser sign-in does nothing.</summary>
   [Fact]
   public async Task WithoutDiscovery_NothingHappens()
   {
      SignInFixture fixture = new();

      await fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);

      Assert.Empty(fixture.Inboxes);
      Assert.Empty(fixture.Opened);
      Assert.Empty(fixture.Shown);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.False(fixture.Flow.IsBusy);
   }



   /// <summary>
   /// The exchange opens the inbox before the browser, opens the provider page, waits (cancellable, with a three minute limit),
   /// completes the login with the callback that arrived, hands the session to the service and closes the inbox. Once the
   /// service confirms, the flow keeps no session. Only the provider's host is logged, never the state or the code.
   /// </summary>
   [Fact]
   public async Task CompletedExchange_SignsInAndLogsNoSecrets()
   {
      SignInFixture fixture = await DiscoveredAsync();
      FakeSession session = fixture.Sessions[0];

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.ClaimLegacyDevice);
      Assert.True(fixture.Flow.BrowserPending);
      Assert.True(fixture.Flow.IsBusy);
      Assert.Equal(SignInStage.WaitingForBrowser, fixture.Flow.Stage);
      FakeInbox inbox = Assert.Single(fixture.Inboxes);
      Assert.True(inbox.Waiting);
      Assert.Equal(session.Authorization, Assert.Single(fixture.Opened));
      inbox.Deliver(session.CallbackForLatestAttempt);
      await running;

      Assert.Equal(new Uri(SignInFixture.Callback), session.Redirect);
      Assert.Equal(session.CallbackForLatestAttempt, session.Callback);
      Assert.True(Assert.Single(fixture.Requests).ClaimLegacyDevice);
      Assert.True(inbox.Disposed);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);
      Assert.Equal(SignInReadiness.PrintingEnabled, fixture.Flow.Readiness);
      Assert.True(session.Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.BrowserOffered);
      Assert.Equal([SignInStage.WaitingForBrowser, SignInStage.HandingOver, SignInStage.SignedIn], fixture.Stages);
      Assert.Equal([new FeedbackMessage("Waiting for the browser", "Finish signing in on the page that opened in your browser. This dialog updates when the sign-in completes, is canceled or times out after three minutes.", FeedbackSeverity.Informational),
         new FeedbackMessage("Signed in", "Signed in to QQQ. Printing is enabled.", FeedbackSeverity.Success)], fixture.Shown);
      Assert.Equal(TimeSpan.FromMinutes(3), Assert.Single(fixture.Time.RequestedDelays));
      Assert.Equal(TimeSpan.FromMinutes(3), SignInFlow.BrowserSignInLimit);
      Assert.Contains("INFO Browser sign-in requested for print.example.com; this application's callback is io.qrun.qqq-print-agent:/oauth/callback.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("INFO Browser callback inbox open; starting the sign-in attempt.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("INFO Opening the identity provider's sign-in page in the default browser (id.example.com).", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("INFO Browser callback received; completing the sign-in.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("secret-state", StringComparison.Ordinal) || entry.Contains("one-time-code", StringComparison.Ordinal));
   }



   /// <summary>A server that does not list this application's callback is never sent to the browser: the sign-in is reported as not completed, and the log names the callbacks the server does list.</summary>
   [Fact]
   public async Task ServerThatDoesNotListTheCallback_IsNotOpenedInTheBrowser()
   {
      SignInFixture fixture = await DiscoveredAsync(redirect: "com.example.other:/callback");

      await fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);

      Assert.Empty(fixture.Inboxes);
      Assert.Empty(fixture.Opened);
      Assert.Equal(new FeedbackMessage("Browser sign-in not completed", "No validated account session was received. Reason: No approved native browser callback is configured. Check the server's advertised native sign-in grant and installed callback configuration, then try again explicitly. The activity log has the full detail.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
      Assert.Contains("WARN The server's native grant does not list this application's callback io.qrun.qqq-print-agent:/oauth/callback; it lists [com.example.other:/callback]. Add the callback to the QQQ native session grant.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("WARN The browser never launched the sign-in protocol handler in the last 10 minutes (no entries in trace/auth-callback.log). Either the identity provider did not redirect to io.qrun.qqq-print-agent:/oauth/callback (check the provider's allowed redirect URIs and any error page it showed), or the browser blocked opening the QQQ Print Agent sign-in link.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.True(fixture.Flow.BrowserOffered);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
   }



   /// <summary>A server without any native grant, reached through a browser click that should not have been possible, is reported the same way, with an empty list in the log.</summary>
   [Fact]
   public async Task ServerWithoutAGrant_IsReportedAsNotConfigured()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);

      await fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);

      Assert.Equal("Browser sign-in not completed", fixture.Shown[^1].Title);
      Assert.Contains(fixture.Log.Entries, entry => entry.Contains("it lists []", StringComparison.Ordinal));
   }



   /// <summary>Cancelling while waiting for the browser discards the session and everything it offered, tells the operator to discover again, and copies the protocol handler's trace into the log.</summary>
   [Fact]
   public async Task Cancelling_DiscardsTheAttemptAndWhatItOffered()
   {
      SignInFixture fixture = await DiscoveredAsync(password: true);
      fixture.Trace.AddRange(["10:00:01 handler started", "10:00:02 forwarded"]);

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Flow.CancelBrowserSignIn();
      await running;

      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.BrowserOffered);
      Assert.False(fixture.Flow.PasswordOffered);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.Empty(fixture.Requests);
      Assert.True(fixture.Inboxes[0].Disposed);
      Assert.Equal(new FeedbackMessage("Browser sign-in canceled", "No new sign-in was accepted. Choose Discover sign-in options to start a new attempt.", FeedbackSeverity.Informational), fixture.Shown[^1]);
      Assert.Contains("INFO Browser sign-in cancelled or timed out (three minute limit) before a callback arrived.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("WARN Protocol handler trace from trace/auth-callback.log:" + Environment.NewLine + "10:00:01 handler started" + Environment.NewLine + "10:00:02 forwarded", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>When no callback arrives within three minutes, or the caller's token is cancelled, the attempt ends exactly like a cancellation.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task TimeLimitOrCallerCancellation_EndsTheAttempt(bool byLimit)
   {
      SignInFixture fixture = await DiscoveredAsync();
      using CancellationTokenSource cancellation = new();

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent, cancellation.Token);
      fixture.Time.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1));
      Assert.True(fixture.Flow.BrowserPending);
      if(byLimit)
      {
         fixture.Time.Advance(TimeSpan.FromSeconds(1));
      }
      else
      {
         await cancellation.CancelAsync();
      }
      await running;

      Assert.Equal("Browser sign-in canceled", fixture.Shown[^1].Title);
      Assert.False(fixture.Flow.HasSession);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>When the wait is cancelled after the callback arrived, while the server is completing the login, the attempt is discarded the same way and the log says the callback had arrived.</summary>
   [Fact]
   public async Task CancelledAfterTheCallbackArrived_SaysSoInTheLog()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Sessions[0].CompleteFailure = new OperationCanceledException("The completion was cancelled.");

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
      await running;

      Assert.Equal("Browser sign-in canceled", fixture.Shown[^1].Title);
      Assert.Contains("INFO Browser sign-in cancelled or timed out (three minute limit) after the callback arrived, before the sign-in was completed.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("before a callback arrived", StringComparison.Ordinal));
      Assert.Empty(fixture.Requests);
   }



   /// <summary>Disposing the flow while a browser sign-in waits cancels it and discards the session, so nothing keeps listening after the window closed.</summary>
   [Fact]
   public async Task Disposing_CancelsAWaitingSignIn()
   {
      SignInFixture fixture = await DiscoveredAsync();

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Flow.Dispose();
      await running;

      Assert.True(fixture.Sessions[0].Disposed);
      Assert.True(fixture.Inboxes[0].Disposed);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.False(fixture.Flow.IsBusy);
      fixture.Flow.CancelBrowserSignIn();
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>
   /// A failure anywhere in the exchange (the inbox cannot be opened, the server refuses to begin, the browser cannot be started,
   /// the inbox breaks, a malformed or oversize callback is written to the inbox, the server rejects the callback) is reported as
   /// not completed with its reason and never escapes the flow; the session stays for an explicit retry.
   /// </summary>
   [Theory]
   [InlineData("inbox")]
   [InlineData("begin")]
   [InlineData("browser")]
   [InlineData("receive")]
   [InlineData("malformed")]
   [InlineData("complete")]
   public async Task FailureDuringTheExchange_IsReportedWithItsReason(string step)
   {
      SignInFixture fixture = await DiscoveredAsync();
      Exception failure = step switch
      {
         "inbox" => new UnauthorizedAccessException("The callback inbox could not be opened."),
         "begin" => new InvalidOperationException("The server does not offer the browser flow."),
         "browser" => new Win32Exception(2, "The system cannot find the file specified."),
         "receive" => new IOException("The callback inbox closed."),
         "malformed" => new InvalidDataException("The browser callback is not a valid callback address."),
         _ => new HttpRequestException("The server rejected the callback."),
      };
      fixture.InboxFailure = string.Equals(step, "inbox", StringComparison.Ordinal) ? failure : null;
      fixture.BrowserFailure = string.Equals(step, "browser", StringComparison.Ordinal) ? failure : null;
      fixture.Sessions[0].BeginFailure = string.Equals(step, "begin", StringComparison.Ordinal) ? failure : null;
      fixture.Sessions[0].CompleteFailure = string.Equals(step, "complete", StringComparison.Ordinal) ? failure : null;

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      if(step is "receive" or "malformed")
      {
         fixture.Inboxes[0].Fail(failure);
      }
      else if(string.Equals(step, "complete", StringComparison.Ordinal))
      {
         fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
      }
      await running;

      Assert.Equal(new FeedbackMessage("Browser sign-in not completed", "No validated account session was received. Reason: " + failure.Message + " Check the server's advertised native sign-in grant and installed callback configuration, then try again explicitly. The activity log has the full detail.", FeedbackSeverity.Warning), fixture.Shown[^1]);
      Assert.Contains("ERROR Browser sign-in not completed: " + failure.GetType().Name + ": " + failure.Message, fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Empty(fixture.Requests);
      Assert.True(fixture.Flow.HasSession);
      Assert.True(fixture.Flow.BrowserOffered);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.All(fixture.Inboxes, inbox => Assert.True(inbox.Disposed));
   }



   /// <summary>After a failed attempt the session can be used for a second one, which begins a new login of its own; a callback left over from the first attempt is refused by the server, and the callback of the second attempt signs in.</summary>
   [Fact]
   public async Task SecondAttemptOnTheSameSession_RefusesTheFirstAttemptsCallback()
   {
      SignInFixture fixture = await DiscoveredAsync();
      FakeSession session = fixture.Sessions[0];
      Task first = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      Uri stale = session.CallbackForLatestAttempt;
      fixture.Inboxes[0].Fail(new IOException("The callback inbox closed."));
      await first;

      Task second = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Inboxes[1].Deliver(stale);
      await second;

      Assert.Equal("Browser sign-in not completed", fixture.Shown[^1].Title);
      Assert.Contains("Reason: The callback does not belong to this sign-in attempt.", fixture.Shown[^1].Message, StringComparison.Ordinal);
      Assert.Empty(fixture.Requests);
      Assert.Equal(2, fixture.Opened.Count);
      Assert.NotEqual(fixture.Opened[0], fixture.Opened[1]);

      Task third = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Inboxes[2].Deliver(session.CallbackForLatestAttempt);
      await third;

      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);
      Assert.Single(fixture.Requests);
   }



   /// <summary>When the service answers the hand-over with an error, its reply is shown whole as an error and the session stays.</summary>
   [Fact]
   public async Task ServiceThatRefusesTheSession_IsShownAsNotCompleted()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromResult("Error: The session belongs to another user.");

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
      await running;

      Assert.Equal(new FeedbackMessage("Service authorization not completed", "Error: The session belongs to another user.", FeedbackSeverity.Error), fixture.Shown[^1]);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.True(fixture.Flow.HasSession);
   }



   /// <summary>
   /// The Cancel button and the three minute limit belong to the wait for the browser. Once the callback arrived and the
   /// session is being handed to the service, neither interrupts the hand-over: the sign-in completes.
   /// </summary>
   [Fact]
   public async Task CancelAndTheTimeLimitDuringTheHandOver_DoNotInterruptIt()
   {
      SignInFixture fixture = await DiscoveredAsync();
      (Task reached, TaskCompletionSource<string> reply) = fixture.HoldService();

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
      await reached;
      Assert.Equal(SignInStage.HandingOver, fixture.Flow.Stage);
      Assert.True(fixture.Flow.BrowserPending);
      fixture.Flow.CancelBrowserSignIn();
      fixture.Time.Advance(TimeSpan.FromMinutes(5));
      reply.SetResult(SignInFixture.SignedInAndPrinting);
      await running;

      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);
      Assert.Equal("Waiting for the browser > Signed in", fixture.Titles);
      Assert.False(fixture.Flow.BrowserPending);
   }



   /// <summary>Disposing the flow, or cancelling the caller's token, during the hand-over ends the wait without claiming anything: no "canceled" or "not completed" message, and the log says the service may still complete the sign-in.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task CallerEndingTheWaitDuringTheHandOver_ClaimsNothing(bool byToken)
   {
      SignInFixture fixture = await DiscoveredAsync();
      (Task reached, _) = fixture.HoldService();
      using CancellationTokenSource cancellation = new();

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent, cancellation.Token);
      fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
      await reached;
      if(byToken)
      {
         await cancellation.CancelAsync();
      }
      else
      {
         fixture.Flow.Dispose();
      }
      await running;

      Assert.Equal("Waiting for the browser", Assert.Single(fixture.Shown).Title);
      Assert.Contains("INFO The hand-over to the service is no longer waited for; the service may still complete the sign-in.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.False(fixture.Flow.IsBusy);
   }



   /// <summary>
   /// When the hand-over to the service runs into a time limit, the browser sign-in is ended like a cancellation, as it always
   /// has been, and discovery must be run again. The log no longer claims that no callback arrived: it says the browser part
   /// had completed and whether the request reached the service at all.
   /// </summary>
   [Theory]
   [InlineData(true, "INFO The hand-over to the service timed out after the browser sign-in completed; whether the service signed in is not confirmed, and the attempt was discarded.")]
   [InlineData(false, "INFO The service did not accept a connection for the hand-over after the browser sign-in completed; nothing was handed over, and the attempt was discarded.")]
   public async Task HandOverThatTimesOut_EndsLikeACancellationAndTheLogSaysWhatHappened(bool sent, string entry)
   {
      SignInFixture fixture = await DiscoveredAsync();
      OperationCanceledException limit = new("The time limit ran out.");
      fixture.Service = _ => Task.FromException<string>(sent ? limit : new AgentPipeNotSentException(limit));

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
      await running;

      Assert.Equal("Browser sign-in canceled", fixture.Shown[^1].Title);
      Assert.Contains(entry, fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, candidate => candidate.Contains("before a callback arrived", StringComparison.Ordinal));
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.BrowserOffered);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>When the service gives no usable answer to the hand-over for another reason, the sign-in is reported as not completed with the failure's reason, the log says whether the request had left, and the handler trace is logged.</summary>
   [Fact]
   public async Task UnansweredHandOver_IsReportedAsNotCompleted()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromException<string>(new InvalidDataException("Local pipe message exceeds the permitted size."));

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
      await running;

      Assert.Equal("Browser sign-in not completed", fixture.Shown[^1].Title);
      Assert.Contains("Reason: Local pipe message exceeds the permitted size.", fixture.Shown[^1].Message, StringComparison.Ordinal);
      Assert.Contains("ERROR Browser sign-in not completed: the service did not answer (InvalidData, request sent).", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains(fixture.Log.Entries, entry => entry.StartsWith("DEBUG Service request 'sign-in' failure detail: System.IO.InvalidDataException", StringComparison.Ordinal));
      Assert.Contains(fixture.Log.Entries, entry => entry.StartsWith("WARN The browser never launched the sign-in protocol handler", StringComparison.Ordinal));
      Assert.True(fixture.Flow.HasSession);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
   }



   /// <summary>
   /// While a browser sign-in is running, the flow starts nothing else: a password sign-in, a second browser sign-in, a new
   /// discovery, a sign-out, an enrollment and both certificate-exception operations are refused without a request, and a
   /// refused change of the exception announces the saved state again so the checkbox goes back.
   /// </summary>
   [Fact]
   public async Task WhileRunning_EveryOtherOperationIsRefused()
   {
      SignInFixture fixture = await DiscoveredAsync(password: true);

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
      await fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      await fixture.Flow.DiscoverAsync("https://other.example.com");
      await fixture.Flow.LoadTrustAsync();
      bool signOut = await fixture.Flow.SignOutAsync();
      bool trust = await fixture.Flow.SetTrustAsync(allow: true, "https://dev.example.com");
      EnrollmentResult enrollment = await fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123");

      Assert.True(signOut);
      Assert.True(trust);
      Assert.Equal(new EnrollmentResult(null, ServiceReachable: true), enrollment);
      Assert.Equal(1, fixture.TrustAnnouncements);
      Assert.Equal(new ServerTrust(), fixture.Flow.Trust);
      Assert.Empty(fixture.Requests);
      Assert.Null(fixture.Sessions[0].PasswordAttempt);
      Assert.Single(fixture.Inboxes);
      Assert.Single(fixture.Sessions);
      Assert.True(fixture.Flow.PasswordOffered);
      Assert.Equal("Waiting for the browser", fixture.Titles);
      Assert.Equal(SignInStage.WaitingForBrowser, fixture.Flow.Stage);
      fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
      await running;
      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);
   }
}
