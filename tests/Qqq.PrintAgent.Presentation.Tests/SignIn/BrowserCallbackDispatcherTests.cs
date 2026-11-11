/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using System.Runtime.CompilerServices;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>
/// The one callback inbox shared by every browser exchange: who gets which callback, what is dropped, when an inbox is
/// open, and what happens when it fails. The dispatcher is the only concurrent piece of the sign-in, so each test
/// decides the order of events itself: a callback is delivered by hand, and the next one is held until the inbox it
/// goes to is known to be open.
/// </summary>
public sealed class BrowserCallbackDispatcherTests
{
   private const string Dropped = "WARN A browser callback that belongs to no waiting sign-in or sign-out was dropped. Nothing of it is logged.";
   private const string InboxOpen = "INFO Browser callback inbox open.";



   /// <summary>A callback on the registered address carrying the given state and a one-time code, as a provider sends it.</summary>
   private static Uri CallbackFor(string state) => new(SignInWorld.Callback + "?code=synthetic-code&state=" + state);



   /// <summary>
   /// Two servers sign in through the browser at the same time. Both wait on the one inbox, which was open before
   /// either browser page was. The second sign-in's callback arrives first and completes the second sign-in only; the
   /// inbox is closed and a new one opened for the first, whose callback then completes it. Each session was handed
   /// its own callback, and nothing of either reached the log.
   /// </summary>
   [Fact]
   public async Task Dispatcher_TwoSignIns_EachGetsItsOwnCallback()
   {
      SignInWorld world = new();
      using CompanionSignIn first = await world.BrowserReadyAsync();
      using CompanionSignIn second = await world.BrowserReadyAsync(SignInWorld.OtherServer);
      FakeSession firstSession = world.Sessions[0];
      FakeSession secondSession = world.Sessions[1];

      Task<QqqSessionCredential?> firstRunning = first.SignInWithBrowserAsync();
      Task<QqqSessionCredential?> secondRunning = second.SignInWithBrowserAsync();
      FakeInbox inbox = Assert.Single(world.Inboxes);
      Assert.Equal([1, 1], world.ListeningAtLaunch);
      Assert.Equal([firstSession.Authorization, secondSession.Authorization], world.Opened);
      Assert.NotEqual(firstSession.Authorization, secondSession.Authorization);
      Task<FakeInbox> reopened = world.NextInboxAsync();
      inbox.Deliver(secondSession.CallbackForLatestAttempt);
      QqqSessionCredential? secondCredential = await SignInWorld.EndsAsync(secondRunning);
      FakeInbox next = await reopened;

      Assert.Equal(new Uri(SignInWorld.OtherServer), secondCredential?.Origin);
      Assert.Equal(CompanionSignInState.SignedIn, second.State);
      Assert.False(firstRunning.IsCompleted);
      Assert.Equal(CompanionSignInState.WaitingForBrowser, first.State);
      Assert.Null(firstSession.Callback);
      Assert.True(inbox.Disposed);
      Assert.False(next.Disposed);

      next.Deliver(firstSession.CallbackForLatestAttempt);
      QqqSessionCredential? firstCredential = await SignInWorld.EndsAsync(firstRunning);

      Assert.Equal(new Uri(SignInWorld.Server), firstCredential?.Origin);
      Assert.Equal(CompanionSignInState.SignedIn, first.State);
      Assert.Equal(firstSession.CallbackForLatestAttempt, firstSession.Callback);
      Assert.Equal(secondSession.CallbackForLatestAttempt, secondSession.Callback);
      Assert.True(next.Disposed);
      Assert.Equal(2, world.Inboxes.Count);
      Assert.DoesNotContain(Dropped, world.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains("synthetic-state", StringComparison.Ordinal) || entry.Contains("one-time-code", StringComparison.Ordinal) || entry.Contains("synthetic-session-token", StringComparison.Ordinal));
   }



   /// <summary>
   /// One of two sign-ins is cancelled, and its browser page comes back afterwards. That late callback belongs to
   /// nobody: it is dropped, the log says only that one was dropped, the session that started it never sees it, and
   /// the other sign-in is neither completed nor failed by it. The other sign-in's own callback, held until the new
   /// inbox is open, then completes it.
   /// </summary>
   [Fact]
   public async Task Dispatcher_LateCallbackForAnEndedSignIn_IsDropped_TheOtherCompletes()
   {
      SignInWorld world = new();
      using CompanionSignIn ended = await world.BrowserReadyAsync();
      using CompanionSignIn other = await world.BrowserReadyAsync(SignInWorld.OtherServer);
      FakeSession endedSession = world.Sessions[0];
      FakeSession otherSession = world.Sessions[1];
      Task<QqqSessionCredential?> endedRunning = ended.SignInWithBrowserAsync();
      Task<QqqSessionCredential?> otherRunning = other.SignInWithBrowserAsync();
      FakeInbox inbox = Assert.Single(world.Inboxes);
      ended.CancelBrowserSignIn();
      Assert.Null(await SignInWorld.EndsAsync(endedRunning));
      Assert.False(inbox.Disposed);
      world.Log.Entries.Clear();

      Task<FakeInbox> reopened = world.NextInboxAsync();
      inbox.Deliver(endedSession.CallbackForLatestAttempt);
      FakeInbox next = await reopened;

      Assert.Equal([Dropped, InboxOpen], world.Log.Entries);
      Assert.Null(endedSession.Callback);
      Assert.Equal(CompanionSignInState.BrowserCanceled, ended.State);
      Assert.False(otherRunning.IsCompleted);
      Assert.Equal(CompanionSignInState.WaitingForBrowser, other.State);
      Assert.True(inbox.Disposed);

      next.Deliver(otherSession.CallbackForLatestAttempt);
      QqqSessionCredential? credential = await SignInWorld.EndsAsync(otherRunning);

      Assert.Equal(new Uri(SignInWorld.OtherServer), credential?.Origin);
      Assert.Equal(otherSession.CallbackForLatestAttempt, otherSession.Callback);
      Assert.True(next.Disposed);
      Assert.Equal(2, world.Inboxes.Count);
      Assert.Single(world.Log.Entries, entry => string.Equals(entry, Dropped, StringComparison.Ordinal));
   }



   /// <summary>
   /// A callback that cannot be given to anyone is dropped: one with no state, with the state twice, with an empty
   /// state, with a parameter whose name only looks like "state", or with a state nobody waits for. A state is
   /// somebody's only when it is exactly the one that was registered: the same letters in another case, the
   /// beginning of it, more than it, or it with a space after it are all nobody's. The ticket that waits is
   /// untouched, a new inbox is opened for it, and the log holds the fixed sentence and nothing of the callback.
   /// The ticket's own callback, sent to the new inbox, still reaches it. A callback the session would refuse to
   /// read is nobody's too, even with the right state in it: a query that starts with a second question mark, an
   /// entry without a value, a repeated name, a control character, or a fragment. Handed to the exchange, each of
   /// these would have ended its sign-in; dropped, it leaves the exchange waiting for the real one.
   /// </summary>
   [Theory]
   [InlineData("?code=synthetic-code")]
   [InlineData("?state=synthetic-state-x&state=synthetic-state-x")]
   [InlineData("?code=synthetic-code&state=")]
   [InlineData("?STATE=synthetic-state-x")]
   [InlineData("?state")]
   [InlineData("?state=synthetic-state-unknown&code=synthetic-code")]
   [InlineData("?code=synthetic-code&state=SYNTHETIC-STATE-X")]
   [InlineData("?code=synthetic-code&state=synthetic-state-")]
   [InlineData("?code=synthetic-code&state=synthetic-state-xy")]
   [InlineData("?code=synthetic-code&state=synthetic-state-x%20")]
   [InlineData("??state=synthetic-state-x")]
   [InlineData("?state=synthetic-state-x&synthetic-entry-without-a-value")]
   [InlineData("?code=synthetic-code&code=synthetic-code&state=synthetic-state-x")]
   [InlineData("?state=synthetic-state-x&code=synthetic%00code")]
   [InlineData("?state=synthetic-state-x#synthetic-fragment")]
   public async Task Dispatcher_CallbackWithNoStateOrTwo_IsDropped_AndNotLogged(string query)
   {
      SignInWorld world = new();
      using BrowserCallbackTicket ticket = world.Dispatcher.Register("synthetic-state-x");
      FakeInbox inbox = Assert.Single(world.Inboxes);

      Task<FakeInbox> reopened = world.NextInboxAsync();
      inbox.Deliver(new Uri(SignInWorld.Callback + query));
      FakeInbox next = await reopened;

      Assert.False(ticket.Callback.IsCompleted);
      Assert.True(inbox.Disposed);
      Assert.False(next.Disposed);
      Assert.Equal([InboxOpen, Dropped, InboxOpen], world.Log.Entries);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains("synthetic", StringComparison.Ordinal) || entry.Contains("oauth", StringComparison.OrdinalIgnoreCase));

      next.Deliver(CallbackFor("synthetic-state-x"));

      Assert.Equal(CallbackFor("synthetic-state-x"), await SignInWorld.EndsAsync(ticket.Callback));
      Assert.True(next.Disposed);
      Assert.Equal(2, world.Inboxes.Count);
   }



   /// <summary>
   /// No inbox exists until something waits. The first ticket opens one and the dispatcher is waiting on it when the
   /// registration returns; a second ticket shares it; it stays open while either waits and is closed with the last,
   /// without that being logged as a failure. A ticket that ended can be disposed again, and that does not take away
   /// a later ticket that waits under the same state. The next registration opens a new inbox. The test runs without
   /// a synchronization context, as the dispatcher's own threads do: closing an inbox then ends the dispatcher's wait
   /// on it inside the very call that closes it, which is the order of events the closing has to get right.
   /// </summary>
   [Fact]
   public void Dispatcher_InboxOpensWithTheFirstTicket_ClosesWithTheLast()
   {
      using NoContext none = new();
      SignInWorld world = new();
      Assert.Empty(world.Inboxes);

      BrowserCallbackTicket first = world.Dispatcher.Register("synthetic-state-a");
      FakeInbox inbox = Assert.Single(world.Inboxes);
      Assert.True(inbox.Waiting);
      BrowserCallbackTicket second = world.Dispatcher.Register("synthetic-state-b");
      Assert.Single(world.Inboxes);

      first.Dispose();
      Assert.False(inbox.Disposed);
      Assert.True(first.Callback.IsCanceled);
      Assert.False(second.Callback.IsCompleted);
      second.Dispose();
      Assert.True(inbox.Disposed);
      Assert.True(second.Callback.IsCanceled);
      Assert.Equal([InboxOpen], world.Log.Entries);

      BrowserCallbackTicket later = world.Dispatcher.Register("synthetic-state-a");
      Assert.Equal(2, world.Inboxes.Count);
      Assert.True(world.Inboxes[1].Waiting);
      first.Dispose();
      Assert.False(world.Inboxes[1].Disposed);
      Assert.False(later.Callback.IsCompleted);
      later.Dispose();
      Assert.True(world.Inboxes[1].Disposed);
      Assert.Equal([InboxOpen, InboxOpen], world.Log.Entries);
   }



   /// <summary>
   /// The inbox breaks while two exchanges wait. Both waits fail with that failure, once: the log has one line for
   /// it, which names the kind of failure and how many it ended and nothing of the failure's own text, and the broken
   /// inbox is closed. The failed tickets are no longer registered, so disposing them changes nothing. The next
   /// registration opens a new inbox, which works.
   /// </summary>
   [Fact]
   public async Task Dispatcher_InboxFails_EveryWaiterFailsOnce_TheNextOpensANewOne()
   {
      SignInWorld world = new();
      using BrowserCallbackTicket first = world.Dispatcher.Register("synthetic-state-a");
      using BrowserCallbackTicket second = world.Dispatcher.Register("synthetic-state-b");
      FakeInbox inbox = Assert.Single(world.Inboxes);
      IOException failure = new("synthetic pipe failure");

      inbox.Fail(failure);

      Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => SignInWorld.EndsAsync(first.Callback)));
      Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => SignInWorld.EndsAsync(second.Callback)));
      Assert.True(inbox.Disposed);
      Assert.Equal([InboxOpen, "ERROR The browser callback inbox failed (IOException); 2 waiting browser exchange(s) were ended and have to be started again."], world.Log.Entries);

      first.Dispose();
      second.Dispose();
      Assert.Single(world.Inboxes);
      using BrowserCallbackTicket next = world.Dispatcher.Register("synthetic-state-a");
      Assert.Equal(2, world.Inboxes.Count);
      Assert.False(next.Callback.IsCompleted);
      world.Inboxes[1].Deliver(CallbackFor("synthetic-state-a"));

      Assert.Equal(CallbackFor("synthetic-state-a"), await SignInWorld.EndsAsync(next.Callback));
      Assert.Single(world.Log.Entries, entry => entry.StartsWith("ERROR", StringComparison.Ordinal));
   }



   /// <summary>
   /// The inbox is fed by other processes and can fail in a way no exchange lists as a failure to report: the real
   /// one gives an argument error when the process that sent a callback has already exited. Left as it is, such a
   /// failure would leave every waiting exchange as an exception nobody handles. Both waiting tickets get one
   /// failure of a kind every exchange reports instead: its text is fixed and names the kind of failure, the
   /// original is not attached to it, and nothing of the original's own text reaches the log.
   /// </summary>
   [Theory]
   [InlineData(nameof(ArgumentException))]
   [InlineData(nameof(OverflowException))]
   [InlineData(nameof(TimeoutException))]
   public async Task Dispatcher_InboxFailsInAWayNoExchangeLists_EveryWaiterGetsAFailureItReports(string kind)
   {
      SignInWorld world = new();
      using BrowserCallbackTicket first = world.Dispatcher.Register("synthetic-state-a");
      using BrowserCallbackTicket second = world.Dispatcher.Register("synthetic-state-b");

      world.Inboxes[0].Fail(SignInWorld.UnlistedInboxFailure(kind));

      IOException reported = await Assert.ThrowsAsync<IOException>(() => SignInWorld.EndsAsync(first.Callback));
      Assert.Same(reported, await Assert.ThrowsAsync<IOException>(() => SignInWorld.EndsAsync(second.Callback)));
      Assert.Equal(SignInWorld.InboxFailedReport(kind), reported.Message);
      Assert.Null(reported.InnerException);
      Assert.DoesNotContain(SignInWorld.UnlistedFailureText, reported.ToString(), StringComparison.Ordinal);
      Assert.True(world.Inboxes[0].Disposed);
      Assert.Equal([InboxOpen, $"ERROR The browser callback inbox failed ({kind}); 2 waiting browser exchange(s) were ended and have to be started again."], world.Log.Entries);
   }



   /// <summary>
   /// The same holds when the inbox cannot be opened: a failure of a kind no exchange lists is thrown to whoever
   /// registered as one that names only its kind, nothing of its text is kept, and nothing is left registered.
   /// </summary>
   [Fact]
   public async Task InboxThatCannotBeOpenedInAWayNoExchangeLists_FailsTheRegistrationWithAFailureItReports()
   {
      SignInWorld world = new() { InboxFailure = SignInWorld.UnlistedInboxFailure(nameof(ArgumentException)) };

      IOException reported = Assert.Throws<IOException>(() => world.Dispatcher.Register("synthetic-state-a"));

      Assert.Equal(SignInWorld.InboxFailedReport(nameof(ArgumentException)), reported.Message);
      Assert.Null(reported.InnerException);
      Assert.DoesNotContain(SignInWorld.UnlistedFailureText, reported.ToString(), StringComparison.Ordinal);
      Assert.Empty(world.Inboxes);
      Assert.Empty(world.Log.Entries);

      world.InboxFailure = null;
      using BrowserCallbackTicket ticket = world.Dispatcher.Register("synthetic-state-a");
      Assert.Single(world.Inboxes).Deliver(CallbackFor("synthetic-state-a"));
      Assert.Equal(CallbackFor("synthetic-state-a"), await SignInWorld.EndsAsync(ticket.Callback));
   }



   /// <summary>When the inbox cannot be opened for the first ticket, the failure goes to whoever registered and nothing is left registered: the same state can be registered again once the inbox can be opened.</summary>
   [Fact]
   public async Task InboxThatCannotBeOpened_FailsTheRegistration_AndLeavesNothingBehind()
   {
      SignInWorld world = new() { InboxFailure = new UnauthorizedAccessException("synthetic open failure") };

      Assert.Same(world.InboxFailure, Assert.Throws<UnauthorizedAccessException>(() => world.Dispatcher.Register("synthetic-state-a")));
      Assert.Empty(world.Inboxes);
      Assert.Empty(world.Log.Entries);

      world.InboxFailure = null;
      using BrowserCallbackTicket ticket = world.Dispatcher.Register("synthetic-state-a");
      Assert.Single(world.Inboxes).Deliver(CallbackFor("synthetic-state-a"));

      Assert.Equal(CallbackFor("synthetic-state-a"), await SignInWorld.EndsAsync(ticket.Callback));
   }



   /// <summary>
   /// After a callback the inbox has to be opened again for the tickets that still wait. When that fails, the callback
   /// that did arrive still completes its own ticket, and the ticket left waiting fails with the failure, because
   /// nothing could reach it any more. The next registration tries a new inbox.
   /// </summary>
   [Fact]
   public async Task InboxThatCannotBeReopened_CompletesTheCallbackItHas_AndFailsWhoStillWaits()
   {
      SignInWorld world = new();
      using BrowserCallbackTicket served = world.Dispatcher.Register("synthetic-state-a");
      using BrowserCallbackTicket stranded = world.Dispatcher.Register("synthetic-state-b");
      UnauthorizedAccessException failure = new("synthetic open failure");
      world.InboxFailure = failure;

      world.Inboxes[0].Deliver(CallbackFor("synthetic-state-a"));

      Assert.Equal(CallbackFor("synthetic-state-a"), await SignInWorld.EndsAsync(served.Callback));
      Assert.Same(failure, await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SignInWorld.EndsAsync(stranded.Callback)));
      Assert.True(world.Inboxes[0].Disposed);
      Assert.Equal([InboxOpen, "ERROR The browser callback inbox failed (UnauthorizedAccessException); 1 waiting browser exchange(s) were ended and have to be started again."], world.Log.Entries);

      world.InboxFailure = null;
      using BrowserCallbackTicket next = world.Dispatcher.Register("synthetic-state-b");
      Assert.Equal(2, world.Inboxes.Count);
      Assert.False(next.Callback.IsCompleted);
   }



   /// <summary>
   /// A callback, or a failure, can come out of an inbox at the very moment its last ticket ends and the inbox is
   /// closed. The inboxes here ignore being closed and answer when told to, which is that moment made certain, and by
   /// then another exchange has registered and a new inbox is open. The late callback is dropped like any other that
   /// reached nobody, even though it carries the state that now waits, and the late failure is not reported at all,
   /// because the closing caused it. Either way the exchange that waits on the new inbox is untouched, and its own
   /// callback completes it. The test runs without a synchronization context, as the dispatcher's own threads do, so
   /// the dispatcher deals with each answer inside the call that gives it and the test reads the result straight after.
   /// </summary>
   [Theory]
   [InlineData(true, new[] { InboxOpen, InboxOpen, Dropped })]
   [InlineData(false, new[] { InboxOpen, InboxOpen })]
   public void AnswerFromAnInboxThatWasClosedMeanwhile_DoesNotTouchTheNextExchange(bool callback, string[] entries)
   {
      using NoContext none = new();
      RecordingLog log = new();
      LateInboxes source = new();
      BrowserCallbackDispatcher dispatcher = new(source, log);
      BrowserCallbackTicket ended = dispatcher.Register("synthetic-state-a");
      ended.Dispose();
      Assert.True(source.Inboxes[0].Disposed);
      using BrowserCallbackTicket waiting = dispatcher.Register("synthetic-state-a");

      if(callback)
      {
         source.Inboxes[0].Answer.SetResult(CallbackFor("synthetic-state-a"));
      }
      else
      {
         source.Inboxes[0].Answer.SetException(new IOException("synthetic pipe failure"));
      }

      Assert.Equal(entries, log.Entries, StringComparer.Ordinal);
      Assert.True(ended.Callback.IsCanceled);
      Assert.False(waiting.Callback.IsCompleted);
      Assert.Equal(2, source.Inboxes.Count);
      Assert.False(source.Inboxes[1].Disposed);

      source.Inboxes[1].Answer.SetResult(CallbackFor("synthetic-state-a"));

      Assert.True(waiting.Callback.IsCompletedSuccessfully);
      Assert.True(source.Inboxes[1].Disposed);
      Assert.Equal(2, source.Inboxes.Count);
   }



   /// <summary>
   /// A log that cannot be written to does not stop the dispatcher. Every entry it writes fails here, and all the
   /// same: a registration returns its ticket with the inbox open and being read; a callback for nobody is dropped
   /// and the next inbox is opened and read; the ticket's own callback reaches it; and when an inbox breaks, the
   /// tickets that wait are failed and not left waiting. Each of the three entries was attempted, so the test is
   /// about entries that failed and not about entries that were never written.
   /// </summary>
   [Fact]
   public async Task LogThatThrows_DoesNotBreakTheDispatcher()
   {
      SignInWorld world = new();
      ThrowingLog log = new();
      BrowserCallbackDispatcher dispatcher = new(world, log);

      using BrowserCallbackTicket ticket = dispatcher.Register("synthetic-state-a");
      FakeInbox inbox = Assert.Single(world.Inboxes);
      Assert.True(inbox.Waiting);
      Task<FakeInbox> reopened = world.NextInboxAsync();
      inbox.Deliver(CallbackFor("synthetic-state-of-nobody"));
      FakeInbox next = await reopened;
      Assert.True(inbox.Disposed);
      Assert.False(ticket.Callback.IsCompleted);
      next.Deliver(CallbackFor("synthetic-state-a"));
      Assert.Equal(CallbackFor("synthetic-state-a"), await SignInWorld.EndsAsync(ticket.Callback));

      using BrowserCallbackTicket first = dispatcher.Register("synthetic-state-b");
      using BrowserCallbackTicket second = dispatcher.Register("synthetic-state-c");
      IOException failure = new("synthetic pipe failure");
      world.Inboxes[2].Fail(failure);

      Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => SignInWorld.EndsAsync(first.Callback)));
      Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => SignInWorld.EndsAsync(second.Callback)));
      Assert.All(world.Inboxes, opened => Assert.True(opened.Disposed));
      Assert.Equal(["INFO", "WARN", "INFO", "INFO", "ERROR"], log.Attempts);
   }



   /// <summary>
   /// An inbox can have its callback ready the moment it is opened, and the next one too. Two hundred inboxes in a
   /// row answer at once here, each with a callback for nobody. The dispatcher drops each and opens the next in one
   /// loop: the stack is no deeper when the last inbox is opened than when the second was, where a call per inbox
   /// would have gone deeper with every callback. The ticket is still waiting afterwards, on the first inbox that
   /// did not answer at once, and its own callback reaches it there.
   /// </summary>
   [Fact]
   public async Task InboxesThatAnswerAtOnce_AreWorkedThroughInALoop()
   {
      EagerInboxes source = new(200);
      RecordingLog log = new();
      BrowserCallbackDispatcher dispatcher = new(source, log);

      using BrowserCallbackTicket ticket = dispatcher.Register("synthetic-state-a");

      Assert.Equal(201, source.Depths.Count);
      Assert.Equal(200, log.Entries.Count(entry => string.Equals(entry, Dropped, StringComparison.Ordinal)));
      Assert.Equal(201, log.Entries.Count(entry => string.Equals(entry, InboxOpen, StringComparison.Ordinal)));
      Assert.InRange(source.Depths.Skip(1).Max() - source.Depths[1], 0, 8);
      Assert.False(ticket.Callback.IsCompleted);

      source.Waiting.SetResult(CallbackFor("synthetic-state-a"));

      Assert.Equal(CallbackFor("synthetic-state-a"), await SignInWorld.EndsAsync(ticket.Callback));
      Assert.Equal(201, source.Depths.Count);
   }



   /// <summary>
   /// Two exchanges cannot wait under one state, because a callback could not be told to belong to one of them. The
   /// second registration is refused without naming the state, and the first is left as it was. A state of nothing is
   /// refused before anything is opened.
   /// </summary>
   [Fact]
   public async Task StateThatIsAlreadyWaiting_OrEmpty_IsRefused()
   {
      SignInWorld world = new();
      Assert.Throws<ArgumentException>(() => world.Dispatcher.Register(""));
      Assert.Empty(world.Inboxes);
      using BrowserCallbackTicket ticket = world.Dispatcher.Register("synthetic-state-a");

      InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => world.Dispatcher.Register("synthetic-state-a"));

      Assert.Equal("A browser exchange with the same state is already waiting for its callback.", refused.Message);
      Assert.Single(world.Inboxes).Deliver(CallbackFor("synthetic-state-a"));
      Assert.Equal(CallbackFor("synthetic-state-a"), await SignInWorld.EndsAsync(ticket.Callback));
   }



   /// <summary>
   /// The state of an address is its one "state" parameter, decoded: found wherever it stands in the query, with a
   /// plus sign and a percent escape read as the provider wrote them, in the name as well as in the value. There is
   /// none when the parameter is missing, empty, repeated or named differently, when the address has no query, and
   /// when it is not an absolute address. There is none either when the session would refuse the query: it starts
   /// with a second question mark, an entry has no value, any name is repeated, a name or a value holds a control
   /// character, or the address has a fragment or user information.
   /// </summary>
   [Theory]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?%73tate=synthetic-state-a", "synthetic-state-a")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=synthetic-state-a&State=synthetic-state-b", "synthetic-state-a")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback??state=synthetic-state-a", null)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=synthetic-state-a&synthetic", null)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?code=a&code=b&state=synthetic-state-a", null)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=synthetic-state-a%00", null)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?synthetic%0Aname=a&state=synthetic-state-a", null)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=synthetic-state-a#state=synthetic-state-b", null)]
   [InlineData("https://synthetic-user@id.example.com/authorize?state=synthetic-state-a", null)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?", null)]
   [InlineData("https://id.example.com/authorize?client_id=synthetic&state=synthetic-state-a&nonce=synthetic", "synthetic-state-a")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=synthetic%2Dstate+a", "synthetic-state a")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=synthetic=state", "synthetic=state")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=a&state=b", null)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=", null)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state", null)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?mystate=synthetic", null)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback", null)]
   [InlineData("/oauth/callback?state=synthetic-state-a", null)]
   public void ReadState_IsTheOneStateParameter(string address, string? state)
   {
      Assert.Equal(state, BrowserCallbackDispatcher.ReadState(new Uri(address, UriKind.RelativeOrAbsolute)));
   }



   /// <summary>
   /// An inbox can fail after an exchange has stopped waiting and before it has disposed its ticket. The ticket is
   /// then failed and never looked at. The runtime reports a failed task nobody looked at as an unobserved error
   /// when the task is collected, and a ticket must not be the cause of one. The test fails a ticket, lets it go
   /// without looking at it, and has everything collected; beside it, as the control, an ordinary task is failed
   /// and let go the same way. The control is reported, which shows that the collection did what it had to, and
   /// the ticket is not. Each failure is told from any other test's by its own text, and nothing here waits: the
   /// collections run to completion before the counts are read.
   /// </summary>
   [Fact]
   public void TicketFailedAndNeverLookedAt_LeavesNoUnobservedFailure()
   {
      const string ticketText = "synthetic failure of a ticket nobody looked at";
      const string controlText = "synthetic failure of a task nobody looked at";
      int tickets = 0;
      int controls = 0;
      void Count(object? sender, UnobservedTaskExceptionEventArgs unobserved)
      {
         tickets += unobserved.Exception.InnerExceptions.Count(inner => string.Equals(inner.Message, ticketText, StringComparison.Ordinal));
         controls += unobserved.Exception.InnerExceptions.Count(inner => string.Equals(inner.Message, controlText, StringComparison.Ordinal));
      }
      TaskScheduler.UnobservedTaskException += Count;
      try
      {
         FailATicketAndLetItGo(ticketText);
         FailATaskAndLetItGo(controlText);
         for(int round = 0; round < 3; round++)
         {
            GC.Collect();
            GC.WaitForPendingFinalizers();
         }
      }
      finally
      {
         TaskScheduler.UnobservedTaskException -= Count;
      }

      Assert.Equal(1, controls);
      Assert.Equal(0, tickets);
   }



   /// <summary>Registers a ticket, breaks its inbox so the ticket is failed inside that call, disposes the ticket without looking at its task, and returns with nothing left that refers to it. It is not inlined, so the caller's frame holds nothing of it either.</summary>
   [MethodImpl(MethodImplOptions.NoInlining)]
   private static void FailATicketAndLetItGo(string text)
   {
      using NoContext none = new();
      LateInboxes source = new();
      BrowserCallbackDispatcher dispatcher = new(source, new RecordingLog());
      BrowserCallbackTicket ticket = dispatcher.Register("synthetic-state-a");
      source.Inboxes[0].Answer.SetException(new IOException(text));
      Assert.True(ticket.Callback.IsFaulted);
      ticket.Dispose();
   }



   /// <summary>The control: fails an ordinary task and returns with nothing left that refers to it.</summary>
   [MethodImpl(MethodImplOptions.NoInlining)]
   private static void FailATaskAndLetItGo(string text)
   {
      TaskCompletionSource<Uri> source = new();
      source.SetException(new IOException(text));
      Assert.True(source.Task.IsFaulted);
   }



   /// <summary>
   /// A ticket's task carries a callback with its one-time code and state, so code outside the presentation assembly
   /// can neither see a ticket nor register for one: the ticket is not a public type and offers no public property,
   /// and the dispatcher's public surface is its constructor alone.
   /// </summary>
   [Fact]
   public void TicketAndRegistration_AreNotOfferedOutsideTheAssembly()
   {
      const BindingFlags declaredPublic = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly;

      Assert.False(typeof(BrowserCallbackTicket).IsVisible);
      Assert.Empty(typeof(BrowserCallbackTicket).GetProperties(declaredPublic));
      Assert.True(typeof(BrowserCallbackDispatcher).IsVisible);
      Assert.Empty(typeof(BrowserCallbackDispatcher).GetMethods(declaredPublic));
      Assert.Empty(typeof(BrowserCallbackDispatcher).GetProperties(declaredPublic));
      Assert.Single(typeof(BrowserCallbackDispatcher).GetConstructors(declaredPublic));
   }



   /// <summary>A query is read up to the length the session accepts for a callback and not beyond: one character more, and the address has no state.</summary>
   [Fact]
   public void ReadState_QueryLongerThanACallbacksMayBe_HasNoState()
   {
      const string prefix = "?state=synthetic-state-a&synthetic=";
      string longest = prefix + new string('a', BrowserCallbackDispatcher.LongestQuery - prefix.Length);

      Assert.Equal(16_384, BrowserCallbackDispatcher.LongestQuery);
      Assert.Equal("synthetic-state-a", BrowserCallbackDispatcher.ReadState(new Uri(SignInWorld.Callback + longest)));
      Assert.Null(BrowserCallbackDispatcher.ReadState(new Uri(SignInWorld.Callback + longest + "a")));
   }



   /// <summary>The dispatcher cannot work without a place callbacks arrive and a log, and says which is missing.</summary>
   [Fact]
   public void MissingCollaborator_IsRefused()
   {
      SignInWorld world = new();

      Assert.Equal("callbacks", Assert.Throws<ArgumentNullException>(() => new BrowserCallbackDispatcher(null!, world.Log)).ParamName);
      Assert.Equal("log", Assert.Throws<ArgumentNullException>(() => new BrowserCallbackDispatcher(world, null!)).ParamName);
   }



   /// <summary>A log that fails on every entry, as a log whose file cannot be written would, and notes the level of each entry that was attempted.</summary>
   private sealed class ThrowingLog : IPresentationLog
   {
      /// <summary>The level of every entry that was attempted, oldest first.</summary>
      internal List<string> Attempts { get; } = [];



      /// <summary>Fails.</summary>
      public void Info(string message) => Fail("INFO");



      /// <summary>Fails.</summary>
      public void Warning(string message) => Fail("WARN");



      /// <summary>Fails.</summary>
      public void Error(string message) => Fail("ERROR");



      /// <summary>Fails.</summary>
      public void Debug(string message) => Fail("DEBUG");



      /// <summary>Notes the attempt and fails.</summary>
      private void Fail(string level)
      {
         lock(Attempts)
         {
            Attempts.Add(level);
         }
         throw new IOException("synthetic log failure");
      }
   }



   /// <summary>A callback source whose first inboxes answer the moment they are waited on, each with a callback for nobody, and whose next inbox waits for the test. It notes how deep the stack is each time an inbox is opened.</summary>
   private sealed class EagerInboxes(int eager) : IBrowserCallbackSource
   {
      /// <summary>The depth of the stack at each opening of an inbox, oldest first.</summary>
      internal List<int> Depths { get; } = [];



      /// <summary>What the first inbox that does not answer at once waits for.</summary>
      internal TaskCompletionSource<Uri> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>The registered callback.</summary>
      public string RedirectUri => SignInWorld.Callback;



      /// <summary>Not read by the dispatcher.</summary>
      public string TracePath => "trace/auth-callback.log";



      /// <summary>Notes the depth of the stack and opens the next inbox.</summary>
      public IBrowserCallbackInbox Open()
      {
         Depths.Add(new System.Diagnostics.StackTrace().FrameCount);
         return new EagerInbox(Depths.Count <= eager ? Task.FromResult(CallbackFor("synthetic-state-of-nobody")) : Waiting.Task);
      }



      /// <summary>Not read by the dispatcher.</summary>
      public IReadOnlyList<string> RecentTrace(TimeSpan window) => [];
   }



   /// <summary>An inbox whose wait is the task it was given: one that is already complete, or one the test completes.</summary>
   private sealed class EagerInbox(Task<Uri> answer) : IBrowserCallbackInbox
   {
      /// <summary>Returns the answer, which may already be there.</summary>
      public Task<Uri> ReceiveAsync(CancellationToken cancellationToken) => answer;



      /// <summary>Nothing to close.</summary>
      public void Dispose()
      {
      }
   }



   /// <summary>A callback source whose inboxes do not notice being closed and answer when the test says so, on the test's own thread.</summary>
   private sealed class LateInboxes : IBrowserCallbackSource
   {
      /// <summary>Every inbox that was opened, oldest first.</summary>
      internal List<LateInbox> Inboxes { get; } = [];



      /// <summary>The registered callback.</summary>
      public string RedirectUri => SignInWorld.Callback;



      /// <summary>Not read by the dispatcher.</summary>
      public string TracePath => "trace/auth-callback.log";



      /// <summary>Opens a new inbox.</summary>
      public IBrowserCallbackInbox Open()
      {
         LateInbox inbox = new();
         Inboxes.Add(inbox);
         return inbox;
      }



      /// <summary>Not read by the dispatcher.</summary>
      public IReadOnlyList<string> RecentTrace(TimeSpan window) => [];
   }



   /// <summary>An inbox whose wait ends only when the test completes <see cref="Answer"/>, whatever happens to the token or the inbox meanwhile. The wait's continuation runs inside that completion, so the test continues after the dispatcher has dealt with the answer.</summary>
   private sealed class LateInbox : IBrowserCallbackInbox
   {
      /// <summary>What the wait ends with; completing it runs the dispatcher's handling at once.</summary>
      internal TaskCompletionSource<Uri> Answer { get; } = new();



      /// <summary>True once the dispatcher closed the inbox.</summary>
      internal bool Disposed { get; private set; }



      /// <summary>Waits for the answer and ignores the token.</summary>
      public Task<Uri> ReceiveAsync(CancellationToken cancellationToken) => Answer.Task;



      /// <summary>Marks the inbox closed.</summary>
      public void Dispose() => Disposed = true;
   }
}
