/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// The part of signing in that needs a session, and runs in the companion as the signed-in Windows user: read the
/// server's sign-in description, sign in by password or through the browser, and return the session credential to the
/// caller. That is all it does. It hands nothing to the service, enrolls nothing and holds no trust setting: the
/// certificate exception to read under is given with each preparation, and what happens to the credential is the
/// caller's business. A password is passed straight to the session and never kept, and the credential is only ever a
/// return value, never a property. One operation runs at a time; a second one is refused and logged. Everything it
/// announces is raised on the thread that called it. Disposing it only cancels: the running operation ends itself,
/// discards the session on its way out, and from then on nothing is announced, neither that a property changed nor
/// that one is about to.
/// </summary>
public sealed partial class CompanionSignIn : ObservableObject, IDisposable
{
   /// <summary>Creates the session a read of the sign-in description runs on.</summary>
   private readonly ISignInSessionFactory _sessions;

   /// <summary>Opens the provider's page; used only through <see cref="BrowserLaunch"/>.</summary>
   private readonly IBrowserLauncher _browser;

   /// <summary>Names this application's registered callback and reads the protocol handler's trace. The inbox itself belongs to the dispatcher.</summary>
   private readonly IBrowserCallbackSource _callbacks;

   /// <summary>The one dispatcher a browser sign-in waits for its callback through.</summary>
   private readonly BrowserCallbackDispatcher _dispatcher;

   /// <summary>The log. No password, credential, code, state or address with a query is ever passed to it.</summary>
   private readonly IPresentationLog _log;

   /// <summary>The clock the browser sign-in's time limit runs on.</summary>
   private readonly TimeProvider _time;

   /// <summary>Cancelled when the sign-in is disposed, which ends the operation that is running.</summary>
   private readonly CancellationTokenSource _lifetime = new();

   /// <summary>The session that is held, on which a sign-in can be made; null when none is.</summary>
   private ISignInSession? _session;

   /// <summary>The callbacks the held session's description lists for a browser sign-in. They are compared with this application's own and never logged.</summary>
   private IReadOnlyList<string> _redirects = [];

   /// <summary>The wait of the operation that is running, from its start until it begins to be released; null otherwise. While it is set, the operation can still be cancelled and will discard the session on its way out.</summary>
   private CancellationTokenSource? _operation;

   /// <summary>Backs <see cref="State"/>.</summary>
   private CompanionSignInState _state;

   /// <summary>Backs <see cref="Offer"/>.</summary>
   private SignInOffer? _offer;

   /// <summary>Backs <see cref="Rejection"/>.</summary>
   private QqqCertificateRejection? _rejection;

   /// <summary>Backs <see cref="Failure"/>.</summary>
   private string? _failure;

   /// <summary>True from the moment an operation claims the sign-in until it has been released, announcements included; it backs <see cref="IsBusy"/>.</summary>
   private bool _operating;

   /// <summary>True when a discard was asked for while an operation ran: the operation discards the session as it ends.</summary>
   private bool _discarding;

   /// <summary>True once the sign-in was disposed; nothing is started or announced after that.</summary>
   private bool _disposed;



   /// <summary>Wires the sign-in to the things it cannot do itself: create a session, open the browser, name this application's callback, and wait for a callback through the one dispatcher. The time provider drives the browser time limit.</summary>
   public CompanionSignIn(ISignInSessionFactory sessions, IBrowserLauncher browser, IBrowserCallbackSource callbacks, BrowserCallbackDispatcher dispatcher, IPresentationLog log, TimeProvider? time = null)
   {
      _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
      _browser = browser ?? throw new ArgumentNullException(nameof(browser));
      _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
      _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
      _log = log ?? throw new ArgumentNullException(nameof(log));
      _time = time ?? TimeProvider.System;
   }



   /// <summary>Where the sign-in is, or how its last step ended.</summary>
   public CompanionSignInState State
   {
      get => _state;
      private set => SetProperty(ref _state, value);
   }



   /// <summary>The methods that can be used on the session that is held; null while none is held.</summary>
   public SignInOffer? Offer
   {
      get => _offer;
      private set => SetProperty(ref _offer, value);
   }



   /// <summary>The certificate this Windows account refused in the last step, with the origin that presented it; null when the last step did not end over a certificate. It is public certificate data.</summary>
   public QqqCertificateRejection? Rejection
   {
      get => _rejection;
      private set => SetProperty(ref _rejection, value);
   }



   /// <summary>Why the last step was not completed, in words, when it failed for a reason other than a certificate; null otherwise. It is the session's own explanation, which never says which part of a sign-in was wrong.</summary>
   public string? Failure
   {
      get => _failure;
      private set => SetProperty(ref _failure, value);
   }



   /// <summary>True while one of the operations is running; any other is refused until it ends.</summary>
   public bool IsBusy => _operating;



   /// <summary>True while a session is held, which is what a sign-in needs.</summary>
   public bool HasSession => _session is not null;



   /// <summary>
   /// Reads the server's sign-in description as the signed-in Windows user and says which methods can be used. Any
   /// session held from before is discarded first. The session is created under the certificate exception the caller
   /// gives, which is the one the operator accepted or the one stored with the server, and under no other. The methods
   /// offered are those the service's check and this read both found; a difference is logged. When the description
   /// names another sign-in provider than the check reported, or this account refuses the server's certificate, or the
   /// description cannot be read, the state says which and no session is kept. It returns the offer, or null when
   /// nothing can be signed in with, when the caller stopped waiting, or when another operation is running. An offer
   /// is returned only while the session it was made on is the one that is held: when a subscriber discarded the
   /// session while the result was being announced, the read returns null, as <see cref="Offer"/> then is, so a
   /// caller is never handed an offer it cannot sign in with.
   /// </summary>
   public async Task<SignInOffer?> PrepareAsync(Uri server, ServerCheckResult check, QqqCertificateExceptionOrigins certificateException, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(server);
      ArgumentNullException.ThrowIfNull(check);
      ArgumentNullException.ThrowIfNull(certificateException);
      if(!TryBegin("Reading the sign-in description"))
      {
         return null;
      }
      SignInOffer? offer = await RunAsync("Reading the sign-in description", CompanionSignInState.Preparing, token => ReadDescriptionAsync(server, check, certificateException, token), cancellationToken);
      if(offer is not null && !ReferenceEquals(offer, _offer))
      {
         _log.Debug("The sign-in description was read, and its session was discarded before the read returned, so the read offers nothing.");
         return null;
      }
      return offer;
   }



   /// <summary>
   /// The read itself. The session it creates becomes the held session only when a method is offered; in every other
   /// case, a failure and a cancellation included, it is discarded before this returns or throws. Taking the session
   /// over and announcing it are outside the protected part, so a subscriber that throws while the result is
   /// announced has not made the read fail.
   /// </summary>
   private async Task<SignInOffer?> ReadDescriptionAsync(Uri server, ServerCheckResult check, QqqCertificateExceptionOrigins certificateException, CancellationToken token)
   {
      DropSession();
      ISignInSession? session = null;
      QqqAuthenticationMetadata? description = null;
      SignInOffer? offer = null;
      try
      {
         _log.Info("Reading the sign-in description of {Server} as this Windows account; an untrusted certificate is accepted from {Exception}.", QqqOrigin.ShownAuthority(server), certificateException);
         session = _sessions.Create(server, certificateException);
         description = await session.DiscoverAsync(token);
         token.ThrowIfCancellationRequested();
         offer = Judge(session, description, check);
      }
      catch(Exception exception) when(Reportable(exception, token) && exception is IOException or ArgumentException or InvalidOperationException or HttpRequestException or OperationCanceledException)
      {
         Fail("The sign-in description could not be read", CompanionSignInState.PrepareFailed, session, null, exception);
      }
      finally
      {
         if(offer is null)
         {
            session?.Dispose();
         }
      }
      return offer is null ? null : Adopt(session!, description!, offer);
   }



   /// <summary>
   /// Judges a description that was read and returns what is offered, or null when nobody can sign in with it, having
   /// said why. The provider it names is compared with the one the service's check reported, by the rule the check
   /// applies (a table-based server names none), and a difference stops the sign-in: a certificate exception the
   /// operator accepted covers the provider that was shown, not another. Then the methods are settled: those the
   /// check and this read both found, with a difference logged.
   /// </summary>
   private SignInOffer? Judge(ISignInSession session, QqqAuthenticationMetadata description, ServerCheckResult check)
   {
      string server = QqqOrigin.ShownAuthority(session.Origin);
      var read = SignInOffer.Read(description);
      Uri? provider = read.Password ? null : description.ProviderOrigin;
      Uri? reported = Uri.TryCreate(check.ProviderOrigin, UriKind.Absolute, out Uri? named) ? QqqOrigin.Of(named) : null;
      if(!Equals(provider, reported))
      {
         _log.Warning("The sign-in provider in the sign-in description of {Server} is {Read}, and the service's check reported {Checked}. Nobody is signed in until the server has been checked again.", server, Shown(provider), Shown(reported));
         Settle(CompanionSignInState.ProviderChanged);
         return null;
      }
      var found = SignInOffer.Checked(check);
      SignInOffer offer = found.Common(read);
      if(found == read)
      {
         _log.Info("Sign-in methods for {Server}: {Offer}.", server, offer);
      }
      else
      {
         _log.Warning("Sign-in methods for {Server}: the service's check found {Checked} and this account's read found {Read}, so {Offer} is offered.", server, found, read, offer);
      }
      if(!offer.Any)
      {
         Settle(CompanionSignInState.NothingOffered);
         return null;
      }
      return offer;
   }



   /// <summary>Takes over the session a method is offered on, with the callbacks its description lists for a browser sign-in, and announces that a sign-in can start.</summary>
   private SignInOffer Adopt(ISignInSession session, QqqAuthenticationMetadata description, SignInOffer offer)
   {
      _session = session;
      _redirects = description.Values?.NativeSessionLogin?.RedirectUris ?? [];
      OnPropertyChanged(nameof(HasSession));
      Offer = offer;
      Settle(CompanionSignInState.Ready);
      return offer;
   }



   /// <summary>
   /// Signs in with a user name and password and returns the session credential. Nothing happens without a session
   /// that offers a password. The password is passed straight to the session and never kept or logged. A failure is
   /// reported by state, with the session's own reason, which does not say which part (name, password, permission)
   /// was wrong, and the session stays for another attempt. It returns null when no credential was obtained: the
   /// sign-in failed, the caller stopped waiting, or nothing was attempted.
   /// </summary>
   public async Task<QqqSessionCredential?> SignInWithPasswordAsync(string? username, string password, CancellationToken cancellationToken = default)
   {
      if(_session is not { } session || _offer is not { Password: true } || !TryBegin("Password sign-in"))
      {
         return null;
      }
      return await RunAsync("Password sign-in", CompanionSignInState.SigningIn, token => TrySignInWithPasswordAsync(session, (username ?? string.Empty).Trim(), password, token), cancellationToken);
   }



   /// <summary>
   /// The server's part of a password sign-in. A failure of it, the server's own time limit included, is reported as
   /// not completed. A wait that the caller or disposal ended is not a failure and is left to the runner, and no
   /// credential is taken once the wait has ended. Announcing the result is outside the protected part: once the
   /// server has accepted the sign-in, a subscriber that throws while it is announced has not made the sign-in fail.
   /// </summary>
   private async Task<QqqSessionCredential?> TrySignInWithPasswordAsync(ISignInSession session, string username, string password, CancellationToken token)
   {
      QqqCertificateRejection? known = session.LastCertificateRejection;
      QqqSessionCredential credential;
      try
      {
         _log.Info("Password sign-in submitted to {Server}.", QqqOrigin.ShownAuthority(session.Origin));
         await session.SignInWithPasswordAsync(username, password, token);
         token.ThrowIfCancellationRequested();
         credential = session.ExportCredential();
      }
      catch(Exception exception) when(Reportable(exception, token) && exception is IOException or ArgumentException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
      {
         Fail("Password sign-in not completed", CompanionSignInState.PasswordFailed, session, known, exception);
         return null;
      }
      return SignedIn(session, credential);
   }



   /// <summary>
   /// Discards the session, if any, so a later sign-in starts with a new preparation. While an operation is running
   /// the session is in use, so this cancels the operation instead, and the operation discards the session as it
   /// ends: discarding a session under a call that is still using it makes that call fail in ways that look like
   /// errors. Either way no session is held once the running operation, if any, has ended. "Running" is decided by
   /// the operation's own wait, which exists exactly while the operation can still discard the session for us, and
   /// not by <see cref="IsBusy"/>, which stays true a little longer: an operation that is being released announces
   /// how it ended before it lets go, and a subscriber that calls this from that announcement finds no call using
   /// the session any more, so the session is discarded at once. Leaving it to the operation then would leave it
   /// to nobody.
   /// </summary>
   public void Discard()
   {
      if(_disposed)
      {
         return;
      }
      if(_operation is { } running)
      {
         _discarding = true;
         running.Cancel();
         return;
      }
      DropSession();
      Settle(CompanionSignInState.NotPrepared);
   }



   /// <summary>
   /// Ends the sign-in. It only cancels: an operation that is still running sees the cancellation, ends quietly and
   /// discards the session itself. When nothing is running the session is discarded here, and "running" is decided
   /// as it is in <see cref="Discard"/>, by the operation's own wait: a subscriber that disposes the sign-in while
   /// an operation that has ended is being released finds no call using the session, and nothing left that would
   /// discard it later. After this nothing is announced.
   /// </summary>
   public void Dispose()
   {
      if(_disposed)
      {
         return;
      }
      _disposed = true;
      _lifetime.Cancel();
      if(_operation is null)
      {
         DropSession();
      }
      _lifetime.Dispose();
   }



   /// <summary>Announces a change, unless the sign-in was disposed: a view that has closed must not be called back by an operation that was still finishing.</summary>
   protected override void OnPropertyChanged(PropertyChangedEventArgs e)
   {
      if(!_disposed)
      {
         base.OnPropertyChanged(e);
      }
   }



   /// <summary>Announces that a property is about to change, unless the sign-in was disposed, for the same reason: "nothing is announced after disposal" holds for both of the events a view can subscribe to.</summary>
   protected override void OnPropertyChanging(PropertyChangingEventArgs e)
   {
      if(!_disposed)
      {
         base.OnPropertyChanging(e);
      }
   }



   /// <summary>Records that the server accepted the user and returns the credential to hand to the caller.</summary>
   private QqqSessionCredential SignedIn(ISignInSession session, QqqSessionCredential credential)
   {
      _log.Info("Sign-in accepted by {Server}; the session credential goes to the caller and is not kept here.", QqqOrigin.ShownAuthority(session.Origin));
      Settle(CompanionSignInState.SignedIn);
      return credential;
   }



   /// <summary>
   /// Claims the sign-in for one operation. After it was disposed it refuses silently. While another operation is
   /// running it refuses and logs that, so a request that did nothing is never silent in the log; the caller sees the
   /// state of the operation that is running.
   /// </summary>
   private bool TryBegin(string operation)
   {
      if(_disposed)
      {
         return false;
      }
      if(_operating)
      {
         _log.Warning("{Operation} was not started: another sign-in operation is still running.", operation);
         return false;
      }
      _operating = true;
      return true;
   }



   /// <summary>
   /// Runs one claimed operation and always releases the sign-in afterwards, whatever the operation or a subscriber
   /// does. The operation's token ends when the caller's does, when the session is discarded and when the sign-in is
   /// disposed. An operation that ended for one of those reasons is not a failure: it is noted as debug detail and
   /// null is returned.
   /// </summary>
   private async Task<T?> RunAsync<T>(string operation, CompanionSignInState running, Func<CancellationToken, Task<T?>> body, CancellationToken cancellationToken)
      where T : class
   {
      CancellationTokenSource? wait = null;
      try
      {
         wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
         _operation = wait;
         OnPropertyChanged(nameof(IsBusy));
         Settle(running);
         wait.Token.ThrowIfCancellationRequested();
         return await body(wait.Token);
      }
      catch(Exception exception) when(_disposed || exception is OperationCanceledException && wait is { IsCancellationRequested: true })
      {
         _log.Debug("{Operation} ended early because {Reason} ({Failure}).", operation, _disposed ? "the sign-in was closed" : _discarding ? "the session was discarded" : "its caller stopped waiting", exception.GetType().Name);
         return null;
      }
      finally
      {
         End(wait);
      }
   }



   /// <summary>
   /// Releases the sign-in after an operation. A sign-in that was disposed, or whose session was asked to be
   /// discarded, while the operation ran discards the session here, now that no call is using it. An operation that
   /// ended without an outcome of its own (its caller stopped waiting, or it threw something unexpected) leaves the
   /// state that matches what is held; when it was a browser sign-in that had already handed its callback to the
   /// session, the session is discarded first, as it is for one the operator cancelled. The state is settled first
   /// and the sign-in released after it, so a subscriber that starts the next operation when the sign-in becomes
   /// free does not have its state overwritten, and the release happens even when a subscriber throws.
   /// </summary>
   private void End(CancellationTokenSource? wait)
   {
      try
      {
         _operation = null;
         if(_disposed || _discarding)
         {
            _discarding = false;
            DropSession();
            Settle(CompanionSignInState.NotPrepared);
         }
         else if(_state is CompanionSignInState.Preparing or CompanionSignInState.SigningIn or CompanionSignInState.WaitingForBrowser)
         {
            DropHandedOverSession();
            Settle(_session is null ? CompanionSignInState.NotPrepared : CompanionSignInState.Ready);
         }
      }
      finally
      {
         _handedOver = false;
         wait?.Dispose();
         _operating = false;
         OnPropertyChanged(nameof(IsBusy));
      }
   }



   /// <summary>
   /// Whether a failure is one to report. A failure seen after the sign-in was disposed never is, and neither is the
   /// cancellation that the operation's own token asked for. A cancellation nobody here asked for, such as the
   /// server's own time limit, is a failure like any other.
   /// </summary>
   private bool Reportable(Exception exception, CancellationToken token)
      => !_disposed && !(exception is OperationCanceledException && token.IsCancellationRequested);



   /// <summary>
   /// Reports a step that failed. The failure is logged by type and message, with the full exception as debug
   /// detail. When the session refused a certificate during the step (its last refusal is not the one known before
   /// the step), the certificate is why: the state says whose it was, the server's own or the sign-in provider's,
   /// and the refusal is kept for the caller to show. Any other failure settles in the given state with its reason.
   /// </summary>
   private void Fail(string context, CompanionSignInState state, ISignInSession? session, QqqCertificateRejection? known, Exception exception)
   {
      _log.Error("{Context}: {Failure}", context, QqqSessionDiagnostics.Describe(exception));
      _log.Debug("{Context} detail: {Exception}", context, exception);
      if(session?.LastCertificateRejection is not { } rejection || ReferenceEquals(rejection, known))
      {
         Settle(state, QqqSessionDiagnostics.Explain(exception));
         return;
      }
      bool own = Equals(rejection.Origin, QqqOrigin.Of(session.Origin));
      _log.Warning(own
         ? "This Windows account refused the certificate presented by the server ({Presenter}; {Errors}). The service checks certificates under its own account, so it can accept one that this account does not."
         : "This Windows account refused the certificate presented by the sign-in provider ({Presenter}; {Errors}).", Shown(rejection.Origin), rejection.Errors);
      Settle(own ? CompanionSignInState.AccountRefusedCertificate : CompanionSignInState.ProviderCertificateRefused, rejection: rejection);
   }



   /// <summary>Sets how things stand: the reason and the certificate first, the state last, so a subscriber that reacts to the state finds what goes with it.</summary>
   private void Settle(CompanionSignInState state, string? failure = null, QqqCertificateRejection? rejection = null)
   {
      Failure = failure;
      Rejection = rejection;
      State = state;
   }



   /// <summary>Discards the session, if any, and withdraws what it offered, so no half-finished attempt can be continued and nothing is offered that cannot work.</summary>
   private void DropSession()
   {
      _session?.Dispose();
      _session = null;
      _redirects = [];
      Offer = null;
      OnPropertyChanged(nameof(HasSession));
   }



   /// <summary>An origin as a log line names it: its host and port, which an origin holds in plain ASCII, or words when there is none.</summary>
   private static string Shown(Uri? origin) => origin is null ? "none" : origin.Authority;
}
