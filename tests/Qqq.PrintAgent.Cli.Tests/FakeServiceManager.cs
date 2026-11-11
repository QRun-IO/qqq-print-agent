/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.ServiceProcess;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A service control manager with one service that exists only in memory. A test says what state the service is in,
/// whether it accepts a stop, and which states it moves through as it is looked at again; every call made is recorded
/// in order. It behaves as the real one does where the service actions depend on it: a handle remembers the state it
/// read until it is refreshed, a service that is not stopped cannot be started and one that is stopped cannot be
/// stopped, and a refusal from Windows is an invalid-operation failure that carries Windows' error. No service of
/// this computer is read or controlled.
/// </summary>
internal sealed class FakeServiceManager : IServiceManager
{
   /// <summary>The state the service is really in, whatever a handle remembers.</summary>
   internal ServiceControllerStatus State { get; set; } = ServiceControllerStatus.Stopped;



   /// <summary>Whether the service accepts a stop request while it runs.</summary>
   internal bool AcceptsStop { get; set; } = true;



   /// <summary>When set, Windows refuses every read and control of the service with this.</summary>
   internal Exception? Refusal { get; set; }



   /// <summary>The states the service moves through: each time a handle is refreshed, the service takes the next one, if any is left.</summary>
   internal Queue<ServiceControllerStatus> Next { get; } = new();



   /// <summary>Every call made, in order: "Open" with the service name, then "Status", "CanStop", "Refresh", "Start", "Stop" and "Dispose".</summary>
   internal List<string> Calls { get; } = [];



   /// <summary>How many handles are held and not yet let go.</summary>
   internal int OpenHandles { get; private set; }



   /// <summary>The refusal Windows gives for a service name no installed service has.</summary>
   internal static InvalidOperationException NotInstalled() => Refused(1060);



   /// <summary>A refusal from Windows with the given error, as the service controller reports one.</summary>
   internal static InvalidOperationException Refused(int error) => new("Synthetic: Windows refused the service request.", new Win32Exception(error));



   /// <summary>Hands out a handle to the service; nothing is read yet, as with the real one.</summary>
   public IManagedService Open(string serviceName)
   {
      Calls.Add("Open " + serviceName);
      OpenHandles++;
      return new Handle(this);
   }



   /// <summary>One handle to the service, with the state it last read.</summary>
   private sealed class Handle(FakeServiceManager manager) : IManagedService
   {
      /// <summary>The state and whether a stop is accepted, as read on first use and kept until the handle is refreshed.</summary>
      private (ServiceControllerStatus State, bool CanStop)? _read;



      /// <summary>The remembered state.</summary>
      public ServiceControllerStatus Status
      {
         get
         {
            manager.Calls.Add("Status");
            return Read().State;
         }
      }



      /// <summary>Whether a stop is accepted in the remembered state.</summary>
      public bool CanStop
      {
         get
         {
            manager.Calls.Add("CanStop");
            return Read().CanStop;
         }
      }



      /// <summary>Forgets what was read; the service has meanwhile moved to its next state, if the test gave it one.</summary>
      public void Refresh()
      {
         manager.Calls.Add("Refresh");
         _read = null;
         if(manager.Next.TryDequeue(out ServiceControllerStatus next))
         { manager.State = next; }
      }



      /// <summary>Starts a stopped service, which then is starting; Windows refuses to start one that is not stopped.</summary>
      public void Start()
      {
         manager.Calls.Add("Start");
         if(manager.Refusal is not null)
         { throw manager.Refusal; }
         if(manager.State != ServiceControllerStatus.Stopped)
         { throw Refused(1056); }
         manager.State = ServiceControllerStatus.StartPending;
      }



      /// <summary>Stops a running service, which then is stopping; Windows refuses to stop one that is stopped or that does not accept a stop.</summary>
      public void Stop()
      {
         manager.Calls.Add("Stop");
         if(manager.Refusal is not null)
         { throw manager.Refusal; }
         if(manager.State == ServiceControllerStatus.Stopped)
         { throw Refused(1062); }
         if(!manager.AcceptsStop)
         { throw Refused(1052); }
         manager.State = ServiceControllerStatus.StopPending;
      }



      /// <summary>Lets the handle go.</summary>
      public void Dispose()
      {
         manager.Calls.Add("Dispose");
         manager.OpenHandles--;
      }



      /// <summary>Reads the service once and remembers the answer, or is refused as the test asked.</summary>
      private (ServiceControllerStatus State, bool CanStop) Read()
      {
         if(manager.Refusal is not null)
         { throw manager.Refusal; }
         return _read ??= (manager.State, manager.AcceptsStop && manager.State == ServiceControllerStatus.Running);
      }
   }
}
