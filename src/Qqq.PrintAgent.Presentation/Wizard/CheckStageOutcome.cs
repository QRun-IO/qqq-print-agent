/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>How one stage of the compatibility check went. The view shows it as a glyph and as the row's words, never as a color alone.</summary>
public enum CheckStageOutcome
{
   /// <summary>The service gave no outcome for this stage, or one this version does not know.</summary>
   Unknown,

   /// <summary>The stage passed.</summary>
   Passed,

   /// <summary>The stage failed; the row lists why.</summary>
   Failed,

   /// <summary>The stage was not checked, because an earlier one failed.</summary>
   NotReached,
}
