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



   /// <summary>Holds the verified service controller, process handle and identity so they live as long as the connection that relies on them.</summary>
   private AgentPipePeer(ServiceController service, Process process, WindowsIdentity identity)
   {
      _service = service;
      _process = process;
      _identity = identity;
   }



   /// <summary>
   /// Proves the process at the far end of the pipe really is the installed print service before the client sends anything.
   /// The Windows service must be running with exactly the pipe server's process id, the process must run as SYSTEM from the
   /// installed service executable, and the id is read again afterwards so a swap during the checks is detected. Holding the
   /// process handle open blocks id reuse. Any doubt throws one exception type and everything acquired is released, so every
   /// caller sees "not verified, nothing sent" the same way; <see cref="Refusal"/> chooses what the operator is told.
   /// </summary>
   internal static AgentPipePeer Authenticate(SafePipeHandle pipe)
   {
      ServiceController service = new("QqqPrintAgent");
      Process? process = null;
      WindowsIdentity? identity = null;
      uint? server = null;
      try
      {
         uint pid = ServerProcessId(pipe);
         server = pid;
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
      catch(Exception exception)
      {
         identity?.Dispose();
         process?.Dispose();
         service.Dispose();
         throw Refusal(server, exception);
      }
   }



   /// <summary>
   /// Chooses what a failed check tells the operator. When the process that was serving the pipe has ended (or the pipe could
   /// no longer say who serves it), the service stopped or restarted between the connection and the check: nothing is wrong
   /// with the installation, so the answer is to try again. When that process is still running and did not pass, it is not the
   /// installed service, and the answer is the one refusal that reveals nothing about which check failed. A check that threw
   /// keeps what it threw as the inner exception, for the log.
   /// </summary>
   internal static UnauthorizedAccessException Refusal(uint? server, Exception found)
   {
      if(server is not { } pid || Ended(pid))
      {
         return Stopped(found);
      }
      return found as UnauthorizedAccessException ?? Untrusted(found);
   }



   /// <summary>Whether no running process has this id any more. A process that cannot be looked at is treated as still running, so doubt never softens the refusal.</summary>
   private static bool Ended(uint pid)
   {
      try
      {
         using var process = Process.GetProcessById(checked((int)pid));
         return process.HasExited;
      }
      catch(Exception exception) when(exception is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
      {
         ////////////////////////////////////////////////////////////////////////////////
         // the lookup throws an argument exception when no process has the id, which  //
         // is the one certain sign that it ended; every other failure is only doubt   //
         ////////////////////////////////////////////////////////////////////////////////
         return exception is ArgumentException;
      }
   }



   /// <summary>The refusal for an endpoint that was checked and is not the installed service; it says no request was sent and how to repair, and reveals nothing about which check failed.</summary>
   private static UnauthorizedAccessException Untrusted(Exception? cause = null) => new("The local endpoint is not the installed Windows print service. No request was sent. Check Windows Services and repair the machine installation if needed.", cause);



   /// <summary>
   /// The refusal for a service that went away during the check. It is the same exception type as the other refusal, because
   /// either way nothing was sent, but it tells the operator to wait and try again instead of repairing an installation that
   /// is fine.
   /// </summary>
   private static UnauthorizedAccessException Stopped(Exception cause) => new("The Windows print service stopped or restarted before it could be verified. No request was sent. Wait a moment and try again; check Windows Services if this continues.", cause);



   /// <summary>Asks the kernel which process serves this pipe instance; the answer cannot be forged by a process that merely uses the pipe's name.</summary>
   private static uint ServerProcessId(SafePipeHandle pipe)
   {
      if(!GetNamedPipeServerProcessId(pipe, out uint pid))
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }
      return pid;
   }



   /// <summary>Reads the service's current state and process id from the service control manager.</summary>
   private static ServiceStatus ReadStatus(ServiceController service)
   {
      if(!QueryServiceStatusEx(service.ServiceHandle, 0, out ServiceStatus status, Marshal.SizeOf<ServiceStatus>(), out _))
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }
      return status;
   }



   /// <summary>Releases the identity, the process handle (which allows the id to be reused) and the service controller.</summary>
   public void Dispose()
   {
      _identity.Dispose();
      _process.Dispose();
      _service.Dispose();
   }



   /// <summary>SERVICE_STATUS_PROCESS as returned by QueryServiceStatusEx; the layout is marshaled and must not change.</summary>
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



   /// <summary>Returns the id of the process that created the server end of a named pipe.</summary>
   [DllImport("kernel32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);



   /// <summary>Queries the extended status of a service, including the id of the process hosting it.</summary>
   [DllImport("advapi32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool QueryServiceStatusEx(SafeHandle service, int level, out ServiceStatus status, int size, out int needed);



   /// <summary>Opens the access token of a process so its account can be verified as SYSTEM.</summary>
   [DllImport("advapi32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
