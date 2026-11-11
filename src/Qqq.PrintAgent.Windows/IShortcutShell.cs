/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Windows shortcut files, read and written through the shell. An implementation only turns a file into what it says and
/// back; whether a shortcut is ours, what a new one must say and where it may be written is decided by
/// <see cref="AgentBrandingShortcuts"/>.
/// </summary>
internal interface IShortcutShell
{
   /// <summary>Reads the program, arguments and description of an existing shortcut file.</summary>
   ShortcutLink Read(string path);



   /// <summary>Writes a shortcut file that says exactly what the link says.</summary>
   void Write(string path, ShortcutLink link);
}
