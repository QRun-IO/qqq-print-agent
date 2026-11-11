/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>
/// The Server step: the address, the name, and the compatibility check, which the service runs because the service is
/// what will connect. A check is one request that returns all five stages together, so the step shows no progress
/// stage by stage: it says that it is checking, and then shows every stage as passed, failed or not reached. Nothing
/// is stored by this step. What the operator accepted (the certificate exception) travels with the wizard and is
/// stored with the server, by a later step.
/// <para>
/// The result of a check belongs to the address it was made for. Editing the address removes the result, stops the
/// wait for a check that is running and forgets an accepted certificate exception. A check is sent for the address
/// that is in the box when it is sent, and for no other: one that was asked for and whose address was edited before
/// it could be sent is given up. So a result on screen is always about the address in the box.
/// </para>
/// <para>
/// The certificate exception is accepted for one check. It stays accepted only while a check that passed under it is
/// on screen; a check under it that does not pass, a refusal, an edit of the address and the operator's own choice
/// all forget it, and the warning is shown again before it can be used again. The step keeps no state of the
/// exception beside the result it belongs to: what an accepted warning said travels with the one check that runs
/// under it and is kept only with that check's pass (<see cref="Settle"/>), and a warning that is open says what
/// the check on screen reported and nothing remembered from an earlier one.
/// </para>
/// <para>
/// Everything this step offers does nothing once the wizard is on another step, has ended or was disposed.
/// </para>
/// </summary>
public sealed partial class ConnectionWizard
{
   /// <summary>Backs <see cref="Address"/>.</summary>
   private string _address = string.Empty;

   /// <summary>The name the address proposes, or empty while the address is not one a server can have.</summary>
   private string _proposedName = string.Empty;

   /// <summary>The name the operator typed; used once <see cref="_nameTyped"/> is true.</summary>
   private string _typedName = string.Empty;

   /// <summary>True once the operator typed a name, from when the name no longer follows the address.</summary>
   private bool _nameTyped;

   /// <summary>Backs <see cref="ServerState"/>.</summary>
   private ServerStepState _serverState = ServerStepState.Editing;

   /// <summary>Backs <see cref="Stages"/>.</summary>
   private IReadOnlyList<CheckStageRow> _stages = [];

   /// <summary>Backs <see cref="Certificate"/>.</summary>
   private CertificateView? _certificate;

   /// <summary>Backs <see cref="SignInMethods"/>.</summary>
   private SignInOffer? _methods;

   /// <summary>The result of the check whose rows are on screen, exactly as the client returned it; null when no rows are shown.</summary>
   private ServerCheckResult? _check;

   /// <summary>The origin the check on screen was made for; null when no rows are shown.</summary>
   private Uri? _checkedOrigin;

   /// <summary>Backs <see cref="CanRequestException"/>.</summary>
   private bool _exceptionOffered;

   /// <summary>Backs <see cref="ExceptionAccepted"/>: the warning the operator accepted, for the check that passed under it and is on screen; null in every other state. Only <see cref="Settle"/> writes it.</summary>
   private AcceptedWarning? _acceptedWarning;

   /// <summary>Backs <see cref="ExceptionOrigins"/>.</summary>
   private IReadOnlyList<string> _exceptionOrigins = [];

   /// <summary>Backs <see cref="ExceptionWarning"/>.</summary>
   private ConfirmationPrompt? _exceptionWarning;

   /// <summary>Backs <see cref="ExistingServerId"/>.</summary>
   private string? _existingServerId;



   /// <summary>
   /// The server's address as the operator types it. Changing it removes the result of a check, stops waiting for
   /// one that is running, closes the certificate warning and forgets an accepted exception. The name follows the
   /// new address unless the operator has typed one.
   /// </summary>
   public string Address
   {
      get => _address;
      set
      {
         string text = value ?? string.Empty;
         if(!OnServerStep || string.Equals(_address, text, StringComparison.Ordinal))
         {
            return;
         }
         _address = text;
         _proposedName = ServerAddress.TryRead(text, out Uri? origin, out _) ? ServerName.Propose(origin) : string.Empty;
         OnPropertyChanged();
         OnPropertyChanged(nameof(Name));
         Result.Clear();
         ResultSeverity = FeedbackSeverity.Informational;
         Settle(ServerStepState.Editing);
         StopWaiting();
      }
   }



   /// <summary>
   /// The server's name. Until the operator types one it is the name the contract proposes for the address, which
   /// always passes the name rule, and it changes with the address. Once typed it stays as typed.
   /// </summary>
   public string Name
   {
      get => _nameTyped ? _typedName : _proposedName;
      set
      {
         string text = value ?? string.Empty;
         if(!OnServerStep || string.Equals(Name, text, StringComparison.Ordinal))
         {
            return;
         }
         _nameTyped = true;
         _typedName = text;
         OnPropertyChanged();
         OnPropertyChanged(nameof(NameProblem));
         NotifyActions();
      }
   }



   /// <summary>Which part of the name rule the typed name breaks, and what to do; null when the name may be a server's name. While it is set, Next cannot be used.</summary>
   public string? NameProblem => _nameTyped ? WizardMessages.Name(ServerName.Check(_typedName)) : null;



   /// <summary>Where the step is.</summary>
   public ServerStepState ServerState => _serverState;



   /// <summary>The five stages of the last check, in order, each passed, failed with its reasons, not reached or not known; empty when no result is shown.</summary>
   public IReadOnlyList<CheckStageRow> Stages => _stages;



   /// <summary>The certificate Windows refused; null unless the step is <see cref="ServerStepState.CertificateRefused"/>.</summary>
   public CertificateView? Certificate => _certificate;



   /// <summary>The sign-in methods the check found that this version knows; null unless the check passed.</summary>
   public SignInOffer? SignInMethods => _methods;



   /// <summary>
   /// True when the development-only certificate exception can be asked for: a certificate was refused, the reasons
   /// include an authority Windows does not trust, which is the one case the exception is for, and the refusal did
   /// not come from a check that already ran under the exception.
   /// </summary>
   public bool CanRequestException => OnServerStep && _exceptionOffered;



   /// <summary>The certificate warning to show; null when none is open. The view shows it as a dialog whose default button goes back.</summary>
   public ConfirmationPrompt? ExceptionWarning
   {
      get => _exceptionWarning;
      private set => SetProperty(ref _exceptionWarning, value);
   }



   /// <summary>True while a check that passed under the certificate exception the operator accepted is on screen. Going on from here carries the exception with the wizard.</summary>
   public bool ExceptionAccepted => _acceptedWarning is not null;



   /// <summary>The addresses the accepted exception covers: the server's, and the sign-in provider's when the check named one at another address. Empty unless <see cref="ExceptionAccepted"/>.</summary>
   public IReadOnlyList<string> ExceptionOrigins => _exceptionOrigins;



   /// <summary>The server this address already is, by its identifier; null unless the step is <see cref="ServerStepState.AlreadyServer"/> and the service named the server by a full identifier.</summary>
   public string? ExistingServerId => _existingServerId;



   /// <summary>The Check button: its label, and whether it can be used. It cannot while a check is running.</summary>
   public WizardAction CheckAction
      => new(_serverState is ServerStepState.Failed or ServerStepState.CertificateRefused ? WizardMessages.CheckAgain : WizardMessages.Check, OnServerStep && !IsBusy);



   /// <summary>The Next button: it can be used when the check on screen passed and the name may be a server's name.</summary>
   public WizardAction NextAction
      => new(WizardMessages.Next, OnServerStep && _serverState is ServerStepState.Passed or ServerStepState.ExceptionReview && WizardMessages.Name(ServerName.Check(Name)) is null);



   // The next step takes the sign-in provider for the companion's own session from this object through SignInProviderOrigin.TryRead, the one function this step judged and showed it with, and from nowhere else.
   /// <summary>
   /// The result of the check that passed and is on screen: the very object the client returned for the rows the
   /// operator sees, and null in every state but <see cref="ServerStepState.Passed"/> and
   /// <see cref="ServerStepState.ExceptionReview"/>. It is there also while the typed name keeps Next from being
   /// used. Adding the server is given this object and no other, because the request states which sign-in provider
   /// the operator was shown.
   /// </summary>
   internal ServerCheckResult? PassedCheck => _serverState is ServerStepState.Passed or ServerStepState.ExceptionReview ? _check : null;



   /// <summary>The origin the check on screen was made for, in the one form an origin is kept in; null when no rows are shown.</summary>
   internal Uri? CheckedOrigin => _checkedOrigin;



   /// <summary>
   /// Asks the service to check the address. An address that cannot be a server's is not sent: the step says what is
   /// wrong with it. While a check is running a second one is refused, and the refusal is said. A check started
   /// from a result that passed under the accepted exception runs under it again; any other runs without one.
   /// </summary>
   public Task CheckAsync() => OnServerStep ? RunCheckAsync(_acceptedWarning) : Task.CompletedTask;



   /// <summary>
   /// Opens the certificate warning, when the exception can be asked for. The warning names the server's address.
   /// When the failed check already reported the sign-in provider it names that too, or says that the check named
   /// none at another address; otherwise it says that the provider's address will be shown after the check. A
   /// provider the contract does not take for an origin is not named: it is not known yet.
   /// </summary>
   public void RequestException()
   {
      if(!CanRequestException)
      {
         return;
      }
      string server = Shown(_checkedOrigin!);
      string? provider = ProviderShown(_check!);
      _log.Info("The certificate warning was opened for {Server}; the sign-in provider is {Provider}.", server, provider ?? "not known until the check has run under the exception");
      ExceptionWarning = new ConfirmationPrompt(WizardMessages.ExceptionTitle,
         WizardMessages.ExceptionWarning(server, string.Equals(provider, server, StringComparison.Ordinal) ? null : provider, providerKnown: provider is not null), WizardMessages.ExceptionAccept);
   }



   /// <summary>
   /// The operator accepted the warning: it closes, and the server is checked again under the exception. What the
   /// warning said of the sign-in provider goes with that one check, read from the check the warning was opened
   /// for, which is still the one on screen: anything that replaces it closes the warning. Nothing is stored.
   /// </summary>
   public Task AcceptExceptionAsync()
   {
      if(!OnServerStep || _exceptionWarning is null)
      {
         return Task.CompletedTask;
      }
      AcceptedWarning accepted = new(ProviderShown(_check!));
      ExceptionWarning = null;
      _log.Warning("The operator accepted the development-only certificate exception for {Server}. The server is checked again under it; nothing is stored until the server is added.", Shown(_checkedOrigin!));
      return RunCheckAsync(accepted);
   }



   /// <summary>
   /// The operator does not want the exception. While the warning is open it closes and nothing was accepted. When a
   /// check that passed under the exception is on screen, the exception is forgotten and the result with it, because
   /// that result was obtained under the exception: the server has to be checked again without it. This does not
   /// check; the operator chooses Check.
   /// </summary>
   public void DeclineException()
   {
      if(!OnServerStep)
      {
         return;
      }
      if(_exceptionWarning is not null)
      {
         _log.Info("The certificate warning was declined; no exception is accepted.");
         ExceptionWarning = null;
         return;
      }
      if(_acceptedWarning is not null)
      {
         _log.Info("The operator dropped the certificate exception that had been accepted; the result obtained under it was removed.");
         Settle(ServerStepState.Editing);
         Say(WizardMessages.ExceptionDropped, FeedbackSeverity.Informational, FocusTarget.PrimaryAction);
      }
   }



   /// <summary>Ends the wizard on the server this address already is, which the window then opens. Nothing happens unless the service named that server.</summary>
   public void OpenExistingServer()
   {
      if(!OnServerStep || _existingServerId is null)
      {
         return;
      }
      _log.Info("The connection wizard was closed to open the server the address already is ({ServerId}).", _existingServerId);
      Close(WizardEnd.Server(_existingServerId));
   }



   /// <summary>The sign-in provider a check reported, in the form origins are shown in, as the contract judges it; null when the check named none or named something the contract does not take for an origin.</summary>
   private static string? ProviderShown(ServerCheckResult check) => CertificateView.Origin(check.ProviderOrigin);



   /// <summary>The Server step's primary button: Next once a check has passed or found the address to be a server already, and Check otherwise.</summary>
   private WizardAction ServerPrimaryAction()
      => _serverState is ServerStepState.Passed or ServerStepState.ExceptionReview or ServerStepState.AlreadyServer ? NextAction : CheckAction;



   /// <summary>
   /// What Enter does on the Server step. With a check that passed it goes on, or says what is wrong with the name.
   /// With an address that is already a server there is nothing to go on to, and it says so again. Otherwise it
   /// checks; while a check is running that is refused out loud.
   /// </summary>
   private Task InvokeServerPrimaryAsync()
   {
      switch(_serverState)
      {
         case ServerStepState.Passed or ServerStepState.ExceptionReview:
            GoOn();
            return Task.CompletedTask;
         case ServerStepState.AlreadyServer:
            SayAlreadyServer();
            return Task.CompletedTask;
         default:
            return CheckAsync();
      }
   }



   /// <summary>Goes on to signing in, when the name may be a server's name; otherwise says which part of the name rule it breaks and points at the name.</summary>
   private void GoOn()
   {
      if(WizardMessages.Name(ServerName.Check(Name)) is { } problem)
      {
         Say(problem, FeedbackSeverity.Warning, FocusTarget.ServerName);
         return;
      }
      _log.Info("The Server step is complete for {Server}: every stage of the check passed{Exception}.", Shown(_checkedOrigin!), _acceptedWarning is not null ? " under the certificate exception the operator accepted" : string.Empty);
      GoTo(WizardStep.SignIn);
   }



   /// <summary>Back from the next step: says again the result that is still on screen, the exception and the addresses it covers among it, and points the focus where that result leads.</summary>
   private void SayServerResultAgain()
   {
      if(_serverState is ServerStepState.Passed or ServerStepState.ExceptionReview)
      {
         SayPassed();
      }
   }



   /// <summary>
   /// Runs one check. The address is read by the contract's own rule first, and what is sent is the origin that rule
   /// returned, never the text as typed. The wizard waits for the service the way every step does
   /// (<see cref="TryBeginWait"/>): a second check is refused out loud, and a check whose address was edited while
   /// the wizard's gate was being entered is given up, so what is sent is always about the address in the box. A
   /// result is shown only when the wait was not ended meanwhile, by an edit of the address, by Cancel or by
   /// disposal: such a result is about an address that is no longer in the box, or about a wizard that is gone.
   /// <paramref name="exception"/> is the warning the operator accepted, when the check runs under the exception,
   /// and null when it runs without one.
   /// </summary>
   private async Task RunCheckAsync(AcceptedWarning? exception)
   {
      if(!ServerAddress.TryRead(_address, out Uri? origin, out ServerAddressProblem problem))
      {
         _log.Debug("The address typed in the wizard cannot be a server's address ({Problem}); nothing was sent.", problem);
         Say(WizardMessages.Address(problem), FeedbackSeverity.Warning, FocusTarget.StepStart);
         return;
      }
      if(!TryBeginWait("A check of the server", WizardMessages.CheckStillRunning, out ServiceWait? wait))
      {
         return;
      }
      ServiceResult<ServerCheckResult> result;
      try
      {
         BeginCheck(origin, exception is not null);
         result = await _client.CheckServerAsync(origin, exception is not null, wait.Cancellation);
      }
      catch
      {
         EndWait(wait);
         Settle(ServerStepState.Editing);
         throw;
      }
      if(!EndWait(wait))
      {
         _log.Debug("The check of {Server} is no longer waited for; whatever it finds is not shown.", Shown(origin));
         return;
      }
      ShowResult(origin, exception, result);
   }



   /// <summary>Shows that a check is running and says so once.</summary>
   private void BeginCheck(Uri origin, bool underException)
   {
      Settle(ServerStepState.Checking);
      _log.Info("Checking {Server} through the service, {Exception}.", Shown(origin), underException ? "under the certificate exception the operator accepted" : "without a certificate exception");
      Say(WizardMessages.Checking, FeedbackSeverity.Informational);
   }



   /// <summary>Shows what came back: a result, a refusal by its code, or no answer.</summary>
   private void ShowResult(Uri origin, AcceptedWarning? exception, ServiceResult<ServerCheckResult> result)
   {
      if(result.Succeeded)
      {
         ShowCheck(origin, exception, result.Payload, failureCode: null);
      }
      else if(result.Outcome == ServiceOutcome.Rejected)
      {
         ShowRefusal(origin, exception, result);
      }
      else
      {
         ShowNoAnswer(origin, result);
      }
   }



   /// <summary>
   /// Shows that no usable answer came. Whether anything may have been carried out is the result's own word
   /// (<see cref="ServiceResult{T}.Unconfirmed"/>) and is asked first: a check that may have run says that its
   /// result is not known and that it can be repeated, because a check changes nothing, whatever kept the answer
   /// away. A service of another version says which side to update and that the check could not be made, which is
   /// all that is known of it, and a request that never left says that the service was not reached and that
   /// nothing was checked. What was typed is kept in every case.
   /// </summary>
   private void ShowNoAnswer(Uri origin, ServiceResult<ServerCheckResult> result)
   {
      (string text, FeedbackSeverity severity) = result switch
      {
         { Unconfirmed: true } => (WizardMessages.Unanswered, FeedbackSeverity.Warning),
         { Failure: ServiceFailure.OlderService } => (WizardMessages.OlderService, FeedbackSeverity.Error),
         { Failure: ServiceFailure.NewerService } => (WizardMessages.NewerService, FeedbackSeverity.Error),
         _ => (WizardMessages.NotReached, FeedbackSeverity.Error),
      };
      _log.Warning("The check of {Server} brought no result ({Failure}, {Sent}).", Shown(origin), result.Failure, result.Unconfirmed ? "the request was sent" : "nothing was carried out");
      Settle(ServerStepState.Editing);
      Say(text, severity, FocusTarget.PrimaryAction);
   }



   /// <summary>Shows a refusal, by the one table that says what each code means for a check.</summary>
   private void ShowRefusal(Uri origin, AcceptedWarning? exception, ServiceResult<ServerCheckResult> result)
   {
      WizardReaction reaction = WizardReactions.For(ManagementOperations.ServersCheck, result.Code!);
      if(reaction == WizardReaction.ShowCheck)
      {
         if(result.TryReadDetails(out ServerCheckResult? details))
         {
            ShowCheck(origin, exception, details, result.Code);
            return;
         }
         _log.Warning("The check of {Server} did not pass ({Code}), and the service sent no details that could be read.", Shown(origin), result.Code);
         Settle(ServerStepState.Failed);
         Say(WizardMessages.FailedWithoutDetails(result.Message), FeedbackSeverity.Error, FocusTarget.PrimaryAction);
         return;
      }
      _log.Warning("The service refused the check of {Server} with {Code}{Expected}.", Shown(origin), result.Code, reaction == WizardReaction.NotThisOperations ? ", which a check is not expected to be answered with" : string.Empty);
      Settle(ServerStepState.Editing);
      switch(reaction)
      {
         case WizardReaction.ValueNotAccepted:
            Say(WizardMessages.AddressRefusedByService, FeedbackSeverity.Warning, FocusTarget.StepStart);
            break;
         case WizardReaction.StoredDataNotReady:
            Say(WizardMessages.StoredDataNotReady, FeedbackSeverity.Error, FocusTarget.PrimaryAction);
            Close(WizardEnd.ComputerPage);
            break;
         case WizardReaction.UpdateInProgress:
            Say(WizardMessages.UpdateInProgress, FeedbackSeverity.Warning, FocusTarget.PrimaryAction);
            break;
         case WizardReaction.Busy:
            Say(WizardMessages.Busy, FeedbackSeverity.Warning, FocusTarget.PrimaryAction);
            break;
         default:
            Say(WizardMessages.ServiceSaid(result.Message), FeedbackSeverity.Error, FocusTarget.PrimaryAction);
            break;
      }
   }



   /// <summary>
   /// Shows the result of a check, passed or not. An address that is already a server says so first, whatever the
   /// stages say. Otherwise a success is a pass and a failure shows its stages, or the certificate when that is why
   /// it failed.
   /// </summary>
   private void ShowCheck(Uri origin, AcceptedWarning? exception, ServerCheckResult check, string? failureCode)
   {
      IReadOnlyList<CheckStageRow> rows = CheckStageRow.From(check);
      if(check.ExistingServerId is not null)
      {
         string? existing = ManagementProtocol.IsServerId(check.ExistingServerId) ? check.ExistingServerId : null;
         _log.Info("The address that was checked ({Server}) is already a server on this computer ({ServerId}).", Shown(origin), existing ?? "the service did not name it by a full identifier");
         Settle(ServerStepState.AlreadyServer, check, origin, rows, existing: existing);
         SayAlreadyServer();
      }
      else if(failureCode is null)
      {
         ShowPassed(origin, exception, check, rows);
      }
      else if(string.Equals(failureCode, ManagementErrorCode.CertificateUntrusted, StringComparison.Ordinal) && check.Certificate is { } certificate)
      {
         ShowCertificate(origin, exception is not null, check, rows, certificate);
      }
      else
      {
         CheckStageRow? failed = rows.FirstOrDefault(row => row.Outcome == CheckStageOutcome.Failed);
         _log.Warning("The check of {Server} did not pass; the stage that failed is {Stage}.", Shown(origin), failed?.Stage ?? "not one this version knows");
         Settle(ServerStepState.Failed, check, origin, rows);
         Say(WizardMessages.Failed(failed?.Title), FeedbackSeverity.Error, FocusTarget.CheckStages);
      }
   }



   /// <summary>
   /// Shows a check the service answered as passed. It is taken as passed only when the contract's own rule finds no
   /// failure in it (<see cref="ServerCheckResult.FailureCode"/>, which is what adding the server will hold it to),
   /// all five stages this version knows say so and none is reported twice, when the sign-in provider it names is
   /// one the contract takes for an origin (<see cref="SignInProviderOrigin.TryRead"/>, the rule the request that
   /// states the provider is held to) or is absent, and when it offers a sign-in method this version knows: Next
   /// must never lead on from a result the wizard cannot stand behind, or from a provider no request can state.
   /// Under the accepted exception the addresses the exception covers are shown; the operator is asked to look at
   /// them before going on unless the warning that was accepted had already named exactly this provider.
   /// </summary>
   private void ShowPassed(Uri origin, AcceptedWarning? exception, ServerCheckResult check, IReadOnlyList<CheckStageRow> rows)
   {
      string? provider = ProviderShown(check);
      bool passed = check.FailureCode() is null && rows.All(row => row.Outcome == CheckStageOutcome.Passed) && !CheckStageRow.ReportsAStageTwice(check);
      if(!passed || (check.ProviderOrigin is not null && provider is null))
      {
         _log.Warning("The service answered the check of {Server} as passed, but the answer cannot be used: a stage is not reported as passed or is reported twice, or the sign-in provider it names is not an origin.", Shown(origin));
         Settle(ServerStepState.Failed);
         Say(WizardMessages.CheckUnusable, FeedbackSeverity.Error, FocusTarget.PrimaryAction);
         return;
      }
      var methods = SignInOffer.Checked(check);
      if(!methods.Any)
      {
         _log.Warning("The check of {Server} passed, but it offers no sign-in method this version knows.", Shown(origin));
         Settle(ServerStepState.Failed, stages: rows);
         Say(WizardMessages.NoKnownSignInMethod, FeedbackSeverity.Error, FocusTarget.CheckStages);
         return;
      }
      string server = Shown(origin);
      IReadOnlyList<string> covered = exception is null ? [] : provider is null || string.Equals(provider, server, StringComparison.Ordinal) ? [server] : [server, provider];
      bool review = exception is not null && !(exception.NamedProvider is { } named && string.Equals(named, provider, StringComparison.Ordinal));
      _log.Info("The check of {Server} passed; sign-in methods: {Methods}; sign-in provider: {Provider}; certificate exception: {Exception}.", server, methods, provider ?? "none named",
         exception is not null ? "accepted by the operator" : "none");
      Settle(review ? ServerStepState.ExceptionReview : ServerStepState.Passed, check, origin, rows, methods: methods, accepted: exception, exceptionOrigins: covered);
      SayPassed();
   }



   /// <summary>
   /// Says the pass that is on screen: what passed, the addresses the exception covers when it passed under one, the
   /// sign-in methods, and what to do next. That is Next, where the focus then belongs. While the name that is
   /// typed breaks the name rule Next cannot be used, so the sentence ends with what is wrong with the name instead
   /// and the focus belongs on the name.
   /// </summary>
   private void SayPassed()
   {
      string? nameProblem = WizardMessages.Name(ServerName.Check(Name));
      string text = _acceptedWarning is null ? WizardMessages.Passed(_methods!, nameProblem)
         : WizardMessages.PassedUnderException(_methods!, _exceptionOrigins, _serverState == ServerStepState.ExceptionReview, nameProblem);
      Say(text, nameProblem is null ? FeedbackSeverity.Success : FeedbackSeverity.Warning, nameProblem is null ? FocusTarget.PrimaryAction : FocusTarget.ServerName);
   }



   /// <summary>
   /// Shows the certificate Windows refused and what can be done about each reason it was refused for. The exception
   /// is offered only when the reasons include an authority Windows does not trust, and not when this refusal came
   /// back from a check that already ran under the exception: then the exception does not cover what was refused,
   /// and the step says so.
   /// </summary>
   private void ShowCertificate(Uri origin, bool underException, ServerCheckResult check, IReadOnlyList<CheckStageRow> rows, PresentedCertificate certificate)
   {
      var view = CertificateView.From(certificate, _timestamps);
      bool offered = !underException && certificate.Refusals.Contains(PresentedCertificate.UntrustedAuthority, StringComparer.Ordinal);
      _log.Warning("Windows refused a certificate while the service checked {Server} (presented by {Presenter}); the certificate exception {Offer}.", Shown(origin), view.PresentedBy ?? "an address the service did not name",
         offered ? "is offered" : underException ? "was accepted and does not cover it" : "is not offered");
      Settle(ServerStepState.CertificateRefused, check, origin, rows, certificate: view, exceptionOffered: offered);
      Say(WizardMessages.CertificateRefused(view.PresentedBy, certificate.Refusals, offered, exceptionDidNotCover: underException), FeedbackSeverity.Error, FocusTarget.Certificate);
   }



   /// <summary>Says that the address is already a server, and points at the way to open it when the service named the server.</summary>
   private void SayAlreadyServer()
   {
      if(_existingServerId is null)
      {
         Say(WizardMessages.AlreadyServerNotNamed, FeedbackSeverity.Informational, FocusTarget.StepStart);
         return;
      }
      Say(WizardMessages.AlreadyServer, FeedbackSeverity.Informational, FocusTarget.ExistingServer);
   }



   /// <summary>
   /// Sets everything the Server step shows, the state last, so whoever reacts to the state finds what goes with it.
   /// What is not given is removed: a state never keeps rows, a certificate, an offer or an acceptance of the
   /// exception, the warning that was accepted or an existing server from the state before it, and the certificate
   /// warning is closed. So no state of the exception outlives the result it belongs to.
   /// </summary>
   private void Settle(ServerStepState state, ServerCheckResult? check = null, Uri? origin = null, IReadOnlyList<CheckStageRow>? stages = null, CertificateView? certificate = null,
      SignInOffer? methods = null, bool exceptionOffered = false, AcceptedWarning? accepted = null, IReadOnlyList<string>? exceptionOrigins = null, string? existing = null)
   {
      _check = check;
      _checkedOrigin = origin;
      SetProperty(ref _stages, stages ?? [], nameof(Stages));
      SetProperty(ref _certificate, certificate, nameof(Certificate));
      SetProperty(ref _methods, methods, nameof(SignInMethods));
      SetProperty(ref _exceptionOffered, exceptionOffered, nameof(CanRequestException));
      SetProperty(ref _acceptedWarning, accepted, nameof(ExceptionAccepted));
      SetProperty(ref _exceptionOrigins, exceptionOrigins ?? [], nameof(ExceptionOrigins));
      SetProperty(ref _existingServerId, existing, nameof(ExistingServerId));
      ExceptionWarning = null;
      SetProperty(ref _serverState, state, nameof(ServerState));
      NotifyActions();
   }



   /// <summary>An origin as it is sent, shown and logged: scheme, the host's ASCII name and the port when it is not the scheme's own, without a trailing slash.</summary>
   private static string Shown(Uri origin) => origin.GetLeftPart(UriPartial.Authority);



   /// <summary>
   /// The certificate warning the operator accepted, by what it said of the sign-in provider: the provider it named,
   /// in the form origins are shown in, or null when it said that the provider was not known yet. It goes with the
   /// one check that runs under the exception and is kept only with that check's pass, so that the review of the
   /// addresses is skipped for the provider the operator was already shown and for no other.
   /// </summary>
   private sealed record AcceptedWarning(string? NamedProvider);
}
