/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>Browser sign-in: the exchange through the default browser, its time limit and cancellation, and what is reported when it does not complete.</summary>
public sealed class SignInFlowBrowserTests
{
   private static readonly Uri s_callback = new(SignInFixture.Callback + "?code=one-time-code&state=secret-state");



   /// <summary>Builds a fixture whose flow has discovered a server offering the browser grant for this application's callback, and password sign-in when asked.</summary>
   private static async Task<SignInFixture> DiscoveredAsync(bool password = false, string redirect = SignInFixture.Callback)
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.Metadata = SignInFixture.Offers(password, redirect) };
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Shown.Clear();
      fixture.Stages.Clear();
      fixture.Log.Entries.Clear();
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
   }



   /// <summary>
   /// The exchange opens the inbox before the browser, opens the provider page, waits (cancellable, with a three minute limit),
   /// completes the login with the callback that arrived, hands the session to the service and closes the inbox. Only the
   /// provider's host is logged, never the state or the code.
   /// </summary>
   [Fact]
   public async Task CompletedExchange_SignsInAndLogsNoSecrets()
   {
      SignInFixture fixture = await DiscoveredAsync();

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.ClaimLegacyDevice);
      Assert.True(fixture.Flow.BrowserPending);
      Assert.Equal(SignInStage.WaitingForBrowser, fixture.Flow.Stage);
      FakeInbox inbox = Assert.Single(fixture.Inboxes);
      Assert.True(inbox.Waiting);
      Assert.Equal(fixture.Sessions[0].Authorization, Assert.Single(fixture.Opened));
      inbox.Deliver(s_callback);
      await running;

      Assert.Equal(new Uri(SignInFixture.Callback), fixture.Sessions[0].Redirect);
      Assert.Equal(s_callback, fixture.Sessions[0].Callback);
      Assert.True(Assert.Single(fixture.Requests).ClaimLegacyDevice);
      Assert.True(inbox.Disposed);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);
      Assert.Equal([SignInStage.WaitingForBrowser, SignInStage.HandingOver, SignInStage.SignedIn], fixture.Stages);
      Assert.Equal([new FeedbackMessage("Waiting for the browser", "Finish signing in on the page that opened in your browser. This dialog updates when the sign-in completes, is canceled or times out after three minutes.", FeedbackSeverity.Informational),
         new FeedbackMessage("Signed in", "Signed in to QQQ. Printing is ready.", FeedbackSeverity.Success)], fixture.Shown);
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



   /// <summary>A server whose grant vanished between discovery and the click (no native grant at all) is reported the same way, with an empty list in the log.</summary>
   [Fact]
   public async Task ServerWithoutAGrant_IsReportedAsNotConfigured()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);

      await fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);

      Assert.Equal("Browser sign-in not completed", fixture.Shown[^1].Title);
      Assert.Contains(fixture.Log.Entries, entry => entry.Contains("it lists []", StringComparison.Ordinal));
   }



   /// <summary>Cancelling while waiting discards the session, withdraws the browser button (the password form stays as it was), tells the operator to discover again, and copies the protocol handler's trace into the log.</summary>
   [Fact]
   public async Task Cancelling_DiscardsTheAttempt()
   {
      SignInFixture fixture = await DiscoveredAsync(password: true);
      fixture.Trace.AddRange(["10:00:01 handler started", "10:00:02 forwarded"]);

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Flow.CancelBrowserSignIn();
      await running;

      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.BrowserOffered);
      Assert.True(fixture.Flow.PasswordOffered);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.Empty(fixture.Requests);
      Assert.True(fixture.Inboxes[0].Disposed);
      Assert.Equal(new FeedbackMessage("Browser sign-in canceled", "No new sign-in was accepted. Choose Discover sign-in options to start a new attempt.", FeedbackSeverity.Informational), fixture.Shown[^1]);
      Assert.Contains("INFO Browser sign-in cancelled or timed out (three minute limit) before a callback arrived.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("WARN Protocol handler trace from trace/auth-callback.log:" + Environment.NewLine + "10:00:01 handler started" + Environment.NewLine + "10:00:02 forwarded", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>When no callback arrives within three minutes the attempt ends exactly like a cancellation.</summary>
   [Fact]
   public async Task ThreeMinutesWithoutACallback_EndsTheAttempt()
   {
      SignInFixture fixture = await DiscoveredAsync();

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Time.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1));
      Assert.True(fixture.Flow.BrowserPending);
      fixture.Time.Advance(TimeSpan.FromSeconds(1));
      await running;

      Assert.Equal("Browser sign-in canceled", fixture.Shown[^1].Title);
      Assert.False(fixture.Flow.HasSession);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
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
      Assert.False(fixture.Flow.BrowserPending);
      fixture.Flow.CancelBrowserSignIn();
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>A failure anywhere in the exchange (the inbox cannot be opened, the server refuses to begin, the browser cannot be started, the callback cannot be received or validated) is reported as not completed with its reason, and the session stays for an explicit retry.</summary>
   [Theory]
   [InlineData("inbox")]
   [InlineData("begin")]
   [InlineData("browser")]
   [InlineData("receive")]
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
         _ => new HttpRequestException("The server rejected the callback."),
      };
      fixture.InboxFailure = string.Equals(step, "inbox", StringComparison.Ordinal) ? failure : null;
      fixture.BrowserFailure = string.Equals(step, "browser", StringComparison.Ordinal) ? failure : null;
      fixture.Sessions[0].BeginFailure = string.Equals(step, "begin", StringComparison.Ordinal) ? failure : null;
      fixture.Sessions[0].CompleteFailure = string.Equals(step, "complete", StringComparison.Ordinal) ? failure : null;

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      if(string.Equals(step, "receive", StringComparison.Ordinal))
      {
         fixture.Inboxes[0].Fail(failure);
      }
      else if(string.Equals(step, "complete", StringComparison.Ordinal))
      {
         fixture.Inboxes[0].Deliver(s_callback);
      }
      await running;

      Assert.Equal(new FeedbackMessage("Browser sign-in not completed", "No validated account session was received. Reason: " + failure.Message + " Check the server's advertised native sign-in grant and installed callback configuration, then try again explicitly. The activity log has the full detail.", FeedbackSeverity.Warning), fixture.Shown[^1]);
      Assert.Contains("ERROR Browser sign-in not completed: " + failure.GetType().Name + ": " + failure.Message, fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Empty(fixture.Requests);
      Assert.True(fixture.Flow.HasSession);
      Assert.True(fixture.Flow.BrowserOffered);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.All(fixture.Inboxes, inbox => Assert.True(inbox.Disposed));
   }



   /// <summary>When the service answers the hand-over without confirming, its sentence is shown as an error and the session stays.</summary>
   [Fact]
   public async Task ServiceThatDoesNotConfirm_IsShownAsNotCompleted()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromResult("Error: The session belongs to another user.");

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Inboxes[0].Deliver(s_callback);
      await running;

      Assert.Equal(new FeedbackMessage("Service authorization not completed", "Error: The session belongs to another user.", FeedbackSeverity.Error), fixture.Shown[^1]);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.True(fixture.Flow.HasSession);
   }



   /// <summary>When the hand-over to the service times out, the browser sign-in is ended like a cancellation, as it always has been: the session is discarded and discovery must be run again.</summary>
   [Fact]
   public async Task HandOverThatTimesOut_EndsLikeACancellation()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromException<string>(new OperationCanceledException("The pipe time limit ran out."));

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Inboxes[0].Deliver(s_callback);
      await running;

      Assert.Equal("Browser sign-in canceled", fixture.Shown[^1].Title);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.BrowserOffered);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>When the service cannot be reached for the hand-over, the sign-in is reported as not completed with the failure's reason and the handler trace is logged.</summary>
   [Fact]
   public async Task UnreachableServiceAtHandOver_IsReportedAsNotCompleted()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Service = _ => Task.FromException<string>(new InvalidDataException("Local pipe message exceeds the permitted size."));

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      fixture.Inboxes[0].Deliver(s_callback);
      await running;

      Assert.Equal("Browser sign-in not completed", fixture.Shown[^1].Title);
      Assert.Contains("Reason: Local pipe message exceeds the permitted size.", fixture.Shown[^1].Message, StringComparison.Ordinal);
      Assert.Contains("ERROR Browser sign-in not completed: the service did not answer (InvalidData).", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains(fixture.Log.Entries, entry => entry.StartsWith("WARN The browser never launched the sign-in protocol handler", StringComparison.Ordinal));
      Assert.True(fixture.Flow.HasSession);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
   }



   /// <summary>While a browser sign-in is waiting, the flow starts nothing else: a password sign-in, a second browser sign-in and a new discovery are all ignored.</summary>
   [Fact]
   public async Task WhileWaiting_NoOtherOperationStarts()
   {
      SignInFixture fixture = await DiscoveredAsync(password: true);

      Task running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
      await fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      await fixture.Flow.DiscoverAsync("https://other.example.com");

      Assert.Null(fixture.Sessions[0].PasswordAttempt);
      Assert.Single(fixture.Inboxes);
      Assert.Single(fixture.Sessions);
      Assert.True(fixture.Flow.PasswordOffered);
      Assert.Equal(SignInStage.WaitingForBrowser, fixture.Flow.Stage);
      fixture.Inboxes[0].Deliver(s_callback);
      await running;
      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);
   }
}
