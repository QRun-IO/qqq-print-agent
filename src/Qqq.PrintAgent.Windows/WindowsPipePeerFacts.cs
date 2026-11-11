/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows behind <see cref="IPipePeerFacts"/>: the kernel's record of which process serves a pipe, a service
/// by name and a process by id. It decides nothing about trust. Nothing here is excluded from coverage: the tests ask
/// it about a pipe they serve themselves, a service name no service has and the test process.
/// </summary>
internal sealed class WindowsPipePeerFacts : IPipePeerFacts
{
   /// <summary>Asks the kernel which process serves this pipe instance; the answer cannot be forged by a process that merely uses the pipe's name.</summary>
   public uint ServerProcessId(SafePipeHandle pipe)
   {
      if(!GetNamedPipeServerProcessId(pipe, out uint pid))
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }
      return pid;
   }



   /// <summary>Holds the named service on this computer; Windows is asked only when its status is read.</summary>
   public IPeerService OpenService(string serviceName) => new WindowsPeerService(new ServiceController(serviceName));



   /// <summary>Finds the running process with the id; Windows' own refusal for an id no running process has is passed on.</summary>
   public IPeerProcess OpenProcess(uint processId) => new WindowsPeerProcess(Process.GetProcessById(checked((int)processId)));



   /// <summary>Returns the id of the process that created the server end of a named pipe.</summary>
   [DllImport("kernel32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
