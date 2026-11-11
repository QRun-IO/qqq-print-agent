/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Native printer calls run in a separate worker process so a hang or crash cannot take down the service. These tests start a
/// real fixture worker (no printer involved) to prove the boundary: refusals cross it intact, a hung worker is killed and
/// replaced, the configured watchdog applies, and a worker never outlives its parent.
/// </summary>
public sealed class PrinterWorkerIsolationTests
{
   private static string FixtureExecutable => Path.ChangeExtension(typeof(WorkerFixture.Program).Assembly.Location, ".exe");



   /// <summary>A media-mismatch refusal made inside the worker arrives as the same specific exception on the service side, so it is treated as a known refusal before printing and not as an uncertain outcome.</summary>
   [Fact]
   public async Task VerifiedMediaRefusalSurvivesTheWorkerBoundaryWithoutAnUncertainOutcome()
   {
      var backend = new IsolatedPrinterBackend(FixtureExecutable)
      {
         StartProcess = start =>
         {
            start.ArgumentList.Add("--media-drift");
            return Process.Start(start) ?? throw new InvalidOperationException("Fixture failed.");
         },
      };
      PrintJob job = new("fixture", "fixture", "Not printed", "PDF", new string('0', 64), 1, new PrintOptions(), "", "RECEIVED");
      await Assert.ThrowsAsync<PrinterMediaMismatchException>(() => backend.PrintFileAsync(job, Path.Combine(Path.GetTempPath(), "unused-media-fixture.pdf"), TimeSpan.FromMinutes(2), CancellationToken.None));
   }



   /// <summary>A file submission obeys the timeout the server configured (one second here) rather than a fixed two minutes: a hung worker is abandoned with a timeout error and its process is gone afterwards.</summary>
   [Fact]
   public async Task FileSubmissionUsesConfiguredWatchdogInsteadOfFixedTwoMinuteTimeout()
   {
      int childId = 0;
      var backend = new IsolatedPrinterBackend(FixtureExecutable)
      {
         StartProcess = start =>
         {
            start.ArgumentList.Add("--hang");
            Process child = Process.Start(start) ?? throw new InvalidOperationException("Fixture failed.");
            childId = child.Id;
            return child;
         },
      };
      PrintJob job = new("synthetic", "fixture", "Never printed", "PDF", new string('0', 64), 1, new PrintOptions(), "", "RECEIVED");
      using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      await Assert.ThrowsAsync<TimeoutException>(() => backend.PrintFileAsync(job, Path.Combine(Path.GetTempPath(), "unused-synthetic.document"), TimeSpan.FromSeconds(1), watchdog.Token));
      Assert.True(ProcessExited(childId));
   }



   /// <summary>When the first worker hangs, the call times out and that worker process is killed; the next operation starts a fresh worker and succeeds, so one hang does not wedge printer discovery.</summary>
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
      Assert.Equal(WorkerFixture.Program.QueueId, Assert.Single(backend.Discover()).PrinterId);
      Assert.Equal(2, launches);
   }



   /// <summary>
   /// The three reads cross the boundary to the real helper loop and come back as what they are. Here the fixture stands for
   /// a station where a queue other than its own has a driver that never answers: the list without media and the lookup of
   /// the fixture's queue are answered (the list without the media profile, the lookup with it, an unknown identifier as
   /// absent), while the full discovery, which asks every driver, runs into its limit.
   /// </summary>
   [Fact]
   public void ListAndLookupAreAnsweredWhileTheFullDiscoveryHangs()
   {
      static Process Hanging(ProcessStartInfo start)
      {
         start.ArgumentList.Add("--hang");
         return Process.Start(start) ?? throw new InvalidOperationException("Fixture launch failed.");
      }
      IsolatedPrinterBackend backend = new(FixtureExecutable) { StartProcess = Hanging };

      PrinterInfo listed = Assert.Single(backend.ListWithoutMedia());
      PrinterInfo found = Assert.IsType<PrinterInfo>(backend.FindWithMedia(WorkerFixture.Program.QueueId));

      Assert.Equal((WorkerFixture.Program.QueueId, "Synthetic queue"), (listed.PrinterId, listed.Name));
      Assert.Null(listed.MediaProfiles);
      Assert.Equal((WorkerFixture.Program.QueueId, "Synthetic queue"), (found.PrinterId, found.Name));
      Assert.Equal("fixture-default", Assert.Single(found.MediaProfiles!).Id);
      Assert.Null(backend.FindWithMedia("000000000000000000000000"));
      Assert.Throws<TimeoutException>(() => new IsolatedPrinterBackend(FixtureExecutable) { OperationTimeoutOverride = TimeSpan.FromSeconds(2), StartProcess = Hanging }.Discover());
   }



   /// <summary>A worker that is busy in a native call still exits once its parent process is gone, so a crashed service cannot leave an orphan worker holding the print spooler.</summary>
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



   /// <summary>
   /// A worker whose parent is already gone when it starts exits with code 2 at once, before it reads a request: nothing is
   /// sent to it here and its input stays open, so only the parent watch can have ended it, and it writes no answer. The
   /// parent is a process that has exited and whose handle this test still holds, so Windows cannot have given its
   /// process id to another process.
   /// </summary>
   [Fact]
   public async Task WorkerExitsAtOnceWhenItsParentIsAlreadyGone()
   {
      ProcessStartInfo gone = new(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
      gone.ArgumentList.Add("/c");
      gone.ArgumentList.Add("exit 0");
      using Process parent = Process.Start(gone) ?? throw new InvalidOperationException("The process that stands for the parent did not start.");
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
      await parent.WaitForExitAsync(timeout.Token);

      ProcessStartInfo start = new(FixtureExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
      start.Environment[PrinterWorkerHost.ParentEnvironmentVariable] = parent.Id.ToString(CultureInfo.InvariantCulture);
      using Process worker = Process.Start(start) ?? throw new InvalidOperationException("Fixture launch failed.");
      try
      {
         await worker.WaitForExitAsync(timeout.Token);
         Assert.Equal(2, worker.ExitCode);
         Assert.Equal("", await worker.StandardOutput.ReadToEndAsync(timeout.Token));
      }
      finally
      {
         if(!worker.HasExited)
         {
            worker.Kill(entireProcessTree: true);
         }
      }
   }



   /// <summary>True when the process has exited or no longer exists; a missing process id counts as exited.</summary>
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
