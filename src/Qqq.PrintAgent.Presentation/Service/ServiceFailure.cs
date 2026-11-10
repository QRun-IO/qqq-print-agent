/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>Why no usable answer arrived from the local service. Each value stands for one kind of failure the pipe client raises, so callers can tell a timeout (the request may have been carried out) from a refusal (it was not).</summary>
public enum ServiceFailure
{
   /// <summary>The request did not fail this way: the service answered.</summary>
   None,

   /// <summary>The pipe's own time limit ran out. The service may still have carried out the request.</summary>
   TimedOut,

   /// <summary>The caller's own cancellation token stopped the wait. The service may still have carried out the request.</summary>
   Canceled,

   /// <summary>The pipe could not be opened or closed before a complete reply arrived.</summary>
   Disconnected,

   /// <summary>Windows refused access, or the process at the other end was not the installed service; nothing was sent.</summary>
   AccessDenied,

   /// <summary>A Windows call made while verifying the other end of the pipe failed.</summary>
   Native,

   /// <summary>A pipe message was larger than the permitted size.</summary>
   InvalidData,

   /// <summary>A reply arrived but was not the data this version expects, which usually means the companion and service versions differ.</summary>
   UnreadableReply,
}
