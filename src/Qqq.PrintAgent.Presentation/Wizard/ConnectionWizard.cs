/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>
/// The connection wizard of ADR-0005 without its window: which step is shown, what the step shows, what its buttons
/// are called and whether they can be used, where the focus belongs and what is announced. The view binds to it and
/// forwards Enter, Escape and its buttons; it decides nothing.
/// <para>
/// This part is the frame every step shares: the steps of the run and the step indicator, the primary and the cancel
/// action (Enter and Escape), Back, the one result announcement, the focus target, and how a run ends. Each step's
/// own behavior is in its own part of the class. So far that is the Server step; on any other step the frame shows
/// the step's title and offers no primary action, because nothing is built there yet.
/// </para>
/// <para>
/// One wizard exists at a time and the window owns it. It is used from one thread, the window's, and raises every
/// change and every announcement on the thread that called it. It never holds or exposes a password, an enrollment
/// code or a session credential. Disposing it only cancels: what is still running ends by itself, and from then on
/// nothing is announced, neither a result nor a property change.
/// </para>
/// </summary>
public sealed partial class ConnectionWizard : ObservableObject, IDisposable
{
   /// <summary>The steps of a run that adds a server: all five.</summary>
   private static readonly WizardStep[] s_addServerSteps = [WizardStep.Server, WizardStep.SignIn, WizardStep.Pairing, WizardStep.Printers, WizardStep.Done];



   /// <summary>
   /// Where Back leads, for the steps that offer it. Back exists only where the step it leaves and the step it
   /// returns to have stored nothing between them: from signing in to the server, and from the summary to the
   /// printers. Pairing and printers offer none, because the step before each of them stored something that going
   /// back would not undo.
   /// </summary>
   private static readonly Dictionary<WizardStep, WizardStep> s_back = new()
   {
      [WizardStep.SignIn] = WizardStep.Server,
      [WizardStep.Done] = WizardStep.Printers,
   };



   /// <summary>The companion's client for the management pipe.</summary>
   private readonly ManagementClient _client;

   /// <summary>The window's gate; the wizard's own commands enter it under <see cref="OperationGate.Wizard"/>.</summary>
   private readonly OperationGate _gate;

   /// <summary>The log. Nothing the operator typed is written to it except the server's address, and that only as the origin the contract's address rule returned.</summary>
   private readonly IPresentationLog _log;

   /// <summary>Formats a certificate's dates.</summary>
   private readonly TimestampFormat _timestamps;

   /// <summary>The steps of this run, in order.</summary>
   private readonly WizardStep[] _steps;

   /// <summary>Cancelled when the run ends or the wizard is disposed, which ends every wait.</summary>
   private readonly CancellationTokenSource _lifetime = new();

   /// <summary>Backs <see cref="Step"/>.</summary>
   private WizardStep _step;

   /// <summary>Backs <see cref="End"/>.</summary>
   private WizardEnd? _end;

   /// <summary>Backs <see cref="FocusTarget"/>.</summary>
   private FocusTarget _focus = FocusTarget.StepStart;

   /// <summary>Backs <see cref="ResultSeverity"/>.</summary>
   private FeedbackSeverity _severity = FeedbackSeverity.Informational;

   /// <summary>True once the wizard was disposed; nothing is started or announced after that.</summary>
   private bool _disposed;



   /// <summary>Builds a wizard for one run. Only the methods that start a run call this, after they have decided that the run may start.</summary>
   private ConnectionWizard(WizardMode mode, WizardStep[] steps, ManagementClient client, OperationGate gate, IPresentationLog log, TimestampFormat timestamps)
   {
      Mode = mode;
      _steps = steps;
      _step = steps[0];
      _client = client;
      _gate = gate;
      _log = log;
      _timestamps = timestamps;
   }



   /// <summary>What this run is for.</summary>
   public WizardMode Mode { get; }



   /// <summary>The one place a result or progress is said. It is announced once, after everything that goes with it is in place, and cleared when the step changes or the address is edited.</summary>
   public Announcement Result { get; } = new();



   /// <summary>The step that is shown.</summary>
   public WizardStep Step
   {
      get => _step;
      private set => SetProperty(ref _step, value);
   }



   /// <summary>The step indicator: "Step 1 of 5: Server".</summary>
   public string StepTitle => WizardMessages.StepTitle(Array.IndexOf(_steps, _step) + 1, _steps.Length, _step);



   /// <summary>The step's primary button, which Enter presses: its label and whether it can be used.</summary>
   public WizardAction PrimaryAction => _step == WizardStep.Server ? ServerPrimaryAction() : new WizardAction(WizardMessages.Next, Enabled: false);



   /// <summary>The cancel button, which Escape presses. It can be used until the run has ended.</summary>
   public WizardAction CancelAction => new(WizardMessages.Cancel, Active);



   /// <summary>True when Back can be used: on a step that offers it, while nothing is running.</summary>
   public bool CanGoBack => Active && !IsBusy && s_back.ContainsKey(_step);



   /// <summary>How serious what <see cref="Result"/> says is, for the message bar's icon. It is set before the result is announced.</summary>
   public FeedbackSeverity ResultSeverity
   {
      get => _severity;
      private set => SetProperty(ref _severity, value);
   }



   /// <summary>Where the focus belongs now. A change is announced every time the wizard points somewhere, even at the same place again, so the view moves the focus each time.</summary>
   public FocusTarget FocusTarget => _focus;



   /// <summary>How the run ended; null while it is running. Once it is set the wizard does nothing more, and the window shows what it names and disposes the wizard.</summary>
   public WizardEnd? End
   {
      get => _end;
      private set => SetProperty(ref _end, value);
   }



   /// <summary>True while the wizard waits for the service.</summary>
   public bool IsBusy => _checkWait is not null;



   /// <summary>True while the run can still do something: it has not ended and the wizard was not disposed.</summary>
   private bool Active => !_disposed && _end is null;



   /// <summary>
   /// Says why a server cannot be added now, or null when it can. The wizard is for a service that answered and runs
   /// connections: without a snapshot, or while the stored data is not ready (an upgrade that did not finish, data a
   /// newer build wrote, a state this version does not know), every request of the wizard would be refused, so it is
   /// not started and the Computer page says why. And a computer that holds as many servers as it may takes no
   /// more; a server that is being removed is still in the snapshot's list and counts. The tree's Add server item
   /// shows the same sentence, so the rule exists once.
   /// </summary>
   public static string? WhyAddServerCannotStart(ServiceSnapshot? snapshot)
   {
      if(snapshot is null)
      {
         return WizardMessages.StartServiceNotAnswered;
      }
      return snapshot.Computer.State.Status switch
      {
         StoredState.Ready => snapshot.Servers.Count >= ManagementProtocol.MaximumServers ? WizardMessages.StartServerLimit : null,
         StoredState.UpgradeIncomplete => WizardMessages.StartUpgradeIncomplete,
         StoredState.NewerBuild => WizardMessages.StartNewerBuild,
         _ => WizardMessages.StartStoredDataNotReady,
      };
   }



   /// <summary>
   /// Starts a run that adds a server, at the Server step, or says why it cannot start
   /// (<see cref="WhyAddServerCannotStart"/>). The snapshot is the last one the window holds. Starting sends nothing.
   /// </summary>
   public static bool TryStartAddServer(ServiceSnapshot? snapshot, ManagementClient client, OperationGate gate, IPresentationLog log, [NotNullWhen(true)] out ConnectionWizard? wizard,
      [NotNullWhen(false)] out string? refusal, TimestampFormat? timestamps = null)
   {
      ArgumentNullException.ThrowIfNull(client);
      ArgumentNullException.ThrowIfNull(gate);
      ArgumentNullException.ThrowIfNull(log);
      refusal = WhyAddServerCannotStart(snapshot);
      if(refusal is not null)
      {
         log.Info("The connection wizard was not opened to add a server. {Reason}", refusal);
         wizard = null;
         return false;
      }
      log.Info("The connection wizard was opened to add a server.");
      wizard = new ConnectionWizard(WizardMode.AddServer, s_addServerSteps, client, gate, log, timestamps ?? new TimestampFormat());
      return true;
   }



   /// <summary>
   /// What Enter does: the step's primary action. On the Server step that is Check, then Next once the check has
   /// passed. Pressing it when it cannot be used is never silent there: the wizard says why. A step that is not
   /// built yet has no primary action.
   /// </summary>
   public Task InvokePrimaryAsync() => Active && _step == WizardStep.Server ? InvokeServerPrimaryAsync() : Task.CompletedTask;



   /// <summary>What Escape does: it cancels the wizard, except while the certificate warning is open, where it closes the warning only.</summary>
   public void InvokeCancel()
   {
      if(_exceptionWarning is not null)
      {
         DeclineException();
         return;
      }
      Cancel();
   }



   /// <summary>
   /// Ends the run at the operator's wish. Nothing more is sent, and a wait for the service ends at once. A request
   /// that was already sent is not taken back: the service finishes a check that is running, which stores nothing.
   /// </summary>
   public void Cancel()
   {
      if(!Active)
      {
         return;
      }
      _log.Info("The connection wizard was cancelled at the {Step} step.", WizardMessages.StepName(_step));
      Close(WizardEnd.Cancelled);
   }



   /// <summary>Goes back one step, where the step offers that (<see cref="CanGoBack"/>); otherwise nothing happens.</summary>
   public void GoBack()
   {
      if(CanGoBack)
      {
         GoTo(s_back[_step]);
      }
   }



   /// <summary>Ends the wizard. It only cancels: a wait that is running ends itself. After this nothing is announced.</summary>
   public void Dispose()
   {
      if(_disposed)
      {
         return;
      }
      _disposed = true;
      _lifetime.Cancel();
      _lifetime.Dispose();
   }



   /// <summary>
   /// Shows another step: the result of the step that is left is cleared, because a step shows only its own, and the
   /// focus goes to the new step's first input. This is how a step hands over to the next one.
   /// </summary>
   internal void GoTo(WizardStep step)
   {
      Result.Clear();
      ResultSeverity = FeedbackSeverity.Informational;
      Step = step;
      OnPropertyChanged(nameof(StepTitle));
      NotifyActions();
      Focus(FocusTarget.StepStart);
   }



   /// <summary>Announces a change, unless the wizard was disposed: a view that has closed must not be called back by something that was still finishing.</summary>
   protected override void OnPropertyChanged(PropertyChangedEventArgs e)
   {
      if(!_disposed)
      {
         base.OnPropertyChanged(e);
      }
   }



   /// <summary>Announces that a property is about to change, unless the wizard was disposed, for the same reason.</summary>
   protected override void OnPropertyChanging(PropertyChangingEventArgs e)
   {
      if(!_disposed)
      {
         base.OnPropertyChanging(e);
      }
   }



   /// <summary>
   /// Says a result or progress, once. Everything that goes with it (the state, the rows, the buttons) is set before
   /// this is called; here the severity is set, the focus is pointed somewhere when the result calls for that, and
   /// the sentence is announced last, so whoever hears it finds the whole step as it now is. A run that has ended
   /// or was disposed says nothing.
   /// </summary>
   private void Say(string text, FeedbackSeverity severity, FocusTarget? focus = null)
   {
      if(!Active)
      {
         return;
      }
      ResultSeverity = severity;
      if(focus is { } target)
      {
         Focus(target);
      }
      Result.Announce(text);
   }



   /// <summary>Points the focus somewhere and announces it, also when it is the same place as before.</summary>
   private void Focus(FocusTarget target)
   {
      _focus = target;
      OnPropertyChanged(nameof(FocusTarget));
   }



   /// <summary>Announces that the buttons may have changed.</summary>
   private void NotifyActions()
   {
      OnPropertyChanged(nameof(IsBusy));
      OnPropertyChanged(nameof(PrimaryAction));
      OnPropertyChanged(nameof(CheckAction));
      OnPropertyChanged(nameof(NextAction));
      OnPropertyChanged(nameof(CancelAction));
      OnPropertyChanged(nameof(CanGoBack));
      OnPropertyChanged(nameof(CanRequestException));
   }



   /// <summary>Ends the run: every wait stops, and the window is told what to show instead.</summary>
   private void Close(WizardEnd end)
   {
      _lifetime.Cancel();
      End = end;
      NotifyActions();
   }
}
