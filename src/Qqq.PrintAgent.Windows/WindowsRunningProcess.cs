/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One real process behind <see cref="IRunningProcess"/>. Every member passes on what Windows says about the process
/// it was given, failures included, and none ends it. Nothing here is excluded from coverage: the tests hold a
/// harmless child program through it.
/// </summary>
internal sealed class WindowsRunningProcess(Process process) : IRunningProcess
{
   /// <summary>Whether the process has ended.</summary>
   public bool HasExited => process.HasExited;



   /// <summary>The file of the process's main module.</summary>
   public string? ProgramPath => process.MainModule?.FileName;



   /// <summary>The start time in universal time ticks.</summary>
   public long StartTicks => process.StartTime.ToUniversalTime().Ticks;



   /// <summary>The exit code of the ended process.</summary>
   public int ExitCode => process.ExitCode;



   /// <summary>Waits for the process to end, or for the token.</summary>
   public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);



   /// <summary>Lets go of the process without ending it.</summary>
   public void Dispose() => process.Dispose();
}
