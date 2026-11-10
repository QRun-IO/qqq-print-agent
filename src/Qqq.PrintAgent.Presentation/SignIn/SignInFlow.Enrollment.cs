/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// Enrollment part of the flow: pairing this computer with a server by an enrollment code. The service does the
/// enrollment and keeps the device credential; the flow only checks the form, sends the request and words the result.
/// </summary>
public sealed partial class SignInFlow
{
   /// <summary>
   /// Checks the enrollment form before anything is sent: the address must be an absolute HTTPS address (plain HTTP only for
   /// this computer itself, for development) without user information, query or fragment, and a code must be entered. When
   /// the form is not acceptable it reports what to correct and returns false.
   /// </summary>
   public bool CheckEnrollment(string? address, string? code)
   {
      if(Uri.TryCreate((address ?? string.Empty).Trim(), UriKind.Absolute, out Uri? server)
         && (string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || (server.IsLoopback && string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
         && string.IsNullOrEmpty(server.UserInfo) && string.IsNullOrEmpty(server.Query) && string.IsNullOrEmpty(server.Fragment)
         && !string.IsNullOrWhiteSpace(code))
      {
         return true;
      }
      Report(SignInMessages.CheckEnrollmentDetails);
      return false;
   }



   /// <summary>
   /// Enrolls this device through the service with an address and code that passed <see cref="CheckEnrollment"/>. A
   /// refusal shows the service's reply. A request that left without a usable answer (a reply time limit, a pipe that closed
   /// mid-reply, a wait the caller ended) says the code may have been used and must be checked on the server before another
   /// attempt. A request that never left is reported to the caller as an unreachable service, because the code was not
   /// used. The code is sent once and not kept. While another operation is running the request is refused and nothing happens.
   /// </summary>
   public async Task<EnrollmentResult> EnrollAsync(string address, string code, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(address);
      ArgumentNullException.ThrowIfNull(code);
      SignInStage before = Stage;
      if(!TryBegin(SignInStage.Enrolling))
      {
         return new EnrollmentResult(null, ServiceReachable: true);
      }
      using CancellationTokenSource wait = Link(cancellationToken);
      try
      {
         Report(SignInMessages.Enrolling);
         ServiceResult<string> result = await _service.EnrollAsync(address.Trim().TrimEnd('/'), code.Trim(), wait.Token);
         if(result.Succeeded)
         {
            Report(SignInMessages.DeviceConnected(result.Payload));
            _log.Info("Print device enrollment completed.");
            return new EnrollmentResult(result.Payload, ServiceReachable: true);
         }
         if(result.Reply is string reply)
         {
            Report(SignInMessages.EnrollmentFailed(reply));
            return new EnrollmentResult(null, ServiceReachable: true);
         }
         if(result.Unconfirmed)
         {
            Report(SignInMessages.EnrollmentUnconfirmed);
            return new EnrollmentResult(null, ServiceReachable: true);
         }
         return new EnrollmentResult(null, ServiceReachable: false);
      }
      finally
      {
         End(before);
      }
   }
}
