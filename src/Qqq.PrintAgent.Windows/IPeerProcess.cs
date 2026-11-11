/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One process held for the pipe client's trust decision. Once <see cref="RunsAsSystem"/> has been asked, the process
/// itself is held open until this is disposed, so its id cannot be given to another process while the connection that
/// relies on the decision lives.
/// </summary>
internal interface IPeerProcess : IDisposable
{
   /// <summary>
   /// Opens the process and its access token and says whether it runs as the SYSTEM account. It fails with what Windows
   /// reported when the process or its token may not be opened.
   /// </summary>
   bool RunsAsSystem();



   /// <summary>True once the process has ended.</summary>
   bool HasExited { get; }



   /// <summary>The full path of the program the process runs, or null when it has no main module. Reading it fails with what Windows reported when the process may not be inspected or has ended.</summary>
   string? ProgramPath { get; }
}
