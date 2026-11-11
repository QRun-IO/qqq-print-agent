/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// One process that exists only in memory: the program it runs, when it started, whether it has ended and with what
/// code. Each time it is opened it hands out a separate handle, as Windows does, and counts the handles that have not
/// been let go. It behaves as a real process does where the update rules depend on it: its exit code cannot be read
/// before it has ended, a wait for it ends with a cancellation when the token is cancelled first, and its program path
/// cannot be read once it has ended. What .NET 10 says about that read depends on whether anything else still holds
/// the ended process. While another handle keeps it, the read fails with Windows error 299, because its memory can no
/// longer be read; that is what this fake does unless a test says otherwise. When nothing else holds it, which is the
/// update helper's own situation, the read fails with an invalid-operation failure saying that the process has
/// exited; a test plays that by setting <see cref="InspectionFailure"/> together with <see cref="ExitsWhenInspected"/>.
/// </summary>
internal sealed class FakeProcess(int id, string? program, long startTicks)
{
   /// <summary>Completed when the process ends, which is what a wait for it waits on.</summary>
   private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);



   /// <summary>The exit code, once the process has ended.</summary>
   private int? _exitCode;



   /// <summary>The process id.</summary>
   internal int Id => id;



   /// <summary>The program the process runs; null plays a process that has no main module.</summary>
   internal string? Program { get; set; } = program;



   /// <summary>When the process started, in universal time ticks.</summary>
   internal long StartTicks { get; set; } = startTicks;



   /// <summary>True once the process has ended.</summary>
   internal bool HasExited => _exitCode is not null;



   /// <summary>How many handles to this process are open: handed out and not yet let go.</summary>
   internal int OpenHandles { get; private set; }



   /// <summary>How many times a wait for the process to end was begun.</summary>
   internal int Waits { get; private set; }



   /// <summary>When set, reading the program path fails with this, the way Windows refuses to let a process be inspected.</summary>
   internal Exception? InspectionFailure { get; set; }



   /// <summary>When true, the process ends at the moment its program path is read, so the read fails: with <see cref="InspectionFailure"/> when a test set one, and otherwise the way it does for a process that has ended.</summary>
   internal bool ExitsWhenInspected { get; set; }



   /// <summary>Ends the process with the given code and releases every wait for it.</summary>
   internal void Exit(int code)
   {
      _exitCode = code;
      _ended.TrySetResult();
   }



   /// <summary>Opens one more handle to the process.</summary>
   internal IRunningProcess Open()
   {
      OpenHandles++;
      return new Handle(this);
   }



   /// <summary>One handle to the process. Like a real one it is of no use once it has been let go.</summary>
   private sealed class Handle(FakeProcess process) : IRunningProcess
   {
      /// <summary>True once this handle has been let go.</summary>
      private bool _disposed;



      /// <summary>Whether the process has ended.</summary>
      public bool HasExited => Live().HasExited;



      /// <summary>The program path, read the way Windows reads it: not when inspection is refused, and not from a process that has ended. For an ended process this plays the case in which something else still holds it, so that its memory can no longer be read (error 299); the case in which nothing holds it is a failure the test sets.</summary>
      public string? ProgramPath
      {
         get
         {
            FakeProcess live = Live();
            if(live.ExitsWhenInspected)
            { live.Exit(0); }
            if(live.InspectionFailure is not null)
            { throw live.InspectionFailure; }
            if(live.HasExited)
            { throw new Win32Exception(299, "Synthetic: only part of a ReadProcessMemory or WriteProcessMemory request was completed."); }
            return live.Program;
         }
      }



      /// <summary>The start time in universal time ticks.</summary>
      public long StartTicks => Live().StartTicks;



      /// <summary>The exit code, which a process that is still running does not have.</summary>
      public int ExitCode => Live()._exitCode ?? throw new InvalidOperationException("Synthetic: process must exit before requested information can be determined.");



      /// <summary>Waits for the process to end; a token cancelled first ends the wait with the cancellation and leaves the process running.</summary>
      public Task WaitForExitAsync(CancellationToken cancellationToken)
      {
         FakeProcess live = Live();
         live.Waits++;
         return live._ended.Task.WaitAsync(cancellationToken);
      }



      /// <summary>Lets the handle go; letting it go twice is harmless, as with a real one.</summary>
      public void Dispose()
      {
         if(!_disposed)
         {
            _disposed = true;
            process.OpenHandles--;
         }
      }



      /// <summary>The process, as long as this handle is still held.</summary>
      private FakeProcess Live() => _disposed ? throw new InvalidOperationException("Synthetic: no process is associated with this object.") : process;
   }
}
