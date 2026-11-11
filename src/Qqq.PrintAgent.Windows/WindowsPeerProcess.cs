/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One real process behind <see cref="IPeerProcess"/>. Asking for its account opens the process itself, and that
/// handle stays open until this is disposed, which prevents Windows from giving the process id to another process
/// meanwhile. Every member passes on what Windows says, failures included.
/// </summary>
internal sealed class WindowsPeerProcess(Process process) : IPeerProcess
{
   /// <summary>The access right that allows a process token to be read.</summary>
   private const uint TokenQuery = 8;



   /// <summary>The account the process runs as, once it has been asked for; kept until this is disposed.</summary>
   private WindowsIdentity? _identity;



   /// <summary>Opens the process and its token, and says whether the token is the SYSTEM account's.</summary>
   public bool RunsAsSystem()
   {
      // Force acquisition of the real process handle, preventing PID reuse until disposal.
      if(!OpenProcessToken(process.SafeHandle, TokenQuery, out SafeAccessTokenHandle token))
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }
      using(token)
      {
         _identity?.Dispose();
         _identity = new WindowsIdentity(token.DangerousGetHandle());
      }
      return _identity.IsSystem;
   }



   /// <summary>Whether the process has ended.</summary>
   public bool HasExited => process.HasExited;



   /// <summary>The file of the process's main module.</summary>
   public string? ProgramPath => process.MainModule?.FileName;



   /// <summary>Releases the account and the process handle, after which the process id may be reused.</summary>
   public void Dispose()
   {
      _identity?.Dispose();
      process.Dispose();
   }



   /// <summary>Opens the access token of a process so its account can be verified as SYSTEM.</summary>
   [DllImport("advapi32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
