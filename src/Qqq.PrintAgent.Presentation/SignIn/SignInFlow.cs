/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
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
/// One operation runs at a time: every operation claims the same gate and a second one is refused with a message, not
/// interleaved, so the flow does not depend on its caller's own busy flag. Disposing the flow only cancels: the running
/// operation ends itself, discards the session on its way out, and from then on nothing is announced, reported or sent.
/// The session is discarded as soon as the service confirms it took it over, and also when the service may have taken it
/// over without confirming, so the companion does not keep a usable server session.
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
   /// no session behind. The address is logged without user information, query or fragment. It returns false only when
   /// the request was refused because another operation is running.
   /// </summary>
   public async Task<bool> DiscoverAsync(string? address, CancellationToken cancellationToken = default)
   {
      if(!TryBegin("Discovery"))
      {
         return false;
      }
      bool discovered = false;
      return await RunAsync("Discovery", SignInStage.Discovering, async token =>
      {
         discovered = await TryDiscoverAsync(address, token);
         return true;
      }, stopped: true, () => discovered ? SignInStage.Ready : SignInStage.Idle, cancellationToken);
   }



   /// <summary>The discovery itself. It returns whether the server answered with its sign-in methods; whenever it did not, for whatever reason, the session it created is discarded before it returns or throws.</summary>
   private async Task<bool> TryDiscoverAsync(string? address, CancellationToken token)
   {
      bool discovered = false;
      try
      {
         DropSession();
         Readiness = null;
         Uri origin = ReadOrigin(address);
         _session = _sessions.Create(origin, _trust.AppliesTo(origin));
         OnPropertyChanged(nameof(HasSession));
         _metadata = await _session.DiscoverAsync(token);
         token.ThrowIfCancellationRequested();
         PasswordOffered = string.Equals(_metadata.Type, "TABLE_BASED", StringComparison.Ordinal);
         BrowserOffered = _metadata.Values?.NativeSessionLogin is not null;
         _log.Info("Sign-in options for " + origin.Authority + ": password sign-in " + (PasswordOffered ? "offered" : "not offered") + ", browser sign-in " + (BrowserOffered ? "offered" : "not offered") + ".");
         Report(PasswordOffered || BrowserOffered ? SignInMessages.OptionsReady : SignInMessages.ConfigurationRequired);
         discovered = true;
      }
      catch(Exception exception) when(Reportable(exception, token) && exception is IOException or ArgumentException or InvalidOperationException or HttpRequestException or OperationCanceledException)
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
      }
      return discovered;
   }



   /// <summary>
   /// Signs in with a user name and password, then hands the resulting session to the service. Nothing happens without a
   /// discovered session. The password is passed straight through and never kept, and a failure is reported without saying
   /// which part (credentials, device ownership or permission) was wrong. It returns false when nothing was attempted (no
   /// session, or another operation is running), so the caller knows the password was not used.
   /// </summary>
   public async Task<bool> SignInWithPasswordAsync(string? username, string password, PairingChoice pairing, CancellationToken cancellationToken = default)
   {
      if(_session is not { } session || !TryBegin("Password sign-in"))
      {
         return false;
      }
      return await RunAsync("Password sign-in", SignInStage.SigningIn, async token =>
      {
         if(await TrySignInWithPasswordAsync(session, (username ?? string.Empty).Trim(), password, token) is { } credential)
         {
            ServiceResult<SignInConfirmation> handover = await HandOverAsync(credential, pairing, token);
            if(handover is { Outcome: ServiceOutcome.Unavailable, Unconfirmed: false })
            {
               _log.Error("Password sign-in not completed: the service did not answer (" + Describe(handover) + ").");
               Report(SignInMessages.PasswordNotCompleted(handover.Message));
            }
         }
         return true;
      }, stopped: true, Settled, cancellationToken);
   }



   /// <summary>
   /// The server's part of a password sign-in. It returns the session credential to hand over, or null when the sign-in
   /// failed and was reported. A failure of the server's sign-in, including the server's own time limit, is shown as "not
   /// completed". A wait that the caller or disposal ended is not a failure and is left to the runner, and no credential
   /// is taken once the wait has ended. The hand-over and what it announces are deliberately not inside this method's
   /// protection: once the server has accepted the sign-in, a failure while the result is announced (a view's handler
   /// that throws) is not a failed sign-in and must not be reported as one.
   /// </summary>
   private async Task<QqqSessionCredential?> TrySignInWithPasswordAsync(ISignInSession session, string username, string password, CancellationToken token)
   {
      try
      {
         _log.Info("Password sign-in submitted for " + session.Origin.Authority + ".");
         Report(SignInMessages.SigningIn);
         await session.SignInWithPasswordAsync(username, password, token);
         token.ThrowIfCancellationRequested();
         return session.ExportCredential();
      }
      catch(Exception exception) when(Reportable(exception, token) && exception is IOException or ArgumentException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
      {
         LogFailure("Password sign-in not completed", exception);
         Report(SignInMessages.PasswordNotCompleted(QqqSessionDiagnostics.Explain(exception)));
         return null;
      }
   }



   /// <summary>
   /// Hands the session credential, and nothing else, to the service together with the pairing choice. The service verifies
   /// it and keeps it protected. When the service confirms, its sentence is shown as success, what it said is still missing
   /// is kept as <see cref="Readiness"/>, and the flow's own session is discarded at once: from then on the service is the
   /// only process holding it. A refusal is shown as an error. When the request left and no usable answer came back, the
   /// service may have signed in all the same: the operator is told the sign-in could not be confirmed and to check the
   /// status before trying again, and the session is discarded too, because the service may hold it. When nothing was sent,
   /// the caller decides what that means for its kind of sign-in.
   /// </summary>
   private async Task<ServiceResult<SignInConfirmation>> HandOverAsync(QqqSessionCredential credential, PairingChoice pairing, CancellationToken cancellationToken)
   {
      Stage = SignInStage.HandingOver;
      string choice = pairing switch
      {
         PairingChoice.StartNewPairing => "start a new pairing",
         PairingChoice.ClaimLegacyDevice => "associate with the existing pairing",
         _ => "keep the current pairing",
      };
      _log.Info("Sign-in accepted by the server; handing the session to the service (" + choice + ").");
      ServiceResult<SignInConfirmation> result = await _service.SignInAsync(credential, pairing == PairingChoice.ClaimLegacyDevice, pairing == PairingChoice.StartNewPairing, cancellationToken);
      ThrowIfDisposed();
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
      else if(result.Unconfirmed)
      {
         string cause = SignInMessages.Unanswered(result);
         _log.Error("The hand-over to the service could not be confirmed, so the service may or may not have signed in; the attempt was discarded: " + cause);
         DropSession();
         Report(SignInMessages.HandOverUnconfirmed(cause));
      }
      return result;
   }



   /// <summary>
   /// Ends the flow. It only cancels: an operation that is still running sees the cancellation, ends quietly and discards
   /// the session itself, because discarding the session under a call that is still using it makes that call fail in ways
   /// that look like errors. When nothing is running the session is discarded here. After this nothing is announced.
   /// </summary>
   public void Dispose()
   {
      if(_disposed)
      {
         return;
      }
      _disposed = true;
      _lifetime.Cancel();
      if(!_operating)
      {
         DropSession();
      }
      _lifetime.Dispose();
   }



   /// <summary>Announces a change, unless the flow was disposed: a view that has closed must not be called back by an operation that was still finishing.</summary>
   protected override void OnPropertyChanged(PropertyChangedEventArgs e)
   {
      if(!_disposed)
      {
         base.OnPropertyChanged(e);
      }
   }



   /// <summary>
   /// Claims the flow for one operation. After the flow was disposed it refuses silently. While another operation is
   /// running it refuses out loud: the refusal is logged and the operator is told that another operation is still running,
   /// so a click that did nothing is never silent. Nothing is announced by a successful claim; the runner does that inside
   /// its own protection.
   /// </summary>
   private bool TryBegin(string operation)
   {
      if(_disposed)
      {
         return false;
      }
      if(_operating)
      {
         _log.Warning(operation + " was not started: another operation is still running.");
         Report(SignInMessages.Busy);
         return false;
      }
      _operating = true;
      return true;
   }



   /// <summary>
   /// Runs one claimed operation and always releases the flow afterwards, whatever the operation or a subscriber does.
   /// The operation's token ends when the caller's does or when the flow is disposed. Everything that announces something
   /// happens inside the protected part, so a subscriber that throws cannot leave the flow claimed forever. An operation
   /// that ended because the flow was disposed, or because its caller stopped waiting, is not a failure: it is noted as
   /// debug detail and <paramref name="stopped"/> is returned. The stage the operation ends in is asked for last, when
   /// the operation knows how it went.
   /// </summary>
   private async Task<T> RunAsync<T>(string operation, SignInStage? stage, Func<CancellationToken, Task<T>> body, T stopped, Func<SignInStage> settle, CancellationToken cancellationToken)
   {
      CancellationTokenSource? wait = null;
      try
      {
         wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
         OnPropertyChanged(nameof(IsBusy));
         if(stage is { } running)
         {
            Stage = running;
         }
         wait.Token.ThrowIfCancellationRequested();
         return await body(wait.Token);
      }
      catch(Exception exception) when(_disposed || exception is OperationCanceledException && wait is { IsCancellationRequested: true })
      {
         _log.Debug(operation + (_disposed ? " ended because the connection flow was closed (" : " was cancelled by its caller (") + exception.GetType().Name + ").");
         return stopped;
      }
      finally
      {
         End(settle, wait);
      }
   }



   /// <summary>
   /// Releases the flow after an operation. The stage is set first and the flow released after it, so a subscriber that
   /// starts the next operation when the flow becomes free does not have its stage overwritten by this one's. The release
   /// happens even when a subscriber throws. A flow that was disposed while the operation ran discards its session here,
   /// now that no call is using it, before the stage the operation ends in is worked out.
   /// </summary>
   private void End(Func<SignInStage> settle, CancellationTokenSource? wait)
   {
      try
      {
         if(_disposed)
         {
            DropSession();
         }
         Stage = settle();
      }
      finally
      {
         wait?.Dispose();
         _operating = false;
         OnPropertyChanged(nameof(IsBusy));
      }
   }



   /// <summary>
   /// The stage a sign-in attempt ends in, read from what the flow holds: ready for another attempt while a session is
   /// left, signed in when the service confirmed (which is when the flow knows what the sign-in achieved and has given its
   /// session up), and otherwise idle. Because it reads the state and not a result handed back, the stage is right even
   /// when a view's handler throws while the confirmation is being announced.
   /// </summary>
   private SignInStage Settled()
      => _session is not null ? SignInStage.Ready : _readiness is not null ? SignInStage.SignedIn : SignInStage.Idle;



   /// <summary>
   /// Whether a failure is one to show. A failure seen after the flow was disposed never is, and neither is the
   /// cancellation that the operation's own token asked for. A cancellation nobody here asked for, such as the server's
   /// own time limit, is a failure like any other and must be shown.
   /// </summary>
   private bool Reportable(Exception exception, CancellationToken token)
      => !_disposed && !(exception is OperationCanceledException && token.IsCancellationRequested);



   /// <summary>Stops an operation that has just come back from the service when the flow was disposed meanwhile, before it logs, announces or changes anything.</summary>
   private void ThrowIfDisposed()
   {
      if(_disposed)
      {
         throw new OperationCanceledException("The connection flow was closed.");
      }
   }



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



   /// <summary>Publishes a message and always announces it; after the flow was disposed nothing is published.</summary>
   private void Report(FeedbackMessage message)
   {
      if(_disposed)
      {
         return;
      }
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
