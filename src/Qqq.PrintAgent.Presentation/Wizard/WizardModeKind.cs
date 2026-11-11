/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>What a run of the connection wizard is for.</summary>
public enum WizardModeKind
{
   /// <summary>A new server is added to this computer.</summary>
   AddServer,

   /// <summary>A server that exists is signed in to again.</summary>
   Repair,

   /// <summary>A server that exists and is signed in is paired with this computer.</summary>
   Pair,
}
