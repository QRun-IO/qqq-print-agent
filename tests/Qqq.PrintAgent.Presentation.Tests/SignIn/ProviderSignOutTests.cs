/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>The identity provider's half of a sign-out: its page is opened, its return is recognized by this sign-out's own state within two minutes, and nothing of the request reaches the log.</summary>
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

      Task<bool> running = Over(world).CompleteAsync(logout);
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

      Assert.True(await SignInWorld.EndsAsync(running));
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

      Task<bool> running = Over(world).CompleteAsync(logout);
      world.Inboxes[0].Deliver(new Uri(SignInWorld.Callback + "?state=" + State));

      Assert.True(await SignInWorld.EndsAsync(running));
      Assert.Same(logout.EndSessionUri, Assert.Single(world.Opened));
      Assert.Contains("INFO Opening the identity provider's sign-out page in the default browser (xn--lgin-55d.example.com:8443).", world.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains('\u043E', StringComparison.Ordinal) || entry.Contains("synthetic", StringComparison.Ordinal));
   }



   /// <summary>A callback that carries this sign-out's state but is not on the registered address is handed to the sign-out, which does not take it as the provider finishing.</summary>
   [Fact]
   public async Task CallbackWithItsStateOnAnotherAddress_DoesNotConfirm()
   {
      SignInWorld world = new();

      Task<bool> running = Over(world).CompleteAsync(Logout());
      world.Inboxes[0].Deliver(new Uri("io.qrun.qqq-print-agent:/oauth/other?state=" + State));

      Assert.False(await SignInWorld.EndsAsync(running));
      Assert.Equal("INFO Identity provider sign-out callback did not match this sign-out.", world.Log.Entries[^1]);
      Assert.True(world.Inboxes[0].Disposed);
   }



   /// <summary>
   /// When the provider does not come back within two minutes, the browser cannot be opened, the inbox cannot be
   /// opened or breaks, or the caller stops waiting, the sign-out is not confirmed, and the log says so with the kind
   /// of failure. Windows names what it could not start in its failure, which is the whole sign-out address with the
   /// token hint; none of that reaches the log.
   /// </summary>
   [Theory]
   [InlineData("limit", "TaskCanceledException: A task was canceled.")]
   [InlineData("token", "TaskCanceledException: A task was canceled.")]
   [InlineData("browser", "IOException: " + BrowserStartFailure.WindowsReport)]
   [InlineData("no inbox", "UnauthorizedAccessException: synthetic open failure")]
   [InlineData("inbox", "IOException: synthetic pipe failure")]
   public async Task ProviderThatDoesNotReturn_IsNotConfirmed(string ending, string cause)
   {
      SignInWorld world = new()
      {
         BrowserFailure = string.Equals(ending, "browser", StringComparison.Ordinal) ? new Win32Exception(2, "An error occurred trying to start process '" + Logout().EndSessionUri.AbsoluteUri + "' with working directory 'C:\\synthetic'. The system cannot find the file specified.") : null,
         InboxFailure = string.Equals(ending, "no inbox", StringComparison.Ordinal) ? new UnauthorizedAccessException("synthetic open failure") : null,
      };
      using CancellationTokenSource cancellation = new();

      Task<bool> running = Over(world).CompleteAsync(Logout(), cancellation.Token);
      if(string.Equals(ending, "limit", StringComparison.Ordinal))
      {
         world.Time.Advance(TimeSpan.FromMinutes(2));
      }
      else if(string.Equals(ending, "token", StringComparison.Ordinal))
      {
         await cancellation.CancelAsync();
      }
      else if(string.Equals(ending, "inbox", StringComparison.Ordinal))
      {
         world.Inboxes[0].Fail(new IOException("synthetic pipe failure"));
      }

      Assert.False(await SignInWorld.EndsAsync(running));
      Assert.Equal("WARN Identity provider sign-out was not confirmed: " + cause, world.Log.Entries[^1]);
      Assert.All(world.Inboxes, inbox => Assert.True(inbox.Disposed));
      Assert.Equal(string.Equals(ending, "browser", StringComparison.Ordinal) || string.Equals(ending, "no inbox", StringComparison.Ordinal) ? 0 : 1, world.Opened.Count);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains(Hint, StringComparison.Ordinal) || entry.Contains(State, StringComparison.Ordinal));
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

      Task<bool> running = Over(world).CompleteAsync(Logout());
      Assert.Single(world.Inboxes).Fail(SignInWorld.UnlistedInboxFailure(kind));

      Assert.False(await SignInWorld.EndsAsync(running));
      Assert.Equal("WARN Identity provider sign-out was not confirmed: IOException: " + SignInWorld.InboxFailedReport(kind), world.Log.Entries[^1]);
      Assert.True(world.Inboxes[0].Disposed);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains(SignInWorld.UnlistedFailureText, StringComparison.Ordinal) || entry.Contains(Hint, StringComparison.Ordinal) || entry.Contains(State, StringComparison.Ordinal));
   }



   /// <summary>
   /// When Windows cannot start the browser, the failure it gives names what it could not start: the whole
   /// end-session address, with the provider's ID token hint and this sign-out's state. The sign-out is reported as
   /// not confirmed, the log says that the default browser could not be opened, with the kind of failure and what to
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

      Assert.False(await SignInWorld.EndsAsync(Over(world).CompleteAsync(logout)));

      Assert.Equal(["INFO Browser callback inbox open.",
         "INFO Opening the identity provider's sign-out page in the default browser (id.example.com).",
         "WARN Identity provider sign-out was not confirmed: IOException: " + report], world.Log.Entries);
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
