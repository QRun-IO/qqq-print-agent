/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class ProgramTests
{
   [Fact]
   public void ForegroundData_UsesCurrentUserAndCannotOverwriteServiceCredentials()
   {
      Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QqqPrintAgent"), Program.ResolveDataDirectory([]));
      Assert.Throws<InvalidOperationException>(() => Program.ResolveDataDirectory(["--data-dir", Qqq.PrintAgent.Windows.AgentStore.DefaultDirectory]));
      Assert.Equal(Qqq.PrintAgent.Windows.AgentStore.DefaultDirectory, Program.ResolveDataDirectory(["--service"]));
   }

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

   [Fact]
   public async Task Help_ExitsSuccessfully()
   {
      Assert.Equal(0, await Program.Main(["help"]));
   }

   [Fact]
   public async Task Enroll_MissingServerFails()
   {
      Assert.Equal(1, await Program.Main(["enroll"]));
   }

   [Fact]
   public async Task RequestTimeout_ExitsWithFailure()
   {
      Assert.Equal(1, await Program.ExecuteAsync(() => Task.FromException<int>(new TaskCanceledException("Request timeout."))));
   }
}
