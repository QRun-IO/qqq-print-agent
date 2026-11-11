/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The real process adapter, run without Windows Installer, the companion or the Services console. The program it
/// starts is a parameter, so these tests give it the repository's own child-process fixture, which exits or waits as
/// told and changes nothing; the other members only read: the identity of the test process and the version resource
/// of a file this build produced.
/// </summary>
public sealed class WindowsProcessHostTests
{
   /// <summary>How long a child that is expected to finish may take; generous, because the test machine may be busy.</summary>
   private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);



   /// <summary>The fixture program built beside the tests.</summary>
   private static string StandIn => Path.ChangeExtension(typeof(WorkerFixture.Program).Assembly.Location, ".exe");



   /// <summary>The current process is the test process: its program path and its id.</summary>
   [Fact]
   public void CurrentProcessIsTheOneRunningTheTest()
   {
      var host = new WindowsProcessHost();
      Assert.Equal(Environment.ProcessPath, host.CurrentPath);
      Assert.Equal(Environment.ProcessId, host.CurrentId);
   }



   /// <summary>A program that is started is held, can be waited for, and is reported as ended with its own exit code.</summary>
   [Theory]
   [InlineData(0)]
   [InlineData(1602)]
   public async Task StartedProgramIsWaitedForAndReportedWithItsExitCode(int code)
   {
      using IRunningProcess process = Assert.IsAssignableFrom<IRunningProcess>(new WindowsProcessHost().Start(Start("exit", code.ToString(CultureInfo.InvariantCulture), "printed by the stand-in", "0")));
      using CancellationTokenSource patience = new(Generous);
      await process.WaitForExitAsync(patience.Token);
      Assert.True(process.HasExited);
      Assert.Equal(code, process.ExitCode);
   }



   /// <summary>
   /// A running program is found by its id with the path of its program and the same start time the handle from the
   /// start reports. A wait whose token is cancelled ends with the cancellation and leaves the program running, and a
   /// running program has no exit code. Once the program has ended, its id no longer names a running process. The
   /// stand-in leaves its id in a file whose path has a space in it, which also shows that each argument arrives whole.
   /// </summary>
   [Fact]
   public async Task RunningProgramIsFoundByItsIdAndAnEndedOneIsNot()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq process host " + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
         string announced = Path.Combine(root, "started here.txt");
         var host = new WindowsProcessHost();
         long before = DateTime.UtcNow.Ticks;
         using IRunningProcess started = Assert.IsAssignableFrom<IRunningProcess>(host.Start(Start("wait", announced)));
         int id = await AnnouncedIdAsync(announced);
         try
         {
            using IRunningProcess found = host.Open(id);
            Assert.False(found.HasExited);
            Assert.Equal(StandIn, found.ProgramPath, ignoreCase: true);
            Assert.Equal(started.StartTicks, found.StartTicks);
            Assert.InRange(found.StartTicks, before - TimeSpan.TicksPerSecond, DateTime.UtcNow.Ticks);
            using CancellationTokenSource brief = new(TimeSpan.FromMilliseconds(200));
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => found.WaitForExitAsync(brief.Token));
            Assert.False(found.HasExited);
            _ = Assert.Throws<InvalidOperationException>(() => found.ExitCode);
         }
         finally
         {
            using var process = Process.GetProcessById(id);
            process.Kill();
         }
         using CancellationTokenSource patience = new(Generous);
         await started.WaitForExitAsync(patience.Token);
         Assert.True(started.HasExited);
         _ = Assert.Throws<ArgumentException>(() => host.Open(id));
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>
   /// A program started through the shell, the way the Services console is, is started too, with its arguments: the
   /// stand-in, started hidden, leaves its id in the file it was told to. Nothing is added to the start information,
   /// so it is the caller's choice of shell or no shell that Windows gets.
   /// </summary>
   [Fact]
   public async Task ProgramStartedThroughTheShellIsStartedWithItsArguments()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq process host " + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
         string announced = Path.Combine(root, "started here.txt");
         ProcessStartInfo start = new(StandIn) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
         foreach(string argument in new[] { "--stand-in", "wait", announced })
         {
            start.ArgumentList.Add(argument);
         }
         using IRunningProcess? started = new WindowsProcessHost().Start(start);
         using var process = Process.GetProcessById(await AnnouncedIdAsync(announced));
         process.Kill();
         using CancellationTokenSource patience = new(Generous);
         await process.WaitForExitAsync(patience.Token);
         Assert.True(start.UseShellExecute);
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>A program file that does not exist is not started, and what Windows said about it is passed on for the rules to report.</summary>
   [Fact]
   public void ProgramThatDoesNotExistFailsWithWhatWindowsSaid()
   {
      string missing = Path.Combine(Path.GetTempPath(), "qqq-no-such-program-" + Guid.NewGuid().ToString("N") + ".exe");
      Win32Exception failure = Assert.Throws<Win32Exception>(() => new WindowsProcessHost().Start(new ProcessStartInfo(missing) { UseShellExecute = false, CreateNoWindow = true }));
      Assert.Equal(2, failure.NativeErrorCode);
   }



   /// <summary>
   /// The product version is read from the file's version resource: for the library these tests were built with it is
   /// the version the build stamped into it, a file without a version resource has none, and a file that does not
   /// exist fails with what Windows said.
   /// </summary>
   [Fact]
   public void ProductVersionIsReadFromTheFilesVersionResource()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-process-host-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
         var host = new WindowsProcessHost();
         Assembly library = typeof(MsiUpdateSteps).Assembly;
         Assert.Equal(library.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, host.ProductVersion(library.Location));
         string plain = Path.Combine(root, "plain.exe");
         File.WriteAllText(plain, "synthetic: not a program");
         Assert.Null(host.ProductVersion(plain));
         _ = Assert.Throws<FileNotFoundException>(() => host.ProductVersion(Path.Combine(root, "missing.exe")));
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>The start of the stand-in with the given arguments: no shell and no window, as the update starts its programs.</summary>
   private static ProcessStartInfo Start(params string[] arguments)
   {
      ProcessStartInfo start = new(StandIn) { UseShellExecute = false, CreateNoWindow = true };
      start.ArgumentList.Add("--stand-in");
      foreach(string argument in arguments)
      {
         start.ArgumentList.Add(argument);
      }
      return start;
   }



   /// <summary>The process id the waiting stand-in left in its file, once it has written it.</summary>
   private static async Task<int> AnnouncedIdAsync(string file)
   {
      long started = Stopwatch.GetTimestamp();
      while(true)
      {
         try
         {
            if(File.Exists(file) && int.TryParse(await File.ReadAllTextAsync(file), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            { return id; }
         }
         catch(IOException)
         {
            // The stand-in is still writing the file.
         }
         Assert.True(Stopwatch.GetElapsedTime(started) < Generous, "The stand-in did not start within a minute; the machine was too busy for this test.");
         await Task.Delay(20);
      }
   }
}
