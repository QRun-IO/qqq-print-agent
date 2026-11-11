/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>The five steps of the connection wizard, in the order an operator who adds a server goes through them (ADR-0005).</summary>
public enum WizardStep
{
   /// <summary>The server's address and name, and the compatibility check.</summary>
   Server,

   /// <summary>Signing in, in the companion, as the user; a new server is stored when it succeeds.</summary>
   SignIn,

   /// <summary>Pairing this computer with the server.</summary>
   Pairing,

   /// <summary>Choosing the Windows printers to share with the server.</summary>
   Printers,

   /// <summary>What was set up, and the way to the server's pages.</summary>
   Done,
}
