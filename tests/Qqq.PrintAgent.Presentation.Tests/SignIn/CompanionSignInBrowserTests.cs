/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>The companion's browser sign-in: the exchange through the default browser and the one callback dispatcher, its time limit and cancellation, what is reported when it does not complete, and the thread everything is announced on.</summary>
public sealed class CompanionSignInBrowserTests
{
   private const string Requested = "INFO Browser sign-in requested for print.example.com; this application's callback is io.qrun.qqq-print-agent:/oauth/callback.";
   private const string NeverLaunched = "WARN The browser never launched the sign-in protocol handler in the last 10 minutes (no entries in trace/auth-callback.log). Either the identity provider did not redirect to io.qrun.qqq-print-agent:/oauth/callback (check the provider's allowed redirect URIs and any error page it showed), or the browser blocked opening the QQQ Print Agent sign-in link.";



   /// <summary>True when an entry holds something that must never be logged: a sign-in state, a one-time code or a session token.</summary>
   private static bool Secret(string entry)
      => entry.Contains("synthetic-state", StringComparison.Ordinal) || entry.Contains("one-time-code", StringComparison.Ordinal) || entry.Contains("synthetic-session-token", StringComparison.Ordinal);



   /// <summary>
   /// The exchange begins the sign-in on the session with this application's registered callback, has an inbox open
   /// before the provider's page is opened, waits under a three minute limit, completes the sign-in with the callback
   /// that carries this attempt's state, closes the inbox and returns the credential. The session stays. Only the
   /// provider's host is logged: no state, no code and no token.
   /// </summary>
   [Fact]
   public async Task Browser_CompletedExchange_ReturnsTheCredential_AndLogsNoSecrets()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      FakeSession session = world.Sessions[0];
      List<string> changes = SignInWorld.Watch(signIn);

      Task<QqqSessionCredential?> running = signIn.SignInWithBrowserAsync();
      Assert.Equal(CompanionSignInState.WaitingForBrowser, signIn.State);
      Assert.True(signIn.IsBusy);
      FakeInbox inbox = Assert.Single(world.Inboxes);
      Assert.True(inbox.Waiting);
      Assert.Equal([1], world.ListeningAtLaunch);
      Assert.Equal(session.Authorization, Assert.Single(world.Opened));
      Assert.Equal(new Uri(SignInWorld.Callback), session.Redirect);
      inbox.Deliver(session.CallbackForLatestAttempt);
      QqqSessionCredential? credential = await SignInWorld.EndsAsync(running);

      Assert.Equal(new Uri(SignInWorld.Server), credential?.Origin);
      Assert.Equal("synthetic-session-token", credential?.SessionToken);
      Assert.Equal(session.CallbackForLatestAttempt, session.Callback);
      Assert.True(inbox.Disposed);
      Assert.Equal(CompanionSignInState.SignedIn, signIn.State);
      Assert.True(signIn.HasSession);
      Assert.False(session.Disposed);
      Assert.Equal(["IsBusy=True", "State=WaitingForBrowser", "State=SignedIn", "IsBusy=False"], changes);
      Assert.Equal(TimeSpan.FromMinutes(3), Assert.Single(world.Time.RequestedDelays));
      Assert.Equal(TimeSpan.FromMinutes(3), CompanionSignIn.BrowserSignInLimit);
      Assert.Equal([Requested,
         "INFO Browser callback inbox open.",
         "INFO Opening the identity provider's sign-in page in the default browser (id.example.com).",
         "INFO Browser callback received; completing the sign-in.",
         "INFO Sign-in accepted by print.example.com; the session credential goes to the caller and is not kept here."], world.Log.Entries);
      Assert.DoesNotContain(world.Log.Entries, Secret);
   }



   /// <summary>The provider's page is at an address the server's provider wrote. The log names its host by the ASCII name the browser will really go to, so a look-alike letter reads as the "xn--" name, and the browser is given the address exactly as the session returned it.</summary>
   [Fact]
   public async Task Browser_ProviderPageWithALookAlikeHost_IsLoggedByItsAsciiName()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      world.Sessions[0].ProviderAuthority = "l\u043Egin.example.com:9443";

      Task<QqqSessionCredential?> running = signIn.SignInWithBrowserAsync();
      world.Inboxes[0].Deliver(world.Sessions[0].CallbackForLatestAttempt);

      Assert.NotNull(await SignInWorld.EndsAsync(running));
      Assert.Equal(new Uri("https://l\u043Egin.example.com:9443/authorize?state=synthetic-state-a-1"), Assert.Single(world.Opened));
      Assert.Contains("INFO Opening the identity provider's sign-in page in the default browser (xn--lgin-55d.example.com:9443).", world.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains('\u043E', StringComparison.Ordinal) || Secret(entry));
   }



   /// <summary>
   /// A browser sign-in waits, with the inbox open, until the operator cancels it or three minutes pass, and not a
   /// second less. Either ending is said to be exactly what it was, the inbox is closed, and the session and its
   /// offer stay for another attempt. Cancel has no effect once the exchange has ended. The next attempt on the same
   /// session works.
   /// </summary>
   [Theory]
   [InlineData(false, CompanionSignInState.BrowserCanceled, "INFO Browser sign-in to print.example.com was cancelled by the operator.")]
   [InlineData(true, CompanionSignInState.BrowserTimedOut, "INFO Browser sign-in to print.example.com was given up after the three minute limit.")]
   public async Task Browser_WaitsCanBeCancelledAndGivesUpAfterThreeMinutes(bool byLimit, CompanionSignInState ending, string entry)
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      FakeSession session = world.Sessions[0];

      Task<QqqSessionCredential?> running = signIn.SignInWithBrowserAsync();
      Assert.Equal(CompanionSignInState.WaitingForBrowser, signIn.State);
      Assert.True(world.Inboxes[0].Waiting);
      world.Time.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1));
      Assert.False(running.IsCompleted);
      Assert.Equal(CompanionSignInState.WaitingForBrowser, signIn.State);
      if(byLimit)
      {
         world.Time.Advance(TimeSpan.FromSeconds(1));
      }
      else
      {
         signIn.CancelBrowserSignIn();
      }

      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.Equal(ending, signIn.State);
      Assert.Null(signIn.Failure);
      Assert.Null(signIn.Rejection);
      Assert.True(signIn.HasSession);
      Assert.Equal(new SignInOffer(false, true), signIn.Offer);
      Assert.False(session.Disposed);
      Assert.Null(session.Callback);
      Assert.True(world.Inboxes[0].Disposed);
      Assert.False(signIn.IsBusy);
      string[] last = [.. world.Log.Entries.TakeLast(2)];
      Assert.Equal([entry, NeverLaunched], last);
      Assert.DoesNotContain(world.Log.Entries, line => line.StartsWith("ERROR", StringComparison.Ordinal) || Secret(line));

      signIn.CancelBrowserSignIn();
      running = signIn.SignInWithBrowserAsync();
      world.Inboxes[1].Deliver(session.CallbackForLatestAttempt);

      Assert.NotNull(await SignInWorld.EndsAsync(running));
      Assert.Equal(CompanionSignInState.SignedIn, signIn.State);
   }



   /// <summary>
   /// An abandoned attempt leaves a browser page behind, and that page can still come back. Its callback carries the
   /// abandoned attempt's state, which nobody waits for: it is dropped while the next attempt on the same session
   /// waits, and that attempt is completed by its own callback, the only one the session is ever given.
   /// </summary>
   [Fact]
   public async Task Browser_AbandonedAttemptsCallback_IsDroppedWhileTheNextAttemptWaits()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      FakeSession session = world.Sessions[0];
      Task<QqqSessionCredential?> abandoned = signIn.SignInWithBrowserAsync();
      Uri late = session.CallbackForLatestAttempt;
      signIn.CancelBrowserSignIn();
      Assert.Null(await SignInWorld.EndsAsync(abandoned));

      Task<QqqSessionCredential?> running = signIn.SignInWithBrowserAsync();
      Task<FakeInbox> reopened = world.NextInboxAsync();
      world.Inboxes[1].Deliver(late);
      FakeInbox next = await reopened;
      Assert.False(running.IsCompleted);
      Assert.Null(session.Callback);
      Assert.NotEqual(late, session.CallbackForLatestAttempt);
      next.Deliver(session.CallbackForLatestAttempt);

      Assert.NotNull(await SignInWorld.EndsAsync(running));
      Assert.Equal(session.CallbackForLatestAttempt, session.Callback);
      Assert.Equal(CompanionSignInState.SignedIn, signIn.State);
      Assert.Single(world.Log.Entries, entry => entry.StartsWith("WARN A browser callback that belongs to no waiting sign-in or sign-out was dropped.", StringComparison.Ordinal));
      Assert.DoesNotContain(world.Log.Entries, Secret);
   }



   /// <summary>
   /// Only the callback this application's installer registered is used, and only when the server lists exactly that
   /// address, so a server cannot steer the callback elsewhere. A server that lists other addresses, one that
   /// differs only by letter case among them, is refused before the session is asked to begin: no browser is opened
   /// and no inbox. The log says how many addresses the server lists and repeats none of them.
   /// </summary>
   [Fact]
   public async Task Browser_CallbackTheServerDoesNotList_IsRefused()
   {
      SignInWorld world = new() { ConfigureSession = session => session.Metadata = SignInWorld.BrowserDescription(SignInWorld.Provider, "https://print.example.com/synthetic-other-callback", "io.qrun.qqq-print-agent:/oauth/Callback") };
      using CompanionSignIn signIn = await world.BrowserReadyAsync();

      QqqSessionCredential? credential = await SignInWorld.EndsAsync(signIn.SignInWithBrowserAsync());

      Assert.Null(credential);
      Assert.Equal(CompanionSignInState.BrowserFailed, signIn.State);
      Assert.Equal("No approved native browser callback is configured.", signIn.Failure);
      Assert.Empty(world.Opened);
      Assert.Empty(world.Inboxes);
      Assert.Null(world.Sessions[0].Redirect);
      Assert.True(signIn.HasSession);
      Assert.False(signIn.IsBusy);
      Assert.Equal("WARN The server's native grant does not list this application's callback io.qrun.qqq-print-agent:/oauth/callback; it lists 2 other address(es). Add the callback to the QQQ native session grant.", world.Log.Entries[1]);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains("synthetic-other-callback", StringComparison.Ordinal) || entry.Contains("/oauth/Callback", StringComparison.Ordinal));
   }



   /// <summary>
   /// The sign-in provider presents a certificate this Windows account does not trust, so the session cannot read the
   /// provider's own description and the sign-in cannot begin. That is reported as the provider's certificate being
   /// refused, with the certificate and the origin that presented it, and not as a plain failure; the browser is not
   /// opened and the session stays. A later attempt that fails for another reason is not blamed on that certificate.
   /// </summary>
   [Fact]
   public async Task Browser_ProvidersCertificateRefused_IsReportedWithTheCertificate()
   {
      QqqCertificateRejection refusal = SignInWorld.Refusal(SignInWorld.Provider);
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      world.Sessions[0].BeginFailure = new HttpRequestException("The SSL connection could not be established, see inner exception.");
      world.Sessions[0].RefusedCertificate = refusal;

      QqqSessionCredential? credential = await SignInWorld.EndsAsync(signIn.SignInWithBrowserAsync());

      Assert.Null(credential);
      Assert.Equal(CompanionSignInState.ProviderCertificateRefused, signIn.State);
      Assert.Same(refusal, signIn.Rejection);
      Assert.Equal(new Uri(SignInWorld.Provider), signIn.Rejection?.Origin);
      Assert.Null(signIn.Failure);
      Assert.True(signIn.HasSession);
      Assert.Empty(world.Opened);
      Assert.Empty(world.Inboxes);
      Assert.Equal("WARN This Windows account refused the certificate presented by the sign-in provider (id.example.com; RemoteCertificateChainErrors).", world.Log.Entries[^1]);

      world.Sessions[0].RefusedCertificate = null;
      world.Sessions[0].BeginFailure = new IOException("synthetic network failure");
      Assert.Null(await SignInWorld.EndsAsync(signIn.SignInWithBrowserAsync()));
      Assert.Equal(CompanionSignInState.BrowserFailed, signIn.State);
      Assert.Null(signIn.Rejection);
      Assert.Equal("synthetic network failure", signIn.Failure);
   }



   /// <summary>
   /// A failure at any step of the exchange ends it as "not completed" with the reason, keeps the session, closes
   /// any inbox that was opened and releases the sign-in: the session cannot begin, it prepares an address without a
   /// state, the inbox cannot be opened, the browser cannot be opened, the inbox breaks while waiting, the session
   /// refuses the callback, no credential can be taken, or the server's own time limit ends the completion. When
   /// the browser cannot be opened the failure Windows gives names the whole sign-in address; the reason shown and
   /// logged names only the kind of failure. No state reaches the log or the reason in any of them.
   /// </summary>
   [Theory]
   [InlineData("begin", 0, "The server's sign-in configuration is not usable.", "QqqSessionException: The server's sign-in configuration is not usable.")]
   [InlineData("no state", 0, "The sign-in address the session prepared carries no state of its own, so its callback could not be told from another.", "InvalidOperationException: The sign-in address the session prepared carries no state of its own, so its callback could not be told from another.")]
   [InlineData("no inbox", 0, "synthetic open failure", "IOException: synthetic open failure")]
   [InlineData("browser", 0, BrowserStartFailure.WindowsReport, "IOException: " + BrowserStartFailure.WindowsReport)]
   [InlineData("launcher", 0, BrowserStartFailure.OtherReport, "IOException: " + BrowserStartFailure.OtherReport)]
   [InlineData("inbox", 1, "Invalid QQQ browser callback.", "InvalidDataException: Invalid QQQ browser callback.")]
   [InlineData("complete", 1, "The server's answer was not usable.", "QqqSessionException: The server's answer was not usable.")]
   [InlineData("export", 1, "Sign in before exporting the session.", "QqqSessionException: Sign in before exporting the session.")]
   [InlineData("time limit", 1, "The server did not answer in time.", "TaskCanceledException: The server did not answer in time.")]
   public async Task Browser_FailureDuringTheExchange_IsReportedWithItsReason(string step, int opened, string reason, string logged)
   {
      const string address = "https://id.example.com/authorize?state=synthetic-state-a-1";
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      FakeSession session = world.Sessions[0];
      session.BeginFailure = step is "begin" ? new QqqSessionException(QqqSessionException.FailureKind.ConfigurationRequired, "The server's sign-in configuration is not usable.") : null;
      session.AuthorizationOverride = step is "no state" ? new Uri("https://id.example.com/authorize?client_id=synthetic-client") : null;
      world.InboxFailure = step is "no inbox" ? new IOException("synthetic open failure") : null;
      world.BrowserFailure = step switch
      {
         "browser" => new Win32Exception(2, "An error occurred trying to start process '" + address + "' with working directory 'C:\\synthetic'. The system cannot find the file specified."),
         "launcher" => new InvalidOperationException("synthetic launcher failure for " + address),
         _ => null,
      };
      session.CompleteFailure = step switch
      {
         "complete" => new QqqSessionException(QqqSessionException.FailureKind.InvalidResponse, "The server's answer was not usable."),
         "time limit" => new TaskCanceledException("The server did not answer in time."),
         _ => null,
      };
      session.ExportFailure = step is "export" ? new QqqSessionException(QqqSessionException.FailureKind.AuthenticationRequired, "Sign in before exporting the session.") : null;

      Task<QqqSessionCredential?> running = signIn.SignInWithBrowserAsync();
      if(step is "inbox")
      {
         world.Inboxes[0].Fail(new InvalidDataException("Invalid QQQ browser callback."));
      }
      else if(step is "complete" or "export" or "time limit")
      {
         world.Inboxes[0].Deliver(session.CallbackForLatestAttempt);
      }

      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.Equal(CompanionSignInState.BrowserFailed, signIn.State);
      Assert.Equal(reason, signIn.Failure);
      Assert.Null(signIn.Rejection);
      Assert.True(signIn.HasSession);
      Assert.False(session.Disposed);
      Assert.False(signIn.IsBusy);
      Assert.Equal(opened, world.Opened.Count);
      Assert.All(world.Inboxes, inbox => Assert.True(inbox.Disposed));
      Assert.Contains(NeverLaunched, world.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("ERROR Browser sign-in not completed: " + logged, world.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(world.Log.Entries, Secret);
      Assert.False(Secret(signIn.Failure!));
   }



   /// <summary>
   /// When Windows cannot start the browser, the failure it gives names what it could not start: the whole sign-in
   /// address, with the attempt's state, nonce and code challenge. None of that is logged or kept. The reason the
   /// wizard is given says that the default browser could not be opened and what to check, the log says the same
   /// with the kind of failure, and no log line and no property a view binds to holds one of those values or any
   /// query at all. The same holds for a launcher that fails in another way and names the address in its text.
   /// </summary>
   [Theory]
   [InlineData(true, BrowserStartFailure.WindowsReport)]
   [InlineData(false, BrowserStartFailure.OtherReport)]
   public async Task Browser_ThatCannotBeStarted_IsReportedWithoutAnythingOfTheSignInAddress(bool asWindows, string report)
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      world.Sessions[0].AuthorizationOverride = BrowserStartFailure.SignInAddress;
      world.BrowserFailure = asWindows ? BrowserStartFailure.AsWindows(BrowserStartFailure.SignInAddress) : BrowserStartFailure.AsAnotherKind(BrowserStartFailure.SignInAddress);
      Assert.True(BrowserStartFailure.Leaks(world.BrowserFailure.Message));

      Assert.Null(await SignInWorld.EndsAsync(signIn.SignInWithBrowserAsync()));

      Assert.Equal(CompanionSignInState.BrowserFailed, signIn.State);
      Assert.Equal(report, signIn.Failure);
      Assert.Contains("ERROR Browser sign-in not completed: IOException: " + report, world.Log.Entries, StringComparer.Ordinal);
      Assert.Contains(world.Log.Entries, entry => entry.StartsWith("DEBUG Browser sign-in not completed detail: System.IO.IOException: " + report, StringComparison.Ordinal));
      Assert.DoesNotContain(world.Log.Entries, BrowserStartFailure.Leaks);
      Assert.DoesNotContain(BrowserStartFailure.Bound(signIn), BrowserStartFailure.Leaks);
      Assert.Empty(world.Opened);
      Assert.True(Assert.Single(world.Inboxes).Disposed);
      Assert.True(signIn.HasSession);
      Assert.False(signIn.IsBusy);
   }



   /// <summary>
   /// When a browser sign-in is not completed, what the protocol handler recorded is copied into the log, so a
   /// hand-off that stopped outside the companion can be seen in one place; when it recorded nothing in the last ten
   /// minutes, the log says the browser never launched the handler and what that usually means.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task Browser_NotCompleted_CopiesTheHandlersTrace_OrSaysItNeverRan(bool traced)
   {
      string[] trace = ["2026-11-10T10:00:00Z Protocol handler started by the browser (process 4242, session 1).", "2026-11-10T10:00:05Z Callback NOT delivered: TimeoutException: synthetic."];
      SignInWorld world = new();
      world.Trace.AddRange(traced ? trace : []);
      using CompanionSignIn signIn = await world.BrowserReadyAsync();

      Task<QqqSessionCredential?> running = signIn.SignInWithBrowserAsync();
      world.Time.Advance(TimeSpan.FromMinutes(3));

      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.Equal(traced ? "WARN Protocol handler trace from trace/auth-callback.log:" + Environment.NewLine + trace[0] + Environment.NewLine + trace[1] : NeverLaunched, world.Log.Entries[^1]);
      Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(world.TraceWindows));
   }



   /// <summary>
   /// A browser sign-in whose caller stops waiting ends quietly: nothing is reported as cancelled or failed, the inbox
   /// is closed, and the session stays. When the wait ends at the very moment the session has begun the sign-in, the
   /// browser is not opened for it and no inbox is.
   /// </summary>
   [Theory]
   [InlineData(true, 1, "TaskCanceledException")]
   [InlineData(false, 0, "OperationCanceledException")]
   public async Task Browser_CallerStopsWaiting_EndsQuietlyAndKeepsTheSession(bool whileWaiting, int opened, string cancellation)
   {
      using CancellationTokenSource caller = new();
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      world.Sessions[0].BeforeAnswer = whileWaiting ? null : caller.Cancel;

      Task<QqqSessionCredential?> running = signIn.SignInWithBrowserAsync(caller.Token);
      if(whileWaiting)
      {
         await caller.CancelAsync();
      }

      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.Equal(CompanionSignInState.Ready, signIn.State);
      Assert.True(signIn.HasSession);
      Assert.False(world.Sessions[0].Disposed);
      Assert.False(signIn.IsBusy);
      Assert.Equal(opened, world.Opened.Count);
      Assert.Equal(opened, world.Inboxes.Count);
      Assert.All(world.Inboxes, inbox => Assert.True(inbox.Disposed));
      Assert.Equal($"DEBUG Browser sign-in ended early because its caller stopped waiting ({cancellation}).", world.Log.Entries[^1]);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.StartsWith("WARN", StringComparison.Ordinal) || entry.StartsWith("ERROR", StringComparison.Ordinal));
   }



   /// <summary>When the operator cancels at the very moment the session completes the sign-in, the wait has ended: no credential is taken, and the attempt ends as cancelled.</summary>
   [Fact]
   public async Task Browser_CompletedAtTheMomentItIsCancelled_TakesNoCredential()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      FakeSession session = world.Sessions[0];
      session.BeforeAnswer = () =>
      {
         if(session.Callback is not null)
         {
            signIn.CancelBrowserSignIn();
         }
      };

      Task<QqqSessionCredential?> running = signIn.SignInWithBrowserAsync();
      world.Inboxes[0].Deliver(session.CallbackForLatestAttempt);

      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.Equal(CompanionSignInState.BrowserCanceled, signIn.State);
      Assert.NotNull(session.Callback);
   }



   /// <summary>
   /// The callback is already there, and the wait has already ended, when the sign-in starts waiting for the
   /// callback: the browser came back at once and, in the same moment, the operator pressed Cancel or the three
   /// minutes ran out. Waiting for something that has already arrived does not look at the token, so the sign-in
   /// looks itself. The session is never asked to complete and nothing says a callback was received; the attempt
   /// ends as cancelled or timed out, and the session, which was handed nothing, stays for another attempt. The
   /// inbox answers inline here, so the callback has reached its ticket before the wait is ended, which the test
   /// checks: it was not dropped, and its inbox was closed, inside the call that delivered it.
   /// </summary>
   [Theory]
   [InlineData(false, CompanionSignInState.BrowserCanceled)]
   [InlineData(true, CompanionSignInState.BrowserTimedOut)]
   public async Task Browser_WaitEndedAsTheCallbackArrived_NeverAsksTheSessionToComplete(bool byLimit, CompanionSignInState ending)
   {
      SignInWorld world = new() { InboxesAnswerInline = true };
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      FakeSession session = world.Sessions[0];
      bool routed = false;
      world.Launched = _ =>
      {
         using NoContext none = new();
         world.Inboxes[0].Deliver(session.CallbackForLatestAttempt);
         routed = world.Inboxes[0].Disposed;
         if(byLimit)
         {
            world.Time.Advance(CompanionSignIn.BrowserSignInLimit);
         }
         else
         {
            signIn.CancelBrowserSignIn();
         }
      };

      QqqSessionCredential? credential = await SignInWorld.EndsAsync(signIn.SignInWithBrowserAsync());

      Assert.True(routed);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains("was dropped", StringComparison.Ordinal));
      Assert.Null(credential);
      Assert.Null(session.Callback);
      Assert.Null(session.CompleteToken);
      Assert.DoesNotContain("INFO Browser callback received; completing the sign-in.", world.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(ending, signIn.State);
      Assert.True(signIn.HasSession);
      Assert.False(session.Disposed);
      Assert.False(signIn.IsBusy);
      Assert.Single(world.Opened);
      Assert.DoesNotContain(world.Log.Entries, Secret);
   }



   /// <summary>
   /// The session is asked to complete with the exchange's own token, not with one that never ends: while the session
   /// is completing, the token it was given is still live, and Cancel ends it, so the completion stops with the
   /// attempt instead of running on behind an attempt that was reported as cancelled.
   /// </summary>
   [Fact]
   public async Task Browser_CancelledWhileTheSessionCompletes_EndsTheCompletionToo()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.BrowserReadyAsync();
      FakeSession session = world.Sessions[0];
      session.PendingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Task<QqqSessionCredential?> running = signIn.SignInWithBrowserAsync();
      world.Inboxes[0].Deliver(session.CallbackForLatestAttempt);
      await session.CompleteEntered.WaitAsync(TimeSpan.FromSeconds(10));
      Assert.True(session.CompleteToken is { CanBeCanceled: true, IsCancellationRequested: false });

      signIn.CancelBrowserSignIn();

      Assert.True(session.CompleteToken is { IsCancellationRequested: true });
      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.Equal(CompanionSignInState.BrowserCanceled, signIn.State);
      Assert.False(signIn.IsBusy);
   }



   /// <summary>Nothing is attempted without a session, or on a session that does not offer the browser: no credential, no inbox, no browser, nothing announced or logged.</summary>
   [Fact]
   public async Task Browser_WithoutASessionThatOffersIt_NothingIsAttempted()
   {
      SignInWorld world = new();
      using CompanionSignIn unprepared = world.NewSignIn();
      using CompanionSignIn passwordOnly = await world.PasswordReadyAsync();
      List<string> changes = SignInWorld.Watch(passwordOnly);

      Assert.Null(await unprepared.SignInWithBrowserAsync());
      Assert.Null(await SignInWorld.EndsAsync(passwordOnly.SignInWithBrowserAsync()));

      Assert.Equal(CompanionSignInState.NotPrepared, unprepared.State);
      Assert.Equal(CompanionSignInState.Ready, passwordOnly.State);
      Assert.Empty(world.Inboxes);
      Assert.Empty(world.Opened);
      Assert.Null(world.Sessions[0].Redirect);
      Assert.Empty(changes);
      Assert.Empty(world.Log.Entries);
   }



   /// <summary>
   /// Discarding, or disposing, while a browser sign-in waits ends the wait quietly: the inbox is closed, the session
   /// is discarded once the exchange has ended, and nothing is reported as cancelled or failed. After a discard the
   /// sign-in says so and can be used again; after disposal it announces nothing.
   /// </summary>
   [Theory]
   [InlineData(false, "DEBUG Browser sign-in ended early because the session was discarded (TaskCanceledException).")]
   [InlineData(true, "DEBUG Browser sign-in ended early because the sign-in was closed (TaskCanceledException).")]
   public async Task Browser_DiscardedOrDisposedWhileWaiting_EndsQuietly(bool dispose, string entry)
   {
      SignInWorld world = new();
      CompanionSignIn signIn = await world.BrowserReadyAsync();
      Task<QqqSessionCredential?> running = signIn.SignInWithBrowserAsync();
      List<string> changes = SignInWorld.Watch(signIn);

      if(dispose)
      {
         signIn.Dispose();
      }
      else
      {
         signIn.Discard();
      }

      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.True(world.Inboxes[0].Disposed);
      Assert.True(world.Sessions[0].Disposed);
      Assert.False(signIn.HasSession);
      Assert.False(signIn.IsBusy);
      Assert.Equal(entry, world.Log.Entries[^1]);
      Assert.DoesNotContain(world.Log.Entries, line => line.StartsWith("WARN", StringComparison.Ordinal) || line.StartsWith("ERROR", StringComparison.Ordinal));
      string[] announced = dispose ? [] : ["Offer=null", "HasSession=False", "State=NotPrepared", "IsBusy=False"];
      Assert.Equal(announced, changes, StringComparer.Ordinal);
      signIn.Dispose();
   }



   /// <summary>
   /// Runs every operation on a single-threaded context, like a window's, while each thing the sign-in waits for is
   /// completed from a thread-pool thread: the description, the password check, the start of the browser sign-in, the
   /// callback (which the dispatcher receives on a thread of its own) and its completion. Every announcement must
   /// arrive on the calling thread with that context current. A view updates its controls in these handlers, so one
   /// "do not return to the caller's thread" inside the sign-in fails this test instead of crashing the companion.
   /// </summary>
   [Fact]
   public void Announcements_AreRaisedOnTheCallingThread()
   {
      using PumpContext pump = new();
      int caller = Environment.CurrentManagedThreadId;
      List<string> offThread = [];
      List<string> announced = [];
      SignInWorld world = new() { ConfigureSession = Held(new QqqAuthenticationMetadata { Type = "TABLE_BASED" }) };
      using CompanionSignIn signIn = world.NewSignIn();
      signIn.PropertyChanged += (_, change) =>
      {
         announced.Add(change.PropertyName!);
         if(Environment.CurrentManagedThreadId != caller || !ReferenceEquals(SynchronizationContext.Current, pump))
         {
            offThread.Add(change.PropertyName!);
         }
      };

      pump.Run(() => RunEveryOperationAsync(world, signIn));

      Assert.Empty(offThread);
      Assert.Contains(nameof(CompanionSignIn.State), announced, StringComparer.Ordinal);
      Assert.Contains(nameof(CompanionSignIn.IsBusy), announced, StringComparer.Ordinal);
      Assert.Contains(nameof(CompanionSignIn.Offer), announced, StringComparer.Ordinal);
      Assert.Contains(nameof(CompanionSignIn.HasSession), announced, StringComparer.Ordinal);
      Assert.Contains(nameof(CompanionSignIn.Failure), announced, StringComparer.Ordinal);
      Assert.Equal(CompanionSignInState.SignedIn, signIn.State);
   }



   /// <summary>A session script in which every step waits for the test, so each can be completed from another thread.</summary>
   private static Action<FakeSession> Held(QqqAuthenticationMetadata description) => session =>
   {
      session.Metadata = description;
      session.PendingDiscovery = new TaskCompletionSource<QqqAuthenticationMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
      session.PendingPassword = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      session.PendingBegin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      session.PendingComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
   };



   /// <summary>Reads a password server's description, fails a password sign-in, discards, reads a browser server's description and signs in through the browser, completing every wait from another thread.</summary>
   private static async Task RunEveryOperationAsync(SignInWorld world, CompanionSignIn signIn)
   {
      Task<SignInOffer?> prepare = signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None);
      await Task.Run(() => world.Sessions[0].PendingDiscovery!.SetResult(world.Sessions[0].Metadata));
      Assert.NotNull(await prepare);
      Task<QqqSessionCredential?> password = signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password");
      await Task.Run(() => world.Sessions[0].PendingPassword!.SetException(new IOException("synthetic network failure")));
      Assert.Null(await password);
      signIn.Discard();

      world.ConfigureSession = Held(SignInWorld.BrowserDescription());
      prepare = signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.BrowserCheck, QqqCertificateExceptionOrigins.None);
      await Task.Run(() => world.Sessions[1].PendingDiscovery!.SetResult(world.Sessions[1].Metadata));
      Assert.NotNull(await prepare);
      Task<FakeInbox> opened = world.NextInboxAsync();
      Task<QqqSessionCredential?> browser = signIn.SignInWithBrowserAsync();
      await Task.Run(() => world.Sessions[1].PendingBegin!.SetResult());
      FakeInbox inbox = await opened;
      await Task.Run(() => inbox.Deliver(world.Sessions[1].CallbackForLatestAttempt));
      await world.Sessions[1].CompleteEntered;
      await Task.Run(() => world.Sessions[1].PendingComplete!.SetResult());
      Assert.NotNull(await browser);
   }
}
