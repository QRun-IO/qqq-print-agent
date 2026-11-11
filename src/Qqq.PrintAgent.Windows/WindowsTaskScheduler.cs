/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Principal;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows behind <see cref="ITaskScheduler"/>: it starts the scheduler's program, reads the identity of the
/// current process, asks the local security authority what account a name stands for and reads a path's attributes. It
/// reports what Windows said and decides nothing about tasks. The two members a test cannot usefully run are excluded
/// from coverage one by one, each with its reason; the other two are tested against a temporary folder and well-known accounts.
/// </summary>
internal sealed class WindowsTaskScheduler : ITaskScheduler
{
   /// <summary>Reads the current Windows identity, its administrator role and the path of the running program.</summary>
   [ExcludeFromCodeCoverage(Justification = "Reads the Windows identity, the administrator role and the program path of the running process. A test can call it, but the answer is whatever account and elevation the tests happen to run under, so a test could only repeat the same three reads. It decides nothing: what a missing identifier, a caller without administrator rights or a program path means is decided in AgentStartupTasks, which is tested with a caller the test supplies.")]
   public StartupCaller CurrentCaller()
   {
      using var identity = WindowsIdentity.GetCurrent();
      return new StartupCaller(identity.User?.Value, new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), Environment.ProcessPath);
   }



   /// <summary>
   /// Runs the given program with an argument list (never a shell string), without a window, and collects what it
   /// prints. A program that does not finish within the limit is killed with its process tree.
   /// </summary>
   [ExcludeFromCodeCoverage(Justification = "Starts a process, waits for it up to the limit and kills it past the limit. The only program it is ever given is schtasks.exe in the Windows system folder: AgentStartupTasks passes that path and a test pins it. A test must not run that program, because it reads and changes this computer's scheduled tasks. It decides nothing about tasks: the program, the arguments, the limit, and what an exit code, a program that did not start or one that had to be stopped means are decided in AgentStartupTasks, which is tested with a fake of ITaskScheduler.")]
   public async Task<TaskSchedulerRun?> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan limit)
   {
      ProcessStartInfo start = new(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
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
