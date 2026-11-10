/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Principal;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows behind <see cref="ITaskScheduler"/>: it starts schtasks.exe, reads the identity of the current process,
/// asks the local security authority what account a name stands for and reads a path's attributes. It reports what Windows
/// said and decides nothing about tasks.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Starts schtasks.exe, reads the identity and elevation of the current process and asks the local security authority to resolve account names. A test cannot run these without depending on, or changing, this computer's scheduled tasks and accounts. No rule lives here: which arguments are passed, how long to wait, what counts as this application's task and when one is created, replaced or removed are decided in AgentStartupTasks, which is tested with a fake of ITaskScheduler.")]
internal sealed class WindowsTaskScheduler : ITaskScheduler
{
   /// <summary>Reads the current Windows identity, its administrator role and the path of the running program.</summary>
   public StartupCaller CurrentCaller()
   {
      using var identity = WindowsIdentity.GetCurrent();
      return new StartupCaller(identity.User?.Value, new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), Environment.ProcessPath);
   }



   /// <summary>
   /// Runs schtasks.exe from the system folder with an argument list (never a shell string), without a window, and collects
   /// what it prints. A scheduler that does not finish within the limit is killed with its process tree.
   /// </summary>
   public async Task<TaskSchedulerRun?> RunAsync(IReadOnlyList<string> arguments, TimeSpan limit)
   {
      ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
      foreach(string argument in arguments)
      {
         start.ArgumentList.Add(argument);
      }
      using var process = Process.Start(start);
      if(process is null)
      {
         return null;
      }
      Task<string> output = process.StandardOutput.ReadToEndAsync();
      Task<string> error = process.StandardError.ReadToEndAsync();
      using CancellationTokenSource timeout = new(limit);
      try
      {
         await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
      }
      catch(OperationCanceledException exception)
      {
         process.Kill(entireProcessTree: true);
         return new TaskSchedulerRun(0, "", exception);
      }
      string result = await output.ConfigureAwait(false);
      _ = await error.ConfigureAwait(false);
      return new TaskSchedulerRun(process.ExitCode, result);
   }



   /// <summary>Translates an account name to its security identifier; a name Windows cannot map is reported as null.</summary>
   public string? ResolveAccountSid(string account)
   {
      try
      {
         return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
      }
      catch(Exception exception) when(exception is IdentityNotMappedException or ArgumentException or System.Security.SecurityException)
      {
         return null;
      }
   }



   /// <summary>Reads the attributes of whatever exists at the path, or null when nothing does.</summary>
   public FileAttributes? AttributesOf(string path) => File.Exists(path) || Directory.Exists(path) ? File.GetAttributes(path) : null;
}
