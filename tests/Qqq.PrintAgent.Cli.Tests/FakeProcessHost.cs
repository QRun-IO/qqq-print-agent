/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The processes of a computer that exists only in memory. A test says which processes are running, which program the
/// rules themselves run as and what version each program file carries; every program the rules start is recorded and
/// nothing is started. It fails the way Windows does where the rules depend on it: an id no process has cannot be
/// opened, a program file that does not exist cannot be started, and a file that does not exist has no version to read.
/// </summary>
internal sealed class FakeProcessHost : IProcessHost
{
   /// <summary>The program the rules run as.</summary>
   public string? CurrentPath { get; set; }



   /// <summary>The id of the process the rules run in.</summary>
   public int CurrentId { get; set; } = 4242;



   /// <summary>The processes that can be opened, by id.</summary>
   internal Dictionary<int, FakeProcess> Running { get; } = [];



   /// <summary>Every id the rules asked to open, in order.</summary>
   internal List<int> Opened { get; } = [];



   /// <summary>Called while a process is being opened, with its id, so a test can act at the moment the rules look at a process.</summary>
   internal Action<int>? WhileOpening { get; set; }



   /// <summary>Every start the rules asked for, in order, exactly as they described it.</summary>
   internal List<ProcessStartInfo> Started { get; } = [];



   /// <summary>Every process a start produced, in order; null where the start produced none.</summary>
   internal List<FakeProcess?> Produced { get; } = [];



   /// <summary>
   /// What a start produces: by default a process that has already ended with code 0. A test replaces it to choose the
   /// exit code, to keep the process running, to look at the files while the program "runs", or to produce nothing.
   /// </summary>
   internal Func<ProcessStartInfo, FakeProcess?> OnStart { get; set; } = start => Ended(start.FileName, 0);



   /// <summary>The product version of each program file; a file that is listed with null carries none.</summary>
   internal Dictionary<string, string?> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);



   /// <summary>Every file whose version was read, in order.</summary>
   internal List<string> VersionsRead { get; } = [];



   /// <summary>A process that has already ended with the given code.</summary>
   internal static FakeProcess Ended(string program, int code)
   {
      FakeProcess process = new(id: 7000, program, startTicks: 1);
      process.Exit(code);
      return process;
   }



   /// <summary>Registers a running process and returns it.</summary>
   internal FakeProcess Run(int id, string? program, long startTicks)
   {
      FakeProcess process = new(id, program, startTicks);
      Running[id] = process;
      return process;
   }



   /// <summary>How many handles the rules still hold, to processes they opened or started.</summary>
   internal int OpenHandles => Running.Values.Sum(process => process.OpenHandles) + Produced.Sum(process => process?.OpenHandles ?? 0);



   /// <summary>Opens the process with the id; an id no process has is refused the way Windows refuses it.</summary>
   public IRunningProcess Open(int processId)
   {
      Opened.Add(processId);
      WhileOpening?.Invoke(processId);
      return Running.TryGetValue(processId, out FakeProcess? process) ? process.Open() : throw new ArgumentException($"Synthetic: process with an Id of {processId} is not running.", nameof(processId));
   }



   /// <summary>Records the start and produces what the test chose; a program file that does not exist cannot be started, as on a real disk.</summary>
   public IRunningProcess? Start(ProcessStartInfo start)
   {
      Started.Add(start);
      if(!File.Exists(start.FileName))
      { throw new Win32Exception(2, "Synthetic: the system cannot find the file specified."); }
      FakeProcess? process = OnStart(start);
      Produced.Add(process);
      return process?.Open();
   }



   /// <summary>The version the test gave the file; a file the test did not list does not exist.</summary>
   public string? ProductVersion(string path)
   {
      VersionsRead.Add(path);
      return Versions.TryGetValue(path, out string? version) ? version : throw new FileNotFoundException("Synthetic: no such program file to read a version from.", path);
   }
}
