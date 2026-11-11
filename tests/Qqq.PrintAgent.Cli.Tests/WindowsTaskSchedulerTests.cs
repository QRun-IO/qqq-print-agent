/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The real task scheduler adapter, run without the scheduler. The program it starts is a parameter, so these tests give
/// it the repository's own child-process fixture, which prints, exits or waits as told and changes nothing; the other
/// members only read: a path's attributes in a temporary folder, the identifier of accounts every Windows has, and the
/// identity of the test process. Nothing here starts schtasks.exe or touches this computer's scheduled tasks.
/// </summary>
public sealed class WindowsTaskSchedulerTests
{
   /// <summary>How long a child that is expected to finish may take; generous, because the test machine may be busy.</summary>
   private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);



   /// <summary>The fixture program built beside the tests.</summary>
   private static string StandIn => Path.ChangeExtension(typeof(WorkerFixture.Program).Assembly.Location, ".exe");



   /// <summary>A program that does not exist is reported as not started, which the rules turn into their own message, instead of failing with whatever Windows says.</summary>
   [Fact]
   public async Task ProgramThatCannotBeStartedIsReportedAsNotStarted()
   {
      string missing = Path.Combine(Path.GetTempPath(), "qqq-no-such-program-" + Guid.NewGuid().ToString("N") + ".exe");
      Assert.Null(await new WindowsTaskScheduler().RunAsync(missing, ["/Query"], Generous));
   }



   /// <summary>A program that finishes is reported with its own exit code and exactly what it printed, and each argument reaches it as one argument, spaces and quotes included.</summary>
   [Theory]
   [InlineData(0, "printed by the stand-in")]
   [InlineData(7, "two words, a \"quoted\" one and a trailing backslash \\")]
   public async Task FinishedProgramIsReportedWithItsExitCodeAndOutput(int code, string text)
   {
      TaskSchedulerRun run = Assert.IsType<TaskSchedulerRun>(await new WindowsTaskScheduler().RunAsync(StandIn, ["--stand-in", "exit", code.ToString(CultureInfo.InvariantCulture), text, "0"], Generous));
      Assert.Equal(new TaskSchedulerRun(code, text), run);
   }



   /// <summary>
   /// A program that fills its error stream is not held up by it: the error stream is read while the program runs, so
   /// the program gets to print its output and exit. A megabyte is far more than a pipe holds; if nothing read it, the
   /// program would block before printing and this run would end at the limit instead.
   /// </summary>
   [Fact]
   public async Task FullErrorStreamDoesNotHoldTheProgramUp()
   {
      TaskSchedulerRun run = Assert.IsType<TaskSchedulerRun>(await new WindowsTaskScheduler().RunAsync(StandIn, ["--stand-in", "exit", "3", "reached the end", "1048576"], Generous));
      Assert.Equal(new TaskSchedulerRun(3, "reached the end"), run);
   }



   /// <summary>
   /// A program that does not finish within the limit is killed and reported as timed out, with the cancellation that
   /// ended the wait and no exit code or output of its own. The child leaves its process id in a file before it starts
   /// waiting, and that process is gone afterwards.
   /// </summary>
   [Fact]
   public async Task ProgramPastItsLimitIsKilledAndReportedAsTimedOut()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-scheduler-run-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
         string started = Path.Combine(root, "started.txt");
         TaskSchedulerRun run = Assert.IsType<TaskSchedulerRun>(await new WindowsTaskScheduler().RunAsync(StandIn, ["--stand-in", "wait", started], TimeSpan.FromSeconds(6)));
         _ = Assert.IsType<OperationCanceledException>(run.TimedOut, exactMatch: false);
         Assert.Equal((0, ""), (run.ExitCode, run.Output));
         Assert.True(File.Exists(started), "The stand-in did not start within the six second limit, so the kill could not be checked; the machine was too busy for this test.");
         Assert.True(await HasExitedAsync(int.Parse(await File.ReadAllTextAsync(started), CultureInfo.InvariantCulture)), "The program that ran past its limit is still running.");
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>
   /// The caller is the test process itself: its program path, a well-formed security identifier that is the one of the
   /// process token, and an administrator flag that agrees with the token read another way, by whether the
   /// administrators group is among its enabled groups.
   /// </summary>
   [Fact]
   public void CallerIsTheRunningProcessAndItsToken()
   {
      StartupCaller caller = new WindowsTaskScheduler().CurrentCaller();
      using var identity = WindowsIdentity.GetCurrent();
      Assert.Equal(Environment.ProcessPath, caller.ProcessPath);
      Assert.Matches("^S-1-\\d+(-\\d+)+$", caller.UserSid);
      Assert.Equal(identity.User!.Value, caller.UserSid);
      Assert.Equal(identity.Groups!.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)), caller.IsAdministrator);
   }



   /// <summary>A folder reports as a folder, a file as a file, neither as a link, and a path where nothing exists reports nothing.</summary>
   [Fact]
   public void AttributesAreReadForWhatExistsAndNothingIsReportedForWhatDoesNot()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-scheduler-paths-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
         string file = Path.Combine(root, "Qqq.PrintAgent.exe");
         File.WriteAllText(file, "synthetic companion");
         var scheduler = new WindowsTaskScheduler();
         FileAttributes folderAttributes = Assert.NotNull(scheduler.AttributesOf(root));
         FileAttributes fileAttributes = Assert.NotNull(scheduler.AttributesOf(file));
         Assert.True(folderAttributes.HasFlag(FileAttributes.Directory));
         Assert.False(fileAttributes.HasFlag(FileAttributes.Directory));
         Assert.False((folderAttributes | fileAttributes).HasFlag(FileAttributes.ReparsePoint));
         Assert.Null(scheduler.AttributesOf(Path.Combine(root, "missing.exe")));
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>
   /// The name Windows itself gives a well-known account resolves back to that account's identifier, whatever language
   /// Windows spells the name in, and a name no account on this computer has resolves to nothing instead of failing.
   /// </summary>
   [Theory]
   [InlineData(WellKnownSidType.LocalSystemSid, "S-1-5-18")]
   [InlineData(WellKnownSidType.BuiltinAdministratorsSid, "S-1-5-32-544")]
   public void AccountNameResolvesToItsIdentifierAndAnUnknownNameToNothing(WellKnownSidType account, string identifier)
   {
      var scheduler = new WindowsTaskScheduler();
      string name = new SecurityIdentifier(account, null).Translate(typeof(NTAccount)).Value;
      Assert.Equal(identifier, scheduler.ResolveAccountSid(name));
      Assert.Null(scheduler.ResolveAccountSid(Environment.MachineName + "\\qqq-none-" + Guid.NewGuid().ToString("N")[..8]));
   }



   /// <summary>Whether the process with the given id is gone, waiting a while for a process that was just killed to finish ending.</summary>
   private static async Task<bool> HasExitedAsync(int processId)
   {
      try
      {
         using var process = Process.GetProcessById(processId);
         using var patience = new CancellationTokenSource(Generous);
         await process.WaitForExitAsync(patience.Token);
         return true;
      }
      catch(ArgumentException)
      {
         return true;
      }
      catch(OperationCanceledException)
      {
         return false;
      }
   }
}
