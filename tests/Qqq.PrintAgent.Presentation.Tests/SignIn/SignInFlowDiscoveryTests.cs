/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>Discovery: asking a server which sign-in methods it offers, and what the flow shows and keeps afterwards.</summary>
public sealed class SignInFlowDiscoveryTests
{
   /// <summary>The failure the real session client raises for an address it will not talk to, such as plain HTTP to a remote host.</summary>
   private static ArgumentException Refused(string origin) => new("The server origin must use HTTPS, not " + origin + ".", nameof(origin));



   /// <summary>A password server offers the password form only; the flow is ready, holds a session for the trimmed address, and says the options are ready.</summary>
   [Fact]
   public async Task PasswordServer_OffersThePasswordForm()
   {
      SignInFixture fixture = new();

      await fixture.Flow.DiscoverAsync("  " + SignInFixture.Server + "  ");

      Assert.True(fixture.Flow.PasswordOffered);
      Assert.False(fixture.Flow.BrowserOffered);
      Assert.True(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Null(fixture.Flow.Readiness);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.Equal(new Uri(SignInFixture.Server), Assert.Single(fixture.Sessions).Origin);
      Assert.False(fixture.Sessions[0].AllowUntrustedCertificate);
      Assert.Equal(new FeedbackMessage("Sign-in options ready", "Use the sign-in method configured by this server.", FeedbackSeverity.Informational), Assert.Single(fixture.Shown));
      Assert.Equal(["INFO Discover sign-in options requested for https://print.example.com/.", "INFO Sign-in options for print.example.com: password sign-in offered, browser sign-in not offered."], fixture.Log.Entries);
      Assert.Equal([SignInStage.Discovering, SignInStage.Ready], fixture.Stages);
   }



   /// <summary>A server with the native browser grant offers the browser button, alone or together with the password form.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task BrowserGrant_OffersTheBrowserButton(bool password)
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.Metadata = SignInFixture.Offers(password, SignInFixture.Callback) };

      await fixture.Flow.DiscoverAsync(SignInFixture.Server);

      Assert.True(fixture.Flow.BrowserOffered);
      Assert.Equal(password, fixture.Flow.PasswordOffered);
      Assert.Equal("Sign-in options ready", Assert.Single(fixture.Shown).Title);
   }



   /// <summary>A server that offers neither method is reported as needing configuration, as a warning, and offers nothing.</summary>
   [Fact]
   public async Task ServerWithoutASupportedMethod_NeedsConfiguration()
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.Metadata = new QqqAuthenticationMetadata { Type = "OAUTH2" } };

      await fixture.Flow.DiscoverAsync(SignInFixture.Server);

      Assert.False(fixture.Flow.PasswordOffered);
      Assert.False(fixture.Flow.BrowserOffered);
      Assert.Equal(new FeedbackMessage("Server configuration required", "This server has not enabled a supported native sign-in grant. Ask its administrator to configure it.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
   }



   /// <summary>An address pasted with a user name, password, query or fragment is logged as scheme, host, port and path only, so no credential or token reaches the shared log.</summary>
   [Fact]
   public async Task AddressWithCredentialsOrAToken_IsLoggedWithoutThem()
   {
      SignInFixture fixture = new();

      await fixture.Flow.DiscoverAsync("https://operator:hunter2@print.example.com:8443/base?token=private-token#fragment-secret");

      Assert.Contains("INFO Discover sign-in options requested for https://print.example.com:8443/base.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("hunter2", StringComparison.Ordinal) || entry.Contains("operator", StringComparison.Ordinal)
         || entry.Contains("private-token", StringComparison.Ordinal) || entry.Contains("fragment-secret", StringComparison.Ordinal));
   }



   /// <summary>An address that is not an absolute URL is refused before any session is created, with the reason on screen; the log says so without repeating what was typed.</summary>
   [Theory]
   [InlineData("print.example.com/?token=private-token")]
   [InlineData("")]
   [InlineData(null)]
   public async Task AddressThatIsNotAUrl_FailsDiscoveryWithoutASessionAndIsNotLogged(string? address)
   {
      SignInFixture fixture = new();

      await fixture.Flow.DiscoverAsync(address);

      Assert.Empty(fixture.Sessions);
      Assert.False(fixture.Flow.HasSession);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.Equal(new FeedbackMessage("Discovery failed", "The server did not provide compatible authentication and printing discovery. Reason: Enter a valid server origin. The activity log has the full detail.", FeedbackSeverity.Error), Assert.Single(fixture.Shown));
      Assert.Equal("INFO Discover sign-in options requested for an address that could not be read; it is not logged.", fixture.Log.Entries[0]);
      Assert.Contains("WARN The server address is not an absolute URL (expected https://host[:port]).", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("ERROR Discovery failed: InvalidOperationException: Enter a valid server origin.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains(fixture.Log.Entries, entry => entry.StartsWith("DEBUG Discovery failed detail: System.InvalidOperationException", StringComparison.Ordinal));
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("private-token", StringComparison.Ordinal));
   }



   /// <summary>An address the session client refuses (such as plain HTTP to a remote host) is a failed discovery with that reason.</summary>
   [Fact]
   public async Task AddressTheClientRefuses_FailsDiscovery()
   {
      SignInFixture fixture = new() { CreateFailure = Refused("http://print.example.com") };

      await fixture.Flow.DiscoverAsync("http://print.example.com");

      Assert.Equal("The server did not provide compatible authentication and printing discovery. Reason: The server origin must use HTTPS, not http://print.example.com. (Parameter 'origin') The activity log has the full detail.", Assert.Single(fixture.Shown).Message);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
   }



   /// <summary>When the server cannot be reached or answers incompatibly, discovery fails with the cause and its inner cause, and the half-made session is discarded so nothing can be continued on it.</summary>
   [Fact]
   public async Task UnreachableServer_FailsDiscoveryAndDiscardsTheSession()
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.DiscoverFailure = new HttpRequestException("The server could not be reached.", new IOException("Connection refused.")) };

      await fixture.Flow.DiscoverAsync(SignInFixture.Server);

      Assert.True(Assert.Single(fixture.Sessions).Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.PasswordOffered);
      Assert.Equal("The server did not provide compatible authentication and printing discovery. Reason: The server could not be reached. Connection refused. The activity log has the full detail.", Assert.Single(fixture.Shown).Message);
      Assert.Contains("ERROR Discovery failed: HttpRequestException: The server could not be reached. <- IOException: Connection refused.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Equal([SignInStage.Discovering, SignInStage.Idle], fixture.Stages);
   }



   /// <summary>A server that takes too long is a failed discovery with the time limit as its reason, which is different from a discovery the caller cancelled.</summary>
   [Fact]
   public async Task ServerTimeLimit_IsAFailedDiscovery()
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.DiscoverFailure = new TaskCanceledException("The server did not answer in time.") };

      await fixture.Flow.DiscoverAsync(SignInFixture.Server);

      Assert.Equal("Discovery failed", Assert.Single(fixture.Shown).Title);
      Assert.Contains("Reason: The server did not answer in time.", fixture.Shown[0].Message, StringComparison.Ordinal);
   }



   /// <summary>Cancelling the caller's token, or disposing the flow, while the server is being asked ends discovery quietly: no failure is shown, no session is left, and the flow is idle.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task CancelledDiscovery_EndsQuietlyWithoutASession(bool byToken)
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.PendingDiscovery = new TaskCompletionSource<QqqAuthenticationMetadata>() };
      using CancellationTokenSource cancellation = new();

      Task running = fixture.Flow.DiscoverAsync(SignInFixture.Server, cancellation.Token);
      Assert.True(fixture.Flow.IsBusy);
      Assert.Equal(SignInStage.Discovering, fixture.Flow.Stage);
      if(byToken)
      {
         await cancellation.CancelAsync();
      }
      else
      {
         fixture.Flow.Dispose();
      }
      await running;

      Assert.Empty(fixture.Shown);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.Contains("DEBUG Discovery was cancelled before the server answered.", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>Discovering again discards the earlier session and withdraws its options before asking, so each attempt starts clean.</summary>
   [Fact]
   public async Task Rediscovery_StartsFromACleanSession()
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.Metadata = SignInFixture.Offers(password: true, SignInFixture.Callback) };
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.ConfigureSession = session => session.DiscoverFailure = new IOException("Timed out.");

      await fixture.Flow.DiscoverAsync("https://other.example.com");

      Assert.True(fixture.Sessions[0].Disposed);
      Assert.True(fixture.Sessions[1].Disposed);
      Assert.False(fixture.Flow.PasswordOffered);
      Assert.False(fixture.Flow.BrowserOffered);
      Assert.Equal("Sign-in options ready > Discovery failed", fixture.Titles);
   }



   /// <summary>The saved certificate exception is applied only to the server it was saved for: discovery of that server asks for it, discovery of another does not.</summary>
   [Fact]
   public async Task CertificateException_AppliesOnlyToItsOwnServer()
   {
      SignInFixture fixture = new() { Service = _ => Task.FromResult("Saved.") };
      Assert.True(await fixture.Flow.SetTrustAsync(allow: true, "https://dev.example.com:8443/"));

      await fixture.Flow.DiscoverAsync("https://dev.example.com:8443");
      await fixture.Flow.DiscoverAsync("https://print.example.com");

      Assert.True(fixture.Sessions[0].AllowUntrustedCertificate);
      Assert.False(fixture.Sessions[1].AllowUntrustedCertificate);
   }



   /// <summary>A failure that is not a discovery failure (a programming error) is not swallowed, but it still leaves the flow idle and free for the next operation, without a session, rather than stuck in the discovering stage.</summary>
   [Fact]
   public async Task UnexpectedFailure_PropagatesAndLeavesTheFlowIdle()
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.DiscoverFailure = new NotSupportedException("A bug.") };

      await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Flow.DiscoverAsync(SignInFixture.Server));

      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.False(fixture.Flow.IsBusy);
      Assert.False(fixture.Flow.HasSession);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.Empty(fixture.Shown);
   }



   /// <summary>A disposed flow starts nothing: discovery, sign-in, sign-out, enrollment and the certificate exception are all refused without a request, and disposing twice is harmless.</summary>
   [Fact]
   public async Task DisposedFlow_StartsNothing()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Flow.Dispose();
      fixture.Flow.Dispose();
      fixture.Forget();

      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
      await fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      await fixture.Flow.LoadTrustAsync();

      Assert.True(await fixture.Flow.SignOutAsync());
      Assert.True(await fixture.Flow.SetTrustAsync(allow: false, null));
      Assert.Equal(new EnrollmentResult(null, ServiceReachable: true), await fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123"));
      Assert.Single(fixture.Sessions);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.Empty(fixture.Requests);
      Assert.Empty(fixture.Shown);
      Assert.False(fixture.Flow.HasSession);
   }



   /// <summary>The flow cannot be built without its collaborators, so a wiring mistake fails at start-up; without a clock it uses the system one.</summary>
   [Fact]
   public void MissingCollaborators_AreRefused()
   {
      SignInFixture fixture = new();
      ServiceClient service = new(_ => Task.FromResult("ok"), fixture.Log, "QQQ");

      Assert.Throws<ArgumentNullException>(() => new SignInFlow(null!, fixture, fixture, fixture, fixture.Log));
      Assert.Throws<ArgumentNullException>(() => new SignInFlow(service, null!, fixture, fixture, fixture.Log));
      Assert.Throws<ArgumentNullException>(() => new SignInFlow(service, fixture, null!, fixture, fixture.Log));
      Assert.Throws<ArgumentNullException>(() => new SignInFlow(service, fixture, fixture, null!, fixture.Log));
      Assert.Throws<ArgumentNullException>(() => new SignInFlow(service, fixture, fixture, fixture, null!));
      using SignInFlow systemClock = new(service, fixture, fixture, fixture, fixture.Log);
      Assert.Equal(SignInStage.Idle, systemClock.Stage);
      Assert.Null(systemClock.Feedback);
   }
}
