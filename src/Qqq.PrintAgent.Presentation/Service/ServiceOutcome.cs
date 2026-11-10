/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>The ways a request to the local service can end, so a caller chooses its message from a value instead of matching reply text or catching exceptions.</summary>
public enum ServiceOutcome
{
   /// <summary>The service answered and the answer was the expected kind; the payload is available.</summary>
   Success,

   /// <summary>The service answered, but with an error or a refusal; its message is available.</summary>
   Rejected,

   /// <summary>No usable answer arrived: the service could not be reached, timed out, was not trusted or sent something unreadable.</summary>
   Unavailable,

   /// <summary>The caller's own cancellation token ended the wait. This is not a failure of the service, which may still carry the request out unless the wait had ended before anything was sent.</summary>
   Canceled,
}
