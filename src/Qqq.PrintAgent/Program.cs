/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.UI.Dispatching;


namespace Qqq.PrintAgent;

/// <summary>
/// Entry point for the MSI-installed AppKit companion.
/// </summary>
public static class Program
{
   /// <summary>A payload-free activation signal, scoped to this user's session and settings profile.</summary>
   internal static EventWaitHandle? ActivationSignal { get; private set; }



   /// <summary>Initialize WinUI; machine installation and updates are handled by MSI.</summary>
   [STAThread]
   private static void Main()
   {
      string key = InstanceKey();
      using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, key + ".activate");
      using var instance = new Mutex(initiallyOwned: true, key, out bool firstInstance);
      if(!firstInstance)
      {
         signal.Set();
         return;
      }
      ActivationSignal = signal;
      try
      {
         // Native signal forwarding avoids blocking an STA on SDK async activation redirection.
         WinRT.ComWrappersSupport.InitializeComWrappers();
         Microsoft.UI.Xaml.Application.Start(callbackParams =>
         {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
         });
      }
      finally
      {
         ActivationSignal = null;
         instance.ReleaseMutex();
      }
   }



   /// <summary>Builds the per-user, per-session, per-profile name for the single-instance mutex and activation signal. It is hashed so the name is bounded and carries no account or path text, and scoping keeps one user's instance from blocking another's.</summary>
   private static string InstanceKey()
   {
      using var identity = WindowsIdentity.GetCurrent();
      using var process = Process.GetCurrentProcess();
      string profile = Path.GetFullPath(AppIdentity.Paths.DataRoot).ToUpperInvariant();
      string scope = $"{identity.User?.Value}|{process.SessionId}|{profile}";
      return "Local\\QqqPrintAgent." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
   }
}
