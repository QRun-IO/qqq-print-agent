/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Command-line entry point behavior: which data folder foreground runs may use, single-worker locking, and exit codes for help and failures.</summary>
public sealed class ProgramTests
{
   /// <summary>A foreground run defaults to the current user's own data folder and may not be pointed at the service's protected machine folder, where it could overwrite the service's credentials; only the explicit service mode resolves to that folder.</summary>
   [Fact]
   public void ForegroundData_UsesCurrentUserAndCannotOverwriteServiceCredentials()
   {
      Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QqqPrintAgent"), Program.ResolveDataDirectory([]));
      Assert.Throws<InvalidOperationException>(() => Program.ResolveDataDirectory(["--data-dir", Qqq.PrintAgent.Windows.AgentStore.DefaultDirectory]));
      Assert.Equal(Qqq.PrintAgent.Windows.AgentStore.DefaultDirectory, Program.ResolveDataDirectory(["--service"]));
   }



   /// <summary>The worker lock is exclusive per data folder: while one worker holds it, a second attempt fails, so a foreground command cannot modify state under an active worker.</summary>
   [Fact]
   public void ForegroundMutation_CannotAcquireActiveWorkerDirectory()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-cli-lock-" + Guid.NewGuid().ToString("N"));
      try
      {
         using FileStream worker = Program.AcquireWorkerLock(directory);
         Assert.Throws<IOException>(() => Program.AcquireWorkerLock(directory));
      }
      finally { Directory.Delete(directory, recursive: true); }
   }



   /// <summary>Asking for help prints usage and exits with code 0.</summary>
   [Fact]
   public async Task Help_ExitsSuccessfully()
   {
      Assert.Equal(0, await Program.Main(["help"]));
   }



   /// <summary>Enrolling without the required server address fails with exit code 1 instead of prompting or guessing.</summary>
   [Fact]
   public async Task Enroll_MissingServerFails()
   {
      Assert.Equal(1, await Program.Main(["enroll"]));
   }



   /// <summary>A request that times out ends the command with exit code 1 rather than an unhandled exception.</summary>
   [Fact]
   public async Task RequestTimeout_ExitsWithFailure()
   {
      Assert.Equal(1, await Program.ExecuteAsync(() => Task.FromException<int>(new TaskCanceledException("Request timeout."))));
   }
}
