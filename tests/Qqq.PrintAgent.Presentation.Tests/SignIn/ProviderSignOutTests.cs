/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>The identity provider's half of a sign-out: its page is opened, its return is recognized by this sign-out's own state within two minutes, the result says how it ended, and nothing of the request reaches the log.</summary>
public sealed class ProviderSignOutTests
{
   private const string Hint = "synthetic-id-token-hint";
   private const string State = "synthetic-sign-out-state";



   /// <summary>A provider sign-out as the service returns one: the end-session address with its token hint, the registered callback and this sign-out's state.</summary>
   private static QqqProviderLogout Logout(string provider = "id.example.com") => new(new Uri("https://" + provider + "/end-session?id_token_hint=" + Hint + "&state=" + State), new Uri(SignInWorld.Callback), State);



   /// <summary>A provider sign-out over the world's dispatcher, browser, log and clock.</summary>
   private static ProviderSignOut Over(SignInWorld world) => new(world, world.Dispatcher, world.Log, world.Time);



   /// <summary>
   /// The inbox is open before the provider's page is, the page is the address the service returned, and the wait has
   /// a two minute limit. A callback carrying another exchange's state is not this sign-out's: it is dropped and the
   /// wait goes on. The callback with this sign-out's state, on the registered address, confirms it. The log names
   /// the provider's host and nothing else of the request: no token hint and no state.
   /// </summary>
   [Fact]
   public async Task ProviderSignOut_MatchesItsOwnState_WithinTwoMinutes()
   {
      SignInWorld world = new();
      QqqProviderLogout logout = Logout();

      Task<ProviderSignOutResult> running = Over(world).CompleteAsync(logout);
      FakeInbox inbox = Assert.Single(world.Inboxes);
      Assert.Equal([1], world.ListeningAtLaunch);
      Assert.Equal(logout.EndSessionUri, Assert.Single(world.Opened));
      Assert.Equal(TimeSpan.FromMinutes(2), Assert.Single(world.Time.RequestedDelays));
      Assert.Equal(TimeSpan.FromMinutes(2), ProviderSignOut.Limit);
      Task<FakeInbox> reopened = world.NextInboxAsync();
      inbox.Deliver(new Uri(SignInWorld.Callback + "?state=synthetic-state-of-another-exchange"));
      FakeInbox next = await reopened;
      Assert.False(running.IsCompleted);
      next.Deliver(new Uri(SignInWorld.Callback + "?state=" + State));

      Assert.Equal(ProviderSignOutResult.Confirmed, await SignInWorld.EndsAsync(running));
      Assert.True(next.Disposed);
      Assert.Equal(["INFO Browser callback inbox open.",
         "INFO Opening the identity provider's sign-out page in the default browser (id.example.com).",
         "WARN A browser callback that belongs to no waiting sign-in or sign-out was dropped. Nothing of it is logged.",
         "INFO Browser callback inbox open.",
         "INFO Identity provider sign-out callback matched."], world.Log.Entries);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains("synthetic", StringComparison.Ordinal));
   }



   /// <summary>The provider's page is at an address the server's provider wrote, so the log names its host by the ASCII name the browser will really go to: a look-alike letter reads as the "xn--" name. The browser is given the address unchanged.</summary>
   [Fact]
   public async Task ProviderPageWithALookAlikeHost_IsLoggedByItsAsciiName()
   {
      SignInWorld world = new();
      QqqProviderLogout logout = Logout("l\u043Egin.example.com:8443");

      Task<ProviderSignOutResult> running = Over(world).CompleteAsync(logout);
      world.Inboxes[0].Deliver(new Uri(SignInWorld.Callback + "?state=" + State));

      Assert.Equal(ProviderSignOutResult.Confirmed, await SignInWorld.EndsAsync(running));
      Assert.Same(logout.EndSessionUri, Assert.Single(world.Opened));
      Assert.Contains("INFO Opening the identity provider's sign-out page in the default browser (xn--lgin-55d.example.com:8443).", world.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains('\u043E', StringComparison.Ordinal) || entry.Contains("synthetic", StringComparison.Ordinal));
   }



   /// <summary>A callback that carries this sign-out's state but is not on the registered address is handed to the sign-out, which does not take it as the provider finishing: the page was opened, and the provider did not confirm.</summary>
   [Fact]
   public async Task CallbackWithItsStateOnAnotherAddress_DoesNotConfirm()
   {
      SignInWorld world = new();

      Task<ProviderSignOutResult> running = Over(world).CompleteAsync(Logout());
      world.Inboxes[0].Deliver(new Uri("io.qrun.qqq-print-agent:/oauth/other?state=" + State));

      Assert.Equal(ProviderSignOutResult.NotConfirmed, await SignInWorld.EndsAsync(running));
      Assert.Equal("INFO Identity provider sign-out callback did not match this sign-out.", world.Log.Entries[^1]);
      Assert.True(world.Inboxes[0].Disposed);
   }



   /// <summary>
   /// A sign-out that is not confirmed says which way it went, because the operator is told something different for
   /// each. The page was never opened when the browser or the inbox could not be opened. The page was opened and the
   /// provider did not confirm when two minutes passed, when the inbox broke meanwhile, or when the inbox's own wait
   /// was ended by something other than this sign-out. The caller stopped it when its token ended the wait. A
   /// failure is logged as a warning with its kind, running out of time as a warning in fixed words, and the
   /// caller's own cancellation as information in fixed words, without the text of the cancellation. Windows names
   /// what it could not start in its failure, which is the whole sign-out address with the token hint; none of that
   /// reaches the log.
   /// </summary>
   [Theory]
   [InlineData("limit", ProviderSignOutResult.NotConfirmed, 1, "WARN Identity provider sign-out was not confirmed: the provider did not return within the two minute limit.")]
   [InlineData("token", ProviderSignOutResult.Cancelled, 1, "INFO Identity provider sign-out was stopped by its caller before the provider confirmed.")]
   [InlineData("browser", ProviderSignOutResult.NotOpened, 0, "WARN The identity provider's sign-out page was not opened: IOException: " + BrowserStartFailure.WindowsReport)]
   [InlineData("no inbox", ProviderSignOutResult.NotOpened, 0, "WARN The identity provider's sign-out page was not opened: UnauthorizedAccessException: synthetic open failure")]
   [InlineData("inbox", ProviderSignOutResult.NotConfirmed, 1, "WARN Identity provider sign-out was not confirmed: IOException: synthetic pipe failure")]
   [InlineData("inbox wait ended", ProviderSignOutResult.NotConfirmed, 1, "WARN Identity provider sign-out was not confirmed: OperationCanceledException: synthetic end of the inbox's wait")]
   public async Task ProviderThatDoesNotConfirm_SaysWhetherItsPageWasOpened(string ending, ProviderSignOutResult result, int opened, string entry)
   {
      SignInWorld world = new()
      {
         BrowserFailure = string.Equals(ending, "browser", StringComparison.Ordinal) ? new Win32Exception(2, "An error occurred trying to start process '" + Logout().EndSessionUri.AbsoluteUri + "' with working directory 'C:\\synthetic'. The system cannot find the file specified.") : null,
         InboxFailure = string.Equals(ending, "no inbox", StringComparison.Ordinal) ? new UnauthorizedAccessException("synthetic open failure") : null,
      };
      using CancellationTokenSource cancellation = new();

      Task<ProviderSignOutResult> running = Over(world).CompleteAsync(Logout(), cancellation.Token);
      switch(ending)
      {
         case "limit":
            world.Time.Advance(ProviderSignOut.Limit - TimeSpan.FromSeconds(1));
            Assert.False(running.IsCompleted);
            world.Time.Advance(TimeSpan.FromSeconds(1));
            break;
         case "token":
            await cancellation.CancelAsync();
            break;
         case "inbox":
            world.Inboxes[0].Fail(new IOException("synthetic pipe failure"));
            break;
         case "inbox wait ended":
            world.Inboxes[0].Fail(new OperationCanceledException("synthetic end of the inbox's wait"));
            break;
         default:
            break;
      }

      Assert.Equal(result, await SignInWorld.EndsAsync(running));
      Assert.Equal(entry, world.Log.Entries[^1]);
      Assert.Equal(string.Equals(ending, "token", StringComparison.Ordinal), !world.Log.Entries.Exists(line => line.StartsWith("WARN", StringComparison.Ordinal)));
      Assert.All(world.Inboxes, inbox => Assert.True(inbox.Disposed));
      Assert.Equal(opened, world.Opened.Count);
      Assert.DoesNotContain(world.Log.Entries, line => line.Contains(Hint, StringComparison.Ordinal) || line.Contains(State, StringComparison.Ordinal));
   }



   /// <summary>A sign-out whose caller had already stopped waiting when it was started opens nothing: no inbox and no browser page. It ends as cancelled, and says so as information.</summary>
   [Fact]
   public async Task CallerThatHadAlreadyStopped_OpensNothing()
   {
      SignInWorld world = new();
      using CancellationTokenSource cancellation = new();
      await cancellation.CancelAsync();

      Assert.Equal(ProviderSignOutResult.Cancelled, await SignInWorld.EndsAsync(Over(world).CompleteAsync(Logout(), cancellation.Token)));

      Assert.Empty(world.Inboxes);
      Assert.Empty(world.Opened);
      Assert.Equal(["INFO Identity provider sign-out was stopped by its caller before the provider confirmed."], world.Log.Entries);
   }



   /// <summary>
   /// The inbox fails, while the sign-out waits, in a way the sign-out does not list as a failure to report: the
   /// argument error the real inbox gives when the process that sent a callback has already exited, an overflow, a
   /// time-out. Nothing escapes: the sign-out is not confirmed, and the log names the kind of failure in fixed text
   /// and holds nothing of the failure's own text.
   /// </summary>
   [Theory]
   [InlineData(nameof(ArgumentException))]
   [InlineData(nameof(OverflowException))]
   [InlineData(nameof(TimeoutException))]
   public async Task InboxFailsInAWayTheSignOutDoesNotList_IsNotConfirmedAndNothingEscapes(string kind)
   {
      SignInWorld world = new();

      Task<ProviderSignOutResult> running = Over(world).CompleteAsync(Logout());
      Assert.Single(world.Inboxes).Fail(SignInWorld.UnlistedInboxFailure(kind));

      Assert.Equal(ProviderSignOutResult.NotConfirmed, await SignInWorld.EndsAsync(running));
      Assert.Equal("WARN Identity provider sign-out was not confirmed: IOException: " + SignInWorld.InboxFailedReport(kind), world.Log.Entries[^1]);
      Assert.True(world.Inboxes[0].Disposed);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains(SignInWorld.UnlistedFailureText, StringComparison.Ordinal) || entry.Contains(Hint, StringComparison.Ordinal) || entry.Contains(State, StringComparison.Ordinal));
   }



   /// <summary>
   /// When Windows cannot start the browser, the failure it gives names what it could not start: the whole
   /// end-session address, with the provider's ID token hint and this sign-out's state. The sign-out is reported as
   /// not opened, the log says that the default browser could not be opened, with the kind of failure and what to
   /// check, and no log line holds one of those values or any query at all. The same holds for a launcher that fails
   /// in another way and names the address in its text.
   /// </summary>
   [Theory]
   [InlineData(true, BrowserStartFailure.WindowsReport)]
   [InlineData(false, BrowserStartFailure.OtherReport)]
   public async Task BrowserThatCannotBeStarted_IsReportedWithoutAnythingOfTheSignOutAddress(bool asWindows, string report)
   {
      QqqProviderLogout logout = new(BrowserStartFailure.SignOutAddress, new Uri(SignInWorld.Callback), BrowserStartFailure.State);
      SignInWorld world = new() { BrowserFailure = asWindows ? BrowserStartFailure.AsWindows(BrowserStartFailure.SignOutAddress) : BrowserStartFailure.AsAnotherKind(BrowserStartFailure.SignOutAddress) };
      Assert.True(BrowserStartFailure.Leaks(world.BrowserFailure.Message));

      Assert.Equal(ProviderSignOutResult.NotOpened, await SignInWorld.EndsAsync(Over(world).CompleteAsync(logout)));

      Assert.Equal(["INFO Browser callback inbox open.",
         "INFO Opening the identity provider's sign-out page in the default browser (id.example.com).",
         "WARN The identity provider's sign-out page was not opened: IOException: " + report], world.Log.Entries);
      Assert.DoesNotContain(world.Log.Entries, BrowserStartFailure.Leaks);
      Assert.Empty(world.Opened);
      Assert.True(Assert.Single(world.Inboxes).Disposed);
   }



   /// <summary>A provider sign-out cannot work without a browser, the dispatcher and a log, and says which is missing; and it needs a sign-out to complete.</summary>
   [Fact]
   public async Task MissingCollaborator_IsRefused()
   {
      SignInWorld world = new();

      Assert.Equal("browser", Assert.Throws<ArgumentNullException>(() => new ProviderSignOut(null!, world.Dispatcher, world.Log)).ParamName);
      Assert.Equal("dispatcher", Assert.Throws<ArgumentNullException>(() => new ProviderSignOut(world, null!, world.Log)).ParamName);
      Assert.Equal("log", Assert.Throws<ArgumentNullException>(() => new ProviderSignOut(world, world.Dispatcher, null!)).ParamName);
      Assert.Equal("logout", (await Assert.ThrowsAsync<ArgumentNullException>(() => new ProviderSignOut(world, world.Dispatcher, world.Log).CompleteAsync(null!))).ParamName);
   }
}
