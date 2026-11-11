/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>Sign-out: ending the service's session, discarding the flow's own, and finishing the identity provider's sign-out in the browser when the server offers it.</summary>
public sealed class SignInFlowSignOutTests
{
   /// <summary>Scripts the service to answer a sign-out with the identity provider's own sign-out attached.</summary>
   private static SignInFixture WithProviderSignOut(string provider = "id.example.com")
   {
      QqqProviderLogout logout = new(new Uri("https://" + provider + "/logout?id_token_hint=secret-hint"), new Uri(SignInFixture.Callback), "state-1");
      return new SignInFixture { Service = _ => Task.FromResult(JsonSerializer.Serialize(new PipeSignOutResult("Signed out of QQQ.", logout), PrintApiClient.JsonOptions)) };
   }



   /// <summary>A plain sign-out asks the service with this application's callback, discards the discovered session together with the sign-in methods it offered, and shows the service's sentence. The service answers as the real one does: a document without a provider sign-out.</summary>
   [Fact]
   public async Task PlainSignOut_EndsTheSessionAndShowsTheServiceSentence()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Stages.Clear();

      bool reachable = await fixture.Flow.SignOutAsync();

      Assert.True(reachable);
      PipeRequest request = Assert.Single(fixture.Requests);
      Assert.Equal("sign-out", request.Operation);
      Assert.Equal(SignInFixture.Callback, request.PostLogoutRedirectUri);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.PasswordOffered);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Equal([SignInStage.SigningOut, SignInStage.Idle], fixture.Stages);
      Assert.Equal(new FeedbackMessage("Account sign-out", "Signed out. Device pairing and print history were preserved.", FeedbackSeverity.Informational), fixture.Shown[^1]);
      Assert.Contains("INFO Sign-out requested; asking the service to end the session.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("INFO Service sign-out result: Signed out. Device pairing and print history were preserved.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Empty(fixture.Opened);
      Assert.Empty(fixture.Inboxes);
   }



   /// <summary>An older service answers a sign-out with a plain sentence; it is shown the same way.</summary>
   [Fact]
   public async Task SignOutAnsweredWithASentence_ShowsTheSentence()
   {
      SignInFixture fixture = new() { Service = _ => Task.FromResult("Signed out of QQQ.") };

      Assert.True(await fixture.Flow.SignOutAsync());

      Assert.Equal(new FeedbackMessage("Account sign-out", "Signed out of QQQ.", FeedbackSeverity.Informational), Assert.Single(fixture.Shown));
      Assert.Contains("INFO Service sign-out result: Signed out of QQQ.", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>Signing out after a confirmed sign-in forgets what that sign-in said about printing.</summary>
   [Fact]
   public async Task SignOutAfterSigningIn_ForgetsTheSignIn()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
      Assert.Equal(SignInReadiness.PrintingEnabled, fixture.Flow.Readiness);

      Assert.True(await fixture.Flow.SignOutAsync());

      Assert.Null(fixture.Flow.Readiness);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>A sign-out the service answers with an error is shown whole, as a warning; the service answered, so the flow's session and what it offered are discarded all the same.</summary>
   [Fact]
   public async Task ServiceError_IsShownAsAWarningAndTheSessionIsStillDiscarded()
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.Metadata = SignInFixture.Offers(password: true, SignInFixture.Callback) };
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Forget();
      fixture.Service = _ => Task.FromResult("Error: The session could not be ended.");

      Assert.True(await fixture.Flow.SignOutAsync());

      Assert.Equal(new FeedbackMessage("Account sign-out", "Error: The session could not be ended.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.PasswordOffered);
      Assert.False(fixture.Flow.BrowserOffered);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>
   /// When the sign-out never left this computer, nothing was changed: the flow shows nothing itself, keeps its session
   /// and stage, and tells the caller, so the window shows its service-unavailable state.
   /// </summary>
   [Fact]
   public async Task SignOutThatNeverLeft_KeepsTheFlowAsItWasAndIsLeftToTheCaller()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Forget();
      fixture.Service = _ => Task.FromException<string>(new AgentPipeNotSentException(new OperationCanceledException("Nothing is listening on the pipe.")));

      bool reachable = await fixture.Flow.SignOutAsync();

      Assert.False(reachable);
      Assert.True(fixture.Flow.HasSession);
      Assert.True(fixture.Flow.PasswordOffered);
      Assert.Empty(fixture.Shown);
      Assert.Equal([SignInStage.SigningOut, SignInStage.Ready], fixture.Stages);
      Assert.Contains("ERROR Sign-out could not reach the service; nothing was changed: Nothing is listening on the pipe.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains(fixture.Log.Entries, candidate => candidate.StartsWith("DEBUG Service request 'sign-out' failure detail: ", StringComparison.Ordinal));
   }



   /// <summary>
   /// When the sign-out left this computer and no usable answer came back, the session may have ended anyway. That is
   /// not "service unavailable": the caller is not told the service is unreachable, and the operator is told the sign-out
   /// could not be confirmed, what really happened to the request, and that the session may have ended. The flow keeps
   /// its own discovered session and stage, which the service's session does not depend on.
   /// </summary>
   [Theory]
   [MemberData(nameof(UnconfirmedFailures))]
   public async Task SignOutThatLeftWithoutAnAnswer_IsUnconfirmedNotUnavailable(Exception failure, string cause)
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Forget();
      fixture.Service = _ => Task.FromException<string>(failure);

      bool reachable = await fixture.Flow.SignOutAsync();

      Assert.True(reachable);
      Assert.Equal(new FeedbackMessage("Sign-out could not be confirmed", cause + " The session may have ended. Check the connection status before trying again. The activity log has the full detail.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
      Assert.Contains("ERROR Sign-out could not be confirmed, so the session may or may not have ended: " + cause, fixture.Log.Entries, StringComparer.Ordinal);
      Assert.True(fixture.Flow.HasSession);
      Assert.Equal([SignInStage.SigningOut, SignInStage.Ready], fixture.Stages);
   }



   /// <summary>The failures of <see cref="SignOutThatLeftWithoutAnAnswer_IsUnconfirmedNotUnavailable"/> and the sentence that says what happened for each.</summary>
   public static TheoryData<Exception, string> UnconfirmedFailures() => new()
   {
      { new OperationCanceledException("The reply did not arrive in time."), "The request timed out." },
      { new IOException("Service connection closed before a complete response was received."), "The connection to the service closed before its answer arrived." },
      { new InvalidDataException("Local pipe message exceeds the permitted size."), "The service's answer was larger than the companion accepts." },
   };



   /// <summary>
   /// A sign-out the service answered with a document the companion cannot read (here its provider part is incomplete)
   /// is an answer, not a missing one. The operator is told the answer could not be read and that the session may have
   /// ended, the log says an answer arrived and could not be read, and nothing of the document, which carries the
   /// provider's token hint, is shown or logged.
   /// </summary>
   [Fact]
   public async Task SignOutAnsweredUnreadably_SaysTheAnswerCouldNotBeRead()
   {
      SignInFixture fixture = new() { Service = _ => Task.FromResult("""{"message":"Signed out.","providerLogout":{"endSessionUri":"https://id.example.com/logout?id_token_hint=secret-hint","redirectUri":null,"state":"state-1"}}""") };

      bool reachable = await fixture.Flow.SignOutAsync();

      Assert.True(reachable);
      Assert.Equal(new FeedbackMessage("Sign-out could not be confirmed", "The service answered, but its answer could not be read. The session may have ended. Check the connection status before trying again. The activity log has the full detail.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
      Assert.Equal(
      [
         "INFO Sign-out requested; asking the service to end the session.",
         "WARN Service request 'sign-out' was answered, but the answer could not be read (JsonException).",
         "ERROR Sign-out could not be confirmed, so the session may or may not have ended: The service answered, but its answer could not be read.",
      ], fixture.Log.Entries);
      Assert.Empty(fixture.Opened);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>Cancelling the caller's token while the service is asked ends the wait, not the sign-out: it is unconfirmed like any other sign-out that left without an answer, and the service is not called unavailable.</summary>
   [Fact]
   public async Task CancelledSignOut_IsUnconfirmedNotUnavailable()
   {
      SignInFixture fixture = new();
      (Task reached, _) = fixture.HoldService();
      using CancellationTokenSource cancellation = new();

      Task<bool> running = fixture.Flow.SignOutAsync(cancellation.Token);
      await reached;
      Assert.Equal(SignInStage.SigningOut, fixture.Flow.Stage);
      await cancellation.CancelAsync();

      Assert.True(await SignInFixture.EndsAsync(running));
      Assert.Equal(new FeedbackMessage("Sign-out could not be confirmed", "The wait for the service's answer was ended. The session may have ended. Check the connection status before trying again. The activity log has the full detail.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>
   /// Disposing the flow while the service is asked, or while the identity provider's page is open, ends the sign-out
   /// quietly: the closed window is told nothing more, the provider is not reported as "did not confirm", and no warning
   /// or error is logged.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task DisposedDuringSignOut_EndsQuietly(bool duringProviderSignOut)
   {
      SignInFixture fixture = duringProviderSignOut ? WithProviderSignOut() : new SignInFixture();
      Task reached = duringProviderSignOut ? Task.CompletedTask : fixture.HoldService().Reached;

      Task<bool> running = fixture.Flow.SignOutAsync();
      await reached;
      int announced = fixture.Changes.Count;
      fixture.Flow.Dispose();

      Assert.True(await SignInFixture.EndsAsync(running));
      Assert.Equal(announced, fixture.Changes.Count);
      Assert.Equal(duringProviderSignOut ? 1 : 0, fixture.Shown.Count);
      Assert.All(fixture.Inboxes, inbox => Assert.True(inbox.Disposed));
      Assert.False(fixture.Flow.IsBusy);
      Assert.StartsWith("DEBUG Sign-out ended because the connection flow was closed (", fixture.Log.Entries[^1], StringComparison.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.StartsWith("WARN", StringComparison.Ordinal) || entry.StartsWith("ERROR", StringComparison.Ordinal));
   }



   /// <summary>When the server offers the provider's sign-out, its page is opened, the operator is told to finish there, and a callback carrying this sign-out's own state confirms that the provider session ended. The token hint is never logged.</summary>
   [Fact]
   public async Task ProviderSignOut_IsOpenedAndConfirmedByItsOwnState()
   {
      SignInFixture fixture = WithProviderSignOut();

      Task<bool> running = fixture.Flow.SignOutAsync();
      FakeInbox inbox = Assert.Single(fixture.Inboxes);
      Assert.Equal(new Uri("https://id.example.com/logout?id_token_hint=secret-hint"), Assert.Single(fixture.Opened));
      Assert.Equal(SignInStage.SigningOut, fixture.Flow.Stage);
      inbox.Deliver(new Uri(SignInFixture.Callback + "?state=state-1"));

      Assert.True(await running);
      Assert.Equal([new FeedbackMessage("Account sign-out", "Signed out of QQQ. Finish signing out of the identity provider on the page that opened in your browser.", FeedbackSeverity.Informational),
         new FeedbackMessage("Account sign-out", "Signed out of QQQ. The identity provider session ended too, so the next sign-in asks for credentials.", FeedbackSeverity.Success)], fixture.Shown);
      Assert.True(inbox.Disposed);
      Assert.Equal(TimeSpan.FromMinutes(2), Assert.Single(fixture.Time.RequestedDelays));
      Assert.Equal(TimeSpan.FromMinutes(2), SignInFlow.ProviderSignOutLimit);
      Assert.Contains("INFO Service sign-out result: Signed out of QQQ. The identity provider sign-out is ready; opening it in the browser.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("INFO Opening the identity provider's sign-out page in the default browser (id.example.com).", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("INFO Identity provider sign-out callback matched.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("secret-hint", StringComparison.Ordinal));
   }



   /// <summary>
   /// The provider's sign-out page is at an address the server passed on from its provider. The log names its host by
   /// the ASCII name the browser will really go to: a look-alike letter (a Cyrillic "o" in "login") reads as the
   /// "xn--" name, the port is kept, and nothing of the path or the token hint is logged. The browser is given the
   /// address as it arrived.
   /// </summary>
   [Fact]
   public async Task ProviderSignOutPageWithALookAlikeHost_IsLoggedByItsAsciiName()
   {
      SignInFixture fixture = WithProviderSignOut("l\u043Egin.example.com:9443");

      Task<bool> running = fixture.Flow.SignOutAsync();
      FakeInbox inbox = Assert.Single(fixture.Inboxes);
      Assert.Equal(new Uri("https://l\u043Egin.example.com:9443/logout?id_token_hint=secret-hint"), Assert.Single(fixture.Opened));
      inbox.Deliver(new Uri(SignInFixture.Callback + "?state=state-1"));

      Assert.True(await running);
      Assert.Contains("INFO Opening the identity provider's sign-out page in the default browser (xn--lgin-55d.example.com:9443).", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.All(fixture.Log.Entries, entry => Assert.All(entry, character => Assert.InRange(character, ' ', '~')));
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("secret-hint", StringComparison.Ordinal) || entry.Contains("/logout", StringComparison.Ordinal));
   }



   /// <summary>A callback with another state is not taken as the provider finishing: the operator is warned that the provider did not confirm.</summary>
   [Fact]
   public async Task CallbackWithAnotherState_DoesNotConfirm()
   {
      SignInFixture fixture = WithProviderSignOut();

      Task<bool> running = fixture.Flow.SignOutAsync();
      fixture.Inboxes[0].Deliver(new Uri(SignInFixture.Callback + "?state=forged"));

      Assert.True(await running);
      Assert.Equal(new FeedbackMessage("Account sign-out", "Signed out of QQQ. The identity provider sign-out was opened in your browser but did not confirm; close that page and sign out there if it is still open.", FeedbackSeverity.Warning), fixture.Shown[^1]);
      Assert.Contains("INFO Identity provider sign-out callback did not match this sign-out.", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// When Windows cannot start the browser for the provider's sign-out, the failure it gives names what it could not
   /// start: the whole end-session address, with the provider's ID token hint and this sign-out's state. None of that
   /// is logged or shown. The log says that the default browser could not be opened, with the kind of failure and what
   /// to check. The sign-out still completes on this computer, and the operator is told what is true: the provider's
   /// page could not be opened, so the provider's session may still be there, and what to check. It is not told that a
   /// page "was opened in your browser", because none was. No log line, message or property a view binds to holds one
   /// of those values or any query at all. The same holds for a launcher that fails in another way and names the
   /// address in its text.
   /// </summary>
   [Theory]
   [InlineData(true, BrowserStartFailure.WindowsReport)]
   [InlineData(false, BrowserStartFailure.OtherReport)]
   public async Task ProviderSignOutPageThatCannotBeStarted_IsReportedWithoutAnythingOfTheSignOutAddress(bool asWindows, string report)
   {
      QqqProviderLogout logout = new(BrowserStartFailure.SignOutAddress, new Uri(SignInFixture.Callback), BrowserStartFailure.State);
      SignInFixture fixture = new()
      {
         Service = _ => Task.FromResult(JsonSerializer.Serialize(new PipeSignOutResult("Signed out of QQQ.", logout), PrintApiClient.JsonOptions)),
         BrowserFailure = asWindows ? BrowserStartFailure.AsWindows(BrowserStartFailure.SignOutAddress) : BrowserStartFailure.AsAnotherKind(BrowserStartFailure.SignOutAddress),
      };
      Assert.True(BrowserStartFailure.Leaks(fixture.BrowserFailure.Message));

      Assert.True(await fixture.Flow.SignOutAsync());

      Assert.Equal("WARN Identity provider sign-out was not confirmed: IOException: " + report, fixture.Log.Entries[^1]);
      Assert.Equal([new FeedbackMessage("Account sign-out", "Signed out of QQQ. Finish signing out of the identity provider on the page that opened in your browser.", FeedbackSeverity.Informational),
         new FeedbackMessage("Account sign-out", "Signed out of QQQ. The identity provider's sign-out page could not be opened in your browser, so you may still be signed in to the identity provider and the next sign-in may not ask for credentials. " + BrowserStartFailure.Remedy, FeedbackSeverity.Warning)], fixture.Shown);
      Assert.DoesNotContain(fixture.Shown, message => message.Message.Contains("was opened in your browser", StringComparison.Ordinal));
      Assert.DoesNotContain(fixture.Log.Entries, BrowserStartFailure.Leaks);
      Assert.DoesNotContain(fixture.Shown, message => BrowserStartFailure.Leaks(message.Title) || BrowserStartFailure.Leaks(message.Message));
      Assert.DoesNotContain(BrowserStartFailure.Bound(fixture.Flow), BrowserStartFailure.Leaks);
      Assert.Empty(fixture.Opened);
      Assert.True(Assert.Single(fixture.Inboxes).Disposed);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.False(fixture.Flow.IsBusy);
   }



   /// <summary>
   /// When the provider does not come back within two minutes, the browser cannot be opened, or the caller cancels,
   /// the wait ends, the sign-out still completes locally, and the operator is warned. The warning says that the
   /// provider did not confirm, except for a browser that could not be opened, where it says that instead: no page
   /// was opened that could be closed.
   /// </summary>
   [Theory]
   [InlineData("limit", "TaskCanceledException", "did not confirm; close that page and sign out there if it is still open.")]
   [InlineData("browser", "IOException: " + BrowserStartFailure.WindowsReport, "could not be opened in your browser, so you may still be signed in to the identity provider and the next sign-in may not ask for credentials. " + BrowserStartFailure.Remedy)]
   [InlineData("token", "TaskCanceledException", "did not confirm; close that page and sign out there if it is still open.")]
   public async Task ProviderThatDoesNotReturn_IsReportedAsUnconfirmed(string ending, string cause, string told)
   {
      SignInFixture fixture = WithProviderSignOut();
      fixture.BrowserFailure = string.Equals(ending, "browser", StringComparison.Ordinal) ? new Win32Exception(2, "The system cannot find the file specified.") : null;
      using CancellationTokenSource cancellation = new();

      Task<bool> running = fixture.Flow.SignOutAsync(cancellation.Token);
      if(string.Equals(ending, "limit", StringComparison.Ordinal))
      {
         fixture.Time.Advance(TimeSpan.FromMinutes(2));
      }
      else if(string.Equals(ending, "token", StringComparison.Ordinal))
      {
         await cancellation.CancelAsync();
      }

      Assert.True(await SignInFixture.EndsAsync(running));
      Assert.Equal(FeedbackSeverity.Warning, fixture.Shown[^1].Severity);
      Assert.EndsWith(told, fixture.Shown[^1].Message, StringComparison.Ordinal);
      Assert.Contains(fixture.Log.Entries, entry => entry.StartsWith("WARN Identity provider sign-out was not confirmed: " + cause, StringComparison.Ordinal));
      Assert.True(fixture.Inboxes[0].Disposed);
      Assert.False(fixture.Flow.IsBusy);
   }
}
