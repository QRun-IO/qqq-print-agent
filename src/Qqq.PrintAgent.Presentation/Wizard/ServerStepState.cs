/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>Where the wizard's Server step is. Next can be used only in <see cref="Passed"/> and <see cref="ExceptionReview"/>.</summary>
public enum ServerStepState
{
   /// <summary>The address and the name are being entered; no result of a check is on screen.</summary>
   Editing,

   /// <summary>The service has been asked to check the server and has not answered yet.</summary>
   Checking,

   /// <summary>Every stage of the check passed, and the address is not a server on this computer yet.</summary>
   Passed,

   /// <summary>Every stage passed under the certificate exception the operator accepted, and the addresses the exception covers are on screen for the operator to see before going on.</summary>
   ExceptionReview,

   /// <summary>The address is already a server on this computer.</summary>
   AlreadyServer,

   /// <summary>The check did not pass, or its result could not be used. The stages are shown when the service gave them.</summary>
   Failed,

   /// <summary>The check did not pass because Windows refused a certificate, which is shown.</summary>
   CertificateRefused,
}
