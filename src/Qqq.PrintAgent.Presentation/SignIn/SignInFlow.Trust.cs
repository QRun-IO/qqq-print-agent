/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// Certificate-exception part of the flow. An administrator can let the service and the companion accept an untrusted
/// certificate from one development server; the exception is saved by the service, bound to that server's origin, and
/// discovery applies it only to exactly that server.
/// </summary>
public sealed partial class SignInFlow
{
   /// <summary>The certificate exception as the service last confirmed it. A change is announced whenever it is read or a save was attempted, even when the value is the same, so the view puts its checkbox back to the saved state.</summary>
   public ServerTrust Trust => _trust;



   /// <summary>
   /// False from a save that was sent and not answered until the setting has been read from the service again. While it is
   /// false the flow does not know what the service holds: <see cref="Trust"/> is then "no exception", so discovery applies
   /// none, and a view must show the checkbox as undetermined instead of in a state the service may not hold.
   /// </summary>
   public bool TrustKnown => _trustKnown;



   /// <summary>
   /// Reads the saved exception from the service. When the service cannot be reached or refuses, no exception is assumed and
   /// the reason is logged. While another operation is running, or when the flow was disposed during the read, the value
   /// already held is kept. Only an answer makes an unconfirmed setting known again; a read that fails leaves it unknown.
   /// </summary>
   public async Task LoadTrustAsync()
   {
      if(!TryBegin("Reading the certificate exception"))
      {
         return;
      }
      _ = await RunAsync("Reading the certificate exception", stage: null, async token =>
      {
         ServiceResult<ServerTrust> result = await _service.GetServerTrustAsync(token);
         ThrowIfDisposed();
         if(result.Outcome == ServiceOutcome.Rejected)
         {
            _log.Warning("Could not read the server trust setting: " + result.Reply);
         }
         else if(!result.Succeeded)
         {
            _log.Error("Could not read the server trust setting from the service: " + result.Message);
         }
         ShowTrust(result.Payload ?? new ServerTrust(), known: result.Succeeded || _trustKnown);
         return true;
      }, stopped: true, () => Stage, CancellationToken.None);
   }



   /// <summary>
   /// Reads the setting again when the last save could not be confirmed, so the view can show what the service really
   /// holds. A view calls this whenever the service has just answered something else; it does nothing while the setting
   /// is known or while another operation is running, so it is safe to call on every status refresh.
   /// </summary>
   public Task ReadUnconfirmedTrustAsync() => _trustKnown || _operating ? Task.CompletedTask : LoadTrustAsync();



   /// <summary>
   /// Saves the exception for the entered server, or removes it, and logs the change loudly. Allowing needs a server address,
   /// because the exception is bound to one server. It returns false only when the request never left this computer, which
   /// the caller shows as an unavailable service, because nothing was changed. In every case, including a save that was
   /// refused because another operation is running (which is also said and logged), the saved state is announced again, so
   /// a checkbox that was clicked but not saved goes back. A save that left without a usable answer may have been carried
   /// out: it is reported as "could not be confirmed", the setting becomes unknown (see <see cref="TrustKnown"/>) and is
   /// not shown in either state until it has been read again.
   /// </summary>
   public async Task<bool> SetTrustAsync(bool allow, string? address)
   {
      Uri? origin = null;
      if(allow && !Uri.TryCreate((address ?? string.Empty).Trim(), UriKind.Absolute, out origin))
      {
         OnPropertyChanged(nameof(Trust));
         Report(SignInMessages.EnterAddressFirst);
         return true;
      }
      if(!TryBegin("Saving the certificate exception"))
      {
         OnPropertyChanged(nameof(Trust));
         return true;
      }
      return await RunAsync("Saving the certificate exception", stage: null, async token =>
      {
         ServerTrust requested = origin is null ? new ServerTrust() : new ServerTrust(true, ServerTrust.Normalize(origin));
         ServiceResult<string> result = await _service.SetServerTrustAsync(requested, token);
         ThrowIfDisposed();
         return ApplyTrustResult(requested, result);
      }, stopped: true, () => Stage, CancellationToken.None);
   }



   /// <summary>
   /// Takes the service's answer to a save. Saved: the new exception. Refused, or never sent: the state held before,
   /// announced again, with the refusal shown or the failure logged. Sent and not answered: the service may hold either
   /// value, so the setting becomes unknown, the flow itself falls back to "no exception", and the operator is told the
   /// change could not be confirmed.
   /// </summary>
   private bool ApplyTrustResult(ServerTrust requested, ServiceResult<string> result)
   {
      if(result.Unconfirmed)
      {
         string cause = SignInMessages.Unanswered(result);
         _log.Error("The certificate exception could not be confirmed, so the service may or may not have saved the change: " + cause);
         ShowTrust(new ServerTrust(), known: false);
         Report(SignInMessages.TrustUnconfirmed(cause));
         return true;
      }
      if(!result.Succeeded)
      {
         OnPropertyChanged(nameof(Trust));
         if(result.Reply is string reply)
         {
            Report(SignInMessages.TrustNotSaved(reply));
            return true;
         }
         _log.Error("Server trust setting could not be saved: " + result.Message);
         return false;
      }
      ShowTrust(requested, known: true);
      if(requested.AllowUntrustedCertificate)
      {
         _log.Warning("Untrusted server certificates ALLOWED by the operator for " + requested.Origin + " (development only).");
         Report(SignInMessages.TrustAllowed(requested.Origin));
      }
      else
      {
         _log.Info("Untrusted server certificates refused again for every server (default).");
         Report(SignInMessages.TrustRefused);
      }
      return true;
   }



   /// <summary>Takes a trust value, and whether it is what the service is known to hold, and always announces it.</summary>
   private void ShowTrust(ServerTrust trust, bool known)
   {
      _trust = trust;
      _trustKnown = known;
      OnPropertyChanged(nameof(Trust));
   }
}
