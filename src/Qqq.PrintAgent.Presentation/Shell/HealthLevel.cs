/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The health colors of the footer dot, the tray icon and a server's node, and the level for a health that is not
/// known. The view turns a level into a brush and an icon; the meaning is always also given in words. The window of
/// 1.x reads a health word with <see cref="HealthStatus.From"/>, which never gives <see cref="Unknown"/>.
/// </summary>
public enum HealthLevel
{
   /// <summary>Connected and ready to print.</summary>
   Green,

   /// <summary>Connected, but a shared printer needs attention or nothing is shared.</summary>
   Orange,

   /// <summary>Disconnected, failing, or holding a job that needs review.</summary>
   Red,

   /// <summary>
   /// Not known: the service reported a health this build does not know, the service is not answering, or nothing
   /// has been read yet. It is shown as unknown and never as healthy.
   /// </summary>
   Unknown,
}
