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



   /// <summary>A plain sign-out asks the service with this application's callback, discards the discovered session, and shows the service's sentence.</summary>
   [Fact]
   public async Task PlainSignOut_EndsTheSessionAndShowsTheServiceSentence()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Service = _ => Task.FromResult("Signed out of QQQ.");

      bool reachable = await fixture.Flow.SignOutAsync();

      Assert.True(reachable);
      PipeRequest request = Assert.Single(fixture.Requests);
      Assert.Equal("sign-out", request.Operation);
      Assert.Equal(SignInFixture.Callback, request.PostLogoutRedirectUri);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.True(fixture.Flow.PasswordOffered);
      Assert.Equal(new FeedbackMessage("Account sign-out", "Signed out of QQQ.", FeedbackSeverity.Informational), fixture.Shown[^1]);
      Assert.Contains("INFO Sign-out requested; asking the service to end the session.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("INFO Service sign-out result: Signed out of QQQ.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Empty(fixture.Opened);
   }



   /// <summary>A sign-out the service answers with an error is shown whole, as a warning.</summary>
   [Fact]
   public async Task ServiceError_IsShownAsAWarning()
   {
      SignInFixture fixture = new() { Service = _ => Task.FromResult("Error: The session could not be ended.") };

      Assert.True(await fixture.Flow.SignOutAsync());

      Assert.Equal(new FeedbackMessage("Account sign-out", "Error: The session could not be ended.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
   }



   /// <summary>When the service cannot be reached nothing is changed or shown by the flow: the session stays and the caller is told, so the window shows its service-unavailable state.</summary>
   [Fact]
   public async Task UnreachableService_ChangesNothing()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Shown.Clear();
      fixture.Service = _ => Task.FromException<string>(new IOException("Service connection closed."));

      bool reachable = await fixture.Flow.SignOutAsync();

      Assert.False(reachable);
      Assert.True(fixture.Flow.HasSession);
      Assert.Empty(fixture.Shown);
      Assert.Contains("ERROR Sign-out could not reach the service: Service connection closed.", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>When the server offers the provider's sign-out, its page is opened, the operator is told to finish there, and a callback carrying this sign-out's own state confirms that the provider session ended. The token hint is never logged.</summary>
   [Fact]
   public async Task ProviderSignOut_IsOpenedAndConfirmedByItsOwnState()
   {
      SignInFixture fixture = WithProviderSignOut();

      Task<bool> running = fixture.Flow.SignOutAsync();
      FakeInbox inbox = Assert.Single(fixture.Inboxes);
      Assert.Equal(new Uri("https://id.example.com/logout?id_token_hint=secret-hint"), Assert.Single(fixture.Opened));
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



   /// <summary>When the provider does not come back within two minutes, or the browser cannot be opened, the sign-out still completes locally and the operator is warned that the provider did not confirm.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task ProviderThatDoesNotReturn_IsReportedAsUnconfirmed(bool browserStarts)
   {
      SignInFixture fixture = WithProviderSignOut();
      fixture.BrowserFailure = browserStarts ? null : new Win32Exception(2, "The system cannot find the file specified.");

      Task<bool> running = fixture.Flow.SignOutAsync();
      fixture.Time.Advance(TimeSpan.FromMinutes(2));

      Assert.True(await running);
      Assert.Equal(FeedbackSeverity.Warning, fixture.Shown[^1].Severity);
      Assert.EndsWith("did not confirm; close that page and sign out there if it is still open.", fixture.Shown[^1].Message, StringComparison.Ordinal);
      Assert.Contains(fixture.Log.Entries, entry => entry.StartsWith("WARN Identity provider sign-out was not confirmed: " + (browserStarts ? "TaskCanceledException" : "Win32Exception: The system cannot find the file specified."), StringComparison.Ordinal));
      Assert.True(fixture.Inboxes[0].Disposed);
   }
}
