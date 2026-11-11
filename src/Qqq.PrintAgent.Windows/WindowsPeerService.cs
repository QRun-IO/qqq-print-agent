/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One real Windows service behind <see cref="IPeerService"/>. Its status is read through the service controller's own
/// handle, which Windows opens with full access to the service and therefore gives only to an administrator: a caller
/// who may not control the service is refused here, before anything is read. Nothing here is excluded from coverage:
/// the tests show the refusal with a name no service has, and read a built-in service's status with
/// <see cref="Query"/> through a handle of their own that may only read.
/// </summary>
internal sealed class WindowsPeerService(ServiceController controller) : IPeerService
{
   /// <summary>Opens the service with the controller's full-access handle and reads its state and process id.</summary>
   public PeerServiceStatus ReadStatus() => Query(controller.ServiceHandle);



   /// <summary>Lets go of the service without changing it.</summary>
   public void Dispose() => controller.Dispose();



   /// <summary>Reads the service's current state and process id from the service control manager through an open service handle.</summary>
   internal static PeerServiceStatus Query(SafeHandle service)
   {
      if(!QueryServiceStatusEx(service, 0, out ServiceStatus status, Marshal.SizeOf<ServiceStatus>(), out _))
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }
      return new PeerServiceStatus(status.State, status.ProcessId);
   }



   /// <summary>SERVICE_STATUS_PROCESS as returned by QueryServiceStatusEx; the layout is marshaled and must not change.</summary>
   [StructLayout(LayoutKind.Sequential)]
   private struct ServiceStatus
   {
      /// <summary>The kind of service.</summary>
      internal uint Type;



      /// <summary>The current state; 4 is running.</summary>
      internal uint State;



      /// <summary>The control codes the service accepts.</summary>
      internal uint Controls;



      /// <summary>The Windows error the service reported when it started or stopped.</summary>
      internal uint ExitCode;



      /// <summary>The service's own error code.</summary>
      internal uint ServiceExitCode;



      /// <summary>The progress counter of a pending start, stop, pause or continue.</summary>
      internal uint CheckPoint;



      /// <summary>How long the pending operation is expected to take, in milliseconds.</summary>
      internal uint WaitHint;



      /// <summary>The id of the process hosting the service; 0 when none does.</summary>
      internal uint ProcessId;



      /// <summary>Whether the service runs in a system process.</summary>
      internal uint Flags;
   }



   /// <summary>Queries the extended status of a service, including the id of the process hosting it.</summary>
   [DllImport("advapi32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool QueryServiceStatusEx(SafeHandle service, int level, out ServiceStatus status, int size, out int needed);
}
