/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The Windows facts and actions the sign-in startup rules are applied to: the task scheduler's command-line program, who
/// is asking, what account a name stands for and what a path is. An implementation only asks Windows and reports what it
/// said; whether a task is ours, whether one may be created, replaced or removed, and what the task says is decided by
/// <see cref="AgentStartupTasks"/>.
/// </summary>
internal interface ITaskScheduler
{
   /// <summary>Who the current process runs as, whether with administrator rights, and from which program.</summary>
   StartupCaller CurrentCaller();



   /// <summary>
   /// Runs the task scheduler's command-line program with the given arguments and waits for it for at most the given time.
   /// It returns null when the program could not be started, and a run marked as timed out when it had to be stopped.
   /// </summary>
   Task<TaskSchedulerRun?> RunAsync(IReadOnlyList<string> arguments, TimeSpan limit);



   /// <summary>The security identifier an account name stands for on this computer, or null when the name stands for no account.</summary>
   string? ResolveAccountSid(string account);



   /// <summary>The attributes of a file or folder (a link shows as a reparse point), or null when nothing exists at the path.</summary>
   FileAttributes? AttributesOf(string path);
}
