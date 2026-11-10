/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>
/// The rules every operation of the flow shares: one runs at a time and a refused one says so, the flow is always
/// released again whatever a subscriber does, and once the flow is disposed a running operation ends quietly, sends
/// nothing more and announces nothing more.
/// </summary>
public sealed class SignInFlowLifecycleTests
{
   /// <summary>Builds a fixture whose flow has discovered a server offering password sign-in and the browser grant.</summary>
   private static async Task<SignInFixture> DiscoveredAsync()
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.Metadata = SignInFixture.Offers(password: true, SignInFixture.Callback) };
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Forget();
      return fixture;
   }



   /// <summary>
   /// The certificate checkbox saves without the window's own busy flag, so the operator can click Enroll while that save
   /// is still running. The enrollment is refused, and the refusal is not silent: the result says it was not started (so
   /// the window keeps the code that was typed), the log names the refused operation, and the operator is told that
   /// another operation is still running. Nothing is sent for it, and the same enrollment works once the save has ended.
   /// </summary>
   [Fact]
   public async Task EnrollmentDuringACertificateSave_IsRefusedOutLoudAndCanBeRepeated()
   {
      SignInFixture fixture = new();
      (Task reached, TaskCompletionSource<string> reply) = fixture.HoldService();
      Task<bool> saving = fixture.Flow.SetTrustAsync(allow: false, null);
      await reached;
      fixture.Forget();

      EnrollmentResult refused = await fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123");

      Assert.Equal(new EnrollmentResult(null, ServiceReachable: true, Started: false), refused);
      Assert.Equal(new FeedbackMessage("Another operation is still running", "This request was not started. Wait for the current operation to finish, then try again.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
      Assert.Equal("WARN Enrollment was not started: another operation is still running.", Assert.Single(fixture.Log.Entries));
      Assert.Equal("set-server-trust", Assert.Single(fixture.Requests).Operation);
      Assert.True(fixture.Flow.IsBusy);

      reply.SetResult(SignInFixture.RealReply(fixture.Requests[0]));
      Assert.True(await SignInFixture.EndsAsync(saving));
      fixture.AnswerAsTheRealService();
      EnrollmentResult repeated = await fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123");

      Assert.Equal(new EnrollmentResult(SignInFixture.Enrolled, ServiceReachable: true), repeated);
      Assert.True(repeated.Started);
      Assert.Equal("code-123", fixture.Requests[1].EnrollmentCode);
   }



   /// <summary>
   /// A subscriber that throws while the flow announces that an operation started (that it is busy, its stage, or the
   /// Cancel button) gets its exception back, but the flow is not left claimed: it is free and idle of that operation
   /// afterwards, and the next operation runs. The claim used to be announced before anything protected it, so one
   /// failing handler made every later click do nothing.
   /// </summary>
   [Theory]
   [InlineData(nameof(SignInFlow.IsBusy))]
   [InlineData(nameof(SignInFlow.Stage))]
   [InlineData(nameof(SignInFlow.BrowserPending))]
   public async Task SubscriberThatThrowsAtTheStart_DoesNotLeaveTheFlowClaimed(string property)
   {
      SignInFixture fixture = await DiscoveredAsync();
      bool thrown = false;
      fixture.Flow.PropertyChanged += (_, change) =>
      {
         if(!thrown && string.Equals(change.PropertyName, property, StringComparison.Ordinal))
         {
            thrown = true;
            throw new NotSupportedException("A view's handler failed.");
         }
      };

      await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent));

      Assert.False(fixture.Flow.IsBusy);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.True(await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent));
      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);
   }



   /// <summary>
   /// A subscriber may start the next operation the moment the flow says it is free. That operation's stage must stand:
   /// the operation that just ended sets its own final stage before it releases the flow, not after, so it cannot
   /// overwrite the stage of the one that follows it.
   /// </summary>
   [Fact]
   public async Task OperationStartedWhenTheFlowBecomesFree_KeepsItsStage()
   {
      SignInFixture fixture = new();
      Task<bool>? next = null;
      fixture.Flow.PropertyChanged += (_, change) =>
      {
         if(next is null && string.Equals(change.PropertyName, nameof(SignInFlow.IsBusy), StringComparison.Ordinal) && !fixture.Flow.IsBusy)
         {
            fixture.ConfigureSession = session => session.PendingDiscovery = new TaskCompletionSource<QqqAuthenticationMetadata>();
            next = fixture.Flow.DiscoverAsync(SignInFixture.Server);
         }
      };

      _ = await fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123");

      Assert.NotNull(next);
      Assert.True(fixture.Flow.IsBusy);
      Assert.Equal(SignInStage.Discovering, fixture.Flow.Stage);
      Assert.Equal([SignInStage.Enrolling, SignInStage.Idle, SignInStage.Discovering], fixture.Stages);
      fixture.Sessions[0].PendingDiscovery!.SetResult(fixture.Sessions[0].Metadata);
      Assert.True(await SignInFixture.EndsAsync(next));
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
   }



   /// <summary>
   /// Disposing the flow while the server is checking a password only cancels. The sign-in ends by itself and quietly:
   /// its task completes without a failure, nothing is handed to the service, nothing is shown or announced after the
   /// dispose, and the session is discarded by the sign-in on its way out. The scripted session fails a call it is
   /// disposed under the way the real client does, so a flow that discarded the session from Dispose would fail here
   /// with "disposed", and a sign-in that did not watch the flow's lifetime would never end.
   /// </summary>
   [Fact]
   public async Task DisposedWhileTheServerChecksThePassword_EndsQuietlyAndDiscardsTheSessionItself()
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Sessions[0].PendingPassword = new TaskCompletionSource();

      Task<bool> running = fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
      int announced = fixture.Changes.Count;
      fixture.Flow.Dispose();

      Assert.True(await SignInFixture.EndsAsync(running));
      Assert.Empty(fixture.Requests);
      Assert.Equal("Signing in", fixture.Titles);
      Assert.Equal(announced, fixture.Changes.Count);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.Equal("DEBUG Password sign-in ended because the connection flow was closed (TaskCanceledException).", fixture.Log.Entries[^1]);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.StartsWith("WARN", StringComparison.Ordinal) || entry.StartsWith("ERROR", StringComparison.Ordinal));
   }



   /// <summary>
   /// The server accepts the password, and the flow is disposed before the sign-in gets to continue on its own thread.
   /// The sign-in must look at its wait again before it does anything with the answer: no session is handed to the
   /// service, and "signed in" is neither announced nor recorded. It runs on a single-threaded context like a window's,
   /// so the dispose is certain to happen between the server's answer and the continuation.
   /// </summary>
   [Fact]
   public void DisposedAfterTheServerAcceptedThePassword_HandsNothingToTheService()
   {
      using PumpContext pump = new();
      SignInFixture fixture = new() { ConfigureSession = session => session.PendingPassword = new TaskCompletionSource() };

      pump.Run(async () =>
      {
         await fixture.Flow.DiscoverAsync(SignInFixture.Server);
         fixture.Forget();
         Task<bool> running = fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
         fixture.Forget();
         fixture.Sessions[0].PendingPassword!.SetResult();
         fixture.Flow.Dispose();
         await SignInFixture.EndsAsync(running);
      });

      Assert.Empty(fixture.Requests);
      Assert.Empty(fixture.Changes);
      Assert.Null(fixture.Flow.Readiness);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.Equal("DEBUG Password sign-in ended because the connection flow was closed (OperationCanceledException).", Assert.Single(fixture.Log.Entries));
   }



   /// <summary>
   /// The service confirms the hand-over, and the flow is disposed before the sign-in gets to continue. Whatever the
   /// service answered, a closed flow announces nothing: no "signed in" message, no stage, no readiness, and nothing is
   /// logged as a result.
   /// </summary>
   [Fact]
   public void DisposedAfterTheServiceAnswered_AnnouncesNothing()
   {
      using PumpContext pump = new();
      SignInFixture fixture = new();

      pump.Run(async () =>
      {
         await fixture.Flow.DiscoverAsync(SignInFixture.Server);
         (Task reached, TaskCompletionSource<string> reply) = fixture.HoldService();
         Task<bool> running = fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
         await reached;
         fixture.Forget();
         reply.SetResult(SignInFixture.SignedInAndPrinting);
         fixture.Flow.Dispose();
         await SignInFixture.EndsAsync(running);
      });

      Assert.Empty(fixture.Changes);
      Assert.Null(fixture.Flow.Readiness);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("Service sign-in result", StringComparison.Ordinal) || !entry.StartsWith("DEBUG", StringComparison.Ordinal));
      Assert.Equal("DEBUG Password sign-in ended because the connection flow was closed (OperationCanceledException).", fixture.Log.Entries[^1]);
   }



   /// <summary>
   /// The browser callback arrives, and the flow is disposed before the sign-in gets to continue. The login is not
   /// completed with that callback, nothing is handed to the service, and nothing is announced. The test waits until
   /// the callback has really reached the flow's wait, so it is the look at the wait after the callback that stops it.
   /// </summary>
   [Fact]
   public void DisposedAfterTheBrowserCallbackArrived_CompletesNothing()
   {
      using PumpContext pump = new();
      SignInFixture fixture = new() { ConfigureSession = session => session.Metadata = SignInFixture.Offers(password: false, SignInFixture.Callback) };

      pump.Run(async () =>
      {
         await fixture.Flow.DiscoverAsync(SignInFixture.Server);
         Task<bool> running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
         fixture.Forget();
         fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
         Assert.True(SpinWait.SpinUntil(() => fixture.Inboxes[0].Received, TimeSpan.FromSeconds(10)));
         fixture.Flow.Dispose();
         await SignInFixture.EndsAsync(running);
      });

      Assert.Null(fixture.Sessions[0].Callback);
      Assert.Empty(fixture.Requests);
      Assert.Empty(fixture.Changes);
      Assert.True(fixture.Inboxes[0].Disposed);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.Equal("DEBUG Browser sign-in ended because the connection flow was closed (OperationCanceledException).", Assert.Single(fixture.Log.Entries));
   }



   /// <summary>
   /// A failure that is not a cancellation, seen after the flow was disposed, is a quiet end too. The server's answer to a
   /// discovery fails (here with a kind of failure the discovery does not handle at all), and the flow is disposed before
   /// the discovery gets to continue: nothing escapes to the window's handler, nothing is announced, and the log has one
   /// debug line that names what was swallowed.
   /// </summary>
   [Fact]
   public void FailureSeenAfterTheFlowWasDisposed_IsAQuietEnd()
   {
      using PumpContext pump = new();
      SignInFixture fixture = new() { ConfigureSession = session => session.PendingDiscovery = new TaskCompletionSource<QqqAuthenticationMetadata>() };
      bool started = false;

      pump.Run(async () =>
      {
         Task<bool> running = fixture.Flow.DiscoverAsync(SignInFixture.Server);
         fixture.Forget();
         fixture.Sessions[0].PendingDiscovery!.SetException(new NotSupportedException("A failure that is not a cancellation."));
         fixture.Flow.Dispose();
         started = await SignInFixture.EndsAsync(running);
      });

      Assert.True(started);
      Assert.Empty(fixture.Changes);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.Equal("DEBUG Discovery ended because the connection flow was closed (NotSupportedException).", Assert.Single(fixture.Log.Entries));
   }



   /// <summary>
   /// The server describes itself, and the caller ends the wait before the discovery gets to continue. The discovery
   /// looks at its wait again before it uses the answer: no sign-in method is offered, no message is shown, no session
   /// is left, and the flow is idle.
   /// </summary>
   [Fact]
   public void CancelledAfterTheServerDescribedItself_OffersNothing()
   {
      using PumpContext pump = new();
      using CancellationTokenSource cancellation = new();
      SignInFixture fixture = new() { ConfigureSession = session => session.PendingDiscovery = new TaskCompletionSource<QqqAuthenticationMetadata>() };

      pump.Run(async () =>
      {
         Task<bool> running = fixture.Flow.DiscoverAsync(SignInFixture.Server, cancellation.Token);
         fixture.Sessions[0].PendingDiscovery!.SetResult(fixture.Sessions[0].Metadata);
         EndTheWait(cancellation);
         await SignInFixture.EndsAsync(running);
      });

      Assert.Empty(fixture.Shown);
      Assert.False(fixture.Flow.PasswordOffered);
      Assert.False(fixture.Flow.HasSession);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.Equal("DEBUG Discovery was cancelled by its caller (OperationCanceledException).", fixture.Log.Entries[^1]);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("Sign-in options for", StringComparison.Ordinal));
   }



   /// <summary>
   /// The server answers the start of a browser sign-in, and the flow is disposed before the sign-in gets to continue.
   /// The sign-in looks at its wait again before it opens anything: no browser window is opened for a window that has
   /// closed, and nothing more is logged or announced.
   /// </summary>
   [Fact]
   public void DisposedBeforeTheBrowserWasOpened_OpensNothing()
   {
      using PumpContext pump = new();
      SignInFixture fixture = new()
      {
         ConfigureSession = session =>
         {
            session.Metadata = SignInFixture.Offers(password: false, SignInFixture.Callback);
            session.PendingBegin = new TaskCompletionSource();
         },
      };

      pump.Run(async () =>
      {
         await fixture.Flow.DiscoverAsync(SignInFixture.Server);
         Task<bool> running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
         fixture.Forget();
         fixture.Sessions[0].PendingBegin!.SetResult();
         fixture.Flow.Dispose();
         await SignInFixture.EndsAsync(running);
      });

      Assert.Empty(fixture.Opened);
      Assert.Empty(fixture.Changes);
      Assert.True(fixture.Inboxes[0].Disposed);
      Assert.Equal("DEBUG Browser sign-in ended because the connection flow was closed (OperationCanceledException).", Assert.Single(fixture.Log.Entries));
   }



   /// <summary>
   /// The server completes the browser sign-in, and the flow is disposed before the sign-in gets to continue. The sign-in
   /// looks at its wait again before it takes the credential: nothing is prepared for the service, nothing is sent, and
   /// the log does not say that a session is being handed over.
   /// </summary>
   [Fact]
   public void DisposedAfterTheBrowserSignInCompleted_TakesNoCredential()
   {
      using PumpContext pump = new();
      SignInFixture fixture = new()
      {
         ConfigureSession = session =>
         {
            session.Metadata = SignInFixture.Offers(password: false, SignInFixture.Callback);
            session.PendingComplete = new TaskCompletionSource();
         },
      };

      pump.Run(async () =>
      {
         await fixture.Flow.DiscoverAsync(SignInFixture.Server);
         Task<bool> running = fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
         fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
         await fixture.Sessions[0].CompleteEntered.WaitAsync(TimeSpan.FromSeconds(10));
         fixture.Forget();
         fixture.Sessions[0].PendingComplete!.SetResult();
         fixture.Flow.Dispose();
         await SignInFixture.EndsAsync(running);
      });

      Assert.Empty(fixture.Requests);
      Assert.Empty(fixture.Changes);
      Assert.Null(fixture.Flow.Readiness);
      Assert.Equal("DEBUG Browser sign-in ended because the connection flow was closed (OperationCanceledException).", Assert.Single(fixture.Log.Entries));
   }



   /// <summary>
   /// The identity provider confirms the sign-out, and the flow is disposed before the sign-out gets to continue. Nothing
   /// more is said: the confirmation is neither logged nor announced to a window that has closed.
   /// </summary>
   [Fact]
   public void DisposedAfterTheProviderConfirmedTheSignOut_SaysNothingMore()
   {
      using PumpContext pump = new();
      QqqProviderLogout logout = new(new Uri("https://id.example.com/logout?id_token_hint=secret-hint"), new Uri(SignInFixture.Callback), "state-1");
      SignInFixture fixture = new() { Service = _ => Task.FromResult(JsonSerializer.Serialize(new PipeSignOutResult(SignInFixture.SignedOut, logout), PrintApiClient.JsonOptions)) };

      pump.Run(async () =>
      {
         Task<bool> running = fixture.Flow.SignOutAsync();
         fixture.Forget();
         fixture.Inboxes[0].Deliver(new Uri(SignInFixture.Callback + "?state=state-1"));
         Assert.True(SpinWait.SpinUntil(() => fixture.Inboxes[0].Received, TimeSpan.FromSeconds(10)));
         fixture.Flow.Dispose();
         await SignInFixture.EndsAsync(running);
      });

      Assert.Empty(fixture.Changes);
      Assert.True(fixture.Inboxes[0].Disposed);
      Assert.Equal("DEBUG Sign-out ended because the connection flow was closed (OperationCanceledException).", Assert.Single(fixture.Log.Entries));
   }



   /// <summary>
   /// The service confirms the sign-in, and a view's handler throws while "Signed in" is being announced, with a kind of
   /// failure that means "not completed" when the server's sign-in raises it. The sign-in did complete, so it is not
   /// reported or logged as failed: the handler's failure goes back to its caller, and the flow is released and holds what
   /// the service confirmed, in the signed-in stage, without a session.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task HandlerThatThrowsWhileSignedInIsAnnounced_IsNotAFailedSignIn(bool browser)
   {
      SignInFixture fixture = await DiscoveredAsync();
      fixture.Flow.PropertyChanged += (_, change) =>
      {
         if(string.Equals(change.PropertyName, nameof(SignInFlow.Feedback), StringComparison.Ordinal) && string.Equals(fixture.Flow.Feedback?.Title, "Signed in", StringComparison.Ordinal))
         {
            throw new IOException("A view's handler failed.");
         }
      };

      Task<bool> running = browser ? fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent) : fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
      if(browser)
      {
         fixture.Inboxes[0].Deliver(fixture.Sessions[0].CallbackForLatestAttempt);
      }
      IOException thrown = await Assert.ThrowsAsync<IOException>(() => running);

      Assert.Equal("A view's handler failed.", thrown.Message);
      Assert.Single(fixture.Requests);
      Assert.Equal("Signed in", fixture.Shown[^1].Title);
      Assert.Equal(SignInStage.SignedIn, fixture.Flow.Stage);
      Assert.Equal(SignInReadiness.PrintingEnabled, fixture.Flow.Readiness);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.IsBusy);
      Assert.False(fixture.Flow.BrowserPending);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("not completed", StringComparison.Ordinal) || entry.StartsWith("ERROR", StringComparison.Ordinal) || entry.StartsWith("WARN", StringComparison.Ordinal));
   }



   /// <summary>Ends a caller's wait at once, on the calling thread, so the cancellation is certain to precede whatever the flow does next.</summary>
   private static void EndTheWait(CancellationTokenSource cancellation) => cancellation.Cancel();



   /// <summary>The sentence for a request that left and was not answered names what really happened, and anything without a sentence of its own quotes the reason it was given.</summary>
   [Fact]
   public void UnansweredRequest_IsWordedByWhatHappened()
   {
      Assert.Equal("The request timed out.", SignInMessages.Unanswered(ServiceResult<string>.Unreachable(ServiceFailure.TimedOut, sent: true, "The reply did not arrive in time.")));
      Assert.Equal("The service answered, but its answer could not be read.", SignInMessages.Unanswered(ServiceResult<string>.Unreachable(ServiceFailure.UnreadableReply, sent: true, "The service's reply could not be read.")));
      Assert.Equal("The wait for the service's answer was ended.", SignInMessages.Unanswered(ServiceResult<string>.Abandoned()));
      Assert.Equal("The service's answer did not arrive. Reason: Access is denied.", SignInMessages.Unanswered(ServiceResult<string>.Unreachable(ServiceFailure.Native, sent: true, "Access is denied.")));
      Assert.Throws<ArgumentNullException>(() => SignInMessages.Unanswered<string>(null!));
   }
}
