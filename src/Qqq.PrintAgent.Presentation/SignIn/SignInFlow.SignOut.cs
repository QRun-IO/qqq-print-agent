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
   /// Asks the service to sign out and forget its session, then discards the flow's own session. It returns false only when
   /// the service could not be reached, in which case nothing was changed and the caller shows its service-unavailable state.
   /// A service error is shown as a warning; a provider sign-out is opened and its confirmation awaited.
   /// </summary>
   public async Task<bool> SignOutAsync()
   {
      _log.Info("Sign-out requested; asking the service to end the session.");
      ServiceResult<PipeSignOutResult> result = await _service.SignOutAsync(_callbacks.RedirectUri);
      if(result.Outcome == ServiceOutcome.Unavailable)
      {
         _log.Error("Sign-out could not reach the service: " + result.Message);
         return false;
      }
      DropSession();
      Settle(confirmed: false);
      bool rejected = !result.Succeeded;
      string message = result.Payload?.Message ?? result.Reply ?? string.Empty;
      QqqProviderLogout? providerLogout = result.Payload?.ProviderLogout;
      _log.Info("Service sign-out result: " + message + (providerLogout is null ? "" : " The identity provider sign-out is ready; opening it in the browser."));
      if(providerLogout is null)
      {
         Report(SignInMessages.SignedOut(message, rejected));
         return true;
      }
      Report(SignInMessages.SigningOutOfProvider(message, rejected));
      Report(SignInMessages.ProviderSignedOut(message, await CompleteProviderSignOutAsync(providerLogout)));
      return true;
   }



   /// <summary>
   /// Opens the identity provider's end-session page in the default browser and waits up to two minutes for the browser to return
   /// to this application's registered callback with this sign-out's own state. The inbox is opened first so a fast redirect is not lost.
   /// The request carries a sensitive token hint, so only its host is logged. Returns whether the provider confirmed.
   /// </summary>
   private async Task<bool> CompleteProviderSignOutAsync(QqqProviderLogout logout)
   {
      try
      {
         using IBrowserCallbackInbox inbox = _callbacks.Open();
         using CancellationTokenSource limit = new(ProviderSignOutLimit, _time);
         _log.Info("Opening the identity provider's sign-out page in the default browser (" + logout.EndSessionUri.Authority + ").");
         _browser.Open(logout.EndSessionUri);
         Uri callback = await inbox.ReceiveAsync(limit.Token);
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
