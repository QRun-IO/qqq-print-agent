/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;


namespace Qqq.PrintAgent.Windows;

/// <summary>Explicit local administrator actions for the fixed installed print service.</summary>
public static class AgentServiceControl
{
   private const string ServiceName = "QqqPrintAgent";



   /// <summary>Read service state even when the print management pipe is unavailable.</summary>
   public static AgentServiceStatus GetStatus()
   {
      try
      {
         using ServiceController service = new(ServiceName);
         ServiceControllerStatus state = service.Status;
         return new AgentServiceStatus(true, state.ToString(), state == ServiceControllerStatus.Stopped,
            state == ServiceControllerStatus.Running && service.CanStop,
            state == ServiceControllerStatus.Stopped ? "Start the service to connect and print." : "The Windows service runs independently of this companion.");
      }
      catch(InvalidOperationException exception) when(exception.InnerException is Win32Exception { NativeErrorCode: 1060 })
      {
         return new AgentServiceStatus(false, "Not installed", false, false, "Install the QQQ Print Agent MSI from the project releases, then reopen this app. Windows Services can confirm the installation.");
      }
      catch(Exception exception) when(exception is InvalidOperationException or Win32Exception)
      {
         return new AgentServiceStatus(false, "Unavailable", false, false, "Windows could not read the service state. Use an administrator account and check Windows Services.");
      }
   }



   /// <summary>Start an installed stopped service and wait at most 30 seconds.</summary>
   public static async Task<AgentServiceStatus> StartAsync()
   {
      using ServiceController service = new(ServiceName);
      if(service.Status == ServiceControllerStatus.Stopped)
      {
         service.Start();
      }
      await WaitAsync(service, ServiceControllerStatus.Running).ConfigureAwait(false);
      return GetStatus();
   }



   /// <summary>Restart only after an explicit administrator action; callers must warn about active submissions.</summary>
   public static async Task<AgentServiceStatus> RestartAsync()
   {
      using ServiceController service = new(ServiceName);
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
   public static void OpenServices()
   {
      ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "mmc.exe")) { UseShellExecute = true };
      start.ArgumentList.Add(Path.Combine(Environment.SystemDirectory, "services.msc"));
      using var process = Process.Start(start);
   }



   /// <summary>Polls the service every 250 ms until it reaches the wanted state, giving up with a clear message after 30 seconds, so a stuck service stops the action instead of hanging the window.</summary>
   private static async Task WaitAsync(ServiceController service, ServiceControllerStatus desired)
   {
      long started = Stopwatch.GetTimestamp();
      while(true)
      {
         service.Refresh();
         if(service.Status == desired)
         {
            return;
         }
         if(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
         {
            throw new System.TimeoutException("The service did not reach " + desired + " within 30 seconds. Check Windows Services and the activity log.");
         }
         await Task.Delay(250).ConfigureAwait(false);
      }
   }
}
