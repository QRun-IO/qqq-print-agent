/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Dispatching;


namespace Qqq.PrintAgent;

/// <summary>
/// Entry point for the MSI-installed AppKit companion.
/// </summary>
public static class Program
{
   /// <summary>Initialize WinUI; machine installation and updates are handled by MSI.</summary>
   [STAThread]
   private static void Main()
   {
      // The remainder mirrors the XAML-generated Main.
      WinRT.ComWrappersSupport.InitializeComWrappers();
      Microsoft.UI.Xaml.Application.Start(callbackParams =>
      {
         SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
         _ = new App();
      });
   }
}
