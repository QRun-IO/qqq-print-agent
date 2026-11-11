/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Presentation.Wizard;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Wizard;

/// <summary>The wizard's Server step: what a check sends, what each answer shows and says, what the name and the address do, and how a check that is running is refused, abandoned and cancelled.</summary>
public sealed class WizardServerStepTests
{
   /// <summary>A new wizard is on the Server step, editing, with nothing typed, nothing said and nothing sent; Enter would check, and Next cannot be used.</summary>
   [Fact]
   public void Start_IsTheServerStep_WithNothingTypedSaidOrSent()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;

      Assert.Equal(WizardMode.AddServer, wizard.Mode);
      Assert.Equal(WizardStep.Server, wizard.Step);
      Assert.Equal("Step 1 of 5: Server", wizard.StepTitle);
      Assert.Equal(ServerStepState.Editing, wizard.ServerState);
      Assert.Equal("", wizard.Address);
      Assert.Equal("", wizard.Name);
      Assert.Null(wizard.NameProblem);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.Certificate);
      Assert.Null(wizard.SignInMethods);
      Assert.Null(wizard.ExceptionWarning);
      Assert.False(wizard.CanRequestException);
      Assert.False(wizard.ExceptionAccepted);
      Assert.Empty(wizard.ExceptionOrigins);
      Assert.Null(wizard.ExistingServerId);
      Assert.Null(wizard.PassedCheck);
      Assert.Null(wizard.CheckedOrigin);
      Assert.Equal(new WizardAction("Check", true), wizard.PrimaryAction);
      Assert.Equal(new WizardAction("Check", true), wizard.CheckAction);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.Equal(new WizardAction("Cancel", true), wizard.CancelAction);
      Assert.False(wizard.CanGoBack);
      Assert.False(wizard.IsBusy);
      Assert.Null(wizard.End);
      Assert.Equal(FocusTarget.StepStart, wizard.FocusTarget);
      Assert.Equal("", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Informational, wizard.ResultSeverity);
      Assert.Empty(fixture.Pipe.Sent);
      Assert.Equal(["INFO The connection wizard was opened to add a server."], fixture.Log.Entries);
   }



   /// <summary>
   /// A check that passes shows the five stages in the contract's order, all passed, and the sign-in methods, and
   /// enables Next. What was sent is one check of the address, without a certificate exception. The result that is
   /// kept for adding the server is the one whose rows are shown.
   /// </summary>
   [Fact]
   public async Task Check_AllFivePass_NextIsEnabled()
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check(provider: WizardFixture.Provider)));

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.Equal(["connection", "security", "server", "printing", "signIn"], wizard.Stages.Select(row => row.Stage), StringComparer.Ordinal);
      Assert.Equal(["Reach the address", "Trusted secure connection", "QQQ server", "Printing extension", "Sign-in method"], wizard.Stages.Select(row => row.Title), StringComparer.Ordinal);
      Assert.All(wizard.Stages, row =>
      {
         Assert.Equal(CheckStageOutcome.Passed, row.Outcome);
         Assert.Equal("Passed", row.OutcomeText);
         Assert.Empty(row.Problems);
         Assert.Equal(row.Title + ": Passed.", row.AccessibleName);
      });
      Assert.Equal(new SignInOffer(true, true), wizard.SignInMethods);
      Assert.Equal(new WizardAction("Next", true), wizard.NextAction);
      Assert.Equal(wizard.NextAction, wizard.PrimaryAction);
      Assert.Equal(new WizardAction("Check", true), wizard.CheckAction);
      Assert.False(wizard.ExceptionAccepted);
      Assert.Empty(wizard.ExceptionOrigins);
      Assert.Null(wizard.Certificate);
      Assert.Equal("The server passed all five checks. It offers sign-in with a user name and password, and sign-in through the browser. Nothing is stored yet. Choose Next to sign in.", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Success, wizard.ResultSeverity);
      Assert.Equal(FocusTarget.PrimaryAction, wizard.FocusTarget);
      Assert.Equal(new ServersCheckArgs(WizardFixture.Address, AcceptCertificateException: false), fixture.Sent(0));
      Assert.Single(fixture.Pipe.Sent);
      Assert.NotNull(wizard.PassedCheck);
      Assert.Equal(WizardFixture.Provider, wizard.PassedCheck.ProviderOrigin);
      Assert.Equal(new Uri("https://qqq.example.test/"), wizard.CheckedOrigin);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
   }



   /// <summary>The rows are in the contract's order whatever order the service sent the stages in.</summary>
   [Fact]
   public async Task Check_StagesSentInAnotherOrder_AreShownInTheContractsOrder()
   {
      using WizardFixture fixture = new();
      ServerCheckResult check = WizardFixture.Check();

      await fixture.CheckAsync(Replies.Success(check with { Stages = [.. check.Stages.Reverse()] }));

      Assert.Equal(CompatibilityStageResult.Order, fixture.Wizard.Stages.Select(row => row.Stage), StringComparer.Ordinal);
      Assert.Equal(ServerStepState.Passed, fixture.Wizard.ServerState);
   }



   /// <summary>The sentence names the methods the check found, and only those this version knows.</summary>
   [Theory]
   [InlineData("password", true, false, "It offers sign-in with a user name and password.")]
   [InlineData("browser", false, true, "It offers sign-in through the browser.")]
   [InlineData("passkey,browser", false, true, "It offers sign-in through the browser.")]
   public async Task Check_Passes_NamesTheSignInMethodsItKnows(string methods, bool password, bool browser, string sentence)
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check(methods: methods.Split(','))));

      Assert.Equal(new SignInOffer(password, browser), fixture.Wizard.SignInMethods);
      Assert.Equal("The server passed all five checks. " + sentence + " Nothing is stored yet. Choose Next to sign in.", fixture.Wizard.Result.Text);
   }



   /// <summary>What is sent is the origin the contract's address rule returned, not the text as typed: lower case, no trailing slash, no surrounding space, and a port only when it is not the scheme's own.</summary>
   [Theory]
   [InlineData("  HTTPS://QQQ.Example.Test/  ", "https://qqq.example.test")]
   [InlineData("https://qqq.example.test:443", "https://qqq.example.test")]
   [InlineData("https://qqq.example.test:8443/", "https://qqq.example.test:8443")]
   [InlineData("http://localhost:8080", "http://localhost:8080")]
   [InlineData("https://[::1]:9443", "https://[::1]:9443")]
   public async Task Check_SendsTheOriginTheAddressRuleReturned(string typed, string sent)
   {
      using WizardFixture fixture = new();
      fixture.Wizard.Address = typed;

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));

      Assert.Equal(sent, fixture.Sent(0).Address);
      Assert.Equal(typed, fixture.Wizard.Address);
      Assert.Equal(ServerStepState.Passed, fixture.Wizard.ServerState);
   }



   /// <summary>A check under the accepted exception is, on the wire, the contract's golden request.</summary>
   [Fact]
   public async Task Check_UnderTheException_IsTheGoldenRequest()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      fixture.Wizard.RequestException();
      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check()));

      await fixture.Wizard.AcceptExceptionAsync();

      Assert.Equal(Replies.GoldenText("servers.check.request.json"), fixture.Pipe.Wire(1));
   }



   /// <summary>
   /// A failed check shows one row per stage in order: those before the failure passed, the failed one with its
   /// reasons, and those after it not reached. Next cannot be used, Enter checks again, and the sentence names the
   /// stage that failed. One case per stage.
   /// </summary>
   [Theory]
   [InlineData(0, "Reach the address")]
   [InlineData(1, "Trusted secure connection")]
   [InlineData(2, "QQQ server")]
   [InlineData(3, "Printing extension")]
   [InlineData(4, "Sign-in method")]
   public async Task Check_AStageFails_ShowsPassedFailedAndNotReachedInOrder(int failed, string title)
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(Replies.Failure(ManagementErrorCode.ServerIncompatible, Replies.Message, WizardFixture.Check(failed, ["the first reason", "the second reason"])));

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.Failed, wizard.ServerState);
      Assert.Equal(5, wizard.Stages.Count);
      for(int index = 0; index < 5; index++)
      {
         CheckStageRow row = wizard.Stages[index];
         Assert.Equal(CompatibilityStageResult.Order[index], row.Stage);
         Assert.Equal(index < failed ? CheckStageOutcome.Passed : index == failed ? CheckStageOutcome.Failed : CheckStageOutcome.NotReached, row.Outcome);
         Assert.Equal(index < failed ? "Passed" : index == failed ? "Failed" : "Not checked, because an earlier check failed", row.OutcomeText);
         Assert.Equal(index == failed ? ["the first reason", "the second reason"] : [], row.Problems);
      }
      Assert.Equal(title + ": Failed. the first reason; the second reason", wizard.Stages[failed].AccessibleName);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.Equal(new WizardAction("Check again", true), wizard.PrimaryAction);
      Assert.Null(wizard.PassedCheck);
      Assert.Null(wizard.SignInMethods);
      Assert.Null(wizard.Certificate);
      Assert.False(wizard.CanRequestException);
      Assert.Equal("The server did not pass the check: \"" + title + "\" failed. The reasons are in the list of checks. Nothing was stored. Correct the problem, then choose Check again.", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Error, wizard.ResultSeverity);
      Assert.Equal(FocusTarget.CheckStages, wizard.FocusTarget);
      Assert.Contains("WARN The check of https://qqq.example.test did not pass; the stage that failed is " + CompatibilityStageResult.Order[failed] + ".", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// A stage the service did not report, or reported with an outcome this version does not know, is shown as not
   /// known and never as passed; a stage this version does not know is not shown. With no stage known to have
   /// failed, the sentence does not name one.
   /// </summary>
   [Fact]
   public async Task Check_UnknownStagesAndOutcomes_AreShownAsNotKnown_NeverAsPassed()
   {
      using WizardFixture fixture = new();
      ServerCheckResult check = new(
      [
         new CompatibilityStageResult("connection", "passed", []),
         new CompatibilityStageResult("security", "postponed", ["a reason that came with it"]),
         new CompatibilityStageResult("licence", "failed", ["not a stage this version shows"]),
         new CompatibilityStageResult("printing", "notReached", []),
      ], []);

      await fixture.CheckAsync(Replies.Failure(ManagementErrorCode.ServerIncompatible, Replies.Message, check));

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(["connection", "security", "server", "printing", "signIn"], wizard.Stages.Select(row => row.Stage), StringComparer.Ordinal);
      Assert.Equal([CheckStageOutcome.Passed, CheckStageOutcome.Unknown, CheckStageOutcome.Unknown, CheckStageOutcome.NotReached, CheckStageOutcome.Unknown], wizard.Stages.Select(row => row.Outcome));
      Assert.Equal("Not known", wizard.Stages[1].OutcomeText);
      Assert.Equal(["a reason that came with it"], wizard.Stages[1].Problems);
      Assert.Equal("QQQ server: Not known.", wizard.Stages[2].AccessibleName);
      Assert.Equal(ServerStepState.Failed, wizard.ServerState);
      Assert.Equal("The server did not pass the check. The list of checks shows how far it got. Nothing was stored. Correct the problem, then choose Check again.", wizard.Result.Text);
      Assert.Contains("WARN The check of https://qqq.example.test did not pass; the stage that failed is not one this version knows.", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// The reasons of a stage are text the service passed on and may quote a server, so each is shown in the
   /// contract's printable form: a line break and a character that reorders text become spaces, and a reason that
   /// then shows nothing is left out. The reply carries them as the service wrote them, so this is the wizard's doing.
   /// </summary>
   [Fact]
   public async Task StageRows_ShowReasonsInTheContractsPrintableForm()
   {
      using WizardFixture fixture = new();
      string[] reasons = ["first\r\nline", "\u202Ereversed", "\u200B\u200B", new string('x', 600)];

      await fixture.CheckAsync(Replies.Failure(ManagementErrorCode.ServerIncompatible, Replies.Message, WizardFixture.Check(0, reasons)));

      CheckStageRow row = fixture.Wizard.Stages[0];
      Assert.Equal(["first  line", "reversed", new string('x', ManagementProtocol.MaximumMessageCharacters)], row.Problems);
      Assert.Equal("Reach the address: Failed. first  line; reversed; " + new string('x', ManagementProtocol.MaximumMessageCharacters), row.AccessibleName);
      Assert.All(fixture.Wizard.Stages, shown => Assert.All(shown.Problems, problem => Assert.Equal(ManagementText.Bounded(problem, ManagementProtocol.MaximumMessageCharacters), problem)));
   }



   /// <summary>
   /// A failed check whose details are missing, or do not fit the contract's record, shows the service's message
   /// and no rows, for either failure code. The message is not changed on its way to the screen: the contract has
   /// already given it its printable form.
   /// </summary>
   [Theory]
   [InlineData(ManagementErrorCode.ServerIncompatible, false)]
   [InlineData(ManagementErrorCode.CertificateUntrusted, false)]
   [InlineData(ManagementErrorCode.ServerIncompatible, true)]
   [InlineData(ManagementErrorCode.CertificateUntrusted, true)]
   public async Task Check_DetailsUnreadable_ShowsTheMessageAndNoRows(string code, bool detailsThatDoNotFit)
   {
      using WizardFixture fixture = new();
      ManagementReplyParse reply = detailsThatDoNotFit
         ? Replies.Text("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"" + code + "\",\"message\":\"The server answered with an unexpected page\",\"details\":{\"stages\":\"none\"}}}")
         : WizardFixture.Refusal(code, "The server answered with an unexpected page");

      await fixture.CheckAsync(reply);

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.Failed, wizard.ServerState);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.Certificate);
      Assert.False(wizard.CanRequestException);
      Assert.Equal("The server did not pass the check, and the print service sent no details. It said: The server answered with an unexpected page. Nothing was stored. Choose Check again to repeat the check.",
         wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Error, wizard.ResultSeverity);
      Assert.Equal(new WizardAction("Check again", true), wizard.PrimaryAction);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.Equal(FocusTarget.PrimaryAction, wizard.FocusTarget);
   }



   /// <summary>
   /// An address that is already a server says so and offers to open it, whether the check passed or failed, and
   /// Next cannot be used. Opening it ends the wizard on that server. Enter says again that there is nothing to add.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task Check_AddressIsAlreadyAServer_SaysSo_OffersToOpenIt(bool passed)
   {
      using WizardFixture fixture = new();
      ManagementReplyParse reply = passed
         ? Replies.Success(WizardFixture.Check(existing: WizardFixture.ServerId))
         : Replies.Failure(ManagementErrorCode.CertificateUntrusted, Replies.Message, WizardFixture.Check(1, certificate: WizardFixture.Certificate(PresentedCertificate.UntrustedAuthority), existing: WizardFixture.ServerId));

      await fixture.CheckAsync(reply);

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.AlreadyServer, wizard.ServerState);
      Assert.Equal(WizardFixture.ServerId, wizard.ExistingServerId);
      Assert.Equal(5, wizard.Stages.Count);
      Assert.Equal(new WizardAction("Next", false), wizard.PrimaryAction);
      Assert.Equal(new WizardAction("Check", true), wizard.CheckAction);
      Assert.Null(wizard.PassedCheck);
      Assert.Null(wizard.Certificate);
      Assert.False(wizard.CanRequestException);
      Assert.Equal("This address is already a server on this computer, so there is nothing to add. Choose Open server to go to it, or enter another address.", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Informational, wizard.ResultSeverity);
      Assert.Equal(FocusTarget.ExistingServer, wizard.FocusTarget);

      await wizard.InvokePrimaryAsync();

      Assert.Equal(2, fixture.Announced.Count(text => string.Equals(text, WizardMessages.AlreadyServer, StringComparison.Ordinal)));
      Assert.Equal(WizardStep.Server, wizard.Step);
      Assert.Single(fixture.Pipe.Sent);

      wizard.OpenExistingServer();

      Assert.Equal(WizardEnd.Server(WizardFixture.ServerId), wizard.End);
      Assert.Equal(WizardFixture.ServerId, wizard.End!.ServerId);
      Assert.False(wizard.End.OpensComputerPage);
      Assert.Equal(new WizardAction("Cancel", false), wizard.CancelAction);
      Assert.Contains("INFO The connection wizard was closed to open the server the address already is (" + WizardFixture.ServerId + ").", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Single(fixture.Pipe.Sent);
   }



   /// <summary>When the service names the existing server in a form that is not a server identifier, the step still says that the address is a server, offers nothing to open, and never shows or logs what it was given.</summary>
   [Fact]
   public async Task Check_AlreadyAServer_NamedByNoIdentifier_SaysSo_OpensNothing()
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check(existing: "SOMETHING-ELSE")));

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.AlreadyServer, wizard.ServerState);
      Assert.Null(wizard.ExistingServerId);
      Assert.Equal("This address is already a server on this computer, so there is nothing to add. Find it in the list of servers, or enter another address.", wizard.Result.Text);
      Assert.Equal(FocusTarget.StepStart, wizard.FocusTarget);
      wizard.OpenExistingServer();
      Assert.Null(wizard.End);
      await wizard.InvokePrimaryAsync();
      Assert.Equal(2, fixture.Announced.Count(text => string.Equals(text, WizardMessages.AlreadyServerNotNamed, StringComparison.Ordinal)));
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("SOMETHING-ELSE", StringComparison.Ordinal));
   }



   /// <summary>
   /// Editing the address removes the result of the check: the rows, the certificate, the sentence, the result kept
   /// for adding the server, and an accepted certificate exception. The next check runs without the exception.
   /// </summary>
   [Fact]
   public async Task AddressEdited_ClearsTheResultAndTheAcceptance()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      fixture.Wizard.RequestException();
      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check(provider: WizardFixture.Provider)));
      await fixture.Wizard.AcceptExceptionAsync();
      ConnectionWizard wizard = fixture.Wizard;
      Assert.True(wizard.ExceptionAccepted);
      Assert.Equal(ServerStepState.ExceptionReview, wizard.ServerState);
      Assert.NotNull(wizard.PassedCheck);
      fixture.Announced.Clear();

      wizard.Address = "https://other.example.test";

      Assert.Equal(ServerStepState.Editing, wizard.ServerState);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.Certificate);
      Assert.Null(wizard.SignInMethods);
      Assert.Null(wizard.PassedCheck);
      Assert.Null(wizard.CheckedOrigin);
      Assert.False(wizard.ExceptionAccepted);
      Assert.Empty(wizard.ExceptionOrigins);
      Assert.Equal("", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Informational, wizard.ResultSeverity);
      Assert.Equal(new WizardAction("Check", true), wizard.PrimaryAction);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.Empty(fixture.Announced);

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));

      Assert.Equal(new ServersCheckArgs("https://other.example.test", AcceptCertificateException: false), fixture.Sent(2));
      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.False(wizard.ExceptionAccepted);
   }



   /// <summary>Editing the address also closes the certificate warning and withdraws the offer of the exception, which belonged to the certificate of the address before.</summary>
   [Fact]
   public async Task AddressEdited_ClosesTheWarning_AndTheOffer()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      fixture.Wizard.RequestException();
      Assert.NotNull(fixture.Wizard.ExceptionWarning);

      fixture.Wizard.Address = "https://other.example.test";

      Assert.Null(fixture.Wizard.ExceptionWarning);
      Assert.False(fixture.Wizard.CanRequestException);
      await fixture.Wizard.AcceptExceptionAsync();
      fixture.Wizard.RequestException();
      Assert.Null(fixture.Wizard.ExceptionWarning);
      Assert.Single(fixture.Pipe.Sent);
   }



   /// <summary>Setting the address to the text it already has is not an edit: the result stays and nothing is announced as changed. No text at all is the empty address, which is an edit.</summary>
   [Fact]
   public async Task AddressSetToTheSameText_KeepsTheResult()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));
      fixture.Changes.Clear();

      fixture.Wizard.Address = WizardFixture.Address;

      Assert.Equal(ServerStepState.Passed, fixture.Wizard.ServerState);
      Assert.NotNull(fixture.Wizard.PassedCheck);
      Assert.Empty(fixture.Changes);

      fixture.Wizard.Address = null!;

      Assert.Equal("", fixture.Wizard.Address);
      Assert.Equal(ServerStepState.Editing, fixture.Wizard.ServerState);
      Assert.Null(fixture.Wizard.PassedCheck);
      Assert.Contains(nameof(ConnectionWizard.Address), fixture.Changes, StringComparer.Ordinal);
   }



   /// <summary>
   /// The name is the contract's proposal for the address until the operator types one: it appears with the first
   /// address that can be a server's, changes with the address, is empty while the address is not one, and passes
   /// the name rule where the host itself would not. Once typed it stays, whatever the address becomes.
   /// </summary>
   [Fact]
   public void Name_FollowsTheProposal_UntilTyped()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;

      wizard.Address = "https://qqq";
      Assert.Equal("qqq", wizard.Name);
      wizard.Address = "https://qqq.example.test";
      Assert.Equal("qqq.example.test", wizard.Name);
      Assert.Contains(nameof(ConnectionWizard.Name), fixture.Changes, StringComparer.Ordinal);
      wizard.Address = "https://[::1]:9443";
      Assert.Equal("::1", wizard.Name);
      wizard.Address = "https://deadbeef";
      Assert.Equal("Server deadbeef", wizard.Name);
      Assert.Equal(ServerName.Propose(new Uri("https://deadbeef")), wizard.Name);
      wizard.Address = "qqq.example.test";
      Assert.Equal("", wizard.Name);
      Assert.Null(wizard.NameProblem);

      wizard.Address = "https://qqq.example.test";
      wizard.Name = "qqq.example.test";
      wizard.Address = "https://bench.example.test";
      Assert.Equal("bench.example.test", wizard.Name);

      wizard.Name = "Warehouse";
      wizard.Address = "https://other.example.test";
      Assert.Equal("Warehouse", wizard.Name);
      wizard.Name = null!;
      Assert.Equal("", wizard.Name);
      wizard.Address = "https://third.example.test";
      Assert.Equal("", wizard.Name);
   }



   /// <summary>
   /// A typed name that breaks the name rule disables Next and says which part of the rule it breaks, for every part.
   /// Enter then says the same and points at the name, and the wizard stays on the step. A name that passes
   /// enables Next again.
   /// </summary>
   [Theory]
   [InlineData("   ", "Enter a name for this server.")]
   [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789", "The name is too long. Use at most 64 characters.")]
   [InlineData("two\nlines", "The name contains a character that cannot be shown, such as a line break. Remove it.")]
   [InlineData("\u3164", "The name shows nothing on screen. Use letters or digits.")]
   [InlineData("Dock [2]", "The name must not contain square brackets. Remove them.")]
   [InlineData("deadbeef", "The name looks like a server identifier, which the command line would take for one. Put a word in front, for example Server.")]
   public async Task Name_BreakingTheRule_DisablesNext_WithTheReason(string name, string reason)
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));
      ConnectionWizard wizard = fixture.Wizard;
      Assert.True(wizard.NextAction.Enabled);

      wizard.Name = name;

      Assert.NotEqual(ServerNameProblem.None, ServerName.Check(name));
      Assert.Equal(reason, wizard.NameProblem);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.Equal(new WizardAction("Next", false), wizard.PrimaryAction);
      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.NotNull(wizard.PassedCheck);

      await wizard.InvokePrimaryAsync();

      Assert.Equal(WizardStep.Server, wizard.Step);
      Assert.Equal(reason, wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Warning, wizard.ResultSeverity);
      Assert.Equal(FocusTarget.ServerName, wizard.FocusTarget);
      Assert.Single(fixture.Pipe.Sent);

      wizard.Name = "  Warehouse  ";

      Assert.Null(wizard.NameProblem);
      Assert.Equal(new WizardAction("Next", true), wizard.NextAction);
      await wizard.InvokePrimaryAsync();
      Assert.Equal(WizardStep.SignIn, wizard.Step);
   }



   /// <summary>The name a view writes back unchanged is not typing: the name goes on following the address.</summary>
   [Fact]
   public void Name_WrittenBackUnchanged_IsNotTyping()
   {
      using WizardFixture fixture = new();
      fixture.Wizard.Address = "https://qqq.example.test";
      fixture.Changes.Clear();

      fixture.Wizard.Name = "qqq.example.test";

      Assert.Empty(fixture.Changes);
      fixture.Wizard.Address = "https://bench.example.test";
      Assert.Equal("bench.example.test", fixture.Wizard.Name);
   }



   /// <summary>
   /// An address that cannot be a server's is not sent. The step says what is wrong with it, for every part of the
   /// address rule, points at the address, and the log names the kind of problem and never the text that was typed.
   /// </summary>
   [Theory]
   [InlineData("", ServerAddressProblem.Empty, "Enter the server's address, for example https://qqq.example.com.")]
   [InlineData("qqq.example.test", ServerAddressProblem.NotAnAddress, "This is not a complete address. Enter it with https:// in front, for example https://qqq.example.com.")]
   [InlineData("http://qqq.example.test", ServerAddressProblem.NotSecure, "The address must start with https://. Plain http:// is accepted only for a server on this computer itself.")]
   [InlineData("https://operator:CANARY@qqq.example.test", ServerAddressProblem.HasUserInformation, "The address must not contain a user name or a password. Remove them; you sign in at the next step.")]
   [InlineData("https://qqq.example.test/?token=CANARY", ServerAddressProblem.HasQueryOrFragment,
      "The address must not contain a question mark or a hash sign, or anything after one. Enter only the server's address, for example https://qqq.example.com.")]
   [InlineData("https://qqq.example.test/CANARY", ServerAddressProblem.HasPath, "Enter only the server's address, without a path after the host name, for example https://qqq.example.com.")]
   [InlineData("https://[fe80::1%25eth0]", ServerAddressProblem.HostNotNameable, "The host name in this address cannot be used. Enter the server's host name, or its IP address without a zone.")]
   public async Task Check_AddressThatCannotBeAServers_IsNotSent_SaysWhatIsWrong(string typed, ServerAddressProblem problem, string sentence)
   {
      using WizardFixture fixture = new();
      fixture.Wizard.Address = typed;
      Assert.False(ServerAddress.TryRead(typed, out _, out ServerAddressProblem found));
      Assert.Equal(problem, found);

      await fixture.Wizard.CheckAsync();

      Assert.Empty(fixture.Pipe.Sent);
      Assert.Equal(ServerStepState.Editing, fixture.Wizard.ServerState);
      Assert.Equal(sentence, fixture.Wizard.Result.Text);
      Assert.Equal([sentence], fixture.Announced);
      Assert.Equal(FeedbackSeverity.Warning, fixture.Wizard.ResultSeverity);
      Assert.Equal(FocusTarget.StepStart, fixture.Wizard.FocusTarget);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Contains("DEBUG The address typed in the wizard cannot be a server's address (" + problem + "); nothing was sent.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("CANARY", StringComparison.Ordinal) || entry.Contains("operator", StringComparison.Ordinal));
      Assert.DoesNotContain(fixture.Log.Values, value => value is string text && text.Contains("CANARY", StringComparison.Ordinal));
   }



   /// <summary>When the service itself does not accept the address, the step returns to editing and says how an address is written.</summary>
   [Fact]
   public async Task Check_InvalidRequest_ReturnsToEditing_WithTheExpectedForm()
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(WizardFixture.Refusal(ManagementErrorCode.InvalidRequest));

      Assert.Equal(ServerStepState.Editing, fixture.Wizard.ServerState);
      Assert.Equal("The print service did not accept this address. Enter the server's address as https:// and its host name, for example https://qqq.example.com, then choose Check.", fixture.Wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Warning, fixture.Wizard.ResultSeverity);
      Assert.Equal(FocusTarget.StepStart, fixture.Wizard.FocusTarget);
      Assert.Equal(WizardFixture.Address, fixture.Wizard.Address);
   }



   /// <summary>
   /// A request that never left this computer says that the service was not reached and that nothing was checked,
   /// and keeps what was typed. It does not say that the check failed, and it can be tried again.
   /// </summary>
   [Fact]
   public async Task Check_ServiceNotReached_SaysNothingWasChecked()
   {
      using WizardFixture fixture = new();
      fixture.Wizard.Address = WizardFixture.Address;
      fixture.Wizard.Name = "Warehouse";
      fixture.Pipe.Fail(new AgentPipeNotSentException(new IOException("Nothing is listening.")));

      await fixture.Wizard.CheckAsync();

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.Editing, wizard.ServerState);
      Assert.Equal("The request did not reach the print service on this computer, so nothing was checked. What you entered is kept. Make sure the QQQ Print Agent service is running, then choose Check. If this repeats, look under Logs for the reason.", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Error, wizard.ResultSeverity);
      Assert.Equal(FocusTarget.PrimaryAction, wizard.FocusTarget);
      Assert.Equal(WizardFixture.Address, wizard.Address);
      Assert.Equal("Warehouse", wizard.Name);
      Assert.Empty(wizard.Stages);
      Assert.Equal(new WizardAction("Check", true), wizard.PrimaryAction);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Contains("WARN The check of https://qqq.example.test brought no result (Disconnected, nothing was carried out).", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// A check that was sent and not answered says that its result is not known and that it can be repeated, because
   /// a check stores nothing. Repeating it sends the same check again and shows its result.
   /// </summary>
   [Fact]
   public async Task Check_Unanswered_CanBeRepeated()
   {
      using WizardFixture fixture = new();
      fixture.Wizard.Address = WizardFixture.Address;
      fixture.Pipe.Fail(new IOException("The pipe closed before the reply."));

      await fixture.Wizard.CheckAsync();

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.Editing, wizard.ServerState);
      Assert.Equal("No answer that could be used came back from the print service, so the result of the check is not known. A check stores nothing, so it is safe to repeat. Choose Check.", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Warning, wizard.ResultSeverity);
      Assert.Equal(new WizardAction("Check", true), wizard.PrimaryAction);
      Assert.Equal(FocusTarget.PrimaryAction, wizard.FocusTarget);
      Assert.Contains("WARN The check of https://qqq.example.test brought no result (Disconnected, the request was sent).", fixture.Log.Entries, StringComparer.Ordinal);

      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check()));
      await wizard.InvokePrimaryAsync();

      Assert.Equal(2, fixture.Pipe.Sent.Count);
      Assert.Equal(fixture.Sent(0), fixture.Sent(1));
      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
   }



   /// <summary>A reply that arrived and could not be read is an unanswered check too.</summary>
   [Fact]
   public async Task Check_ReplyThatCannotBeRead_IsUnanswered()
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(Replies.Text("{\"v\":2,\"ok\":true}"));

      Assert.Equal(ServerStepState.Editing, fixture.Wizard.ServerState);
      Assert.Equal(WizardMessages.Unanswered, fixture.Wizard.Result.Text);
   }



   /// <summary>
   /// A service of another version says which side to update and that the check could not be made, and keeps what
   /// was typed. It does not say that nothing was checked: that the service is of another version is read from the
   /// shape of its reply, and what such a service did with the request is not known.
   /// </summary>
   [Theory]
   [InlineData("Error: unknown command", "The print service on this computer is an older version than this companion and does not know this request, so the check could not be made. Finish updating QQQ Print Agent so that both are the same version, then choose Check.")]
   [InlineData("{\"v\":3,\"ok\":false,\"error\":{\"code\":\"unsupported_version\",\"message\":\"No.\"}}", "The print service on this computer is a newer version than this companion, so the check could not be made. Update the companion to the same version, then start it again.")]
   public async Task Check_ServiceOfAnotherVersion_SaysWhichSideToUpdate(string reply, string sentence)
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(Replies.Text(reply));

      Assert.Equal(ServerStepState.Editing, fixture.Wizard.ServerState);
      Assert.Equal(sentence, fixture.Wizard.Result.Text);
      Assert.DoesNotContain("nothing was checked", fixture.Wizard.Result.Text, StringComparison.OrdinalIgnoreCase);
      Assert.Equal(FeedbackSeverity.Error, fixture.Wizard.ResultSeverity);
      Assert.Equal(WizardFixture.Address, fixture.Wizard.Address);
   }



   /// <summary>
   /// While a check is running, a second one is refused and the refusal is said: by the Check button and by Enter
   /// alike. Nothing more is sent, the focus is not moved, and the first check's result is shown when it arrives.
   /// </summary>
   [Fact]
   public async Task SecondCheckWhileOneRuns_IsRefusedOutLoud()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      wizard.Address = WizardFixture.Address;
      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();

      Task first = wizard.CheckAsync();

      Assert.Equal(ServerStepState.Checking, wizard.ServerState);
      Assert.True(wizard.IsBusy);
      Assert.True(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Equal(new WizardAction("Check", false), wizard.CheckAction);
      Assert.Equal(new WizardAction("Check", false), wizard.PrimaryAction);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.Equal(["Checking the server. This can take up to two minutes."], fixture.Announced);
      fixture.Changes.Clear();

      await wizard.CheckAsync();
      await wizard.InvokePrimaryAsync();

      Assert.Equal(["Checking the server. This can take up to two minutes.", WizardMessages.CheckStillRunning, WizardMessages.CheckStillRunning], fixture.Announced);
      Assert.Equal("The server is still being checked, so this was not started. Wait for the result, or choose Cancel to close the wizard.", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Warning, wizard.ResultSeverity);
      Assert.DoesNotContain(nameof(ConnectionWizard.FocusTarget), fixture.Changes, StringComparer.Ordinal);
      Assert.Single(fixture.Pipe.Sent);
      Assert.Equal(ServerStepState.Checking, wizard.ServerState);
      Assert.Equal(2, fixture.Log.Entries.Count(entry => string.Equals(entry, "INFO A check of the server was asked for and not started: the wizard is still waiting for the service.", StringComparison.Ordinal)));
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.StartsWith("WARN", StringComparison.Ordinal) || entry.StartsWith("ERROR", StringComparison.Ordinal));

      held.SetResult(Replies.Success(WizardFixture.Check()));
      await first.WaitAsync(WizardFixture.Limit);

      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.False(wizard.IsBusy);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Single(fixture.Pipe.Sent);
   }



   /// <summary>
   /// A second check that was refused changes nothing about the first: the wizard still waits for it and holds its
   /// gate, and an edit of the address still ends that wait, so the first check's answer is not shown when it
   /// arrives.
   /// </summary>
   [Fact]
   public async Task SecondCheckRefused_TheFirstIsStillTheOneTheWizardWaitsFor()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      wizard.Address = WizardFixture.Address;
      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
      Task first = wizard.CheckAsync();

      await wizard.CheckAsync();

      Assert.True(wizard.IsBusy);
      Assert.True(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Equal(new WizardAction("Check", false), wizard.CheckAction);
      fixture.Announced.Clear();

      wizard.Address = "https://other.example.test";
      await first.WaitAsync(WizardFixture.Limit);
      held.SetResult(Replies.Success(WizardFixture.Check()));
      await Task.Yield();

      Assert.Equal(ServerStepState.Editing, wizard.ServerState);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.PassedCheck);
      Assert.Empty(fixture.Announced);
      Assert.False(wizard.IsBusy);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Single(fixture.Pipe.Sent);
   }



   /// <summary>
   /// Cancel during a check stops waiting and ends the wizard. Nothing but the check was ever sent, so nothing was
   /// stored. The answer that arrives later is not shown and not announced. It runs as it does in the window: on
   /// one thread, where the check learns that its wait ended only after Cancel has returned.
   /// </summary>
   [Fact]
   public void Cancel_DuringTheCheck_StopsWaiting_NothingStored()
   {
      using WizardFixture fixture = new();
      using PumpContext pump = new();
      ConnectionWizard wizard = fixture.Wizard;
      pump.Run(async () =>
      {
         wizard.Address = WizardFixture.Address;
         TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
         Task check = wizard.CheckAsync();
         fixture.Announced.Clear();

         wizard.Cancel();

         Assert.Equal(WizardEnd.Cancelled, wizard.End);
         await check.WaitAsync(WizardFixture.Limit);
         held.SetResult(Replies.Success(WizardFixture.Check()));
         await Task.Yield();
      });

      Assert.Equal(WizardEnd.Cancelled, wizard.End);
      Assert.Null(wizard.End!.ServerId);
      Assert.False(wizard.End.OpensComputerPage);
      Assert.False(wizard.IsBusy);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Empty(fixture.Announced);
      Assert.Equal([ManagementOperations.ServersCheck], fixture.Pipe.Sent.Select(request => request.Op), StringComparer.Ordinal);
      Assert.Equal(new WizardAction("Cancel", false), wizard.CancelAction);
      Assert.False(wizard.CheckAction.Enabled);
      Assert.False(wizard.NextAction.Enabled);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.PassedCheck);
      Assert.Contains("INFO The connection wizard was cancelled at the Server step.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Contains("DEBUG The check of https://qqq.example.test is no longer waited for; whatever it finds is not shown.", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>The same when the check learns that its wait ended inside the call to Cancel, as it does where no thread owns the wizard: it ends cancelled all the same, with the gate free and nothing announced.</summary>
   [Fact]
   public void Cancel_DuringTheCheck_EndsTheSame_WhenTheWaitEndsInsideTheCall()
   {
      using WizardFixture fixture = new();
      using NoContext none = new();
      ConnectionWizard wizard = fixture.Wizard;
      wizard.Address = WizardFixture.Address;
      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
      Task check = wizard.CheckAsync();
      fixture.Announced.Clear();

      wizard.Cancel();

      Assert.True(check.IsCompletedSuccessfully);
      Assert.Equal(WizardEnd.Cancelled, wizard.End);
      Assert.False(wizard.IsBusy);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      held.SetResult(Replies.Success(WizardFixture.Check()));
      Assert.Empty(fixture.Announced);
      Assert.Empty(wizard.Stages);
      Assert.Single(fixture.Pipe.Sent);
   }



   /// <summary>Once the wizard has ended, nothing it is asked for does anything or sends anything.</summary>
   [Fact]
   public async Task AfterTheWizardEnded_NothingIsStartedOrSent()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      wizard.Cancel();
      fixture.Changes.Clear();
      fixture.Announced.Clear();

      wizard.Address = "https://other.example.test";
      wizard.Name = "Other";
      await wizard.CheckAsync();
      await wizard.InvokePrimaryAsync();
      wizard.RequestException();
      await wizard.AcceptExceptionAsync();
      wizard.DeclineException();
      wizard.OpenExistingServer();
      wizard.GoBack();
      wizard.Cancel();
      wizard.InvokeCancel();

      Assert.Equal(WizardEnd.Cancelled, wizard.End);
      Assert.Equal(WizardFixture.Address, wizard.Address);
      Assert.Empty(fixture.Changes);
      Assert.Empty(fixture.Announced);
      Assert.Single(fixture.Pipe.Sent);
      Assert.False(wizard.CanRequestException);
      Assert.Equal(1, fixture.Log.Entries.Count(entry => entry.StartsWith("INFO The connection wizard was cancelled", StringComparison.Ordinal)));
   }



   /// <summary>
   /// Editing the address while a check runs stops the wait: the step is editing again at once, the wizard's gate
   /// comes free, and the answer for the address before is neither shown nor announced when it arrives.
   /// </summary>
   [Fact]
   public async Task AddressEditedDuringTheCheck_StopsWaiting_TheLateAnswerIsNotShown()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      wizard.Address = WizardFixture.Address;
      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
      Task check = wizard.CheckAsync();
      fixture.Announced.Clear();

      wizard.Address = "https://other.example.test";
      await check.WaitAsync(WizardFixture.Limit);

      Assert.Equal(ServerStepState.Editing, wizard.ServerState);
      Assert.False(wizard.IsBusy);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Equal("", wizard.Result.Text);
      Assert.Equal(new WizardAction("Check", true), wizard.CheckAction);
      Assert.Contains("DEBUG The check of https://qqq.example.test is no longer waited for; whatever it finds is not shown.", fixture.Log.Entries, StringComparer.Ordinal);

      held.SetResult(Replies.Success(WizardFixture.Check()));
      await Task.Yield();

      Assert.Equal(ServerStepState.Editing, wizard.ServerState);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.PassedCheck);
      Assert.Empty(fixture.Announced);
   }



   /// <summary>
   /// An answer that arrived just before the address was edited, and had not been shown yet, is not shown either:
   /// it is about the address that is no longer in the box.
   /// </summary>
   [Fact]
   public void AnswerThatArrivedJustBeforeAnEdit_IsNotShown()
   {
      using WizardFixture fixture = new();
      using PumpContext pump = new();
      ConnectionWizard wizard = fixture.Wizard;
      pump.Run(async () =>
      {
         wizard.Address = WizardFixture.Address;
         TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
         Task check = wizard.CheckAsync();
         fixture.Announced.Clear();

         held.SetResult(Replies.Success(WizardFixture.Check()));
         Assert.Equal(ServerStepState.Checking, wizard.ServerState);
         wizard.Address = "https://other.example.test";
         await check.WaitAsync(WizardFixture.Limit);
      });

      Assert.Equal(ServerStepState.Editing, wizard.ServerState);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.PassedCheck);
      Assert.Empty(fixture.Announced);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
   }



   /// <summary>Enter is the step's primary action: Check first, and Next once the check has passed, which shows the next step with the result of this one cleared and the focus at the new step's start.</summary>
   [Fact]
   public async Task Enter_IsCheckThenNext()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      wizard.Address = WizardFixture.Address;
      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check()));
      Assert.Equal("Check", wizard.PrimaryAction.Label);

      await wizard.InvokePrimaryAsync();

      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.Single(fixture.Pipe.Sent);
      Assert.Equal("Next", wizard.PrimaryAction.Label);
      fixture.Changes.Clear();

      await wizard.InvokePrimaryAsync();

      Assert.Equal(WizardStep.SignIn, wizard.Step);
      Assert.Equal("Step 2 of 5: Sign in", wizard.StepTitle);
      Assert.Equal("", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Informational, wizard.ResultSeverity);
      Assert.Equal(FocusTarget.StepStart, wizard.FocusTarget);
      Assert.Contains(nameof(ConnectionWizard.Step), fixture.Changes, StringComparer.Ordinal);
      Assert.Contains(nameof(ConnectionWizard.StepTitle), fixture.Changes, StringComparer.Ordinal);
      Assert.Contains(nameof(ConnectionWizard.FocusTarget), fixture.Changes, StringComparer.Ordinal);
      Assert.Single(fixture.Pipe.Sent);
      Assert.Contains("INFO The Server step is complete for https://qqq.example.test: every stage of the check passed.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.NotNull(wizard.PassedCheck);
   }



   /// <summary>Escape is Cancel: the wizard ends, and nothing was sent.</summary>
   [Fact]
   public void Escape_IsCancel()
   {
      using WizardFixture fixture = new();
      fixture.Wizard.Address = WizardFixture.Address;
      Assert.Equal(new WizardAction("Cancel", true), fixture.Wizard.CancelAction);

      fixture.Wizard.InvokeCancel();

      Assert.Equal(WizardEnd.Cancelled, fixture.Wizard.End);
      Assert.Contains(nameof(ConnectionWizard.End), fixture.Changes, StringComparer.Ordinal);
      Assert.Empty(fixture.Pipe.Sent);
      Assert.Empty(fixture.Announced);
   }



   /// <summary>
   /// One check says two things, each once: that it is checking, when it starts, and the result, when it is final.
   /// At the moment the result is announced everything that goes with it is already in place: the state, the rows,
   /// the buttons, the severity and the focus, and the wizard is no longer busy and no longer holds its gate.
   /// </summary>
   [Fact]
   public async Task Result_IsAnnouncedOnce_WhenFinal()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      wizard.Address = WizardFixture.Address;
      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();

      Task check = wizard.CheckAsync();

      WizardFixture.Moment progress = Assert.Single(fixture.Moments);
      Assert.Equal(new WizardFixture.Moment(WizardMessages.Checking, ServerStepState.Checking, 0, new WizardAction("Check", false), Busy: true, GateHeld: true, FeedbackSeverity.Informational, FocusTarget.StepStart), progress);

      held.SetResult(Replies.Failure(ManagementErrorCode.ServerIncompatible, Replies.Message, WizardFixture.Check(3)));
      await check.WaitAsync(WizardFixture.Limit);

      Assert.Equal(2, fixture.Moments.Count);
      Assert.Equal(new WizardFixture.Moment(WizardMessages.Failed("Printing extension"), ServerStepState.Failed, 5, new WizardAction("Check again", true), Busy: false, GateHeld: false, FeedbackSeverity.Error,
         FocusTarget.CheckStages), fixture.Moments[1]);
      Assert.Equal(fixture.Moments[1].Text, wizard.Result.Text);

      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check()));
      await wizard.CheckAsync();

      Assert.Equal(4, fixture.Moments.Count);
      Assert.Equal(WizardMessages.Checking, fixture.Moments[2].Text);
      Assert.Equal(0, fixture.Moments[2].Rows);
      Assert.Equal(new WizardFixture.Moment(wizard.Result.Text, ServerStepState.Passed, 5, new WizardAction("Next", true), Busy: false, GateHeld: false, FeedbackSeverity.Success, FocusTarget.PrimaryAction),
         fixture.Moments[3]);
   }



   /// <summary>The focus target is announced every time a result points somewhere, also when it points at the same place as the result before, so the view moves the focus each time.</summary>
   [Fact]
   public async Task FocusTarget_IsAnnouncedForEveryResult_EvenAtTheSamePlace()
   {
      using WizardFixture fixture = new();
      await fixture.CheckAsync(Replies.Failure(ManagementErrorCode.ServerIncompatible, Replies.Message, WizardFixture.Check(2)));
      Assert.Equal(FocusTarget.CheckStages, fixture.Wizard.FocusTarget);
      fixture.Changes.Clear();

      await fixture.CheckAsync(Replies.Failure(ManagementErrorCode.ServerIncompatible, Replies.Message, WizardFixture.Check(2)));

      Assert.Equal(FocusTarget.CheckStages, fixture.Wizard.FocusTarget);
      Assert.Equal(1, fixture.Changes.Count(change => string.Equals(change, nameof(ConnectionWizard.FocusTarget), StringComparison.Ordinal)));
   }



   /// <summary>An answer that arrives on another thread is shown and announced on the thread the wizard was called on.</summary>
   [Fact]
   public void AnswerFromAnotherThread_IsShownOnTheCallingThread()
   {
      using WizardFixture fixture = new();
      using PumpContext pump = new();
      int caller = 0;
      pump.Run(async () =>
      {
         caller = Environment.CurrentManagedThreadId;
         fixture.Wizard.Address = WizardFixture.Address;
         TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
         Task check = fixture.Wizard.CheckAsync();
         _ = Task.Run(() => held.SetResult(Replies.Success(WizardFixture.Check())));
         await check.WaitAsync(WizardFixture.Limit);
      });

      Assert.Equal(ServerStepState.Passed, fixture.Wizard.ServerState);
      Assert.Equal(2, fixture.Announced.Count);
      Assert.NotEmpty(fixture.Threads);
      Assert.All(fixture.Threads, thread => Assert.Equal(caller, thread));
   }



   /// <summary>
   /// The wizard does not start when the computer already holds eight servers, counting one that is being removed,
   /// when the stored data is not ready, or when no snapshot has been answered. It says why, and it starts with
   /// seven servers and data that is ready.
   /// </summary>
   [Fact]
   public void EightServers_OrStoredDataNotReady_TheWizardDoesNotStart()
   {
      Refused(WizardFixture.Snapshot(servers: 8), "This computer already has 8 servers, which is the most it can have. A server that is being removed counts until it is gone. Remove a server before adding another.");
      Refused(WizardFixture.Snapshot(servers: 8, removing: 1), WizardMessages.StartServerLimit);
      Refused(WizardFixture.Snapshot(servers: 9), WizardMessages.StartServerLimit);
      Refused(WizardFixture.Snapshot(status: StoredState.UpgradeIncomplete), "The upgrade from the earlier version has not finished, so a server cannot be added yet. The Computer page says where it stopped and what to do.");
      Refused(WizardFixture.Snapshot(status: StoredState.NewerBuild), "A newer version of QQQ Print Agent wrote this computer's stored data, so this version cannot add a server. The Computer page says which version to install.");
      Refused(WizardFixture.Snapshot(status: "somethingNew"), "This computer's stored data is not ready, so a server cannot be added. The Computer page says more.");
      Refused(WizardFixture.Snapshot(servers: 8, status: StoredState.UpgradeIncomplete), WizardMessages.StartUpgradeIncomplete);
      Refused(null, "The print service has not answered yet, so a server cannot be added now. Try again when the window shows the service's state.");

      Assert.Equal(8, ManagementProtocol.MaximumServers);
      foreach(ServiceSnapshot snapshot in new[] { WizardFixture.Snapshot(), WizardFixture.Snapshot(servers: 7), WizardFixture.Snapshot(servers: 7, removing: 7) })
      {
         FakePipe pipe = new();
         Assert.Null(ConnectionWizard.WhyAddServerCannotStart(snapshot));
         Assert.True(ConnectionWizard.TryStartAddServer(snapshot, new ManagementClient(pipe.SendAsync, new TemplateLog()), new OperationGate(), new TemplateLog(), out ConnectionWizard? wizard, out string? refusal));
         Assert.Null(refusal);
         Assert.Equal(WizardStep.Server, wizard.Step);
         Assert.Empty(pipe.Sent);
         wizard.Dispose();
      }

      static void Refused(ServiceSnapshot? snapshot, string reason)
      {
         FakePipe pipe = new();
         TemplateLog log = new();
         OperationGate gate = new();

         Assert.Equal(reason, ConnectionWizard.WhyAddServerCannotStart(snapshot));
         Assert.False(ConnectionWizard.TryStartAddServer(snapshot, new ManagementClient(pipe.SendAsync, log), gate, log, out ConnectionWizard? wizard, out string? refusal));

         Assert.Null(wizard);
         Assert.Equal(reason, refusal);
         Assert.Empty(pipe.Sent);
         Assert.False(gate.IsBusy(OperationGate.Wizard));
         Assert.Equal(["INFO The connection wizard was not opened to add a server. " + reason], log.Entries);
      }
   }



   /// <summary>Starting a wizard needs the client, the gate and the log.</summary>
   [Fact]
   public void Start_MissingArgument_IsRefused()
   {
      ManagementClient client = new(new FakePipe().SendAsync, new TemplateLog());

      Assert.Equal("client", Assert.Throws<ArgumentNullException>(() => ConnectionWizard.TryStartAddServer(null, null!, new OperationGate(), new TemplateLog(), out _, out _)).ParamName);
      Assert.Equal("gate", Assert.Throws<ArgumentNullException>(() => ConnectionWizard.TryStartAddServer(null, client, null!, new TemplateLog(), out _, out _)).ParamName);
      Assert.Equal("log", Assert.Throws<ArgumentNullException>(() => ConnectionWizard.TryStartAddServer(null, client, new OperationGate(), null!, out _, out _)).ParamName);
   }



   /// <summary>A check that passed for the service but offers no sign-in method this version knows is not a pass: the rows are shown, Next cannot be used, and the step says what to do.</summary>
   [Fact]
   public async Task Check_Passes_WithNoSignInMethodThisVersionKnows_IsNotAPass()
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check(methods: ["passkey"])));

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.Failed, wizard.ServerState);
      Assert.Equal(5, wizard.Stages.Count);
      Assert.All(wizard.Stages, row => Assert.Equal(CheckStageOutcome.Passed, row.Outcome));
      Assert.Null(wizard.PassedCheck);
      Assert.Null(wizard.SignInMethods);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.Equal("The server passed the print service's checks, but it offers no sign-in method this version of the companion knows, so you could not sign in. Nothing was stored. Update QQQ Print Agent, or ask the server's administrator which sign-in methods it offers.",
         wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Error, wizard.ResultSeverity);
      Assert.Equal(FocusTarget.CheckStages, wizard.FocusTarget);
   }



   /// <summary>
   /// An answer that says "passed" and cannot be stood behind is not a pass: a stage that is missing, not reported
   /// as passed or reported twice (even when both entries say passed), or a sign-in provider the contract does not
   /// take for an origin, an IPv6 literal with a zone among them. No rows are shown, nothing is kept for adding
   /// the server, and the text that was no origin reaches neither the screen nor the log.
   /// </summary>
   [Theory]
   [InlineData("", "NOT-AN-ORIGIN")]
   [InlineData("", "mailto:someone@example.test")]
   [InlineData("", "https://[fe80::1%25eth0]")]
   [InlineData("", "https://[fe80::1%eth0]:8443/")]
   [InlineData("missing", null)]
   [InlineData("unknownStageFailed", null)]
   [InlineData("twice", null)]
   [InlineData("twiceTheSecondFailed", null)]
   [InlineData("unknownStageTwice", null)]
   public async Task Check_AnswerThatCannotBeUsed_IsNotAPass(string stages, string? provider)
   {
      using WizardFixture fixture = new();
      ServerCheckResult check = WizardFixture.Check(provider: provider);
      check = stages switch
      {
         "missing" => check with { Stages = [.. check.Stages.Take(4)] },
         "unknownStageFailed" => check with { Stages = [.. check.Stages, new CompatibilityStageResult("licence", CompatibilityStageResult.Failed, ["a stage a later version checks"])] },
         "twice" => check with { Stages = [.. check.Stages, new CompatibilityStageResult(CompatibilityStageResult.Security, CompatibilityStageResult.Passed, [])] },
         "twiceTheSecondFailed" => check with { Stages = [.. check.Stages, new CompatibilityStageResult(CompatibilityStageResult.Security, CompatibilityStageResult.Failed, ["the second entry"])] },
         "unknownStageTwice" => check with
         {
            Stages = [.. check.Stages, new CompatibilityStageResult("licence", CompatibilityStageResult.Passed, []), new CompatibilityStageResult("licence", CompatibilityStageResult.Passed, [])],
         },
         _ => check,
      };

      await fixture.CheckAsync(Replies.Success(check));

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.Failed, wizard.ServerState);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.PassedCheck);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.Equal("The print service answered the check, but its answer could not be used. Nothing was stored. Choose Check again. If this repeats, look under Logs.", wizard.Result.Text);
      Assert.Equal(FeedbackSeverity.Error, wizard.ResultSeverity);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("NOT-AN-ORIGIN", StringComparison.Ordinal) || entry.Contains("someone", StringComparison.Ordinal) || entry.Contains("fe80", StringComparison.Ordinal));
      Assert.Contains("WARN The service answered the check of https://qqq.example.test as passed, but the answer cannot be used: a stage is not reported as passed or is reported twice, or the sign-in provider it names is not an origin.",
         fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>A stage this version does not know, reported once as passed, does not keep a check from passing: the contract has a client ignore it.</summary>
   [Fact]
   public async Task Check_Passes_WithAStageThisVersionDoesNotKnow()
   {
      using WizardFixture fixture = new();
      ServerCheckResult check = WizardFixture.Check();

      await fixture.CheckAsync(Replies.Success(check with { Stages = [.. check.Stages, new CompatibilityStageResult("licence", CompatibilityStageResult.Passed, [])] }));

      Assert.Equal(ServerStepState.Passed, fixture.Wizard.ServerState);
      Assert.Equal(5, fixture.Wizard.Stages.Count);
   }



   /// <summary>
   /// A stage the service reported twice in a failed check is shown with the worse of the two outcomes and the
   /// reasons that came with it, wherever the worse entry stands: the rows never read "Passed" for a stage that
   /// failed, under a sentence that says the check did not pass.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task Check_AStageReportedTwice_ShowsTheWorseOutcome(bool failedEntryFirst)
   {
      using WizardFixture fixture = new();
      ServerCheckResult passed = WizardFixture.Check(methods: []);
      CompatibilityStageResult failed = new(CompatibilityStageResult.Security, CompatibilityStageResult.Failed, ["the entry that failed"]);

      await fixture.CheckAsync(Replies.Failure(ManagementErrorCode.ServerIncompatible, Replies.Message, passed with { Stages = failedEntryFirst ? [failed, .. passed.Stages] : [.. passed.Stages, failed] }));

      ConnectionWizard wizard = fixture.Wizard;
      Assert.Equal(ServerStepState.Failed, wizard.ServerState);
      Assert.Equal([CheckStageOutcome.Passed, CheckStageOutcome.Failed, CheckStageOutcome.Passed, CheckStageOutcome.Passed, CheckStageOutcome.Passed], wizard.Stages.Select(row => row.Outcome));
      Assert.Equal(["the entry that failed"], wizard.Stages[1].Problems);
      Assert.Equal("Trusted secure connection: Failed. the entry that failed", wizard.Stages[1].AccessibleName);
      Assert.Equal("The server did not pass the check: \"Trusted secure connection\" failed. The reasons are in the list of checks. Nothing was stored. Correct the problem, then choose Check again.", wizard.Result.Text);
   }



   /// <summary>Of several entries for one stage the worst is shown: a failure before an outcome that is not known, that before a stage that was not reached, and that before a pass. The reasons of every entry are shown, in the order they came.</summary>
   [Theory]
   [InlineData("passed,notReached", CheckStageOutcome.NotReached)]
   [InlineData("notReached,passed", CheckStageOutcome.NotReached)]
   [InlineData("notReached,postponed,passed", CheckStageOutcome.Unknown)]
   [InlineData("postponed,failed,notReached", CheckStageOutcome.Failed)]
   [InlineData("passed,passed", CheckStageOutcome.Passed)]
   public void StageRows_OfSeveralEntriesForOneStage_ShowTheWorst(string outcomes, CheckStageOutcome shown)
   {
      string[] reported = outcomes.Split(',');
      ServerCheckResult check = new([.. reported.Select((outcome, index) => new CompatibilityStageResult(CompatibilityStageResult.Server, outcome, ["reason " + index.ToString(CultureInfo.InvariantCulture)]))], []);

      CheckStageRow row = CheckStageRow.From(check)[2];

      Assert.Equal(shown, row.Outcome);
      Assert.Equal(reported.Select((_, index) => "reason " + index.ToString(CultureInfo.InvariantCulture)), row.Problems, StringComparer.Ordinal);
      Assert.True(CheckStageRow.ReportsAStageTwice(check));
      Assert.False(CheckStageRow.ReportsAStageTwice(WizardFixture.Check()));
   }



   /// <summary>
   /// A row shows at most five reasons, however many the service sent, and then says how many more there are: each
   /// reason is cut at the contract's limit, and without a limit on their number a row, and the one sentence a
   /// screen reader reads for it, could be as long as the service made it. A reason that shows nothing is not
   /// counted.
   /// </summary>
   [Fact]
   public async Task StageRow_ShowsAtMostFiveReasons_AndSaysHowManyMore()
   {
      using WizardFixture fixture = new();
      string[] reasons = [.. Enumerable.Range(1, 3000).Select(number => "reason " + number.ToString(CultureInfo.InvariantCulture))];

      await fixture.CheckAsync(Replies.Failure(ManagementErrorCode.ServerIncompatible, Replies.Message, WizardFixture.Check(0, reasons)));

      CheckStageRow row = fixture.Wizard.Stages[0];
      Assert.Equal(5, CheckStageRow.MaximumProblems);
      Assert.Equal(["reason 1", "reason 2", "reason 3", "reason 4", "reason 5", "And 2995 more reasons, which are not shown."], row.Problems);
      Assert.Equal("Reach the address: Failed. reason 1; reason 2; reason 3; reason 4; reason 5; And 2995 more reasons, which are not shown.", row.AccessibleName);

      CheckStageRow six = CheckStageRow.From(WizardFixture.Check(0, ["one", " ", "two", "three", "\u200B", "four", "five", "six"]))[0];
      Assert.Equal(["one", "two", "three", "four", "five", "And 1 more reason, which is not shown."], six.Problems);
      CheckStageRow five = CheckStageRow.From(WizardFixture.Check(0, ["one", " ", "two", "three", "four", "five"]))[0];
      Assert.Equal(["one", "two", "three", "four", "five"], five.Problems);
      Assert.Equal("Reach the address: Failed. one; two; three; four; five", five.AccessibleName);
   }



   /// <summary>Enter after a check that did not pass checks again: it never goes on to signing in, and it never runs under the exception.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Enter_AfterACheckThatDidNotPass_ChecksAgain(bool certificateRefused)
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      ManagementReplyParse reply = certificateRefused ? WizardFixture.CertificateRefused(WizardFixture.Provider) : Replies.Failure(ManagementErrorCode.ServerIncompatible, Replies.Message, WizardFixture.Check(2));
      await fixture.CheckAsync(reply);
      Assert.Equal(certificateRefused ? ServerStepState.CertificateRefused : ServerStepState.Failed, wizard.ServerState);
      Assert.Equal(new WizardAction("Check again", true), wizard.PrimaryAction);
      fixture.Pipe.Answer(reply);

      await wizard.InvokePrimaryAsync();

      Assert.Equal(WizardStep.Server, wizard.Step);
      Assert.Equal("Step 1 of 5: Server", wizard.StepTitle);
      Assert.Equal(2, fixture.Pipe.Sent.Count);
      Assert.Equal(new ServersCheckArgs(WizardFixture.Address, AcceptCertificateException: false), fixture.Sent(1));
      Assert.Equal(certificateRefused ? ServerStepState.CertificateRefused : ServerStepState.Failed, wizard.ServerState);
      Assert.Null(wizard.PassedCheck);
      Assert.False(wizard.ExceptionAccepted);
      Assert.Equal(2, fixture.Announced.Count(said => string.Equals(said, WizardMessages.Checking, StringComparison.Ordinal)));
   }



   /// <summary>
   /// A check that passes while the typed name breaks the name rule cannot be gone on from, so it does not say
   /// "Choose Next" and does not point the focus at a button that cannot be used: the sentence ends with what is
   /// wrong with the name, and the focus belongs on the name. Correcting the name is all it takes.
   /// </summary>
   [Fact]
   public async Task Check_PassesWhileTheNameBreaksTheRule_SaysTheNameProblem_AndPointsAtTheName()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      wizard.Address = WizardFixture.Address;
      wizard.Name = "Dock [2]";

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));

      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.NotNull(wizard.PassedCheck);
      Assert.Equal(new WizardAction("Next", false), wizard.PrimaryAction);
      Assert.Equal("The server passed all five checks. It offers sign-in with a user name and password, and sign-in through the browser. Nothing is stored yet."
         + " Before you can go on, correct the server's name. The name must not contain square brackets. Remove them.", wizard.Result.Text);
      Assert.DoesNotContain("Choose Next", wizard.Result.Text, StringComparison.Ordinal);
      Assert.Equal(FocusTarget.ServerName, wizard.FocusTarget);
      Assert.Equal(FeedbackSeverity.Warning, wizard.ResultSeverity);
      Assert.Equal(new WizardFixture.Moment(wizard.Result.Text, ServerStepState.Passed, 5, new WizardAction("Next", false), Busy: false, GateHeld: false, FeedbackSeverity.Warning, FocusTarget.ServerName), fixture.Moments[^1]);

      wizard.Name = "Dock 2";

      Assert.Equal(new WizardAction("Next", true), wizard.PrimaryAction);
      await wizard.InvokePrimaryAsync();
      Assert.Equal(WizardStep.SignIn, wizard.Step);
   }



   /// <summary>The same under the certificate exception: the sentence still says what the exception covers and how to drop it, and ends with what is wrong with the name instead of pointing at Next.</summary>
   [Fact]
   public async Task Check_PassesUnderTheExceptionWhileTheNameBreaksTheRule_SaysTheNameProblem()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      wizard.Address = WizardFixture.Address;
      wizard.Name = "   ";
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      wizard.RequestException();
      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check(provider: WizardFixture.Provider)));

      await wizard.AcceptExceptionAsync();

      Assert.Equal(ServerStepState.ExceptionReview, wizard.ServerState);
      Assert.True(wizard.ExceptionAccepted);
      Assert.Equal(new WizardAction("Next", false), wizard.PrimaryAction);
      Assert.Equal("The server passed all five checks with the development-only certificate exception. Before you go on, look at what the exception covers."
         + " It accepts a certificate Windows does not trust from two addresses: https://qqq.example.test, the server, and https://sign-in.example.test, the sign-in provider the server names."
         + " It offers sign-in with a user name and password, and sign-in through the browser. Nothing is stored yet: the exception is stored with the server when it is added."
         + " Choose Drop the exception to remove this result and check the server without it. To go on with the exception, correct the server's name first. Enter a name for this server.", wizard.Result.Text);
      Assert.DoesNotContain("Choose Next", wizard.Result.Text, StringComparison.Ordinal);
      Assert.Equal(FocusTarget.ServerName, wizard.FocusTarget);
      Assert.Equal(FeedbackSeverity.Warning, wizard.ResultSeverity);
   }



   /// <summary>
   /// Once the run has ended or the wizard was disposed, handing over to another step does nothing: the step stays,
   /// nothing is announced as changed, and what was said is not cleared. A step that hands over after waiting for
   /// something therefore cannot move a wizard that is gone.
   /// </summary>
   [Fact]
   public async Task GoTo_AfterTheRunEndedOrTheWizardWasDisposed_DoesNothing()
   {
      using WizardFixture ended = new();
      await ended.CheckAsync(Replies.Success(WizardFixture.Check()));
      string said = ended.Wizard.Result.Text;
      ended.Wizard.Cancel();
      ended.Changes.Clear();

      ended.Wizard.GoTo(WizardStep.Pairing);

      Assert.Equal(WizardStep.Server, ended.Wizard.Step);
      Assert.Equal("Step 1 of 5: Server", ended.Wizard.StepTitle);
      Assert.Empty(ended.Changes);
      Assert.Equal(said, ended.Wizard.Result.Text);
      Assert.Equal(FocusTarget.PrimaryAction, ended.Wizard.FocusTarget);

      WizardFixture disposed = new();
      await disposed.CheckAsync(Replies.Success(WizardFixture.Check()));
      List<string?> resultChanges = [];
      disposed.Wizard.Result.PropertyChanged += (_, change) => resultChanges.Add(change.PropertyName);
      disposed.Wizard.Dispose();
      disposed.Changes.Clear();

      disposed.Wizard.GoTo(WizardStep.Pairing);

      Assert.Equal(WizardStep.Server, disposed.Wizard.Step);
      Assert.Empty(disposed.Changes);
      Assert.Empty(resultChanges);
      Assert.Equal(said, disposed.Wizard.Result.Text);
   }



   /// <summary>
   /// A check is sent for the address that is in the box, and for no other. Entering the wizard's gate tells
   /// whoever listens to the gate; a listener that edits the address at that moment ends the check that was being
   /// started, before anything is sent. Nothing is shown about the address that was in the box before, the
   /// exception that had been accepted for it is forgotten and is not used for the new address, and the gate is
   /// free again. The next check is for the address in the box, without the exception.
   /// </summary>
   [Fact]
   public async Task AddressEditedWhileTheGateIsBeingEntered_TheCheckIsGivenUp_NothingIsAboutAnotherAddress()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      await fixture.CheckAsync(WizardFixture.CertificateRefused());
      wizard.RequestException();
      fixture.Pipe.Answer(Replies.Success(WizardFixture.Check(provider: WizardFixture.Provider)));
      await wizard.AcceptExceptionAsync();
      Assert.True(wizard.ExceptionAccepted);
      bool edited = false;
      fixture.Gate.Changed += (_, change) =>
      {
         if(!edited && fixture.Gate.IsBusy(change.Key))
         {
            edited = true;
            wizard.Address = "https://other.example.test";
         }
      };
      fixture.Announced.Clear();

      await wizard.CheckAsync();

      Assert.True(edited);
      Assert.Equal(2, fixture.Pipe.Sent.Count);
      Assert.Equal("https://other.example.test", wizard.Address);
      Assert.Equal(ServerStepState.Editing, wizard.ServerState);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.PassedCheck);
      Assert.Null(wizard.CheckedOrigin);
      Assert.False(wizard.ExceptionAccepted);
      Assert.Empty(wizard.ExceptionOrigins);
      Assert.Empty(fixture.Announced);
      Assert.Equal("", wizard.Result.Text);
      Assert.False(wizard.IsBusy);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Equal(new WizardAction("Check", true), wizard.CheckAction);
      Assert.Contains("DEBUG A check of the server was asked for and given up before anything was sent: the step changed while the wizard's gate was being entered.", fixture.Log.Entries, StringComparer.Ordinal);

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));

      Assert.Equal(new ServersCheckArgs("https://other.example.test", AcceptCertificateException: false), fixture.Sent(2));
      Assert.Equal(new Uri("https://other.example.test/"), wizard.CheckedOrigin);
      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.False(wizard.ExceptionAccepted);
   }



   /// <summary>A listener of the gate that disposes the wizard while a check is being started ends that check too: nothing is sent, nothing is said, and the gate comes free although the wizard was gone before it held the gate.</summary>
   [Fact]
   public async Task WizardDisposedWhileTheGateIsBeingEntered_NothingIsSent_AndTheGateComesFree()
   {
      WizardFixture fixture = new();
      fixture.Wizard.Address = WizardFixture.Address;
      fixture.Gate.Changed += (_, change) =>
      {
         if(change.Busy)
         {
            fixture.Wizard.Dispose();
         }
      };

      await fixture.Wizard.CheckAsync();

      Assert.Empty(fixture.Pipe.Sent);
      Assert.Empty(fixture.Announced);
      Assert.False(fixture.Wizard.IsBusy);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
   }



   /// <summary>
   /// Disposing the wizard during a check frees the wizard's gate there and then, not when the abandoned check has
   /// ended. In a window the abandoned check learns that its wait ended only later, and a wizard opened in between
   /// must not be refused by one that is gone. The abandoned check's own end then frees nothing that is not its
   /// own: the second wizard still holds the gate for its check.
   /// </summary>
   [Fact]
   public void Dispose_DuringTheCheck_FreesTheGateItself_TheNextWizardIsNotRefused()
   {
      WizardFixture fixture = new();
      using PumpContext pump = new();
      pump.Run(async () =>
      {
         fixture.Wizard.Address = WizardFixture.Address;
         fixture.Pipe.Hold();
         Task abandoned = fixture.Wizard.CheckAsync();
         Assert.True(fixture.Gate.IsBusy(OperationGate.Wizard));

         fixture.Wizard.Dispose();

         Assert.False(abandoned.IsCompleted);
         Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
         Assert.False(fixture.Wizard.IsBusy);
         Assert.True(ConnectionWizard.TryStartAddServer(WizardFixture.Snapshot(), fixture.Client, fixture.Gate, fixture.Log, out ConnectionWizard? second, out _, TestData.Time));
         using ConnectionWizard next = second;
         next.Address = "https://second.example.test";
         TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
         Task check = next.CheckAsync();
         Assert.Equal(ServerStepState.Checking, next.ServerState);
         Assert.Equal(2, fixture.Pipe.Sent.Count);

         await abandoned.WaitAsync(WizardFixture.Limit);

         Assert.True(fixture.Gate.IsBusy(OperationGate.Wizard));
         Assert.True(next.IsBusy);
         held.SetResult(Replies.Success(WizardFixture.Check()));
         await check.WaitAsync(WizardFixture.Limit);
         Assert.Equal(ServerStepState.Passed, next.ServerState);
         Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      });
   }



   /// <summary>Ending the run during a check frees the wizard's gate there and then as well, and the wizard is no longer busy, before the abandoned check has learned that its wait ended.</summary>
   [Fact]
   public void Cancel_DuringTheCheck_FreesTheGateItself()
   {
      using WizardFixture fixture = new();
      using PumpContext pump = new();
      pump.Run(async () =>
      {
         fixture.Wizard.Address = WizardFixture.Address;
         fixture.Pipe.Hold();
         Task abandoned = fixture.Wizard.CheckAsync();

         fixture.Wizard.Cancel();

         Assert.False(abandoned.IsCompleted);
         Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
         Assert.False(fixture.Wizard.IsBusy);
         Assert.Equal(WizardEnd.Cancelled, fixture.Wizard.End);
         await abandoned.WaitAsync(WizardFixture.Limit);
      });

      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
   }



   /// <summary>
   /// A view that disposes the wizard when it hears that the stored data is not ready does not make the check fail:
   /// a wizard that was disposed is not ended as well, so the check ends quietly, with the gate free.
   /// </summary>
   [Fact]
   public async Task DisposedWhenStoredDataNotReadyIsSaid_TheRunIsNotEndedAsWell()
   {
      WizardFixture fixture = new();
      fixture.Wizard.Address = WizardFixture.Address;
      fixture.Wizard.Result.Raised += (_, said) =>
      {
         if(string.Equals(said.Text, WizardMessages.StoredDataNotReady, StringComparison.Ordinal))
         {
            fixture.Wizard.Dispose();
         }
      };

      await fixture.CheckAsync(WizardFixture.Refusal(ManagementErrorCode.StateNewer));

      Assert.Equal([WizardMessages.Checking, WizardMessages.StoredDataNotReady], fixture.Announced);
      Assert.Null(fixture.Wizard.End);
      Assert.False(fixture.Wizard.CancelAction.Enabled);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
   }



   /// <summary>
   /// A view that disposes the wizard in the middle of a result being put in place is told nothing after that:
   /// neither that a property changed nor that one is about to change, although the result goes on being put in
   /// place, and the result is not announced.
   /// </summary>
   [Fact]
   public async Task DisposedWhileAResultIsBeingPutInPlace_NothingIsAnnouncedAfterwards_NotEvenAsAboutToChange()
   {
      WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      wizard.Address = WizardFixture.Address;
      bool disposed = false;
      List<string> afterwards = [];
      wizard.PropertyChanging += (_, change) =>
      {
         if(disposed)
         {
            afterwards.Add("about to change: " + change.PropertyName);
         }
      };
      wizard.PropertyChanged += (_, change) =>
      {
         if(disposed)
         {
            afterwards.Add("changed: " + change.PropertyName);
         }
         if(string.Equals(change.PropertyName, nameof(ConnectionWizard.Stages), StringComparison.Ordinal) && wizard.Stages.Count == 5)
         {
            disposed = true;
            wizard.Dispose();
         }
      };

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));

      Assert.True(disposed);
      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.Empty(afterwards);
      Assert.Equal([WizardMessages.Checking], fixture.Announced);
   }



   /// <summary>When the run ends, every button is told that it may have changed, after the end is set: Cancel, Check, Next and Back can no longer be used.</summary>
   [Fact]
   public async Task WhenTheRunEnds_TheButtonsAreToldThatTheyChanged()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));
      List<(string? Property, bool Ended, bool Cancel, bool Next, bool Check)> seen = [];
      wizard.PropertyChanged += (_, change) => seen.Add((change.PropertyName, wizard.End is not null, wizard.CancelAction.Enabled, wizard.NextAction.Enabled, wizard.CheckAction.Enabled));
      fixture.Changes.Clear();

      wizard.Cancel();

      Assert.Equal(
      [
         nameof(ConnectionWizard.End), nameof(ConnectionWizard.IsBusy), nameof(ConnectionWizard.PrimaryAction), nameof(ConnectionWizard.CheckAction), nameof(ConnectionWizard.NextAction),
         nameof(ConnectionWizard.CancelAction), nameof(ConnectionWizard.CanGoBack), nameof(ConnectionWizard.CanRequestException),
      ], fixture.Changes);
      Assert.All(seen, moment => Assert.Equal((true, false, false, false), (moment.Ended, moment.Cancel, moment.Next, moment.Check)));
   }



   /// <summary>
   /// Something the client does not turn into a result is a programming error and is left to propagate, but it does
   /// not leave the step stuck: it is editing again, not busy, and the gate is free.
   /// </summary>
   [Fact]
   public async Task Check_UnexpectedFailure_Propagates_AndLeavesTheStepUsable()
   {
      using WizardFixture fixture = new();
      fixture.Wizard.Address = WizardFixture.Address;
      fixture.Pipe.Fail(new AgentPipeNotSentException(new FormatException("A wrapped bug.")));

      await Assert.ThrowsAsync<AgentPipeNotSentException>(fixture.Wizard.CheckAsync);

      Assert.Equal(ServerStepState.Editing, fixture.Wizard.ServerState);
      Assert.False(fixture.Wizard.IsBusy);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Equal(new WizardAction("Check", true), fixture.Wizard.CheckAction);
      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));
      Assert.Equal(ServerStepState.Passed, fixture.Wizard.ServerState);
   }



   /// <summary>
   /// Disposing only cancels: a check that is running ends itself and frees the gate, and afterwards nothing is
   /// announced, neither a result nor a property change, and nothing can be started. Disposing twice is harmless.
   /// </summary>
   [Fact]
   public async Task Dispose_OnlyCancels_NothingIsAnnouncedAfterwards()
   {
      WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      wizard.Address = WizardFixture.Address;
      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
      Task check = wizard.CheckAsync();
      fixture.Changes.Clear();
      fixture.Announced.Clear();
      List<string?> changing = [];
      wizard.PropertyChanging += (_, change) => changing.Add(change.PropertyName);

      wizard.Dispose();
      wizard.Dispose();
      await check.WaitAsync(WizardFixture.Limit);
      held.SetResult(Replies.Success(WizardFixture.Check()));
      await Task.Yield();
      wizard.Address = "https://other.example.test";
      wizard.Name = "Other";
      await wizard.CheckAsync();
      await wizard.InvokePrimaryAsync();
      wizard.InvokeCancel();
      wizard.GoBack();

      Assert.Empty(fixture.Changes);
      Assert.Empty(changing);
      Assert.Empty(fixture.Announced);
      Assert.Null(wizard.End);
      Assert.False(wizard.IsBusy);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      Assert.Single(fixture.Pipe.Sent);
      Assert.False(wizard.CancelAction.Enabled);
   }



   /// <summary>A view that disposes the wizard while a result is being shown is not told the result afterwards.</summary>
   [Fact]
   public async Task DisposedWhileAResultIsBeingShown_TheResultIsNotAnnounced()
   {
      WizardFixture fixture = new();
      fixture.Wizard.Address = WizardFixture.Address;
      fixture.Wizard.PropertyChanged += (_, change) =>
      {
         if(string.Equals(change.PropertyName, nameof(ConnectionWizard.ServerState), StringComparison.Ordinal) && fixture.Wizard.ServerState == ServerStepState.Passed)
         {
            fixture.Wizard.Dispose();
         }
      };

      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));

      Assert.Equal([WizardMessages.Checking], fixture.Announced);
      Assert.Equal(WizardMessages.Checking, fixture.Wizard.Result.Text);
   }



   /// <summary>
   /// Back is offered only where the step it leaves and the one it returns to stored nothing in between: from signing
   /// in back to the server, and from the summary back to the printers. The Server step comes back as it was left,
   /// with its check still on screen, and says its result again with the focus on Next: changing step cleared what
   /// was said.
   /// </summary>
   [Fact]
   public async Task Back_IsOfferedOnlyFromSignInAndDone()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      Assert.False(wizard.CanGoBack);
      wizard.GoBack();
      Assert.Equal(WizardStep.Server, wizard.Step);
      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));
      await wizard.InvokePrimaryAsync();
      Assert.Equal(WizardStep.SignIn, wizard.Step);
      Assert.True(wizard.CanGoBack);

      wizard.GoBack();

      Assert.Equal(WizardStep.Server, wizard.Step);
      Assert.Equal("Step 1 of 5: Server", wizard.StepTitle);
      Assert.Equal(ServerStepState.Passed, wizard.ServerState);
      Assert.Equal(5, wizard.Stages.Count);
      Assert.NotNull(wizard.PassedCheck);
      Assert.Equal(new WizardAction("Next", true), wizard.PrimaryAction);
      Assert.Equal(FocusTarget.PrimaryAction, wizard.FocusTarget);
      Assert.Equal("The server passed all five checks. It offers sign-in with a user name and password, and sign-in through the browser. Nothing is stored yet. Choose Next to sign in.", wizard.Result.Text);
      Assert.Equal(wizard.Result.Text, fixture.Announced[^1]);
      Assert.Equal(FeedbackSeverity.Success, wizard.ResultSeverity);
      Assert.False(wizard.CanGoBack);

      wizard.GoTo(WizardStep.Pairing);
      Assert.Equal("Step 3 of 5: Pair this computer", wizard.StepTitle);
      Assert.False(wizard.CanGoBack);
      wizard.GoBack();
      Assert.Equal(WizardStep.Pairing, wizard.Step);
      wizard.GoTo(WizardStep.Printers);
      Assert.Equal("Step 4 of 5: Printers", wizard.StepTitle);
      Assert.False(wizard.CanGoBack);
      wizard.GoTo(WizardStep.Done);
      Assert.Equal("Step 5 of 5: Done", wizard.StepTitle);
      Assert.True(wizard.CanGoBack);
      fixture.Announced.Clear();
      wizard.GoBack();
      Assert.Equal(WizardStep.Printers, wizard.Step);
      Assert.Empty(fixture.Announced);
      Assert.Equal(FocusTarget.StepStart, wizard.FocusTarget);
      Assert.Single(fixture.Pipe.Sent);
   }



   /// <summary>
   /// A step that is not built yet offers no primary action and nothing of the Server step: Enter does nothing, the
   /// Server step's buttons cannot be used, its boxes cannot be changed, and a check cannot be started from it.
   /// Cancel still ends the wizard.
   /// </summary>
   [Fact]
   public async Task StepThatIsNotBuiltYet_OffersNoPrimaryAction()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));
      await wizard.InvokePrimaryAsync();
      fixture.Announced.Clear();
      fixture.Changes.Clear();

      Assert.Equal(new WizardAction("Next", false), wizard.PrimaryAction);
      Assert.Equal(new WizardAction("Next", false), wizard.NextAction);
      Assert.False(wizard.CheckAction.Enabled);
      await wizard.InvokePrimaryAsync();
      await wizard.CheckAsync();
      wizard.Address = "https://other.example.test";
      wizard.Name = "Other";

      Assert.Equal(WizardStep.SignIn, wizard.Step);
      Assert.Equal(WizardFixture.Address, wizard.Address);
      Assert.Equal("qqq.example.test", wizard.Name);
      Assert.NotNull(wizard.PassedCheck);
      Assert.Empty(fixture.Announced);
      Assert.Empty(fixture.Changes);
      Assert.Single(fixture.Pipe.Sent);

      wizard.InvokeCancel();

      Assert.Equal(WizardEnd.Cancelled, wizard.End);
      Assert.Contains("INFO The connection wizard was cancelled at the Sign in step.", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>Back cannot be used while the wizard waits for the service.</summary>
   [Fact]
   public async Task Back_IsNotOfferedWhileBusy()
   {
      using WizardFixture fixture = new();
      ConnectionWizard wizard = fixture.Wizard;
      await fixture.CheckAsync(Replies.Success(WizardFixture.Check()));
      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
      Task check = wizard.CheckAsync();
      wizard.GoTo(WizardStep.SignIn);

      Assert.True(wizard.IsBusy);
      Assert.False(wizard.CanGoBack);
      wizard.GoBack();
      Assert.Equal(WizardStep.SignIn, wizard.Step);

      held.SetResult(Replies.Success(WizardFixture.Check()));
      await check.WaitAsync(WizardFixture.Limit);
      Assert.True(wizard.CanGoBack);
   }



   /// <summary>The service's own message is shown as the contract gave it to the client, which is already printable and bounded; the wizard does not change it again.</summary>
   [Fact]
   public async Task ServiceMessage_IsShownAsTheContractHoldsIt()
   {
      using WizardFixture fixture = new();
      string raw = "first\r\nline \u202Ereversed " + new string('x', 600);
      string held = ManagementText.Bounded(raw, ManagementProtocol.MaximumMessageCharacters);

      await fixture.CheckAsync(WizardFixture.Refusal(ManagementErrorCode.InternalError, raw));

      Assert.Equal("The print service could not carry out the check. It said: " + held + ". Nothing was stored. Look under Logs for more, then choose Check to try again.", fixture.Wizard.Result.Text);
      Assert.DoesNotContain('\n', fixture.Wizard.Result.Text);
      Assert.DoesNotContain('\u202E', fixture.Wizard.Result.Text);
   }
}
