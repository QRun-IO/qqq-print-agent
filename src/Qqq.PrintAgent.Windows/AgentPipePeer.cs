/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;

namespace Qqq.PrintAgent.Windows;

/// <summary>Authenticates the connected kernel pipe instance before any request bytes are sent.</summary>
internal sealed class AgentPipePeer : IDisposable
{
   private readonly ServiceController _service;
   private readonly Process _process;
   private readonly WindowsIdentity _identity;

   private AgentPipePeer(ServiceController service, Process process, WindowsIdentity identity)
   {
      _service = service;
      _process = process;
      _identity = identity;
   }

   internal static AgentPipePeer Authenticate(SafePipeHandle pipe)
   {
      ServiceController service = new("QqqPrintAgent");
      Process? process = null;
      WindowsIdentity? identity = null;
      try
      {
         uint pid = ServerProcessId(pipe);
         ServiceStatus status = ReadStatus(service);
         if(status.State != 4 || status.ProcessId != pid || pid == 0)
         {
            throw Untrusted();
         }
         process = Process.GetProcessById(checked((int)pid));
         // Force acquisition of the real process handle, preventing PID reuse until disposal.
         if(!OpenProcessToken(process.SafeHandle, 8, out SafeAccessTokenHandle token))
         {
            throw new Win32Exception(Marshal.GetLastWin32Error());
         }
         using(token)
         {
            identity = new WindowsIdentity(token.DangerousGetHandle());
         }
         string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "Service", "Qqq.PrintAgent.Service.exe");
         if(!identity.IsSystem || process.HasExited || !string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)
            || ServerProcessId(pipe) != pid || ReadStatus(service).ProcessId != pid)
         {
            throw Untrusted();
         }
         return new AgentPipePeer(service, process, identity);
      }
      catch
      {
         identity?.Dispose();
         process?.Dispose();
         service.Dispose();
         throw;
      }
   }

   private static UnauthorizedAccessException Untrusted() => new("The local endpoint is not the installed Windows print service. No request was sent. Check Windows Services and repair the machine installation if needed.");

   private static uint ServerProcessId(SafePipeHandle pipe)
   {
      if(!GetNamedPipeServerProcessId(pipe, out uint pid))
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }
      return pid;
   }

   private static ServiceStatus ReadStatus(ServiceController service)
   {
      if(!QueryServiceStatusEx(service.ServiceHandle, 0, out ServiceStatus status, Marshal.SizeOf<ServiceStatus>(), out _))
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }
      return status;
   }

   public void Dispose()
   {
      _identity.Dispose();
      _process.Dispose();
      _service.Dispose();
   }

   [StructLayout(LayoutKind.Sequential)]
   private struct ServiceStatus
   {
      internal uint Type;
      internal uint State;
      internal uint Controls;
      internal uint ExitCode;
      internal uint ServiceExitCode;
      internal uint CheckPoint;
      internal uint WaitHint;
      internal uint ProcessId;
      internal uint Flags;
   }

   [DllImport("kernel32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

   [DllImport("advapi32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool QueryServiceStatusEx(SafeHandle service, int level, out ServiceStatus status, int size, out int needed);

   [DllImport("advapi32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
