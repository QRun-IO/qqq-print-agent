/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows behind <see cref="IProcessHost"/>. It reads the identity of the current process, finds a process
/// by id, starts the program it is given and reads a file's version resource; it decides nothing. Nothing here is
/// excluded from coverage: the program to start is a parameter, so the tests start a harmless child program in place
/// of Windows Installer and the companion, and the other members only read.
/// </summary>
internal sealed class WindowsProcessHost : IProcessHost
{
   /// <summary>The path of the program this process runs.</summary>
   public string? CurrentPath => Environment.ProcessPath;



   /// <summary>The id of this process.</summary>
   public int CurrentId => Environment.ProcessId;



   /// <summary>Finds the process with the id; Windows' own refusal for an id no process has is passed on.</summary>
   public IRunningProcess Open(int processId) => new WindowsRunningProcess(Process.GetProcessById(processId));



   /// <summary>Starts the program as described; nothing is added to or taken from the start information.</summary>
   public IRunningProcess? Start(ProcessStartInfo start) => Process.Start(start) is Process process ? new WindowsRunningProcess(process) : null;



   /// <summary>Reads the product version from the file's version resource.</summary>
   public string? ProductVersion(string path) => FileVersionInfo.GetVersionInfo(path).ProductVersion;
}
