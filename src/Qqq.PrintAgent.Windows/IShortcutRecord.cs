/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The machine-wide record of the shortcut files this installer may own on the Start menu, so a later run or an uninstall
/// can find exactly those files. It holds two notes: the name of the current shortcut, and the names of any others, which
/// are a new shortcut while it is being written and earlier ones that have not been removed yet. An implementation only
/// keeps the names; when each is read, written or forgotten, and in which order, is decided by
/// <see cref="AgentBrandingShortcuts"/>.
/// </summary>
internal interface IShortcutRecord
{
   /// <summary>
   /// Proves that this caller may change the record, creating the place it is kept when that does not exist yet, and
   /// throws when the caller may not. It changes no name. The rules call it before they write or delete anything, so a
   /// run that could not update the record afterwards fails while nothing has been changed; an uninstall calls it only
   /// when a name is recorded, so it never creates the place merely to find it empty.
   /// </summary>
   void RequireChangeable();



   /// <summary>The recorded name of the current shortcut, or null when none is recorded or the record does not exist. Reading never creates or changes anything.</summary>
   string? ReadLastShortcutName();



   /// <summary>The recorded names of the other shortcuts this installer may own, or none. Reading never creates or changes anything.</summary>
   IReadOnlyList<string> ReadPendingShortcutNames();



   /// <summary>Records the file name of the current shortcut.</summary>
   void WriteLastShortcutName(string filename);



   /// <summary>Records the file names of the other shortcuts this installer may own, replacing the names recorded before. The list is never empty; see <see cref="ClearPendingShortcutNames"/>.</summary>
   void WritePendingShortcutNames(IReadOnlyList<string> filenames);



   /// <summary>Forgets the name of the current shortcut; nothing happens when none is recorded.</summary>
   void ClearLastShortcutName();



   /// <summary>Forgets the names of the other shortcuts; nothing happens when none are recorded.</summary>
   void ClearPendingShortcutNames();
}
