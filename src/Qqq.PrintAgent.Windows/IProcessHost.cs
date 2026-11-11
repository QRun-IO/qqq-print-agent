/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The processes of this computer as the update rules need them: which program is running the rules, a process found
/// by its id, a program started, and the version a program file carries. An implementation only asks Windows and
/// reports what it said; which program may be started, with what arguments, whose identity is accepted and what an
/// exit code means is decided by the caller (<see cref="MsiUpdateSteps"/>).
/// </summary>
internal interface IProcessHost
{
   /// <summary>The full path of the program the current process runs, or null when Windows cannot say.</summary>
   string? CurrentPath { get; }



   /// <summary>The id of the current process.</summary>
   int CurrentId { get; }



   /// <summary>
   /// Takes hold of the running process with the given id. It throws <see cref="ArgumentException"/> when no process
   /// has that id, as Windows reports it; what that means is the caller's decision.
   /// </summary>
   IRunningProcess Open(int processId);



   /// <summary>
   /// Starts a program exactly as the start information says and returns the new process, or null when Windows started
   /// nothing new. A program that does not exist or that Windows refuses to run fails with what Windows reported.
   /// </summary>
   IRunningProcess? Start(ProcessStartInfo start);



   /// <summary>The product version recorded in a program file, or null when the file carries none; a file that cannot be read fails with what Windows reported.</summary>
   string? ProductVersion(string path);
}
