/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>The three health colors of the footer dot and the tray icon. The view turns a level into a brush and an icon; the meaning is always also given in words.</summary>
public enum HealthLevel
{
   /// <summary>Connected and ready to print.</summary>
   Green,

   /// <summary>Connected, but a shared printer needs attention or nothing is shared.</summary>
   Orange,

   /// <summary>Disconnected, failing, or holding a job that needs review.</summary>
   Red,
}
