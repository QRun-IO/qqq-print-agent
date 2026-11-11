/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The machine-wide note of which shortcut file this installer last made, so a later run or an uninstall can find exactly
/// that file. An implementation only keeps the name; when it is read, replaced or forgotten is decided by
/// <see cref="AgentBrandingShortcuts"/>.
/// </summary>
internal interface IShortcutRecord
{
   /// <summary>The recorded file name, or null when none is recorded. With <paramref name="createStore"/> the place the note is kept is created when it is missing, as branding does; without it a missing place simply means nothing is recorded, as uninstall needs.</summary>
   string? ReadLastShortcutName(bool createStore);



   /// <summary>Records the file name of the shortcut just written.</summary>
   void WriteLastShortcutName(string filename);



   /// <summary>Forgets the recorded name; nothing happens when none is recorded.</summary>
   void ClearLastShortcutName();
}
