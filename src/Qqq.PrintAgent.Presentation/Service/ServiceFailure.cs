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

   /// <summary>The pipe could not be opened, or it closed before a complete reply arrived.</summary>
   Disconnected,

   /// <summary>Windows refused access, or the process at the other end was not verified as the installed service; nothing was sent.</summary>
   AccessDenied,

   /// <summary>A Windows call failed before the request was sent.</summary>
   Native,

   /// <summary>A pipe message was larger than the permitted size: the request before it was sent, or the reply.</summary>
   InvalidData,

   /// <summary>A reply arrived but was not the data this version expects, which usually means the companion and service versions differ.</summary>
   UnreadableReply,

   /// <summary>
   /// A service that speaks version 1 of the management pipe answered a version 2 request: its reply was not JSON, or had no
   /// version. That service knows none of version 2's operations, so it carried nothing out; the service has to be updated
   /// to this companion's version.
   /// </summary>
   OlderService,

   /// <summary>
   /// The reply was in a version of the management pipe newer than this companion speaks. The one reply a service gives in
   /// another version than it was asked in is its refusal of the version, so it carried nothing out; the companion has to
   /// be updated to the service's version.
   /// </summary>
   NewerService,
}
