/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.Win32.SafeHandles;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The facts about the other end of a pipe as a test describes them. Unless a test says otherwise they are those of
/// the installed print service: the pipe is served by the process the running service is hosted in, and that process
/// runs as SYSTEM from the service's program. A test changes one fact, or makes one question fail, and every question
/// asked is recorded in order. Where a fact is asked twice a test can give a different second answer, the way the
/// real one changes when the service is replaced during the check. It fails as Windows does where the decision depends
/// on it: an id no running process has cannot be opened. Nothing is read from Windows.
/// </summary>
internal sealed class FakePipePeerFacts : IPipePeerFacts
{
   /// <summary>The process id of the service in these tests.</summary>
   internal const uint ServicePid = 5120;



   /// <summary>The program the installed service runs.</summary>
   internal const string ServiceProgram = @"C:\Program Files\QQQ Print Agent\Service\Qqq.PrintAgent.Service.exe";



   /// <summary>The state Windows reports for a running service.</summary>
   internal const uint Running = 4;



   /// <summary>The answers to "which process serves the pipe", in the order asked; the last one repeats.</summary>
   internal List<uint> ServerPids { get; } = [ServicePid];



   /// <summary>When set, the pipe cannot say who serves it from the given question on (1 is the first).</summary>
   internal (int From, Exception Failure)? PipeFailure { get; set; }



   /// <summary>The answers of the service control manager, in the order asked; the last one repeats.</summary>
   internal List<PeerServiceStatus> Statuses { get; } = [new(Running, ServicePid)];



   /// <summary>When set, the service's status cannot be read: every read fails with this.</summary>
   internal Exception? ServiceFailure { get; set; }



   /// <summary>False when no running process has the id any more, which Windows reports as an argument failure.</summary>
   internal bool ProcessRuns { get; set; } = true;



   /// <summary>When set, the process cannot be opened although it may be running: every attempt fails with this.</summary>
   internal Exception? OpenFailure { get; set; }



   /// <summary>Whether the process runs as SYSTEM.</summary>
   internal bool IsSystem { get; set; } = true;



   /// <summary>When set, the process's account cannot be read: asking fails with this.</summary>
   internal Exception? AccountFailure { get; set; }



   /// <summary>Whether the process has ended although a handle to it could still be opened.</summary>
   internal bool HasExited { get; set; }



   /// <summary>The program the process runs; null plays a process without a main module.</summary>
   internal string? ProgramPath { get; set; } = ServiceProgram;



   /// <summary>When set, the process's program cannot be read: asking fails with this.</summary>
   internal Exception? ProgramFailure { get; set; }



   /// <summary>Every question asked and every handle let go, in order.</summary>
   internal List<string> Calls { get; } = [];



   /// <summary>How many service and process handles are held and not yet let go.</summary>
   internal int OpenHandles { get; private set; }



   /// <summary>How many times the pipe was asked who serves it.</summary>
   private int _pipeQuestions;



   /// <summary>How many times the service's status was read.</summary>
   private int _statusReads;



   /// <summary>Answers who serves the pipe, with the next answer the test gave, or fails as the test asked.</summary>
   public uint ServerProcessId(SafePipeHandle pipe)
   {
      Calls.Add("ServerProcessId");
      _pipeQuestions++;
      if(PipeFailure is { } failure && _pipeQuestions >= failure.From)
      { throw failure.Failure; }
      return ServerPids[Math.Min(_pipeQuestions, ServerPids.Count) - 1];
   }



   /// <summary>Hands out a handle to the named service; nothing is read yet.</summary>
   public IPeerService OpenService(string serviceName)
   {
      Calls.Add("OpenService " + serviceName);
      OpenHandles++;
      return new Service(this);
   }



   /// <summary>Hands out a handle to the process with the id, or fails the way Windows does when none runs under it or it may not be opened.</summary>
   public IPeerProcess OpenProcess(uint processId)
   {
      Calls.Add("OpenProcess " + processId);
      if(OpenFailure is not null)
      { throw OpenFailure; }
      if(!ProcessRuns)
      { throw new ArgumentException($"Synthetic: process with an Id of {processId} is not running.", nameof(processId)); }
      OpenHandles++;
      return new Process(this);
   }



   /// <summary>One handle to the service.</summary>
   private sealed class Service(FakePipePeerFacts facts) : IPeerService
   {
      /// <summary>Answers with the next status the test gave, or fails as the test asked.</summary>
      public PeerServiceStatus ReadStatus()
      {
         facts.Calls.Add("ReadStatus");
         facts._statusReads++;
         return facts.ServiceFailure is not null ? throw facts.ServiceFailure : facts.Statuses[Math.Min(facts._statusReads, facts.Statuses.Count) - 1];
      }



      /// <summary>Lets the handle go.</summary>
      public void Dispose()
      {
         facts.Calls.Add("Dispose service");
         facts.OpenHandles--;
      }
   }



   /// <summary>One handle to the process.</summary>
   private sealed class Process(FakePipePeerFacts facts) : IPeerProcess
   {
      /// <summary>Whether the process has ended.</summary>
      public bool HasExited
      {
         get
         {
            facts.Calls.Add("HasExited");
            return facts.HasExited;
         }
      }



      /// <summary>The program the process runs, or the failure the test asked for.</summary>
      public string? ProgramPath
      {
         get
         {
            facts.Calls.Add("ProgramPath");
            return facts.ProgramFailure is not null ? throw facts.ProgramFailure : facts.ProgramPath;
         }
      }



      /// <summary>Whether the process runs as SYSTEM, or the failure the test asked for.</summary>
      public bool RunsAsSystem()
      {
         facts.Calls.Add("RunsAsSystem");
         return facts.AccountFailure is not null ? throw facts.AccountFailure : facts.IsSystem;
      }



      /// <summary>Lets the handle go.</summary>
      public void Dispose()
      {
         facts.Calls.Add("Dispose process");
         facts.OpenHandles--;
      }
   }
}
