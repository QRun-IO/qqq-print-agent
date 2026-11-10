/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Command-line entry point behavior: exit codes for help and failures.</summary>
public sealed class ProgramTests
{
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
