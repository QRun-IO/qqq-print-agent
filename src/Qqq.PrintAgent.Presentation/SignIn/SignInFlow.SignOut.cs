/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// Sign-out part of the flow. The identity provider is meant to be invisible, so when the server offers the provider's
/// own sign-out it is opened in the browser too: staying signed in to the provider would let the next sign-in skip the
/// credential prompt. Pairing and job history are not removed by signing out.
/// </summary>
public sealed partial class SignInFlow
{
   /// <summary>How long the flow waits for the identity provider to confirm its sign-out.</summary>
   public static readonly TimeSpan ProviderSignOutLimit = TimeSpan.FromMinutes(2);



   /// <summary>
   /// Asks the service to sign out and forget its session, then discards the flow's own session and what it offered. It
   /// returns false only when the request never left this computer, and the caller then shows its service-unavailable
   /// state, because nothing was changed. A request that left without a usable answer is not "unavailable": the operator
   /// is told the sign-out could not be confirmed and that the session may have ended. It returns true in every other
   /// case too, including a request that was refused because another operation is running. A service error is shown as a
   /// warning; a provider sign-out is opened and its confirmation awaited.
   /// </summary>
   public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
   {
      SignInStage before = Stage;
      if(!TryBegin("Sign-out"))
      {
         return true;
      }
      bool answered = false;
      return await RunAsync("Sign-out", SignInStage.SigningOut, async token =>
      {
         _log.Info("Sign-out requested; asking the service to end the session.");
         ServiceResult<PipeSignOutResult> result = await _service.SignOutAsync(_callbacks.RedirectUri, token);
         ThrowIfDisposed();
         if(result.Reply is not string reply)
         {
            return SignOutUnanswered(result);
         }
         answered = true;
         DropSession();
         Readiness = null;
         await ReportSignOutAsync(result, reply, token);
         return true;
      }, stopped: true, () => answered ? SignInStage.Idle : before, cancellationToken);
   }



   /// <summary>
   /// Handles a sign-out that got no usable answer. When nothing was sent, nothing was changed, and the caller is told to
   /// show the service as unavailable. When the request left, the session may have ended anyway: that is logged with what
   /// really happened (no answer in time, a closed connection, an answer that could not be read, a wait that was ended)
   /// and shown as "could not be confirmed", which is not the same as the service being unavailable.
   /// </summary>
   private bool SignOutUnanswered(ServiceResult<PipeSignOutResult> result)
   {
      if(!result.Unconfirmed)
      {
         _log.Error("Sign-out could not reach the service; nothing was changed: " + result.Message);
         return false;
      }
      string cause = SignInMessages.Unanswered(result);
      _log.Error("Sign-out could not be confirmed, so the session may or may not have ended: " + cause);
      Report(SignInMessages.SignOutUnconfirmed(cause));
      return true;
   }



   /// <summary>Shows the service's answer to the sign-out and, when the server supplied the identity provider's own sign-out, opens it and reports whether the provider confirmed.</summary>
   private async Task ReportSignOutAsync(ServiceResult<PipeSignOutResult> result, string reply, CancellationToken cancellationToken)
   {
      bool rejected = !result.Succeeded;
      string message = result.Payload?.Message ?? reply;
      QqqProviderLogout? providerLogout = result.Payload?.ProviderLogout;
      _log.Info("Service sign-out result: " + message + (providerLogout is null ? "" : " The identity provider sign-out is ready; opening it in the browser."));
      if(providerLogout is null)
      {
         Report(SignInMessages.SignedOut(message, rejected));
         return;
      }
      Report(SignInMessages.SigningOutOfProvider(message, rejected));
      Report(SignInMessages.ProviderSignedOut(message, await CompleteProviderSignOutAsync(providerLogout, cancellationToken)));
   }



   /// <summary>
   /// Opens the identity provider's end-session page in the default browser and waits up to two minutes for the browser to return
   /// to this application's registered callback with this sign-out's own state. The inbox is opened first so a fast redirect is not lost.
   /// The request carries a sensitive token hint, so only its host is logged, and a browser that cannot be started is logged by
   /// the kind of failure only, because Windows' own text for it names the whole request (see <see cref="BrowserLaunch"/>).
   /// Returns whether the provider confirmed, did not confirm, or was never asked because the browser could not be
   /// started, which the operator is told as what it is (an inbox that could not be opened is still returned as not
   /// confirmed, as it was before); the wait
   /// also ends when the caller's token does. When the flow is disposed the wait ends without a result, and that is left
   /// to the runner as a quiet end instead of being reported as an unconfirmed sign-out.
   /// </summary>
   private async Task<ProviderSignOutResult> CompleteProviderSignOutAsync(QqqProviderLogout logout, CancellationToken cancellationToken)
   {
      try
      {
         using IBrowserCallbackInbox inbox = _callbacks.Open();
         using CancellationTokenSource limit = new(ProviderSignOutLimit, _time);
         using var wait = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);
         _log.Info("Opening the identity provider's sign-out page in the default browser (" + QqqOrigin.ShownAuthority(logout.EndSessionUri) + ").");
         try
         {
            BrowserLaunch.Open(_browser, logout.EndSessionUri);
         }
         catch(IOException exception) when(!_disposed)
         {
            _log.Warning("Identity provider sign-out was not confirmed: " + QqqSessionDiagnostics.Describe(exception));
            return ProviderSignOutResult.NotOpened;
         }
         Uri callback = await inbox.ReceiveAsync(wait.Token);
         ThrowIfDisposed();
         bool matched = logout.Matches(callback);
         _log.Info("Identity provider sign-out callback " + (matched ? "matched." : "did not match this sign-out."));
         return matched ? ProviderSignOutResult.Confirmed : ProviderSignOutResult.NotConfirmed;
      }
      catch(Exception exception) when(!_disposed && exception is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
      {
         _log.Warning("Identity provider sign-out was not confirmed: " + QqqSessionDiagnostics.Describe(exception));
         return ProviderSignOutResult.NotConfirmed;
      }
   }
}
