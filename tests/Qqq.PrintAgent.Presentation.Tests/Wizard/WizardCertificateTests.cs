/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Presentation.Wizard;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Wizard;

/// <summary>The Server step when Windows refuses a certificate: what is shown of it, when the development-only exception is offered, what its warning says, and how an acceptance is reviewed, kept and forgotten.</summary>
public sealed class WizardCertificateTests
{
   private const string Server = WizardFixture.Address;

   private const string Provider = WizardFixture.Provider;

   private const string Risk = " could then read or change what is sent, including your sign-in and print jobs.";

   private const string Head = "This is for a development server only. If you accept, the print service and this companion will accept a certificate that Windows does not trust from ";

   private const string LastingFix = " The print service checks certificates under its own account, not yours. The certificate and the reasons are shown. The lasting fix is to install the issuing authority's root certificate in this computer's Trusted Root Certification Authorities store, then choose Check again.";



   /// <summary>
   /// A refused certificate is shown with the address that presented it, its subject, issuer, validity in local
   /// time, fingerprint and the reasons in words, beside the stages. Next cannot be used; Enter checks again; and
   /// both ways forward are named.
   /// </summary>
   [Fact]
   public async Task Check_CertificateRefused_ShowsSubjectIssuerValidityFingerprintAndWhy()
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(WizardFixture.CertificateRefused(null, PresentedCertificate.UntrustedAuthority, PresentedCertificate.NameMismatch));

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.CertificateRefused, wizard.ServerState);
      CertificateView certificate = Assert.IsType<CertificateView>(wizard.Certificate);
      Assert.Equal(Server, certificate.PresentedBy);
      Assert.Equal("CN=qqq.example.test", certificate.Subject);
      Assert.Equal("CN=Example Development Root", certificate.Issuer);
      Assert.Equal("01/01/2026 02:00:00", certificate.ValidFrom);
      Assert.Equal("01/01/2027 02:00:00", certificate.ValidTo);
      Assert.Equal("A1B2C3D4E5F60718293A4B5C6D7E8F9001122334", certificate.Fingerprint);
      Assert.Equal(["Windows does not trust the authority that issued this certificate.", "The certificate was issued for a different name or address than the one entered."], certificate.Refusals);
      Assert.Equal([CheckStageOutcome.Passed, CheckStageOutcome.Failed, CheckStageOutcome.NotReached, CheckStageOutcome.NotReached, CheckStageOutcome.NotReached], wizard.Stages.Select(row => row.Outcome));
      Assert.Equal(["Windows does not trust the authority that issued the certificate"], wizard.Stages[1].Problems);
      Assert.True(wizard.CanRequestException);
      Assert.Null(wizard.ExceptionWarning);
      Assert.Null(wizard.PassedCheck);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.Equal(new WizardAction("Check again", true), wizard.PrimaryAction);
      Assert.Equal("Windows refused the certificate presented by https://qqq.example.test." + LastingFix + " For a development server only, you can choose Use the development-only exception instead.", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Error, wizard.ResultSeverity);
      Assert.Equal(FocusTarget.Certificate, wizard.FocusTarget);
      Assert.Contains("WARN Windows refused a certificate while the service checked https://qqq.example.test (presented by https://qqq.example.test); the certificate exception is offered.", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// The exception is offered only when the reasons include an authority Windows does not trust, the one case it is
   /// for. For any other refusal it is not offered, the step says why, and asking for it does nothing.
   /// </summary>
   [Theory]
   [InlineData("untrustedAuthority", true)]
   [InlineData("untrustedAuthority,nameMismatch", true)]
   [InlineData("nameMismatch", false)]
   [InlineData("notPresented", false)]
   [InlineData("somethingNew", false)]
   [InlineData("UntrustedAuthority", false)]
   public async Task Exception_IsOfferedOnlyForAnAuthorityWindowsDoesNotTrust(string refusals, bool offered)
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(WizardFixture.CertificateRefused(null, refusals.Split(',')));
      fixture.Wizard.RequestException();

      Assert.Equal(ServerStepState.CertificateRefused, fixture.Wizard.ServerState);
      Assert.Equal(offered, fixture.Wizard.CanRequestException);
      Assert.Equal(offered, fixture.Wizard.ExceptionWarning is not null);
      Assert.EndsWith(offered ? " For a development server only, you can choose Use the development-only exception instead."
         : " The development-only exception is not offered, because it only covers a certificate from an authority Windows does not trust.", fixture.Wizard.Result.Text, StringComparison.Ordinal);
      Assert.Single(fixture.Pipe.Sent);
   }



   /// <summary>
   /// When the server's own certificate was refused, the sign-in provider is not known. The first warning names the
   /// server's address and says that the provider's address will be shown after the check. Opening it sends
   /// nothing and accepts nothing.
   /// </summary>
   [Fact]
   public async Task Exception_FirstWarning_NamesTheServer_SaysTheProviderFollows()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused());

      fixture.Wizard.RequestException();

      ConfirmationPrompt warning = Assert.IsType<ConfirmationPrompt>(fixture.Wizard.ExceptionWarning);
      Assert.Equal("Accept an untrusted certificate?", warning.Title);
      Assert.Equal("Accept for this server", warning.Action);
      Assert.Equal(Head + Server + ". Anyone who can get between this computer and that address" + Risk
         + " The server's sign-in provider is not known yet: it can only be read once the server's certificate is accepted. If the server names one at another address, the exception covers that address too, and it will be shown after the check, before you go on."
         + " Nothing is stored until the server is added.", warning.Message);
      Assert.Contains(nameof(ConnectionWizard.ExceptionWarning), fixture.Changes, StringComparer.Ordinal);
      Assert.Equal(ServerStepState.CertificateRefused, fixture.Wizard.ServerState);
      Assert.False(fixture.Wizard.ExceptionAccepted);
      Assert.Single(fixture.Pipe.Sent);
      Assert.Contains("INFO The certificate warning was opened for https://qqq.example.test; the sign-in provider is not known until the check has run under the exception.", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// Accepting the first warning checks the server again under the exception. The re-check returns the provider,
   /// and the step shows both addresses the exception covers before Next can be used: Next is never enabled while
   /// those addresses are not on screen. Going on keeps the acceptance and the result the operator was shown.
   /// </summary>
   [Fact]
   public async Task Exception_Recheck_ShowsBothOrigins_BeforeNextIsEnabled()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      wizard.RequestException();
      List<(bool Next, int Origins)> seen = [];
      wizard.PropertyChanged += (_, _) => seen.Add((wizard.NextAction.Enabled, wizard.ExceptionOrigins.Count));
      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();

      Task accepted = wizard.AcceptExceptionAsync();

      Assert.Null(wizard.ExceptionWarning);
      Assert.Equal(ServerStepState.Checking, wizard.ServerState);
      Assert.Equal(new ServersCheckArgs(Server, AcceptCertificateException: true), fixture.Sent(1));
      Assert.False(wizard.NextAction.Enabled);
      Assert.False(wizard.ExceptionAccepted);

      held.SetResult(Replies.Success(WizardFixture.Check(provider: Provider)));
      await accepted.WaitAsync(WizardFixture.Limit);

      Assert.Equal(ServerStepState.ExceptionReview, wizard.ServerState);
      Assert.True(wizard.ExceptionAccepted);
      Assert.Equal([Server, Provider], wizard.ExceptionOrigins);
      Assert.Equal(new WizardAction("Next", true), wizard.PrimaryAction);
      Assert.DoesNotContain(seen, moment => moment.Next && moment.Origins == 0);
      Assert.Contains(seen, moment => moment.Next && moment.Origins == 2);
      Assert.Equal("The server passed all five checks with the development-only certificate exception. Before you go on, look at what the exception covers."
         + " It accepts a certificate Windows does not trust from two addresses: https://qqq.example.test, the server, and https://sign-in.example.test, the sign-in provider the server names."
         + " It offers sign-in with a user name and password, and sign-in through the browser. Nothing is stored yet: the exception is stored with the server when it is added."
         + " Choose Next to go on with the exception, or choose Check without the exception to drop it.", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Success, wizard.ResultSeverity);
      Assert.Equal(ServerStepState.ExceptionReview, fixture.Moments[^1].State);
      Assert.Equal(new WizardAction("Next", true), fixture.Moments[^1].Primary);
      Assert.Null(wizard.Certificate);
      Assert.False(wizard.CanRequestException);
      Assert.Contains("WARN The operator accepted the development-only certificate exception for https://qqq.example.test. The server is checked again under it; nothing is stored until the server is added.", fixture.Log.Entries, StringComparer.Ordinal);

      await wizard.InvokePrimaryAsync();

      Assert.Equal(WizardStep.SignIn, wizard.Step);
      Assert.True(wizard.ExceptionAccepted);
      Assert.Equal([Server, Provider], wizard.ExceptionOrigins);
      Assert.Equal(Provider, wizard.PassedCheck!.ProviderOrigin);
      Assert.Contains("INFO The Server step is complete for https://qqq.example.test: every stage of the check passed under the certificate exception the operator accepted.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(2, fixture.Pipe.Sent.Count);
   }



   /// <summary>When the re-check names no provider, or names the server's own address, the review shows the one address the exception covers and says that the check named no provider elsewhere.</summary>
   [Theory]
   [InlineData(null)]
   [InlineData(Server)]
   [InlineData("https://QQQ.example.test:443/")]
   public async Task Exception_Recheck_NoProviderElsewhere_ShowsTheOneOrigin(string? provider)
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      fixture.Wizard.RequestException();
      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check(provider: provider)));

      await fixture.Wizard.AcceptExceptionAsync();

      Assert.Equal(ServerStepState.ExceptionReview, fixture.Wizard.ServerState);
      Assert.Equal([Server], fixture.Wizard.ExceptionOrigins);
      Assert.Contains(" It accepts a certificate Windows does not trust from https://qqq.example.test only. The check named no sign-in provider at another address.", fixture.Wizard.Result.Text, StringComparison.Ordinal);
   }



   /// <summary>
   /// When the failed check already reported the provider, one warning names both addresses, and the review is
   /// skipped: after the re-check, which names the same provider, the step has passed and Next can be used. The
   /// addresses the exception covers stay on screen.
   /// </summary>
   [Fact]
   public async Task Exception_ProviderAlreadyKnown_OneWarningNamesBoth()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      await fixture.CheckAsync(WizardFixture.CertificateRefused(Provider));

      wizard.RequestException();

      Assert.Equal(Head + "two addresses: https://qqq.example.test, the server, and https://sign-in.example.test, the sign-in provider the server names. Anyone who can get between this computer and either address" + Risk
         + " Nothing is stored until the server is added.", wizard.ExceptionWarning!.Message);
      Assert.DoesNotContain("after the check", wizard.ExceptionWarning.Message, StringComparison.Ordinal);
      Assert.Contains("INFO The certificate warning was opened for https://qqq.example.test; the sign-in provider is https://sign-in.example.test.", fixture.Log.Entries, StringComparer.Ordinal);

      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check(provider: Provider)));
      await wizard.AcceptExceptionAsync();

      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.True(wizard.ExceptionAccepted);
      Assert.Equal([Server, Provider], wizard.ExceptionOrigins);
      Assert.Equal(new WizardAction("Next", true), wizard.PrimaryAction);
      Assert.StartsWith("The server passed all five checks with the development-only certificate exception. It accepts a certificate Windows does not trust from two addresses: https://qqq.example.test, the server, and https://sign-in.example.test,",
         wizard.Result.Text, StringComparison.Ordinal);
      Assert.DoesNotContain("Before you go on", wizard.Result.Text, StringComparison.Ordinal);
      Assert.Equal(new ServersCheckArgs(Server, AcceptCertificateException: true), fixture.Sent(1));
   }



   /// <summary>When the failed check reported the server's own address as the provider, the one warning names the one address and says that the check named no provider elsewhere; the re-check that agrees needs no review.</summary>
   [Fact]
   public async Task Exception_ProviderIsTheServerItself_OneWarningNamesTheOneAddress()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused(Server));

      fixture.Wizard.RequestException();

      Assert.Equal(Head + Server + ". The check named no sign-in provider at another address. Anyone who can get between this computer and that address" + Risk + " Nothing is stored until the server is added.",
         fixture.Wizard.ExceptionWarning!.Message);

      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check(provider: Server)));
      await fixture.Wizard.AcceptExceptionAsync();

      Assert.Equal(ServerStepState.Passed, fixture.Wizard.ServerState);
      Assert.Equal([Server], fixture.Wizard.ExceptionOrigins);
   }



   /// <summary>
   /// The review is skipped only for the provider the warning named. When the re-check names another provider, or
   /// none, than the one the operator accepted in the warning, the step shows what the exception now covers and
   /// asks the operator to look before going on.
   /// </summary>
   [Theory]
   [InlineData("https://other-sign-in.example.test", 2)]
   [InlineData(null, 1)]
   public async Task Exception_RecheckNamesAnotherProviderThanTheWarning_IsReviewed(string? now, int origins)
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused(Provider));
      fixture.Wizard.RequestException();
      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check(provider: now)));

      await fixture.Wizard.AcceptExceptionAsync();

      Assert.Equal(ServerStepState.ExceptionReview, fixture.Wizard.ServerState);
      Assert.Equal(origins, fixture.Wizard.ExceptionOrigins.Count);
      Assert.DoesNotContain(Provider, fixture.Wizard.ExceptionOrigins, StringComparer.Ordinal);
      Assert.Contains("Before you go on, look at what the exception covers.", fixture.Wizard.Result.Text, StringComparison.Ordinal);
      Assert.Equal(now, fixture.Wizard.PassedCheck!.ProviderOrigin);
   }



   /// <summary>A provider the failed check reported as text that is no origin is not named: the warning treats the provider as not known yet, and the text is neither shown nor logged.</summary>
   [Fact]
   public async Task Exception_ProviderThatIsNoOrigin_IsTreatedAsNotKnownYet()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused("NOT-AN-ORIGIN"));

      fixture.Wizard.RequestException();

      Assert.Contains("is not known yet", fixture.Wizard.ExceptionWarning!.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("NOT-AN-ORIGIN", fixture.Wizard.ExceptionWarning.Message, StringComparison.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("NOT-AN-ORIGIN", StringComparison.Ordinal));
   }



   /// <summary>
   /// A declined exception is forgotten. Declined in the warning, nothing was accepted and the next check runs
   /// without it. Dropped after it was accepted, the result obtained under it goes with it, and the next check runs
   /// without it.
   /// </summary>
   [Fact]
   public async Task Exception_Declined_IsForgotten()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      wizard.RequestException();

      wizard.DeclineException();

      Assert.Null(wizard.ExceptionWarning);
      Assert.False(wizard.ExceptionAccepted);
      Assert.Equal(ServerStepState.CertificateRefused, wizard.ServerState);
      Assert.True(wizard.CanRequestException);
      Assert.Single(fixture.Pipe.Sent);
      Assert.Contains("INFO The certificate warning was declined; no exception is accepted.", fixture.Log.Entries, StringComparer.Ordinal);
      await wizard.AcceptExceptionAsync();
      Assert.Single(fixture.Pipe.Sent);

      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      Assert.False(fixture.Sent(1).AcceptCertificateException);

      wizard.RequestException();
      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check(provider: Provider)));
      await wizard.AcceptExceptionAsync();
      Assert.True(fixture.Sent(2).AcceptCertificateException);
      Assert.True(wizard.ExceptionAccepted);
      Assert.Equal(ServerStepState.ExceptionReview, wizard.ServerState);

      wizard.DeclineException();

      Assert.False(wizard.ExceptionAccepted);
      Assert.Empty(wizard.ExceptionOrigins);
      Assert.Equal(ServerStepState.Editing, wizard.ServerState);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.PassedCheck);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.Equal("The certificate exception was dropped. Choose Check to check the server without it.", wizard.Result.Text);
      Assert.Equal(FocusTarget.PrimaryAction, wizard.FocusTarget);
      Assert.Contains("INFO The operator dropped the certificate exception that had been accepted; the result obtained under it was removed.", fixture.Log.Entries, StringComparer.Ordinal);

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check(provider: Provider)));

      Assert.False(fixture.Sent(3).AcceptCertificateException);
      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.False(wizard.ExceptionAccepted);
      Assert.Empty(wizard.ExceptionOrigins);
   }



   /// <summary>Declining when no warning is open and nothing was accepted does nothing.</summary>
   [Fact]
   public async Task Exception_DeclinedWithNothingToDecline_DoesNothing()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));
      fixture.Changes.Clear();
      fixture.Announced.Clear();

      fixture.Wizard.DeclineException();

      Assert.Equal(ServerStepState.Passed, fixture.Wizard.ServerState);
      Assert.Empty(fixture.Changes);
      Assert.Empty(fixture.Announced);
   }



   /// <summary>Inside the certificate warning, Escape closes the warning only: the wizard goes on, and nothing was accepted.</summary>
   [Fact]
   public async Task Escape_InsideTheWarning_ClosesTheWarningOnly()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      fixture.Wizard.RequestException();

      fixture.Wizard.InvokeCancel();

      Assert.Null(fixture.Wizard.ExceptionWarning);
      Assert.Null(fixture.Wizard.End);
      Assert.False(fixture.Wizard.ExceptionAccepted);
      Assert.Equal(ServerStepState.CertificateRefused, fixture.Wizard.ServerState);

      fixture.Wizard.InvokeCancel();

      Assert.Equal(WizardEnd.Cancelled, fixture.Wizard.End);
   }



   /// <summary>
   /// When the certificate is refused again although the exception was accepted, the exception does not cover what
   /// Windows refused. The step says so, drops the acceptance and does not offer the exception again for that
   /// result, even when the reasons still include an untrusted authority.
   /// </summary>
   [Theory]
   [InlineData("nameMismatch")]
   [InlineData("untrustedAuthority,nameMismatch")]
   public async Task Exception_RecheckStillRefused_SaysItDoesNotCoverIt_AndIsNotOfferedAgain(string refusals)
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      await fixture.CheckAsync(WizardFixture.CertificateRefused(null, PresentedCertificate.UntrustedAuthority, PresentedCertificate.NameMismatch));
      wizard.RequestException();
      fixture.Pipe.Answer(WizardFixture.CertificateRefused(null, refusals.Split(',')));

      await wizard.AcceptExceptionAsync();

      Assert.Equal(ServerStepState.CertificateRefused, wizard.ServerState);
      Assert.False(wizard.ExceptionAccepted);
      Assert.False(wizard.CanRequestException);
      Assert.NotNull(wizard.Certificate);
      Assert.Equal("The certificate exception does not cover what Windows refused, so it was dropped. Windows refused the certificate presented by https://qqq.example.test." + LastingFix, wizard.Result.Text);
      Assert.Contains("WARN Windows refused a certificate while the service checked https://qqq.example.test (presented by https://qqq.example.test); the certificate exception was accepted and does not cover it.", fixture.Log.Entries, StringComparer.Ordinal);
      wizard.RequestException();
      Assert.Null(wizard.ExceptionWarning);

      await fixture.CheckAsync(WizardFixture.CertificateRefused());

      Assert.False(fixture.Sent(2).AcceptCertificateException);
      Assert.True(wizard.CanRequestException);
   }



   /// <summary>
   /// The exception is accepted for one check. When that check does not pass, for whatever reason, the acceptance
   /// is forgotten: the next check runs without it, and the warning has to be accepted again.
   /// </summary>
   [Theory]
   [InlineData("unanswered")]
   [InlineData("notSent")]
   [InlineData(ManagementErrorCode.ServerIncompatible)]
   [InlineData(ManagementErrorCode.InternalError)]
   [InlineData(ManagementErrorCode.UpdateInProgress)]
   public async Task Exception_AcceptedCheckThatDoesNotPass_IsForgotten(string outcome)
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      wizard.RequestException();
      _ = outcome switch
      {
         "unanswered" => fixture.Pipe.Fail(new IOException("The pipe closed.")),
         "notSent" => fixture.Pipe.Fail(new AgentPipeNotSentException(new IOException("No pipe."))),
         ManagementErrorCode.ServerIncompatible => fixture.Pipe.Answer(Replies.Failure(outcome, Replies.Message, WizardFixture.Check(2))),
         _ => fixture.Pipe.Answer(WizardFixture.Refusal(outcome)),
      };

      await wizard.AcceptExceptionAsync();

      Assert.True(fixture.Sent(1).AcceptCertificateException);
      Assert.False(wizard.ExceptionAccepted);
      Assert.Empty(wizard.ExceptionOrigins);
      Assert.False(wizard.CanRequestException);
      Assert.Null(wizard.ExceptionWarning);
      Assert.False(wizard.NextAction.Enabled);

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));

      Assert.False(fixture.Sent(2).AcceptCertificateException);
      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.False(wizard.ExceptionAccepted);
   }



   /// <summary>A check asked for again while a pass under the exception is on screen runs under the exception again, because the acceptance stands until it is dropped or the address is edited.</summary>
   [Fact]
   public async Task Exception_CheckAgainFromAPassUnderIt_RunsUnderItAgain()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      fixture.Wizard.RequestException();
      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check(provider: Provider)));
      await fixture.Wizard.AcceptExceptionAsync();

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check(provider: Provider)));

      Assert.True(fixture.Sent(2).AcceptCertificateException);
      Assert.True(fixture.Wizard.ExceptionAccepted);
      Assert.Equal(ServerStepState.ExceptionReview, fixture.Wizard.ServerState);
   }



   /// <summary>Accepting when no warning is open accepts nothing and sends nothing.</summary>
   [Fact]
   public async Task Exception_AcceptedWithoutAWarning_DoesNothing()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused());

      await fixture.Wizard.AcceptExceptionAsync();

      Assert.Single(fixture.Pipe.Sent);
      Assert.False(fixture.Wizard.ExceptionAccepted);
      Assert.Equal(ServerStepState.CertificateRefused, fixture.Wizard.ServerState);
   }



   /// <summary>A certificate failure that came without a certificate shows the stages as any failed check does, and a failed check of another kind is not a certificate refusal even when a certificate came with it.</summary>
   [Theory]
   [InlineData(ManagementErrorCode.CertificateUntrusted, false)]
   [InlineData(ManagementErrorCode.ServerIncompatible, true)]
   public async Task Check_OnlyACertificateFailureWithACertificate_IsACertificateRefusal(string code, bool withCertificate)
   {
      using WizardFixture fixture = new();
      ServerCheckResult check = WizardFixture.Check(1, certificate: withCertificate ? WizardFixture.Certificate(PresentedCertificate.UntrustedAuthority) : null);

      await fixture.CheckAsync(Replies.Failure(code, Replies.Message, check));

      Assert.Equal(ServerStepState.Failed, fixture.Wizard.ServerState);
      Assert.Equal(5, fixture.Wizard.Stages.Count);
      Assert.Null(fixture.Wizard.Certificate);
      Assert.False(fixture.Wizard.CanRequestException);
      Assert.Equal(WizardMessages.Failed("Trusted secure connection"), fixture.Wizard.Result.Text);
   }



   /// <summary>
   /// The certificate is shown from what the service reported, and no more. A part that is missing or shows nothing
   /// is not shown; text is in the contract's printable form; the address is shown only as an origin, in the form
   /// every origin is shown in; a reason this version does not know is said to be that, once; and a certificate
   /// that came with no reason says so.
   /// </summary>
   [Fact]
   public void CertificateView_ShowsOnlyWhatWasReported_InPrintableForm()
   {
      var bare = CertificateView.From(new PresentedCertificate([PresentedCertificate.NotPresented], "https://QQQ.example.test:443/ignored?x=1"), TestData.Time);
      Assert.Equal("https://qqq.example.test", bare.PresentedBy);
      Assert.Null(bare.Subject);
      Assert.Null(bare.Issuer);
      Assert.Null(bare.ValidFrom);
      Assert.Null(bare.ValidTo);
      Assert.Null(bare.Fingerprint);
      Assert.Equal(["The server presented no certificate."], bare.Refusals);

      var hostile = CertificateView.From(new PresentedCertificate(["somethingNew", "somethingElse"], "not an origin", null, "CN=evil\r\n\u202Etxt.exe", "\u200B", null, null, "  "), TestData.Time);
      Assert.Null(hostile.PresentedBy);
      Assert.Equal("CN=evil   txt.exe", hostile.Subject);
      Assert.Null(hostile.Issuer);
      Assert.Null(hostile.Fingerprint);
      Assert.Equal(["Windows refused the certificate for a reason this version of the companion does not know."], hostile.Refusals);

      var silent = CertificateView.From(new PresentedCertificate([], null, null, new string('s', 600)), TestData.Time);
      Assert.Null(silent.PresentedBy);
      Assert.Equal(new string('s', ManagementProtocol.MaximumMessageCharacters), silent.Subject);
      Assert.Equal(["Windows refused the certificate, and the print service did not say why."], silent.Refusals);

      Assert.Equal("https://xn--bcher-kva.example", CertificateView.Origin("https://b\u00FCcher.example/"));
      Assert.Equal("https://qqq.example.test:8443", CertificateView.Origin("https://qqq.example.test:8443"));
      Assert.Null(CertificateView.Origin(null));
      Assert.Null(CertificateView.Origin("mailto:someone@example.test"));
   }



   /// <summary>When the service did not say which address presented the certificate, the step does not name one.</summary>
   [Fact]
   public async Task Check_CertificateRefused_PresenterNotNamed_NamesNone()
   {
      using WizardFixture fixture = new();
      ServerCheckResult check = WizardFixture.Check(1, certificate: new PresentedCertificate([PresentedCertificate.NameMismatch]));

      await fixture.CheckAsync(Replies.Failure(ManagementErrorCode.CertificateUntrusted, Replies.Message, check));

      Assert.Equal(ServerStepState.CertificateRefused, fixture.Wizard.ServerState);
      Assert.Null(fixture.Wizard.Certificate!.PresentedBy);
      Assert.StartsWith("Windows refused the certificate that was presented. The print service checks certificates under its own account, not yours.", fixture.Wizard.Result.Text, StringComparison.Ordinal);
      Assert.Contains("WARN Windows refused a certificate while the service checked https://qqq.example.test (presented by an address the service did not name); the certificate exception is not offered.", fixture.Log.Entries, StringComparer.Ordinal);
   }
}
