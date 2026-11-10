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
/// It does not own the window's busy flag: the caller serializes operations, and this class only refuses to start one
/// while another of its own is running.
/// </summary>
public sealed partial class SignInFlow : ObservableObject, IDisposable
{
   private readonly ServiceClient _service;
   private readonly ISignInSessionFactory _sessions;
   private readonly IBrowserLauncher _browser;
   private readonly IBrowserCallbackSource _callbacks;
   private readonly IPresentationLog _log;
   private readonly TimeProvider _time;
   private ISignInSession? _session;
   private QqqAuthenticationMetadata? _metadata;
   private SignInStage _stage;
   private bool _passwordOffered;
   private bool _browserOffered;
   private bool _browserPending;
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



   /// <summary>True when the discovered server offers user name and password sign-in, so the password form is shown.</summary>
   public bool PasswordOffered
   {
      get => _passwordOffered;
      private set => SetProperty(ref _passwordOffered, value);
   }



   /// <summary>True when the discovered server offers the native browser grant, so the browser button is shown.</summary>
   public bool BrowserOffered
   {
      get => _browserOffered;
      private set => SetProperty(ref _browserOffered, value);
   }



   /// <summary>True while a browser sign-in is waiting for its callback, which is when it can be cancelled.</summary>
   public bool BrowserPending
   {
      get => _browserPending;
      private set => SetProperty(ref _browserPending, value);
   }



   /// <summary>True while a discovered session exists, which is what a sign-in needs; it says nothing about which methods are offered.</summary>
   public bool HasSession => _session is not null;



   /// <summary>The latest message for the operator. A change is announced every time one is reported, even when it equals the previous one, so the view opens its message bar again.</summary>
   public FeedbackMessage? Feedback => _feedback;



   /// <summary>Asks the entered server which sign-in methods it offers. Any earlier session is discarded first, so each attempt starts clean; a server that offers neither method is reported as needing configuration, and a failure leaves no session behind.</summary>
   public async Task DiscoverAsync(string? address)
   {
      if(IsRunning)
      {
         return;
      }
      PasswordOffered = false;
      BrowserOffered = false;
      Stage = SignInStage.Discovering;
      bool discovered = false;
      try
      {
         DropSession();
         string entered = (address ?? string.Empty).Trim();
         _log.Info("Discover sign-in options requested for '" + entered + "'.");
         if(!Uri.TryCreate(entered, UriKind.Absolute, out Uri? origin))
         {
            _log.Warning("The server address is not an absolute URL (expected https://host[:port]).");
            throw new InvalidOperationException("Enter a valid server origin.");
         }
         _session = _sessions.Create(origin, _trust.AppliesTo(origin));
         _metadata = await _session.DiscoverAsync(CancellationToken.None);
         PasswordOffered = string.Equals(_metadata.Type, "TABLE_BASED", StringComparison.Ordinal);
         BrowserOffered = _metadata.Values?.NativeSessionLogin is not null;
         _log.Info("Sign-in options for " + origin.Authority + ": password sign-in " + (PasswordOffered ? "offered" : "not offered") + ", browser sign-in " + (BrowserOffered ? "offered" : "not offered") + ".");
         Report(PasswordOffered || BrowserOffered ? SignInMessages.OptionsReady : SignInMessages.ConfigurationRequired);
         discovered = true;
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
         Settle(confirmed: false);
      }
   }



   /// <summary>
   /// Signs in with a user name and password, then hands the resulting session to the service. Nothing happens without a
   /// discovered session. The password is passed straight through and never kept, and a failure is reported without saying
   /// which part (credentials, device ownership or permission) was wrong.
   /// </summary>
   public async Task SignInWithPasswordAsync(string? username, string password, PairingChoice pairing)
   {
      if(IsRunning || _session is not { } session)
      {
         return;
      }
      bool confirmed = false;
      Stage = SignInStage.SigningIn;
      try
      {
         _log.Info("Password sign-in submitted for " + session.Origin.Authority + ".");
         Report(SignInMessages.SigningIn);
         await session.SignInWithPasswordAsync((username ?? string.Empty).Trim(), password, CancellationToken.None);
         ServiceResult<string> handover = await HandOverAsync(session, pairing);
         confirmed = handover.Succeeded;
         if(handover.Outcome == ServiceOutcome.Unavailable)
         {
            _log.Error("Password sign-in not completed: the service did not answer (" + handover.Failure + ").");
            Report(SignInMessages.PasswordNotCompleted(handover.Message));
         }
      }
      catch(Exception exception) when(exception is IOException or ArgumentException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
      {
         LogFailure("Password sign-in not completed", exception);
         Report(SignInMessages.PasswordNotCompleted(QqqSessionDiagnostics.Explain(exception)));
      }
      finally
      {
         Settle(confirmed);
      }
   }



   /// <summary>
   /// Hands the session credential, and nothing else, to the service together with the pairing choice. The service verifies
   /// it and keeps it protected; the result is reported as success only when the service confirms it signed in, and its own
   /// sentence is shown either way. When the service did not answer, the caller decides what that means for its kind of sign-in.
   /// </summary>
   private async Task<ServiceResult<string>> HandOverAsync(ISignInSession session, PairingChoice pairing)
   {
      Stage = SignInStage.HandingOver;
      string choice = pairing switch
      {
         PairingChoice.StartNewPairing => "start a new pairing",
         PairingChoice.ClaimLegacyDevice => "associate with the existing pairing",
         _ => "keep the current pairing",
      };
      _log.Info("Sign-in accepted by the server; handing the session to the service (" + choice + ").");
      ServiceResult<string> result = await _service.SignInAsync(session.ExportCredential(), pairing == PairingChoice.ClaimLegacyDevice, pairing == PairingChoice.StartNewPairing);
      if(result.Reply is string reply)
      {
         if(result.Succeeded)
         {
            _log.Info("Service sign-in result: " + reply);
         }
         else
         {
            _log.Error("Service sign-in result: " + reply);
         }
         Report(SignInMessages.HandedOver(reply, result.Succeeded));
      }
      return result;
   }



   /// <summary>Stops everything the flow holds: a waiting browser sign-in is cancelled and the session is discarded.</summary>
   public void Dispose()
   {
      CancelBrowserSignIn();
      DropSession();
   }



   /// <summary>True while one of the flow's own operations is between its first and last step, when another must not start.</summary>
   private bool IsRunning => _stage is SignInStage.Discovering or SignInStage.SigningIn or SignInStage.WaitingForBrowser or SignInStage.HandingOver;



   /// <summary>Leaves an operation: idle when no session is left, signed in when the service confirmed, and otherwise ready for another attempt.</summary>
   private void Settle(bool confirmed)
      => Stage = _session is null ? SignInStage.Idle : confirmed ? SignInStage.SignedIn : SignInStage.Ready;



   /// <summary>Discards the session, if any, so no half-finished attempt can be continued.</summary>
   private void DropSession()
   {
      _session?.Dispose();
      _session = null;
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
}
