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
   /// returns false when no usable answer arrived, and the caller then shows its service-unavailable state: if the request
   /// never left, nothing was changed; if it left, the service may have signed out anyway, and the log says which. It
   /// returns true in every other case, including a request that was refused because another operation is running. A
   /// service error is shown as a warning; a provider sign-out is opened and its confirmation awaited.
   /// </summary>
   public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
   {
      SignInStage before = Stage;
      if(!TryBegin(SignInStage.SigningOut))
      {
         return true;
      }
      bool answered = false;
      using CancellationTokenSource wait = Link(cancellationToken);
      try
      {
         _log.Info("Sign-out requested; asking the service to end the session.");
         ServiceResult<PipeSignOutResult> result = await _service.SignOutAsync(_callbacks.RedirectUri, wait.Token);
         if(result.Reply is not string reply)
         {
            return SignOutUnanswered(result);
         }
         answered = true;
         DropSession();
         Readiness = null;
         await ReportSignOutAsync(result, reply, wait.Token);
         return true;
      }
      finally
      {
         End(answered ? SignInStage.Idle : before);
      }
   }



   /// <summary>Logs a sign-out that got no answer, saying whether the session may have ended anyway, and tells the caller whether to show the service as unavailable.</summary>
   private bool SignOutUnanswered(ServiceResult<PipeSignOutResult> result)
   {
      if(result.Outcome == ServiceOutcome.Canceled)
      {
         _log.Info("Sign-out is no longer waited for; the service may still end the session.");
         return true;
      }
      _log.Error(result.Unconfirmed
         ? "Sign-out was sent, but the service's answer did not arrive, so the session may or may not have ended: " + result.Message
         : "Sign-out could not reach the service; nothing was changed: " + result.Message);
      return false;
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
   /// The request carries a sensitive token hint, so only its host is logged. Returns whether the provider confirmed; the wait
   /// also ends when the caller's token does or the flow is disposed.
   /// </summary>
   private async Task<bool> CompleteProviderSignOutAsync(QqqProviderLogout logout, CancellationToken cancellationToken)
   {
      try
      {
         using IBrowserCallbackInbox inbox = _callbacks.Open();
         using CancellationTokenSource limit = new(ProviderSignOutLimit, _time);
         using var wait = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);
         _log.Info("Opening the identity provider's sign-out page in the default browser (" + logout.EndSessionUri.Authority + ").");
         _browser.Open(logout.EndSessionUri);
         Uri callback = await inbox.ReceiveAsync(wait.Token);
         bool matched = logout.Matches(callback);
         _log.Info("Identity provider sign-out callback " + (matched ? "matched." : "did not match this sign-out."));
         return matched;
      }
      catch(Exception exception) when(exception is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
      {
         _log.Warning("Identity provider sign-out was not confirmed: " + QqqSessionDiagnostics.Describe(exception));
         return false;
      }
   }
}
