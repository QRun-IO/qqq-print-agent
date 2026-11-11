/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Security.AccessControl;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The update steps under test in temporary folders: a Program Files folder with the installed companion, service and
/// command line tool, a machine state folder with an update session that holds a staged MSI, and fakes for folder
/// security, processes, the pipe, the log and the clock. The companion that asks for the update is a running process of
/// the fake. Everything lives under one temporary root that is removed afterwards; no installer is started, no service
/// or pipe is reached, and nothing under this computer's Program Files or machine state folder is touched.
/// </summary>
internal sealed class MsiUpdateSetup : IDisposable
{
   /// <summary>The release the staged MSI is said to hold; an eligible one.</summary>
   internal const string Version = "0.3.2-rc.1";



   /// <summary>The process id of the companion that asks for the update.</summary>
   internal const int CompanionId = 3100;



   /// <summary>When that companion started, in universal time ticks.</summary>
   internal const long CompanionStarted = 638_900_000_000_000_000;



   /// <summary>The instant the manual clock starts at.</summary>
   internal static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);



   /// <summary>How long a test waits for something the code under test must do; long, because the test machine may be busy, and finite, so that code that never does it fails the test instead of hanging it.</summary>
   internal static readonly TimeSpan Patience = TimeSpan.FromMinutes(1);



   /// <summary>Creates the folders, the installed programs, the session with its staged MSI, and the steps over the fakes.</summary>
   internal MsiUpdateSetup()
   {
      ProgramFiles = Path.Combine(Root, "Program Files");
      State = Path.Combine(Root, "ProgramData", "QqqPrintAgent");
      Session = Path.Combine(State, "updates", Guid.NewGuid().ToString("N"));
      Package = Path.Combine(Session, "QqqPrintAgent-v" + Version + "-x64.msi");
      Places = new MsiUpdatePlaces(ProgramFiles, State, Guard, OpenLog, SendAsync, Clock);
      foreach(string file in new[] { Places.InstalledCompanion, Places.InstalledService, InstalledTool, Path.Combine(Places.InstalledCli, "qqq-print-agent.dll"), Path.Combine(Places.InstalledCli, "runtimes", "win-x64", "native.dll") })
      {
         Directory.CreateDirectory(Path.GetDirectoryName(file)!);
         File.WriteAllText(file, "synthetic " + Path.GetFileName(file));
         Processes.Versions[file] = Version + "+reviewed-source";
      }
      Directory.CreateDirectory(Session);
      File.WriteAllBytes(Package, PackageBytes);
      Companion = Processes.Run(CompanionId, Places.InstalledCompanion, CompanionStarted);
      Processes.CurrentPath = Places.InstalledCompanion;
      Processes.CurrentId = CompanionId;
      Processes.OnStart = start =>
      {
         string program = Path.GetFileName(start.FileName);
         Journal.Add("start " + program);
         WhileStarting?.Invoke(start);
         return StartsNothing.Contains(program) ? null : FakeProcessHost.Ended(start.FileName, ExitCodes.GetValueOrDefault(program, 0));
      };
      Steps = new MsiUpdateSteps(Security, Processes, Places);
   }



   /// <summary>The temporary root everything in this setup lives under.</summary>
   internal string Root { get; } = Path.Combine(Path.GetTempPath(), "qqq-update-steps-" + Guid.NewGuid().ToString("N"));



   /// <summary>The stand-in for this computer's Program Files folder.</summary>
   internal string ProgramFiles { get; }



   /// <summary>The stand-in for the machine state folder.</summary>
   internal string State { get; }



   /// <summary>The folder of one update session inside the updates folder.</summary>
   internal string Session { get; }



   /// <summary>The staged MSI of that session.</summary>
   internal string Package { get; }



   /// <summary>What the staged MSI holds.</summary>
   internal byte[] PackageBytes { get; } = [0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3, 4, 5, 6];



   /// <summary>The folder security fake; every folder and file is trusted unless a test says otherwise.</summary>
   internal FakePathSecurity Security { get; } = new();



   /// <summary>The process fake.</summary>
   internal FakeProcessHost Processes { get; } = new();



   /// <summary>The log the steps are handed when they open the log of the state folder.</summary>
   internal FakeActivityLog Log { get; } = new();



   /// <summary>The clock, which moves only when a test advances it.</summary>
   internal ManualTimeProvider Clock { get; } = new(Now);



   /// <summary>The places the steps work in.</summary>
   internal MsiUpdatePlaces Places { get; }



   /// <summary>The steps under test.</summary>
   internal MsiUpdateSteps Steps { get; }



   /// <summary>The companion that asks for the update, as a running process.</summary>
   internal FakeProcess Companion { get; }



   /// <summary>Every path the machine state check was asked about, in order.</summary>
   internal List<string> Guarded { get; } = [];



   /// <summary>When set, the machine state check refuses with this.</summary>
   internal Exception? GuardFailure { get; set; }



   /// <summary>Every request sent to the service, in order, by operation.</summary>
   internal List<string> Sent { get; } = [];



   /// <summary>When set, the service cannot be reached: every request fails with this.</summary>
   internal Exception? PipeFailure { get; set; }



   /// <summary>What the service answers to each operation; "Printing resumed." unless a test says otherwise.</summary>
   internal Dictionary<string, string> Replies { get; } = new(StringComparer.Ordinal);



   /// <summary>Called while a request is being sent, with its operation, so a test can act at the moment the service is asked.</summary>
   internal Action<string>? WhileSending { get; set; }



   /// <summary>Every state folder whose log was opened, in order.</summary>
   internal List<string> LogsOpened { get; } = [];



   /// <summary>What reached the outside, in order: each request sent as "send" and its operation, each program started as "start" and its file name.</summary>
   internal List<string> Journal { get; } = [];



   /// <summary>The code each started program ends with, by file name; 0 for a program that is not listed.</summary>
   internal Dictionary<string, int> ExitCodes { get; } = new(StringComparer.OrdinalIgnoreCase);



   /// <summary>Programs, by file name, for which Windows starts nothing.</summary>
   internal HashSet<string> StartsNothing { get; } = new(StringComparer.OrdinalIgnoreCase);



   /// <summary>Called while a program is being started, so a test can look at the files as they are while the program runs.</summary>
   internal Action<ProcessStartInfo>? WhileStarting { get; set; }



   /// <summary>The installed command line tool.</summary>
   internal string InstalledTool => Path.Combine(Places.InstalledCli, "qqq-print-agent.exe");



   /// <summary>The folder the installed command line tool is copied to.</summary>
   internal string HelperDirectory => Path.Combine(Session, "helper");



   /// <summary>The copied command line tool that applies the update.</summary>
   internal string Helper => Path.Combine(HelperDirectory, "qqq-print-agent.exe");



   /// <summary>The request the preparing step writes.</summary>
   internal string RequestPath => Path.Combine(Session, "request.json");



   /// <summary>The recorded outcome of the last update.</summary>
   internal string OutcomePath => Path.Combine(Places.UpdatesDirectory, "last-result.json");



   /// <summary>Every program the steps asked to start, by file name, in order.</summary>
   internal IEnumerable<string> StartedPrograms => Processes.Started.Select(start => Path.GetFileName(start.FileName));



   /// <summary>A descriptor that lets ordinary users write, which no update path may have.</summary>
   internal static FileSystemSecurity WritableByUsers() => FakePathSecurity.Descriptor(alsoAllow: FakePathSecurity.Users, rights: FileSystemRights.Write);



   /// <summary>Prepares the update as the installed companion does and returns the request path.</summary>
   internal string Prepare() => Steps.PrepareHelper(Package, Version, CompanionId);



   /// <summary>Applies the update and returns the helper's exit code. The wait has a limit, so that an apply step that wrongly waits for something that never comes fails the test instead of hanging it.</summary>
   internal Task<int> ApplyAsync(string request) => Steps.ApplyAsync(request).WaitAsync(Patience);



   /// <summary>
   /// Prepares the update and then becomes the staged helper: the rules now run as the copied command line tool, and the
   /// companion has closed unless the test keeps it running.
   /// </summary>
   internal string PrepareAndRunAsHelper(bool companionClosed = true)
   {
      string request = Prepare();
      Processes.CurrentPath = Helper;
      if(companionClosed)
      { _ = Processes.Running.Remove(CompanionId); }
      return request;
   }



   /// <summary>
   /// Makes a junction inside the temporary root with the command interpreter's own mklink, which needs no administrator
   /// rights. The target must be a folder inside the root too.
   /// </summary>
   internal static void MakeJunction(string junction, string target)
   {
      using Process mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "mklink", "/J", junction, target]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
      string said = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
      mklink.WaitForExit();
      Assert.True(mklink.ExitCode == 0, "The junction this test needs could not be made in its temporary folder: " + said);
   }



   /// <summary>Waits until the condition holds, for at most a minute, looking again every few milliseconds; a test uses it to see that the code under test has reached a wait.</summary>
   internal static async Task UntilAsync(Func<bool> condition)
   {
      long started = Stopwatch.GetTimestamp();
      while(!condition())
      {
         Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(1), "The condition this test waits for did not come about within a minute.");
         await Task.Delay(5);
      }
   }



   /// <summary>Removes the temporary root. Junctions in it are removed first, as junctions and without following them, because a recursive delete reports a junction as an error although it removes it.</summary>
   public void Dispose()
   {
      if(Directory.Exists(Root))
      {
         RemoveJunctions(Root);
         Directory.Delete(Root, recursive: true);
      }
   }



   /// <summary>Removes every junction below a folder, leaving what each points at alone.</summary>
   private static void RemoveJunctions(string directory)
   {
      foreach(string entry in Directory.EnumerateDirectories(directory))
      {
         if(File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
         { Directory.Delete(entry); }
         else
         { RemoveJunctions(entry); }
      }
   }



   /// <summary>The machine state check of this setup: it notes the path and refuses when the test said so.</summary>
   private void Guard(string path)
   {
      Guarded.Add(path);
      if(GuardFailure is not null)
      { throw GuardFailure; }
   }



   /// <summary>Opens the log of a state folder: notes which folder and hands out the fake log.</summary>
   private FakeActivityLog OpenLog(string directory)
   {
      LogsOpened.Add(directory);
      return Log;
   }



   /// <summary>The pipe of this setup: it notes the operation and answers, or fails when the test said so.</summary>
   internal Task<string> SendAsync(PipeRequest request)
   {
      Sent.Add(request.Operation);
      Journal.Add("send " + request.Operation);
      WhileSending?.Invoke(request.Operation);
      return PipeFailure is not null ? Task.FromException<string>(PipeFailure) : Task.FromResult(Replies.GetValueOrDefault(request.Operation, "Printing resumed."));
   }
}
