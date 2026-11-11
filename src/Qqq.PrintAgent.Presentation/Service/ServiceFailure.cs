/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// What went wrong when no usable answer arrived from the local service. It names the cause only. Whether the service may
/// have acted is a separate question, answered by <see cref="ServiceResult{T}.Unconfirmed"/>, because the same cause (a
/// time limit, a broken pipe) can strike before the request was sent or after.
/// </summary>
public enum ServiceFailure
{
   /// <summary>The request did not fail this way: the service answered, or the caller stopped waiting.</summary>
   None,

   /// <summary>A time limit ran out: the connect limit when nothing is listening, or the request's own limit while waiting for the reply.</summary>
   TimedOut,

   /// <summary>The pipe could not be opened, or it closed before a complete reply arrived, or it was closed under the call.</summary>
   Disconnected,

   /// <summary>Windows refused access, or the process at the other end was not verified as the installed service; nothing was sent.</summary>
   AccessDenied,

   /// <summary>A Windows call failed. When the result is not unconfirmed it failed before the request was sent; otherwise when it failed is not known, and the request may have left.</summary>
   Native,

   /// <summary>A pipe message was larger than the permitted size: the request before it was sent, or the reply.</summary>
   InvalidData,

   /// <summary>A reply arrived but was not the data this version expects, which usually means the companion and service versions differ.</summary>
   UnreadableReply,

   /// <summary>
   /// The reply to a version 2 request was not JSON, which is what a service that speaks version 1 of the management pipe
   /// answers; the service has to be updated to this companion's version. A version 1 service knows none of version 2's
   /// operations and answers each with an error before it does anything (run against this product's version 1 runtime
   /// with every version 2 request). But text that is not JSON is also what any answer outside the envelope looks
   /// like, so the cause does not prove that nothing was carried out: the result is unconfirmed unless the operation
   /// is a read (<see cref="ServiceResult{T}.Unconfirmed"/>).
   /// </summary>
   OlderService,

   /// <summary>
   /// The reply was in a version of the management pipe newer than this companion speaks; the companion has to be updated
   /// to the service's version. A service that keeps the contract gives one reply in another version than it was asked
   /// in, its refusal of the version, but a reply this companion cannot read cannot show that it is that one. So the
   /// cause does not prove that nothing was carried out: the result is unconfirmed unless the operation is a read
   /// (<see cref="ServiceResult{T}.Unconfirmed"/>).
   /// </summary>
   NewerService,
}
