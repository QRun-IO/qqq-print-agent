/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.Cli.Tests;

public sealed class PrinterWorkerIsolationTests
{
   private static string FixtureExecutable => Path.ChangeExtension(typeof(WorkerFixture.Program).Assembly.Location, ".exe");

   [Fact]
   public void TimedOutWorkerIsKilledAndNextOperationSucceeds()
   {
      int launches = 0;
      int hungProcessId = 0;
      IsolatedPrinterBackend backend = new(FixtureExecutable)
      {
         OperationTimeoutOverride = TimeSpan.FromSeconds(2),
         StartProcess = start =>
         {
            if(launches++ == 0)
            {
               start.ArgumentList.Add("--hang");
            }
            Process child = Process.Start(start) ?? throw new InvalidOperationException("Fixture launch failed.");
            if(launches == 1)
            {
               hungProcessId = child.Id;
            }
            return child;
         },
      };
      Assert.Throws<TimeoutException>(() => backend.Discover());
      Assert.True(ProcessExited(hungProcessId));
      Assert.Equal("fixture", Assert.Single(backend.Discover()).PrinterId);
      Assert.Equal(2, launches);
   }

   [Fact]
   public async Task WorkerExitsWhenItsParentDiesDuringANativeOperation()
   {
      ProcessStartInfo start = new(FixtureExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
      start.ArgumentList.Add("--launch-parent");
      using Process parent = Process.Start(start) ?? throw new InvalidOperationException("Parent fixture launch failed.");
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
      try
      {
         string? line = await parent.StandardOutput.ReadLineAsync(timeout.Token);
         int workerId = int.Parse(line ?? throw new InvalidOperationException("Parent did not report its worker."), CultureInfo.InvariantCulture);
         await parent.WaitForExitAsync(timeout.Token);
         Assert.Equal(0, parent.ExitCode);
         try
         {
            using var worker = Process.GetProcessById(workerId);
            await worker.WaitForExitAsync(timeout.Token);
            Assert.True(worker.HasExited);
         }
         catch(ArgumentException)
         {
            // The worker already exited before its process handle was opened.
         }
      }
      finally
      {
         if(!parent.HasExited)
         {
            parent.Kill(entireProcessTree: true);
         }
      }
   }

   private static bool ProcessExited(int processId)
   {
      try
      {
         using var process = Process.GetProcessById(processId);
         return process.HasExited;
      }
      catch(ArgumentException)
      {
         return true;
      }
   }
}
