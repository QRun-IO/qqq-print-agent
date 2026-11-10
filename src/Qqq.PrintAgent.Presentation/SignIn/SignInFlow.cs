/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.ComponentModel;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// The connection flow as a state machine: discover what a server offers, sign in by password or through the browser,
/// hand the session to the service with the operator's pairing choice, sign out, enroll with a code, and keep the
/// certificate exception. It owns the sign-in session and decides every transition and message; a view calls its methods
/// and shows its properties. The browser, the callback inbox and the session client are behind interfaces, so the same
/// logic drives today's dialog and a later wizard, and a test can run it without a network, a browser or a pipe.
/// One operation runs at a time: every operation claims the same gate and a second one is refused, not interleaved, so
/// the flow does not depend on its caller's own busy flag. Every wait ends when the flow is disposed. The session is
/// discarded as soon as the service confirms it took it over, so the companion does not keep a usable server session.
/// </summary>
public sealed partial class SignInFlow : ObservableObject, IDisposable
{
   private readonly ServiceClient _service;
   private readonly ISignInSessionFactory _sessions;
   private readonly IBrowserLauncher _browser;
   private readonly IBrowserCallbackSource _callbacks;
   private readonly IPresentationLog _log;
   private readonly TimeProvider _time;
   private readonly CancellationTokenSource _lifetime = new();
   private ISignInSession? _session;
   private QqqAuthenticationMetadata? _metadata;
   private SignInStage _stage;
   private bool _operating;
   private bool _disposed;
   private bool _passwordOffered;
   private bool _browserOffered;
   private bool _browserPending;
   private SignInReadiness? _readiness;
   private FeedbackMessage? _feedback;
   private ServerTrust _trust = new();



   /// <summary>Wires the flow to the service client and the three things it cannot do itself; the time provider drives the browser time limits.</summary>
   public SignInFlow(ServiceClient service, ISignInSessionFactory sessions, IBrowserLauncher browser, IBrowserCallbackSource callbacks, IPresentationLog log, TimeProvider? time = null)
   {
      _service = service ?? throw new ArgumentNullException(nameof(service));
      _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
      _browser = browser ?? throw new ArgumentNullException(nameof(browser));
      _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
      _log = log ?? throw new ArgumentNullException(nameof(log));
      _time = time ?? TimeProvider.System;
   }



   /// <summary>Where the sign-in currently is.</summary>
   public SignInStage Stage
   {
      get => _stage;
      private set => SetProperty(ref _stage, value);
   }



   /// <summary>True while one of the flow's operations is running; any other operation is refused until it ends.</summary>
   public bool IsBusy => _operating;



   /// <summary>True when the discovered server offers user name and password sign-in, so the password form is shown. It is never true without a session.</summary>
   public bool PasswordOffered
   {
      get => _passwordOffered;
      private set => SetProperty(ref _passwordOffered, value);
   }



   /// <summary>True when the discovered server offers the native browser grant, so the browser button is shown. It is never true without a session.</summary>
   public bool BrowserOffered
   {
      get => _browserOffered;
      private set => SetProperty(ref _browserOffered, value);
   }



   /// <summary>True from the start of a browser sign-in until it ends, which is when the view shows its Cancel button; cancelling takes effect while the flow waits for the browser.</summary>
   public bool BrowserPending
   {
      get => _browserPending;
      private set => SetProperty(ref _browserPending, value);
   }



   /// <summary>True while a discovered session exists, which is what a sign-in needs.</summary>
   public bool HasSession => _session is not null;



   /// <summary>What the service said still has to happen after the last confirmed sign-in (printing is enabled, or this computer has to be enrolled first); null before a sign-in is confirmed and after a new discovery or a sign-out.</summary>
   public SignInReadiness? Readiness
   {
      get => _readiness;
      private set => SetProperty(ref _readiness, value);
   }



   /// <summary>The latest message for the operator. A change is announced every time one is reported, even when it equals the previous one, so the view opens its message bar again.</summary>
   public FeedbackMessage? Feedback => _feedback;



   /// <summary>
   /// Asks the entered server which sign-in methods it offers. Any earlier session is discarded first, so each attempt starts
   /// clean; a server that offers neither method is reported as needing configuration, and a failure or a cancellation leaves
   /// no session behind. The address is logged without user information, query or fragment.
   /// </summary>
   public async Task DiscoverAsync(string? address, CancellationToken cancellationToken = default)
   {
      if(!TryBegin(SignInStage.Discovering))
      {
         return;
      }
      bool discovered = false;
      using CancellationTokenSource wait = Link(cancellationToken);
      try
      {
         DropSession();
         Readiness = null;
         Uri origin = ReadOrigin(address);
         _session = _sessions.Create(origin, _trust.AppliesTo(origin));
         OnPropertyChanged(nameof(HasSession));
         _metadata = await _session.DiscoverAsync(wait.Token);
         PasswordOffered = string.Equals(_metadata.Type, "TABLE_BASED", StringComparison.Ordinal);
         BrowserOffered = _metadata.Values?.NativeSessionLogin is not null;
         _log.Info("Sign-in options for " + origin.Authority + ": password sign-in " + (PasswordOffered ? "offered" : "not offered") + ", browser sign-in " + (BrowserOffered ? "offered" : "not offered") + ".");
         Report(PasswordOffered || BrowserOffered ? SignInMessages.OptionsReady : SignInMessages.ConfigurationRequired);
         discovered = true;
      }
      catch(OperationCanceledException) when(wait.IsCancellationRequested)
      {
         _log.Debug("Discovery was cancelled before the server answered.");
      }
      catch(Exception exception) when(exception is IOException or ArgumentException or InvalidOperationException or HttpRequestException or OperationCanceledException)
      {
         LogFailure("Discovery failed", exception);
         Report(SignInMessages.DiscoveryFailed(QqqSessionDiagnostics.Explain(exception)));
      }
      finally
      {
         if(!discovered)
         {
            DropSession();
         }
         End(discovered ? SignInStage.Ready : SignInStage.Idle);
      }
   }



   /// <summary>
   /// Signs in with a user name and password, then hands the resulting session to the service. Nothing happens without a
   /// discovered session. The password is passed straight through and never kept, and a failure is reported without saying
   /// which part (credentials, device ownership or permission) was wrong.
   /// </summary>
   public async Task SignInWithPasswordAsync(string? username, string password, PairingChoice pairing, CancellationToken cancellationToken = default)
   {
      if(_session is not { } session || !TryBegin(SignInStage.SigningIn))
      {
         return;
      }
      bool confirmed = false;
      using CancellationTokenSource wait = Link(cancellationToken);
      try
      {
         _log.Info("Password sign-in submitted for " + session.Origin.Authority + ".");
         Report(SignInMessages.SigningIn);
         await session.SignInWithPasswordAsync((username ?? string.Empty).Trim(), password, wait.Token);
         ServiceResult<SignInConfirmation> handover = await HandOverAsync(session, pairing, wait.Token);
         confirmed = handover.Succeeded;
         if(handover.Outcome == ServiceOutcome.Unavailable)
         {
            _log.Error("Password sign-in not completed: the service did not answer (" + Describe(handover) + ").");
            Report(SignInMessages.PasswordNotCompleted(handover.Message));
         }
      }
      catch(OperationCanceledException) when(wait.IsCancellationRequested)
      {
         _log.Debug("Password sign-in was cancelled before the server answered.");
      }
      catch(Exception exception) when(exception is IOException or ArgumentException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
      {
         LogFailure("Password sign-in not completed", exception);
         Report(SignInMessages.PasswordNotCompleted(QqqSessionDiagnostics.Explain(exception)));
      }
      finally
      {
         End(Settled(confirmed));
      }
   }



   /// <summary>
   /// Hands the session credential, and nothing else, to the service together with the pairing choice. The service verifies
   /// it and keeps it protected. When the service confirms, its sentence is shown as success, what it said is still missing
   /// is kept as <see cref="Readiness"/>, and the flow's own session is discarded at once: from then on the service is the
   /// only process holding it. Any other answer is shown as an error. When the service did not answer, the caller decides
   /// what that means for its kind of sign-in.
   /// </summary>
   private async Task<ServiceResult<SignInConfirmation>> HandOverAsync(ISignInSession session, PairingChoice pairing, CancellationToken cancellationToken)
   {
      Stage = SignInStage.HandingOver;
      string choice = pairing switch
      {
         PairingChoice.StartNewPairing => "start a new pairing",
         PairingChoice.ClaimLegacyDevice => "associate with the existing pairing",
         _ => "keep the current pairing",
      };
      _log.Info("Sign-in accepted by the server; handing the session to the service (" + choice + ").");
      ServiceResult<SignInConfirmation> result = await _service.SignInAsync(session.ExportCredential(), pairing == PairingChoice.ClaimLegacyDevice, pairing == PairingChoice.StartNewPairing, cancellationToken);
      if(result.Succeeded)
      {
         _log.Info("Service sign-in result: " + result.Payload.Message);
         Readiness = result.Payload.Readiness;
         DropSession();
         Report(SignInMessages.HandedOver(result.Payload.Message, confirmed: true));
      }
      else if(result.Reply is string reply)
      {
         _log.Error("Service sign-in result: " + reply);
         Report(SignInMessages.HandedOver(reply, confirmed: false));
      }
      else if(result.Outcome == ServiceOutcome.Canceled)
      {
         _log.Info("The hand-over to the service is no longer waited for; the service may still complete the sign-in.");
      }
      return result;
   }



   /// <summary>Stops everything the flow holds: every wait is cancelled, the session is discarded, and no further operation starts.</summary>
   public void Dispose()
   {
      if(_disposed)
      {
         return;
      }
      _disposed = true;
      _lifetime.Cancel();
      DropSession();
      _lifetime.Dispose();
   }



   /// <summary>
   /// Claims the flow for one operation, moving to the operation's stage when it has one. It refuses while another operation
   /// is running and after the flow was disposed; the caller then does nothing, so two operations can never interleave.
   /// </summary>
   private bool TryBegin(SignInStage? stage = null)
   {
      if(_operating || _disposed)
      {
         return false;
      }
      _operating = true;
      OnPropertyChanged(nameof(IsBusy));
      if(stage is { } running)
      {
         Stage = running;
      }
      return true;
   }



   /// <summary>Releases the flow after an operation and moves to the stage the operation ended in.</summary>
   private void End(SignInStage stage)
   {
      _operating = false;
      OnPropertyChanged(nameof(IsBusy));
      Stage = stage;
   }



   /// <summary>The stage a sign-in attempt ends in: signed in when the service confirmed, idle when no session is left, and otherwise ready for another attempt.</summary>
   private SignInStage Settled(bool confirmed)
      => confirmed ? SignInStage.SignedIn : _session is null ? SignInStage.Idle : SignInStage.Ready;



   /// <summary>A cancellation source for one operation's waits: it ends when the caller's token does or when the flow is disposed.</summary>
   private CancellationTokenSource Link(CancellationToken cancellationToken)
      => CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);



   /// <summary>
   /// Reads the entered server address and logs what was asked for. Only the scheme, host, port and path are logged; an
   /// address pasted with a user name, password or token in it must not reach the shared log, and an address that cannot
   /// be read at all is not logged.
   /// </summary>
   private Uri ReadOrigin(string? address)
   {
      if(Uri.TryCreate((address ?? string.Empty).Trim(), UriKind.Absolute, out Uri? origin))
      {
         _log.Info("Discover sign-in options requested for " + QqqSessionDiagnostics.Describe(origin) + ".");
         return origin;
      }
      _log.Info("Discover sign-in options requested for an address that could not be read; it is not logged.");
      _log.Warning("The server address is not an absolute URL (expected https://host[:port]).");
      throw new InvalidOperationException("Enter a valid server origin.");
   }



   /// <summary>Discards the session, if any, and withdraws the sign-in methods it offered, so no half-finished attempt can be continued and nothing is offered that cannot work.</summary>
   private void DropSession()
   {
      _session?.Dispose();
      _session = null;
      PasswordOffered = false;
      BrowserOffered = false;
      OnPropertyChanged(nameof(HasSession));
   }



   /// <summary>Publishes a message and always announces it.</summary>
   private void Report(FeedbackMessage message)
   {
      _feedback = message;
      OnPropertyChanged(nameof(Feedback));
   }



   /// <summary>Records a failure: type and message as an error, the full exception as debug detail.</summary>
   private void LogFailure(string context, Exception exception)
   {
      _log.Error(context + ": " + QqqSessionDiagnostics.Describe(exception));
      _log.Debug(context + " detail: " + exception);
   }



   /// <summary>Words an unanswered request for the log: the cause, and whether the request had been sent.</summary>
   private static string Describe<T>(ServiceResult<T> result)
      => result.Failure + (result.Unconfirmed ? ", request sent" : ", nothing sent");
}
