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
   /// <summary>The failure the real session client raises for an address it will not talk to, such as plain HTTP to a remote host. It is taken from the real client, which refuses before it opens any connection, so the text here cannot drift from the real one.</summary>
   private static ArgumentException Refused(string address) => Assert.Throws<ArgumentException>(() => new QqqSessionClient(new Uri(address)));



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
      Assert.True(fixture.Sessions[0].CertificateException.IsEmpty);
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

      Assert.Equal("The server did not provide compatible authentication and printing discovery. Reason: Use an HTTPS server origin, or explicitly enabled loopback HTTP. (Parameter 'origin') The activity log has the full detail.", Assert.Single(fixture.Shown).Message);
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



   /// <summary>
   /// Cancelling the caller's token, or disposing the flow, while the server is being asked ends discovery quietly: no
   /// failure is shown or logged, no session is left, and the flow is idle. The session is discarded by the discovery
   /// itself once its call has ended, never under the running call: the scripted session fails a call it is disposed
   /// under the way the real client does, which would show here as "Discovery failed ... disposed". After a dispose
   /// nothing more is announced at all.
   /// </summary>
   [Theory]
   [InlineData(true, "DEBUG Discovery was cancelled by its caller (TaskCanceledException).")]
   [InlineData(false, "DEBUG Discovery ended because the connection flow was closed (TaskCanceledException).")]
   public async Task CancelledDiscovery_EndsQuietlyWithoutASession(bool byToken, string entry)
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.PendingDiscovery = new TaskCompletionSource<QqqAuthenticationMetadata>() };
      using CancellationTokenSource cancellation = new();

      Task<bool> running = fixture.Flow.DiscoverAsync(SignInFixture.Server, cancellation.Token);
      Assert.True(fixture.Flow.IsBusy);
      Assert.Equal(SignInStage.Discovering, fixture.Flow.Stage);
      int announced = fixture.Changes.Count;
      if(byToken)
      {
         await cancellation.CancelAsync();
      }
      else
      {
         fixture.Flow.Dispose();
      }

      Assert.True(await SignInFixture.EndsAsync(running));
      Assert.Empty(fixture.Shown);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.False(fixture.Flow.HasSession);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.Equal(entry, fixture.Log.Entries[^1]);
      Assert.DoesNotContain(fixture.Log.Entries, candidate => candidate.StartsWith("WARN", StringComparison.Ordinal) || candidate.StartsWith("ERROR", StringComparison.Ordinal));
      Assert.Equal(byToken, fixture.Changes.Count > announced);
   }



   /// <summary>A discovery asked for with a token that is already cancelled does nothing: no session is created and nothing is shown.</summary>
   [Fact]
   public async Task DiscoveryWithAWaitThatAlreadyEnded_DoesNothing()
   {
      SignInFixture fixture = new();
      using CancellationTokenSource cancellation = new();
      await cancellation.CancelAsync();

      Assert.True(await fixture.Flow.DiscoverAsync(SignInFixture.Server, cancellation.Token));

      Assert.Empty(fixture.Sessions);
      Assert.Empty(fixture.Shown);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Equal(SignInStage.Idle, fixture.Flow.Stage);
      Assert.Equal("DEBUG Discovery was cancelled by its caller (OperationCanceledException).", Assert.Single(fixture.Log.Entries));
   }



   /// <summary>
   /// What the last confirmed sign-in said about printing belongs to that sign-in. Discovering again forgets it at once,
   /// before the server has answered, so a view never shows "printing is enabled" for a server that is only being asked.
   /// </summary>
   [Fact]
   public async Task Rediscovery_ForgetsWhatTheLastSignInAchieved()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
      Assert.Equal(SignInReadiness.PrintingEnabled, fixture.Flow.Readiness);
      fixture.ConfigureSession = session => session.PendingDiscovery = new TaskCompletionSource<QqqAuthenticationMetadata>();

      Task<bool> running = fixture.Flow.DiscoverAsync(SignInFixture.Server);

      Assert.Null(fixture.Flow.Readiness);
      fixture.Sessions[1].PendingDiscovery!.SetResult(fixture.Sessions[1].Metadata);
      Assert.True(await SignInFixture.EndsAsync(running));
      Assert.Null(fixture.Flow.Readiness);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
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



   /// <summary>The saved certificate exception is applied only to the server it was saved for: the session for that server accepts an untrusted certificate from that server's origin and no other, and the session for another server accepts none.</summary>
   [Fact]
   public async Task CertificateException_AppliesOnlyToItsOwnServer()
   {
      SignInFixture fixture = new();
      Assert.True(await fixture.Flow.SetTrustAsync(allow: true, "https://dev.example.com:8443/"));

      await fixture.Flow.DiscoverAsync("https://dev.example.com:8443");
      await fixture.Flow.DiscoverAsync("https://print.example.com");

      Assert.Equal(2, fixture.Sessions.Count);
      Assert.Equal(["https://dev.example.com:8443/"], fixture.Sessions[0].CertificateException.Origins.Select(origin => origin.AbsoluteUri), StringComparer.Ordinal);
      Assert.True(fixture.Sessions[1].CertificateException.IsEmpty);
   }



   /// <summary>
   /// A server with a saved certificate exception that names a sign-in provider on another origin. The description is
   /// read on a session that accepts an untrusted certificate from the server only; that session is then discarded and
   /// the attempt continues on a second one whose exception covers the server and the provider it named, and nothing
   /// else, so the browser sign-in can read the provider's discovery document. The log names both origins, and the
   /// operator sees one result. The provider is taken from the first read only. Here the second read, on the second
   /// session, names a different provider: the exception is not extended to it, no third session is made, and the
   /// warning is written once and names the first provider.
   /// </summary>
   [Fact]
   public async Task CertificateException_AlsoCoversTheSeparateProviderItsServerNames()
   {
      int reads = 0;
      SignInFixture fixture = new()
      {
         ConfigureSession = session => session.Metadata = OffersThrough(reads++ == 0 ? "https://login.example.com:9443/realms/dev" : "https://other-login.example.com/realms/dev"),
      };
      Assert.True(await fixture.Flow.SetTrustAsync(allow: true, "https://dev.example.com:8443/"));
      fixture.Shown.Clear();

      await fixture.Flow.DiscoverAsync("https://dev.example.com:8443");

      Assert.Equal(2, reads);
      Assert.Equal(2, fixture.Sessions.Count);
      Assert.Equal(["https://dev.example.com:8443/"], fixture.Sessions[0].CertificateException.Origins.Select(origin => origin.AbsoluteUri), StringComparer.Ordinal);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.Equal("https://other-login.example.com/", fixture.Sessions[1].Metadata.ProviderOrigin?.AbsoluteUri);
      Assert.Equal(["https://dev.example.com:8443/", "https://login.example.com:9443/"], fixture.Sessions[1].CertificateException.Origins.Select(origin => origin.AbsoluteUri), StringComparer.Ordinal);
      Assert.False(fixture.Sessions[1].Disposed);
      Assert.True(fixture.Flow.HasSession);
      Assert.True(fixture.Flow.BrowserOffered);
      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.Equal("Sign-in options ready", Assert.Single(fixture.Shown).Title);
      Assert.Equal("WARN The certificate exception saved for dev.example.com:8443 is also applied to the sign-in provider this server names, login.example.com:9443: an untrusted certificate is accepted from https://dev.example.com:8443, https://login.example.com:9443 and from no other host.",
         Assert.Single(fixture.Log.Entries, entry => entry.Contains("is also applied to the sign-in provider", StringComparison.Ordinal)));
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("other-login", StringComparison.Ordinal));
   }



   /// <summary>
   /// The server writes the provider's address, and the warning is what an operator reads before trusting it, so the
   /// provider is shown and compared by its host's ASCII name. A look-alike letter (a Cyrillic "o" in "login") appears
   /// as the "xn--" name the connection would really go to, and a zero-width space is not in the name at all. Nothing
   /// outside printable ASCII reaches the log, and the second session's exception holds the same ASCII origin.
   /// </summary>
   [Theory]
   [InlineData("https://l\u043Egin.example.com:9443/realms/dev", "xn--lgin-55d.example.com:9443")]
   [InlineData("https://log\u200Bin.example.com:9443/realms/dev", "login.example.com:9443")]
   public async Task CertificateException_ShowsTheProviderByItsAsciiName(string issuer, string shown)
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.Metadata = OffersThrough(issuer) };
      Assert.True(await fixture.Flow.SetTrustAsync(allow: true, "https://dev.example.com:8443/"));

      await fixture.Flow.DiscoverAsync("https://dev.example.com:8443");

      Assert.Equal(2, fixture.Sessions.Count);
      Assert.Equal(["https://dev.example.com:8443/", "https://" + shown + "/"], fixture.Sessions[1].CertificateException.Origins.Select(origin => origin.AbsoluteUri), StringComparer.Ordinal);
      Assert.Contains("WARN The certificate exception saved for dev.example.com:8443 is also applied to the sign-in provider this server names, " + shown + ": an untrusted certificate is accepted from https://dev.example.com:8443, https://" + shown + " and from no other host.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.All(fixture.Log.Entries, entry => Assert.All(entry, character => Assert.InRange(character, ' ', '~')));
   }



   /// <summary>
   /// The second session is created only when it would cover more. A provider on the server's own origin is already
   /// covered, so the attempt stays on the first session; a provider whose host has no ASCII name (here it holds a
   /// line separator) is no origin, so nothing is added for it and nothing of it is shown; and a server without a
   /// saved exception gets no exception at all, whatever provider it names.
   /// </summary>
   [Theory]
   [InlineData(true, "https://dev.example.com:8443/realms/dev", "https://dev.example.com:8443/")]
   [InlineData(true, "https://log\u2028in.example.com:9443/realms/dev", "https://dev.example.com:8443/")]
   [InlineData(false, "https://login.example.com:9443/realms/dev", null)]
   public async Task CertificateException_IsNotWidenedWithoutNeedOrWithoutBeingSaved(bool saved, string issuer, string? covered)
   {
      SignInFixture fixture = new() { ConfigureSession = session => session.Metadata = OffersThrough(issuer) };
      if(saved)
      { Assert.True(await fixture.Flow.SetTrustAsync(allow: true, "https://dev.example.com:8443/")); }

      await fixture.Flow.DiscoverAsync("https://dev.example.com:8443");

      FakeSession session = Assert.Single(fixture.Sessions);
      Assert.False(session.Disposed);
      Assert.Equal(covered is null ? [] : new[] { covered }, session.CertificateException.Origins.Select(origin => origin.AbsoluteUri), StringComparer.Ordinal);
      Assert.True(fixture.Flow.BrowserOffered);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("is also applied to the sign-in provider", StringComparison.Ordinal));
   }



   /// <summary>A server description offering the native browser grant through the given issuer.</summary>
   private static QqqAuthenticationMetadata OffersThrough(string issuer) => new()
   {
      Type = "OAUTH2",
      Values = new QqqAuthenticationMetadata.ProviderValues { NativeSessionLogin = new QqqAuthenticationMetadata.NativeGrant { Issuer = issuer, RedirectUris = [SignInFixture.Callback] } },
   };



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



   /// <summary>
   /// A disposed flow starts nothing and says nothing: discovery, sign-in, sign-out, enrollment and the certificate
   /// exception are all refused without a request, every result says "not started", nothing is announced or logged (not
   /// even a form check or a checkbox going back), and disposing twice is harmless. When nothing was running, disposing
   /// discards the session itself.
   /// </summary>
   [Fact]
   public async Task DisposedFlow_StartsNothingAndAnnouncesNothing()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      FeedbackMessage? last = fixture.Flow.Feedback;
      fixture.Flow.Dispose();
      fixture.Flow.Dispose();
      fixture.Forget();

      Assert.False(await fixture.Flow.DiscoverAsync(SignInFixture.Server));
      Assert.False(await fixture.Flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent));
      Assert.False(await fixture.Flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent));
      await fixture.Flow.LoadTrustAsync();
      Assert.True(await fixture.Flow.SignOutAsync());
      Assert.True(await fixture.Flow.SetTrustAsync(allow: false, null));
      Assert.True(await fixture.Flow.SetTrustAsync(allow: true, null));
      Assert.Equal(new EnrollmentResult(null, ServiceReachable: true, Started: false), await fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123"));
      Assert.False(fixture.Flow.CheckEnrollment(null, null));

      Assert.Single(fixture.Sessions);
      Assert.True(fixture.Sessions[0].Disposed);
      Assert.Empty(fixture.Requests);
      Assert.Empty(fixture.Shown);
      Assert.Empty(fixture.Changes);
      Assert.Empty(fixture.Log.Entries);
      Assert.Same(last, fixture.Flow.Feedback);
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
