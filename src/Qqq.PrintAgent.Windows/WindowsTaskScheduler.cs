/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows behind <see cref="ITaskScheduler"/>: it starts the program it is given, reads the identity of the
/// current process, asks the local security authority what account a name stands for and reads a path's attributes. It
/// reports what Windows said and decides nothing about tasks. Nothing here is excluded from coverage: the program is a
/// parameter, so the tests run a harmless child program in place of the scheduler's, and the other members only read.
/// </summary>
internal sealed class WindowsTaskScheduler : ITaskScheduler
{
   /// <summary>Reads the current Windows identity, its administrator role and the path of the running program.</summary>
   public StartupCaller CurrentCaller()
   {
      using var identity = WindowsIdentity.GetCurrent();
      return new StartupCaller(identity.User?.Value, new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), Environment.ProcessPath);
   }



   /// <summary>
   /// Runs the given program with an argument list (never a shell string), without a window, and collects what it
   /// prints. Both of its streams are read while it runs, so a program that fills its error stream is not held up. A
   /// program that cannot be started is reported as null, and one that does not finish within the limit is killed
   /// with its process tree and reported as timed out.
   /// </summary>
   public async Task<TaskSchedulerRun?> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan limit)
   {
      ProcessStartInfo start = new(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
      foreach(string argument in arguments)
      {
         start.ArgumentList.Add(argument);
      }
      using Process? process = Start(start);
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



   /// <summary>Starts the process; a program that does not exist or that Windows will not run is reported as not started instead of failing.</summary>
   private static Process? Start(ProcessStartInfo start)
   {
      try
      {
         return Process.Start(start);
      }
      catch(Win32Exception)
      {
         return null;
      }
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
