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
   /// <summary>
   /// Reads the program, arguments and description of an existing shortcut file. Three outcomes are kept apart, because
   /// they mean different things to the rules. A file that cannot be opened for reading (another program holds it, or
   /// access is denied) is a failure, passed on as the file system gives it, an <see cref="IOException"/> or an
   /// <see cref="UnauthorizedAccessException"/>: nothing is known about that file. A file that opens and holds no
   /// shortcut at all (an empty file, or anything else under the name) is null. Anything else is what the shortcut
   /// says. A shell that cannot do its work fails with an <see cref="InvalidOperationException"/>.
   /// </summary>
   ShortcutLink? Read(string path);



   /// <summary>Writes a shortcut file that says exactly what the link says.</summary>
   void Write(string path, ShortcutLink link);
}
