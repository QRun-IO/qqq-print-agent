/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>Sign-out: ending the service's session, discarding the flow's own, and finishing the identity provider's sign-out in the browser when the server offers it.</summary>
public sealed class SignInFlowSignOutTests
{
   /// <summary>Scripts the service to answer a sign-out with the identity provider's own sign-out attached.</summary>
   private static SignInFixture WithProviderSignOut()
   {
      QqqProviderLogout logout = new(new Uri("https://id.example.com/logout?id_token_hint=secret-hint"), new Uri(SignInFixture.Callback), "state-1");
      return new SignInFixture { Service = _ => Task.FromResult(JsonSerializer.Serialize(new PipeSignOutResult("Signed out of QQQ.", logout), PrintApiClient.JsonOptions)) };
   }



   /// <summary>A plain sign-out asks the service with this application's callback, discards the discovered session together with the sign-in methods it offered, and shows the service's sentence.</summary>
   [Fact]
   public async Task PlainSignOut_EndsTheSessionAndShowsTheServiceSentence()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Service = _ => Task.FromResult("Signed out of QQQ.");
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
      Assert.Equal(new FeedbackMessage("Account sign-out", "Signed out of QQQ.", FeedbackSeverity.Informational), fixture.Shown[^1]);
      Assert.Contains("INFO Sign-out requested; asking the service to end the session.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("INFO Service sign-out result: Signed out of QQQ.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Empty(fixture.Opened);
   }



   /// <summary>Signing out after a confirmed sign-in forgets what that sign-in said about printing.</summary>
   [Fact]
   public async Task SignOutAfterSigningIn_ForgetsTheSignIn()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
      fixture.Service = _ => Task.FromResult("Signed out of QQQ.");

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
   /// When the service gives no usable answer, the flow shows nothing itself, keeps its session and stage, and tells the caller,
   /// so the window shows its service-unavailable state. The log says which of two things happened: the request never left and
   /// nothing was changed, or it left and the session may have ended anyway.
   /// </summary>
   [Theory]
   [InlineData(false, "ERROR Sign-out could not reach the service; nothing was changed: Nothing is listening on the pipe.")]
   [InlineData(true, "ERROR Sign-out was sent, but the service's answer did not arrive, so the session may or may not have ended: The reply did not arrive in time.")]
   public async Task UnansweredSignOut_KeepsTheFlowAsItWasAndSaysWhetherTheSessionMayHaveEnded(bool sent, string entry)
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Forget();
      fixture.Service = _ => Task.FromException<string>(sent
         ? new OperationCanceledException("The reply did not arrive in time.")
         : new AgentPipeNotSentException(new OperationCanceledException("Nothing is listening on the pipe.")));

      bool reachable = await fixture.Flow.SignOutAsync();

      Assert.False(reachable);
      Assert.True(fixture.Flow.HasSession);
      Assert.True(fixture.Flow.PasswordOffered);
      Assert.Empty(fixture.Shown);
      Assert.Equal([SignInStage.SigningOut, SignInStage.Ready], fixture.Stages);
      Assert.Contains(entry, fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains(fixture.Log.Entries, candidate => candidate.StartsWith("DEBUG Service request 'sign-out' failure detail: ", StringComparison.Ordinal));
   }



   /// <summary>Cancelling the caller's token while the service is asked ends the wait without calling the service unavailable; the log says the session may still end.</summary>
   [Fact]
   public async Task CancelledSignOut_IsNotReportedAsAnUnavailableService()
   {
      SignInFixture fixture = new();
      (Task reached, _) = fixture.HoldService();
      using CancellationTokenSource cancellation = new();

      Task<bool> running = fixture.Flow.SignOutAsync(cancellation.Token);
      await reached;
      Assert.Equal(SignInStage.SigningOut, fixture.Flow.Stage);
      await cancellation.CancelAsync();

      Assert.True(await running);
      Assert.Empty(fixture.Shown);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.Contains("INFO Sign-out is no longer waited for; the service may still end the session.", fixture.Log.Entries, StringComparer.Ordinal);
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
   /// When the provider does not come back within two minutes, the browser cannot be opened, the caller cancels, or the flow is
   /// disposed, the wait ends, the sign-out still completes locally, and the operator is warned that the provider did not confirm.
   /// </summary>
   [Theory]
   [InlineData("limit", "TaskCanceledException")]
   [InlineData("browser", "Win32Exception: The system cannot find the file specified.")]
   [InlineData("token", "TaskCanceledException")]
   [InlineData("dispose", "TaskCanceledException")]
   public async Task ProviderThatDoesNotReturn_IsReportedAsUnconfirmed(string ending, string cause)
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
      else if(string.Equals(ending, "dispose", StringComparison.Ordinal))
      {
         fixture.Flow.Dispose();
      }

      Assert.True(await running);
      Assert.Equal(FeedbackSeverity.Warning, fixture.Shown[^1].Severity);
      Assert.EndsWith("did not confirm; close that page and sign out there if it is still open.", fixture.Shown[^1].Message, StringComparison.Ordinal);
      Assert.Contains(fixture.Log.Entries, entry => entry.StartsWith("WARN Identity provider sign-out was not confirmed: " + cause, StringComparison.Ordinal));
      Assert.True(fixture.Inboxes[0].Disposed);
      Assert.False(fixture.Flow.IsBusy);
   }
}
