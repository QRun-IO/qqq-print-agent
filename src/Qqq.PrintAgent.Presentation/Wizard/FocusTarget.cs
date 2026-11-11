/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>Where the keyboard focus belongs after the wizard changed step or showed a result, so an operator who uses the keyboard or a screen reader lands on what comes next.</summary>
public enum FocusTarget
{
   /// <summary>The first input of the step that is shown; on the Server step that is the address.</summary>
   StepStart,

   /// <summary>The server's name, which has to be corrected.</summary>
   ServerName,

   /// <summary>The step's primary button: Check, Check again or Next.</summary>
   PrimaryAction,

   /// <summary>The list of the check's stages, which says what failed.</summary>
   CheckStages,

   /// <summary>The certificate that was refused.</summary>
   Certificate,

   /// <summary>The button that opens the server this address already is.</summary>
   ExistingServer,
}
