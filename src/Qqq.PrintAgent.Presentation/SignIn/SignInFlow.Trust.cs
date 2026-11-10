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
   /// Reads the saved exception from the service. When the service cannot be reached or refuses, no exception is assumed and
   /// the reason is logged. While another operation is running, or when the flow was disposed during the read, the value
   /// already held is kept.
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
         ShowTrust(result.Payload ?? new ServerTrust());
         return true;
      }, stopped: true, () => Stage, CancellationToken.None);
   }



   /// <summary>
   /// Saves the exception for the entered server, or removes it, and logs the change loudly. Allowing needs a server address,
   /// because the exception is bound to one server. It returns false only when the service gave no usable answer; in every
   /// case, including a save that was refused because another operation is running (which is also said and logged), the
   /// saved state is announced again, so a checkbox that was clicked but not saved goes back.
   /// </summary>
   public async Task<bool> SetTrustAsync(bool allow, string? address)
   {
      Uri? origin = null;
      if(allow && !Uri.TryCreate((address ?? string.Empty).Trim(), UriKind.Absolute, out origin))
      {
         ShowTrust(_trust);
         Report(SignInMessages.EnterAddressFirst);
         return true;
      }
      if(!TryBegin("Saving the certificate exception"))
      {
         ShowTrust(_trust);
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



   /// <summary>Takes the service's answer to a save: the new exception when it was saved, and otherwise the saved state again with the service's refusal shown or the missing answer logged.</summary>
   private bool ApplyTrustResult(ServerTrust requested, ServiceResult<string> result)
   {
      if(!result.Succeeded)
      {
         ShowTrust(_trust);
         if(result.Reply is string reply)
         {
            Report(SignInMessages.TrustNotSaved(reply));
            return true;
         }
         _log.Error("Server trust setting could not be saved: " + result.Message);
         return false;
      }
      ShowTrust(requested);
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



   /// <summary>Takes a trust value as the saved state and always announces it.</summary>
   private void ShowTrust(ServerTrust trust)
   {
      _trust = trust;
      OnPropertyChanged(nameof(Trust));
   }
}
