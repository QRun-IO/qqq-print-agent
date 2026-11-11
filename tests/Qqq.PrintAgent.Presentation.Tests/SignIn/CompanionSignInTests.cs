/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using System.Security.Authentication;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>The companion's own sign-in: reading a server's sign-in description as the signed-in user, signing in by password, and what it holds, announces and logs. Browser sign-in has its own file.</summary>
public sealed class CompanionSignInTests
{
   private const string Reading = "INFO Reading the sign-in description of print.example.com as this Windows account; an untrusted certificate is accepted from none.";



   /// <summary>
   /// The methods offered are those the service's check found and this account's read of the description offers too.
   /// When the two agree that is logged as information, and when they differ as a warning naming both. A method name
   /// the companion does not know is ignored. The session is created for the server that was asked for, under exactly
   /// the certificate exception the caller gave, and kept; the view is told in the order things became true.
   /// </summary>
   [Theory]
   [InlineData("password", true, true, false, "INFO Sign-in methods for print.example.com: password.")]
   [InlineData("password,passkey", true, true, false, "INFO Sign-in methods for print.example.com: password.")]
   [InlineData("browser", false, false, true, "INFO Sign-in methods for print.example.com: browser.")]
   [InlineData("password,browser", false, false, true, "WARN Sign-in methods for print.example.com: the service's check found password and browser and this account's read found browser, so browser is offered.")]
   [InlineData("password,browser", true, true, false, "WARN Sign-in methods for print.example.com: the service's check found password and browser and this account's read found password, so password is offered.")]
   public async Task Prepare_OffersOnlyWhatTheCheckAndThisReadBothFound(string found, bool tableBased, bool password, bool browser, string entry)
   {
      SignInWorld world = new() { ConfigureSession = session => session.Metadata = tableBased ? new QqqAuthenticationMetadata { Type = "TABLE_BASED" } : SignInWorld.BrowserDescription() };
      ServerCheckResult check = new([], found.Split(','), tableBased ? null : SignInWorld.Provider);
      var exception = QqqCertificateExceptionOrigins.ForServer(new Uri(SignInWorld.Server), new Uri(SignInWorld.Provider));
      using CompanionSignIn signIn = world.NewSignIn();
      List<string> changes = SignInWorld.Watch(signIn);

      SignInOffer? offer = await signIn.PrepareAsync(new Uri(SignInWorld.Server), check, exception);

      Assert.Equal(new SignInOffer(password, browser), offer);
      Assert.Same(offer, signIn.Offer);
      Assert.Equal(CompanionSignInState.Ready, signIn.State);
      Assert.True(signIn.HasSession);
      Assert.False(signIn.IsBusy);
      Assert.Null(signIn.Failure);
      Assert.Null(signIn.Rejection);
      FakeSession session = Assert.Single(world.Sessions);
      Assert.False(session.Disposed);
      Assert.Equal(new Uri(SignInWorld.Server), session.Origin);
      Assert.Same(exception, session.CertificateException);
      Assert.Equal(["INFO Reading the sign-in description of print.example.com as this Windows account; an untrusted certificate is accepted from https://print.example.com, https://id.example.com.", entry], world.Log.Entries);
      Assert.Equal(["IsBusy=True", "State=Preparing", "HasSession=False", "HasSession=True", "Offer=" + offer, "State=Ready", "IsBusy=False"], changes);
   }



   /// <summary>When no method is one that both the check and this read found, nothing is offered: the state says so, the difference is logged, and the session that was created is discarded, because nobody could sign in on it.</summary>
   [Fact]
   public async Task Prepare_NoMethodBothFound_OffersNothingAndKeepsNoSession()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = world.NewSignIn();

      SignInOffer? offer = await signIn.PrepareAsync(new Uri(SignInWorld.Server), new ServerCheckResult([], [ServerCheckResult.Browser]), QqqCertificateExceptionOrigins.None);

      Assert.Null(offer);
      Assert.Null(signIn.Offer);
      Assert.Equal(CompanionSignInState.NothingOffered, signIn.State);
      Assert.False(signIn.HasSession);
      Assert.True(Assert.Single(world.Sessions).Disposed);
      Assert.Equal([Reading, "WARN Sign-in methods for print.example.com: the service's check found browser and this account's read found password, so no method is offered."], world.Log.Entries);
   }



   /// <summary>
   /// The service's check accepted the server's certificate under its own account, and this Windows account refuses
   /// it. That is reported as exactly that, with the certificate the session refused and the origin that presented
   /// it, which is the server's own. It is not reported as an unreadable description, no reason text is given beside
   /// the certificate, and no session is kept.
   /// </summary>
   [Fact]
   public async Task Prepare_ThisAccountRefusesTheCertificate_ReportsItAndWhoPresentedIt()
   {
      QqqCertificateRejection refusal = SignInWorld.Refusal(SignInWorld.Server);
      SignInWorld world = new()
      {
         ConfigureSession = session =>
         {
            session.DiscoverFailure = new HttpRequestException("The SSL connection could not be established, see inner exception.", new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));
            session.RefusedCertificate = refusal;
         },
      };
      using CompanionSignIn signIn = world.NewSignIn();

      SignInOffer? offer = await signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None);

      Assert.Null(offer);
      Assert.Equal(CompanionSignInState.AccountRefusedCertificate, signIn.State);
      Assert.Same(refusal, signIn.Rejection);
      Assert.Equal(new Uri(SignInWorld.Server), signIn.Rejection?.Origin);
      Assert.Null(signIn.Failure);
      Assert.False(signIn.HasSession);
      Assert.Null(signIn.Offer);
      Assert.True(Assert.Single(world.Sessions).Disposed);
      Assert.Equal("ERROR The sign-in description could not be read: HttpRequestException: The SSL connection could not be established, see inner exception. <- AuthenticationException: The remote certificate was rejected by the provided RemoteCertificateValidationCallback.", world.Log.Entries[1]);
      Assert.Equal("WARN This Windows account refused the certificate presented by the server (print.example.com; RemoteCertificateChainErrors). The service checks certificates under its own account, so it can accept one that this account does not.", world.Log.Entries[^1]);
   }



   /// <summary>
   /// The description this account reads names another sign-in provider than the service's check reported: a
   /// different one, one where the check reported none, none where the check reported one, or a look-alike of it.
   /// The sign-in stops there, because a certificate exception the operator accepted covers the provider that was
   /// shown and not another. Both are named in the log by the ASCII name a connection would really go to, and no
   /// session is kept.
   /// </summary>
   [Theory]
   [InlineData("https://id.example.com", "https://login.example.org/realms/synthetic", "login.example.org", "id.example.com")]
   [InlineData(null, "https://id.example.com", "id.example.com", "none")]
   [InlineData("https://id.example.com", "", "none", "id.example.com")]
   [InlineData("not an address", "https://id.example.com", "id.example.com", "none")]
   [InlineData("https://login.example.com", "https://l\u043Egin.example.com", "xn--lgin-55d.example.com", "login.example.com")]
   public async Task Prepare_AnotherProviderThanTheOneChecked_IsRefused(string? reported, string issuer, string read, string shown)
   {
      SignInWorld world = new() { ConfigureSession = session => session.Metadata = SignInWorld.BrowserDescription(issuer) };
      using CompanionSignIn signIn = world.NewSignIn();

      SignInOffer? offer = await signIn.PrepareAsync(new Uri(SignInWorld.Server), new ServerCheckResult([], [ServerCheckResult.Browser], reported), QqqCertificateExceptionOrigins.None);

      Assert.Null(offer);
      Assert.Equal(CompanionSignInState.ProviderChanged, signIn.State);
      Assert.False(signIn.HasSession);
      Assert.Null(signIn.Offer);
      Assert.Null(signIn.Failure);
      Assert.True(Assert.Single(world.Sessions).Disposed);
      Assert.Equal([Reading, $"WARN The sign-in provider in the sign-in description of print.example.com is {read}, and the service's check reported {shown}. Nobody is signed in until the server has been checked again."], world.Log.Entries);
   }



   /// <summary>
   /// The provider is compared as an origin. The check's address and the description's issuer are the same provider
   /// when their scheme, host and port are: whatever the letter case, an explicit default port or a path on either
   /// side, and whether a name outside ASCII is written in Unicode or in its "xn--" form.
   /// </summary>
   [Theory]
   [InlineData("https://ID.example.com:443/", "https://id.example.com/realms/synthetic")]
   [InlineData("https://id.example.com/realms/other", "https://id.example.com")]
   [InlineData("https://l\u043Egin.example.com", "https://xn--lgin-55d.example.com")]
   public async Task Prepare_SameProviderWrittenDifferently_IsTheSameProvider(string reported, string issuer)
   {
      SignInWorld world = new() { ConfigureSession = session => session.Metadata = SignInWorld.BrowserDescription(issuer) };
      using CompanionSignIn signIn = world.NewSignIn();

      SignInOffer? offer = await signIn.PrepareAsync(new Uri(SignInWorld.Server), new ServerCheckResult([], [ServerCheckResult.Browser], reported), QqqCertificateExceptionOrigins.None);

      Assert.Equal(new SignInOffer(false, true), offer);
      Assert.Equal(CompanionSignInState.Ready, signIn.State);
   }



   /// <summary>
   /// A description that cannot be read is reported with the session's own reason, and no session is kept: a server
   /// that does not answer, an address the session client refuses, and the server's own time limit, which is a
   /// cancellation nobody here asked for and therefore a failure like any other.
   /// </summary>
   [Theory]
   [InlineData("unreachable", "No connection could be made because the target machine actively refused it.", "HttpRequestException: No connection could be made because the target machine actively refused it.")]
   [InlineData("refused address", "Use an HTTPS server origin, or explicitly enabled loopback HTTP. (Parameter 'kind')", "ArgumentException: Use an HTTPS server origin, or explicitly enabled loopback HTTP. (Parameter 'kind')")]
   [InlineData("time limit", "The server did not answer in time.", "TaskCanceledException: The server did not answer in time.")]
   public async Task Prepare_DescriptionThatCannotBeRead_IsReportedWithItsReason(string kind, string reason, string logged)
   {
      SignInWorld world = new()
      {
         CreateFailure = string.Equals(kind, "refused address", StringComparison.Ordinal) ? new ArgumentException("Use an HTTPS server origin, or explicitly enabled loopback HTTP.", nameof(kind)) : null,
         ConfigureSession = session => session.DiscoverFailure = string.Equals(kind, "unreachable", StringComparison.Ordinal)
            ? new HttpRequestException("No connection could be made because the target machine actively refused it.")
            : new TaskCanceledException("The server did not answer in time."),
      };
      using CompanionSignIn signIn = world.NewSignIn();

      SignInOffer? offer = await signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None);

      Assert.Null(offer);
      Assert.Equal(CompanionSignInState.PrepareFailed, signIn.State);
      Assert.Equal(reason, signIn.Failure);
      Assert.Null(signIn.Rejection);
      Assert.False(signIn.HasSession);
      Assert.False(signIn.IsBusy);
      Assert.All(world.Sessions, session => Assert.True(session.Disposed));
      Assert.Equal("ERROR The sign-in description could not be read: " + logged, world.Log.Entries[1]);
      Assert.StartsWith("DEBUG The sign-in description could not be read detail: System.", world.Log.Entries[2], StringComparison.Ordinal);
   }



   /// <summary>
   /// A read whose caller stops waiting ends quietly: nothing is reported as failed, the session it created is
   /// discarded once its call has ended, and only a debug entry says what happened. The same holds when the answer
   /// arrives at the very moment the wait ends, and a read asked for with a wait that has already ended creates
   /// no session at all.
   /// </summary>
   [Theory]
   [InlineData("while reading", 1, "TaskCanceledException")]
   [InlineData("as the answer arrives", 1, "OperationCanceledException")]
   [InlineData("before it starts", 0, "OperationCanceledException")]
   public async Task Prepare_CallerStopsWaiting_EndsQuietlyWithoutASession(string moment, int sessions, string cancellation)
   {
      using CancellationTokenSource caller = new();
      SignInWorld world = new()
      {
         ConfigureSession = session =>
         {
            session.PendingDiscovery = string.Equals(moment, "while reading", StringComparison.Ordinal) ? new TaskCompletionSource<QqqAuthenticationMetadata>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            session.BeforeAnswer = caller.Cancel;
         },
      };
      using CompanionSignIn signIn = world.NewSignIn();
      if(string.Equals(moment, "before it starts", StringComparison.Ordinal))
      {
         await caller.CancelAsync();
      }

      Task<SignInOffer?> running = signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None, caller.Token);
      if(string.Equals(moment, "while reading", StringComparison.Ordinal))
      {
         Assert.Equal(CompanionSignInState.Preparing, signIn.State);
         Assert.True(signIn.IsBusy);
         Assert.False(signIn.HasSession);
         await caller.CancelAsync();
      }

      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.Equal(CompanionSignInState.NotPrepared, signIn.State);
      Assert.False(signIn.HasSession);
      Assert.False(signIn.IsBusy);
      Assert.Null(signIn.Failure);
      Assert.Equal(sessions, world.Sessions.Count);
      Assert.All(world.Sessions, session => Assert.True(session.Disposed));
      Assert.Equal($"DEBUG Reading the sign-in description ended early because its caller stopped waiting ({cancellation}).", world.Log.Entries[^1]);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.StartsWith("ERROR", StringComparison.Ordinal) || entry.StartsWith("WARN", StringComparison.Ordinal));
   }



   /// <summary>Reading again starts clean: the session held from the earlier read is discarded before the new one is created, and the new one is the one that is held.</summary>
   [Fact]
   public async Task Prepare_Again_DiscardsTheEarlierSession()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.PasswordReadyAsync();

      Assert.NotNull(await signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None));

      Assert.Equal(2, world.Sessions.Count);
      Assert.True(world.Sessions[0].Disposed);
      Assert.False(world.Sessions[1].Disposed);
      Assert.True(signIn.HasSession);
   }



   /// <summary>
   /// A password sign-in passes the trimmed user name and the password to the session once and returns the session
   /// credential to the caller. The sign-in keeps neither: no field of it holds the password, the user name or the
   /// credential, it has no property that could show a credential, and nothing of them is logged. The session stays,
   /// so the caller decides when it is discarded.
   /// </summary>
   [Fact]
   public async Task Password_ReturnsTheCredential_KeepsNoPassword()
   {
      const string password = "synthetic-password-canary";
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.PasswordReadyAsync();
      List<string> changes = SignInWorld.Watch(signIn);

      QqqSessionCredential? credential = await signIn.SignInWithPasswordAsync("  synthetic-operator  ", password);

      Assert.Equal(new Uri(SignInWorld.Server), credential?.Origin);
      Assert.Equal("synthetic-session-token", credential?.SessionToken);
      Assert.Equal(("synthetic-operator", password), world.Sessions[0].PasswordAttempt);
      Assert.Equal(CompanionSignInState.SignedIn, signIn.State);
      Assert.True(signIn.HasSession);
      Assert.False(world.Sessions[0].Disposed);
      Assert.Null(signIn.Failure);
      Assert.Equal(["IsBusy=True", "State=SigningIn", "State=SignedIn", "IsBusy=False"], changes);
      Assert.Equal(["INFO Password sign-in submitted to print.example.com.", "INFO Sign-in accepted by print.example.com; the session credential goes to the caller and is not kept here."], world.Log.Entries);
      object?[] held = [.. typeof(CompanionSignIn).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(field => field.GetValue(signIn))];
      Assert.NotEmpty(held);
      Assert.DoesNotContain(held, value => value is QqqSessionCredential || value is string text && (text.Contains(password, StringComparison.Ordinal) || text.Contains("synthetic-operator", StringComparison.Ordinal)));
      Assert.DoesNotContain(typeof(CompanionSignIn).GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic), property => property.PropertyType == typeof(QqqSessionCredential));
   }



   /// <summary>
   /// A refused password sign-in ends the same way whatever the server objected to, the credentials or the permission:
   /// the same state and the session's own sentence, to which nothing is added that tells the cases apart. Neither
   /// the user name nor the password is logged, and the session and its offer stay, so another attempt needs no new
   /// read; that attempt succeeds and the reason is gone.
   /// </summary>
   [Theory]
   [InlineData(QqqSessionException.FailureKind.AuthenticationRequired)]
   [InlineData(QqqSessionException.FailureKind.PermissionDenied)]
   public async Task Password_Failure_DoesNotSayWhichPartWasWrong(QqqSessionException.FailureKind kind)
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.PasswordReadyAsync();
      world.Sessions[0].PasswordFailure = new QqqSessionException(kind, "The server did not accept this sign-in.");

      QqqSessionCredential? credential = await signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-wrong-password");

      Assert.Null(credential);
      Assert.Equal(CompanionSignInState.PasswordFailed, signIn.State);
      Assert.Equal("The server did not accept this sign-in.", signIn.Failure);
      Assert.Null(signIn.Rejection);
      Assert.True(signIn.HasSession);
      Assert.Equal(new SignInOffer(true, false), signIn.Offer);
      Assert.False(world.Sessions[0].Disposed);
      Assert.Equal("ERROR Password sign-in not completed: QqqSessionException: The server did not accept this sign-in.", world.Log.Entries[1]);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.Contains("synthetic-wrong-password", StringComparison.Ordinal) || entry.Contains("synthetic-operator", StringComparison.Ordinal) || entry.Contains(kind.ToString(), StringComparison.Ordinal));

      world.Sessions[0].PasswordFailure = null;
      Assert.NotNull(await signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password"));
      Assert.Equal(CompanionSignInState.SignedIn, signIn.State);
      Assert.Null(signIn.Failure);
   }



   /// <summary>The server's own time limit is a cancellation nobody here asked for, so it is a failed sign-in with its reason, not a quiet end.</summary>
   [Fact]
   public async Task Password_ServerTimeLimit_IsAFailedSignIn()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.PasswordReadyAsync();
      world.Sessions[0].PasswordFailure = new TaskCanceledException("The server did not answer in time.");

      Assert.Null(await signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password"));

      Assert.Equal(CompanionSignInState.PasswordFailed, signIn.State);
      Assert.Equal("The server did not answer in time.", signIn.Failure);
      Assert.True(signIn.HasSession);
   }



   /// <summary>
   /// A certificate this account refuses while a password is being checked is why that step failed: the state says
   /// whose certificate it was and the refusal is kept. A later step that fails for another reason is not blamed on
   /// the certificate the session refused before.
   /// </summary>
   [Fact]
   public async Task Password_CertificateRefusedDuringTheStep_IsReported_AndAnOlderRefusalIsNot()
   {
      QqqCertificateRejection refusal = SignInWorld.Refusal(SignInWorld.Server);
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.PasswordReadyAsync();
      world.Sessions[0].PasswordFailure = new HttpRequestException("The SSL connection could not be established, see inner exception.");
      world.Sessions[0].RefusedCertificate = refusal;

      Assert.Null(await signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password"));
      Assert.Equal(CompanionSignInState.AccountRefusedCertificate, signIn.State);
      Assert.Same(refusal, signIn.Rejection);
      Assert.Null(signIn.Failure);
      Assert.True(signIn.HasSession);

      world.Sessions[0].RefusedCertificate = null;
      world.Sessions[0].PasswordFailure = new IOException("synthetic network failure");
      Assert.Null(await signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password"));
      Assert.Equal(CompanionSignInState.PasswordFailed, signIn.State);
      Assert.Null(signIn.Rejection);
      Assert.Equal("synthetic network failure", signIn.Failure);
   }



   /// <summary>
   /// A password sign-in whose caller stops waiting ends quietly and keeps the session for another attempt; nothing
   /// is reported as failed. When the server accepts the sign-in at the very moment the wait ends, no credential is
   /// taken either.
   /// </summary>
   [Theory]
   [InlineData(true, "TaskCanceledException")]
   [InlineData(false, "OperationCanceledException")]
   public async Task Password_CallerStopsWaiting_EndsQuietlyAndKeepsTheSession(bool whileChecking, string cancellation)
   {
      using CancellationTokenSource caller = new();
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.PasswordReadyAsync();
      world.Sessions[0].PendingPassword = whileChecking ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
      world.Sessions[0].BeforeAnswer = caller.Cancel;

      Task<QqqSessionCredential?> running = signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password", caller.Token);
      if(whileChecking)
      {
         Assert.Equal(CompanionSignInState.SigningIn, signIn.State);
         await caller.CancelAsync();
      }

      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.Equal(CompanionSignInState.Ready, signIn.State);
      Assert.True(signIn.HasSession);
      Assert.False(world.Sessions[0].Disposed);
      Assert.False(signIn.IsBusy);
      Assert.Null(signIn.Failure);
      Assert.Equal(["INFO Password sign-in submitted to print.example.com.", $"DEBUG Password sign-in ended early because its caller stopped waiting ({cancellation})."], world.Log.Entries);
   }



   /// <summary>Nothing is attempted without a session, or on a session that does not offer a password: the caller gets no credential, the session is not asked, and nothing is announced or logged.</summary>
   [Fact]
   public async Task Password_WithoutASessionThatOffersIt_NothingIsAttempted()
   {
      SignInWorld world = new();
      using CompanionSignIn unprepared = world.NewSignIn();
      using CompanionSignIn browserOnly = await world.BrowserReadyAsync();
      List<string> changes = SignInWorld.Watch(browserOnly);

      Assert.Null(await unprepared.SignInWithPasswordAsync("synthetic-operator", "synthetic-password"));
      Assert.Null(await browserOnly.SignInWithPasswordAsync("synthetic-operator", "synthetic-password"));

      Assert.Equal(CompanionSignInState.NotPrepared, unprepared.State);
      Assert.Equal(CompanionSignInState.Ready, browserOnly.State);
      Assert.Null(Assert.Single(world.Sessions).PasswordAttempt);
      Assert.Empty(changes);
      Assert.Empty(world.Log.Entries);
   }



   /// <summary>
   /// One operation runs at a time. While a password is being checked, another read and another password sign-in are
   /// refused: each returns nothing, each refusal is logged, and the running operation and its state are not
   /// disturbed. When it ends the sign-in is free again.
   /// </summary>
   [Fact]
   public async Task SecondOperationWhileOneRuns_IsRefusedAndLogged()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.PasswordReadyAsync();
      TaskCompletionSource answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
      world.Sessions[0].PendingPassword = answer;
      Task<QqqSessionCredential?> running = signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password");
      List<string> changes = SignInWorld.Watch(signIn);

      Assert.Null(await signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None));
      Assert.Null(await signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-other-password"));

      Assert.Equal(["INFO Password sign-in submitted to print.example.com.",
         "WARN Reading the sign-in description was not started: another sign-in operation is still running.",
         "WARN Password sign-in was not started: another sign-in operation is still running."], world.Log.Entries);
      Assert.Empty(changes);
      Assert.Equal(CompanionSignInState.SigningIn, signIn.State);
      Assert.True(signIn.IsBusy);
      Assert.Single(world.Sessions);
      Assert.Equal("synthetic-password", world.Sessions[0].PasswordAttempt?.Password);

      answer.SetResult();
      Assert.NotNull(await SignInWorld.EndsAsync(running));
      Assert.False(signIn.IsBusy);
   }



   /// <summary>Discarding when nothing runs lets the session go at once and withdraws what it offered; a sign-in then needs a new read, and until one is made nothing is attempted.</summary>
   [Fact]
   public async Task Discard_WhenNothingRuns_LetsTheSessionGo()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.PasswordReadyAsync();
      Assert.NotNull(await signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password"));
      List<string> changes = SignInWorld.Watch(signIn);

      signIn.Discard();

      Assert.True(world.Sessions[0].Disposed);
      Assert.False(signIn.HasSession);
      Assert.Null(signIn.Offer);
      Assert.Equal(CompanionSignInState.NotPrepared, signIn.State);
      Assert.Equal(["Offer=null", "HasSession=False", "State=NotPrepared"], changes);
      Assert.Null(await signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password"));
      Assert.Equal(CompanionSignInState.NotPrepared, signIn.State);
   }



   /// <summary>
   /// Discarding while a password is being checked does not pull the session out from under that call: a session
   /// that is discarded under a running call makes the call end with "disposed", and here it ends with the
   /// cancellation that was asked for. It ends quietly, the session is discarded once it has ended, and nothing is
   /// reported as failed. Afterwards the sign-in can read a description again and keeps that session.
   /// </summary>
   [Fact]
   public async Task Discard_WhileAnOperationRuns_CancelsIt_AndTheSessionGoesWhenItEnds()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = await world.PasswordReadyAsync();
      world.Sessions[0].PendingPassword = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Task<QqqSessionCredential?> running = signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password");

      signIn.Discard();

      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.True(world.Sessions[0].Disposed);
      Assert.False(signIn.HasSession);
      Assert.Null(signIn.Offer);
      Assert.Equal(CompanionSignInState.NotPrepared, signIn.State);
      Assert.False(signIn.IsBusy);
      Assert.Equal(["INFO Password sign-in submitted to print.example.com.", "DEBUG Password sign-in ended early because the session was discarded (TaskCanceledException)."], world.Log.Entries);

      Assert.NotNull(await signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None));
      Assert.True(signIn.HasSession);
      Assert.False(world.Sessions[1].Disposed);
   }



   /// <summary>
   /// Disposing while the server checks a password only cancels. The session is not discarded under the running
   /// call, which would make the call end with "disposed": it ends with the cancellation, quietly, the session is
   /// discarded then, and from the moment of disposal nothing more is announced: a view that has closed is not
   /// called back. Only a debug entry records the end.
   /// </summary>
   [Fact]
   public async Task Dispose_OnlyCancels_NothingIsAnnouncedAfterwards()
   {
      SignInWorld world = new();
      CompanionSignIn signIn = await world.PasswordReadyAsync();
      world.Sessions[0].PendingPassword = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      List<string> changes = SignInWorld.Watch(signIn);
      Task<QqqSessionCredential?> running = signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password");
      Assert.Equal(["IsBusy=True", "State=SigningIn"], changes);

      signIn.Dispose();

      Assert.Null(await SignInWorld.EndsAsync(running));
      Assert.True(world.Sessions[0].Disposed);
      Assert.Equal(["IsBusy=True", "State=SigningIn"], changes);
      Assert.False(signIn.HasSession);
      Assert.False(signIn.IsBusy);
      Assert.Equal(["INFO Password sign-in submitted to print.example.com.", "DEBUG Password sign-in ended early because the sign-in was closed (TaskCanceledException)."], world.Log.Entries);
   }



   /// <summary>
   /// Disposing when nothing runs discards the session at once and announces nothing. After that every request does
   /// nothing: no read, no sign-in, no discard, and disposing again is harmless.
   /// </summary>
   [Fact]
   public async Task Dispose_WhenNothingRuns_DiscardsTheSession_AndEverythingAfterwardsDoesNothing()
   {
      SignInWorld world = new();
      CompanionSignIn signIn = await world.PasswordReadyAsync();
      List<string> changes = SignInWorld.Watch(signIn);

      signIn.Dispose();
      signIn.Dispose();
      signIn.Discard();
      signIn.CancelBrowserSignIn();

      Assert.True(world.Sessions[0].Disposed);
      Assert.Null(await signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None));
      Assert.Null(await signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password"));
      Assert.Null(await signIn.SignInWithBrowserAsync());
      Assert.Single(world.Sessions);
      Assert.Empty(changes);
      Assert.Empty(world.Log.Entries);
   }



   /// <summary>
   /// A subscriber that throws while the sign-in announces that it is busy does not leave it claimed forever: the
   /// failure reaches the caller, the sign-in is free again, and the next read works.
   /// </summary>
   [Fact]
   public async Task SubscriberThatThrowsAtTheStart_DoesNotLeaveTheSignInClaimed()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = world.NewSignIn();
      bool thrown = false;
      signIn.PropertyChanged += (_, change) =>
      {
         if(!thrown && string.Equals(change.PropertyName, nameof(CompanionSignIn.IsBusy), StringComparison.Ordinal))
         {
            thrown = true;
            throw new InvalidOperationException("synthetic subscriber failure");
         }
      };

      await Assert.ThrowsAsync<InvalidOperationException>(() => signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None));

      Assert.False(signIn.IsBusy);
      Assert.Equal(CompanionSignInState.NotPrepared, signIn.State);
      Assert.NotNull(await signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None));
      Assert.Equal(CompanionSignInState.Ready, signIn.State);
   }



   /// <summary>
   /// A subscriber that throws while a result is announced has not made the step fail. The read still ended ready
   /// with its session, and the password sign-in still ended signed in; neither is reported as not completed, the
   /// subscriber's failure reaches the caller, and the sign-in is free again.
   /// </summary>
   [Theory]
   [InlineData(CompanionSignInState.Ready)]
   [InlineData(CompanionSignInState.SignedIn)]
   public async Task SubscriberThatThrowsWhileAResultIsAnnounced_HasNotMadeTheStepFail(CompanionSignInState announced)
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = world.NewSignIn();
      signIn.PropertyChanged += (_, change) =>
      {
         if(string.Equals(change.PropertyName, nameof(CompanionSignIn.State), StringComparison.Ordinal) && signIn.State == announced)
         {
            throw new IOException("synthetic subscriber failure");
         }
      };

      Task step = announced == CompanionSignInState.Ready
         ? signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None)
         : PrepareThenSignInAsync(signIn);

      await Assert.ThrowsAsync<IOException>(() => step);
      Assert.Equal(announced, signIn.State);
      Assert.True(signIn.HasSession);
      Assert.False(Assert.Single(world.Sessions).Disposed);
      Assert.Null(signIn.Failure);
      Assert.False(signIn.IsBusy);
      Assert.DoesNotContain(world.Log.Entries, entry => entry.StartsWith("ERROR", StringComparison.Ordinal));
   }



   /// <summary>Reads a password server's description and then signs in by password.</summary>
   private static async Task PrepareThenSignInAsync(CompanionSignIn signIn)
   {
      Assert.NotNull(await signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None));
      _ = await signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password");
   }



   /// <summary>
   /// A failure nobody expects (a programming error in a session, say) is not dressed up as a failed sign-in: it
   /// reaches the caller. The sign-in is released all the same and its state matches what it holds: no session after
   /// a read that threw, and the session still there after a sign-in that threw.
   /// </summary>
   [Fact]
   public async Task UnexpectedFailure_ReachesTheCaller_AndTheStateMatchesWhatIsHeld()
   {
      SignInWorld world = new() { ConfigureSession = session => session.DiscoverFailure = new NotSupportedException("synthetic unexpected failure") };
      using CompanionSignIn signIn = world.NewSignIn();

      await Assert.ThrowsAsync<NotSupportedException>(() => signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None));
      Assert.Equal(CompanionSignInState.NotPrepared, signIn.State);
      Assert.False(signIn.HasSession);
      Assert.False(signIn.IsBusy);
      Assert.True(world.Sessions[0].Disposed);

      world.ConfigureSession = session => session.PasswordFailure = new NotSupportedException("synthetic unexpected failure");
      Assert.NotNull(await signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None));
      await Assert.ThrowsAsync<NotSupportedException>(() => signIn.SignInWithPasswordAsync("synthetic-operator", "synthetic-password"));
      Assert.Equal(CompanionSignInState.Ready, signIn.State);
      Assert.True(signIn.HasSession);
      Assert.False(signIn.IsBusy);
   }



   /// <summary>A sign-in cannot work without its collaborators and says which is missing; a read needs a server, the check's result and a certificate exception, which may be the empty one but not nothing.</summary>
   [Fact]
   public async Task MissingCollaboratorOrArgument_IsRefused()
   {
      SignInWorld world = new();
      using CompanionSignIn signIn = world.NewSignIn();

      Assert.Equal("sessions", Assert.Throws<ArgumentNullException>(() => new CompanionSignIn(null!, world, world, world.Dispatcher, world.Log)).ParamName);
      Assert.Equal("browser", Assert.Throws<ArgumentNullException>(() => new CompanionSignIn(world, null!, world, world.Dispatcher, world.Log)).ParamName);
      Assert.Equal("callbacks", Assert.Throws<ArgumentNullException>(() => new CompanionSignIn(world, world, null!, world.Dispatcher, world.Log)).ParamName);
      Assert.Equal("dispatcher", Assert.Throws<ArgumentNullException>(() => new CompanionSignIn(world, world, world, null!, world.Log)).ParamName);
      Assert.Equal("log", Assert.Throws<ArgumentNullException>(() => new CompanionSignIn(world, world, world, world.Dispatcher, null!)).ParamName);
      Assert.Equal("server", (await Assert.ThrowsAsync<ArgumentNullException>(() => signIn.PrepareAsync(null!, SignInWorld.PasswordCheck, QqqCertificateExceptionOrigins.None))).ParamName);
      Assert.Equal("check", (await Assert.ThrowsAsync<ArgumentNullException>(() => signIn.PrepareAsync(new Uri(SignInWorld.Server), null!, QqqCertificateExceptionOrigins.None))).ParamName);
      Assert.Equal("certificateException", (await Assert.ThrowsAsync<ArgumentNullException>(() => signIn.PrepareAsync(new Uri(SignInWorld.Server), SignInWorld.PasswordCheck, null!))).ParamName);
      Assert.False(signIn.IsBusy);
   }
}
