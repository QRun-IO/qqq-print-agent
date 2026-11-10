/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation;

/// <summary>How serious a feedback message is. The values mirror the four severities of the window's message bar, so the view maps them one to one without the presentation assembly referencing WinUI.</summary>
public enum FeedbackSeverity
{
   /// <summary>Progress or a neutral fact.</summary>
   Informational,

   /// <summary>The requested action completed.</summary>
   Success,

   /// <summary>The operator should check something, but nothing definitely failed.</summary>
   Warning,

   /// <summary>The requested action failed.</summary>
   Error,
}
