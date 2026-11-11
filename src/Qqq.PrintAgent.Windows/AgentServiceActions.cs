/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The local administrator actions on one Windows service and what they tell the operator: how a state reads, what can
/// be offered in it, when the service is started or stopped and how long it is waited for. The service control manager
/// comes through <see cref="IServiceManager"/>, the Services console is started through <see cref="IProcessHost"/> and
/// the wait runs on the given clock, so every action can be run against fakes without touching a service.
/// </summary>
internal sealed class AgentServiceActions(IServiceManager services, IProcessHost processes, TimeProvider clock, string serviceName)
{
   /// <summary>How long a start or a stop is waited for before the action gives up, so a stuck service stops the action instead of hanging the window.</summary>
   internal static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);



   /// <summary>How long the wait pauses between two looks at the service.</summary>
   internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);



   /// <summary>The Windows error for a service name that no installed service has.</summary>
   private const int ServiceDoesNotExist = 1060;



   /// <summary>The name of the service these actions are for.</summary>
   internal string ServiceName => serviceName;



   /// <summary>Read service state even when the print management pipe is unavailable.</summary>
   internal AgentServiceStatus GetStatus()
   {
      try
      {
         using IManagedService service = services.Open(serviceName);
         ServiceControllerStatus state = service.Status;
         return new AgentServiceStatus(true, state.ToString(), state == ServiceControllerStatus.Stopped,
            state == ServiceControllerStatus.Running && service.CanStop,
            state == ServiceControllerStatus.Stopped ? "Start the service to connect and print." : "The Windows service runs independently of this companion.");
      }
      catch(InvalidOperationException exception) when(exception.InnerException is Win32Exception { NativeErrorCode: ServiceDoesNotExist })
      {
         return new AgentServiceStatus(false, "Not installed", false, false, "Install the QQQ Print Agent MSI from the project releases, then reopen this app. Windows Services can confirm the installation.");
      }
      catch(Exception exception) when(exception is InvalidOperationException or Win32Exception)
      {
         return new AgentServiceStatus(false, "Unavailable", false, false, "Windows could not read the service state. Use an administrator account and check Windows Services.");
      }
   }



   /// <summary>Start an installed stopped service and wait at most 30 seconds.</summary>
   internal async Task<AgentServiceStatus> StartAsync()
   {
      using IManagedService service = services.Open(serviceName);
      if(service.Status == ServiceControllerStatus.Stopped)
      {
         service.Start();
      }
      await WaitAsync(service, ServiceControllerStatus.Running).ConfigureAwait(false);
      return GetStatus();
   }



   /// <summary>Restart only after an explicit administrator action; callers must warn about active submissions.</summary>
   internal async Task<AgentServiceStatus> RestartAsync()
   {
      using IManagedService service = services.Open(serviceName);
      if(service.Status != ServiceControllerStatus.Stopped)
      {
         service.Stop();
         await WaitAsync(service, ServiceControllerStatus.Stopped).ConfigureAwait(false);
      }
      service.Start();
      await WaitAsync(service, ServiceControllerStatus.Running).ConfigureAwait(false);
      return GetStatus();
   }



   /// <summary>Open the native Services console on explicit user request.</summary>
   internal void OpenServices()
   {
      ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "mmc.exe")) { UseShellExecute = true };
      start.ArgumentList.Add(Path.Combine(Environment.SystemDirectory, "services.msc"));
      using IRunningProcess? process = processes.Start(start);
   }



   /// <summary>Polls the service every 250 ms until it reaches the wanted state, giving up with a clear message after 30 seconds, so a stuck service stops the action instead of hanging the window.</summary>
   private async Task WaitAsync(IManagedService service, ServiceControllerStatus desired)
   {
      long started = clock.GetTimestamp();
      while(true)
      {
         service.Refresh();
         if(service.Status == desired)
         {
            return;
         }
         if(clock.GetElapsedTime(started) >= WaitLimit)
         {
            throw new System.TimeoutException("The service did not reach " + desired + " within 30 seconds. Check Windows Services and the activity log.");
         }
         await Task.Delay(PollInterval, clock).ConfigureAwait(false);
      }
   }
}
